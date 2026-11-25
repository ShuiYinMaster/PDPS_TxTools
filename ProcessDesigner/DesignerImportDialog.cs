using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using Tecnomatix.Planning;
using TxTools.Common;
using TxTools.Common.TreeWorkflow;

namespace TxTools.ProcessDesigner
{
    internal sealed class DesignerImportDialog : TxForm
    {
        private readonly DesignerImportPackage _package;
        private readonly string _systemRoot;
        private readonly Label _status = WorkflowUi.Label("");
        private readonly Button _open, _check;
        private (int Session, int Project, int Library) _submittedTo;
        private readonly List<string> _importNotices = new List<string>();
        private bool _libraryConfirmed;

        internal DesignerImportDialog(DesignerImportPackage package, string systemRoot = null)
        {
            _package = package;
            _systemRoot = systemRoot;
            FormUiKit.InitStandardForm(this, "Process Designer · 导入" + TreeName, new Size(690, 380), new Size(560, 320));
            SemiModal = false;
            var buttons = WorkflowUi.Bar();
            _open = WorkflowUi.Button(buttons, package.Structure == null ? "导入到 " + LibraryName : "导入当前工程", () => Run(Import));
            _check = WorkflowUi.Button(buttons, "核对入库结果", () => Run(CheckImport));
            _check.Enabled = false;
            if (package.Warnings.Count != 0)
                WorkflowUi.Button(buttons, "转换提示", () => MessageBox.Show(this,
                    string.Join(Environment.NewLine, package.Warnings), Text, MessageBoxButtons.OK, MessageBoxIcon.Information));
            string counts = package.Structure == null ? package.Library.RootIds.Count + " 个焊点"
                : (package.Library?.RootIds.Count ?? 0) + " 个原型和 " + package.Structure.ObjectIds.Count + " 个结构节点";
            Controls.Add(WorkflowUi.Stack(
                WorkflowUi.Label("已准备 “" + package.Name + "”，包含 " + counts + "。" +
                    (package.MissingGeometry == 0 ? "" : "其中 " + package.MissingGeometry + " 个实例仅导入结构，可查看转换提示。")),
                WorkflowUi.Label(ImportDestination), buttons, _status));
        }

        private string TreeName => _package.Tree == DesignerTree.Part ? "零件树"
            : _package.Tree == DesignerTree.Resource ? "资源树" : "制造特征树";

        private string LibraryName => DesignerLibraryImport.LibraryName(_package.Tree);

        private string ImportDestination => _package.Tree == DesignerTree.Mfg
            ? "焊点将导入当前工程的 MfgLibrary。"
            : "原型导入 " + LibraryName + "，实例放在当前工程根节点下新建的" +
                (_package.Tree == DesignerTree.Part ? "复合零件中。" : "复合资源中。");

        private static ITxPlanningObject PlanningObject(ITxObject value) =>
            value as ITxPlanningObject ?? (value as ITxProcessModelObject)?.PlanningRepresentation;

