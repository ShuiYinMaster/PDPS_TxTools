using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Utilities;
using TxTools.Agent.Core;
using TxTools.CatiaPartTree.Core;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;
using TxTools.CrossEnvIO;

namespace TxTools.CatiaPartTree.Ps
{
    internal enum CatiaImportStage { Ready, Converted, TreeCreated, Completed, Failed }

    internal sealed class CatiaImportSession
    {
        private sealed class Package
        {
            internal string Source, Hash, Directory, Jt, JtHash, TuneHash, Error, Warning;
        }
        private readonly CatiaImportSource _source;
        private readonly object _document;
        private readonly long _generation;
        private readonly string _library, _parentLocation;
        private readonly ITxObject _parent;
        private readonly bool _newRoot;
        private readonly Func<string, string, CancellationToken, CatiaTessellation> _tessellate;
        private readonly List<Package> _packages = new List<Package>();
        private readonly Dictionary<string, Package> _bySource = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
        internal readonly List<StructureChange> Items = new List<StructureChange>();
        // A part that cannot be converted is reported as a warning and dropped from
        // the batch instead of stopping the remaining parts.
        internal readonly List<string> Warnings = new List<string>();
        internal CatiaImportStage Stage { get; private set; }
        internal string OutputDirectory { get; private set; }
        internal ITxObject Target { get { return Items[0].Result ?? Items[0].Existing; } }
        internal bool SceneWriteAttempted { get; private set; }
        internal int Imported { get; private set; }
        internal int Created { get; private set; }
        internal int SkippedInstances { get; private set; }
        internal string Current { get; private set; }
        internal int InstanceCount { get { return Items.Count(c => !c.Container && c.Error == null); } }

