using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
            internal string Source, Hash, Directory, Jt, JtHash, TuneHash, Error, Warning, Log;
        }
        private sealed class ConversionProgress : IProgress<string>
        {
            private readonly Action<string> _report;
            internal ConversionProgress(Action<string> report) { _report = report; }
            public void Report(string value) { _report(value); }
        }
        // Each action owns one package and one isolated converter process. Wait
        // for all active actions (including process cleanup) before returning.
        internal static Task RunConversionBatchAsync<T>(IList<T> packages, int parallelism,
            CancellationToken cancel, Action<T> convert)
        {
            if (parallelism < 1) throw new ArgumentOutOfRangeException(nameof(parallelism));
            int next = -1;
            var workers = Enumerable.Range(0, Math.Min(packages.Count, parallelism)).Select(_ =>
                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int index = Interlocked.Increment(ref next);
                        if (index >= packages.Count) return;
                        cancel.ThrowIfCancellationRequested();
                        convert(packages[index]);
                    }
                }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            return Task.WhenAll(workers);
        }
        private readonly CatiaImportSource _source;
        private readonly object _document;
        private readonly long _generation;
        private readonly string _library, _parentLocation;
        private readonly ITxObject _parent;
        private readonly bool _newRoot;
        private bool _modernWorker;
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
        internal int NeedsReview { get; private set; }
        internal int Created { get; private set; }
        internal int SkippedInstances { get; private set; }
        internal string ImportSummary => "已创建 " + Created + " 个复合零件，已插入 " + (Imported + NeedsReview) +
            " 个实例（已归位 " + Imported + "，待核对 " + NeedsReview + "），未插入 " + SkippedInstances + " 个实例。";
        internal string Current { get; private set; }
        internal string DiagnosticLog { get; private set; }
        internal int InstanceCount { get { return Items.Count(c => !c.Container && c.Error == null); } }
        internal bool CanConvert { get { return InstanceCount > 0 && Items.Where(c => !c.Container).All(c => _source.Parts[c.Node.Key].GeometryEntries == null); } }
        internal bool TreeBuilt { get; private set; }
        internal ITxObject Parent { get { return _parent; } }
        private bool _converted;

        internal CatiaImportSession(CatiaImportSource source, StructureNode draft, ITxObject parent, bool newRoot, string rootName, bool includeRoot, string outputDirectory = null)
        {
            if (source == null || draft == null || parent == null || !(parent is ITxCompoundPartCreation))
                throw new InvalidOperationException("请读取 CATIA，并在当前 PS 工程确认目标容器。");
            _source = source; _parent = parent; _newRoot = newRoot;
            _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            _library = Path.GetFullPath(TxApplication.SystemRootDirectory);
            OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory) ? CatiaLibraryDestination.Default(_library, source.DocumentPath) : outputDirectory;
            _parentLocation = parent.Name + "|" + StructureEngine.Id(StructureEngine.Parent(parent));
            var root = new StructureChange { Node = new StructureNode { Key = "destination", SourceName = newRoot ? StructureEngine.Clean(rootName) : parent.Name, IsContainer = true },
                Name = newRoot ? StructureEngine.Clean(rootName) : parent.Name, Container = true, Existing = newRoot ? null : parent };
            Items.Add(root); Add(draft.Copy(), root, includeRoot);
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
            EnsureDocument();
            if (Stage != CatiaImportStage.Ready && Stage != CatiaImportStage.TreeCreated)
                throw new InvalidOperationException("当前状态不能转换，请重新读取来源。");
            try
            {
                cancel.ThrowIfCancellationRequested();
                if (InstanceCount == 0) throw new InvalidOperationException("没有参与转换的 CATPart 引用，可直接创建复合零件。");
                foreach (var item in Items.Where(c => !c.Container))
                {
                    var part = _source.Parts[item.Node.Key];
                    if (part.ConversionIssue != null) throw new NotSupportedException(part.ConversionIssue);
                    string issue;
                    if (_source.GeometryIssues.TryGetValue(_source.Parts[item.Node.Key].File, out issue))
                        throw new IOException("所选零件 " + item.Node.SourceName + "：" + issue);
                }
                OutputDirectory = CatiaLibraryDestination.Resolve(_library, OutputDirectory, _source.DocumentPath);
                LibraryInsertion.CheckDestination(Path.Combine(OutputDirectory, "location.cojt"), _library);
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
                // Probe once, before starting any concurrent conversion.
                _modernWorker = await Task.Run(() => SupportsCurrentProtocol(worker, cancel, progress), cancel);
                int parallelism = Math.Min(_packages.Count, Math.Max(1, Environment.ProcessorCount));
                int completed = 0;
                var progressGate = new object();
                var batchProgress = new ConversionProgress(message =>
                {
                    lock (progressGate) progress.Report("JT 转换已完成 " + completed + "/" + _packages.Count +
                        "（最多 " + parallelism + " 路并行）" + Environment.NewLine + message);
                });
                Current = "JT 并行转换（" + _packages.Count + " 个文件，最多 " + parallelism + " 路）";
                batchProgress.Report("开始批量转换");
                await RunConversionBatchAsync(_packages, parallelism, cancel, package =>
                {
                    try
                    {
                        CatiaLibraryDestination.CreatePackage(package.Directory);
                        ConvertPart(worker, package, cancel, batchProgress);
                        cancel.ThrowIfCancellationRequested();
                        package.JtHash = CatiaImportSource.Hash(package.Jt);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { package.Error = error.GetBaseException().Message; }
                    lock (progressGate)
                    {
                        completed++;
                        batchProgress.Report(Path.GetFileName(package.Source) + (package.Error == null ? " · JT 已完成" : " · 转换失败，已跳过"));
                    }
                });
                EnsureDocument(); cancel.ThrowIfCancellationRequested();
                foreach (var package in _packages)
                {
                    if (!string.IsNullOrWhiteSpace(package.Warning)) Warnings.Add(Path.GetFileName(package.Source) + "：" + package.Warning);
                    if (package.Log != null) DiagnosticLog = package.Log;
                }
                foreach (var package in _packages.Where(p => p.Error == null))
                {
                    cancel.ThrowIfCancellationRequested(); EnsureDocument();
                    Current = Path.GetFileName(package.Source);
                    progress.Report(Current + " · 封装 COJT 并写入 PmPartPrototype");
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
                _converted = true;
                Stage = CatiaImportStage.Converted;
            }
            catch { Stage = CatiaImportStage.Failed; throw; }
        }
        private bool Same(StructureChange item, string file)
        {
            CatiaSourcePart part;
            return _source.Parts.TryGetValue(item.Node.Key, out part) && string.Equals(part.File, file, StringComparison.OrdinalIgnoreCase);
        }
        private bool SupportsCurrentProtocol(string worker, CancellationToken cancel, IProgress<string> progress)
        {
            using (var diagnostics = new CatiaConversionDiagnostics(_source.DocumentPath, message => progress.Report(message)))
            {
                DiagnosticLog = diagnostics.LogPath;
                diagnostics.Begin("核对插件与转换器接口");
                diagnostics.Write("PLUGIN " + typeof(CatiaImportSession).Assembly.Location);
                diagnostics.Write("CONVERTER " + worker + " sha256=" + CatiaImportSource.Hash(worker));
                var start = new ProcessStartInfo(worker, "--protocol")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            cancel.ThrowIfCancellationRequested();
                            // Only the no-work capability query is bounded. Actual
                            // CAD/JT conversion retains its unrestricted runtime.
                            if (watch.ElapsedMilliseconds >= 5000) break;
                        }
                        if (process.HasExited)
                        {
                            Task.WaitAll(output, errors);
                            diagnostics.Write("PROTOCOL exit=" + process.ExitCode + Environment.NewLine + output.Result + errors.Result);
                            if (process.ExitCode == 0 && output.Result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .Contains("TxTools.CatiaConverter/2")) return true;
                        }
                        diagnostics.Write("Using legacy three-argument conversion.");
                        Warnings.Add("部署的转换器未确认支持当前接口，已使用原有三参数转换；请同时更新 TxTools.dll 与 CatiaPartTree/TxTools.CatiaConverter.exe。接口日志：" + diagnostics.LogPath);
                        return false;
                    }
                    finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                }
            }
        }
        private void ConvertPart(string worker, Package package, CancellationToken cancel, IProgress<string> progress)
        {
            cancel.ThrowIfCancellationRequested();
            using (var diagnostics = new CatiaConversionDiagnostics(package.Source, message => progress.Report(message)))
            {
                package.Log = diagnostics.LogPath;
                var start = new ProcessStartInfo(worker, Quote(package.Source) + " " + Quote(package.Jt) + " " + Quote(package.Hash) + (_modernWorker ? " quality" : ""))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
                diagnostics.Begin("启动 JT 转换程序");
                using (var process = Process.Start(start))
                {
                    diagnostics.Write("PROCESS pid=" + process.Id + " executable=" + worker + " output=" + package.Jt);
                    diagnostics.Begin("生成 JT（曲面优化与写入）");
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    var heartbeat = Stopwatch.StartNew();
                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (heartbeat.ElapsedMilliseconds >= 1000) { diagnostics.Pulse(false); heartbeat.Restart(); }
                        }
                        Task.WaitAll(stdout, stderr);
                        diagnostics.Write("EXIT " + process.ExitCode + Environment.NewLine + stdout.Result + stderr.Result);
                        if (process.ExitCode != 0 || !File.Exists(package.Jt) || new FileInfo(package.Jt).Length < 105)
                            throw new IOException(string.IsNullOrWhiteSpace(stderr.Result)
                                ? "转换程序未产出 JT（退出码 " + process.ExitCode + "）"
                                : stderr.Result.Trim());
                        package.Warning = stderr.Result.Trim();
                        diagnostics.Complete("JT 已生成：" + package.Jt);
                    }
                    catch (Exception error) { diagnostics.Failure(error); throw; }
                    finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                }
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
        // Only containers with imported instances participate in import ordering.
        private HashSet<StructureChange> LiveContainers()
        {
            var live = new HashSet<StructureChange>();
            foreach (var item in Items.Where(c => !c.Container && c.Error == null))
                for (var node = item.Parent; node != null && live.Add(node); node = node.Parent) { }
            return live;
        }
        internal void CreateTree()
        {
            EnsureDocument();
            if (Stage != CatiaImportStage.Ready && Stage != CatiaImportStage.Converted && Stage != CatiaImportStage.TreeCreated)
                throw new InvalidOperationException("当前状态不能创建复合零件，请重新读取来源。");
            var previousStage = Stage;
            bool attempted = false;
            try
            {
                if (_converted) ValidatePackages();
                if (!_parent.IsValid() || _parent.Name + "|" + StructureEngine.Id(StructureEngine.Parent(_parent)) != _parentLocation)
                    throw new InvalidOperationException("选取的目标容器已失效、改名或移动，请重新预览。");
                using (var undo = SceneUndoScope.Begin("CATIA 导入 · 创建目标树"))
                    foreach (var item in Items.Where(c => c.Container))
                    {
                        undo.EnsureDocument(); Current = item.Name;
                        if (item.Result != null)
                        {
                            var expectedParent = item.Parent == null ? _parent : item.Parent.Result;
                            if (!item.Result.IsValid() || item.Result.Name != item.Name ||
                                (item.Result != _parent && !StructureEngine.Children(expectedParent).Any(o => StructureEngine.Id(o) == StructureEngine.Id(item.Result))))
                                throw new InvalidOperationException("已创建的复合零件已变化，请重新读取来源。");
                            continue;
                        }
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
                TreeBuilt = true; Stage = CatiaImportStage.TreeCreated;
            }
            catch { Stage = attempted ? CatiaImportStage.Failed : previousStage; throw; }
        }
        private static void VerifyPlacement(StructureChange item, ITxLocatableObject located, double[] expected)
        {
            var issues = new List<string>();
            if (!string.Equals(item.Result.Name, item.Name, StringComparison.Ordinal))
                issues.Add($"名称应为“{item.Name}”，PS 中为“{item.Result.Name}”");
            if (!StructureEngine.Children(item.Parent.Result).Any(o => StructureEngine.Id(o) == StructureEngine.Id(item.Result)))
                issues.Add($"未找到目标父级“{item.Parent.Result.Name}”下的该实例");

            var actual = TxMath.WorldValues(located.AbsoluteLocation);
            if (actual.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                issues.Add("PS 返回的位姿包含无效数值");
            else
            {
                double distance = Math.Sqrt(new[] { 3, 7, 11 }.Sum(i => Math.Pow(actual[i] - expected[i], 2)));
                double basisError = Enumerable.Range(0, 12).Where(i => i % 4 != 3).Max(i => Math.Abs(actual[i] - expected[i]));
                // Compare translation in millimeters and basis entries as unitless values.
                if (distance > 0.001)
                    issues.Add("位置偏差 " + distance.ToString("G6", CultureInfo.InvariantCulture) + " mm（容差 0.001 mm）");
                if (basisError > 1e-6)
                    issues.Add("姿态矩阵最大分量偏差 " + basisError.ToString("G6", CultureInfo.InvariantCulture) + "（容差 1e-6）");
            }
            if (issues.Count != 0) throw new IOException(string.Join("，", issues));
        }

        internal void Import(Action<string> log)
        {
            Require(CatiaImportStage.TreeCreated);
            if (!_converted) throw new InvalidOperationException("请先转换格式。");
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
                            var expected = new[] { c[0], c[3], c[6], c[9], c[1], c[4], c[7], c[10], c[2], c[5], c[8], c[11] };
                            located.AbsoluteLocation = TxMath.FromWorldValues(expected);
                            VerifyPlacement(item, located, expected);
                            item.Node.BoundPart = inserted; Imported++;
                            log?.Invoke("[已归位] " + item.Name);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            // A blocked compatibility layer is process wide: stop instead of
                            // repeating a known-bad insert for every remaining instance.
                            string blocked = LibraryInsertion.SupportIssue(package.Directory);
                            if (blocked != null) throw new IOException("插入环境不可用：" + blocked, error);
                            bool partial = inserted != null && inserted.IsValid();
                            item.Result = partial ? inserted : null;
                            item.Error = partial ? "已插入，需核对" : "未插入";
                            if (partial) NeedsReview++;
                            else SkippedInstances++;
                            string detail = error.GetBaseException().Message;
                            string message = "[" + item.Error + "] " + item.Name + "：" + detail;
                            Warnings.Add(message);
                            log?.Invoke(message);
                        }
                    }
                    if (Imported + NeedsReview == 0 && Items.Any(c => !c.Container))
                        throw new IOException("未插入任何实例，请查看日志。");
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
            return "已停止：" + error.Message + Environment.NewLine + ImportSummary + " 当前：" + Current +
                (Stage == CatiaImportStage.Converted || Stage == CatiaImportStage.TreeCreated ? "。当前步骤尚未写入场景，可处理原因后重试；前序结果保留。" :
                    SceneWriteAttempted ? "。场景可能有部分结果，请检查；建树与导入可分别 Ctrl+Z 撤销，之后重新读取。" : "。未写入 PS 场景，可处理原因后重新检查。") +
                (Warnings.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Warnings)) +
                (OutputDirectory == null ? "" : Environment.NewLine + "转换目录：" + OutputDirectory + "（已导入模型依赖此目录，请保留）") +
                (DiagnosticLog == null ? "" : Environment.NewLine + "诊断日志：" + DiagnosticLog);
        }
    }
}
