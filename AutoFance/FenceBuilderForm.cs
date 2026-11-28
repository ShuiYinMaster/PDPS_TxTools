using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using TxTools.LineToSolid;
using Theme = TxTools.Common.FormUiKit.Theme;

namespace TxTools.FenceBuilder
{
    /// <summary>
    /// 围栏生成器主窗体。
    /// 风格参考 WeldPointAnnotator: 左侧 270px 控制栏 (GroupBox 卡片 + FlowLayoutPanel + AutoSize),
    /// 右侧主区域 (基线列表 + 操作按钮 + 日志面板)。
    /// 窗体非模态,允许用户在窗口打开时继续在 PS 选择对象。
    /// </summary>
    public class FenceBuilderForm : PickAwareTxForm
    {
        // 参数控件
        private NumericUpDown _nudMeshWidth, _nudMeshHeight, _nudMeshThk;
        private NumericUpDown _nudFrameWidth, _nudGroundClear;
        private CheckBox _chkMeshTexture;
        private Panel _pnlMeshColor, _pnlFrameColor;

        private NumericUpDown _nudPostWidth, _nudPostGap, _nudPostTopMargin;
        private CheckBox _chkBaseplate;
        private NumericUpDown _nudBpW, _nudBpL, _nudBpT;
        private Panel _pnlPostColor, _pnlBpColor;

        private CheckBox _chkShareCorner;
        private CheckBox _chkIgnoreZ, _chkForceAxis, _chkUseActiveModeling;
        private NumericUpDown _nudMinLastPanel;

        // 颜色状态（收编 FormUiKit.Theme.Fence* 色系）
        private Color _meshColor = Theme.FenceMesh;
        private Color _frameColor = Theme.FenceFrame;
        private Color _postColor = Theme.FencePost;
        private Color _bpColor = Theme.FenceBaseplate;

        // 基线列表
        private TxObjGridCtrl _baselineGrid;
        private Button _btnAddSel, _btnClearList;

        // DXF 基线
        private List<BaseSegmentChain> _dxfChains = new List<BaseSegmentChain>();
        private string _dxfPath;
        private string _lastDxfDir;
        private bool _dxfDirty;          // 图层过滤/阈值改过，生成前需重读
        private double _dxfMinX, _dxfMinY;
        private Button _btnDxfImport, _btnDxfClear;
        private Label _lblDxfStatus;
        private NumericUpDown _nudDxfIsolated;
        private TextBox _txtDxfLayers;
        private CheckBox _chkDxfToOrigin;

        // 操作按钮
        private Button _btnGenerate, _btnUndo, _btnToggleLog;

        // 日志
        private Panel _logPanel;
        private RichTextBox _logBox;
        private bool _logVisible = true;

        // 撤销记忆
        private TxTools.Common.SceneCreationBatch _lastBatch;

        // 纹理
        private string _texturePath;

        // 字体（统一走 FormUiKit，OS 已 DPI 缩放）
        private readonly Font _baseFont = FormUiKit.BaseFont;
        private readonly Font _boldFont = FormUiKit.BoldFont;
        private static readonly Size _designSize = new Size(1100, 640);
        private bool _dpiApplied;

        public FenceBuilderForm()
        {
            SemiModal = false;
            InitForm();
            BuildUI();
            ResolveTexturePath();


        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, _designSize);
        }

        public override void OnInitTxForm()
        {
            try
            {
                if (_baselineGrid != null)
                {
                    try { _baselineGrid.ListenToPick = true; } catch { }
                    try { _baselineGrid.EnableMultipleSelection = true; } catch { }
                    try { _baselineGrid.EnableRecurringObjects = false; } catch { }
                }
            }
            catch { }
        }

        private void InitForm()
        {
            // 统一窗体规范 + DPI（套件唯一一处缩放设置，详见 FormUiKit）
            FormUiKit.InitStandardForm(this, "围栏生成器 - FenceBuilder",
                _designSize, new Size(960, 600));
        }

        private void BuildUI()
        {
            // ===== 总体骨架: TableLayoutPanel 1行2列 (左侧控制栏固定 320px, 右侧 Fill) =====
            TableLayoutPanel root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(4),
                BackColor = FormUiKit.CardBack
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            this.Controls.Add(root);

            // ===== 左侧: 用 FlowLayoutPanel(纵向滚动) 装多张 GroupBox 卡片 =====
            FlowLayoutPanel sidePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,          // 卡片多于可视高度时纵向滚动
                Padding = new Padding(0)
            };
            sidePanel.Controls.Add(BuildMeshCard());
            sidePanel.Controls.Add(BuildPostCard());
            sidePanel.Controls.Add(BuildDxfCard());
            var optionsCard = BuildOptionCard();
            root.Controls.Add(sidePanel, 0, 0);

