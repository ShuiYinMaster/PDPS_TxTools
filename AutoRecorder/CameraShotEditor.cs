using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

namespace TxTools.AutoRecorder
{
    internal sealed class CameraShotEditor : UserControl
    {
        private IList<CameraShot> _shots;
        private readonly ComboBox _mode=Combo("快速录像","编排镜头");
        private readonly RadioButton _quickModeButton=ModeButton("快速录像"),_composeModeButton=ModeButton("编排镜头");
        private readonly ComboBox _addKind=Combo("定点","环绕焦点","推近 / 拉远","两点移动","跟随资源");
        private readonly Label _summary=new Label {AutoSize=true,ForeColor=Color.DimGray,Margin=new Padding(8,8,0,0)};
        private double? _operationDuration;
        private int _locationCount=int.MaxValue;
        private double? _simulationTime;
        private bool _quick;
        private double? _timelineCursor;
        private bool _timelineSelecting;
        private int? _hitLane;
        private Point _dragOrigin;
        private CameraShot _dragShot;
        private bool _dragging;
        private int _dropIndex=-1;
        private sealed class ShotDragData {public CameraShotEditor Owner;public IList<CameraShot> Plan;public Guid Id;}
        private sealed class BufferedCanvas:Panel
        {
            public BufferedCanvas(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);TabStop=true;}
        }

        private Label _quickHint,_quickSettings,_inspectorHeading;
        private readonly ToolTip _tips=new ToolTip {ShowAlways=true,InitialDelay=400,ReshowDelay=100,AutoPopDelay=8000};
        public bool QuickMode {get {return _quick;}}
        public event Action Changed,SaveRequested,LoadRequested,CopyRequested,SimulationRequested,StopRequested,PauseRequested,RestoreViewRequested;

