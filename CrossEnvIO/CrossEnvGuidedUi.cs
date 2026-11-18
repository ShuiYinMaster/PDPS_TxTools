using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    public partial class CrossEnvIOForm
    {
        private ComboBox _task;
        private TabControl _workspaces;
        private Panel _cards;
        private GroupBox _sourceSection, _targetSection;
        private MeasuredColumn _sourceCard,_targetCard,_directoryGroup,_coordinateGroup,_weldSettings;
        private Control _sceneActions,_openRow;
        private Label _sourceNote;
        private CrossEnvNativeFrame _sourceOriginFrame, _originFrame;
        private TableLayoutPanel _coordinateRow;
        private MeasuredColumn _taskGroup;
        private TextBox _dataPath;
        private DataGridView _filePreview;
        private bool _rendering;

        private void BuildGuidedHeader()
        {
            _workspaces=new TabControl { Height=46,MinimumSize=new Size(0,46),Font=Font,SizeMode=TabSizeMode.Fixed,
                ItemSize=new Size(188,34),DrawMode=TabDrawMode.OwnerDrawFixed,Padding=new Point(14,6) };
            _workspaces.TabPages.Add("模型结构 · 资源与零件");_workspaces.TabPages.Add("焊点");
            _workspaces.DrawItem+=(s,e)=>
            {
                bool selected=e.Index==_workspaces.SelectedIndex;
                using(var brush=new SolidBrush(selected?Color.FromArgb(0,111,134):Color.FromArgb(227,234,241)))e.Graphics.FillRectangle(brush,e.Bounds);
                using(var font=new Font(Font,selected?FontStyle.Bold:FontStyle.Regular))
                    TextRenderer.DrawText(e.Graphics,_workspaces.TabPages[e.Index].Text,font,e.Bounds,selected?Color.White:Color.FromArgb(45,65,82),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
                if((e.State&DrawItemState.Focus)!=0)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(e.Bounds,-4,-4));
            };
            _kind=Choice("模型结构","焊点");_kind.Visible=false;_header.Controls.Add(_kind);
            _workspaces.SelectedIndexChanged+=(s,e)=> { if(!_populating&&_workspaces.SelectedIndex>=0)_kind.SelectedIndex=_workspaces.SelectedIndex; };
            _kind.SelectedIndexChanged+=(s,e)=>
            {if(_populating)return;ResetInput();UpdateKind();FitColumns();};
            _header.Controls.Add(_workspaces);
            _taskGroup=Column(false);_taskGroup.AutoSize=true;_taskGroup.Controls.Add(Caption("我要",true));
            _task=Choice("把场景对象传到另一个工程","把已有数据文件重建到本端工程");
            _task.SelectedIndexChanged+=(s,e)=>
            {
                if(_populating)return;
                ResetInput();
                if(_task.SelectedIndex==1){int local=_peerList.FindIndex(p=>p.IsSelf);if(local>=0)_peers.SelectedIndex=local+1;}
                FitColumns();
            };
            _taskGroup.Controls.Add(_task);
        }
        private void ResetInput()
        {
            _populating=true;
            try
            {
                _plan=null; _sourceFromFile=false;
                _whole.Checked=false; _objects.Objects=new TxObjectList();
                _dataPath.Clear(); _filePreview.Rows.Clear(); _filePreview.RowCount=5;
                _package.Items.Clear(); _bindingIssues.Clear();
                _pickId=null; _pickTimer.Stop();
            }
            finally { _populating=false; }
            SourceChanged();
            if (_task.SelectedIndex==1)
            {
                _capturePending=false;
                _scope.Text="请打开要在本端工程重建的数据文件。";
            }
        }
        private static GroupBox CreateSection(string title, Control content = null)
        {
            var section = FormUiKit.MkCardGroup(title, FormUiKit.Theme.TxCol1.Color);
            section.AutoSize = false;
            section.BackColor = FormUiKit.CardBack;
            if (content != null)
            {
                content.Dock = DockStyle.Fill;
                section.Controls.Add(content);
            }
            return section;
        }
        private void BuildGuidedCards()
        {
            _cards=new Panel { Margin=Padding.Empty };
            _sourceCard=Column(false);
            _sourceSection=CreateSection("1  选取内容和接收工程",_sourceCard);
            _sourceSection.Dock=DockStyle.None;
            _sourceCard.Controls.Add(_taskGroup);
            _sourceCard.Controls.Add(Caption("接收工程 / 进程",true));
            var peers=new TableLayoutPanel { Height=30,MinimumSize=new Size(0,30),ColumnCount=2 };
            peers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));peers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,90));
            _peers=Choice("请选择接收工程…");_peers.DropDownWidth=640;_peers.Dock=DockStyle.Fill;peers.Controls.Add(_peers,0,0);
            var refresh=FormUiKit.MkBtn("刷新",Color.White,60,28);refresh.Click+=(s,e)=>RefreshPeers();peers.Controls.Add(refresh,1,0);
            _peers.SelectedIndexChanged+=(s,e)=>{if(!_populating)ReceiverChanged();};_sourceCard.Controls.Add(peers);
            _receiver=Caption("请选择接收工程。");_sourceCard.Controls.Add(_receiver);
            _sourceNote=Caption("在本端场景中多选对象。");_sourceCard.Controls.Add(_sourceNote);
            var open=new TableLayoutPanel { Height=30,MinimumSize=new Size(0,30),ColumnCount=2 };_openRow=open;
            open.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));open.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,64));
            _dataPath=new TextBox { ReadOnly=true,Dock=DockStyle.Fill };open.Controls.Add(_dataPath,0,0);
            var load=FormUiKit.MkBtn("打开…",Color.White,60,28);load.Click+=(s,e)=>Run(ImportData);open.Controls.Add(load,1,0);_sourceCard.Controls.Add(open);
            _objects=new TxObjGridCtrl { Font=FormUiKit.BaseFont,ListenToPick=true,EnableMultipleSelection=true,EnableRecurringObjects=false,Objects=new TxObjectList() };
            _objects.BorderStyle=BorderStyle.FixedSingle;
            var objectGrid = _objects.Controls.OfType<DataGridView>().FirstOrDefault();
            if (objectGrid != null)
                objectGrid.ColumnAdded += (s,e) => UpdateObjectPickerHeader();
            _objects.Load += (s,e) => FitColumns();
            PickFocus.Wire(_objects);
            _objects.DataChanged+=(s,e)=>ObserveSceneSelection();
            _objects.Picked+=(s,e)=>ObserveSceneSelection();
            _objects.ObjectInserted+=(s,e)=>ObserveSceneSelection();
            _objects.RowDeleted+=(s,e)=>ObserveSceneSelection();
            _objects.ValidObjectTyped+=(s,e)=>ObserveSceneSelection();
            _sourceCard.Controls.Add(_objects);
            _filePreview=new DataGridView { ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,
                AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,Font=Font };
            _filePreview.Columns.Add("Name","对象名称");_filePreview.Columns.Add("Type","类型");_filePreview.RowTemplate.Height=27;_filePreview.RowCount=5;
            _sourceCard.Controls.Add(_filePreview);
            _scope=Caption("在本端场景中选取要传输的对象。");_sourceCard.Controls.Add(_scope);
            var selection=Row();_sceneActions=selection;
            _whole=new CheckBox { AutoSize=true,Text="整个场景",Font=Font,Margin=new Padding(0,6,8,0) };
            _whole.CheckedChanged+=(s,e)=>{_objects.Enabled=!_whole.Checked;SourceChanged();};selection.Controls.Add(_whole);
            Btn(selection,"清空选择",()=>{_whole.Checked=false;_objects.Objects=new TxObjectList();},96);_sourceCard.Controls.Add(selection);
            _sourceFileActions=Row();
            _exportTsv=Btn(_sourceFileActions,"导出 TSV…",()=>Run(()=>ExportData(false)),106);
            _exportBundle=Btn(_sourceFileActions,"导出离线包…",()=>Run(()=>ExportData(true)),120);
            _previewTransfer=Btn(_sourceFileActions,"传输明细",()=>Run(()=>
            {
                EnsureCaptured();
                PreviewPlan();
                return System.Threading.Tasks.Task.CompletedTask;
            }),100);
            _cleanupFiles=Btn(_sourceFileActions,"清理未用文件",()=>Run(CleanupFiles),128);
            _sourceCard.Controls.Add(_sourceFileActions);

            _targetCard=Column(false);
            _targetSection=CreateSection("2  分配存放位置与重建坐标",_targetCard);
            _targetSection.Dock=DockStyle.None;
            BuildStorageBrowser(_targetCard);
            _directoryGroup=Column(false);_directoryGroup.AutoSize=true;
            _directoryGroup.Controls.Add(Caption("目录匹配方式（拖放分配优先）"));
            _destination=new TextBox { Text="导入",Font=Font };_directoryGroup.Controls.Add(_destination);
            _destination.TextChanged+=(s,e)=>{if(!_populating&&_placement!=null&&_placement.SelectedIndex==1){_singleDir.Text=_destination.Text;if(_plan!=null)_plan.SingleDirectory=_destination.Text;InvalidatePlan();}};
            var folders=Row();_browse=Btn(folders,"浏览目录…",()=>Run(async()=>{if(_description==null)await ConfirmReceiver();PickFolder(v=>_destination.Text=v);}),104);
            _settings=Btn(folders,"高级规则…",()=>OpenOptions(true),104);_settings.AccessibleDescription="类型规则、源文件重新定位及同名冲突处理。";
            _directoryGroup.Controls.Add(folders);_targetCard.Controls.Add(_directoryGroup);

            _coordinateGroup=Column(false);
            _coordinateRow = new TableLayoutPanel
            {
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            _coordinateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            _coordinateRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            _coordinateRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            _sourceOrigin=new TxFrameEditBoxCtrl { Height=28,Font=Font };PickFocus.Wire(_sourceOrigin);
            _sourceOrigin.ValidFrameSet+=(s,e)=>{_sourceFrameInvalid=false;InvalidatePlan();};
            _sourceOrigin.InvalidFrameSet+=(s,e)=>{_sourceFrameInvalid=true;InvalidatePlan();};
            _sourceOriginFrame=new CrossEnvNativeFrame("源端参考坐标",_sourceOrigin) { Dock=DockStyle.Fill };
            _coordinateRow.Controls.Add(_sourceOriginFrame,0,0);
            _origin=new TxFrameEditBoxCtrl { Height=28,Font=Font };PickFocus.Wire(_origin);
            _origin.ValidFrameSet+=(s,e)=>{_frameInvalid=false;InvalidatePlan();};_origin.InvalidFrameSet+=(s,e)=>{_frameInvalid=true;InvalidatePlan();};
            _originFrame=new CrossEnvNativeFrame("接收端参考坐标",_origin) { Dock=DockStyle.Fill };
            _coordinateRow.Controls.Add(_originFrame,1,0);
            _coordinateGroup.Controls.Add(_coordinateRow);
            _coordinateGroup.Controls.Add(Caption("左侧在本端选取，右侧在接收工程选取。默认使用世界坐标，指定参考坐标后按两端坐标对齐位置和朝向。"));
            var world=Row();_pickTargetFrame=Btn(world,"在接收端选坐标…",()=>Run(()=>RequestPick("frame",null)),158);
            Btn(world,"两端恢复世界坐标",()=>{_sourceOrigin.SetLocation(new TxTransformation());_origin.SetLocation(new TxTransformation());_sourceFrameInvalid=_frameInvalid=false;InvalidatePlan();},160);_coordinateGroup.Controls.Add(world);
            _targetCard.Controls.Add(_coordinateGroup);
            _weldSettings=Column(false);_targetCard.Controls.Add(_weldSettings);
            _cards.Controls.Add(_sourceSection);_cards.Controls.Add(_targetSection);_main.Controls.Add(_cards);
        }

        private void UpdateObjectPickerHeader()
        {
            string title = _kind.SelectedIndex == 0 ? "场景对象（多选）" : "焊点或操作（多选）";
            _objects.AccessibleName = title;
            // Change the display caption without recreating the SDK column or its pick binding.
            var grid = _objects.Controls.OfType<DataGridView>().FirstOrDefault();
            if (grid?.Columns.Count == 1)
                grid.Columns[0].HeaderText = title;
        }

        private void FitPickers()
        {
            int height = CrossEnvPickForm.NativeGridHeight(_objects.Font, DeviceDpi);
            _objects.MinimumSize = new Size(0, height);
            _objects.Height = height;
            _filePreview.Height = height;
            _sourceOriginFrame.RefreshMetrics(DeviceDpi);
            _originFrame.RefreshMetrics(DeviceDpi);
            int coordinateHeight = Math.Max(
                _sourceOriginFrame.Height + _sourceOriginFrame.Margin.Vertical,
                _originFrame.Height + _originFrame.Margin.Vertical);
            _coordinateRow.MinimumSize = new Size(0, coordinateHeight);
            _coordinateRow.Height = coordinateHeight;
        }
        private bool Prepared=>_plan!=null&&!_sourceDirty&&!string.IsNullOrEmpty(_plan.PreparedStamp);
        private void UpdateGuidedState()
        {
            if(_next==null||_rendering||IsDisposed||Disposing)return;_rendering=true;
            try
            {
                bool file=_task.SelectedIndex==1,model=_kind.SelectedIndex==0,editable=!_busy&&!_uncertain;
                bool source=file?_sourceFromFile&&!_sourceDirty:_whole.Checked||_objects.Count>0,target=SelectedPid()>=0;
                _header.Enabled=_main.Enabled=_settings.Enabled=_settingsWindow.Enabled=editable;
                bool fileActions=editable&&!_refreshingContext;
                _exportTsv.Enabled=fileActions&&source&&(model||!_includeWeldParts.Checked);
                _exportBundle.Enabled=fileActions&&source;
                _previewTransfer.Enabled=fileActions&&source&&target&&_description!=null;
                _cleanupFiles.Enabled=fileActions&&_copies?.CopiedDirs.Count>0;
                _storageSources.Enabled=editable&&!_sourceDirty;
                _objects.Visible=_sceneActions.Visible=!file;_objects.Enabled=editable&&!_whole.Checked;
                UpdateObjectPickerHeader();
                _openRow.Visible=_filePreview.Visible=file;
                _sourceSection.Text=file?"1  文件内容和接收工程":"1  选取内容和接收工程";
                _sourceNote.Text=file?"打开数据文件，核对内容。":model?"本端场景多选；资源与零件自动识别。":"选取焊点、焊点操作或操作组。";
                _peers.Enabled=editable&&!file;
                bool files=model||_includeWeldParts.Checked;
                _storageSplit.Visible=_directoryGroup.Visible=files;_coordinateGroup.Visible=true;
                // The allocation action row and hint belong to the browser, not the weld page.
                foreach(Control c in _targetCard.Controls)if(c.Tag as string=="allocation")c.Visible=files;
                _weldSettings.Visible=!model;
                _targetSection.Text=model?"2  分配存放位置与重建坐标":files?"2  绑定零件存放与焊点定位":"2  焊点定位与可选绑定";
                _sourceOrigin.Enabled=editable;_origin.Enabled=editable&&SelectedPid()==TxTools.Agent.Core.PsInstanceRegistry.SelfPid;
                _pickTargetFrame.Visible=SelectedPid()!=TxTools.Agent.Core.PsInstanceRegistry.SelfPid;
                _pickTargetFrame.Enabled=editable&&target&&_pickId==null;
                _browse.Enabled=editable&&target&&_placement.SelectedIndex==1&&!_destination.ReadOnly;
                if(file&&_description==null)_receiver.Text="本端活动工程；正在读取。";
                else if(SelectedPid()<0)_receiver.Text="选择接收进程后自动读取工程和库目录。";
                _bindOnly.Enabled=_project.Enabled=!model;_bindOnly.Visible=_project.Visible=!model;
                _includeWeldParts.Enabled=editable&&!model&&(!_sourceFromFile||!string.IsNullOrWhiteSpace(_plan?.WeldModelsFile));
                _issues.Visible=_issues.Items.Count>1;_bindingButton.Visible=!model&&_bindingIssues.Count>0;
                _bindingButton.Enabled=editable&&_pickId==null&&_issues.SelectedItem is BindingIssue;
                UpdateStorageActions();
                _next.Enabled=!_busy&&!_refreshingContext&&_pickId==null&&(_uncertain||((!_finishedJob||!_jobSucceeded)&&source&&target));
                _next.Text=_uncertain?"查询传输结果":_finishedJob?(_jobSucceeded?"传输完成":"核对后重新检查"):Prepared?"确认执行":"检查重建条件";
                _cancel.Visible=_busy&&_cancellation!=null;_query.Visible=false;
                if (!_busy && !_uncertain && !_finishedJob && !Prepared && _issues.Items.Count==0)
                    _summary.Text = !source ? (file ? "请先打开要重建的数据文件。" : "请先在第一区选取场景对象，或勾选「整个场景」。")
                        : !target ? "已选定源数据。请选择接收工程，或直接导出数据。"
                        : _refreshingContext || _receiverPending ? "正在读取源数据或接收工程…"
                        : "点击「检查重建条件」核对文件和接收工程。";
            }
            finally{_rendering=false;}
        }
        private void UpdateGuidedSource(string originalFile=null)
        {
            if(_plan==null||_workspaces==null)return;bool old=_populating;_populating=true;
            try{_workspaces.SelectedIndex=_kind.SelectedIndex;}finally{_populating=old;}
            if(_sourceFromFile)
            {
                _dataPath.Text=originalFile??_plan.DataFile;var table=TransferTable.Read(_plan.DataFile);_filePreview.Rows.Clear();
                foreach(var row in table.Rows.Take(500))_filePreview.Rows.Add(table.Value(row,_plan.Kind=="weld"?"WeldOpName":"Name"),table.Value(row,"Type"));
                while(_filePreview.Rows.Count<5)_filePreview.Rows.Add("","");_task.SelectedIndex=1;
            }
            UpdateGuidedState();
        }
        private void FitGuidedCards()
        {
            if(_cards==null)return;
            int width=Math.Max(300,_main.ClientSize.Width-_main.Padding.Horizontal);
            int gap=ScaleUi(8),edge=ScaleUi(2);
            bool parallel=width>=ScaleUi(760);
            int available=width-2*edge;
            int first=parallel?Math.Max(ScaleUi(260),(int)((available-gap)*.32)):available;
            int second=parallel?available-first-gap:available;
            _sourceSection.Width=first;
            _targetSection.Width=second;
            FitColumn(_sourceCard);FitColumn(_targetCard);
            int sourceHeight=SectionHeight(_sourceSection,_sourceCard);
            int targetHeight=SectionHeight(_targetSection,_targetCard);
            if(parallel)
            {
                int height=Math.Max(Math.Max(sourceHeight,targetHeight),_main.ClientSize.Height-2*edge);
                _sourceSection.SetBounds(edge,edge,first,height);
                _targetSection.SetBounds(edge+first+gap,edge,second,height);
                _cards.Size=new Size(width,height+2*edge);
            }
            else
            {
                _sourceSection.SetBounds(edge,edge,first,sourceHeight);
                _targetSection.SetBounds(edge,edge+sourceHeight+gap,second,targetHeight);
                _cards.Size=new Size(width,sourceHeight+targetHeight+gap+2*edge);
            }
            _main.AutoScroll=true;_main.AutoScrollMinSize=_cards.Height>_main.ClientSize.Height?new Size(0,_cards.Height):Size.Empty;
        }
        private static int SectionHeight(GroupBox section, MeasuredColumn content)
        {
            return content.GetPreferredSize(Size.Empty).Height + section.ClientSize.Height - section.DisplayRectangle.Height;
        }
        private void ExpandForContent()
        {
            if(!Visible||_fitting)return;FitColumns();
            int needed=_header.Height+_cards.Height+_problemSection.Height+_problemSection.Margin.Vertical+_footer.Height+_root.Padding.Vertical+(Height-ClientSize.Height)+12;
            var work=Screen.FromControl(this).WorkingArea;
            int available=Math.Max(Height,work.Bottom-Math.Max(work.Top,Top));
            int wanted=Math.Min(needed,available);if(wanted>Height+4)Height=wanted;
        }
        private int ScaleUi(int value)=>(int)Math.Ceiling(value*DeviceDpi/96.0);
    }
}
