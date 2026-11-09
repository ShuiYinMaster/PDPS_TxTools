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
        private readonly ComboBox _addKind=Combo("定点","环绕焦点","推近 / 拉远","两点移动");
        private readonly Label _summary=new Label {AutoSize=true,ForeColor=Color.DimGray,Margin=new Padding(8,8,0,0)};
        private double? _operationDuration;
        private int _locationCount=int.MaxValue;
        private double? _simulationTime;
        private bool _quick;
        private double? _timelineCursor;
        private bool _timelineSelecting;
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

        private Panel _guide;
        private Label _instruction;
        private CheckBox _motionToggle;
        private Control _motionRow;
        private Button _captureStart, _captureEnd;
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
        private readonly ComboBox _kind=Combo("定点","环绕焦点","推近 / 拉远","两点移动");
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
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Percent,48));
            center.RowStyles.Add(new RowStyle(SizeType.Percent,52));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,84));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Absolute,30));
            var modeRow=new FlowLayoutPanel {Dock=DockStyle.Fill,WrapContents=false,BackColor=Color.FromArgb(224,234,243)};
            modeRow.Controls.Add(new Label {Text="2  录像方式",AutoSize=true,Margin=new Padding(6,7,6,0)});
            _mode.Width=112;modeRow.Controls.Add(_mode);modeRow.Controls.Add(_summary);
            center.Controls.Add(modeRow,0,0);
            _mode.SelectedIndexChanged+=(sender,e)=> {if(_loading)return;Restore();_quick=_mode.SelectedIndex==0;UpdateEnabled();Changed?.Invoke();};
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Padding=new Padding(4)};
            _addKind.Width=100;buttons.Controls.Add(_addKind);
            AddButton(buttons,"＋ 添加镜头",()=>AddShot());
            var files=new ContextMenuStrip();
            files.Items.Add("保存当前操作配置…",null,(sender,e)=>SaveRequested?.Invoke());
            files.Items.Add("载入配置…",null,(sender,e)=>LoadRequested?.Invoke());
            files.Items.Add("复制到其他已选操作…",null,(sender,e)=>CopyRequested?.Invoke());
            AddButton(buttons,"配置 ▾",()=>files.Show(buttons,new Point(0,buttons.Height)));
            buttons.Disposed+=(sender,e)=>files.Dispose();
            var edit=new ContextMenuStrip();
            edit.Items.Add("复制镜头",null,(sender,e)=>DuplicateShot());
            edit.Items.Add("删除镜头（Delete）",null,(sender,e)=>DeleteSelected());
            edit.Items.Add("上移（顺序镜头时间随排序改变）",null,(sender,e)=>MoveShot(-1));
            edit.Items.Add("下移（指定时间保持不变）",null,(sender,e)=>MoveShot(1));
            AddButton(buttons,"编辑 ▾",()=>edit.Show(buttons,new Point(0,buttons.Height)));
            buttons.Disposed+=(sender,e)=>edit.Dispose();
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
            listHost.Controls.Add(_list);listHost.Controls.Add(_quickHint);center.Controls.Add(listHost,0,2);
            var guide=new BufferedCanvas {Dock=DockStyle.Fill,BackColor=Color.FromArgb(237,243,248),Margin=new Padding(0,6,0,4)};
            guide.Paint+=(sender,e)=>DrawGuide(e.Graphics,guide.ClientRectangle);
            guide.Resize+=(sender,e)=>guide.Invalidate();
            _guide=guide;
            center.Controls.Add(guide,0,3);
            _timeline.Paint+=(sender,e)=>DrawTimeline(e.Graphics);
            _timeline.Resize+=(sender,e)=>_timeline.Invalidate();
            _timeline.MouseDown+=(sender,e)=> {if(e.Button==MouseButtons.Left&&!_busy&&!_quick){_timeline.Focus();SelectTimelineTime(TimeFromPixel(e.X));}};
            _timeline.KeyDown+=OnTimelineKeyDown;
            _tips.SetToolTip(_timeline,"点击时间条预览相机并选中镜头；灰色区保持上一视角。此操作不跳转机器人仿真进度。Delete 删除选中镜头。");
            _tips.SetToolTip(_list,"点击镜头定位时间条；Delete 删除；Ctrl+↑/↓或拖放调序。只有顺序镜头的时间随排序改变。");
            center.Controls.Add(_timeline,0,4);
            var transport=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,Padding=new Padding(4)};
            transport.WrapContents=false;
            _playButton=AddIconButton(transport,TransportIcon.PlayShot,"预览选中镜头","只移动相机，不推进机器人仿真",()=> {if(Selected==null)return;Selected.Evaluate(0);_singlePaused=false;SetPreviewPaused(false);_previewClock.Restart();_previewTimer.Start();UpdateEnabled();});
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
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,36));
            inspector.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            inspector.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inspector.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            _inspectorHeading=Heading("3  设置选中镜头");inspector.Controls.Add(_inspectorHeading,0,0);
            _instruction=new Label {Dock=DockStyle.Fill,Padding=new Padding(8),ForeColor=Color.FromArgb(37,87,126),Text="先在左侧拾取操作，再添加镜头。"};
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
            AddField(fields,"焦点来源",_focusMode);AddField(fields,"焦点对象",_object);AddField(fields,"世界焦点坐标",_point);
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
            _status.AutoSize=false;_status.Dock=DockStyle.Fill;_status.Padding=new Padding(6);_status.ForeColor=Color.DimGray;
            inspector.Controls.Add(_status,0,4);
            root.Controls.Add(center,0,0);root.Controls.Add(inspector,1,0);Controls.Add(root);
            PickFocus.Wire(_object); PickFocus.Wire(_point);
            _object.Picked+=(s,e)=> { if(_loading||Selected==null)return; try {Selected.FocusObject=_object.Object as ITxLocatableObject; Selected.Focus=Selected.FocusObject==null?null:CameraShot.Copy(Selected.FocusObject.AbsoluteLocation.Translation);Selected.FocusLabel=Selected.FocusObject==null?null:PsReader.GetObjectName(Selected.FocusObject); Save();} catch(Exception ex){Report(ex);} };
            _object.TypeValid+=(s,e)=>ReadObject();
            _object.TypeInvalid+=(s,e)=> {if(!_loading&&Selected!=null){Selected.FocusObject=null;Selected.Focus=null;_list.Invalidate();UpdateEnabled();}};
            _point.ValidFrameSet+=(s,e)=> {if(!_loading&&Selected!=null){Selected.Focus=CameraShot.Copy(_point.GetLocation().Translation);Save();}};
            _point.InvalidFrameSet+=(s,e)=> {if(!_loading&&Selected!=null){Selected.Focus=null;_list.Invalidate();UpdateEnabled();}};
            _list.SelectedIndexChanged+=(s,e)=>LoadSelected();
            foreach(var c in new ComboBox[]{_kind,_trigger,_axis,_focusMode}) c.SelectedIndexChanged+=(s,e)=>Save();
            foreach(var c in new NumericUpDown[]{_start,_duration,_location,_angle,_distance}) c.ValueChanged+=(s,e)=>Save();
            foreach(var c in new NumericUpDown[]{_radius,_height,_startAngle}) c.ValueChanged+=(s,e)=>{if(_loading||Selected==null)return;Selected.Radius=(double)_radius.Value;Selected.Height=(double)_height.Value;Selected.StartAngle=(double)_startAngle.Value;Selected.OrbitInitialized=true;_guide.Invalidate();Changed?.Invoke();};
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
        private static Label Heading(string text)
        {return new Label {Text=text,Dock=DockStyle.Fill,Padding=new Padding(8,6,0,0),BackColor=Color.FromArgb(224,234,243),ForeColor=Color.FromArgb(30,65,94)};}
        private void AddShot()
        {
            if(_shots==null)return;
            var previous=_shots.LastOrDefault(x=>x.Enabled);
            var shot=new CameraShot {Name="镜头 "+(_shots.Count+1),Start=previous!=null&&previous.Trigger==ShotTrigger.Time?previous.Start+previous.Duration:0,Trigger=previous!=null&&previous.Trigger!=ShotTrigger.Time?ShotTrigger.Previous:ShotTrigger.Time};
            shot.Kind=(ShotKind)_addKind.SelectedIndex;shot.Name=new[]{"定点","环绕","推拉","两点移动"}[(int)shot.Kind]+" "+(_shots.Count+1);
            shot.Trigger=ShotTrigger.Previous;shot.FocusSource=shot.Kind==ShotKind.Orbit||shot.Kind==ShotKind.Dolly?1:0;
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
            using(var brush=new SolidBrush(selected?Color.FromArgb(220,237,251):Color.White))e.Graphics.FillRectangle(brush,e.Bounds);
            string kind=new[]{"定点","环绕焦点","推近 / 拉远","两点移动"}[(int)shot.Kind];
            string ready=_busy?"播放中，配置已锁定":ShotPlan.Missing(shot)??"已配置";
            TextRenderer.DrawText(e.Graphics,(e.Index+1).ToString("00")+"  "+shot.Name+" · "+kind,Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y+5,e.Bounds.Width-12,lineHeight),Color.FromArgb(27,62,88),TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,shot.ToString().Substring(shot.Name.Length)+"  /  "+(shot.Enabled?ready:"已停用"),Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y+5+lineHeight,e.Bounds.Width-12,lineHeight),Color.DimGray,TextFormatFlags.EndEllipsis);
            if(_dropIndex==e.Index||(_dropIndex==_list.Items.Count&&e.Index==_list.Items.Count-1))
            {int y=_dropIndex==e.Index?e.Bounds.Top:e.Bounds.Bottom-2;using(var pen=new Pen(Color.FromArgb(20,105,166),2))e.Graphics.DrawLine(pen,e.Bounds.Left,y,e.Bounds.Right,y);}
        }
        private void DrawGuide(Graphics g,Rectangle bounds)
        {
            if(bounds.Height<70)
            {
                TextRenderer.DrawText(g,"4  镜头预览 · 使用下方预览按钮",Font,new Rectangle(8,6,Math.Max(1,bounds.Width-16),Font.Height+2),Color.DimGray,TextFormatFlags.EndEllipsis);
                return;
            }
            g.DrawString("4  镜头路径与视角 · PS 主视口可预演",Font,Brushes.DimGray,8,8);
            if(_busy){g.DrawString(_simulationStopping?"正在停止预演…":"预演或录制中，镜头配置已锁定",Font,Brushes.SteelBlue,new RectangleF(12,40,bounds.Width-24,60));return;}
            if(_quick||Selected==null)
            {
                g.DrawString(_quick?"快速录像：不应用镜头编排，按输出设置直接录制。":_shots==null?"先在左侧选择操作。":"先选择镜头类型，再点击添加镜头。",Font,Brushes.SteelBlue,new RectangleF(12,40,bounds.Width-24,80));return;
            }
            var shot=Selected;
            int thumbWidth=Math.Min(90,bounds.Width/5),thumbHeight=thumbWidth*9/16;
            bool thumbnails=bounds.Height>120;
            if(thumbnails)
            {
                if(shot.FromThumbnail!=null)g.DrawImage(shot.FromThumbnail,new Rectangle(8,40,thumbWidth,thumbHeight));
                if(shot.ToThumbnail!=null)g.DrawImage(shot.ToThumbnail,new Rectangle(bounds.Width-thumbWidth-8,40,thumbWidth,thumbHeight));
                g.DrawString(shot.From==null?"起点未记录":"起点已记录",Font,Brushes.DimGray,8,43+thumbHeight);
                if(shot.Kind==ShotKind.Move)g.DrawString(shot.To==null?"终点未记录":"终点已记录",Font,Brushes.DimGray,bounds.Width-thumbWidth-8,43+thumbHeight);
            }
            try
            {
                var positions=new List<TxVector>();for(int i=0;i<=24;i++)positions.Add(shot.Evaluate(i/24.0).Position);
                var focus=shot.Evaluate(0).ReferencePoint;
                Func<TxVector,PointF> project=v=>new PointF((float)(v.X-v.Y*.5),(float)(v.Z+v.Y*.3));
                var points=positions.Select(project).ToList();var target=project(focus);
                var all=points.Concat(new[]{target}).ToList();
                float minX=all.Min(v=>v.X),maxX=all.Max(v=>v.X),minY=all.Min(v=>v.Y),maxY=all.Max(v=>v.Y);
                float left=thumbnails?thumbWidth+24:24,available=Math.Max(30,bounds.Width-left*2);
                float h=Math.Max(20,bounds.Height-90);
                float scale=Math.Min(available/Math.Max(1,maxX-minX),h/Math.Max(1,maxY-minY));
                Func<PointF,PointF> fit=v=>new PointF(bounds.Width/2f+(v.X-(minX+maxX)/2)*scale,55+h/2-(v.Y-(minY+maxY)/2)*scale);
                var path=points.Select(fit).ToArray();
                using(var pen=new Pen(Color.SteelBlue,2))g.DrawLines(pen,path);
                var point=fit(target);g.FillEllipse(Brushes.SteelBlue,point.X-3,point.Y-3,6,6);
                var current=path[Math.Min(24,_preview.Value*24/1000)];g.FillRectangle(Brushes.DarkSlateBlue,current.X-5,current.Y-5,10,10);
                g.DrawString(shot.Kind==ShotKind.Orbit?"环绕 "+shot.Angle.ToString("0.#")+"° · 半径 "+shot.Radius.ToString("0.#")+" mm":shot.Kind==ShotKind.Dolly?"结束距离 "+shot.EndDistance.ToString("0.#")+" mm":"起点 → 终点",Font,Brushes.DimGray,8,Math.Max(40,bounds.Height-24));
            }
            catch{g.DrawString(ShotPlan.Missing(shot)??"路径尚不可用",Font,Brushes.DarkOrange,new RectangleF(thumbWidth+20,45,Math.Max(20,bounds.Width-thumbWidth*2-40),60));}
        }
        private TransportIconButton AddIconButton(FlowLayoutPanel panel,TransportIcon icon,string name,string hint,Action action,bool allowWhileBusy=false)
        {
            var button=new TransportIconButton(icon) {Name="transport"+icon,AccessibleName=name,AccessibleDescription=hint};
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
        {var b=new Button {Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(52,26),Padding=new Padding(5,1,5,1)};b.Click+=(s,e)=>{if(!_busy||allowWhileBusy)try{action();}catch(Exception ex){Report(ex);}};p.Controls.Add(b);_editControls.Add(b);}
        private void AddField(TableLayoutPanel p,string label,Control c)
        {int r=p.RowCount++;p.RowStyles.Add(new RowStyle(SizeType.AutoSize));p.Controls.Add(new Label {Text=label,AutoSize=true,Margin=new Padding(2,5,6,2)},0,r);c.Dock=DockStyle.Fill;c.Margin=new Padding(2,2,2,2);p.Controls.Add(c,1,r);_editControls.Add(c);}
        private void ReadObject()
        {if(_loading||Selected==null)return;try{Selected.FocusObject=_object.Object as ITxLocatableObject;Selected.Focus=Selected.FocusObject==null?null:CameraShot.Copy(Selected.FocusObject.AbsoluteLocation.Translation);Selected.FocusLabel=Selected.FocusObject==null?null:PsReader.GetObjectName(Selected.FocusObject);Save();}catch(Exception ex){Report(ex);}}
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
        {return Math.Max(0,Math.Min(1,(x-8.0)/Math.Max(1,_timeline.ClientSize.Width-20)))*TimelineMaximum();}
        private bool SelectTimelineTime(double time)
        {
            if(_busy||_quick||_shots==null)return false;
            _previewTimer.Stop();_previewClock.Stop();_singlePaused=false;SetPreviewPaused(false);
            time=Math.Max(0,Math.Min(TimelineMaximum(),time));
            var known=ShotPlan.Ranges(_shots).Where(r=>r.Start.HasValue&&r.Start.Value<=time+1e-9).OrderBy(r=>r.Start.Value).ToList();
            var range=known.LastOrDefault();
            string previewError=null;
            _timelineSelecting=true;
            try
            {
                _list.SelectedItem=range?.Shot;
                if(range!=null)
                {
                    double fraction=Math.Max(0,Math.Min(1,(time-range.Start.Value)/range.Shot.Duration));
                    _preview.Value=(int)Math.Round(fraction*1000);
                    previewError=ShotPlan.Missing(range.Shot);
                    if(previewError==null&&!PreviewAt(fraction))previewError=_status.Text;
                }
                else
                {
                    if(_saved!=null)PsReader.SetCurrentCamera(PsReader.GetGraphicViewer(),_saved);
                }
                UpdateEnabled();
                _status.Text=range==null?time.ToString("0.000")+" s · 沿用起始视角，无镜头片段":time.ToString("0.000")+" s · "+range.Shot.Name+(previewError==null?"（仅预览相机）":"："+previewError);
                _timelineCursor=time;_timeline.Invalidate();_guide.Invalidate();return range!=null;
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
        public void UpdateSimulationTime(double time){_simulationTime=time;_timeline.Invalidate();RefreshTransportFeedback();}
        public void SetSimulationState(bool running,double? time=null,bool stopping=false)
        {_simulationRunning=running;_simulationStopping=stopping;_simulationTime=time;UpdateEnabled();_timeline.Invalidate();}

        private void RefreshList(CameraShot selected)
        {_loading=true;_list.Items.Clear();if(_shots!=null)foreach(var s in _shots)_list.Items.Add(s);_list.SelectedItem=selected;_loading=false;LoadSelected();_timeline.Invalidate();}
        private void LoadSelected()
        {
            if(_loading)return;if(!_timelineSelecting)Restore();else{_previewTimer.Stop();_previewClock.Stop();}
            _loading=true;var s=Selected;
            try {if(s!=null){_enabled.Checked=s.Enabled;_name.Text=s.Name;_kind.SelectedIndex=(int)s.Kind;_trigger.SelectedIndex=(int)s.Trigger;_start.Value=(decimal)s.Start;_duration.Value=(decimal)s.Duration;_location.Value=s.LocationIndex;_axis.SelectedIndex=s.Axis;_angle.Value=(decimal)s.Angle;_distance.Value=(decimal)s.EndDistance;_smooth.Checked=s.Smooth;_focusMode.SelectedIndex=s.FocusSource;_object.Object=s.FocusObject;if(s.Focus==null)_point.Text=string.Empty;else _point.SetLocation(new TxTransformation(s.Focus,new TxVector(0,0,0)));_preview.Value=0;_status.Text=(s.From==null?"起始视角未记录":"起始视角已记录")+(s.Kind==ShotKind.Move?"；"+(s.To==null?"终点未记录":"终点已记录"):"；调整参数后可播放预览");}}
            finally{_loading=false;LoadOrbitFields();UpdateEnabled();if(!_timelineSelecting)_timelineCursor=SelectedRange()?.Start;_timeline.Invalidate();}
        }
        private void Save()
        {if(_loading||Selected==null||_busy)return;var s=Selected;s.Name=_name.Text;s.Kind=(ShotKind)_kind.SelectedIndex;s.Trigger=(ShotTrigger)_trigger.SelectedIndex;s.Start=(double)_start.Value;s.Duration=(double)_duration.Value;s.LocationIndex=(int)_location.Value;s.Axis=_axis.SelectedIndex;s.Angle=(double)_angle.Value;s.EndDistance=(double)_distance.Value;s.Smooth=_smooth.Checked;s.Follow=_focusMode.SelectedIndex==2;
            if(s.FocusSource!=_focusMode.SelectedIndex){s.Focus=null;s.FocusObject=null;_loading=true;try{_object.Object=null;_point.Text=string.Empty;}finally{_loading=false;}s.FocusSource=_focusMode.SelectedIndex;}
            _loading=true;try{_list.Items[_list.SelectedIndex]=s;}finally{_loading=false;}_list.Invalidate();_timeline.Invalidate();UpdateEnabled();Changed?.Invoke();}
        private void CaptureCamera(bool end)
        {var s=Selected;if(s==null)return;var c=CameraShot.Snapshot(PsReader.GetCurrentCamera(PsReader.GetGraphicViewer()));if(c==null)throw new InvalidOperationException("无法获取当前视角");if(end)s.To=c;else{s.From=c;if(s.Kind==ShotKind.Orbit){s.InitializeOrbit();LoadOrbitFields();}}_status.Text=end?"终点已记录，可以播放预览":s.Kind==ShotKind.Move?"起点已记录，请调整视角后记录终点":s.Kind==ShotKind.Orbit?"起点已记录，环绕参数已初始化，可以播放预览":"起点已记录，可以播放预览";_list.Invalidate();
            try{var image=PsReader.GetGraphicViewer().GetImage(new Size(192,108),false);if(end){s.ToThumbnail?.Dispose();s.ToThumbnail=image;}else{s.FromThumbnail?.Dispose();s.FromThumbnail=image;}}catch(Exception ex){_status.Text="视角已记录，缩略图获取失败："+ex.Message;}
            UpdateEnabled();Changed?.Invoke();}
        private void LoadOrbitFields()
        {if(Selected==null)return;_loading=true;try{_radius.Value=Math.Max(_radius.Minimum,Math.Min(_radius.Maximum,(decimal)Selected.Radius));_height.Value=Math.Max(_height.Minimum,Math.Min(_height.Maximum,(decimal)Selected.Height));_startAngle.Value=(decimal)Selected.StartAngle;}finally{_loading=false;}}
        public void SetBusy(bool busy){if(busy&&!_busy)Restore();_busy=busy;UpdateEnabled();}
        private void UpdateEnabled()
        {
            bool showing=Selected!=null&&!_quick;
            bool selected=showing&&!_busy;
            foreach(var c in _editControls)c.Enabled=selected;
            foreach(var button in _editControls.OfType<Button>().Where(b=>b.Text.Contains("添加镜头")))button.Enabled=_shots!=null&&!_busy;
            _fields.Visible=!_quick;_quickSettings.Visible=_quick;_quickHint.Visible=_quick;_list.Visible=!_quick;
            if(_quick){_quickSettings.BringToFront();_quickHint.BringToFront();}
            _quickHint.Text="快速录像只需选择操作和输出位置。\n\n当前操作保存的 "+(_shots?.Count??0)+" 个镜头会保留，本次不应用。\n\n需要镜头运动时，可选择类型后点击添加，自动进入编排模式。";
            _captureStart.Parent.Visible=!_quick;_inspectorHeading.Text=_quick?"3  快速录像说明":"3  设置选中镜头";
            _mode.Enabled=_shots!=null&&!_busy;
            _addKind.Enabled=_shots!=null&&!_busy;
            foreach(var button in _editControls.OfType<Button>().Where(b=>b.Text.StartsWith("配置")))button.Enabled=_shots!=null&&!_busy;
            _simulationButton.Enabled=_shots!=null&&!_busy&&(_quick||ShotPlan.Issues(_shots,_operationDuration,_locationCount).Count==0);
            _pauseButton.Enabled=(_simulationRunning&&!_simulationStopping)||_previewTimer.Enabled||_singlePaused;_stopButton.Enabled=(_simulationRunning&&!_simulationStopping)||_saved!=null||_previewTimer.Enabled;
            _list.Enabled=!_busy&&!_quick;
            _timeline.Enabled=!_busy&&!_quick;_timeline.Cursor=_timeline.Enabled?Cursors.Hand:Cursors.Default;
            _selectedIssue=selected?ShotPlan.Missing(Selected):null;
            _preview.Enabled=selected&&_selectedIssue==null;
            _summary.Text=_busy?(_simulationStopping?"正在停止…":"播放中，配置已锁定"):_quick?"选输出后可直接录制":_shots==null?"未选择操作":ShotPlan.Summary(_shots,_operationDuration);
            _summary.MaximumSize=new Size(200,0);
            if(_quick)_status.Text="快速录像不应用镜头编排；已有配置会保留。";
            if(Selected==null)_status.Text=_shots==null?"未选择操作":_shots.Count==0?"还没有镜头。也可直接录制，使用默认视角。":"请选择镜头或点击时间条";
            _start.Enabled=selected&&_trigger.SelectedIndex==0;_location.Enabled=selected&&_trigger.SelectedIndex==1;
            bool focus=showing&&(_kind.SelectedIndex==1||_kind.SelectedIndex==2);
            _object.Enabled=selected&&focus&&_focusMode.SelectedIndex!=0;_point.Enabled=selected&&focus&&_focusMode.SelectedIndex==0;
            _axis.Enabled=_angle.Enabled=selected&&_kind.SelectedIndex==1;_distance.Enabled=selected&&_kind.SelectedIndex==2;
            _radius.Enabled=_height.Enabled=_startAngle.Enabled=selected&&_kind.SelectedIndex==1;
            ShowField(_focusMode,focus);ShowField(_object,focus&&_focusMode.SelectedIndex!=0);ShowField(_point,focus&&_focusMode.SelectedIndex==0);
            _captureStart.Enabled=selected;_captureEnd.Visible=Selected!=null&&Selected.Kind==ShotKind.Move;
            _instruction.Text=Selected==null?(_shots==null?"① 先在左侧拾取操作":"② 添加镜头，开始配置"):(focus?"选焦点 → 记录视角 → 预览":Selected.Kind==ShotKind.Move?"记录起点、终点 → 预览":"调整视角 → 记录起点 → 预览");
            ShowField(_motionRow,focus);
            _guide.Invalidate();
            foreach(var c in new Control[]{_axis,_radius,_height,_startAngle})ShowField(c,showing&&_kind.SelectedIndex==1&&_motionToggle.Checked);
            ShowField(_smooth,Selected!=null&&Selected.Kind!=ShotKind.Fixed&&!_quick);
            ShowField(_angle,showing&&_kind.SelectedIndex==1);
            ShowField(_distance,showing&&_kind.SelectedIndex==2);ShowField(_start,showing&&_trigger.SelectedIndex==0);ShowField(_location,showing&&_trigger.SelectedIndex==1);
            PickFocus.SetSuspended(_object,!Visible||!_object.Enabled);PickFocus.SetSuspended(_point,!Visible||!_point.Enabled);
            _playButton.Enabled=selected&&_selectedIssue==null;
            if(Selected!=null&&!_quick&&!_busy)
                _status.Text=_selectedIssue==null?"已就绪：预览镜头或预演整段操作":"下一步："+_selectedIssue;
            if(Selected!=null&&Selected.FocusSource>0&&Selected.FocusObject==null&&!string.IsNullOrEmpty(Selected.FocusLabel))_instruction.Text="请重新拾取焦点："+Selected.FocusLabel;
            if(_quick)_instruction.Text="快速录像：选择操作 → 选择输出 → 开始录制";
            if(_busy)_instruction.Text="预演或录制中；停止后继续编辑";
            if(_busy)_status.Text=_simulationStopping?"正在停止预演，请稍候…":"播放期间已锁定编辑；停止后保留当前位置";
            RefreshPropertyScroll();RefreshTransportFeedback();
        }
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
                _timeline.Invalidate();_guide.Invalidate();
                if(!PsReader.SetCurrentCamera(viewer,Selected.Evaluate(t)))throw new InvalidOperationException("应用预览视角失败");
                RefreshTransportFeedback();return true;
            }
            catch(Exception ex){_previewTimer.Stop();Report(ex);RefreshTransportFeedback();return false;}
        }
        private void DrawTimeline(Graphics g)
        {
            if(_shots==null){g.DrawString("选择操作后显示完整时间范围",Font,Brushes.Gray,6,6);return;}
            var ranges=ShotPlan.Ranges(_shots);var known=ranges.Where(r=>r.Start.HasValue).ToList();
            double end=known.Count==0?0:known.Max(r=>r.Start.Value+r.Shot.Duration);
            double max=Math.Max(1,Math.Max(_operationDuration??0,end));
            float width=Math.Max(1,_timeline.Width-20),line=Font.Height+2;
            g.DrawString(_quick?"快速录像 · 完整操作":"操作时间 / s · 灰色沿用当前视角",Font,Brushes.DimGray,4,0);
            float y=line*2+2;
            g.FillRectangle(Brushes.Gainsboro,8,y,width,line);
            for(int i=0;i<=4;i++){float x=8+width*i/4;g.DrawString((max*i/4).ToString("0.#"),Font,Brushes.DimGray,Math.Min(x,_timeline.Width-36),line);}
            if(!_quick)foreach(var range in known)
            {
                float x=8+(float)(range.Start.Value/max)*width,w=Math.Max(2,(float)(range.Shot.Duration/max)*width);
                g.FillRectangle(range.Shot==Selected?Brushes.SteelBlue:Brushes.LightSteelBlue,x,y,w,line);
                if(w>26)g.DrawString((_shots.IndexOf(range.Shot)+1).ToString("00"),Font,Brushes.White,x+3,y);
            }
            if(_operationDuration.HasValue)
            {float x=8+(float)(_operationDuration.Value/max)*width;g.DrawLine(Pens.DarkOrange,x,line,x,y+line);}
            double? cursor=_simulationRunning?_simulationTime:_timelineCursor;
            if(cursor.HasValue)
            {float x=8+(float)(Math.Min(max,cursor.Value)/max)*width;g.DrawLine(Pens.Blue,x,line,x,y+line);g.FillPolygon(Brushes.Blue,new[]{new PointF(x-4,line),new PointF(x+4,line),new PointF(x,line+5)});}
            string events=string.Join("、",ranges.Where(r=>!r.Start.HasValue).Select(r=>r.Shot.Name));
            string caption=_quick?(_operationDuration.HasValue?"操作约 "+_operationDuration.Value.ToString("0.###")+" s；使用录像设置中的视角":"操作时长未知；使用录像设置中的视角")
                :events.Length>0?"事件时间待定："+events:_operationDuration.HasValue?"操作约 "+_operationDuration.Value.ToString("0.###")+" s；镜头结束后保持末态":"操作时长未知，范围按镜头估算";
            g.DrawString(caption,Font,Brushes.DimGray,new RectangleF(4,y+line+3,_timeline.Width-8,line*2));
        }
        public void Restore(){_previewTimer.Stop();_previewClock.Stop();_singlePaused=false;if(_pauseButton!=null)SetPreviewPaused(false);_simulationTime=null;_timelineCursor=null;if(_saved==null)return;try{PsReader.SetCurrentCamera(PsReader.GetGraphicViewer(),_saved);}catch{}finally{_saved=null;}}
    }
}
