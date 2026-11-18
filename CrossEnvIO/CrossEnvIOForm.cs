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
        private bool _scaled, _busy, _populating, _uncertain, _finishedJob, _jobSucceeded, _fitting, _closeRequested;
        private MeasuredColumn _main, _header, _storagePage;
        private GroupBox _problemSection;
        private Panel _problemPanel;
        private TabControl _options;
        private Form _settingsWindow;
        private TableLayoutPanel _root, _footer;
        private ComboBox _kind, _peers, _placement, _basis, _package, _conflict;
        private CheckBox _whole, _copy, _bindOnly, _project,_includeWeldParts;
        private TxObjGridCtrl _objects;
        private TxFrameEditBoxCtrl _sourceOrigin,_origin;
        private TextBox _destination, _singleDir, _fallback, _source, _targetName;
        private Label _scope, _receiver, _storageNote, _status, _summary;
        private TextBox _issueDetail;
        private TableLayoutPanel _executionTop;
        private FlowLayoutPanel _sourceFileActions, _executionToolbar;
        private FlowLayoutPanel _executionActions;
        private FlowLayoutPanel _typeRules;
        private ListBox _issues;
        private RichTextBox _log;
        private Button _next, _cancel, _settings, _query, _browse, _details, _bindingButton,_pickTargetFrame;
        private Button _exportTsv, _exportBundle, _previewTransfer, _cleanupFiles;
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
            BackColor = FormUiKit.CardBack;
            _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10), BackColor = FormUiKit.CardBack };
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute,140));
            _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _header = Column(false); _header.AutoSize = true; _header.Dock = DockStyle.Fill;
            _main = Column(true); _main.Dock = DockStyle.Fill;
            _root.Controls.Add(_header,0,0); _root.Controls.Add(_main,0,1);
            BuildGuidedHeader();
            BuildGuidedCards();
            BuildOptions();
            BuildExecution();
            BuildFooter();
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
            BuildStorageHelp();
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

        private void BuildStorageHelp()
        {
            var page = new TabPage("操作说明") { Padding = new Padding(8), BackColor = FormUiKit.CardBack };
            var instructions = new TextBox
            {
                Dock = DockStyle.Fill,
                Font = FormUiKit.BaseFont,
                Multiline = true,
                ReadOnly = true,
                WordWrap = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                Text = @"1. 准备源数据和接收工程

在主窗口选取场景对象或打开数据文件，确认接收工程后进入「高级规则」。保持「复制模型文件」勾选可复制缺失的目标模型。关闭时，所需模型必须已存在于指定的接收目录。

2. 选定目录分配方式

• 自动关联源目录与类型：优先匹配源库目录，再按模型类型匹配。若有多个候选，回到主窗口，在该模型的「接收目录」列点击「选择目录」。
• 按实际类型分配：先选 SDK 实际对象类型、Planning 类型、Prototype 类型或 Family，再为下方列出的分类选择接收目录。共享模型同时命中不同目录时，在主窗口为该模型单独选定。
• 统一存放目录：回到主窗口，在「目录匹配方式」下输入目录或点击「浏览目录…」。已有手动分配的模型仍按各自的分配存放。
• 逐个组件包指定：在主窗口将模型拖到右侧接收库子目录，也可以先选文件和目录，再点击「分配 →」。
• 保持源库相对目录：沿用模型在源库中的相对目录。源位置不在源库内，或相对路径指向库根时，请手动指定接收库子目录。

手动分配优先。选中模型并点击「清除分配」可恢复按规则计算。自动或按类型分配时，可设置兜底目录处理未匹配项。所有模型应放在接收库的子目录中，例如「设备\夹具」。保存为 JSON 的目录方案可在同一接收库根下加载。

3. 处理源文件缺失或目标同名

先在「逐项覆盖 / 处理同名冲突」下拉框选中模型。源目录已移动时，点击「重新定位源 cojt…」，选择可读的 .cojt 目录。

目标同名且内容不同时，可选「使用指定新名称」并填写带 .cojt 后缀的名称，或选「人工核验后复用目标」并点击「查看并核验复用目标…」。确认这个现有模型可用于重建后再复用。选「跳过此模型及其对应实例」会一并跳过使用该模型的实例。

4. 回主窗口检查并执行

需要时先打开「传输明细」核对目标路径。点击「检查重建条件」，按第三分区指出的问题修正设置。检查通过后点击「确认执行」。传输完成后，在接收工程核对结果并按需保存。"
            };
            page.Controls.Add(instructions);
            _options.TabPages.Add(page);
        }

        private void BuildExecution()
        {
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            content.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var views = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            _problemPanel = new Panel { AutoScroll=true };
            _problemPanel.Dock = DockStyle.Fill;
            _problemSection = CreateSection("3  检查与执行",content);
            views.Controls.Add(_problemPanel);
            _log = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = FormUiKit.CardBack,
                BorderStyle = BorderStyle.None, Font = new Font("Consolas",9), DetectUrls = false, Visible = false };
            views.Controls.Add(_log);
            content.Controls.Add(views,0,0);
            _executionToolbar = Row();
            _executionToolbar.Dock = DockStyle.Fill;
            _executionToolbar.FlowDirection = FlowDirection.RightToLeft;
            _details = Btn(_executionToolbar,"详细日志",ToggleLog,96);
            content.Controls.Add(_executionToolbar,0,1);
            PluginLog.Register(_log,false,_details);
            _log.VisibleChanged += (s,e) =>
            {
                _problemPanel.Visible = !_log.Visible;
                _details.Text = _log.Visible ? "返回检查结果" : "详细日志";
                if (_log.Visible) _log.ScrollToCaret();
                FitColumns();
            };
            _executionTop=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1 };
            _executionTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            _problemPanel.Controls.Add(_executionTop);
            _summary = Caption("尚未检查；检查通过后确认执行。");
            _executionTop.Controls.Add(_summary);
            _issues = new ListBox { Font = Font, Height = 56,MinimumSize=new Size(0,56), DrawMode = DrawMode.OwnerDrawVariable, IntegralHeight = false, Visible = false };
            _issues.MeasureItem += (s,e) => { if(e.Index>=0)e.ItemHeight=Font.Height+8; };
            _issues.DrawItem += (s,e) => { if(e.Index<0)return; e.DrawBackground(); TextRenderer.DrawText(e.Graphics,Convert.ToString(_issues.Items[e.Index]).Split('\n')[0].TrimEnd('\r'),Font,
                new Rectangle(e.Bounds.X+3,e.Bounds.Y+3,e.Bounds.Width-6,e.Bounds.Height-6),e.ForeColor,TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis); e.DrawFocusRectangle(); };
            _issues.Dock=DockStyle.Top; _executionTop.Controls.Add(_issues);
            _issueDetail=new TextBox { Multiline=true,ReadOnly=true,WordWrap=true,BorderStyle=BorderStyle.None,
                Height=64,Font=FormUiKit.BaseFont,BackColor=FormUiKit.CardBack,Visible=false,Margin=new Padding(3,4,3,4) };
            _issueDetail.Dock=DockStyle.Top; _executionTop.Controls.Add(_issueDetail);
            PluginUiPolicy.AllowListScrolling(_issueDetail);
            _issues.SelectedIndexChanged+=(s,e)=>
            {
                _issueDetail.Text=Convert.ToString(_issues.SelectedItem).Replace("\r\n","\n").Replace("\r","\n").Replace("\n",Environment.NewLine);
                _issueDetail.Visible=_issues.SelectedItem!=null;UpdateActions();FitColumns();
            };
            var fixes = Row(); fixes.Tag = "fixes";
            _bindingButton=Btn(fixes,"为选中焊点指定零件…",()=>Run(BindIssue),180); _executionTop.Controls.Add(fixes);
            _root.Controls.Add(_problemSection,0,2);
        }

        private void BuildFooter()
        {
            _footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _status = Caption("");
            _status.Anchor = AnchorStyles.Left;
            _footer.Controls.Add(_status,0,0);
            var actions = Row();_executionActions=actions;actions.Anchor=AnchorStyles.Right;
            actions.FlowDirection=FlowDirection.RightToLeft;
            _next = FormUiKit.MkBtn("检查重建条件",Color.FromArgb(0,111,134),180,34);
            _next.MinimumSize = new Size(180,34); _next.ForeColor = Color.White;
            _next.Click += (s,e) =>
            {
                PluginLog.SetVisible(_log,false);
                if (_uncertain) Run(QueryJob,true); else Run(Transfer);
            };
            actions.Controls.Add(_next);
            _cancel = Btn(actions,"取消复制",()=>_cancellation?.Cancel(),96);
            _query = Btn(actions,"查询结果",()=>Run(QueryJob,true),96); _query.Visible = false;
            _footer.Controls.Add(actions,1,0);
            _root.Controls.Add(_footer,0,3);
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
            PluginLog.SetVisible(_log,!_log.Visible);
        }
        private void ShowProblems()
        {
            PluginLog.SetVisible(_log,false);
            _issues.Visible=_issues.Items.Count>1;
            if(_issues.Items.Count>0)_issues.SelectedIndex=0;
            else{_issueDetail.Text="";_issueDetail.Visible=false;}
            UpdateGuidedState();FitColumns();ExpandForContent();
        }
        private void ShowFailure(Exception error)
        {
            _settingsWindow?.Hide();
            _status.Text=_uncertain?"结果待确认":"操作未完成"; _issues.Items.Clear(); _issues.Items.Add(error.Message);
            _summary.Text=_uncertain ? "请查询原任务。" : "请根据下方提示调整后重试。";
            if(_sourceDirty)_sourceCaptureError=error.Message;
            RefreshStorageSources();
            ShowProblems(); Log(error.ToString());
        }
        private void FitColumns()
        {
            if(_fitting||_footer==null||_status==null||IsDisposed||Disposing)return; _fitting=true;
            try
            {
                FitPickers();
                FitGuidedCards();
                FitExecution();
                // Measure nested columns from their children. Native controls and wrapped button
                // rows are often taller than FlowLayoutPanel's cached preferred height.
                for(int pass=0;pass<2;pass++)
                {
                    FitColumn(_header);
                    FitColumn(_storagePage);
                }
                FitGuidedCards();
                FitExecution();
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
            int width=Math.Max(240,_problemPanel.ClientSize.Width);
            _summary.MaximumSize=new Size(width-_summary.Margin.Horizontal,0);
            if(_issueDetail.Visible)
            {
                int textHeight=TextRenderer.MeasureText(_issueDetail.Text,_issueDetail.Font,new Size(width-_issueDetail.Margin.Horizontal,0),TextFormatFlags.WordBreak).Height;
                int detailHeight=textHeight+ScaleUi(6);
                _issueDetail.ScrollBars=detailHeight>ScaleUi(132)?ScrollBars.Vertical:ScrollBars.None;
                _issueDetail.Height=Math.Min(ScaleUi(132),detailHeight);
            }
            // The action row has its own reserved bottom row and is never part of
            // the scrollable message area. Shrink messages before hiding actions.
            int actionWidth=_executionActions.GetPreferredSize(Size.Empty).Width+_executionActions.Margin.Horizontal;
            _status.MaximumSize=new Size(Math.Max(ScaleUi(80),_footer.ClientSize.Width-actionWidth-_status.Margin.Horizontal),0);
            int fixedHeight=_header.Height+_footer.Height+_root.Padding.Vertical;
            int messageBudget=Math.Max(ScaleUi(60),Math.Min(ScaleUi(220),(_root.ClientSize.Height-fixedHeight)/3));
            int frameHeight=_problemSection.ClientSize.Height-_problemSection.DisplayRectangle.Height+_problemSection.Margin.Vertical;
            int toolbarHeight=_details.Visible?_executionToolbar.GetPreferredSize(new Size(width,0)).Height+_executionToolbar.Margin.Vertical:0;
            int viewHeight=_log.Visible?ScaleUi(112):_executionTop.GetPreferredSize(new Size(width,0)).Height;
            _root.RowStyles[2].Height=Math.Min(viewHeight+frameHeight+toolbarHeight,messageBudget);
            _problemPanel.AutoScroll=true;
        }
        private IWin32Window DialogOwner
        {
            get
            {
                Form owner=Form.ActiveForm==_settingsWindow?_settingsWindow:this;
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
