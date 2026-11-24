using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Utilities;
using Tecnomatix.Planning;
using TxTools.CatiaPartTree.Core;
using TxTools.CatiaPartTree.Ps;
using TxTools.Common;
using TxTools.CrossEnvIO;

namespace TxTools.ResourceImporter
{
    internal sealed class ResourceImportItem
    {
        internal ResourceImportItem(string file) { File = file; }
        internal readonly string File;
        internal ResourcePrototype Prototype;
        internal string Folder, Package, Error;
        internal string[] Warnings = Array.Empty<string>();
        internal bool SceneWriteAttempted, Imported;
        internal string Name => Path.GetFileName(File);
    }

    internal sealed class ResourceImportSession
    {
        private sealed class BatchEntry
        {
            internal BatchEntry(ResourceImportItem item, string destination)
            {
                Item = item;
                Destination = destination;
            }

            internal readonly ResourceImportItem Item;
            internal readonly string Destination;
            internal string StagedPackage, PrototypeId;
        }

        internal static int ConversionParallelism => Environment.ProcessorCount;
        private readonly TxDocument _document;
        private readonly long _generation;
        internal readonly string Library;
        internal readonly ResourcePrototype[] Prototypes;

        internal ResourceImportSession()
        {
            _document = TxApplication.ActiveDocument;
            if (_document?.PhysicalRoot == null || !(_document.PlatformGlobalServicesProvider is ITxOfflineGlobalServicesProvider))
                throw new InvalidOperationException("请打开具有物理根树的 Process Simulate 离线工程，再启动资源导入。");
            _generation = SceneUndoScope.DocumentGeneration;
            Library = Path.GetFullPath(TxApplication.SystemRootDirectory);
            if (!Directory.Exists(Library)) throw new DirectoryNotFoundException("当前工程的系统库不存在：" + Library);
            Prototypes = ResourcePrototype.Read(_document);
        }

        internal void EnsureDocument()
        {
            if (_generation != SceneUndoScope.DocumentGeneration || !Equals(_document, TxApplication.ActiveDocument) ||
                !SamePath(Library, Path.GetFullPath(TxApplication.SystemRootDirectory)))
                throw new InvalidOperationException("工程或系统库已切换。请在目标工程重新打开资源导入。");
        }

        internal static bool SamePath(string first, string second) =>
            string.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        internal void CheckFolder(string folder)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("COJT 目标目录不存在：" + folder);
            LibraryInsertion.CheckDestination(Path.Combine(folder, "model.cojt"), Library, true);
            var current = new DirectoryInfo(folder);
            while (!SamePath(current.FullName, Library))
            {
                if (current.Name.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("请指定系统库中的目录。");
                current = current.Parent;
            }
        }

        internal async Task ImportBatchAsync(IReadOnlyList<ResourceImportItem> items, bool skipHidden, CancellationToken cancel,
            IProgress<int> conversionProgress, Action<string> sceneProgress)
        {
            foreach (var item in items)
            {
                item.Error = null;
                item.Warnings = Array.Empty<string>();
            }
            EnsureDocument();
            var entries = PlanBatch(items, cancel);
            int completed = items.Count - entries.Count;
            var progressGate = new object();
            conversionProgress.Report(completed);
            try
            {
                await CatiaImportSession.RunConversionBatchAsync(entries, ConversionParallelism, cancel, entry =>
                {
                    try
                    {
                        var prepared = ResourceConversion.PreparePackage(entry.Item.File, entry.Destination, cancel, skipHidden);
                        entry.StagedPackage = prepared.Package;
                        entry.Item.Warnings = prepared.Warnings;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { entry.Item.Error = error.GetBaseException().Message; }
                    finally { lock (progressGate) conversionProgress.Report(++completed); }
                });
                cancel.ThrowIfCancellationRequested();
                EnsureDocument();
                var converted = entries.Where(entry => entry.StagedPackage != null).ToArray();
                if (converted.Length != 0) ImportConverted(converted, sceneProgress);
            }
            catch (Exception error)
            {
                foreach (var entry in entries)
                {
                    if (entry.Item.SceneWriteAttempted && !entry.Item.Imported && entry.Item.Error == null)
                        entry.Item.Error = error.Message;
                }
                throw;
            }
            finally
            {
                foreach (var entry in entries) CleanStage(entry);
            }
        }

        private List<BatchEntry> PlanBatch(IReadOnlyList<ResourceImportItem> items, CancellationToken cancel)
        {
            var entries = new List<BatchEntry>(items.Count);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var supportIssues = new Dictionary<bool, string>();
            foreach (var item in items)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (item.SceneWriteAttempted || item.Prototype == null || item.Folder == null)
                        throw new InvalidOperationException("请为每个待导入文件指定原型和 COJT 目录。");
                    CheckFolder(item.Folder);
                    string path = CatiaLibraryDestination.Package(item.Folder, item.File, reserved);
                    bool compatibility = LibraryInsertion.NeedsCompatibility(path);
                    if (!supportIssues.TryGetValue(compatibility, out string issue))
                    {
                        issue = LibraryInsertion.SupportIssue(path);
                        supportIssues.Add(compatibility, issue);
                    }
                    if (issue != null) throw new IOException(issue);
                    entries.Add(new BatchEntry(item, path));
                }
                catch (Exception error) { item.Error = error.GetBaseException().Message; }
            }
            return entries;
        }