        internal CatiaImportSession(CatiaImportSource source, StructureNode draft, ITxObject parent, bool newRoot, string rootName, bool includeRoot, string outputDirectory = null,
            Func<string, string, CancellationToken, CatiaTessellation> tessellate = null)
        {
            if (source == null || draft == null || parent == null || !(parent is ITxCompoundPartCreation))
                throw new InvalidOperationException("请读取 CATIA，并在当前 PS 工程确认目标容器。");
            _source = source; _parent = parent; _newRoot = newRoot;
            _tessellate = tessellate ?? CatiaTessellation.Capture;
            _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            _library = Path.GetFullPath(TxApplication.SystemRootDirectory);
            OutputDirectory = CatiaLibraryDestination.Resolve(_library, outputDirectory, source.DocumentPath);
            LibraryInsertion.CheckDestination(Path.Combine(OutputDirectory, "location.cojt"), _library);
            _parentLocation = parent.Name + "|" + StructureEngine.Id(StructureEngine.Parent(parent));
            var root = new StructureChange { Node = new StructureNode { Key = "destination", SourceName = newRoot ? StructureEngine.Clean(rootName) : parent.Name, IsContainer = true },
                Name = newRoot ? StructureEngine.Clean(rootName) : parent.Name, Container = true, Existing = newRoot ? null : parent };
            Items.Add(root); Add(draft.Copy(), root, includeRoot);
            if (InstanceCount == 0) throw new InvalidOperationException("请保留至少一个参与导入的零件；可在左树右键恢复纳入零件或分支。");
            foreach (var item in Items.Where(c => !c.Container))
            {
                string issue;
                if (source.GeometryIssues.TryGetValue(source.Parts[item.Node.Key].File, out issue))
                    throw new IOException("所选零件 " + item.Node.SourceName + "：" + issue);
            }
        }
        private void Add(StructureNode node, StructureChange parent, bool include)
        {
            if (!node.Include) return;
            var next = parent;
            if (!node.IsContainer || (include && !node.SkipLevel))
            {
                next = new StructureChange { Node = node, Parent = parent, Name = StructureEngine.Clean(node.Label), Container = node.IsContainer };
                if (!node.IsContainer && !_source.Parts.ContainsKey(node.Key)) throw new IOException("来源零件引用丢失：" + node.SourceName);
                Items.Add(next);
            }
            foreach (var child in node.Children) Add(child, next, true);
        }
        internal void EnsureDocument()
        {
            if (!ReferenceEquals(_document, TxApplication.ActiveDocument) || _generation != SceneUndoScope.DocumentGeneration)
                throw new InvalidOperationException("PS 工程已切换或重载，请重新读取来源。");
            if (!string.Equals(_library, Path.GetFullPath(TxApplication.SystemRootDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("系统库已切换，请重新读取来源。");
        }
        private void Require(CatiaImportStage stage)
        { EnsureDocument(); if (Stage != stage) throw new InvalidOperationException("当前步骤不能执行，失败后请核对结果并重新读取来源。"); }

        // One user-visible format stage: JT + COJT + the receiver project's PmPartPrototype.
        internal async Task ConvertAsync(CancellationToken cancel, IProgress<string> progress)
        {
            Require(CatiaImportStage.Ready);
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (!(TxApplication.ActiveDocument.PlatformGlobalServicesProvider is ITxOfflineGlobalServicesProvider))
                    throw new InvalidOperationException("CATIA 导入目前要求 PS 离线工程。");
                var manager = new TxTunePmDocumentManager();
                string worker = Path.Combine(Path.GetDirectoryName(typeof(CatiaImportSession).Assembly.Location), "CatiaPartTree", "TxTools.CatiaConverter.exe");
                if (!File.Exists(worker)) throw new FileNotFoundException("缺少格式转换程序，请完整部署插件及 CatiaPartTree 子目录。", worker);
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Items.Where(c => !c.Container).Select(c => _source.Parts[c.Node.Key].File).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    string directory = CatiaLibraryDestination.Package(OutputDirectory, file, reserved);
                    LibraryInsertion.CheckDestination(directory, _library);
                    var package = new Package { Source = file, Hash = _source.Files[file], Directory = directory, Jt = Path.Combine(directory, name + ".jt") };
                    _packages.Add(package); _bySource.Add(file, package);
                }
                await Task.Run(() =>
                {
                    for (int i = 0; i < _packages.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested(); var package = _packages[i];
                        progress.Report("格式转换 " + (i + 1) + "/" + _packages.Count + "：" + Path.GetFileName(package.Source));
                        try
                        {
                            CatiaLibraryDestination.CreatePackage(package.Directory);
                            CatiaTessellation tessellation = null;
                            try
                            {
                                progress.Report("重新细分 CATIA 曲面：" + Path.GetFileName(package.Source));
                                tessellation = _tessellate(package.Source, package.Hash, cancel);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error)
                            {
                                Warnings.Add(Path.GetFileName(package.Source) + "：CATIA 曲面重新细分未完成，使用文档内嵌网格；" +
                                    (error is AggregateException ? error.Message : error.GetBaseException().Message));
                            }
                            using (tessellation) ConvertPart(worker, package, cancel, tessellation);
                            if (!string.IsNullOrWhiteSpace(package.Warning)) Warnings.Add(Path.GetFileName(package.Source) + "：" + package.Warning);
                            package.JtHash = CatiaImportSource.Hash(package.Jt);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { package.Error = error.GetBaseException().Message; }
                    }
                }, cancel);
                EnsureDocument(); cancel.ThrowIfCancellationRequested();
                foreach (var package in _packages.Where(p => p.Error == null))
                {
                    cancel.ThrowIfCancellationRequested(); EnsureDocument();
                    try
                    {
                        var prototype = DevicePrototypes.CreateTargetData(new TransferPackage
                        {
                            Name = Path.GetFileName(package.Source),
                            Prototype = new DevicePrototypeData { Type = "PmPartPrototype", Family = "PartPrototype", Weight = 0 }
                        });
                        manager.SavePrototypeDataToDocument(package.Directory, prototype);
                        var saved = manager.LoadPrototypeDataFromDocument(package.Directory);
                        if (saved == null || saved.Type != prototype.Type || saved.Family != prototype.Family || saved.ExternalId != prototype.ExternalId)
                            throw new IOException("PmPartPrototype 设置校验失败：" + package.Directory);
                        package.TuneHash = CatiaImportSource.Hash(Path.Combine(package.Directory, "TuneData.xml"));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { package.Error = error.GetBaseException().Message; }
                }
                var failed = _packages.Where(p => p.Error != null).ToArray();
                if (failed.Length == _packages.Count)
                    throw new IOException("全部 " + failed.Length + " 个零件格式转换失败：" +
                        string.Join("；", failed.Select(p => Path.GetFileName(p.Source) + "：" + p.Error)));
                foreach (var package in failed)
                {
                    var items = Items.Where(c => !c.Container && Same(c, package.Source)).ToArray();
                    foreach (var item in items) item.Error = "转换失败，已跳过";
                    SkippedInstances += items.Length;
                    Warnings.Add("已跳过 " + Path.GetFileName(package.Source) + "（" + items.Length + " 个实例）：" + package.Error);
                }
                Stage = CatiaImportStage.Converted;
            }
            catch { Stage = CatiaImportStage.Failed; throw; }
        }
        private bool Same(StructureChange item, string file)
        {
            CatiaSourcePart part;
            return _source.Parts.TryGetValue(item.Node.Key, out part) && string.Equals(part.File, file, StringComparison.OrdinalIgnoreCase);
        }
        private static void ConvertPart(string worker, Package package, CancellationToken cancel, CatiaTessellation tessellation)
        {
            string mesh = tessellation == null ? "" : " " + Quote("mesh=" + tessellation.File) + " " + Quote("meshhash=" + tessellation.Hash);
            var start = new ProcessStartInfo(worker, Quote(package.Source) + " " + Quote(package.Jt) + " " + Quote(package.Hash) + " quality" + mesh)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
            using (var process = Process.Start(start))
            {
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                try
                {
                    while (!process.WaitForExit(200))
                    {
                        cancel.ThrowIfCancellationRequested();
                    }
                    Task.WaitAll(stdout, stderr);
                    if (process.ExitCode != 0 || !File.Exists(package.Jt) || new FileInfo(package.Jt).Length < 105)
                        throw new IOException(string.IsNullOrWhiteSpace(stderr.Result)
                            ? "转换程序未产出 JT（退出码 " + process.ExitCode + "）"
                            : stderr.Result.Trim());
                    package.Warning = stderr.Result.Trim();
                }
                finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
            }
        }
        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0) throw new IOException("文件路径包含无效引号。");
            return "\"" + value + "\"";
        }
        private void ValidatePackages()
        {
            foreach (var package in _packages.Where(p => p.Error == null))
            {
                LibraryInsertion.CheckDestination(package.Directory, _library);
                if (CatiaImportSource.Hash(package.Jt) != package.JtHash || CatiaImportSource.Hash(Path.Combine(package.Directory, "TuneData.xml")) != package.TuneHash)
                    throw new IOException("转换结果已变化，请重新读取并转换：" + package.Directory);
                var prototype = new TxTunePmDocumentManager().LoadPrototypeDataFromDocument(package.Directory);
                if (prototype == null || prototype.Type != "PmPartPrototype" || prototype.Family != "PartPrototype") throw new IOException("当前工程的 PmPartPrototype 原型已失效。");
            }
        }
        // Containers without any remaining instance are not created, so a skipped
        // part never leaves an empty container behind.
        private HashSet<StructureChange> LiveContainers()
        {
            var live = new HashSet<StructureChange>();
            foreach (var item in Items.Where(c => !c.Container && c.Error == null))
                for (var node = item.Parent; node != null && live.Add(node); node = node.Parent) { }
            return live;
        }
        internal void CreateTree()
        {
            Require(CatiaImportStage.Converted);
            bool attempted = false;
            try
            {
                ValidatePackages();
                if (!_parent.IsValid() || _parent.Name + "|" + StructureEngine.Id(StructureEngine.Parent(_parent)) != _parentLocation)
                    throw new InvalidOperationException("选取的目标容器已失效、改名或移动，请重新预览。");
                var live = LiveContainers();
                using (var undo = SceneUndoScope.Begin("CATIA 导入 · 创建目标树"))
                    foreach (var item in Items.Where(c => c.Container && (c == Items[0] || live.Contains(c))))
                    {
                        undo.EnsureDocument(); Current = item.Name;
                        if (item == Items[0] && !_newRoot) item.Result = _parent;
                        else
                        {
                            SceneWriteAttempted = attempted = true;
                            var parent = item.Parent == null ? _parent : item.Parent.Result;
                            item.Result = PsCompoundHelper.CreatePart(parent, item.Name, item.Name, setTypeName: false); Created++;
                            if (item.Result == null || item.Result.Name != item.Name || !StructureEngine.Children(parent).Any(o => StructureEngine.Id(o) == StructureEngine.Id(item.Result)))
                                throw new IOException("目标容器名称或父级校验失败：" + item.Name);
                        }
                    }
                Stage = CatiaImportStage.TreeCreated;
            }
            catch { Stage = attempted ? CatiaImportStage.Failed : CatiaImportStage.Converted; throw; }
        }
        internal void Import(Action<string> log)
        {
            Require(CatiaImportStage.TreeCreated);
            bool attempted = false;
            try
            {
                ValidatePackages();
                var live = LiveContainers();
                foreach (var container in Items.Where(c => c.Container && (c == Items[0] || live.Contains(c))))
                {
                    var expectedParent = container.Parent == null ? _parent : container.Parent.Result;
                    if (container.Result == null || !container.Result.IsValid() || container.Result.Name != container.Name ||
                        (container.Result != _parent && !StructureEngine.Children(expectedParent).Any(o => StructureEngine.Id(o) == StructureEngine.Id(container.Result))))
                        throw new InvalidOperationException("本批目标容器已失效、改名或移动，请重新预览。");
                }
                foreach (var package in _packages.Where(p => p.Error == null))
                {
                    string issue = LibraryInsertion.SupportIssue(package.Directory);
                    if (issue != null) throw new InvalidOperationException(issue);
                }
                using (var undo = SceneUndoScope.Begin("CATIA 导入 · 导入归位"))
                {
                    foreach (var item in Items.Where(c => !c.Container && c.Error == null))
                    {
                        undo.EnsureDocument(); Current = item.Name;
                        var source = _source.Parts[item.Node.Key]; var package = _bySource[source.File];
                        ITxObject inserted = null; string summary = ""; SceneWriteAttempted = attempted = true;
                        try
                        {
                            if (!ComponentIO.InsertCojt(package.Directory, item.Name, package.Directory, _library, 0, 0, 0, log, out summary, out inserted))
                            { item.Result = inserted; throw new IOException(summary.Trim()); }
                            item.Result = inserted;
                            if (inserted == null || !inserted.IsValid()) throw new IOException("插入后未取得有效实例");
                            undo.EnsureDocument(); inserted.Name = item.Name;
                            dynamic parent = item.Parent.Result; parent.AddObject(inserted);
                            var located = inserted as ITxLocatableObject;
                            if (located == null) throw new IOException("插入对象无法设置实例位置");
                            var c = source.Frame;
                            var world = TxMath.FromWorldValues(new[] { c[0], c[3], c[6], c[9], c[1], c[4], c[7], c[10], c[2], c[5], c[8], c[11] });
                            located.AbsoluteLocation = world;
                            var actual = TxMath.WorldValues(located.AbsoluteLocation); var expected = TxMath.WorldValues(world);
                            if (!StructureEngine.Children(item.Parent.Result).Any(o => StructureEngine.Id(o) == StructureEngine.Id(inserted)) || inserted.Name != item.Name || actual.Where((v, i) => Math.Abs(v - expected[i]) > 1e-6).Any())
                                throw new IOException("实例名称、归属或位置校验失败");
                            item.Node.BoundPart = inserted; Imported++;
                            if (!string.IsNullOrWhiteSpace(summary)) log?.Invoke("[插入] " + item.Name + Environment.NewLine + summary.Trim());
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            // A blocked compatibility layer is process wide: stop instead of
                            // repeating a known-bad insert for every remaining instance.
                            string blocked = LibraryInsertion.SupportIssue(package.Directory);
                            if (blocked != null) throw new IOException("插入环境不可用：" + blocked, error);
                            bool partial = inserted != null;
                            item.Error = partial ? "已插入，需核对" : "未插入";
                            SkippedInstances++;
                            string detail = error.GetBaseException().Message;
                            Warnings.Add("已跳过实例 " + item.Name + "（" + item.Error + "）：" + detail);
                            log?.Invoke("[跳过] " + item.Name + "：[" + item.Error + "] " + detail);
                        }
                    }
                    if (Imported == 0 && Items.Any(c => !c.Container))
                        throw new IOException("全部实例导入失败，请按上方警告处理后重试；场景未建立可用实例。");
                    foreach (var group in Items.Where(c => c.Parent != null && (c.Container ? (c == Items[0] || live.Contains(c)) : c.Error == null)).GroupBy(c => c.Parent))
                    {
                        ITxObject previous = null; dynamic container = group.Key.Result;
                        foreach (var item in group)
                        {
                            undo.EnsureDocument();
                            if (previous != null) container.AddObjectAfter(item.Result, previous);
                            previous = item.Result;
                        }
                        var ids = new HashSet<string>(group.Select(c => StructureEngine.Id(c.Result)));
                        if (!StructureEngine.Children(group.Key.Result).Where(o => ids.Contains(StructureEngine.Id(o))).Select(StructureEngine.Id).SequenceEqual(group.Select(c => StructureEngine.Id(c.Result))))
                            throw new IOException("目标子节点顺序与预览不一致：" + group.Key.Name);
                    }
                }
                Stage = CatiaImportStage.Completed;
            }
            catch { Stage = attempted ? CatiaImportStage.Failed : CatiaImportStage.TreeCreated; throw; }
        }
        internal string Failure(Exception error)
        {
            return "已停止：" + error.Message + Environment.NewLine + "已建容器 " + Created + "，已导入归位 " + Imported + "；当前：" + Current +
                (Stage == CatiaImportStage.Converted || Stage == CatiaImportStage.TreeCreated ? "。当前步骤尚未写入场景，可处理原因后重试；前序结果保留。" :
                    SceneWriteAttempted ? "。场景可能有部分结果，请检查；建树与导入可分别 Ctrl+Z 撤销，之后重新读取。" : "。未写入 PS 场景，可处理原因后重新检查。") +
                (SkippedInstances == 0 ? "" : Environment.NewLine + "已跳过 " + SkippedInstances + " 个实例（转换失败，未参与建树与导入）") +
                (OutputDirectory == null ? "" : Environment.NewLine + "转换目录：" + OutputDirectory + "（已导入模型依赖此目录，请保留）");
        }
    }
}
