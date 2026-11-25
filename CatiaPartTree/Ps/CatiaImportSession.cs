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
using TxTools.CatiaPartTree.Conversion;
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
            internal string Source, Member, StepDefinition, StepMesh, Hash, Directory, Jt, JtHash, TuneHash, Error, Warning, Log;
            internal bool XmlRepresentation;
            internal StructureChange[] Instances;
            internal string Label => StepDefinition == null ? Member ?? Path.GetFileName(Source) : Path.GetFileName(Jt);
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
        private readonly bool _newRoot, _fileOnly;
        // Base destination folder; the CATIA document name is an optional subfolder.
        private readonly string _folder;
        private readonly bool _createDocumentFolder;
        // Optional: mirror the compound-part hierarchy as subfolders for packages.
        private readonly bool _hierarchyFolders;
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
        internal bool CanConvert => Items.Any(c => !c.Container && c.Error == null && _source.Parts[c.Node.Key].ConversionIssue == null);
        internal int ConvertedFileCount => _packages.Count(p => p.Error == null);
        internal bool TreeBuilt { get; private set; }
        internal ITxObject Parent { get { return _parent; } }
        internal string JtForPart(string key)
        {
            return _bySource.TryGetValue(_source.Parts[key].AssetKey, out var package) && package.Error == null
                ? package.Jt : null;
        }

        internal static CatiaImportSession ForJtExport(CatiaImportSource source, StructureNode draft)
        {
            return new CatiaImportSession(source, draft);
        }

        private CatiaImportSession(CatiaImportSource source, StructureNode draft)
        {
            _source = source;
            _fileOnly = true;
            var root = new StructureChange { Container = true };
            Items.Add(root);
            Add(draft.Copy(), root, true);
        }

        private bool _converted;
        // Containers were already re-created by Paste, so the standalone tree stage
        // must not paste a second time when the user clicks the button again.
        private bool _containersPasted;

        internal CatiaImportSession(CatiaImportSource source, StructureNode draft, ITxObject parent, bool newRoot, string rootName, bool includeRoot, string outputDirectory = null, bool createDocumentFolder = true, bool hierarchyFolders = false)
        {
            if (source == null || draft == null || parent == null || !(parent is ITxCompoundPartCreation))
                throw new InvalidOperationException("请读取 CATIA，并在当前 PS 工程确认目标容器。");
            _source = source; _parent = parent; _newRoot = newRoot;
            _document = TxApplication.ActiveDocument; _generation = SceneUndoScope.DocumentGeneration;
            _library = Path.GetFullPath(TxApplication.SystemRootDirectory);
            _folder = CatiaLibraryDestination.ResolveFolder(_library, outputDirectory);
            _createDocumentFolder = createDocumentFolder;
            _hierarchyFolders = hierarchyFolders;
            OutputDirectory = FinalFolder();
            _parentLocation = parent.Name + "|" + StructureEngine.Id(StructureEngine.Parent(parent));
            var root = new StructureChange { Node = new StructureNode { Key = "destination", SourceName = newRoot ? StructureEngine.Clean(rootName) : parent.Name, IsContainer = true },
                Name = newRoot ? StructureEngine.Clean(rootName) : parent.Name, Container = true, Existing = newRoot ? null : parent };
            Items.Add(root); Add(draft.Copy(), root, includeRoot);
        }
        private string FinalFolder()
        {
            return _createDocumentFolder ? CatiaLibraryDestination.DocumentFolder(_folder, _source.DocumentPath) : _folder;
        }
        private string PackageFolder(StructureChange item)
        {
            if (!_hierarchyFolders) return OutputDirectory;
            var segments = new List<string>();
            for (var parent = item.Parent; parent != null && !ReferenceEquals(parent, Items[0]); parent = parent.Parent)
                if (parent.Container) segments.Add(CatiaLibraryDestination.FolderSegment(parent.Name));
            if (segments.Count == 0) return OutputDirectory;
            segments.Reverse();
            string path = OutputDirectory;
            foreach (string segment in segments) path = Path.Combine(path, segment);
            return path;
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
            if (_document == null || !ReferenceEquals(_document, TxApplication.ActiveDocument) ||
                _generation != SceneUndoScope.DocumentGeneration)
                throw new InvalidOperationException("PS 工程已切换或重载，请重新读取来源。");
            if (!string.Equals(_library, Path.GetFullPath(TxApplication.SystemRootDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("系统库已切换，请重新读取来源。");
        }
        private void Require(CatiaImportStage stage)
        { EnsureDocument(); if (Stage != stage) throw new InvalidOperationException("当前步骤不能执行，失败后请核对结果并重新读取来源。"); }

        // PS documents the call as refreshing every viewer with the current data.
        // Do it after the scene transaction ends so the object tree sees the final
        // hierarchy instead of the intermediate insert-at-root state.
        private static void RefreshView()
        {
            try { TxApplication.RefreshDisplay(); } catch (Exception) { }
        }

        // One user-visible format stage: JT + COJT + the receiver project's PmPartPrototype.
        internal async Task ConvertAsync(CancellationToken cancel, IProgress<string> progress, bool reduceMesh = true, string jtOutputDirectory = null)
        {
            if (!_fileOnly) EnsureDocument();
            if (_fileOnly && jtOutputDirectory == null)
                throw new InvalidOperationException("文件转换需要指定 JT 输出目录。");
            if (Stage != CatiaImportStage.Ready && Stage != CatiaImportStage.TreeCreated)
                throw new InvalidOperationException("当前状态不能转换，请重新读取来源。");
            string stepStaging = null;
            try
            {
                cancel.ThrowIfCancellationRequested();
                bool jtOnly = jtOutputDirectory != null;
                if (InstanceCount == 0) throw new InvalidOperationException("没有参与转换的零件。");
                OutputDirectory = jtOnly ? Path.GetFullPath(jtOutputDirectory) : FinalFolder();
                if (!jtOnly)
                {
                    LibraryInsertion.CheckDestination(Path.Combine(OutputDirectory, "location.cojt"), _library);
                    if (!(TxApplication.ActiveDocument.PlatformGlobalServicesProvider is ITxOfflineGlobalServicesProvider))
                        throw new InvalidOperationException("转换并导入需要 PS 离线工程。");
                }
                string worker = Path.Combine(Path.GetDirectoryName(typeof(CatiaImportSession).Assembly.Location), "CatiaPartTree", "TxTools.CatiaConverter.exe");
                if (!File.Exists(worker)) throw new FileNotFoundException("缺少格式转换程序，请完整部署插件及 CatiaPartTree 子目录。", worker);
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in Items.Where(c => !c.Container && c.Error == null)
                    .GroupBy(c => _source.Parts[c.Node.Key].AssetKey, StringComparer.OrdinalIgnoreCase))
                {
                    var item = group.First();
                    var part = _source.Parts[item.Node.Key];
                    string member = part.GeometryEntry;
                    string stepDefinition = part.StepDefinition;
                    if (part.ConversionIssue != null)
                    {
                        if (!_fileOnly && member == null && stepDefinition == null) throw new NotSupportedException(part.ConversionIssue);
                        SkipInstances(group.ToArray(), member ?? item.Node.SourceName, part.ConversionIssue);
                        continue;
                    }
                    if (_source.GeometryIssues.TryGetValue(part.File, out string issue))
                    {
                        if (!_fileOnly) throw new IOException("所选零件 " + item.Node.SourceName + "：" + issue);
                        SkipInstances(group.ToArray(), item.Node.SourceName, issue);
                        continue;
                    }
                    string name = member == null ? Path.GetFileNameWithoutExtension(part.File) :
                        ThreeDXmlSource.GeometryOutputStem(member, item.Node.PartNumber, item.Node.SourceName);
                    if (stepDefinition != null)
                        name = CatiaLibraryDestination.FolderSegment(item.Node.PartNumber ?? item.Node.SourceName);
                    var package = new Package { Source = part.File, Member = member, StepDefinition = stepDefinition,
                        Hash = _source.Files[part.File], Instances = group.ToArray(),
                        XmlRepresentation = _fileOnly && member != null && ThreeDXmlSource.IsXmlRepresentation(member) };
                    if (jtOnly)
                        package.Jt = CatiaLibraryDestination.Reserve(OutputDirectory, name, ".jt", reserved);
                    else
                    {
                        package.Directory = CatiaLibraryDestination.Reserve(PackageFolder(item), name, ".cojt", reserved);
                        LibraryInsertion.CheckDestination(package.Directory, _library);
                        package.Jt = Path.Combine(package.Directory, Path.GetFileNameWithoutExtension(package.Directory) + ".jt");
                    }
                    _packages.Add(package); _bySource.Add(group.Key, package);
                }
                if (_packages.Count == 0)
                {
                    if (!_fileOnly) throw new NotSupportedException("所选实例没有可转换的几何。");
                    Stage = CatiaImportStage.Converted;
                    return;
                }
                // Probe once, before starting any concurrent conversion.
                bool hasCgr = _packages.Any(package => package.Member != null && !package.XmlRepresentation);
                bool hasXml = _packages.Any(package => package.XmlRepresentation);
                var stepPackages = _packages.Where(package => package.StepDefinition != null).ToArray();
                bool modernWorker = await Task.Run(() => SupportsCurrentProtocol(worker, cancel, progress,
                    hasCgr, stepPackages.Length != 0, hasXml), cancel);
                bool optimize = reduceMesh && modernWorker;
                string meshMode = optimize ? "自动减面" : "不减面";
                int parallelism = Math.Min(_packages.Count, Math.Max(1, Environment.ProcessorCount));
                int completed = 0;
                var progressGate = new object();
                var batchProgress = new ConversionProgress(message =>
                {
                    lock (progressGate) progress.Report("JT 转换已完成 " + completed + "/" + _packages.Count +
                        "（" + meshMode + "，最多 " + parallelism + " 路并行）" + Environment.NewLine + message);
                });
                Current = "JT 并行转换（" + _packages.Count + " 个文件，最多 " + parallelism + " 路）";
                batchProgress.Report("开始批量转换");
                using (var archive = hasCgr || hasXml || stepPackages.Length != 0 ? File.OpenRead(_source.DocumentPath) : null)
                {
                    if (archive != null)
                    {
                        string digest = await Task.Run(() => CatiaImportSource.Hash(archive), cancel);
                        cancel.ThrowIfCancellationRequested();
                        if (digest != _source.Files[_source.DocumentPath])
                            throw new IOException("源文件在读取后发生变化，请重新读取来源。");
                    }
                    if (stepPackages.Length != 0)
                    {
                        stepStaging = StepSource.CreateStaging();
                        batchProgress.Report("正在细分选中的 STEP 零件");
                        await Task.Run(() => StepSource.PrepareMeshes(_source.DocumentPath,
                            stepPackages.Select(p => p.StepDefinition), stepStaging, cancel), cancel);
                        foreach (var package in stepPackages)
                            package.StepMesh = Path.Combine(stepStaging, package.StepDefinition + ".mesh");
                    }
                    await RunConversionBatchAsync(_packages, parallelism, cancel, package =>
                    {
                        try
                        {
                            if (package.StepMesh != null && StepSource.MeshIssue(package.StepMesh) is string issue)
                                throw new IOException(issue);
                            if (jtOnly) ConvertPart(worker, package, optimize, cancel, batchProgress);
                            else ConvertCojt(worker, package, optimize, cancel, batchProgress);
                            if (package.StepMesh != null && StepSource.MeshCoverageWarning(package.StepMesh) is string coverage)
                                package.Warning = string.IsNullOrEmpty(package.Warning) ? coverage : package.Warning + Environment.NewLine + coverage;
                            cancel.ThrowIfCancellationRequested();
                            if (!jtOnly) package.JtHash = CatiaImportSource.Hash(package.Jt);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { package.Error = error.GetBaseException().Message; }
                        lock (progressGate)
                        {
                            completed++;
                            batchProgress.Report(package.Label + (package.Error == null ? " · JT 已完成" : " · 转换失败，已跳过"));
                        }
                    });
                }
                if (!_fileOnly) EnsureDocument();
                cancel.ThrowIfCancellationRequested();
                foreach (var package in _packages)
                {
                    if (!string.IsNullOrWhiteSpace(package.Warning)) Warnings.Add(package.Label + "：" + package.Warning);
                    if (package.Log != null) DiagnosticLog = package.Log;
                }
                if (!jtOnly) SavePrototypes(cancel, progress);
                var failed = _packages.Where(p => p.Error != null).ToArray();
                foreach (var package in failed)
                    SkipInstances(package.Instances, package.Label, package.Error);
                if (!_fileOnly && failed.Length == _packages.Count)
                    throw new IOException("全部 " + failed.Length + " 个几何文件转换失败：" +
                        string.Join("；", failed.Take(5).Select(p => p.Label + "：" + p.Error)) +
                        (failed.Length > 5 ? "；其余失败见逐项日志。" : ""));
                _converted = !jtOnly;
                Stage = CatiaImportStage.Converted;
            }
            catch { Stage = CatiaImportStage.Failed; throw; }
            finally
            {
                if (stepStaging != null) Directory.Delete(stepStaging, true);
            }
        }

        private void SavePrototypes(CancellationToken cancel, IProgress<string> progress)
        {
            var manager = new TxTunePmDocumentManager();
            foreach (var package in _packages.Where(p => p.Error == null))
            {
                cancel.ThrowIfCancellationRequested(); EnsureDocument();
                Current = package.Label;
                progress.Report(Current + " · 封装 COJT 并写入 PmPartPrototype");
                try
                {
                    var prototype = DevicePrototypes.CreateTargetData(new TransferPackage
                    {
                        Name = package.Member == null && package.StepDefinition == null ? package.Label : Path.GetFileName(package.Jt),
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
        }
        private void SkipInstances(StructureChange[] items, string label, string reason)
        {
            foreach (var item in items) item.Error = "转换失败，已跳过";
            SkippedInstances += items.Length;
            Warnings.Add((_fileOnly ? "未生成 JT · " : "已跳过 ") + label + "（" + items.Length + " 个实例）：" + reason);
        }
        private bool SupportsCurrentProtocol(string worker, CancellationToken cancel, IProgress<string> progress,
            bool requireCgr, bool requireStep, bool requireAssembly)
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
                                .Contains("TxTools.CatiaConverter/2") &&
                                (!requireCgr || output.Result.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains("cgr")) &&
                                (!requireStep || output.Result.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains("stepmesh")) &&
                                (!requireAssembly || output.Result.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains("assembly")))
                                return true;
                        }
                        if (requireCgr) throw new NotSupportedException("部署的转换器缺少 CGR 支持，请同时更新插件和转换器。接口日志：" + diagnostics.LogPath);
                        if (requireStep) throw new NotSupportedException("部署的转换器缺少 STEP 支持，请同时更新插件和转换器。接口日志：" + diagnostics.LogPath);
                        if (requireAssembly) throw new NotSupportedException("部署的转换器缺少 3DXML 网格支持，请同时更新插件和转换器。接口日志：" + diagnostics.LogPath);
                        diagnostics.Write("Using legacy three-argument conversion.");
                        Warnings.Add("部署的转换器未确认支持当前接口，已使用原有三参数转换；请同时更新 TxTools.dll 与 CatiaPartTree/TxTools.CatiaConverter.exe。接口日志：" + diagnostics.LogPath);
                        return false;
                    }
                    finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
                }
            }
        }
        private static void ConvertCojt(string worker, Package package, bool optimize, CancellationToken cancel, IProgress<string> progress)
        {
            if (Directory.Exists(Path.GetDirectoryName(package.Directory)))
            {
                CatiaLibraryDestination.CreatePackage(package.Directory);
                ConvertPart(worker, package, optimize, cancel, progress);
                return;
            }

            string staging = Path.Combine(Path.GetTempPath(), "TxTools-COJT-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string destination = package.Jt;
            try
            {
                package.Jt = Path.Combine(staging, Path.GetFileName(destination));
                ConvertPart(worker, package, optimize, cancel, progress);
                cancel.ThrowIfCancellationRequested();
                CatiaLibraryDestination.CreatePackage(package.Directory);
                File.Move(package.Jt, destination);
            }
            finally
            {
                package.Jt = destination;
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void ConvertPart(string worker, Package package, bool optimize, CancellationToken cancel, IProgress<string> progress)
        {
            cancel.ThrowIfCancellationRequested();
            using (var diagnostics = new CatiaConversionDiagnostics(package.Source, message => progress.Report(message)))
            {
                package.Log = diagnostics.LogPath;
                string staging = null, manifest = null, input = package.Source;
                try
                {
                    string digest = package.Hash;
                    if (package.Member != null)
                    {
                        staging = Path.Combine(Path.GetTempPath(), "TxTools-CGR-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(staging);
                        input = Path.Combine(staging, Path.GetFileNameWithoutExtension(package.Jt) +
                            (package.XmlRepresentation ? ".3drep" : ".cgr"));
                        diagnostics.Write("3DXML member=" + package.Member);
                        diagnostics.Begin("读取 3DXML 内嵌几何 · " + package.Member);
                        ThreeDXmlSource.ExtractGeometry(package.Source, package.Member, input, cancel);
                        digest = CatiaImportSource.Hash(input);
                        if (package.XmlRepresentation)
                        {
                            // The eBOP instance supplies the placement. Convert the definition in its own coordinates.
                            manifest = Path.Combine(staging, "representation.bin");
                            using (var writer = new BinaryWriter(File.Create(manifest)))
                            {
                                writer.Write(1);
                                writer.Write(input);
                                writer.Write(Path.GetFileNameWithoutExtension(package.Jt));
                                foreach (double component in Frame12.Identity.Components) writer.Write(component);
                            }
                        }
                    }
                    RunConverter(worker, package, input, digest, manifest, optimize, cancel, diagnostics);
                }
                finally
                {
                    if (staging != null && Directory.Exists(staging))
                    {
                        if (File.Exists(input) && !string.Equals(input, package.Source, StringComparison.OrdinalIgnoreCase)) File.Delete(input);
                        if (manifest != null) File.Delete(manifest);
                        Directory.Delete(staging);
                    }
                }
            }
        }

        private static void RunConverter(string worker, Package package, string input, string digest, string manifest,
            bool optimize, CancellationToken cancel, CatiaConversionDiagnostics diagnostics)
        {
            cancel.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo(worker, Quote(input) + " " + Quote(package.Jt) + " " + Quote(digest) +
                (manifest != null ? " " + Quote("assembly=" + manifest) : package.Member == null ? "" : " cgr") +
                (package.StepMesh == null ? "" : " " + Quote("stepmesh=" + package.StepMesh)) +
                (optimize ? " quality" : ""))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8 };
            diagnostics.Begin("启动 JT 转换程序");
            using (var process = Process.Start(start))
            {
                diagnostics.Write("PROCESS pid=" + process.Id + " executable=" + worker + " output=" + package.Jt);
                diagnostics.Begin(optimize ? "生成 JT（自动减面与写入）" : "生成 JT（不减面）");
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
        internal void CreateTree(Action<string> log = null)
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
                {
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
                    // CreateCompoundPart 只写托管 Collection，对象树按原生关系建层会平铺。
                    // 建完容器后先 Paste 一次，独立建树即可得到正确的树视图。
                    if (!_containersPasted)
                        _containersPasted = PasteHierarchy(PasteRoots(), undo, log, "创建目标树");
                }
                TreeBuilt = true; Stage = CatiaImportStage.TreeCreated;
                RefreshView();
            }
            catch { Stage = attempted ? CatiaImportStage.Failed : previousStage; throw; }
        }

        // PS 的 Create/AddObject 只写托管 Collection，对象树 (Object Viewer) 读取的是
        // 原生对象库中的树关系，因此新层级会平铺在 Parts/Resources 根下。Paste 是当前
        // SDK 中唯一会写入原生关系并复制整棵子树的托管入口：原有 Create 流程保持不变，
        // 完成后把整棵子树 Paste 到目标父级，删除原树，再把映射改到副本上。
        private List<StructureChange> PasteRoots()
        {
            var root = Items[0];
            if (root.Result != null && !ReferenceEquals(root.Result, _parent))
                return new List<StructureChange> { root };
            return Items.Where(c => c.Parent == root && c.Result != null && !ReferenceEquals(c.Result, _parent)).ToList();
        }

        private bool PasteHierarchy(List<StructureChange> roots, SceneUndoScope undo, Action<string> log, string stage)
        {
            if (roots.Count == 0) return false;
            var target = _parent as ITxObjectCollection;
            if (target == null) throw new InvalidOperationException("目标父容器不支持 Paste，无法建立原生对象树关系。");
            undo.EnsureDocument();
            var oldRoots = roots.Select(c => c.Result).ToList();
            TxObjectList copies;
            try { copies = PsCompoundHelper.PasteSubtree(target, oldRoots); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                string message = "[" + stage + "] 原生对象树重建失败，对象树可能仍平铺：" + error.GetBaseException().Message;
                Warnings.Add(message); log?.Invoke(message);
                return false;
            }
            var byId = Items.Where(c => c.Result != null)
                .GroupBy(c => StructureEngine.Id(c.Result))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var mapping = new List<Tuple<ITxObject, ITxObject>>();
            try
            {
                for (int i = 0; i < oldRoots.Count; i++) MapSubtree(oldRoots[i], copies[i], mapping);
            }
            catch
            {
                // Nothing has been switched yet: drop the copies so the original
                // hierarchy remains the only one in the scene.
                foreach (var copy in copies) DeleteQuietly(copy, stage, log);
                throw;
            }
            foreach (var pair in mapping)
            {
                StructureChange change;
                if (!byId.TryGetValue(StructureEngine.Id(pair.Item1), out change)) continue;
                change.Result = pair.Item2;
                if (!change.Container) change.Node.BoundPart = pair.Item2;
            }
            for (int i = 0; i < oldRoots.Count; i++)
            {
                undo.EnsureDocument();
                try { oldRoots[i].Delete(); }
                catch (Exception error)
                {
                    throw new IOException("[" + stage + "] 原生对象树重建后无法删除原对象“" + roots[i].Name + "”："
                        + error.GetBaseException().Message, error);
                }
                if (!PsCompoundHelper.TryRename(copies[i], roots[i].Name))
                    throw new IOException("[" + stage + "] 原生对象树重建后无法恢复名称：" + roots[i].Name);
            }
            log?.Invoke("[" + stage + "] 已用 Paste 重建原生对象树：" + roots.Count + " 棵子树");
            return true;
        }

        // 容器已在建树阶段建立原生关系时，只把新实例按容器分组 Paste 一遍：
        // 副本获得原生树关系，容器本身和已建层级都不重建。
        private bool PasteInstances(List<StructureChange> instances, SceneUndoScope undo, Action<string> log, string stage)
        {
            var byId = Items.Where(c => c.Result != null)
                .GroupBy(c => StructureEngine.Id(c.Result))
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var mapping = new List<Tuple<ITxObject, ITxObject>>();
            var replaced = new List<Tuple<StructureChange, ITxObject>>();
            var oldRoots = new List<ITxObject>();
            foreach (var group in instances.Where(c => c.Error == null).GroupBy(c => c.Parent))
            {
                undo.EnsureDocument();
                var target = group.Key.Result as ITxObjectCollection;
                if (target == null)
                {
                    string message = "[" + stage + "] 目标容器不支持 Paste，实例对象树可能仍平铺：" + group.Key.Name;
                    Warnings.Add(message); log?.Invoke(message);
                    continue;
                }
                var items = group.ToList();
                var olds = items.Select(c => c.Result).ToList();
                TxObjectList copies;
                try { copies = PsCompoundHelper.PasteSubtree(target, olds); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    string message = "[" + stage + "] 实例原生树重建失败，对象树可能仍平铺：" + error.GetBaseException().Message;
                    Warnings.Add(message); log?.Invoke(message);
                    continue;
                }
                var local = new List<Tuple<ITxObject, ITxObject>>();
                try
                {
                    for (int i = 0; i < olds.Count; i++) MapSubtree(olds[i], copies[i], local);
                }
                catch (Exception error)
                {
                    foreach (var copy in copies) DeleteQuietly(copy, stage, log);
                    string message = "[" + stage + "] 实例原生树重建失败，对象树可能仍平铺：" + error.GetBaseException().Message;
                    Warnings.Add(message); log?.Invoke(message);
                    continue;
                }
                mapping.AddRange(local); oldRoots.AddRange(olds);
                for (int i = 0; i < items.Count; i++) replaced.Add(Tuple.Create(items[i], copies[i]));
            }
            if (replaced.Count == 0) return false;
            foreach (var pair in mapping)
            {
                StructureChange change;
                if (!byId.TryGetValue(StructureEngine.Id(pair.Item1), out change)) continue;
                change.Result = pair.Item2;
                if (!change.Container) change.Node.BoundPart = pair.Item2;
            }
            foreach (var oldRoot in oldRoots)
            {
                undo.EnsureDocument();
                try { oldRoot.Delete(); }
                catch (Exception error)
                {
                    throw new IOException("[" + stage + "] 实例原生树重建后无法删除原实例：" + oldRoot.Name + "；"
                        + error.GetBaseException().Message, error);
                }
            }
            foreach (var pair in replaced)
                if (!PsCompoundHelper.TryRename(pair.Item2, pair.Item1.Name))
                    throw new IOException("[" + stage + "] 实例原生树重建后无法恢复名称：" + pair.Item1.Name);
            log?.Invoke("[" + stage + "] 已用 Paste 重建实例原生树：" + replaced.Count + " 个实例");
            return true;
        }

        private static void MapSubtree(ITxObject oldObject, ITxObject newObject, List<Tuple<ITxObject, ITxObject>> mapping)
        {
            mapping.Add(Tuple.Create(oldObject, newObject));
            var oldChildren = StructureEngine.Children(oldObject);
            var newChildren = StructureEngine.Children(newObject);
            if (oldChildren.Count != newChildren.Count)
                throw new IOException("Paste 后的子节点数量与预期不一致：" + oldObject.Name
                    + "（" + oldChildren.Count + " → " + newChildren.Count + "）");
            var matched = PsCompoundHelper.MatchChildren(oldChildren, newChildren);
            if (matched == null) throw new IOException("Paste 后的子节点无法对应：" + oldObject.Name);
            for (int i = 0; i < oldChildren.Count; i++) MapSubtree(oldChildren[i], matched[i], mapping);
        }

        private static void DeleteQuietly(ITxObject value, string stage, Action<string> log)
        {
            try { if (value != null) value.Delete(); }
            catch (Exception error) { log?.Invoke("[" + stage + "] 删除多建的副本失败：" + value.Name + "；" + error.Message); }
        }
        private static void VerifyPlacement(StructureChange item, ITxLocatableObject located, double[] expected, HashSet<string> childIds)
        {
            var issues = new List<string>();
            if (!string.Equals(item.Result.Name, item.Name, StringComparison.Ordinal))
                issues.Add($"名称应为“{item.Name}”，PS 中为“{item.Result.Name}”");
            if (!childIds.Contains(StructureEngine.Id(item.Result)))
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
                    issues.Add("位置偏差 " + distance.ToString("G6", CultureInfo.InvariantCulture) + " mm（容差 0.001 mm；期望 "
                        + Vector(expected, 3) + "，实际 " + Vector(actual, 3) + "）");
                if (basisError > 1e-6)
                {
                    int index = Enumerable.Range(0, 12).Where(i => i % 4 != 3)
                        .OrderByDescending(i => Math.Abs(actual[i] - expected[i])).First();
                    issues.Add("姿态矩阵最大分量偏差 " + basisError.ToString("G6", CultureInfo.InvariantCulture)
                        + "（容差 1e-6；矩阵第 " + (index / 4 + 1) + " 行第 " + (index % 4 + 1) + " 列 期望 "
                        + expected[index].ToString("G9", CultureInfo.InvariantCulture) + "，实际 "
                        + actual[index].ToString("G9", CultureInfo.InvariantCulture) + "）");
                    try
                    {
                        var parent = item.Parent == null ? null : item.Parent.Result as ITxLocatableObject;
                        if (parent != null)
                            issues.Add("目标容器位姿 " + Vector(TxMath.WorldValues(parent.AbsoluteLocation), 3)
                                + "；实际姿态相对期望的整体差异：" + RotationDelta(expected, actual));
                    }
                    catch (Exception) { }
                }
            }
            if (issues.Count != 0) throw new IOException(string.Join("，", issues));
        }

        private static string Vector(double[] values, int offset)
        {
            return "(" + values[offset].ToString("G9", CultureInfo.InvariantCulture) + ", "
                + values[offset + 4].ToString("G9", CultureInfo.InvariantCulture) + ", "
                + values[offset + 8].ToString("G9", CultureInfo.InvariantCulture) + ")";
        }

        // Classifies the read-back basis against the expected one so a systematic
        // SDK convention problem is visible in the warning itself.
        private static string RotationDelta(double[] expected, double[] actual)
        {
            double transpose = 0, identity = 0;
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                {
                    transpose = Math.Max(transpose, Math.Abs(actual[row * 4 + column] - expected[column * 4 + row]));
                    identity = Math.Max(identity, Math.Abs(actual[row * 4 + column] - (row == column ? 1d : 0d)));
                }
            if (transpose <= 1e-6) return "等于期望矩阵的转置（写入被转置）";
            if (identity <= 1e-6) return "等于单位矩阵（旋转未写入）";
            return "不等于转置，也不等于单位矩阵";
        }

        private void RecordImportIssue(StructureChange item, Exception error, Action<string> log)
        {
            bool partial = item.Result != null && item.Result.IsValid();
            if (!partial) item.Result = null;
            item.Error = partial ? "已插入，需核对" : "未插入";
            if (partial) NeedsReview++;
            else SkippedInstances++;
            string message = "[" + item.Error + "] " + item.Name + "：" + error.GetBaseException().Message;
            Warnings.Add(message);
            log?.Invoke(message);
        }

        private static void MoveAfter(ITxOrderedObjectCollection ordered, ITxObject item, ITxObject previous)
        {
            // Some PS versions accept ordering only through MoveChildAfter when the
            // child already belongs to the target collection.
            try { ordered.AddObjectAfter(item, previous); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { ordered.MoveChildAfter(item, previous); }
        }

        private void MoveInstances(StructureChange parent, List<StructureChange> items, SceneUndoScope undo, Action<string> log)
        {
            undo.EnsureDocument();
            Current = parent.Name;
            var collection = (ITxObjectCollection)parent.Result;
            var childIds = new HashSet<string>(StructureEngine.Children(parent.Result).Select(StructureEngine.Id));
            var pending = items.Where(item => !childIds.Contains(StructureEngine.Id(item.Result))).ToList();
            if (pending.Count > 1)
            {
                var objects = new TxObjectList();
                foreach (var item in pending) objects.Add(item.Result);
                try { collection.AddObjects(objects); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    log?.Invoke("[批量归位] " + parent.Name + "：" + error.GetBaseException().Message + "；将核对并补放未移动的实例。");
                }
                undo.EnsureDocument();
                childIds = new HashSet<string>(StructureEngine.Children(parent.Result).Select(StructureEngine.Id));
            }
            // A collection can reject AddObjects or move only part of the list.
            foreach (var item in pending.Where(item => !childIds.Contains(StructureEngine.Id(item.Result))))
            {
                undo.EnsureDocument(); Current = item.Name;
                try { collection.AddObject(item.Result); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { RecordImportIssue(item, error, log); }
            }
            undo.EnsureDocument();
            var missing = pending.Any(item => item.Error == null && !childIds.Contains(StructureEngine.Id(item.Result)));
            if (missing) childIds = new HashSet<string>(StructureEngine.Children(parent.Result).Select(StructureEngine.Id));
            foreach (var item in items.Where(item => item.Error == null && !childIds.Contains(StructureEngine.Id(item.Result))))
                RecordImportIssue(item, new IOException($"未找到目标父级“{parent.Result.Name}”下的该实例"), log);
        }

        private void ApplyInstancePositions(IEnumerable<StructureChange> items, HashSet<string> childIds, SceneUndoScope undo, Action<string> log)
        {
            foreach (var item in items.Where(item => item.Error == null))
            {
                undo.EnsureDocument(); Current = item.Name;
                try
                {
                    var located = item.Result as ITxLocatableObject;
                    if (located == null) throw new IOException("插入对象无法设置实例位置");
                    var c = _source.Parts[item.Node.Key].Frame;
                    var expected = new[] { c[0], c[3], c[6], c[9], c[1], c[4], c[7], c[10], c[2], c[5], c[8], c[11] };
                    located.AbsoluteLocation = TxMath.FromWorldValues(expected);
                    VerifyPlacement(item, located, expected, childIds);
                    item.Node.BoundPart = item.Result; Imported++;
                    log?.Invoke("[已归位] " + item.Name);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { RecordImportIssue(item, error, log); }
            }
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
                foreach (var paths in _packages.Where(p => p.Error == null).GroupBy(p => LibraryInsertion.NeedsCompatibility(p.Directory)))
                {
                    string issue = LibraryInsertion.SupportIssue(paths.First().Directory);
                    if (issue != null) throw new InvalidOperationException(issue);
                }
                using (var undo = SceneUndoScope.Begin("CATIA 导入 · 导入归位"))
                {
                    var instances = Items.Where(c => !c.Container && c.Error == null).ToList();
                    var paths = instances.Select(item => _bySource[_source.Parts[item.Node.Key].AssetKey].Directory).ToArray();
                    var loaded = ComponentIO.LoadCojts(paths, _library, index =>
                    {
                        undo.EnsureDocument(); Current = instances[index].Name;
                        SceneWriteAttempted = attempted = true;
                    });
                    for (int i = 0; i < instances.Count; i++)
                    {
                        var item = instances[i];
                        undo.EnsureDocument(); Current = item.Name;
                        try
                        {
                            ComponentIO.VerifyLoadedCojt(loaded[i], paths[i], _library);
                            item.Result = loaded[i].Instance;
                            undo.EnsureDocument(); item.Result.Name = item.Name;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            item.Result = loaded[i].Instance;
                            RecordImportIssue(item, error, log);
                        }
                    }
                    foreach (var group in instances.Where(item => item.Error == null).GroupBy(item => item.Parent))
                        MoveInstances(group.Key, group.ToList(), undo, log);
                    // 实例已归入容器，但 AddObject 同样不写原生树关系。容器若已在建树
                    // 阶段 Paste 过，只把实例 Paste 进各自容器，保持容器身份不变；
                    // 建树阶段未重建时再对整棵子树做一次。
                    bool native = _containersPasted
                        ? PasteInstances(instances, undo, log, "导入归位")
                        : PasteHierarchy(PasteRoots(), undo, log, "导入归位");
                    if (native && instances.Count > 0)
                    {
                        for (int i = 0; i < instances.Count; i++)
                        {
                            var item = instances[i];
                            if (item.Error != null) continue;
                            undo.EnsureDocument(); Current = item.Name;
                            try { ComponentIO.VerifyPastedCojt(item.Result, paths[i], _library); }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception error) { RecordImportIssue(item, error, log); }
                        }
                    }
                    var childrenByParent = new Dictionary<StructureChange, HashSet<string>>();
                    foreach (var group in Items.Where(c => c.Parent != null && (c.Container ? (c == Items[0] || live.Contains(c)) : c.Error == null)).GroupBy(c => c.Parent))
                    {
                        var expected = group.Select(c => StructureEngine.Id(c.Result)).ToArray();
                        var ids = new HashSet<string>(expected);
                        var current = StructureEngine.Children(group.Key.Result).Select(StructureEngine.Id).Where(ids.Contains).ToArray();
                        if (!current.SequenceEqual(expected))
                        {
                            // PS inserts new children at the top, so the scene order may
                            // differ from the preview. Ordering is only exposed by the
                            // SDK ordered collection; TxCompoundPart has no public
                            // AddObjectAfter, so a dynamic call fails at runtime.
                            var ordered = group.Key.Result as ITxOrderedObjectCollection;
                            if (ordered == null)
                            {
                                string message = "[顺序] " + group.Key.Name + "：目标容器不支持子节点排序，请手工核对顺序。";
                                Warnings.Add(message); log?.Invoke(message);
                            }
                            else
                            {
                                ITxObject previous = null;
                                try
                                {
                                    foreach (var item in group)
                                    {
                                        undo.EnsureDocument();
                                        if (previous != null) MoveAfter(ordered, item.Result, previous);
                                        previous = item.Result;
                                    }
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception error)
                                {
                                    string message = "[顺序] " + group.Key.Name + "：" + error.GetBaseException().Message + "，请手工核对顺序。";
                                    Warnings.Add(message); log?.Invoke(message);
                                }
                                if (!StructureEngine.Children(group.Key.Result).Select(StructureEngine.Id).Where(ids.Contains).SequenceEqual(expected))
                                {
                                    string message = "[顺序] " + group.Key.Name + "：子节点顺序与预览不一致，请手工核对。";
                                    Warnings.Add(message); log?.Invoke(message);
                                }
                            }
                        }
                        childrenByParent.Add(group.Key, ids);
                    }
                    // Assign absolute poses after all SDK hierarchy edits. The
                    // preview may flatten or move containers within the PS tree.
                    foreach (var group in instances.Where(item => item.Error == null).GroupBy(item => item.Parent))
                        ApplyInstancePositions(group, childrenByParent[group.Key], undo, log);
                    if (Imported + NeedsReview == 0 && Items.Any(c => !c.Container))
                        throw new IOException("未插入任何实例，请查看日志。");
                }
                Stage = CatiaImportStage.Completed;
                RefreshView();
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
