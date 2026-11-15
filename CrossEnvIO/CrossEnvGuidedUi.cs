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
        private MeasuredColumn _sourceCard,_targetCard,_directoryGroup,_coordinateGroup,_weldSettings;
        private Control _sceneActions,_peerRow,_openRow;
        private Label _sourceNote,_sourceTitle;
        private GroupBox _pickFrame,_sourceOriginFrame,_originFrame;
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
            {if(_populating)return;_objects.Objects=new TxObjectList();_whole.Checked=false;SourceChanged();UpdateKind();};
            _header.Controls.Add(_workspaces);
            _taskGroup=Column(false);_taskGroup.AutoSize=true;_taskGroup.Controls.Add(Caption("我要",true));
            _task=Choice("把场景对象传到另一个工程","把已有数据文件重建到本端工程");
            _task.SelectedIndexChanged+=(s,e)=>
            {
                if(_populating)return;
                if(_task.SelectedIndex!=1&&_sourceFromFile){_sourceFromFile=false;_objects.Objects=new TxObjectList();SourceChanged();}
                if(_task.SelectedIndex==1){int local=_peerList.FindIndex(p=>p.IsSelf);if(local>=0)_peers.SelectedIndex=local+1;}
                InvalidatePlan();
            };
            _taskGroup.Controls.Add(_task);
        }
        private MeasuredColumn Card(string title)
        {
            var card=Column(false);card.Padding=new Padding(10);card.Margin=new Padding(3);
            card.BackColor=Color.White;card.BorderStyle=BorderStyle.FixedSingle;card.Controls.Add(Caption(title,true));return card;
        }
        private GroupBox NativeFrame(string title,Control picker)=>CrossEnvPickForm.Frame(title,picker);
        private void BuildGuidedCards()
        {
            _cards=new Panel { Margin=Padding.Empty };
            _sourceCard=Card("1  选取内容和接收工程");_sourceTitle=(Label)_sourceCard.Controls[0];
            _sourceCard.Controls.Add(_taskGroup);_sourceCard.Controls.SetChildIndex(_taskGroup,0);
            _sourceCard.Controls.Add(Caption("接收工程 / 进程",true));
            var peers=new TableLayoutPanel { Height=30,MinimumSize=new Size(0,30),ColumnCount=2 };_peerRow=peers;
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
            PickFocus.Wire(_objects);
            _objects.DataChanged+=(s,e)=>ObserveSceneSelection();
            _objects.Picked+=(s,e)=>ObserveSceneSelection();
            _objects.ObjectInserted+=(s,e)=>ObserveSceneSelection();
            _objects.RowDeleted+=(s,e)=>ObserveSceneSelection();
            _objects.ValidObjectTyped+=(s,e)=>ObserveSceneSelection();
            _pickFrame=NativeFrame("场景对象 · 多选",_objects);_sourceCard.Controls.Add(_pickFrame);
            _filePreview=new DataGridView { ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,
                AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,Font=Font,Dock=DockStyle.Fill };
            _filePreview.Columns.Add("Name","Objects");_filePreview.Columns.Add("Type","类型");_filePreview.RowTemplate.Height=27;_filePreview.RowCount=5;_pickFrame.Controls.Add(_filePreview);
            _scope=Caption("在本端场景中选取要传输的对象。");_sourceCard.Controls.Add(_scope);
            var selection=Row();_sceneActions=selection;
            _whole=new CheckBox { AutoSize=true,Text="整个场景",Font=Font,Margin=new Padding(0,6,8,0) };
            _whole.CheckedChanged+=(s,e)=>{_objects.Enabled=!_whole.Checked;SourceChanged();};selection.Controls.Add(_whole);
            Btn(selection,"清空选择",()=>{_whole.Checked=false;_objects.Objects=new TxObjectList();},96);_sourceCard.Controls.Add(selection);

            _targetCard=Card("2  分配存放位置与重建坐标");
            BuildStorageBrowser(_targetCard);
            _directoryGroup=Column(false);_directoryGroup.AutoSize=true;
            _directoryGroup.Controls.Add(Caption("目录匹配方式（拖放分配优先）"));
            _destination=new TextBox { Text="导入",Font=Font };_directoryGroup.Controls.Add(_destination);
            _destination.TextChanged+=(s,e)=>{if(!_populating&&_placement!=null&&_placement.SelectedIndex==1){_singleDir.Text=_destination.Text;if(_plan!=null)_plan.SingleDirectory=_destination.Text;InvalidatePlan();}};
            var folders=Row();_browse=Btn(folders,"浏览目录…",()=>Run(async()=>{if(_description==null)await ConfirmReceiver();PickFolder(v=>_destination.Text=v);}),104);
            _settings=Btn(folders,"高级规则…",()=>OpenOptions(true),104);_settings.AccessibleDescription="类型规则、源文件重新定位及同名冲突处理。";
            _directoryGroup.Controls.Add(folders);_targetCard.Controls.Add(_directoryGroup);

            _coordinateGroup=Column(false);
            _sourceOrigin=new TxFrameEditBoxCtrl { Height=28,Font=Font };PickFocus.Wire(_sourceOrigin);
            _sourceOrigin.ValidFrameSet+=(s,e)=>{_sourceFrameInvalid=false;InvalidatePlan();};
            _sourceOrigin.InvalidFrameSet+=(s,e)=>{_sourceFrameInvalid=true;InvalidatePlan();};
            _sourceOriginFrame=NativeFrame("源端参考坐标 · 在本端选取",_sourceOrigin);_coordinateGroup.Controls.Add(_sourceOriginFrame);
            _origin=new TxFrameEditBoxCtrl { Height=28,Font=Font };PickFocus.Wire(_origin);
            _origin.ValidFrameSet+=(s,e)=>{_frameInvalid=false;InvalidatePlan();};_origin.InvalidFrameSet+=(s,e)=>{_frameInvalid=true;InvalidatePlan();};
            _originFrame=NativeFrame("接收端参考坐标 · 在接收工程选取",_origin);_coordinateGroup.Controls.Add(_originFrame);
            _coordinateGroup.Controls.Add(Caption("默认两端世界坐标。源端参考坐标 → 接收端参考坐标，同时对齐位置和朝向，保持实例间相对位置。"));
            var world=Row();_pickTargetFrame=Btn(world,"在接收端选坐标…",()=>Run(()=>RequestPick("frame",null)),158);
            Btn(world,"两端恢复世界坐标",()=>{_sourceOrigin.SetLocation(new TxTransformation());_origin.SetLocation(new TxTransformation());_sourceFrameInvalid=_frameInvalid=false;InvalidatePlan();},160);_coordinateGroup.Controls.Add(world);
            _targetCard.Controls.Add(_coordinateGroup);
            _weldSettings=Column(false);_targetCard.Controls.Add(_weldSettings);
            _cards.Controls.Add(_sourceCard);_cards.Controls.Add(_targetCard);_main.Controls.Add(_cards);
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
                _executionFiles.Enabled=editable;
                _storageSources.Enabled=editable&&!_sourceDirty;
                _objects.Visible=_sceneActions.Visible=!file;_objects.Enabled=editable&&!_whole.Checked;
                _pickFrame.Text=file?"文件内容 · 只读预览":"场景对象 · 多选";_openRow.Visible=_filePreview.Visible=file;
                _sourceTitle.Text=file?"1  文件内容和接收工程":"1  选取内容和接收工程";
                _sourceNote.Text=file?"打开数据文件，核对内容。":model?"本端场景多选；资源与零件自动识别。":"选取焊点、焊点操作或操作组。";
                _targetCard.Visible=_peerRow.Visible=true;_peers.Enabled=editable&&!file;
                bool files=model||_includeWeldParts.Checked;
                _storageSplit.Visible=_directoryGroup.Visible=files;_coordinateGroup.Visible=true;
                // The allocation action row and hint belong to the browser, not the weld page.
                foreach(Control c in _targetCard.Controls)if(c.Tag as string=="allocation")c.Visible=files;
                _weldSettings.Visible=!model;((Label)_targetCard.Controls[0]).Text=model?"2  分配存放位置与重建坐标":files?"2  绑定零件存放与焊点定位":"2  焊点定位与可选绑定";
                _sourceOrigin.Enabled=editable;_origin.Enabled=editable&&SelectedPid()==TxTools.Agent.Core.PsInstanceRegistry.SelfPid;
                _pickTargetFrame.Visible=SelectedPid()!=TxTools.Agent.Core.PsInstanceRegistry.SelfPid;
                _pickTargetFrame.Enabled=editable&&target&&_pickId==null;
                _browse.Enabled=editable&&target&&_placement.SelectedIndex==1&&!_destination.ReadOnly;
                if(file&&_description==null)_receiver.Text="本端活动工程；正在读取。";
                else if(SelectedPid()<0)_receiver.Text="选择接收进程后自动读取工程和库目录。";
                _bindOnly.Enabled=_project.Enabled=!model;_bindOnly.Visible=_project.Visible=!model;
                _includeWeldParts.Enabled=editable&&!model&&(!_sourceFromFile||!string.IsNullOrWhiteSpace(_plan?.WeldModelsFile));
                _issues.Visible=_issues.Items.Count>1;_bindingButton.Visible=!model&&_bindingIssues.Count>0;
                _bindingButton.Enabled=editable&&_issues.SelectedItem is BindingIssue;
                UpdateStorageActions();
                _next.Enabled=!_busy&&!_refreshingContext&&_pickId==null&&(_uncertain||((!_finishedJob||!_jobSucceeded)&&source&&target));
                _next.Text=_uncertain?"查询传输结果":_finishedJob?(_jobSucceeded?"传输完成":"核对后重新检查"):Prepared?"确认执行":"检查重建条件";
                _cancel.Visible=_busy&&_cancellation!=null;_query.Visible=false;
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
            int width=Math.Max(300,_main.ClientSize.Width-_main.Padding.Horizontal),gap=ScaleUi(8),edge=2;
            bool parallel=width>=ScaleUi(760),single=!_targetCard.Visible;
            int first=parallel&&!single?Math.Max(ScaleUi(260),(int)((width-gap-2*edge)*.32)):width-2*edge;
            int second=parallel&&!single?width-first-gap-2*edge:width-2*edge;
            SetCardWidth(_sourceCard,first);SetCardWidth(_targetCard,second);
            int positionWidth=Math.Max(200,second-_targetCard.Padding.Horizontal);
            SetCardWidth(_coordinateGroup,positionWidth);FitColumn(_coordinateGroup);
            FitColumn(_sourceCard);FitColumn(_targetCard);
            int sourceHeight=_sourceCard.GetPreferredSize(new Size(first,0)).Height,targetHeight=_targetCard.GetPreferredSize(new Size(second,0)).Height;
            if(parallel||single)
            {
                int height=Math.Max(Math.Max(sourceHeight,single?0:targetHeight),_main.ClientSize.Height-2*edge);
                _sourceCard.Height=_targetCard.Height=height;_sourceCard.Location=new Point(edge,edge);_targetCard.Location=new Point(edge+first+gap,edge);
                _cards.Size=new Size(width,height+2*edge);
            }
            else
            {
                _sourceCard.Height=sourceHeight;_targetCard.Height=targetHeight;_sourceCard.Location=new Point(edge,edge);_targetCard.Location=new Point(edge,edge+sourceHeight+gap);
                _cards.Size=new Size(width,sourceHeight+targetHeight+gap+2*edge);
            }
            _main.AutoScroll=true;_main.AutoScrollMinSize=_cards.Height>_main.ClientSize.Height?new Size(0,_cards.Height):Size.Empty;
        }
        private static void SetCardWidth(Control card,int width)
        {card.MinimumSize=Size.Empty;card.MaximumSize=new Size(width,0);card.Width=width;}
        private void ExpandForContent()
        {
            if(!Visible||_fitting)return;FitColumns();
            int needed=_header.Height+_cards.Height+_problemPanel.Height+_executionActions.Height+_status.Height+(_logExpanded?_log.Height:0)+_root.Padding.Vertical+(Height-ClientSize.Height)+12;
            var work=Screen.FromControl(this).WorkingArea;
            int available=Math.Max(Height,work.Bottom-Math.Max(work.Top,Top));
            int wanted=Math.Min(needed,available);if(wanted>Height+4)Height=wanted;
        }
        private int ScaleUi(int value)=>(int)Math.Ceiling(value*DeviceDpi/96.0);
    }
}
