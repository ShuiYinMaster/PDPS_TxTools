using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

namespace TxTools.CrossEnvIO
{
    public partial class CrossEnvIOForm : PickAwareTxForm
    {
        internal static CrossEnvIOForm Instance;
        private readonly Size _design = new Size(900, 860);
        private bool _scaled, _busy, _populating, _uncertain, _finishedJob, _jobSucceeded, _fitting, _logExpanded, _closeRequested;
        private MeasuredColumn _main, _header, _storagePage, _problemPanel;
        private TabControl _options;
        private Form _settingsWindow;
        private TableLayoutPanel _root;
        private ComboBox _kind, _peers, _placement, _basis, _package, _conflict;
        private CheckBox _whole, _copy, _bindOnly, _project,_includeWeldParts;
        private TxObjGridCtrl _objects;
        private TxFrameEditBoxCtrl _sourceOrigin,_origin;
        private TextBox _destination, _singleDir, _fallback, _source, _targetName;
        private Label _scope, _receiver, _storageNote, _status, _summary;
        private TextBox _issueDetail;
        private Panel _executionTop;
        private MeasuredColumn _executionLead,_executionMessages;
        private FlowLayoutPanel _executionFiles;
        private FlowLayoutPanel _executionActions;
        private FlowLayoutPanel _typeRules;
        private ListBox _issues;
        private RichTextBox _log;
        private Button _next, _cancel, _settings, _query, _browse, _details, _bindingButton,_pickTargetFrame;
        private readonly Dictionary<string, TextBox> _typeEditors = new Dictionary<string, TextBox>();
        private readonly List<MeasuredColumn> _columns = new List<MeasuredColumn>();
        private readonly System.Windows.Forms.Timer _pickTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private readonly System.Windows.Forms.Timer _discoverTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly System.Windows.Forms.Timer _contextTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private bool _capturePending,_receiverPending,_refreshingContext;