        private void ImportConverted(BatchEntry[] converted, Action<string> progress)
        {
            progress("准备 COJT · " + converted.Length + " 个文件");
            var ready = new List<BatchEntry>(converted.Length);
            foreach (var entry in converted)
            {
                EnsureDocument();
                try
                {
                    CheckFolder(entry.Item.Folder);
                    entry.PrototypeId = SavePrototype(entry.Item, entry.StagedPackage);
                    Directory.Move(entry.StagedPackage, entry.Destination);
                    entry.Item.Package = entry.Destination;
                    ready.Add(entry);
                }
                catch (Exception error) { entry.Item.Error = error.GetBaseException().Message; }
            }
            if (ready.Count == 0) return;

            progress("连续插入根树 · " + ready.Count + " 个资源");
            using (var undo = SceneUndoScope.Begin(_document, "导入资源 · " + ready.Count + " 个"))
            {
                var paths = ready.Select(entry => entry.Destination).ToArray();
                var loaded = ComponentIO.LoadCojts(paths, Library, index =>
                {
                    undo.EnsureDocument();
                    ready[index].Item.SceneWriteAttempted = true;
                }, allowRoot: true);

                progress("统一校验 · " + ready.Count + " 个资源");
                for (int i = 0; i < ready.Count; i++)
                {
                    undo.EnsureDocument();
                    var entry = ready[i];
                    try
                    {
                        ComponentIO.VerifyLoadedCojt(loaded[i], entry.Destination, Library);
                        VerifyPrototype(entry, loaded[i].Instance);
                        entry.Item.Imported = true;
                    }
                    catch (Exception error) { entry.Item.Error = error.GetBaseException().Message; }
                }
            }
        }

        private static void CleanStage(BatchEntry entry)
        {
            // A package may already be referenced once the SDK call has begun.
            if (entry.Item.Package == entry.Destination && !entry.Item.SceneWriteAttempted)
            {
                try
                {
                    Directory.Move(entry.Destination, entry.StagedPackage);
                    entry.Item.Package = null;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                {
                    entry.Item.Error = "尚未插入，未能收回 COJT：" + error.Message;
                }
            }
            ResourceConversion.DeleteWorkDirectory(Path.GetDirectoryName(entry.StagedPackage));
        }

        private static string SavePrototype(ResourceImportItem item, string package)
        {
            var manager = new TxTunePmDocumentManager();
            var data = DevicePrototypes.CreateTargetData(new TransferPackage
            {
                Name = item.Name,
                Prototype = new DevicePrototypeData { Type = item.Prototype.Type, Family = ResourcePrototype.Family }
            });
            manager.SavePrototypeDataToDocument(package, data);
            return data.ExternalId;
        }

        private static void VerifyPrototype(BatchEntry entry, ITxObject instance)
        {
            var item = entry.Item;
            instance.Name = Path.GetFileNameWithoutExtension(item.File);
            var planning = (instance as ITxProcessModelObject)?.PlanningRepresentation;
            var prototype = (planning as ITxPlanningInstance)?.Prototype ?? planning as ITxPlanningPrototype;
            if (prototype != null && !string.Equals(prototype.PlanningType, item.Prototype.Type, StringComparison.OrdinalIgnoreCase))
                throw new IOException("场景原型为 " + prototype.PlanningType + "，请选择该实例核对原型。");

            var persisted = new TxTunePmDocumentManager().LoadPrototypeDataFromDocument(entry.Destination);
            if (persisted == null || persisted.ExternalId != entry.PrototypeId ||
                persisted.Type != item.Prototype.Type || persisted.Family != ResourcePrototype.Family)
                throw new IOException("COJT 原型回读校验失败：" + item.Prototype.Type);
        }
    }
}
