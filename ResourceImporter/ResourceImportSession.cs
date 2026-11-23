using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Utilities;
using Tecnomatix.Planning;
using TxTools.CatiaPartTree.Core;
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

        internal async Task ImportAsync(ResourceImportItem item, CancellationToken cancel, Action<string> progress)
        {
            EnsureDocument();
            if (item.SceneWriteAttempted || item.Prototype == null || item.Folder == null)
                throw new InvalidOperationException("请为每个待导入文件指定原型和 COJT 目录。");
            CheckFolder(item.Folder);
            string path = CatiaLibraryDestination.Package(item.Folder, item.File, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string issue = LibraryInsertion.SupportIssue(path);
            if (issue != null) throw new IOException(issue);
            string stage = null;
            try
            {
                progress("准备 JT · " + item.Name);
                var prepared = await Task.Run(() => ResourceConversion.PreparePackage(item.File, path, cancel), cancel);
                string package = prepared.Package;
                stage = Path.GetDirectoryName(package);
                item.Warnings = prepared.Warnings;
                cancel.ThrowIfCancellationRequested();
                EnsureDocument();
                CheckFolder(item.Folder);
                progress("写入原型并插入根树 · " + item.Name);
                SavePrototype(item, package);
                using (var undo = SceneUndoScope.Begin(_document, "导入资源 · " + item.Name))
                {
                    undo.EnsureDocument();
                    Directory.Move(package, path);
                    item.Package = path;
                    item.SceneWriteAttempted = true;
                    var result = ComponentIO.LoadCojts(new[] { path }, Library, _ => undo.EnsureDocument(), true)[0];
                    undo.EnsureDocument();
                    ComponentIO.VerifyLoadedCojt(result, path, Library);
                    result.Instance.Name = Path.GetFileNameWithoutExtension(item.File);
                    var planning = (result.Instance as ITxProcessModelObject)?.PlanningRepresentation;
                    var prototype = (planning as ITxPlanningInstance)?.Prototype ?? planning as ITxPlanningPrototype;
                    if (prototype != null && !string.Equals(prototype.PlanningType, item.Prototype.Type, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("场景原型为 " + prototype.PlanningType + "，请选择该实例核对原型。");
                }
                item.Imported = true;
            }
            finally
            {
                ResourceConversion.DeleteWorkDirectory(stage);
            }
        }

        private static void SavePrototype(ResourceImportItem item, string package)
        {
            var manager = new TxTunePmDocumentManager();
            var data = DevicePrototypes.CreateTargetData(new TransferPackage
            {
                Name = item.Name,
                Prototype = new DevicePrototypeData { Type = item.Prototype.Type, Family = ResourcePrototype.Family }
            });
            manager.SavePrototypeDataToDocument(package, data);
            var persisted = manager.LoadPrototypeDataFromDocument(package);
            if (persisted == null || persisted.ExternalId != data.ExternalId ||
                persisted.Type != data.Type || persisted.Family != data.Family)
                throw new IOException("COJT 原型回读校验失败：" + item.Prototype.Type);
        }
    }
}