        private readonly ComboBox _startView=Combo("独立记录起点","继承前段末态");
        private readonly CheckBox _geometryCenter=new CheckBox{Text="使用几何中心",AutoSize=true};
        private readonly Dictionary<Guid,string> _hints=new Dictionary<Guid,string>();
        private readonly Label _inlineHint=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8,5,4,0)};
        private Label _instruction;
        private CheckBox _motionToggle;
        private Control _motionRow;
        private Button _captureStart, _captureEnd;
        private readonly List<TransportIconButton> _planTools=new List<TransportIconButton>();
        private TransportIconButton _moveUpButton,_moveDownButton;
        private TransportIconButton _playButton,_simulationButton,_pauseButton,_stopButton,_restoreButton;
        private readonly Label _transportTime=new Label {AutoSize=false,Width=166,Height=30,TextAlign=ContentAlignment.MiddleLeft,AutoEllipsis=true,ForeColor=Color.DimGray,Margin=new Padding(6,0,0,0),AccessibleName="预览状态"};
        private bool _restoreAvailable,_wholePaused;
        private string _selectedIssue;
        private bool _simulationRunning,_simulationStopping,_singlePaused;
        private Panel _propertyScroll;
        private TableLayoutPanel _fields;
        private bool _layingOutProperties;
        private readonly ListBox _list = new ListBox { Dock=DockStyle.Fill };
        private readonly CheckBox _enabled=new CheckBox {Text="启用此镜头",Checked=true,AutoSize=true};
        private readonly TextBox _name=new TextBox();
        private readonly ComboBox _kind=Combo("定点","环绕焦点","推近 / 拉远","两点移动","跟随资源");
        private readonly ComboBox _trigger=Combo("指定仿真时间","到达 Location","按顺序衔接（默认）");
        private readonly ComboBox _axis=Combo("世界 X","世界 Y","世界 Z");
        private readonly NumericUpDown _start=Number(0,100000,0), _duration=Number(0.001m,100000,3),
            _location=Number(1,100000,1), _angle=Number(-36000,36000,180), _distance=Number(0.001m,10000000,500);
        private readonly NumericUpDown _radius=Number(0.001m,10000000,1500), _height=Number(-10000000,10000000,600), _startAngle=Number(-36000,36000,0);
        private readonly TxObjEditBoxCtrl _object=new TxObjEditBoxCtrl { ValidatorType=TxValidatorType.AnyLocatableObject };
        private readonly TxFrameEditBoxCtrl _point=new TxFrameEditBoxCtrl();
        private readonly ComboBox _focusMode=Combo("固定坐标","对象位置快照","跟随对象");
        private readonly CheckBox _smooth=new CheckBox {Text="缓入缓出",Checked=true,AutoSize=true};
        private readonly Label _status=new Label { AutoSize=true, MaximumSize=new Size(480,0) };
        private readonly Panel _timeline=new BufferedCanvas { Dock=DockStyle.Fill, Height=85, BackColor=Color.White };
        private readonly System.Windows.Forms.Timer _previewTimer=new System.Windows.Forms.Timer {Interval=33};
        private readonly System.Diagnostics.Stopwatch _previewClock=new System.Diagnostics.Stopwatch();
        private readonly TrackBar _preview=new TrackBar { Minimum=0, Maximum=1000, TickFrequency=250, Dock=DockStyle.Fill };
        private TxCamera _saved;
        private bool _loading, _busy;
        private readonly List<Control> _editControls=new List<Control>();
        private CameraShot Selected { get { return _list.SelectedItem as CameraShot; } }
        private static ComboBox Combo(params string[] items)
        { var c=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList}; c.Items.AddRange(items); c.SelectedIndex=0; return c; }
        private static NumericUpDown Number(decimal min,decimal max,decimal value)
        { return new NumericUpDown { Minimum=min,Maximum=max,Value=value,DecimalPlaces=3,Increment=0.1m }; }
        public CameraShotEditor()
        {
            Dock=DockStyle.Fill;
            _list.IntegralHeight=false;
            _location.DecimalPlaces=0;_location.Increment=1;
            BackColor = Color.FromArgb(244,247,250);
            var root = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Padding=new Padding(4) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,310));
            root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var center = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=7, Margin=new Padding(0,0,8,0), BackColor=Color.White };
            center.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            center.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,132));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            var modeRow=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,BackColor=Color.White,Margin=Padding.Empty};
            modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            var title=Heading("02  镜头编排");title.AutoSize=true;title.Margin=Padding.Empty;modeRow.Controls.Add(title,0,0);
            var modes=new FlowLayoutPanel{AutoSize=true,WrapContents=false,Margin=Padding.Empty,Anchor=AnchorStyles.Left};
            modes.Controls.Add(_quickModeButton);modes.Controls.Add(_composeModeButton);modeRow.Controls.Add(modes,1,0);
            _quickModeButton.CheckedChanged+=(sender,e)=>{if(_quickModeButton.Checked&&!_loading)_mode.SelectedIndex=0;};
            _composeModeButton.CheckedChanged+=(sender,e)=>{if(_composeModeButton.Checked&&!_loading)_mode.SelectedIndex=1;};
            _mode.Visible=false;_mode.TabStop=false;modeRow.Controls.Add(_mode,0,0);
            _summary.AutoSize=false;_summary.Dock=DockStyle.Fill;_summary.AutoEllipsis=true;_summary.TextAlign=ContentAlignment.MiddleRight;_summary.Margin=new Padding(6,0,6,0);modeRow.Controls.Add(_summary,2,0);
            center.Controls.Add(modeRow,0,0);
            _mode.SelectedIndexChanged+=(sender,e)=> {if(_loading)return;Restore();_quick=_mode.SelectedIndex==0;UpdateEnabled();Changed?.Invoke();};
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new Padding(6,5,6,5),Margin=Padding.Empty,BackColor=Color.FromArgb(247,250,252)};
            _addKind.FlatStyle=FlatStyle.Flat;_addKind.Width=96;_addKind.Margin=new Padding(0,2,6,2);buttons.Controls.Add(_addKind);
            AddButton(buttons,"＋ 添加镜头",()=>AddShot());
            AddToolDivider(buttons);
            var planTools=new FlowLayoutPanel{Name="planTools",AutoSize=true,WrapContents=false,Margin=Padding.Empty,BackColor=buttons.BackColor};buttons.Controls.Add(planTools);
            _planTools.Add(AddIconButton(planTools,TransportIcon.SavePlan,"保存配置","保存当前操作的镜头配置",()=>SaveRequested?.Invoke(),compact:true));
            _planTools.Add(AddIconButton(planTools,TransportIcon.LoadPlan,"载入配置","从文件载入镜头配置",()=>LoadRequested?.Invoke(),compact:true));
            _planTools.Add(AddIconButton(planTools,TransportIcon.ApplyPlan,"应用到其他操作","将当前配置复制到其他已选操作",()=>CopyRequested?.Invoke(),compact:true));
            AddToolDivider(buttons);
            var editTools=new FlowLayoutPanel{Name="shotEditTools",AutoSize=true,WrapContents=false,Margin=Padding.Empty,BackColor=buttons.BackColor};buttons.Controls.Add(editTools);
            AddIconButton(editTools,TransportIcon.DuplicateShot,"复制镜头","在当前操作内复制选中镜头",()=>DuplicateShot(),compact:true);
            AddIconButton(editTools,TransportIcon.DeleteShot,"删除镜头","删除选中镜头（Delete）",()=>DeleteSelected(),compact:true);
            _moveUpButton=AddIconButton(editTools,TransportIcon.MoveUp,"上移镜头","上移选中镜头（Ctrl+↑）；顺序镜头时间随排序改变",()=>MoveShot(-1),compact:true);
            _moveDownButton=AddIconButton(editTools,TransportIcon.MoveDown,"下移镜头","下移选中镜头（Ctrl+↓）；指定时间保持不变",()=>MoveShot(1),compact:true);
            center.Controls.Add(buttons,0,1);
            _list.DrawMode=DrawMode.OwnerDrawFixed; _list.ItemHeight=Math.Max(18,Font.Height)*2+10;
            _list.FontChanged+=(sender,e)=> {_list.ItemHeight=Math.Max(18,_list.Font.Height)*2+10;_list.Invalidate();};
            _list.DrawItem+=(sender,e)=>DrawShot(e);
            _list.AllowDrop=true;
            _list.KeyDown+=OnListKeyDown;
            _list.MouseDown+=OnListMouseDown;
            _list.MouseMove+=OnListMouseMove;
            _list.MouseUp+=(sender,args)=>{if(!_dragging)_dragShot=null;};
            _list.DragEnter+=OnShotDragOver;_list.DragOver+=OnShotDragOver;
            _list.DragLeave+=(sender,e)=>ClearDropMarker();
            _list.DragDrop+=OnShotDrop;
            var listHost=new Panel {Dock=DockStyle.Fill};
            _quickHint=new Label {Dock=DockStyle.Fill,Padding=new Padding(12),ForeColor=Color.FromArgb(35,81,116)};
            listHost.Controls.Add(_list);listHost.Controls.Add(_quickHint);center.Controls.Add(listHost,0,3);
            center.Controls.Add(_inlineHint,0,2);
            _timeline.Paint+=(sender,e)=>DrawTimeline(e.Graphics);
            _timeline.Resize+=(sender,e)=>_timeline.Invalidate();
            _timeline.MouseDown+=(sender,e)=> {if(e.Button==MouseButtons.Left&&!_busy&&!_quick){_timeline.Focus();_hitLane=Math.Max(0,Math.Min(2,(e.Y-(Font.Height+2)*2-2)/(Font.Height+4)));SelectTimelineTime(TimeFromPixel(e.X));}};
            _timeline.KeyDown+=OnTimelineKeyDown;
            _tips.SetToolTip(_timeline,"点击时间条预览相机并选中镜头；灰色区保持上一视角。此操作不跳转机器人仿真进度。Delete 删除选中镜头。");
            _tips.SetToolTip(_list,"点击镜头定位时间条；Delete 删除；Ctrl+↑/↓或拖放调序。只有顺序镜头的时间随排序改变。");
            center.Controls.Add(_timeline,0,4);
            var transport=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(4)};
            transport.WrapContents=false;
            _playButton=AddIconButton(transport,TransportIcon.PlayShot,"预览选中镜头","只移动相机，不推进机器人仿真",()=> {if(Selected==null)return;ShotPlan.Prepare(Selected,_shots).Evaluate(0);_singlePaused=false;SetPreviewPaused(false);_previewClock.Restart();_previewTimer.Start();UpdateEnabled();});
            _simulationButton=AddIconButton(transport,TransportIcon.PlayOperation,"预演整段操作","播放完整仿真与镜头编排，不生成视频",()=> {Restore();SimulationRequested?.Invoke();});
            transport.Controls.Add(new Label {AutoSize=false,Width=1,Height=22,BackColor=Color.FromArgb(207,218,228),Margin=new Padding(6,4,6,0)});
            _pauseButton=AddIconButton(transport,TransportIcon.Pause,"暂停","暂停或继续当前预览",()=> {
                if(_simulationRunning){PauseRequested?.Invoke();return;}
                if(_previewTimer.Enabled){_previewTimer.Stop();_previewClock.Stop();_singlePaused=true;}
                else if(_singlePaused){_previewClock.Start();_previewTimer.Start();_singlePaused=false;}
                SetPreviewPaused(_singlePaused);UpdateEnabled();
            },true);
            _stopButton=AddIconButton(transport,TransportIcon.Stop,"停止","停止播放并保留当前仿真位置，不退带",()=> {_previewTimer.Stop();_previewClock.Stop();_singlePaused=false;SetPreviewPaused(false);StopRequested?.Invoke();UpdateEnabled();},true);
            _restoreButton=AddIconButton(transport,TransportIcon.RestoreView,"恢复视角","恢复预览前相机，不改变机器人位置",()=> {bool own=_saved!=null;Restore();if(!own)RestoreViewRequested?.Invoke();UpdateEnabled();});
            transport.Controls.Add(_transportTime);
            center.Controls.Add(transport,0,5);
            _preview.AutoSize=false;_preview.Height=36;center.Controls.Add(_preview,0,6);
            var inspector=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,BackColor=Color.White};
            inspector.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,34));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            inspector.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            _inspectorHeading=Heading("03  镜头属性");inspector.Controls.Add(_inspectorHeading,0,0);
            _instruction=new Label {Dock=DockStyle.Fill,Padding=new Padding(10,6,8,0),AutoEllipsis=true,ForeColor=Color.FromArgb(91,110,125),Text="先在左侧拾取操作，再添加镜头。"};
            inspector.Controls.Add(_instruction,0,1);
            var scroll=new Panel {Dock=DockStyle.Fill,AutoScroll=true};
            var fields=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(6)};
            _propertyScroll=scroll;_fields=fields;
            PluginUiPolicy.AllowListScrolling(scroll);
            scroll.SizeChanged+=(sender,e)=>RefreshPropertyScroll();
            fields.Layout+=(sender,e)=>RefreshPropertyScroll();
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            AddField(fields,"镜头名称",_name);AddField(fields,"状态",_enabled);AddField(fields,"镜头方式",_kind);
            AddField(fields,"开始条件",_trigger);AddField(fields,"开始时间 / s",_start);AddField(fields,"Location 序号",_location);AddField(fields,"持续时长 / s",_duration);
            AddField(fields,"起始视角",_startView);AddField(fields,"焦点来源",_focusMode);AddField(fields,"焦点对象",_object);AddField(fields,"焦点位置",_geometryCenter);AddField(fields,"世界焦点坐标",_point);
            var advanced=new FlowLayoutPanel {AutoSize=true};
            _motionToggle=new CheckBox {Text="更多运动参数",AutoSize=true};
            _motionToggle.CheckedChanged+=(sender,e)=>UpdateEnabled();advanced.Controls.Add(_motionToggle);AddField(fields,"运动设置",advanced);_motionRow=advanced;
            AddField(fields,"旋转轴",_axis);AddField(fields,"半径 / mm",_radius);AddField(fields,"轴向高度 / mm",_height);AddField(fields,"起始角度 / °",_startAngle);AddField(fields,"旋转角度 / °",_angle);AddField(fields,"结束距离 / mm",_distance);AddField(fields,"速度曲线",_smooth);
            scroll.Controls.Add(fields);
            _quickSettings=new Label {Dock=DockStyle.Fill,Padding=new Padding(10),ForeColor=Color.DimGray,Text="快速录像使用完整操作时长。\n\n视角按高级设置决定是否自动聚焦。\n\n编码、分辨率与输出倍速可在底部高级设置中调整。"};
            scroll.Controls.Add(_quickSettings);inspector.Controls.Add(scroll,0,2);
            var capture=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(6)};
            AddButton(capture,"记录当前视角为起点",()=>CaptureCamera(false));
            _captureStart=(Button)capture.Controls[capture.Controls.Count-1];
            AddButton(capture,"记录终点视角",()=>CaptureCamera(true));
            _captureEnd=(Button)capture.Controls[capture.Controls.Count-1];
            inspector.Controls.Add(capture,0,3);
            _status.AutoSize=false;_status.AutoEllipsis=true;_status.Dock=DockStyle.Fill;_status.Padding=new Padding(6);_status.ForeColor=Color.DimGray;
            inspector.Controls.Add(_status,0,4);
            root.Controls.Add(center,0,0);root.Controls.Add(inspector,1,0);Controls.Add(root);
            PickFocus.Wire(_object); PickFocus.Wire(_point);
            _object.Picked+=(sender,e)=>ReadObject();
            _object.TypeValid+=(s,e)=>ReadObject();
            _object.TypeInvalid+=(s,e)=> {if(!_loading&&Selected!=null){Selected.FocusObject=null;Selected.Focus=null;_list.Invalidate();UpdateEnabled();}};
            _point.ValidFrameSet+=(s,e)=> {if(!_loading&&Selected!=null){Selected.Focus=CameraShot.Copy(_point.GetLocation().Translation);Save();}};
            _point.InvalidFrameSet+=(s,e)=> {if(!_loading&&Selected!=null){Selected.Focus=null;_list.Invalidate();UpdateEnabled();}};
            _list.SelectedIndexChanged+=(s,e)=>LoadSelected();
            foreach(var c in new ComboBox[]{_kind,_trigger,_axis,_focusMode}) c.SelectedIndexChanged+=(s,e)=>Save();
            foreach(var c in new NumericUpDown[]{_start,_duration,_location,_angle,_distance}) c.ValueChanged+=(s,e)=>Save();
            foreach(var c in new NumericUpDown[]{_radius,_height,_startAngle}) c.ValueChanged+=(s,e)=>{if(_loading||Selected==null)return;Selected.Radius=(double)_radius.Value;Selected.Height=(double)_height.Value;Selected.StartAngle=(double)_startAngle.Value;Selected.OrbitInitialized=true;_list.Invalidate();Changed?.Invoke();};
            _startView.SelectedIndexChanged+=(sender,e)=>{if(_loading||Selected==null)return;Selected.InheritStart=_startView.SelectedIndex==1;UpdateEnabled();Changed?.Invoke();};
            _geometryCenter.CheckedChanged+=(sender,e)=>{if(_loading||Selected==null)return;Selected.GeometryCenter=_geometryCenter.Checked;try{if(Selected.FocusObject!=null)Selected.CaptureFocus();}catch(Exception ex){Selected.Focus=null;Report(ex);}UpdateEnabled();_list.Invalidate();Changed?.Invoke();};
            _tips.SetToolTip(_startView,"继承在触发时求值；顺序镜头取前段末态，重叠运动取该时刻构图。首镜头需独立记录。");
            _tips.SetToolTip(_geometryCenter,"使用 SDK 世界包围盒的几何中心，非对象坐标原点；无几何时会提示缺失。");
            _name.TextChanged+=(s,e)=>Save(); _smooth.CheckedChanged+=(s,e)=>Save();
            _enabled.CheckedChanged+=(s,e)=> {if(!_loading&&Selected!=null){Selected.Enabled=_enabled.Checked;_timeline.Invalidate();_list.Invalidate();UpdateEnabled();Changed?.Invoke();}};
            _preview.Scroll+=(s,e)=> { _previewTimer.Stop(); PreviewAt(_preview.Value/1000.0); };
            _previewTimer.Tick+=(s,e)=> {if(Selected==null){_previewTimer.Stop();return;}double t=Math.Min(1,_previewClock.Elapsed.TotalSeconds/Selected.Duration);_preview.Value=(int)(1000*t);PreviewAt(t);if(t>=1){_previewTimer.Stop();UpdateEnabled();}};
            VisibleChanged+=(s,e)=> {if(!Visible)Restore();UpdateEnabled();};
            Disposed+=(s,e)=> {Restore();_previewTimer.Dispose();_tips.Dispose();};
            _tips.SetToolTip(_trigger,"按顺序衔接：首镜头从 0 秒开始，后续接在前一个启用镜头之后；移动顺序会改变时间。指定时间和 Location 保持各自触发条件。");
            _tips.SetToolTip(_mode,"快速录像忽略镜头配置；编排镜头按已启用镜头控制视角。切换方式不会删除配置。");
            _tips.SetToolTip(_preview,"此进度仅预览选中镜头；整段仿真请使用“预演整段操作”。");
            _preview.AccessibleName="选中镜头预览进度";_timeline.AccessibleName="操作时间轴";
            UpdateEnabled();
        }
        private static RadioButton ModeButton(string text)
        {
            var button=new RadioButton{Text=text,Appearance=Appearance.Button,AutoSize=false,Size=new Size(74,26),FlatStyle=FlatStyle.Flat,TextAlign=ContentAlignment.MiddleCenter,Margin=Padding.Empty,AccessibleName=text};
            button.FlatAppearance.BorderSize=1;button.FlatAppearance.BorderColor=Color.FromArgb(211,222,231);return button;
        }
        private void UpdateModeButtons()
        {
            _quickModeButton.Checked=_quick;_composeModeButton.Checked=!_quick;
            foreach(var button in new[]{_quickModeButton,_composeModeButton})
            {
                button.Enabled=_shots!=null&&!_busy;
                button.BackColor=button.Checked?Color.FromArgb(0,113,130):Color.White;
                button.ForeColor=button.Checked?Color.White:Color.FromArgb(72,93,110);
                button.FlatAppearance.CheckedBackColor=Color.FromArgb(0,113,130);
                button.FlatAppearance.MouseOverBackColor=button.Checked?Color.FromArgb(0,99,116):Color.FromArgb(236,245,249);
            }
        }
        private Label Heading(string text)
        {
            var label=new Label{Text=text,Dock=DockStyle.Fill,Padding=new Padding(10,0,10,0),TextAlign=ContentAlignment.MiddleLeft,BackColor=Color.White,ForeColor=Color.FromArgb(30,65,85)};
            Control fontOwner=null;Font headingFont=null;
            EventHandler refreshFont=(sender,e)=>{if(label.IsDisposed)return;var next=new Font(fontOwner==null?Font:fontOwner.Font,FontStyle.Bold);var previous=headingFont;headingFont=next;label.Font=next;previous?.Dispose();};
            label.ParentChanged+=(sender,e)=>{if(fontOwner!=null)fontOwner.FontChanged-=refreshFont;fontOwner=label.Parent;if(fontOwner!=null)fontOwner.FontChanged+=refreshFont;refreshFont(sender,e);};
            label.Disposed+=(sender,e)=>{if(fontOwner!=null)fontOwner.FontChanged-=refreshFont;headingFont?.Dispose();};
            label.Paint+=(sender,e)=>{using(var pen=new Pen(Color.FromArgb(223,232,238)))e.Graphics.DrawLine(pen,0,label.Height-1,label.Width,label.Height-1);};return label;
        }
        private void AddShot()
        {
            if(_shots==null)return;
            var previous=_shots.LastOrDefault(x=>x.Enabled);
            var shot=new CameraShot {Name="镜头 "+(_shots.Count+1),Start=previous!=null&&previous.Trigger==ShotTrigger.Time?previous.Start+previous.Duration:0,Trigger=previous!=null&&previous.Trigger!=ShotTrigger.Time?ShotTrigger.Previous:ShotTrigger.Time};
            shot.Kind=(ShotKind)_addKind.SelectedIndex;shot.Name=new[]{"定点","环绕","推拉","两点移动","跟随资源"}[(int)shot.Kind]+" "+(_shots.Count+1);
            shot.InheritStart=previous!=null;shot.Trigger=ShotTrigger.Previous;shot.FocusSource=shot.Kind==ShotKind.Orbit||shot.Kind==ShotKind.Dolly?(previous!=null?0:1):shot.Kind==ShotKind.Follow?2:0;
            _quick=false;_loading=true;_mode.SelectedIndex=1;_loading=false;
            _shots.Add(shot);RefreshList(shot);Changed?.Invoke();
        }
        private void DuplicateShot()
        {
            var s=Selected;if(s==null)return;
            var copy=s.Clone();copy.Name=s.Name+" 副本";copy.Trigger=ShotTrigger.Previous;
            _shots.Insert(_shots.IndexOf(s)+1,copy);RefreshList(copy);Changed?.Invoke();
        }

        private void DrawShot(DrawItemEventArgs e)
        {
            if(e.Index<0)return;var shot=_list.Items[e.Index] as CameraShot;if(shot==null)return;
            int lineHeight=Math.Max(18,Font.Height);
            bool selected=(e.State&DrawItemState.Selected)!=0;
            using(var brush=new SolidBrush(selected?Color.FromArgb(229,243,250):Color.White))e.Graphics.FillRectangle(brush,e.Bounds);
            using(var accent=new SolidBrush(selected?Color.FromArgb(0,113,130):Color.FromArgb(226,234,241)))
            {
                e.Graphics.FillRectangle(accent,e.Bounds.Left,e.Bounds.Top,3,e.Bounds.Height);
                e.Graphics.FillRectangle(accent,e.Bounds.Left+9,e.Bounds.Top+9,29,25);
            }
            TextRenderer.DrawText(e.Graphics,(e.Index+1).ToString("00"),Font,new Rectangle(e.Bounds.Left+9,e.Bounds.Top+9,29,25),selected?Color.White:Color.FromArgb(65,87,105),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            using(var line=new Pen(Color.FromArgb(234,239,244)))e.Graphics.DrawLine(line,e.Bounds.Left+45,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
            string kind=new[]{"定点","环绕焦点","推近 / 拉远","两点移动","跟随资源"}[(int)shot.Kind];
            string issue;_hints.TryGetValue(shot.Id,out issue);string ready=_busy?"播放中，配置已锁定":issue??(shot.InheritStart?"继承前段末态":"独立起点");
            TextRenderer.DrawText(e.Graphics,shot.Name+" · "+kind,Font,new Rectangle(e.Bounds.X+46,e.Bounds.Y+5,e.Bounds.Width-50,lineHeight),Color.FromArgb(27,62,88),TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,shot.ToString().Substring(shot.Name.Length)+"  /  "+(shot.Enabled?ready:"已停用"),Font,new Rectangle(e.Bounds.X+46,e.Bounds.Y+5+lineHeight,e.Bounds.Width-50,lineHeight),issue==null?Color.DimGray:Color.Firebrick,TextFormatFlags.EndEllipsis);
            if(_dropIndex==e.Index||(_dropIndex==_list.Items.Count&&e.Index==_list.Items.Count-1))
            {int y=_dropIndex==e.Index?e.Bounds.Top:e.Bounds.Bottom-2;using(var pen=new Pen(Color.FromArgb(20,105,166),2))e.Graphics.DrawLine(pen,e.Bounds.Left,y,e.Bounds.Right,y);}
        }
        private static void AddToolDivider(FlowLayoutPanel panel)
        {panel.Controls.Add(new Label{AutoSize=false,Width=1,Height=22,BackColor=Color.FromArgb(211,222,231),Margin=new Padding(4,4,4,0)});}
        private TransportIconButton AddIconButton(FlowLayoutPanel panel,TransportIcon icon,string name,string hint,Action action,bool allowWhileBusy=false,bool compact=false)
        {
            var button=new TransportIconButton(icon,compact) {Name="transport"+icon,AccessibleName=name,AccessibleDescription=hint};
            if(compact)button.Margin=new Padding(1,2,1,2);
            _tips.SetToolTip(button,name+"："+hint);
            button.Click+=(sender,e)=>{if(!_busy||allowWhileBusy)try{action();}catch(Exception ex){Report(ex);}};
            panel.Controls.Add(button);_editControls.Add(button);return button;
        }
        private void RefreshTransportFeedback()
        {
            if(_playButton==null||_pauseButton==null||_restoreButton==null)return;
            bool paused=_simulationRunning?_wholePaused:_singlePaused;
            _pauseButton.Icon=paused?TransportIcon.Resume:TransportIcon.Pause;
            string pauseName=paused?"继续播放":"暂停播放";
            string pauseHint=(_simulationRunning?"整段操作预演":"选中镜头预览")+(paused?"已暂停，点击继续":"播放中可暂停");
            if(_pauseButton.AccessibleName!=pauseName||_pauseButton.AccessibleDescription!=pauseHint)
            {
                _pauseButton.AccessibleName=pauseName;_pauseButton.AccessibleDescription=pauseHint;
                _tips.SetToolTip(_pauseButton,pauseName+"："+pauseHint);
            }
            _playButton.Active=_previewTimer.Enabled;
            _simulationButton.Active=_simulationRunning&&!paused&&!_simulationStopping;
            _pauseButton.Active=paused;
            _transportTime.ForeColor=_simulationStopping?Color.FromArgb(174,56,56):paused?Color.FromArgb(143,91,16):Color.FromArgb(35,81,116);
            _stopButton.Active=_simulationStopping;
            _pauseButton.Enabled=(_simulationRunning&&!_simulationStopping)||_previewTimer.Enabled||_singlePaused;
            _stopButton.Enabled=(_simulationRunning&&!_simulationStopping)||_saved!=null||_previewTimer.Enabled;
            _restoreButton.Enabled=!_busy&&(_saved!=null||_restoreAvailable);
            string message;
            if(_simulationStopping)message="正在停止…";
            else if(_simulationRunning)message="整段 "+(_simulationTime??0).ToString("0.00")+(_operationDuration.HasValue?" / "+_operationDuration.Value.ToString("0.00"):"")+" s"+(paused?" · 已暂停":"");
            else if(_busy)message="录像中 · 编辑已锁定";
            else if(_quick)message="整段操作 · 可预演";
            else if(Selected==null)message="选择镜头后预览";
            else if(_selectedIssue!=null)message=_selectedIssue;
            else message="镜头 "+(_preview.Value/1000.0*Selected.Duration).ToString("0.00")+" / "+Selected.Duration.ToString("0.00")+" s"+(_singlePaused?" · 已暂停":"");
            if(_transportTime.Text!=message){_transportTime.Text=message;_transportTime.AccessibleDescription=message;_tips.SetToolTip(_transportTime,message);}
        }
        private void AddButton(FlowLayoutPanel p,string text,Action action,bool allowWhileBusy=false)
        {var primary=text.Contains("添加镜头");var b=new Button {Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(52,26),Padding=new Padding(5,1,5,1),FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(0,113,130):Color.White,ForeColor=primary?Color.White:Color.FromArgb(49,75,96),Margin=new Padding(0,2,6,2),UseVisualStyleBackColor=false};b.FlatAppearance.BorderColor=primary?Color.FromArgb(0,113,130):Color.FromArgb(207,219,229);b.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(0,96,114):Color.FromArgb(235,245,250);b.Click+=(s,e)=>{if(!_busy||allowWhileBusy)try{action();}catch(Exception ex){Report(ex);}};p.Controls.Add(b);_editControls.Add(b);}
        private void AddField(TableLayoutPanel p,string label,Control c)
        {int r=p.RowCount++;p.RowStyles.Add(new RowStyle(SizeType.AutoSize));p.Controls.Add(new Label {Text=label,AutoSize=true,Margin=new Padding(2,5,6,2),Tag=label},0,r);c.Dock=DockStyle.Fill;c.Margin=new Padding(2,2,2,2);p.Controls.Add(c,1,r);_editControls.Add(c);}
        private void ReadObject()
        {if(_loading||Selected==null)return;try{Selected.FocusObject=_object.Object as ITxLocatableObject;if(Selected.FocusObject==null){Selected.Focus=null;Selected.FocusLocal=null;}else Selected.CaptureFocus();Selected.FocusLabel=Selected.FocusObject==null?null:PsReader.GetObjectName(Selected.FocusObject);Save();}catch(Exception ex){UpdateEnabled();Report(ex);Changed?.Invoke();}}
        private bool DeleteSelected()
        {
            var shot=Selected;
            if(_busy||_quick||_dragging||shot==null||_shots==null)return false;
            int index=_shots.IndexOf(shot);if(index<0)return false;
            Restore();_shots.RemoveAt(index);shot.DisposeThumbnails();
            RefreshList(_shots.Count==0?null:_shots[Math.Min(index,_shots.Count-1)]);
            Changed?.Invoke();return true;
        }
        protected override bool ProcessCmdKey(ref Message message,Keys keyData)
        {
            if(_list.ContainsFocus||_timeline.ContainsFocus)
            {
                if((keyData&Keys.KeyCode)==Keys.Delete)
                {if((keyData&Keys.Modifiers)==Keys.None)DeleteSelected();return true;}
                if(keyData==(Keys.Control|Keys.Up)||keyData==(Keys.Control|Keys.Down))
                {MoveShot((keyData&Keys.KeyCode)==Keys.Up?-1:1);return true;}
            }
            return base.ProcessCmdKey(ref message,keyData);
        }
        private void OnListKeyDown(object sender,KeyEventArgs args)
        {
            if(args.KeyCode==Keys.Delete&&args.Modifiers==Keys.None)
            {args.Handled=true;args.SuppressKeyPress=true;DeleteSelected();}
            else if(args.Control&&(args.KeyCode==Keys.Up||args.KeyCode==Keys.Down))
            {args.Handled=true;args.SuppressKeyPress=true;MoveShot(args.KeyCode==Keys.Up?-1:1);}
        }
        private void OnTimelineKeyDown(object sender,KeyEventArgs args)
        {
            if(args.KeyCode==Keys.Delete){OnListKeyDown(sender,args);return;}
            if(_busy||_quick)return;
            double time=_timelineCursor??SelectedRange()?.Start??0;
            if(args.KeyCode==Keys.Left||args.KeyCode==Keys.Right)
                time+=(args.KeyCode==Keys.Left?-1:1)*(args.Control?1:0.1);
            else if(args.KeyCode==Keys.Home)time=0;
            else if(args.KeyCode==Keys.End)time=TimelineMaximum();
            else return;
            args.Handled=true;args.SuppressKeyPress=true;SelectTimelineTime(time);
        }
        private ShotRange SelectedRange()
        {return _shots==null?null:ShotPlan.Ranges(_shots).FirstOrDefault(r=>ReferenceEquals(r.Shot,Selected));}
        private double TimelineMaximum()
        {
            var ranges=_shots==null?new List<ShotRange>():ShotPlan.Ranges(_shots);
            double end=ranges.Where(r=>r.Start.HasValue).Select(r=>r.Start.Value+r.Shot.Duration).DefaultIfEmpty(0).Max();
            return Math.Max(1,Math.Max(_operationDuration??0,end));
        }
        private double TimeFromPixel(int x)
        {return Math.Max(0,Math.Min(1,(x-50.0)/Math.Max(1,_timeline.ClientSize.Width-62)))*TimelineMaximum();}
        private bool SelectTimelineTime(double time)
        {
            if(_busy||_quick||_shots==null)return false;
            _previewTimer.Stop();_previewClock.Stop();_singlePaused=false;SetPreviewPaused(false);
            time=Math.Max(0,Math.Min(TimelineMaximum(),time));
            var known=ShotPlan.Ranges(_shots).Where(r=>r.Start.HasValue&&r.Start.Value<=time+1e-9).OrderBy(r=>r.Start.Value).ToList();
            var range=(_hitLane.HasValue?known.Where(r=>Channel(r.Shot)==_hitLane.Value):known).LastOrDefault();
            if(range==null&&!_hitLane.HasValue)range=known.LastOrDefault();_hitLane=null;
            string previewError=null;
            _timelineSelecting=true;
            try
            {
                _list.SelectedItem=range?.Shot;
                if(range!=null)
                {
                    double fraction=Math.Max(0,Math.Min(1,(time-range.Start.Value)/range.Shot.Duration));
                    _preview.Value=(int)Math.Round(fraction*1000);
                    previewError=ShotPlan.Missing(range.Shot,_shots);
                    if(previewError==null&&!PreviewAt(fraction))previewError=_status.Text;
                }
                else
                {
                    if(_saved!=null)PsReader.SetCurrentCamera(PsReader.GetGraphicViewer(),_saved);
                }
                UpdateEnabled();
                _status.Text=range==null?time.ToString("0.000")+" s · 沿用起始视角，无镜头片段":time.ToString("0.000")+" s · "+range.Shot.Name+(previewError==null?"（仅预览相机）":"："+previewError);
                _timelineCursor=time;_timeline.Invalidate();_list.Invalidate();return range!=null;
            }
            finally{_timelineSelecting=false;}
        }
        private void MoveShot(int delta)
        {
            var shot=Selected;if(shot==null||_shots==null)return;
            int index=_shots.IndexOf(shot),destination=index+delta;
            if(destination<0||destination>=_shots.Count)return;
            ReorderShot(shot.Id,destination+(delta>0?1:0),_shots);
        }
        private bool ReorderShot(Guid id,int insertion,IList<CameraShot> source)
        {
            if(_busy||_quick||_shots==null||!ReferenceEquals(source,_shots))return false;
            var shot=_shots.FirstOrDefault(s=>s.Id==id);if(shot==null)return false;
            int index=_shots.IndexOf(shot);insertion=Math.Max(0,Math.Min(_shots.Count,insertion));
            int target=insertion-(index<insertion?1:0);if(target==index)return false;
            Restore();_shots.RemoveAt(index);_shots.Insert(target,shot);
            RefreshList(shot);Changed?.Invoke();return true;
        }
        private void OnListMouseDown(object sender,MouseEventArgs args)
        {
            _dragShot=null;if(args.Button!=MouseButtons.Left||_busy||_quick)return;
            int index=_list.IndexFromPoint(args.Location);
            if(index<0||index>=_list.Items.Count)return;
            _dragOrigin=args.Location;_dragShot=_list.Items[index] as CameraShot;
        }
        private void OnListMouseMove(object sender,MouseEventArgs args)
        {
            if(_busy||_quick||_dragging||_dragShot==null||args.Button!=MouseButtons.Left)return;
            var size=SystemInformation.DragSize;
            if(new Rectangle(_dragOrigin.X-size.Width/2,_dragOrigin.Y-size.Height/2,size.Width,size.Height).Contains(args.Location))return;
            var payload=new ShotDragData {Owner=this,Plan=_shots,Id=_dragShot.Id};
            Restore();_dragging=true;
            try{var data=new DataObject();data.SetData(typeof(ShotDragData),payload);_list.DoDragDrop(data,DragDropEffects.Move);}
            finally{_dragging=false;_dragShot=null;ClearDropMarker();}
        }
        private ShotDragData GetShotDrag(IDataObject data)
        {
            if(_busy||_quick||data==null||_shots==null)return null;
            ShotDragData payload;
            try{payload=data.GetData(typeof(ShotDragData)) as ShotDragData;}catch{return null;}
            return payload!=null&&ReferenceEquals(payload.Owner,this)&&ReferenceEquals(payload.Plan,_shots)&&_shots.Any(s=>s.Id==payload.Id)?payload:null;
        }
        private int DropIndex(Point point)
        {
            int index=_list.IndexFromPoint(point);
            if(index<0)return point.Y<0?0:_list.Items.Count;
            var row=_list.GetItemRectangle(index);return index+(point.Y>=row.Top+row.Height/2?1:0);
        }
        private void OnShotDragOver(object sender,DragEventArgs args)
        {
            if(GetShotDrag(args.Data)==null){args.Effect=DragDropEffects.None;ClearDropMarker();return;}
            var point=_list.PointToClient(new Point(args.X,args.Y));
            if(point.Y<12&&_list.TopIndex>0)_list.TopIndex--;
            else if(point.Y>_list.ClientSize.Height-12&&_list.TopIndex<_list.Items.Count-1)_list.TopIndex++;
            _dropIndex=DropIndex(point);args.Effect=DragDropEffects.Move;_list.Invalidate();
        }
        private void OnShotDrop(object sender,DragEventArgs args)
        {
            var payload=GetShotDrag(args.Data);
            if(payload!=null)ReorderShot(payload.Id,DropIndex(_list.PointToClient(new Point(args.X,args.Y))),payload.Plan);
            ClearDropMarker();
        }
        private void ClearDropMarker(){_dropIndex=-1;if(!_list.IsDisposed)_list.Invalidate();}
        public void Bind(IList<CameraShot> shots,double? operationDuration=null,bool quick=false,int locationCount=int.MaxValue)
        {Restore();_shots=shots;_operationDuration=operationDuration;_locationCount=locationCount;_quick=quick;_loading=true;_mode.SelectedIndex=quick?0:1;_loading=false;RefreshList(shots==null?null:shots.FirstOrDefault());}
        public void SetPreviewPaused(bool paused){_wholePaused=paused;RefreshTransportFeedback();}
        public void SetRestoreAvailable(bool available){if(_restoreAvailable==available)return;_restoreAvailable=available;RefreshTransportFeedback();}
        public void UpdateSimulationTime(double time)
        {
            _simulationTime=time;
            if(_simulationRunning&&_operationDuration.HasValue&&_operationDuration.Value>0)
                _preview.Value=(int)Math.Max(0,Math.Min(1000,time/_operationDuration.Value*1000));
            _timeline.Invalidate();RefreshTransportFeedback();
        }
        public void SetSimulationState(bool running,double? time=null,bool stopping=false)
        {
            if(running&&!_simulationRunning){_simulationTime=0;_preview.Value=0;}
            if(!running&&_simulationRunning)_timelineCursor=_simulationTime;
            _simulationRunning=running;_simulationStopping=stopping;
            if(time.HasValue)UpdateSimulationTime(time.Value);
            UpdateEnabled();_timeline.Invalidate();
        }

        private void RefreshList(CameraShot selected)
        {_loading=true;_list.Items.Clear();if(_shots!=null)foreach(var s in _shots)_list.Items.Add(s);_list.SelectedItem=selected;_loading=false;LoadSelected();_timeline.Invalidate();}
        private void LoadSelected()
        {
            if(_loading)return;if(!_timelineSelecting)Restore();else{_previewTimer.Stop();_previewClock.Stop();}
            _loading=true;var s=Selected;
            try {if(s!=null){_startView.SelectedIndex=s.InheritStart?1:0;_geometryCenter.Checked=s.GeometryCenter;_enabled.Checked=s.Enabled;_name.Text=s.Name;_kind.SelectedIndex=(int)s.Kind;_trigger.SelectedIndex=(int)s.Trigger;_start.Value=(decimal)s.Start;_duration.Value=(decimal)s.Duration;_location.Value=s.LocationIndex;_axis.SelectedIndex=s.Axis;_angle.Value=(decimal)s.Angle;_distance.Value=(decimal)s.EndDistance;_smooth.Checked=s.Smooth;_focusMode.SelectedIndex=s.FocusSource;_object.Object=s.FocusObject;if(s.Focus==null)_point.Text=string.Empty;else _point.SetLocation(new TxTransformation(s.Focus,new TxVector(0,0,0)));_preview.Value=0;_status.Text=(s.From==null?"起始视角未记录":"起始视角已记录")+(s.Kind==ShotKind.Move?"；"+(s.To==null?"终点未记录":"终点已记录"):"；调整参数后可播放预览");}}
            finally{_loading=false;LoadOrbitFields();UpdateEnabled();if(!_timelineSelecting)_timelineCursor=SelectedRange()?.Start;_timeline.Invalidate();}
        }
        private void Save()
        {if(_loading||Selected==null||_busy)return;var s=Selected;s.Name=_name.Text;s.Kind=(ShotKind)_kind.SelectedIndex;if(s.Kind==ShotKind.Follow){_loading=true;_focusMode.SelectedIndex=2;_loading=false;}s.Trigger=(ShotTrigger)_trigger.SelectedIndex;s.Start=(double)_start.Value;s.Duration=(double)_duration.Value;s.LocationIndex=(int)_location.Value;s.Axis=_axis.SelectedIndex;s.Angle=(double)_angle.Value;s.EndDistance=(double)_distance.Value;s.Smooth=_smooth.Checked;s.Follow=_focusMode.SelectedIndex==2;
            if(s.FocusSource!=_focusMode.SelectedIndex){s.Focus=null;s.FocusObject=null;_loading=true;try{_object.Object=null;_point.Text=string.Empty;}finally{_loading=false;}s.FocusSource=_focusMode.SelectedIndex;}
            _loading=true;try{_list.Items[_list.SelectedIndex]=s;}finally{_loading=false;}_list.Invalidate();_timeline.Invalidate();UpdateEnabled();Changed?.Invoke();}
        private void CaptureCamera(bool end)
        {var s=Selected;if(s==null)return;var c=CameraShot.Snapshot(PsReader.GetCurrentCamera(PsReader.GetGraphicViewer()));if(c==null)throw new InvalidOperationException("无法获取当前视角");if(end)s.To=c;else{s.From=c;s.InheritStart=false;_loading=true;_startView.SelectedIndex=0;_loading=false;if(s.Kind==ShotKind.Follow){if(s.FocusObject==null||!s.FocusObject.IsValid())throw new InvalidOperationException("请先选取跟随资源");s.CaptureFocus();}if(s.Kind==ShotKind.Orbit){s.InitializeOrbit();LoadOrbitFields();}}_status.Text=end?"终点已记录，可以播放预览":s.Kind==ShotKind.Move?"起点已记录，请调整视角后记录终点":s.Kind==ShotKind.Orbit?"起点已记录，环绕参数已初始化，可以播放预览":"起点已记录，可以播放预览";_list.Invalidate();
            try{var image=PsReader.GetGraphicViewer().GetImage(new Size(192,108),false);if(end){s.ToThumbnail?.Dispose();s.ToThumbnail=image;}else{s.FromThumbnail?.Dispose();s.FromThumbnail=image;}}catch(Exception ex){_status.Text="视角已记录，缩略图获取失败："+ex.Message;}
            UpdateEnabled();Changed?.Invoke();}
        private void LoadOrbitFields()
        {if(Selected==null)return;_loading=true;try{_radius.Value=Math.Max(_radius.Minimum,Math.Min(_radius.Maximum,(decimal)Selected.Radius));_height.Value=Math.Max(_height.Minimum,Math.Min(_height.Maximum,(decimal)Selected.Height));_startAngle.Value=(decimal)Selected.StartAngle;}finally{_loading=false;}}
        public void SetBusy(bool busy){if(busy&&!_busy)Restore();_busy=busy;UpdateEnabled();}
        private void UpdateEnabled()
        {
            _hints.Clear();
            if(!_busy&&_shots!=null){var conflicts=ShotPlan.Conflicts(_shots);foreach(var shot in _shots.Where(s=>s.Enabled))_hints[shot.Id]=ShotPlan.Hint(shot,_shots,_operationDuration,_locationCount,conflicts);}
            bool showing=Selected!=null&&!_quick;
            bool selected=showing&&!_busy;
            foreach(var c in _editControls)c.Enabled=selected;
            foreach(var button in _editControls.OfType<Button>().Where(b=>b.Text.Contains("添加镜头")))button.Enabled=_shots!=null&&!_busy;
            _fields.Visible=!_quick;_quickSettings.Visible=_quick;_quickHint.Visible=_quick;_list.Visible=!_quick;
            if(_quick){_quickSettings.BringToFront();_quickHint.BringToFront();}
            _quickHint.Text="快速录像只需选择操作和输出位置。\n\n当前操作保存的 "+(_shots?.Count??0)+" 个镜头会保留，本次不应用。\n\n需要镜头运动时，可选择类型后点击添加，自动进入编排模式。";
            _captureStart.Parent.Visible=!_quick;_inspectorHeading.Text=_quick?"03  快速录像":"03  镜头属性";
            _mode.Enabled=_shots!=null&&!_busy;UpdateModeButtons();
            _addKind.Enabled=_shots!=null&&!_busy;
            foreach(var button in _planTools)button.Enabled=_shots!=null&&!_busy;
            _moveUpButton.Enabled=selected&&_shots.IndexOf(Selected)>0;
            _moveDownButton.Enabled=selected&&_shots.IndexOf(Selected)<_shots.Count-1;
            _simulationButton.Enabled=_shots!=null&&!_busy&&(_quick||ShotPlan.Issues(_shots,_operationDuration,_locationCount).Count==0);
            _pauseButton.Enabled=(_simulationRunning&&!_simulationStopping)||_previewTimer.Enabled||_singlePaused;_stopButton.Enabled=(_simulationRunning&&!_simulationStopping)||_saved!=null||_previewTimer.Enabled;
            _list.Enabled=!_busy&&!_quick;
            _timeline.Enabled=!_busy&&!_quick;_timeline.Cursor=_timeline.Enabled?Cursors.Hand:Cursors.Default;
            _selectedIssue=selected?(!Selected.Enabled?"已停用":_hints.ContainsKey(Selected.Id)?_hints[Selected.Id]:null):null;
            _preview.Enabled=selected&&_selectedIssue==null;
            _summary.Text=_busy?(_simulationStopping?"正在停止…":"播放中，配置已锁定"):_quick?"选输出后可直接录制":_shots==null?"未选择操作":ShotPlan.Summary(_shots,_operationDuration);
            _summary.MaximumSize=Size.Empty;
            if(_quick)_status.Text="快速录像不应用镜头编排；已有配置会保留。";
            if(Selected==null)_status.Text=_shots==null?"未选择操作":_shots.Count==0?"还没有镜头。也可直接录制，使用默认视角。":"请选择镜头或点击时间条";
            _start.Enabled=selected&&_trigger.SelectedIndex==0;_location.Enabled=selected&&_trigger.SelectedIndex==1;
            bool focus=showing&&(_kind.SelectedIndex==1||_kind.SelectedIndex==2||_kind.SelectedIndex==4);
            _object.Enabled=selected&&focus&&_focusMode.SelectedIndex!=0;_point.Enabled=selected&&focus&&_focusMode.SelectedIndex==0;
            var objectLabel=_fields.GetControlFromPosition(0,_fields.GetRow(_object)) as Label;
            if(objectLabel!=null)objectLabel.Text=_kind.SelectedIndex==4?"跟随资源":"焦点对象";
            _axis.Enabled=_angle.Enabled=selected&&_kind.SelectedIndex==1;_distance.Enabled=selected&&_kind.SelectedIndex==2;
            _radius.Enabled=_height.Enabled=_startAngle.Enabled=selected&&_kind.SelectedIndex==1;
            ShowField(_geometryCenter,focus&&_focusMode.SelectedIndex!=0);ShowField(_focusMode,focus&&_kind.SelectedIndex!=4);ShowField(_object,focus&&_focusMode.SelectedIndex!=0);ShowField(_point,focus&&_focusMode.SelectedIndex==0);
            ShowField(_startView,showing);_captureStart.Text=Selected!=null&&Selected.InheritStart?"改用当前视角为起点":"记录当前视角为起点";
            _captureStart.Enabled=selected;_captureEnd.Visible=Selected!=null&&Selected.Kind==ShotKind.Move;
            _instruction.Text=Selected==null?(_shots==null?"① 先在左侧拾取操作":"② 添加镜头，开始配置"):(focus?"选焦点 → 记录视角 → 预览":Selected.Kind==ShotKind.Move?"记录起点、终点 → 预览":"调整视角 → 记录起点 → 预览");
            ShowField(_motionRow,showing&&_kind.SelectedIndex==1);
            _list.Invalidate();
            ShowField(_axis,showing&&_kind.SelectedIndex==1&&_motionToggle.Checked);
            foreach(var c in new Control[]{_radius,_height,_startAngle})ShowField(c,showing&&_kind.SelectedIndex==1&&_motionToggle.Checked&&!Selected.InheritStart);
            ShowField(_smooth,Selected!=null&&Selected.Kind!=ShotKind.Fixed&&Selected.Kind!=ShotKind.Follow&&!_quick);
            ShowField(_angle,showing&&_kind.SelectedIndex==1);
            ShowField(_distance,showing&&_kind.SelectedIndex==2);ShowField(_start,showing&&_trigger.SelectedIndex==0);ShowField(_location,showing&&_trigger.SelectedIndex==1);
            PickFocus.SetSuspended(_object,!Visible||!_object.Enabled);PickFocus.SetSuspended(_point,!Visible||!_point.Enabled);
            _playButton.Enabled=selected&&_selectedIssue==null;
            if(Selected!=null&&!_quick&&!_busy)
                _status.Text=_selectedIssue==null?"已就绪：预览镜头或预演整段操作":"下一步："+_selectedIssue;
            if(Selected!=null&&Selected.FocusSource>0&&Selected.FocusObject==null&&!string.IsNullOrEmpty(Selected.FocusLabel))_instruction.Text="请重新拾取焦点："+Selected.FocusLabel;
            if(Selected!=null&&Selected.Kind==ShotKind.Follow)_instruction.Text="在 PS 中选移动资源 → 记录起点 → 整段预演";
            if(Selected!=null&&Selected.InheritStart)_instruction.Text=Selected.Kind==ShotKind.Follow?"在 PS 选资源 → 继承起点 → 整段预演":Selected.FocusSource==0&&Selected.Focus==null&&focus?"沿用前段焦点与构图 → 设置参数 → 预览":"自动继承起点 → 设置参数 → 预览";
            if(_quick)_instruction.Text="快速录像：选择操作 → 选择输出 → 开始录制";
            if(_busy)_instruction.Text="预演或录制中；停止后继续编辑";
            if(_busy)_status.Text=_simulationStopping?"正在停止预演，请稍候…":"播放期间已锁定编辑；停止后保留当前位置";
            UpdateVisualGuidance();RefreshPropertyScroll();RefreshTransportFeedback();
        }
        private void UpdateVisualGuidance()
        {
            int count=_hints.Values.Count(v=>v!=null);
            _inlineHint.Text=_quick?"快速录像：无需设置镜头":count>0?"● "+count+" 个镜头待补全 · "+(_selectedIssue??_hints.Values.First(v=>v!=null)):"✓ 同焦点的旋转＋推拉可重叠；后段默认继承前段末态";
            _inlineHint.ForeColor=count>0&&!_quick?Color.Firebrick:Color.FromArgb(0,106,115);
            _tips.SetToolTip(_inlineHint,_inlineHint.Text);
            foreach(Control field in _fields.Controls)
            {
                var label=field as Label;if(label!=null)label.ForeColor=Color.FromArgb(45,63,78);
                if(field is TextBox||field is ComboBox||field is NumericUpDown)field.BackColor=SystemColors.Window;
            }
            if(Selected==null||_busy||_quick)return;
            bool needsFocus=Selected.Kind==ShotKind.Orbit||Selected.Kind==ShotKind.Dolly||Selected.Kind==ShotKind.Follow;
            if(needsFocus)
            {
                if(Selected.FocusSource>0&&(Selected.FocusObject==null||Selected.Focus==null))WarnField(_object);
                if(Selected.FocusSource==0&&Selected.Focus==null&&!Selected.InheritStart)WarnField(_point);
                if(Selected.GeometryCenter&&Selected.FocusObject!=null&&Selected.Focus==null)WarnField(_geometryCenter);
            }
            if(Selected.From==null&&!Selected.InheritStart)WarnField(_startView);
            if(Selected.InheritStart&&_selectedIssue!=null&&(_selectedIssue.Contains("前段")||_selectedIssue.Contains("首镜头")))WarnField(_startView);
            if(Selected.Kind==ShotKind.Move&&Selected.To==null)_captureEnd.BackColor=Color.MistyRose;else _captureEnd.BackColor=SystemColors.Control;
            _captureStart.BackColor=Selected.From==null&&!Selected.InheritStart?Color.MistyRose:SystemColors.Control;
            if(_selectedIssue!=null&&(_selectedIssue.Contains("冲突")||_selectedIssue.Contains("时长"))){WarnField(_start);WarnField(_duration);}
            _status.ForeColor=_selectedIssue==null?Color.FromArgb(0,106,115):Color.Firebrick;
            _status.Text=_selectedIssue==null?(Selected.InheritStart?"✓ 起点自动继承前段实际末态":"✓ 独立起点已就绪"):"● "+_selectedIssue;
            _tips.SetToolTip(_status,_status.Text);
        }
        private void WarnField(Control field)
        {
            var label=_fields.GetControlFromPosition(0,_fields.GetRow(field));if(label!=null)label.ForeColor=Color.Firebrick;
            if(field is TextBox||field is ComboBox||field is NumericUpDown)field.BackColor=Color.FromArgb(255,238,235);
            _tips.SetToolTip(field,_selectedIssue??"请补全此项");
        }
        private static int Channel(CameraShot shot){return shot.Kind==ShotKind.Orbit?1:shot.Kind==ShotKind.Dolly?2:0;}
        private void RefreshPropertyScroll()
        {
            if(_layingOutProperties||_fields==null||_propertyScroll==null||_propertyScroll.IsDisposed)return;
            if(_quick){_propertyScroll.AutoScrollMinSize=Size.Empty;return;}
            _layingOutProperties=true;
            try
            {
                int width=Math.Max(1,_propertyScroll.ClientSize.Width-SystemInformation.VerticalScrollBarWidth);
                int height=_fields.GetPreferredSize(new Size(width,0)).Height;
                _propertyScroll.AutoScrollMinSize=new Size(0,height);
            }
            finally{_layingOutProperties=false;}
        }
        private static void ShowField(Control c,bool show)
        {var table=c.Parent as TableLayoutPanel;if(table==null)return;int row=table.GetRow(c);var label=table.GetControlFromPosition(0,row);c.Visible=show;if(label!=null)label.Visible=show;}
        private void Report(Exception ex){_status.Text=ex.Message;}
        private bool PreviewAt(double t)
        {
            try
            {
                if(Selected==null||_busy)return false;
                var viewer=PsReader.GetGraphicViewer();if(viewer==null)throw new InvalidOperationException("无法获取主视口");
                if(_saved==null)_saved=CameraShot.Snapshot(PsReader.GetCurrentCamera(viewer));
                _timelineCursor=SelectedRange()?.Start+t*Selected.Duration;
                _timeline.Invalidate();_list.Invalidate();
                if(!PsReader.SetCurrentCamera(viewer,ShotPlan.Preview(_shots,Selected,t)))throw new InvalidOperationException("应用预览视角失败");
                RefreshTransportFeedback();return true;
            }
            catch(Exception ex){_previewTimer.Stop();Report(ex);RefreshTransportFeedback();return false;}
        }
        private void DrawTimeline(Graphics g)
        {
            if(_shots==null){g.DrawString("先在当前 PS 工程选取操作",Font,Brushes.Gray,6,6);return;}
            var ranges=ShotPlan.Ranges(_shots);var known=ranges.Where(r=>r.Start.HasValue).ToList();
            double max=Math.Max(1,Math.Max(_operationDuration??0,known.Count==0?0:known.Max(r=>r.Start.Value+r.Shot.Duration)));
            float left=50,width=Math.Max(1,_timeline.Width-left-12),line=Font.Height+2,lane=Font.Height+4,y=line*2+2;
            g.DrawString(_quick?"完整操作进度":"时间 / s · 同焦点旋转＋推拉可叠加",Font,Brushes.DimGray,4,0);
            for(int i=0;i<=4;i++){float x=left+width*i/4;g.DrawString((max*i/4).ToString("0.#"),Font,Brushes.DimGray,Math.Min(x,_timeline.Width-36),line);}
            for(int i=0;i<3;i++)
            {
                float row=y+i*lane;g.DrawString(new[]{"视角","旋转","推拉"}[i],Font,Brushes.DimGray,4,row);
                using(var back=new SolidBrush(Color.FromArgb(237,242,246)))g.FillRectangle(back,left,row,width,lane-2);
            }
            if(!_quick)foreach(var range in known)
            {
                float x=left+(float)(range.Start.Value/max)*width,w=Math.Max(2,(float)(range.Shot.Duration/max)*width),row=y+Channel(range.Shot)*lane;
                string issue;_hints.TryGetValue(range.Shot.Id,out issue);
                using(var brush=new SolidBrush(issue!=null?Color.IndianRed:range.Shot==Selected?Color.FromArgb(0,118,139):Color.FromArgb(136,176,198)))g.FillRectangle(brush,x,row,w,lane-2);
                if(w>22)g.DrawString((_shots.IndexOf(range.Shot)+1).ToString("00"),Font,Brushes.White,x+2,row);
            }
            double? cursor=_simulationRunning?_simulationTime:_timelineCursor;
            if(cursor.HasValue){float x=left+(float)(Math.Min(max,cursor.Value)/max)*width;g.DrawLine(Pens.Blue,x,line,x,y+lane*3);g.FillPolygon(Brushes.Blue,new[]{new PointF(x-4,line),new PointF(x+4,line),new PointF(x,line+5)});}
            var events=ranges.Where(r=>!r.Start.HasValue).Select(r=>r.Shot.Name).ToList();
            g.DrawString(events.Count>0?"事件触发："+string.Join("、",events):"空白区保持末态 · 点击轨道互选镜头",Font,Brushes.DimGray,new RectangleF(4,y+lane*3+2,_timeline.Width-8,line*2));
        }
        public void Restore(){_previewTimer.Stop();_previewClock.Stop();_singlePaused=false;if(_pauseButton!=null)SetPreviewPaused(false);_simulationTime=null;_timelineCursor=null;if(_saved==null)return;try{PsReader.SetCurrentCamera(PsReader.GetGraphicViewer(),_saved);}catch{}finally{_saved=null;}}
    }
}