        private void Import()
        {
            if ((_package.Library != null && !File.Exists(_package.Library.FilePath)) ||
                (_package.Structure != null && !File.Exists(_package.Structure.FilePath)))
                throw new FileNotFoundException("导入 XML 已丢失，请重新准备。");
            if (_systemRoot != null)
            {
                string currentRoot = TxApplication.SystemRootDirectory;
                if (string.IsNullOrWhiteSpace(currentRoot) || !string.Equals(
                    _systemRoot.TrimEnd('\\', '/'), Path.GetFullPath(currentRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("当前系统库与准备几何时使用的系统库不同，请重新准备导入。");
            }
            var document = TxApplication.ActiveDocument;
            var services = DesignerLibraryImport.Services(document);
            int session = document.eMSSessionId;
            var target = DesignerLibraryImport.Resolve(services, session, _package.Library == null ? (DesignerTree?)null : _package.Tree);
            if (_submittedTo.Project != 0 && _submittedTo != (session, target.ProjectId, target.LibraryId))
                throw new InvalidOperationException("当前工程与本批首次导入的工程不同。请返回原工程后重试。");
            _submittedTo = (session, target.ProjectId, target.LibraryId);
            _open.Enabled = false;
            _check.Enabled = false;
            UseWaitCursor = true;
            try
            {
                _importNotices.Clear();
                if (_package.Library != null && !_libraryConfirmed)
                    Submit(session, target.LibraryId, _package.Library, LibraryName);
                var notes = new List<string>();
                int? libraryCount = ReadLibraryCount(services, target.Library, notes);
                if (_package.Structure != null && _libraryConfirmed)
                    Submit(session, target.ProjectId, _package.Structure, "工程结构");
                ShowResult(services, target.Project, libraryCount, notes);
            }
            finally
            {
                UseWaitCursor = false;
                _check.Enabled = true;
            }
        }

        private void Submit(int session, int targetId, DesignerImportFile file, string destination)
        {
            try { DesignerLibraryImport.Import(session, targetId, file.FilePath); }
            catch (Exception error) { _importNotices.Add(destination + " 导入接口返回 " + DesignerLibraryImport.ImportErrorMessage(error)); }
        }

        private void CheckImport()
        {
            var document = TxApplication.ActiveDocument;
            var services = DesignerLibraryImport.Services(document);
            int session = document.eMSSessionId;
            var target = DesignerLibraryImport.Resolve(services, session, _package.Library == null ? (DesignerTree?)null : _package.Tree);
            if (_submittedTo != (session, target.ProjectId, target.LibraryId))
                throw new InvalidOperationException("请在原导入工程中核对结果。");
            var notes = new List<string>();
            int? libraryCount = ReadLibraryCount(services, target.Library, notes);
            ShowResult(services, target.Project, libraryCount, notes);
        }

        private int? ReadLibraryCount(ITxEmsGlobalServicesProvider services, ITxPlanningObject library, List<string> notes)
        {
            int? count = _package.Library == null ? 0
                : CountRoots(services, library, _package.Library, LibraryName, notes);
            _libraryConfirmed = count == (_package.Library?.RootIds.Count ?? 0);
            return count;
        }

        private static int? CountRoots(ITxEmsGlobalServicesProvider services, ITxPlanningObject target,
            DesignerImportFile file, string destination, List<string> notes)
        {
            try
            {
                RefreshField(services, new[] { target }, "children");
                var children = new HashSet<string>(DesignerLibraryImport.Children(target)
                    .Select(item => item.ProcessModelId?.ExternalId), StringComparer.OrdinalIgnoreCase);
                return file.RootIds.Count(children.Contains);
            }
            catch (Exception error)
            {
                notes.Add(destination + " 下级核对暂不可用。 " + error.GetBaseException().Message);
                return null;
            }
        }

        private static void RefreshField(ITxEmsGlobalServicesProvider services, ITxPlanningObject[] objects, string fieldName)
        {
            var roots = new TxObjectList();
            foreach (var item in objects) roots.Add(item);
            var fields = new TxEmsImportFieldsData { Roots = roots };
            fields.addFieldsToCollect(new ArrayList(objects.Select(item => item.PlanningType).Distinct()
                .Select(type => new TxClassFieldsCollectionData { ClassName = type, FieldNames = new ArrayList { fieldName } }).ToArray()));
            services.ImportOverwriteFields(fields);
        }

        private bool ReadStructure(ITxEmsGlobalServicesProvider services, ITxPlanningObject project, List<string> notes)
        {
            var structure = _package.Structure;
            int? roots = CountRoots(services, project, structure, "工程根节点", notes);
            if (roots.HasValue)
                notes.Add("工程根节点下查到 " + roots + "/" + structure.RootIds.Count + " 个复合对象。");
            if (roots == 0)
            {
                if (!_libraryConfirmed) notes.Add("库中原型核实后才能导入引用这些原型的结构。");
                return false;
            }
            try
            {
                var ids = new ArrayList(structure.ObjectIds.Select(id => new TxProcessModelId(id)).ToArray());
                var expected = new HashSet<string>(structure.ObjectIds, StringComparer.OrdinalIgnoreCase);
                var actual = services.GetObjectsByProcessModelIds(ids).Cast<ITxObject>().Select(PlanningObject)
                    .Where(item => item != null && expected.Contains(item.ProcessModelId?.ExternalId))
                    .ToLookup(item => item.ProcessModelId.ExternalId, StringComparer.OrdinalIgnoreCase);
                var containers = actual.Where(group => !_package.InstancePrototypes.ContainsKey(group.Key))
                    .Select(group => group.First()).ToArray();
                if (containers.Length != 0) RefreshField(services, containers, "children");

                var located = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<string>(structure.RootIds);
                while (pending.Count != 0)
                {
                    string id = pending.Pop();
                    var node = actual[id].FirstOrDefault();
                    if (node == null || !located.Add(id) || _package.InstancePrototypes.ContainsKey(id)) continue;
                    foreach (var child in DesignerLibraryImport.Children(node))
                        pending.Push(child.ProcessModelId?.ExternalId);
                }
                int nodes = located.Count;
                notes.Add("复合对象中查到 " + nodes + "/" + structure.ObjectIds.Count + " 个结构节点（含根节点）。");
                var instances = located.Where(_package.InstancePrototypes.ContainsKey).Select(id => actual[id].First()).ToArray();
                if (_package.InstancePrototypes.Count == 0) return roots == structure.RootIds.Count && nodes == structure.ObjectIds.Count;

                if (instances.Length != 0) RefreshField(services, instances, "prototype");
                int linked = instances.Where(item => string.Equals(
                        PlanningObject(item.GetField("prototype") as ITxObject)?.ProcessModelId?.ExternalId,
                        _package.InstancePrototypes[item.ProcessModelId.ExternalId], StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ProcessModelId.ExternalId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                notes.Add("已核实 " + linked + "/" + _package.InstancePrototypes.Count + " 个实例的原型关联。");
                return roots == structure.RootIds.Count && nodes == structure.ObjectIds.Count && linked == _package.InstancePrototypes.Count;
            }
            catch (Exception error)
            {
                notes.Add("结构核对暂不可用。 " + error.GetBaseException().Message);
                return false;
            }
        }

        private void ShowResult(ITxEmsGlobalServicesProvider services, ITxPlanningObject project, int? libraryCount, List<string> notes)
        {
            if (_package.Library != null && libraryCount.HasValue)
                notes.Add(LibraryName + " 直接下级查到 " + libraryCount + "/" + _package.Library.RootIds.Count +
                    (_package.Structure == null ? " 个焊点。" : " 个原型。"));
            bool structureConfirmed = _package.Structure == null || ReadStructure(services, project, notes);
            bool complete = _libraryConfirmed && structureConfirmed;
            _open.Enabled = !complete;
            string summary = complete ? "已核实本批导入。" : "导入结果待核实。";
            _status.Text = string.Join(Environment.NewLine, new[] { summary }.Concat(notes).Concat(_importNotices));
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception error) { _status.Text = error.GetBaseException().Message; }
        }
    }
}
