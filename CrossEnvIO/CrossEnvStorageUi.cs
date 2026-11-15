using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace TxTools.CrossEnvIO
{
    public partial class CrossEnvIOForm
    {
        private DataGridView _storageSources;
        private TreeView _libraryTree;
        private Label _allocationStatus;
        private bool _storageRefreshing;
        private Point _dragStart;
        private MeasuredColumn _storageAdvanced;
        private CheckBox _advancedStorageToggle;
        private SplitContainer _storageSplit;
        private Button _assignButton,_sameTypeButton,_clearAssignmentButton;
        private readonly List<string> _libraryDirectories=new List<string>();

        private void BuildStorageBrowser(MeasuredColumn page)
        {
            var split=new SplitContainer { Width=560,Height=230,MinimumSize=new Size(300,190),SplitterDistance=278,
                Panel1MinSize=140,Panel2MinSize=140,FixedPanel=FixedPanel.None,Margin=new Padding(0,3,0,3) };
            _storageSplit=split;
            var left=new GroupBox { Text="本端 · 模型文件",Dock=DockStyle.Fill,Padding=new Padding(6,20,6,6),Font=Font };
            var right=new GroupBox { Text="接收端 · 库文件夹",Dock=DockStyle.Fill,Padding=new Padding(6,20,6,6),Font=Font };
            _storageSources=new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
                RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=true,
                AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,Font=Font };
            _storageSources.Columns.Add("File","文件名");_storageSources.Columns[0].FillWeight=130;
            _storageSources.Columns.Add("Type","类型");_storageSources.Columns.Add("Folder","接收目录");
            _storageSources.SelectionChanged+=(s,e)=>
            {
                if(!_storageRefreshing)UpdateStorageActions();
                if(_storageRefreshing||_plan==null||_storageSources.CurrentRow==null)return;
                var key=_storageSources.CurrentRow.Tag as string;
                int index=_plan.Packages.FindIndex(p=>p.Key==key);_package.SelectedIndex=index;
                UpdateStorageActions();
            };
            _storageSources.MouseDown+=(s,e)=>_dragStart=e.Location;
            _storageSources.MouseMove+=(s,e)=>
            {
                if(e.Button!=MouseButtons.Left||_busy||_uncertain)return;
                if(new Rectangle(_dragStart.X-SystemInformation.DragSize.Width/2,_dragStart.Y-SystemInformation.DragSize.Height/2,
                    SystemInformation.DragSize.Width,SystemInformation.DragSize.Height).Contains(e.Location))return;
                var keys=SelectedStorageKeys();if(keys.Length>0)_storageSources.DoDragDrop(keys,DragDropEffects.Copy);
            };
            _libraryTree=new TreeView { Dock=DockStyle.Fill,AllowDrop=true,HideSelection=false,Font=Font,ShowNodeToolTips=true };
            _libraryTree.AfterSelect+=(s,e)=>UpdateStorageActions();
            _libraryTree.DragOver+=(s,e)=>
            {
                var node=_libraryTree.GetNodeAt(_libraryTree.PointToClient(new Point(e.X,e.Y)));
                e.Effect=!_busy&&!_uncertain&&node?.Tag is string folder&&IsStorageSubfolder(folder)&&e.Data.GetDataPresent(typeof(string[]))?DragDropEffects.Copy:DragDropEffects.None;
                if(e.Effect!=DragDropEffects.None)_libraryTree.SelectedNode=node;
            };
            _libraryTree.DragDrop+=(s,e)=>
            {
                try
                {
                    var node=_libraryTree.GetNodeAt(_libraryTree.PointToClient(new Point(e.X,e.Y)));
                    if(node?.Tag is string folder&&e.Data.GetData(typeof(string[])) is string[] keys)AssignStorage(keys,folder,false);
                }
                catch(Exception ex){ShowFailure(ex);}
            };
            left.Controls.Add(_storageSources);right.Controls.Add(_libraryTree);split.Panel1.Controls.Add(left);split.Panel2.Controls.Add(right);page.Controls.Add(split);
            var actions=Row();
            _assignButton=Btn(actions,"分配 →",()=>AssignToSelectedFolder(false),90);
            _sameTypeButton=Btn(actions,"同分类 →",()=>AssignToSelectedFolder(true),100);
            _sameTypeButton.AccessibleDescription="按相同源目录或明确的设备分类批量分配，避免把所有普通组件归为一类。";
            _clearAssignmentButton=Btn(actions,"清除分配",()=>
            {
                if(_plan==null)return;var keys=SelectedStorageKeys();
                foreach(var p in _plan.Packages.Where(p=>keys.Contains(p.Key)))p.OverrideDirectory=null;
                InvalidatePlan();RefreshStorageSources();ShowPackage();
            },100);
            Btn(actions,"刷新目录",()=>Run(LoadLibraryTree),100);actions.Tag="allocation";page.Controls.Add(actions);
            _allocationStatus=Caption("多选本端文件，拖到接收库文件夹分配位置。");_allocationStatus.Tag="allocation";page.Controls.Add(_allocationStatus);
        }
        private string[] SelectedStorageKeys()=>_storageSources.SelectedRows.Cast<DataGridViewRow>().Select(r=>r.Tag as string).Where(k=>k!=null).ToArray();
        private void UpdateStorageActions()
        {
            if(_assignButton==null)return;
            bool ready=!_busy&&!_uncertain&&!_sourceDirty&&_plan!=null&&SelectedStorageKeys().Length>0;
            _assignButton.Enabled=_sameTypeButton.Enabled=ready&&_libraryTree.SelectedNode?.Tag is string folder&&IsStorageSubfolder(folder);
            _clearAssignmentButton.Enabled=ready;
        }
        private bool IsStorageSubfolder(string folder)
        {
            try
            {
                if(_description==null)return false;
                string root=(string)_description["systemRoot"],directory=TransferPaths.Destination(root,folder);
                LibraryInsertion.CheckDestination(Path.Combine(directory,"model.cojt"),root);
                return true;
            }
            catch{return false;}
        }
        private static string PackageType(TransferPackage package)=>string.Join(" / ",package.Types.Where(p=>p.Key.StartsWith("Tecnomatix.",StringComparison.Ordinal)).Select(p=>p.Value).Distinct());
        private void RefreshStorageSources()
        {
            if(_storageSources==null)return;_storageRefreshing=true;
            try
            {
                var selected=SelectedStorageKeys();_storageSources.Rows.Clear();
                if(_sourceDirty&&!_sourceFromFile)
                {
                    try
                    {
                        foreach(var item in ReadSceneSelection())
                        {
                            int row=_storageSources.Rows.Add(item.Name,item.GetType().Name,_sourceCaptureError==null?"正在识别…":"识别失败");
                            _storageSources.Rows[row].Cells[0].ToolTipText=_sourceCaptureError??"正在读取原生选取内容，完成后显示实际模型文件。";
                        }
                    }
                    catch(Exception ex){_allocationStatus.Text="无法读取选取内容："+ex.Message;}
                    _allocationStatus.Text=_storageSources.Rows.Count==0?"尚未选择模型对象。":_sourceCaptureError==null?"正在识别选取对象的模型文件…":"识别未完成，已保留选取项；原因见第三步。";
                    return;
                }
                if(_plan==null)return;
                foreach(var package in _plan.Packages)
                {
                    string folder;
                    try{folder=package.Conflict==TransferConflict.Skip?"已跳过":_plan.DirectoryFor(package);}
                    catch(Exception ex){folder="未分配："+ex.Message;}
                    int row=_storageSources.Rows.Add(package.Name,PackageType(package),folder);
                    _storageSources.Rows[row].Tag=package.Key;_storageSources.Rows[row].Selected=selected.Contains(package.Key);
                    _storageSources.Rows[row].Cells[0].ToolTipText=package.Source+"\n场景实例："+package.Instances+"；文件只复制一次，每个实例分别重建。";
                    string basis;
                    if(!string.IsNullOrWhiteSpace(package.OverrideDirectory))basis="手动拖放 / 分配";
                    else if(_plan.Placement==TransferPlacement.Automatic)TransferDirectoryMatcher.Match(package,_plan.LibraryDirectories,out basis);
                    else basis="高级目录规则";
                    _storageSources.Rows[row].Cells[2].ToolTipText=basis;
                }
                if(_plan.Kind=="model")
                {
                    TransferTable table;
                    try{table=TransferTable.Read(_plan.DataFile);}
                    catch(Exception ex){_allocationStatus.Text="数据快照无法读取，请重新检查或打开数据文件。";_storageSources.AccessibleDescription=ex.Message;return;}
                    foreach(var row in table.Rows.Where(r=>string.IsNullOrWhiteSpace(table.Value(r,"COJT"))))
                    {
                        string type=table.Value(row,"Type");
                        if(type=="TxCompoundResource"||type=="TxCompoundPart"||type.Contains("PhysicalRoot"))continue;
                        int index=_storageSources.Rows.Add(table.Value(row,"Name"),type,"无可读库文件");
                        _storageSources.Rows[index].DefaultCellStyle.ForeColor=Color.FromArgb(150,65,25);
                        _storageSources.Rows[index].Cells[0].ToolTipText="此对象没有可读取的 .cojt 库引用。请核对源端是否为本地建模对象，或库文件是否有效；不能将它作为文件包拖放。";
                    }
                }
                if(selected.Length==0&&_plan.Packages.Count==1)
                {
                    var row=_storageSources.Rows.Cast<DataGridViewRow>().First(r=>r.Tag as string==_plan.Packages[0].Key);
                    row.Selected=true;
                }
                _allocationStatus.Text=_plan.Packages.Count>0?"已识别 "+_plan.Packages.Count+" 个模型文件；可多选拖到接收库目录。"
                    :_storageSources.Rows.Count>0?"选取对象没有可读的库文件，请核对源端存储状态。":"当前范围仅有结构节点，无模型文件需要分配。";
            }
            finally{_storageRefreshing=false;UpdateStorageActions();}
        }
        private void AssignToSelectedFolder(bool sameType)
        {
            if(SelectedStorageKeys().Length==0){_allocationStatus.Text="先在本端模型文件列表中选中可分配的文件。";return;}
            if(!(_libraryTree.SelectedNode?.Tag is string folder))throw new InvalidOperationException("请先在右侧选择接收库文件夹。");
            AssignStorage(SelectedStorageKeys(),folder,sameType);
        }
        private void AssignStorage(string[] keys,string folder,bool sameType)
        {
            if(_busy||_uncertain)return;
            NeedPlan();if(_description==null)throw new InvalidOperationException("正在读取接收工程，请稍候再分配。");
            if(!IsStorageSubfolder(folder)){_allocationStatus.Text="模型不能存放在库根；请展开右侧目录，选择库内子文件夹。";return;}
            var selected=_plan.Packages.Where(p=>keys.Contains(p.Key)).ToArray();
            if(selected.Length==0){_allocationStatus.Text="先在本端模型文件列表中选中可分配的文件。";return;}
            TransferPaths.Destination((string)_description["systemRoot"],folder);
            var categories=new HashSet<string>(selected.Select(TransferDirectoryMatcher.Association).Where(k=>k!=null),StringComparer.OrdinalIgnoreCase);
            var assigned=sameType?_plan.Packages.Where(p=>selected.Contains(p)||categories.Contains(TransferDirectoryMatcher.Association(p)??"")).ToArray():selected;
            foreach(var package in assigned)package.OverrideDirectory=folder;
            InvalidatePlan();RefreshStorageSources();ShowPackage();UpdateDestination();
            _allocationStatus.Text="已分配 "+assigned.Length+" 个模型 → "+folder+"；尚未复制，请重新检查。";
        }
        private async Task LoadLibraryTree()
        {
            string selected=_libraryTree.SelectedNode?.Tag as string;
            _libraryTree.Nodes.Clear();_libraryDirectories.Clear();
            if(_description==null){_allocationStatus.Text="请先选择接收工程。";return;}
            int pid=_confirmedPid;string scope=ReceiverScope(pid,_description);
            var result=await Call("crossenv_inspect",new JObject { ["library_relative"]=".",["recursive_folders"]=true,
                ["expected_study_id"]=(string)_description["study_id"],["expected_library_root"]=(string)_description["systemRoot"] },true,pid);
            if(IsDisposed||scope!=ReceiverScope(_confirmedPid,_description))return;
            if((bool?)result["ok"]==false)throw new IOException((string)result["error"]);
            if((string)result["study_id"]!=(string)_description["study_id"]||!TransferPaths.Same((string)result["systemRoot"],(string)_description["systemRoot"]))
                throw new InvalidOperationException("接收工程已变化，请重新读取目录。");
            var root=new TreeNode((string)_description["study"]+" / 库根") { Tag=".",ForeColor=SystemColors.GrayText,
                ToolTipText=(string)_description["systemRoot"]+"\n模型不能直接存放在库根，请选择子文件夹。" };
            _libraryTree.BeginUpdate();
            try
            {
                _libraryTree.Nodes.Add(root);
                var nodes=new Dictionary<string,TreeNode>(StringComparer.OrdinalIgnoreCase) { ["."]=root };
                foreach(JObject folder in result["folders"] as JArray??new JArray())_libraryDirectories.Add(((string)folder["relative"]).Replace('/','\\'));
                foreach(string folder in _libraryDirectories.OrderBy(f=>f.Count(c=>c=='\\')).ThenBy(f=>f,StringComparer.CurrentCultureIgnoreCase))
                {
                    string parent=Path.GetDirectoryName(folder);if(string.IsNullOrEmpty(parent))parent=".";
                    if(!nodes.TryGetValue(parent,out var owner))continue;
                    var child=new TreeNode(Path.GetFileName(folder)) { Tag=folder,ToolTipText=folder };owner.Nodes.Add(child);nodes[folder]=child;
                }
                root.Expand();_libraryTree.SelectedNode=selected!=null&&nodes.TryGetValue(selected,out var previous)?previous:root;
            }
            finally{_libraryTree.EndUpdate();}
            if(_plan!=null){_plan.LibraryDirectories=new List<string>(_libraryDirectories);InvalidatePlan();}
            var warnings=result["folder_warnings"] as JArray;
            if(warnings?.Count>0)_allocationStatus.Text="部分库目录未读取；未匹配项请手动指定。";
        }
    }
}