        public CrossEnvIOForm(SynchronizationContext psCtx)
        {
            SemiModal = false;
            FormUiKit.InitStandardForm(this, "跨环境传输 · 模型结构 / 焊点", _design, new Size(460, 700), sizable: true);
            BuildUi();
            FormClosing += (s, e) => { if (_busy) { e.Cancel = true; _closeRequested=true; _cancellation?.Cancel(); _status.Text = "正在结束当前操作，完成后关闭窗口。"; } };
            Activated+=(s,e)=> { if(_settingsWindow.Visible&&!_busy)ShowSettingsWindow(); };
            LibraryPersistence.Reloading+=ClearScenePickersForReload;
            Shown += (s, e) => { FitColumns(); RestorePending(); if (!_uncertain) RefreshPeers(); _discoverTimer.Start();_contextTimer.Start(); BeginInvoke(new Action(ExpandForContent)); };
            DpiChanged += (s, e) => FitColumns();
            _pickTimer.Tick += (s, e) => { if (!_busy && !_uncertain && _pickId != null) Run(ReadPick); };
            _discoverTimer.Tick += (s, e) => { if (!_busy && !_uncertain) RefreshPeersCore(true); };
            _contextTimer.Tick+=async(s,e)=>
            {
                if(_busy||_uncertain||_populating||_refreshingContext)return;
                ObserveSceneSelection();
                if(!_capturePending&&!_receiverPending)return;
                // Recognition is passive: never suspend the native picker, disable
                // its parent controls or change the host selection while multi-picking.
                _refreshingContext=true;UpdateActions();
                try{await RefreshContext();}
                catch(Exception ex){ShowFailure(ex);}
                finally{_refreshingContext=false;if(!IsDisposed)UpdateActions();}
            };
            FormClosed += (s, e) => { LibraryPersistence.Reloading-=ClearScenePickersForReload; _pickTimer.Dispose(); _discoverTimer.Dispose(); _contextTimer.Dispose(); _settingsWindow?.Dispose(); };
            Log("选择任务和接收位置；检查不写入工程，通过后再确认执行。");
        }
        public override void OnInitTxForm() { base.OnInitTxForm(); SemiModal = false; }
        protected override void OnLoad(EventArgs e)
        { base.OnLoad(e); FormUiKit.ApplyDpiScaling(this, ref _scaled, _design); FormBorderStyle=FormBorderStyle.Sizable;MaximizeBox=true;FitColumns(); }
        private void BuildUi()
        {
            _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10), BackColor = Color.FromArgb(241,244,248) };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute,140));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute,26));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute,0));
            _header = Column(false); _header.AutoSize = true; _header.Dock = DockStyle.Fill;
            _main = Column(true); _main.Dock = DockStyle.Fill;
            _root.Controls.Add(_header,0,0); _root.Controls.Add(_main,0,1);
            BuildGuidedHeader();
            BuildGuidedCards();
            BuildOptions();
            BuildExecution();
            _status = Caption("尚未检查；确认执行时使用原生入库，不自动保存工程。");
            _status.AutoSize=false;_status.AutoEllipsis=true;_status.Dock=DockStyle.Fill;_root.Controls.Add(_status,0,4);
            _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Font = new Font("Consolas",9), DetectUrls = false };
            _root.Controls.Add(_log,0,5); PluginLog.Register(_log,false);
            Controls.Add(_root); ClientSizeChanged += (s,e) => FitColumns();
            _placement.SelectedIndex = (int)TransferPlacement.Automatic; UpdateDestination(); UpdateActions();
        }
        private void BuildOptions()
        {
            _settingsWindow = new Form { Text = "跨环境传输 · 高级规则", Tag=this, ShowInTaskbar = true, StartPosition = FormStartPosition.Manual,
                Font = FormUiKit.BaseFont, ClientSize = new Size(540,700), MinimumSize = new Size(460,480), AutoScaleMode = AutoScaleMode.Dpi };
            _settingsWindow.FormClosing += (s,e) => { if(e.CloseReason==CloseReason.UserClosing){ e.Cancel = true; _settingsWindow.Hide(); } };
            _settingsWindow.ClientSizeChanged += (s,e) => FitColumns();
            _options = new TabControl { Dock = DockStyle.Fill, Font = FormUiKit.BaseFont };
            _storagePage = OptionPage("模型存放高级规则"); BuildStorage(_storagePage);
            _bindOnly = new CheckBox { AutoSize = true, Text = "仅修改已有焊点的绑定", Font = Font };
            _project = new CheckBox { AutoSize = true, Text = "绑定后投影到零件表面", Font = Font };
            _includeWeldParts=new CheckBox { AutoSize=true,Text="同时传输本端绑定零件（先传零件，再恢复绑定）",Font=Font };
            _includeWeldParts.CheckedChanged+=(s,e)=>
            {
                if(_populating)return;
                if(_sourceFromFile){if(_plan!=null)_plan.IncludeWeldParts=_includeWeldParts.Checked;InvalidatePlan();}
                else SourceChanged();
                UpdateKind();
            };
            _bindOnly.CheckedChanged += (s,e) => InvalidatePlan(); _project.CheckedChanged += (s,e) => InvalidatePlan();
            _weldSettings.Controls.Add(Caption("焊点绑定选项",true));_weldSettings.Controls.Add(_includeWeldParts);_weldSettings.Controls.Add(_bindOnly);_weldSettings.Controls.Add(_project);
            _weldSettings.Controls.Add(Caption("默认允许无绑定零件传输，缺失绑定只记日志；投影或仅修改绑定需要有效的接收零件。携带零件离线传输请使用离线包。"));
            _settingsWindow.Controls.Add(_options);
        }
        private void BuildExecution()
        {
            _problemPanel = Column(true); _problemPanel.AutoSize = false; _problemPanel.Dock = DockStyle.Fill;
            _problemPanel.Padding = new Padding(10); _problemPanel.BackColor = Color.White; _problemPanel.BorderStyle = BorderStyle.FixedSingle;
            _executionTop=new Panel();_problemPanel.Controls.Add(_executionTop);
            _executionLead=Column(false);_executionMessages=Column(false);_executionTop.Controls.Add(_executionLead);_executionTop.Controls.Add(_executionMessages);
            _executionLead.Controls.Add(Caption("3  检查与执行",true));
            _summary = Caption("尚未检查；检查通过后确认执行。"); _executionLead.Controls.Add(_summary);
            _issues = new ListBox { Font = Font, Height = 56,MinimumSize=new Size(0,56), DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false, Visible = false };
            _issues.MeasureItem += (s,e) => { if(e.Index>=0)e.ItemHeight=Font.Height+8; };
            _issues.DrawItem += (s,e) => { if(e.Index<0)return; e.DrawBackground(); TextRenderer.DrawText(e.Graphics,Convert.ToString(_issues.Items[e.Index]).Split('\n')[0].TrimEnd('\r'),Font,
                new Rectangle(e.Bounds.X+3,e.Bounds.Y+3,e.Bounds.Width-6,e.Bounds.Height-6),e.ForeColor,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis); e.DrawFocusRectangle(); };
            _executionMessages.Controls.Add(_issues);
            _issueDetail=new TextBox { Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,
                Height=64,Font=FormUiKit.BaseFont,BackColor=Color.FromArgb(255,248,243),Visible=false,Margin=new Padding(0,4,0,4) };
            _executionMessages.Controls.Add(_issueDetail);
            PluginUiPolicy.AllowListScrolling(_issueDetail);
            _issues.SelectedIndexChanged+=(s,e)=>
            {
                _issueDetail.Text=Convert.ToString(_issues.SelectedItem).Replace("\r\n","\n").Replace("\r","\n").Replace("\n",Environment.NewLine);
                _issueDetail.Visible=_issues.SelectedItem!=null;UpdateActions();FitColumns();
            };
            var fixes = Row(); fixes.Tag = "fixes";
            _bindingButton=Btn(fixes,"为选中焊点指定零件…",()=>Run(BindIssue),180); _executionMessages.Controls.Add(fixes);
            var actions = Row();_executionActions=actions;actions.Dock=DockStyle.Fill;actions.Padding=new Padding(2,4,2,2);
            _next = FormUiKit.MkBtn("检查重建条件",Color.FromArgb(0,111,134),180,34);
            _next.MinimumSize = new Size(180,34); _next.ForeColor = Color.White;
            _next.Click += (s,e) => { if (_uncertain) Run(QueryJob,true); else Run(Transfer); }; actions.Controls.Add(_next);
            _cancel = Btn(actions,"取消复制",()=>_cancellation?.Cancel(),96);
            _query = Btn(actions,"查询结果",()=>Run(QueryJob,true),96); _query.Visible = false;
            _root.Controls.Add(actions,0,3);
            _executionFiles=Row();
            Btn(_executionFiles,"导出 TSV…",()=>Run(()=>ExportData(false)),106);
            Btn(_executionFiles,"导出离线包…",()=>Run(()=>ExportData(true)),120);
            Btn(_executionFiles,"传输明细",()=>Run(()=>{EnsureCaptured();SyncPlan();PreviewPlan();return System.Threading.Tasks.Task.CompletedTask;}),100);
            Btn(_executionFiles,"清理未用文件",()=>Run(CleanupFiles),128);
            _details=Btn(_executionFiles,"详细日志",ToggleLog,96);_executionMessages.Controls.Add(_executionFiles);
            _root.Controls.Add(_problemPanel,0,2);
        }
        private MeasuredColumn Column(bool scroll = true)
        {
            var c = new MeasuredColumn { AutoScroll = scroll, Margin = Padding.Empty };
            c.ClientSizeChanged += (s,e)=> { if (IsHandleCreated) FitColumns(); }; _columns.Add(c); return c;
        }
        private MeasuredColumn OptionPage(string title)
        {
            var tab = new TabPage(title) { Padding = new Padding(8), BackColor = Color.White };
            var c = Column(); c.Dock = DockStyle.Fill; tab.Controls.Add(c); _options.TabPages.Add(tab); return c;
        }
        private static Label Caption(string text, bool emphasis = false) => new Label { AutoSize = true, Text = text, Font = emphasis ? new Font(FormUiKit.BaseFont,FontStyle.Bold) : FormUiKit.BaseFont, Margin = new Padding(0,5,0,5) };
        private sealed class ActionRow : FlowLayoutPanel, IManagedFlowRow { }
        private static FlowLayoutPanel Row() => new ActionRow { AutoSize = true, WrapContents = true, Margin = new Padding(0,3,0,5) };
        private static Button Btn(Control host,string text,Action action,int width = 112)
        {
            var b = FormUiKit.MkBtn(text,Color.White,width,32);
            b.BackColor = Color.White; b.ForeColor = Color.FromArgb(35,55,70);
            b.Click += (s,e)=> { try { action(); } catch(Exception ex) { var form=host.FindForm() as CrossEnvIOForm ?? host.FindForm()?.Tag as CrossEnvIOForm; form?.ShowFailure(ex); } };
            host.Controls.Add(b); return b;
        }
        private static ComboBox Choice(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = FormUiKit.BaseFont, Margin = new Padding(0,3,0,5) };
            c.Items.AddRange(items); if(items.Length>0)c.SelectedIndex=0; return c;
        }
        private TextBox Field(Control host,string caption,Action<string> changed = null)
        {
            host.Controls.Add(Caption(caption)); var t=new TextBox { Font=FormUiKit.BaseFont, Margin=new Padding(0,0,0,4) };
            if(changed!=null)t.TextChanged+=(s,e)=> { if(!_populating) { changed(t.Text); InvalidatePlan(); UpdateDestination(); } };
            host.Controls.Add(t); return t;
        }
        private void BuildStorage(MeasuredColumn page)
        {
            _advancedStorageToggle=new CheckBox { Text="高级规则",AutoSize=true,Checked=true,Visible=false,Font=FormUiKit.BaseFont };
            page.Controls.Add(_advancedStorageToggle);
            _storageAdvanced=Column(false);_storageAdvanced.AutoSize=true;page.Controls.Add(_storageAdvanced);
            BuildStorageAdvanced(_storageAdvanced);_storageAdvanced.Visible=true;
            _advancedStorageToggle.CheckedChanged+=(s,e)=> { _storageAdvanced.Visible=_advancedStorageToggle.Checked;FitColumns(); };
        }
        private void BuildStorageAdvanced(MeasuredColumn page)
        {
            page.Controls.Add(Caption("分配目标库中的存放位置", true));
            _storageNote = Caption("自动关联源目录与接收库目录；拖放分配优先。"); page.Controls.Add(_storageNote);
            _copy = new CheckBox { AutoSize = true, Checked = true, Text = "复制模型文件（关闭则仅使用已有目标模型）", Font = FormUiKit.BaseFont };
            _copy.CheckedChanged += (s, e) => InvalidatePlan(); page.Controls.Add(_copy);
            _placement = Choice("按实际类型分配", "统一存放目录", "逐个组件包指定", "保持源库相对目录", "自动关联源目录与类型"); page.Controls.Add(_placement);
            _placement.SelectedIndexChanged += (s, e) => { if (!_populating && _plan != null) _plan.Placement = (TransferPlacement)_placement.SelectedIndex; InvalidatePlan(); BuildTypeRules(); UpdateDestination(); };
            // The main page is the single editor for the uniform directory.
            _singleDir = new TextBox { Text="导入" };
            _basis = Choice("SDK 实际对象类型", "Planning 类型", "Prototype 类型", "Family"); page.Controls.Add(_basis);
            _basis.SelectedIndexChanged += (s, e) => { if (!_populating && _plan != null) _plan.Classification = new[] { "sdk", "planning", "prototype", "family" }[_basis.SelectedIndex]; InvalidatePlan(); BuildTypeRules(); };
            _typeRules = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty }; page.Controls.Add(_typeRules);
            _fallback = Field(page, "未分配类型的兜底目录（留空即阻止执行）", v => { if (_plan != null) _plan.DefaultDirectory = v; });
            var fallbackBrowse = Row(); Btn(fallbackBrowse, "选择兜底目录…", () => PickFolder(v => _fallback.Text = v), 144); page.Controls.Add(fallbackBrowse);
            var scheme = Row(); Btn(scheme, "保存目录方案", SaveRules, 124); Btn(scheme, "加载目录方案", LoadRules, 124); page.Controls.Add(scheme);
            page.Controls.Add(Caption("逐项覆盖 / 处理同名冲突", true));
            _package = Choice(); _package.SelectedIndexChanged += (s, e) => ShowPackage(); page.Controls.Add(_package);
            _source = Field(page, "源模型实际位置（可重新定位，原始关联保持不变）"); _source.ReadOnly = true;
            var relocate = Row(); Btn(relocate, "重新定位源 cojt…", ()=>Run(RelocateSource), 152); page.Controls.Add(relocate);
            page.Controls.Add(Caption("单个模型的存放位置统一在主页面拖放分配。"));
            _targetName = Field(page, "目标 cojt 名称（保留 .cojt，可用于消除同名冲突）", v => { if (CurrentPackage != null) CurrentPackage.TargetName = v; });
            _conflict = Choice("同名内容不同则阻止", "使用指定新名称", "人工核验后复用目标", "跳过此模型及其对应实例"); page.Controls.Add(_conflict);
            _conflict.SelectedIndexChanged += (s, e) => { if (!_populating && CurrentPackage != null) { CurrentPackage.Conflict = (TransferConflict)_conflict.SelectedIndex; CurrentPackage.TargetHash = null; InvalidatePlan(); } };
            var verify = Row(); Btn(verify, "查看并核验复用目标…", () => Run(VerifyReuse), 180); page.Controls.Add(verify);
            page.Controls.Add(Caption("同一 cojt 的多个实例共用一次复制。多类型命中不同目录时，请逐项覆盖；没有默认覆盖已有文件。"));
        }



        private void UpdateKind() => UpdateGuidedState();
        private void UpdateDestination()
        {
            if (_destination == null || _singleDir == null) return;
            bool old = _populating; _populating = true;
            try { bool simple = _placement.SelectedIndex == 1;
                int assigned=_sourceDirty?0:_plan?.Packages.Count(p=>!string.IsNullOrWhiteSpace(p.OverrideDirectory))??0;
                _destination.ReadOnly = !simple||assigned>0;
                _destination.Text = assigned>0?"已手动分配 "+assigned+" 个模型；见上方列表":simple ? _singleDir.Text : new[] { "按类型分配（高级规则）","","逐个模型指定","保持源库相对目录","自动匹配源目录 / 文件夹名称 / 类型" }[Math.Max(0,_placement.SelectedIndex)]; }
            finally { _populating=old; }
            UpdateGuidedState();
        }
        private void ToggleLog()
        {
            _logExpanded=!_logExpanded;PluginLog.SetVisible(_log,_logExpanded);_root.RowStyles[5].Height=_logExpanded?100*DeviceDpi/96f:0;
            FitColumns();if(_logExpanded)ExpandForContent();
        }
        private void ShowProblems()
        {
            _issues.Visible=_issues.Items.Count>1;
            if(_issues.Items.Count>0)_issues.SelectedIndex=0;
            else{_issueDetail.Text="";_issueDetail.Visible=false;}
            UpdateGuidedState();FitColumns();ExpandForContent();
        }
        private void ShowFailure(Exception error)
        {
            _settingsWindow?.Hide();
            _status.Text=_uncertain?"结果待确认":"操作未完成"; _issues.Items.Clear(); _issues.Items.Add(error.Message);
            _summary.Text=_uncertain ? "请查询原任务。" : "请查看右侧原因，处理后重新检查。";
            if(_sourceDirty)_sourceCaptureError=error.Message;
            RefreshStorageSources();
            ShowProblems(); Log(error.ToString());
        }
        private void FitColumns()
        {
            if(_fitting||IsDisposed||Disposing)return; _fitting=true;
            try
            {
                foreach(var frame in new[]{_pickFrame,_sourceOriginFrame,_originFrame})((CrossEnvNativeFrame)frame).RefreshMetrics(DeviceDpi);
                FitGuidedCards();
                FitExecution();
                // Measure nested columns from their children. Native controls and wrapped button
                // rows are often taller than FlowLayoutPanel's cached preferred height.
                for(int pass=0;pass<2;pass++)
                {
                    FitColumn(_header); FitColumn(_sourceCard); FitColumn(_targetCard); FitColumn(_problemPanel);
                    foreach(TabPage page in _options.TabPages)FitColumn((MeasuredColumn)page.Controls[0]);
                }
                FitGuidedCards();
                FitExecution();FitColumn(_problemPanel);
            }
            finally { _fitting=false; }
        }
        private void FitColumn(ScrollableControl column)
        {
            bool fitHeight=column!=_main
                &&(column==_header||column.Parent is MeasuredColumn||column.Parent is FlowLayoutPanel);
            if(fitHeight)column.AutoSize=false;
            int width=Math.Max(140,column.ClientSize.Width-column.Padding.Horizontal),height=column.Padding.Vertical;
            foreach(Control child in column.Controls)
            {
                if(!child.Visible)continue;
                int available=Math.Max(120,width-child.Margin.Horizontal);
                child.MaximumSize=new Size(available,child is CrossEnvNativeFrame?child.MaximumSize.Height:0); child.Width=available;
                if(child is Label label)
                {
                    if(label.AutoSize)label.Height=label.GetPreferredSize(new Size(available,0)).Height;
                }
                else if(child is MeasuredColumn measured)FitColumn(measured);
                else if(child is FlowLayoutPanel flow)
                {
                    if(flow.FlowDirection==FlowDirection.TopDown)FitColumn(flow);
                    else
                    {
                        flow.AutoSize=false;
                        flow.Height=flow.GetPreferredSize(new Size(available,0)).Height;
                        flow.PerformLayout();
                        flow.Height=Math.Max(flow.Height,flow.Controls.Cast<Control>().Where(c=>c.Visible).Select(c=>c.Bottom+c.Margin.Bottom).DefaultIfEmpty(0).Max()+flow.Padding.Bottom);
                    }
                }
                height+=child.Height+child.Margin.Vertical;
            }
            if(fitHeight)
            { column.MinimumSize=new Size(0,height); column.Height=height; }
            if(column.AutoScroll)column.AutoScrollMinSize=new Size(0,height);
            column.PerformLayout();
        }
        private void UpdateActions() => UpdateGuidedState();
        private void FitExecution()
        {
            if(_executionTop==null)return;
            int width=Math.Max(240,_problemPanel.ClientSize.Width-_problemPanel.Padding.Horizontal);
            _executionTop.Width=width;bool parallel=width>=ScaleUi(650);int gap=ScaleUi(10),left=parallel?Math.Max(240,(int)(width*.32)):width;
            SetCardWidth(_executionLead,left);SetCardWidth(_executionMessages,parallel?width-left-gap:width);
            if(_issueDetail.Visible)
            {
                int detailWidth=Math.Max(160,_executionMessages.Width-24);
                int textHeight=TextRenderer.MeasureText(_issueDetail.Text,Font,new Size(detailWidth,0),TextFormatFlags.WordBreak).Height;
                _issueDetail.Height=Math.Max(ScaleUi(52),Math.Min(ScaleUi(132),textHeight+12));
            }
            FitColumn(_executionLead);FitColumn(_executionMessages);
            _executionLead.Height=_executionLead.GetPreferredSize(Size.Empty).Height;_executionMessages.Height=_executionMessages.GetPreferredSize(Size.Empty).Height;
            _executionLead.Location=Point.Empty;_executionMessages.Location=parallel?new Point(left+gap,0):new Point(0,_executionLead.Height+4);
            _executionTop.Height=parallel?Math.Max(_executionLead.Height,_executionMessages.Height):_executionLead.Height+_executionMessages.Height+4;
            // The action row has its own reserved bottom row and is never part of
            // the scrollable message area. Shrink messages before hiding actions.
            int actionHeight=_executionActions.GetPreferredSize(new Size(Math.Max(240,_root.ClientSize.Width-_root.Padding.Horizontal),0)).Height;
            int fixedHeight=_header.Height+actionHeight+ScaleUi(26)+(int)_root.RowStyles[5].Height+_root.Padding.Vertical;
            int messageBudget=Math.Max(ScaleUi(60),Math.Min(ScaleUi(220),(_root.ClientSize.Height-fixedHeight)/3));
            _root.RowStyles[2].Height=Math.Min(_executionTop.Height+_problemPanel.Padding.Vertical+4,messageBudget);
            _root.RowStyles[4].Height=ScaleUi(26);
            _problemPanel.MinimumSize=Size.Empty;
            _problemPanel.AutoScrollMinSize=new Size(0,_executionTop.Height+_problemPanel.Padding.Vertical);
        }
        private IWin32Window DialogOwner
        {
            get
            {
                Form owner=_settingsWindow.Visible&&!_settingsWindow.IsDisposed?_settingsWindow:this;
                if(owner.WindowState==FormWindowState.Minimized)owner.WindowState=FormWindowState.Normal;
                owner.BringToFront();owner.Activate();return owner;
            }
        }
        private void ShowSettingsWindow()
        {
            if(!_settingsWindow.Visible)
            {
                var work=Screen.FromControl(this).WorkingArea;
                _settingsWindow.Location=new Point(Math.Max(work.Left,Math.Min(work.Right-_settingsWindow.Width,Right+8)),Math.Max(work.Top,Math.Min(work.Bottom-_settingsWindow.Height,Top+30)));
                _settingsWindow.Show();
            }
            if(_settingsWindow.WindowState==FormWindowState.Minimized)_settingsWindow.WindowState=FormWindowState.Normal;
            _settingsWindow.BringToFront();_settingsWindow.Activate();
        }
        private void ClearScenePickersForReload()
        {
            bool old=_populating;_populating=true;
            try{_objects.Objects=new TxObjectList();_whole.Checked=false;_sourceOrigin.SetLocation(new TxTransformation());_origin.SetLocation(new TxTransformation());_sourceDirty=!_sourceFromFile;}
            finally{_populating=old;}
        }

        private sealed class MeasuredColumn : Panel
        {
            private bool _arranging;
            public override Size GetPreferredSize(Size proposedSize)
            {
                int width=0,height=Padding.Vertical;
                foreach(Control child in Controls)
                    if(child.Visible) { width=Math.Max(width,child.Width+child.Margin.Horizontal); height+=child.Height+child.Margin.Vertical; }
                return new Size(Parent is TableLayoutPanel?0:width+Padding.Horizontal,height);
            }
            protected override void OnLayout(LayoutEventArgs e)
            {
                if(_arranging)return;
                _arranging=true;
                try
                {
                    base.OnLayout(e);
                    int top=Padding.Top+AutoScrollPosition.Y;
                    foreach(Control child in Controls)
                    {
                        if(!child.Visible)continue;
                        top+=child.Margin.Top;
                        child.Location=new Point(Padding.Left+child.Margin.Left+AutoScrollPosition.X,top);
                        top+=child.Height+child.Margin.Bottom;
                    }
                    if(AutoScroll)AdjustFormScrollbars(true);
                }
                finally { _arranging=false; }
            }
        }
    }
}