            // ===== 右侧: 3 行 1 列 TableLayoutPanel (基线 170固定 / 操作 44固定 / 日志 Fill) =====
            TableLayoutPanel rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(8, 0, 0, 0)
            };
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 基线列表
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));   // 操作
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));  // 日志
            root.Controls.Add(rightLayout, 1, 0);
            optionsCard.Dock = DockStyle.Top;
            rightLayout.Controls.Add(optionsCard, 0, 0);

            // --- 右侧第 1 行: 基线列表卡片 ---
            GroupBox grpBase = new GroupBox
            {
                Text = "基线 (场景特征 / DXF 折线)",
                Font = _boldFont,
                Dock = DockStyle.Fill,
                Padding = new Padding(8, 20, 8, 8)
            };

            // 基线卡片内部: TableLayoutPanel 2行2列 (Grid Fill / 按钮列 140宽 / 底部状态行)
            TableLayoutPanel baseInner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2
            };
            baseInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            baseInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            baseInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            baseInner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _baselineGrid = new TxObjGridCtrl { Dock = DockStyle.Fill };
            // TxObjGridCtrl 拾取焦点统一管理：启动抢焦点 + 点击重获焦点 + ESC 取消焦点
            FormUiKit.GridPickFocus.Wire(_baselineGrid);
            baseInner.Controls.Add(_baselineGrid, 0, 0);

            FlowLayoutPanel baseBtns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(4, 0, 0, 0)
            };
            _btnAddSel = NewFlatButton("从选择添加", 130);
            _btnAddSel.Click += OnAddFromSelection;
            baseBtns.Controls.Add(_btnAddSel);
            _btnClearList = NewFlatButton("清空列表", 130);
            _btnClearList.Click += OnClearList;
            baseBtns.Controls.Add(_btnClearList);
            baseInner.Controls.Add(baseBtns, 1, 0);

            _lblDxfStatus = new Label
            {
                Text = "DXF: 未导入",
                AutoSize = false,
                Height = 32,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _baseFont,
                ForeColor = Theme.LogText,
                Margin = new Padding(0, 4, 0, 0)
            };
            baseInner.Controls.Add(_lblDxfStatus, 0, 1);
            baseInner.SetColumnSpan(_lblDxfStatus, 2);

            grpBase.Controls.Add(baseInner);
            rightLayout.Controls.Add(grpBase, 0, 1);

            // --- 右侧第 2 行: 操作按钮条 ---
            FlowLayoutPanel actFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 6, 0, 6)
            };
            _btnGenerate = NewFlatButton("生成围栏", 140, true);
            _btnGenerate.Click += OnGenerate;
            actFlow.Controls.Add(_btnGenerate);

            _btnUndo = NewFlatButton("清理上次生成", 120);
            _btnUndo.Click += OnUndoLast;
            actFlow.Controls.Add(_btnUndo);

            _btnToggleLog = NewFlatButton("隐藏日志", 120);
            _btnToggleLog.Click += (s, e) =>
            {
                _logVisible = !_logVisible;
                PluginLog.SetVisible(_logPanel, _logVisible);
                _btnToggleLog.Text = _logVisible ? "隐藏日志" : "显示日志";
            };
            actFlow.Controls.Add(_btnToggleLog);
            rightLayout.Controls.Add(actFlow, 0, 2);

            // --- 右侧第 3 行: 日志面板 ---
            _logPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle
            };
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,   // 跟随主题（默认白底黑字，深色主题下自动反色）
                ReadOnly = true,
                BorderStyle = BorderStyle.None
            };
            _logPanel.Controls.Add(_logBox);
            rightLayout.Controls.Add(_logPanel, 0, 3);
            PluginLog.Register(_logPanel, true, _btnToggleLog);
        }

        // ===================== 卡片构建 =====================

        private GroupBox BuildMeshCard()
        {
            GroupBox g = NewCard("网片参数");
            g.Padding = new Padding(6, 14, 6, 6);
            FlowLayoutPanel f = NewCardFlow();

            AddParamRow(f, "标称网片宽度 (mm)", out _nudMeshWidth, 200, 6000, 1000);
            AddParamRow(f, "网片高度 (mm)", out _nudMeshHeight, 500, 4000, 1800);
            AddParamRow(f, "网片薄板厚度 (mm)", out _nudMeshThk, 1, 20, 2);
            AddParamRow(f, "外框方管截面 (mm)", out _nudFrameWidth, 10, 200, 40);
            AddParamRow(f, "离地间隙 (mm)", out _nudGroundClear, 0, 1000, 150);

            _chkMeshTexture = new CheckBox
            {
                Text = "贴网格纹理(失败自动降级)",
                Checked = true,
                AutoSize = true,
                Font = _baseFont
            };
            f.Controls.Add(_chkMeshTexture);

            // 颜色按钮
            AddColorRow(f, "网片薄板颜色", _meshColor, c => _meshColor = c, out _pnlMeshColor);
            AddColorRow(f, "外框方管颜色", _frameColor, c => _frameColor = c, out _pnlFrameColor);

            g.Controls.Add(f);
            return g;
        }

        private GroupBox BuildPostCard()
        {
            GroupBox g = NewCard("立柱与底座");
            g.Padding = new Padding(6, 14, 6, 6);
            FlowLayoutPanel f = NewCardFlow();

            AddParamRow(f, "立柱截面 (mm)", out _nudPostWidth, 20, 300, 60);
            AddParamRow(f, "立柱-网片间隙 (mm)", out _nudPostGap, 0, 200, 5);
            AddParamRow(f, "立柱顶上边距 (mm)", out _nudPostTopMargin, 0, 500, 100);
            AddColorRow(f, "立柱颜色", _postColor, c => _postColor = c, out _pnlPostColor);

            _chkBaseplate = new CheckBox
            {
                Text = "启用底板法兰",
                Checked = false,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 4, 0, 4)
            };
            _chkBaseplate.CheckedChanged += (s, e) =>
            {
                bool en = _chkBaseplate.Checked;
                _nudBpW.Enabled = en; _nudBpL.Enabled = en; _nudBpT.Enabled = en;
                _pnlBpColor.Enabled = en;
            };
            f.Controls.Add(_chkBaseplate);

            AddParamRow(f, "底板宽 (mm)", out _nudBpW, 50, 500, 150);
            AddParamRow(f, "底板长 (mm)", out _nudBpL, 50, 500, 150);
            AddParamRow(f, "底板厚 (mm)", out _nudBpT, 2, 50, 10);
            AddColorRow(f, "底板颜色", _bpColor, c => _bpColor = c, out _pnlBpColor);
            _nudBpW.Enabled = false; _nudBpL.Enabled = false; _nudBpT.Enabled = false;
            _pnlBpColor.Enabled = false;

            g.Controls.Add(f);
            return g;
        }

        private GroupBox BuildDxfCard()
        {
            GroupBox g = NewCard("DXF 基线");
            g.Padding = new Padding(6, 14, 6, 6);
            FlowLayoutPanel f = NewCardFlow();

            Label hint = new Label
            {
                Text = "直接读取 DXF 的 LINE / 多段线 / 圆弧作为围栏基线，"
                     + "可与场景内选择的特征叠加使用。图层过滤留空=全部，多个用逗号分隔。",
                AutoSize = false,
                Width = 284,
                Height = 58,
                Font = _baseFont,
                ForeColor = Theme.LogText,
                Margin = new Padding(0, 0, 0, 4)
            };
            f.Controls.Add(hint);

            FlowLayoutPanel btnRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };
            _btnDxfImport = NewFlatButton("导入 DXF…", 130);
            _btnDxfImport.Click += OnImportDxf;
            btnRow.Controls.Add(_btnDxfImport);

            _btnDxfClear = NewFlatButton("清除 DXF", 130);
            _btnDxfClear.Click += OnClearDxf;
            _btnDxfClear.Enabled = false;
            btnRow.Controls.Add(_btnDxfClear);
            f.Controls.Add(btnRow);

            AddTextRow(f, "图层过滤", out _txtDxfLayers, 120, "");
            _txtDxfLayers.TextChanged += (s, e) => { if (_dxfPath != null) _dxfDirty = true; };

            AddParamRow(f, "丢弃孤立短线 < (mm)", out _nudDxfIsolated, 0, 10000, 1100);
            _nudDxfIsolated.ValueChanged += (s, e) => { if (_dxfPath != null) _dxfDirty = true; };

            _chkDxfToOrigin = new CheckBox
            {
                Text = "生成时平移到原点",
                Checked = false,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 2, 0, 2)
            };
            f.Controls.Add(_chkDxfToOrigin);

            g.Controls.Add(f);
            return g;
        }

        private GroupBox BuildOptionCard()
        {
            GroupBox g = NewCard("通用选项");
            g.Padding = new Padding(6, 14, 6, 6);
            FlowLayoutPanel f = NewCardFlow();

            _chkShareCorner = new CheckBox
            {
                Text = "拐角共享立柱",
                Checked = true,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 1, 0, 1)
            };
            f.Controls.Add(_chkShareCorner);

            _chkIgnoreZ = new CheckBox
            {
                Text = "忽略线段 Z 向(立柱底贴地)",
                Checked = true,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 2, 0, 2)
            };
            f.Controls.Add(_chkIgnoreZ);

            _chkForceAxis = new CheckBox
            {
                Text = "忽略线段夹角(强制横平竖直)",
                Checked = false,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 2, 0, 2)
            };
            f.Controls.Add(_chkForceAxis);

            _chkUseActiveModeling = new CheckBox
            {
                Text = "在当前建模资源下生成",
                Checked = true,
                AutoSize = true,
                Font = _baseFont,
                Margin = new Padding(0, 2, 0, 2)
            };
            f.Controls.Add(_chkUseActiveModeling);

            AddParamRow(f, "末片最小宽度 (mm)", out _nudMinLastPanel, 0, 5000, 500);

            g.Controls.Add(f);
            return g;
        }

        private GroupBox NewCard(string title)
        {
            return new FormUiKit.ColoredGroupBox
            {
                Text = title,
                Font = _boldFont,
                Width = 300,
                MinimumSize = new Size(300, 0),
                Margin = new Padding(0, 0, 0, 8),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                HeaderColor = Theme.CardTitle,
                ForeColor = Theme.CardTitle
            };
        }

        private FlowLayoutPanel NewCardFlow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };
        }

        private void AddParamRow(FlowLayoutPanel parent, string label,
            out NumericUpDown nud, decimal min, decimal max, decimal val)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 1, 0, 1)
            };

            Label lbl = new Label
            {
                Text = label,
                AutoSize = false,
                Width = 160,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _baseFont,
                Height = 24,
                Margin = Padding.Empty
            };
            nud = new NumericUpDown
            {
                Width = 95,
                Height = 24,
                Margin = new Padding(6, 0, 0, 0),
                Minimum = min,
                Maximum = max,
                Value = val,
                DecimalPlaces = (max < 100 ? 1 : 0),
                Increment = (max < 100 ? 0.5m : 10),
                Font = _baseFont
            };
            row.Controls.Add(lbl);
            row.Controls.Add(nud);
            parent.Controls.Add(row);
        }

        private void AddTextRow(FlowLayoutPanel parent, string label,
            out TextBox box, int width, string value)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 1, 0, 1)
            };

            Label lbl = new Label
            {
                Text = label,
                AutoSize = false,
                Width = 160,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _baseFont,
                Height = 24,
                Margin = Padding.Empty
            };
            box = new TextBox
            {
                Width = width,
                Height = 24,
                Margin = new Padding(6, 0, 0, 0),
                Font = _baseFont,
                Text = value ?? ""
            };
            row.Controls.Add(lbl);
            row.Controls.Add(box);
            parent.Controls.Add(row);
        }

        private void AddColorRow(FlowLayoutPanel parent, string label,
            Color initialColor, Action<Color> onColorChanged, out Panel colorPanel)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(0, 1, 0, 1)
            };

            Label lbl = new Label
            {
                Text = label,
                AutoSize = false,
                Width = 160,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = _baseFont,
                Height = 26,
                Margin = Padding.Empty
            };

            // 颜色控件用 Panel 而不是 Button: Panel 的 BackColor 变化立即重绘,
            // 不会被 Windows 按钮系统渲染覆盖
            Panel pnl = new Panel
            {
                Width = 95,
                Height = 24,
                BackColor = initialColor,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 1, 0, 1),
                Cursor = Cursors.Hand
            };

            pnl.Click += (s, e) =>
            {
                if (!pnl.Enabled) return;
                using (ColorDialog cd = new ColorDialog
                {
                    Color = pnl.BackColor,
                    FullOpen = true,
                    AnyColor = true
                })
                {
                    if (cd.ShowDialog() == DialogResult.OK)
                    {
                        pnl.BackColor = cd.Color;
                        pnl.Invalidate();
                        pnl.Update();
                        // 通过 callback 同步外部字段(替代之前用 Tag 反向查找)
                        onColorChanged?.Invoke(cd.Color);
                    }
                }
            };

            row.Controls.Add(lbl);
            row.Controls.Add(pnl);
            parent.Controls.Add(row);
            colorPanel = pnl;
        }

        private Button NewFlatButton(string text, int width, bool primary = false)
        {
            FormUiKit.FlatColorButton b = new FormUiKit.FlatColorButton
            {
                Text = text,
                Width = width,
                Height = 26,
                FlatStyle = FlatStyle.Flat,
                BgColor = primary ? Theme.BtnPrimary : SystemColors.ControlLight,
                ForeColor = primary ? Theme.BtnFore : SystemColors.ControlText,
                BorderColor = FormUiKit.Theme.BtnBorder,
                HoverColor = FormUiKit.Theme.BtnHover,
                Font = primary ? _boldFont : _baseFont,
                Margin = new Padding(0, 0, 4, 0)
            };
            return b;
        }

        // ===================== 操作 =====================

        private void OnAddFromSelection(object sender, EventArgs e)
        {
            try
            {
                TxObjectList sel = TxApplication.ActiveSelection.GetItems();
                if (sel == null || sel.Count == 0)
                {
                    Log("[UI] 当前 PS 中没有选中对象");
                    return;
                }

                // 用 TxTools.LineToSolid 同款 PolylineReader 展开曲线（替代原 FenceBaselineReader）
                var features = new List<ITxObject>();
                foreach (ITxObject obj in sel)
                {
                    if (obj == null) continue;
                    if (PolylineReader.ClassifyFeature(obj) != TxTools.LineToSolid.FeatureKind.Unknown)
                    {
                        if (!features.Contains(obj)) features.Add(obj);
                    }
                    else
                    {
                        int before = features.Count;
                        PolylineReader.CollectCurvesRecursive(obj, features);
                        Log("[UI] 容器展开，新增 " + (features.Count - before) + " 条曲线");
                    }
                }

                if (features.Count == 0)
                {
                    Log("[UI] WARN 选中对象不是曲线，且容器内未找到任何曲线特征");
                    return;
                }
                Log("[UI] 选中 " + sel.Count + " 项，展开后曲线特征 " + features.Count + " 个");

                int added = 0;
                foreach (ITxObject feat in features)
                {
                    try
                    {
                        _baselineGrid.AppendObject(feat);
                        added++;
                    }
                    catch (Exception ex) { Log("[UI] AppendObject 失败: " + ex.Message); }
                }
                Log("[UI] 已添加 " + added + " 个曲线特征到基线列表");
            }
            catch (Exception ex)
            {
                Log("[UI] ERR 从选择添加失败: " + ex.Message);
            }
        }

        private void OnClearList(object sender, EventArgs e)
        {
            try
            {
                _baselineGrid.Objects = new TxObjectList();
                Log("[UI] 列表已清空");
            }
            catch (Exception ex)
            {
                Log("[UI] ERR 清空列表: " + ex.Message);
            }
        }

        private void OnGenerate(object sender, EventArgs e)
        {
            try
            {
                _btnGenerate.Enabled = false;

                // ---- 1) 收集基线链：DXF + 场景特征 ----
                var chains = new List<BaseSegmentChain>();

                if (_dxfChains.Count > 0 || _dxfPath != null)
                {
                    if (_dxfDirty && !string.IsNullOrEmpty(_dxfPath))
                    {
                        Log("[Gen] DXF 读取参数已变更，先重读 " + Path.GetFileName(_dxfPath));
                        ImportDxf(_dxfPath, true);
                    }
                    if (_dxfChains.Count > 0)
                    {
                        bool toOrigin = _chkDxfToOrigin.Checked;
                        double ox = toOrigin ? -_dxfMinX : 0.0;
                        double oy = toOrigin ? -_dxfMinY : 0.0;
                        chains.AddRange(FenceDxfReader.OffsetChains(_dxfChains, ox, oy,
                            toOrigin ? "@origin" : null));
                        Log("[Gen] DXF 基线 " + _dxfChains.Count + " 条链" +
                            (toOrigin
                                ? "，平移 (" + ox.ToString("F1") + ", " + oy.ToString("F1") + ") 到原点"
                                : "，保持图纸坐标"));
                    }
                }

                List<ITxObject> raw = ReadGridObjects();
                if (raw.Count > 0)
                {
                    // 展开容器(资源/零件等) → 提取全部曲线特征（与 OnAddFromSelection 同款逻辑）
                    var baselines = new List<ITxObject>();
                    foreach (ITxObject obj in raw)
                    {
                        if (PolylineReader.ClassifyFeature(obj) != TxTools.LineToSolid.FeatureKind.Unknown)
                            baselines.Add(obj);
                        else
                            PolylineReader.CollectCurvesRecursive(obj, baselines);
                    }
                    Log("[Gen] 网格 " + raw.Count + " 项 → 展开后曲线特征 " + baselines.Count + " 个");
                    foreach (ITxObject obj in baselines)
                    {
                        BaseSegmentChain c = FenceBaselineReader.ReadAsChain(obj, Log);
                        if (c != null) chains.Add(c);
                    }
                }
                else
                {
                    Log("[Gen] 基线列表为空，仅使用 DXF 基线");
                }

                if (chains.Count == 0)
                {
                    Log("[Gen] 没有可用基线：请从场景选择特征，或导入 DXF");
                    return;
                }

                FenceParameters p = CollectParameters();
                Log("[Gen] 开始: 基线链=" + chains.Count +
                    "  网片=" + p.MeshNominalWidth + "×" + p.MeshHeight +
                    "  立柱=" + p.PostWidth + "  底板=" + p.BaseplateMode);

                // ---- 2) 逐链生成 ----
                int totalSolids = 0;
                int doneChains = 0;
                var batch = new TxTools.Common.SceneCreationBatch();
                using (var undo = TxTools.Common.SceneUndoScope.Begin("生成围栏"))
                foreach (BaseSegmentChain chain in chains)
                {
                    FenceLayout layout = FenceLayoutPlanner.Plan(chain, p, Log);
                    if (layout.Posts.Count + layout.Panels.Count == 0) continue;
                    undo.EnsureDocument();
                    var br = new BuildResult();
                    try { FenceGeometryBuilder.Build(layout, p, _texturePath, Log, br); }
                    finally
                    {
                        foreach (var solid in br.CreatedSolids) batch.Add(solid);
                        foreach (var group in br.PanelGroups) batch.AddNewContainer(group);
                        foreach (var group in br.PostGroups) batch.AddNewContainer(group);
                        if (!br.UsedActiveModeling) batch.AddNewContainer(br.Container);
                        if (batch.Count > 0) _lastBatch = batch;
                    }
                    totalSolids += br.CreatedSolids.Count;
                    doneChains++;
                }
                Log("[Gen] 全部完成: 链 " + doneChains + "/" + chains.Count +
                    "，本次共创建 " + totalSolids + " 个 Solid");
            }
            catch (Exception ex)
            {
                Log("[Gen] ERR " + ex.Message + "\r\n" + ex.StackTrace);
            }
            finally
            {
                _btnGenerate.Enabled = true;
            }
        }

        // ===================== DXF 基线 =====================

        private void OnImportDxf(object sender, EventArgs e)
        {
            try
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "选择 DXF 图纸作为围栏基线";
                    dlg.Filter = "DXF 图纸 (*.dxf)|*.dxf|所有文件 (*.*)|*.*";
                    dlg.CheckFileExists = true;
                    if (!string.IsNullOrEmpty(_lastDxfDir) && Directory.Exists(_lastDxfDir))
                        dlg.InitialDirectory = _lastDxfDir;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    _lastDxfDir = Path.GetDirectoryName(dlg.FileName);
                    ImportDxf(dlg.FileName, false);
                }
            }
            catch (Exception ex)
            {
                Log("[DXF] ERR 导入失败: " + ex.Message);
            }
        }

        private void OnClearDxf(object sender, EventArgs e)
        {
            _dxfChains = new List<BaseSegmentChain>();
            _dxfPath = null;
            _dxfDirty = false;
            _dxfMinX = _dxfMinY = 0.0;
            if (_btnDxfClear != null) _btnDxfClear.Enabled = false;
            UpdateDxfStatus(null);
            Log("[DXF] 已清除已导入的 DXF 基线");
        }

        /// <summary>
        /// 解析 DXF 并存入 _dxfChains。quiet=true 时（生成前自动重读）不弹确认框。
        /// </summary>
        private bool ImportDxf(string path, bool quiet)
        {
            try
            {
                var opt = new FenceDxfOptions
                {
                    IsolatedSegmentThreshold = (double)_nudDxfIsolated.Value,
                    LayerFilter = _txtDxfLayers.Text,
                    JoinTolerance = 0.5,
                    EntityZ = 0.0
                };

                FenceDxfResult r = FenceDxfReader.Read(path, opt, Log);
                if (r.Polylines.Count == 0)
                {
                    Log("[DXF] WARN 未解析出可用基线，保留原有导入结果");
                    if (!quiet)
                    {
                        MessageBox.Show(this,
                            "没有从该 DXF 中解析出可用的基线线段。\r\n"
                            + "支持 LINE / LWPOLYLINE / POLYLINE / ARC；如需限定范围请填写图层过滤。",
                            "DXF 导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return false;
                }

                // 大图纸保护：链过多通常说明把设备轮廓一起读进来了
                if (r.Polylines.Count > 200 && !quiet)
                {
                    DialogResult dr = MessageBox.Show(this,
                        "解析出 " + r.Polylines.Count + " 条基线链，总长 " + (r.TotalLength / 1000.0).ToString("F1") + " m。\r\n"
                        + "链数过多通常表示图纸里还包含设备轮廓，建议先填写图层过滤。\r\n\r\n仍要导入吗？",
                        "DXF 导入确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) return false;
                }

                string prefix = "DXF:" + Path.GetFileNameWithoutExtension(path);
                _dxfChains = FenceDxfReader.ToBaseChains(r, 0.0, prefix);
                _dxfPath = path;
                _dxfDirty = false;
                _dxfMinX = r.MinX;
                _dxfMinY = r.MinY;
                if (_btnDxfClear != null) _btnDxfClear.Enabled = true;
                UpdateDxfStatus(r);
                return true;
            }
            catch (Exception ex)
            {
                Log("[DXF] ERR " + ex.Message);
                if (!quiet)
                    MessageBox.Show(this, "读取 DXF 失败：\r\n" + ex.Message,
                        "DXF 导入", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void UpdateDxfStatus(FenceDxfResult r)
        {
            if (_lblDxfStatus == null) return;
            try
            {
                if (r == null || _dxfChains.Count == 0)
                {
                    _lblDxfStatus.Text = "DXF: 未导入";
                    _lblDxfStatus.ForeColor = Theme.LogText;
                    return;
                }

                int segCount = 0;
                foreach (BaseSegmentChain c in _dxfChains) segCount += c.Segments.Count;

                string text = "DXF: " + Path.GetFileName(_dxfPath) + "  —  链 " + _dxfChains.Count
                            + " / 线段 " + segCount
                            + " / 总长 " + (r.TotalLength / 1000.0).ToString("F1") + " m"
                            + "  |  范围 " + (r.Width / 1000.0).ToString("F1") + " × "
                            + (r.Depth / 1000.0).ToString("F1") + " m";
                if (r.DuplicateCount > 0) text += "  |  去重 " + r.DuplicateCount;
                if (r.IsolatedDroppedCount > 0) text += "  |  丢弃孤立短线 " + r.IsolatedDroppedCount;
                _lblDxfStatus.Text = text;
                _lblDxfStatus.ForeColor = Theme.LogText;
            }
            catch { }
        }

        private void OnUndoLast(object sender, EventArgs e)
        {
            try
            {
                if (_lastBatch == null || _lastBatch.Count == 0)
                { Log("[清理] 没有上次生成记录；原生撤销请在 PS 中按 Ctrl+Z。"); return; }
                bool complete = _lastBatch.RemoveCreated(Log);
                if (complete) _lastBatch = null;
                Log(complete ? "[清理] 已清理整批生成物；原有组件保留。本次清理也可 Ctrl+Z 撤销。"
                             : "[清理] 部分对象保留，记录未丢失，可再次清理。");
                TxApplication.RefreshDisplay();
            }
            catch (Exception ex) { Log("[清理] 已停止: " + ex.Message); }
        }

        private List<ITxObject> ReadGridObjects()
        {
            var result = new List<ITxObject>();
            try
            {
                int n = 0;
                try { n = _baselineGrid.Count; }
                catch (Exception ex) { Log("[UI] Count 失败: " + ex.Message); }

                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        ITxObject o = _baselineGrid.GetObject(i) as ITxObject;
                        if (o != null) result.Add(o);
                    }
                    catch (Exception ex) { Log("[UI] GetObject(" + i + ") 失败: " + ex.Message); }
                }
            }
            catch (Exception ex) { Log("[UI] ReadGridObjects 异常: " + ex.Message); }
            return result;
        }

        private FenceParameters CollectParameters()
        {
            return new FenceParameters
            {
                MeshNominalWidth = (double)_nudMeshWidth.Value,
                MeshHeight = (double)_nudMeshHeight.Value,
                MeshThickness = (double)_nudMeshThk.Value,
                MeshFrameWidth = (double)_nudFrameWidth.Value,
                MeshFrameThickness = (double)_nudFrameWidth.Value, // 同宽同厚,正方形截面
                GroundClearance = (double)_nudGroundClear.Value,
                EnableMeshTexture = _chkMeshTexture.Checked,
                MeshColor = _meshColor,
                FrameColor = _frameColor,

                PostWidth = (double)_nudPostWidth.Value,
                PostThickness = (double)_nudPostWidth.Value, // 同宽同厚
                PostGap = (double)_nudPostGap.Value,
                PostTopMargin = (double)_nudPostTopMargin.Value,
                PostColor = _postColor,

                BaseplateMode = _chkBaseplate.Checked ? BaseplateMode.WithBaseplate : BaseplateMode.None,
                BaseplateWidth = (double)_nudBpW.Value,
                BaseplateLength = (double)_nudBpL.Value,
                BaseplateThickness = (double)_nudBpT.Value,
                BaseplateColor = _bpColor,

                ShareCornerPost = _chkShareCorner.Checked,
                IgnoreSegmentZ = _chkIgnoreZ.Checked,
                ForceAxisAligned = _chkForceAxis.Checked,
                CreateUnderActiveModeling = _chkUseActiveModeling.Checked,
                MinLastPanelWidth = (double)_nudMinLastPanel.Value
            };
        }

        private void ResolveTexturePath()
        {
            // 优先级:
            //   1. 嵌入资源(DLL 自带,无需外部部署): 从 manifest stream 读出释放到临时目录
            //   2. DLL 同目录下 Resources\mesh_pattern.png(用户手动部署的)
            //   3. 都没有 → 走降级(半透明纯色)
            try
            {
                // ---- 路径 1: 嵌入资源 ----
                Assembly asm = Assembly.GetExecutingAssembly();
                // 资源名通常是 "<DefaultNamespace>.Resources.mesh_pattern.png"
                // 用反射扫描所有嵌入资源,匹配文件名结尾
                string resName = null;
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("mesh_pattern.png", StringComparison.OrdinalIgnoreCase))
                    {
                        resName = n; break;
                    }
                }
                if (resName != null)
                {
                    string tempPath = TxToolsTemp.FileFor("FenceBuilder",
                        "mesh_pattern_" + asm.GetName().Version + ".png");
                    if (!File.Exists(tempPath))
                    {
                        using (var stream = asm.GetManifestResourceStream(resName))
                        using (var fs = File.Create(tempPath))
                        {
                            stream.CopyTo(fs);
                        }
                    }
                    _texturePath = tempPath;
                    Log("[Init] 纹理就绪(嵌入资源): " + tempPath);
                    return;
                }

                // ---- 路径 2: DLL 同目录的 Resources\mesh_pattern.png ----
                string dllDir = Path.GetDirectoryName(asm.Location);
                string candidate = Path.Combine(dllDir, "Resources", "mesh_pattern.png");
                if (File.Exists(candidate))
                {
                    _texturePath = candidate;
                    Log("[Init] 纹理就绪(同目录): " + _texturePath);
                    return;
                }

                Log("[Init] WARN 未找到嵌入资源,也未找到 " + candidate + ",将使用降级方案");
            }
            catch (Exception ex) { Log("[Init] 纹理定位异常: " + ex.Message); }
        }

        private void Log(string msg)
        {
            try
            {
                if (_logBox == null) return;
                if (_logBox.InvokeRequired)
                {
                    _logBox.BeginInvoke(new Action<string>(Log), msg);
                    return;
                }
                Color c = Theme.LogText;
                if (msg.Contains("ERR")) c = Theme.LogErr;
                else if (msg.Contains("WARN")) c = Theme.LogWarn;

                int start = _logBox.TextLength;
                string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine;
                _logBox.AppendText(line);
                _logBox.Select(start, line.Length);
                _logBox.SelectionColor = c;
                _logBox.Select(_logBox.TextLength, 0);
                _logBox.ScrollToCaret();
            }
            catch { }
        }
    }
}
