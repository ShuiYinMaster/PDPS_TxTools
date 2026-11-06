using System;
using System.Drawing;
using System.Windows.Forms;
using C1.Win.C1FlexGrid;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;

namespace TxTools.WeldAnnotator
{
    public partial class WeldAnnotatorForm
    {
        private TableLayoutPanel _workspace, _footer;
        private Label _emptyState;
        private static readonly Color Accent = Color.FromArgb(0, 116, 120);
        private static readonly Color Surface = Color.FromArgb(246, 248, 250);

        private void BuildCardPanel()
        {
            _workspace = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                Padding = new Padding(6), BackColor = Surface
            };
            _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            _workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            Controls.Add(_workspace);

            var sidebar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
                BackColor = Color.White, Padding = new Padding(0),
                Margin = new Padding(0, 0, 6, 0)
            };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int[] heights = { 0, 24, 148, 6, 104, 170 };
            for (int row = 0; row < heights.Length; row++)
                sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, heights[row]));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _workspace.Controls.Add(sidebar, 0, 0);
            _lblOpHint = new Label
            {
                Text = "在 PS 中选择操作节点或视口", Dock = DockStyle.Fill,
                ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft
            };
            sidebar.Controls.Add(_lblOpHint, 0, 1);
            var opHost = new Panel
            {
                Dock = DockStyle.Fill, BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, Padding = new Padding(1)
            };
            _opGrid = new TxObjGridCtrl
            {
                Dock = DockStyle.Fill, MinimumSize = Size.Empty, AutoSize = false,
                ListenToPick = true, EnableMultipleSelection = false, EnableRecurringObjects = false
            };
            _opGrid.ValidatorType = TxValidatorType.Operation;
            opHost.Controls.Add(_opGrid);
            var nodeSection = SectionBox("操作节点");
            nodeSection.Controls.Add(opHost);
            sidebar.Controls.Add(nodeSection, 0, 2);
            FormUiKit.GridPickFocus.Wire(_opGrid);
            _opGrid.ObjectInserted += OnOpInserted;
            _opGrid.RowDeleted += OnOpDeleted;

            var settings = VerticalStack();
            _chkNewSheet = SettingCheck("写入新工作表");
            _chkWriteList = SettingCheck("附加焊点数据表");
            _chkAutoThick = SettingCheck("自动检测板厚");
            settings.Controls.AddRange(new Control[] { _chkNewSheet, _chkWriteList, _chkAutoThick });
            _sharedToolTip.SetToolTip(_chkWriteList, "在截图下方附加焊点数据表（序号、名称、操作、坐标、视口状态和类型）。");
            _sharedToolTip.SetToolTip(_chkAutoThick, "按绑定的不重复零件数设置板层类型；手动修改过的类型不会被覆盖。");
            _chkAutoThick.CheckedChanged += (s, e) => ApplyAutoThicknessIfEnabled();
            var exportSection = SectionBox("导出设置");
            exportSection.Controls.Add(settings);
            sidebar.Controls.Add(exportSection, 0, 4);

            var naming = VerticalStack();
            _cmbLabelMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Width = 174,
                Margin = new Padding(0, 4, 0, 8)
            };
            _cmbLabelMode.Items.AddRange(new object[] { "按序号", "按焊点名", "前缀+序号", "序号+后缀", "序号+焊点名" });
            _cmbLabelMode.SelectedIndex = 0;
            _cmbLabelMode.SelectedIndexChanged += (s, e) => UpdateLabelCardEnabled();
            naming.Controls.Add(_cmbLabelMode);
            var fields = new TableLayoutPanel
            {
                Width = 174, Height = 58, ColumnCount = 2, RowCount = 2,
                Margin = Padding.Empty
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            fields.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            _lblPrefix = new Label { Text = "前缀", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _lblSuffix = new Label { Text = "后缀", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            _txtPrefix = new TextBox { Dock = DockStyle.Fill };
            _txtSuffix = new TextBox { Dock = DockStyle.Fill };
            fields.Controls.Add(_lblPrefix, 0, 0);
            fields.Controls.Add(_txtPrefix, 1, 0);
            fields.Controls.Add(_lblSuffix, 0, 1);
            fields.Controls.Add(_txtSuffix, 1, 1);
            naming.Controls.Add(fields);
            _btnStyle = ActionButton("标注样式…", BtnStyle_Click);
            naming.Controls.Add(_btnStyle);
            var namingSection = SectionBox("标注命名");
            namingSection.Controls.Add(naming);
            sidebar.Controls.Add(namingSection, 0, 5);
            UpdateLabelCardEnabled();

            _footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
                Margin = new Padding(0, 4, 0, 0)
            };
            _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _workspace.Controls.Add(_footer, 0, 1);
            _workspace.SetColumnSpan(_footer, 2);
            _lblCount = new Label
            {
                Text = "焊点 0 · 视口内 0", AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 20, 0)
            };
            _footer.Controls.Add(_lblCount, 0, 0);
            _btnExport = ActionButton("导出 Excel", BtnExport_Click, true);
            _btnExport.Enabled = false;
            _btnExport.Anchor = AnchorStyles.Right;
            _footer.Controls.Add(_btnExport, 2, 0);
            _sharedToolTip.SetToolTip(_btnExport, "选择包含焊点的操作节点后导出。");
        }

        private Label SectionTitle(string text)
        {
            return new FormUiKit.FlatColorLabel
            {
                Text = text, Width = 210, Height = 28, Margin = Padding.Empty,
                Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(40, 48, 56),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private GroupBox SectionBox(string text)
        {
            return new FormUiKit.ColoredGroupBox
            {
                Text = text, Dock = DockStyle.Fill,
                // GroupBox already reserves the title font height in DisplayRectangle.
                Padding = new Padding(8, 3, 8, 6), Margin = new Padding(0, 0, 0, 4),
                BackColor = Color.White, HeaderColor = Color.FromArgb(40, 48, 56),
                BorderColor = Color.FromArgb(190, 198, 205)
            };
        }

        private FlowLayoutPanel VerticalStack()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Margin = Padding.Empty
            };
        }

        private CheckBox SettingCheck(string text)
        {
            return new CheckBox { Text = text, AutoSize = true, Margin = new Padding(0, 2, 0, 3) };
        }

        private Button ActionButton(string text, EventHandler handler, bool primary = false)
        {
            var button = new FormUiKit.FlatColorButton
            {
                Text = text, Width = primary ? 100 : 86, Height = 26,
                Font = primary ? new Font(Font, FontStyle.Bold) : Font,
                BgColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(40, 48, 56),
                BorderColor = primary ? Accent : Color.FromArgb(204, 210, 216),
                HoverColor = primary ? Color.FromArgb(0, 98, 102) : Color.FromArgb(235, 243, 244),
                Margin = new Padding(0, 2, 6, 2)
            };
            button.Click += handler;
            return button;
        }

        private void UpdateLabelCardEnabled()
        {
            if (_cmbLabelMode == null) return;
            var mode = (LabelNamingMode)_cmbLabelMode.SelectedIndex;
            _lblPrefix.Enabled = _txtPrefix.Enabled = mode == LabelNamingMode.Prefix;
            _lblSuffix.Enabled = _txtSuffix.Enabled = mode == LabelNamingMode.Suffix;
            _sharedToolTip.SetToolTip(_txtPrefix, "选择“前缀+序号”后可填写。");
            _sharedToolTip.SetToolTip(_txtSuffix, "选择“序号+后缀”后可填写。");
        }

        private void BuildMainArea()
        {
            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                Margin = Padding.Empty, BackColor = Color.White
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
            };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty
            };
            _btnSnap = ActionButton("拍摄快照", BtnSnap_Click);
            _btnShowOnly = ActionButton("仅显示外观", BtnShowOnly_Click);
            _btnRestore = ActionButton("恢复显示", BtnRestore_Click);
            _btnRestore.Enabled = false;
            actions.Controls.AddRange(new Control[] { _btnSnap, _btnShowOnly, _btnRestore });
            _sharedToolTip.SetToolTip(_btnSnap, "记录当前场景显示状态，可重复恢复。");
            _sharedToolTip.SetToolTip(_btnShowOnly, "保留操作绑定外观、机器人及工具，隐藏其他对象，并保留恢复记录。");
            _sharedToolTip.SetToolTip(_btnRestore, "拍摄快照或执行显示操作后，可以恢复场景显示状态。");
            _lblSnapStatus = new Label
            {
                Text = "可先拍摄快照，保存当前显示状态",
                Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = Padding.Empty,
                TextAlign = ContentAlignment.MiddleLeft
            };
            toolbar.Controls.Add(actions, 0, 0);
            toolbar.Controls.Add(_lblSnapStatus, 0, 1);
            var displaySection = SectionBox("显示控制");
            displaySection.Controls.Add(toolbar);
            main.Controls.Add(displaySection, 0, 0);
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            heading.Controls.Add(SectionTitle("焊点列表"), 0, 0);
            _btnClear = ActionButton("清空列表", BtnClear_Click);
            _btnClear.Enabled = false;
            heading.Controls.Add(_btnClear, 1, 0);
            main.Controls.Add(heading, 0, 1);
            var gridHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BorderStyle = BorderStyle.FixedSingle };
            main.Controls.Add(gridHost, 0, 2);
            _grid = new TxFlexGrid
            {
                Dock = DockStyle.Fill, AllowEditing = true, SelectionMode = SelectionModeEnum.Row
            };
            _grid.Rows.Fixed = 1;
            _grid.Cols.Count = 8;
            _grid.Rows.Count = 1;
            string[] headers = { "#", "焊点名称", "操作名", "X (mm)", "Y (mm)", "Z (mm)", "预览", "类型" };
            int[] widths = { 28, 148, 54, 66, 66, 66, 40, 120 };
            for (int c = 0; c < headers.Length; c++)
            {
                _grid[0, c] = headers[c];
                _grid.Cols[c].Width = widths[c];
                _grid.Cols[c].AllowSorting = false;
                _grid.Cols[c].AllowEditing = c == C_TYPE;
            }
            var typeEditor = new CategoryComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = Font };
            typeEditor.Items.AddRange(CATEGORY_OPTIONS);
            _grid.Cols[C_TYPE].Editor = typeEditor;
            typeEditor.SelectionChangeCommitted += (s, e) => _grid.FinishEditing();
            _grid.Cols[C_TYPE].TextAlign = TextAlignEnum.CenterCenter;
            for (int c = C_X; c <= C_Z; c++)
                _grid.Cols[c].TextAlign = TextAlignEnum.RightCenter;
            _grid.Font = Font;
            _grid.Rows.DefaultSize = 23;
            _grid.Rows[0].Height = 25;
            _grid.BackColor = Color.White;
            _grid.Styles[CellStyleEnum.EmptyArea].BackColor = Color.White;
            _grid.DrawMode = DrawModeEnum.OwnerDraw;
            _grid.OwnerDrawCell += DrawPreviewCell;
            _grid.SizeChanged += (s, e) => FitGridColumns();
            _grid.Styles[CellStyleEnum.Fixed].BackColor = Theme.GridHeader;
            _grid.Styles[CellStyleEnum.Fixed].ForeColor = SystemColors.ControlText;
            _grid.Styles[CellStyleEnum.Fixed].Font = new Font(Font, FontStyle.Bold);
            _grid.Styles[CellStyleEnum.Alternate].BackColor = Theme.GridAlt;
            _grid.Styles[CellStyleEnum.Highlight].BackColor = Color.FromArgb(216, 240, 238);
            _grid.Styles[CellStyleEnum.Highlight].ForeColor = Color.FromArgb(30, 55, 55);
            _grid.AfterEdit += Grid_AfterEdit;
            gridHost.Controls.Add(_grid);
            _emptyState = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Text = "尚未添加焊点\n\n请在 Process Simulate 中选择操作节点或视口\n焊点将自动显示在此处",
                BackColor = Color.White, ForeColor = Color.DimGray
            };
            gridHost.Controls.Add(_emptyState);
            _emptyState.BringToFront();
            _workspace.Controls.Add(main, 1, 0);
        }

        private void BuildStatusBar()
        {
            _status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = Surface };
            _lblStatus = new ToolStripStatusLabel("就绪")
            {
                Spring = true, TextAlign = ContentAlignment.MiddleLeft
            };
            _progress = new ToolStripProgressBar
            {
                Width = 100, Visible = false, Alignment = ToolStripItemAlignment.Right
            };
            var logButton = new ToolStripButton("日志 ▲");
            logButton.Click += (s, e) => ToggleLog(logButton);
            PluginLog.Register(_logPanel, false, logButton);
            _status.Items.AddRange(new ToolStripItem[] { _lblStatus, _progress, logButton });
            _footer.Controls.Add(_status, 1, 0);
        }

        private void FitGridColumns()
        {
            if (_grid == null || _grid.Cols.Count != 8) return;
            // Keep coordinates and type readable without letting long names expand the table.
            float scale = _grid.Rows[0].Height / 25f;
            int[] widths = { 28, 148, 54, 66, 66, 66, 40, 120 };
            int total = 0;
            for (int c = 0; c < widths.Length; c++)
            {
                widths[c] = (int)Math.Round(widths[c] * scale);
                total += widths[c];
            }
            int available = _grid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4;
            widths[C_NAME] = Math.Max((int)(100 * scale), widths[C_NAME] + available - total);
            for (int c = 0; c < widths.Length; c++) _grid.Cols[c].Width = widths[c];
        }

        private void DrawPreviewCell(object sender, OwnerDrawCellEventArgs e)
        {
            if (e.Row < 1 || e.Col != C_PREVIEW || e.Measuring) return;
            e.DrawCell(DrawCellFlags.Background | DrawCellFlags.Border);
            string category = _grid[e.Row, C_TYPE] as string ?? DEFAULT_CATEGORY;
            int shape = 9;
            bool filled = true;
            if (_style.CategoryShapes != null && _style.CategoryShapes.ContainsKey(category))
                shape = _style.CategoryShapes[category];
            if (_style.CategoryFilled != null && _style.CategoryFilled.ContainsKey(category))
                filled = _style.CategoryFilled[category];
            float size = Math.Min(e.Bounds.Height - 7, e.Bounds.Width - 8);
            if (size <= 0) return;
            float x = e.Bounds.Left + (e.Bounds.Width - size) / 2;
            float y = e.Bounds.Top + (e.Bounds.Height - size) / 2;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                switch (shape)
                {
                    case 1: path.AddRectangle(new RectangleF(x, y, size, size)); break;
                    case 5:
                        float r = size / 3;
                        path.AddArc(x, y, r, r, 180, 90);
                        path.AddArc(x + size - r, y, r, r, 270, 90);
                        path.AddArc(x + size - r, y + size - r, r, r, 0, 90);
                        path.AddArc(x, y + size - r, r, r, 90, 90);
                        path.CloseFigure();
                        break;
                    case 4:
                        path.AddPolygon(new[] { new PointF(x + size / 2, y), new PointF(x + size, y + size / 2),
                            new PointF(x + size / 2, y + size), new PointF(x, y + size / 2) });
                        break;
                    case 7:
                        path.AddPolygon(new[] { new PointF(x + size / 2, y), new PointF(x + size, y + size), new PointF(x, y + size) });
                        break;
                    case 10:
                    case 12:
                        int count = shape == 12 ? 10 : 6;
                        var vertices = new PointF[count];
                        for (int i = 0; i < count; i++)
                        {
                            double angle = -Math.PI / 2 + i * Math.PI * 2 / count;
                            float radius = size / 2 * (shape == 12 && i % 2 == 1 ? 0.4f : 1f);
                            vertices[i] = new PointF(x + size / 2 + radius * (float)Math.Cos(angle),
                                y + size / 2 + radius * (float)Math.Sin(angle));
                        }
                        path.AddPolygon(vertices);
                        break;
                    default: path.AddEllipse(x, y, size, size); break;
                }
                var previous = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(_style.DotColor))
                using (var pen = new Pen(_style.DotColor, filled ? 1f : 1.5f))
                {
                    if (filled) e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                }
                e.Graphics.SmoothingMode = previous;
            }
        }

        // Paint both the selected value and popup items explicitly under the PS skin.
        private sealed class CategoryComboBox : ComboBox
        {
            public CategoryComboBox()
            {
                DrawMode = System.Windows.Forms.DrawMode.OwnerDrawFixed;
                BackColor = Color.White;
                ForeColor = Color.FromArgb(35, 40, 45);
                IntegralHeight = false;
                MaxDropDownItems = 12;
                DropDownHeight = 260;
                DropDownWidth = 180;
                ItemHeight = 22;
            }

            public void C1EditorInitialize(object value, System.Collections.IDictionary attributes)
            {
                SelectedItem = value as string;
                BackColor = Color.White;
                ForeColor = Color.FromArgb(35, 40, 45);
                if (attributes["Font"] is Font editorFont) Font = editorFont;
            }

            public object C1EditorGetValue() { return SelectedItem ?? Text; }
            public bool C1EditorValueIsValid() { return SelectedIndex >= 0; }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Color.FromArgb(216, 240, 238) : Color.White))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                string text = e.Index >= 0 && e.Index < Items.Count ? Items[e.Index].ToString() : Text;
                TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -3, 0),
                    Color.FromArgb(35, 40, 45), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                e.DrawFocusRectangle();
            }
        }

        private void BuildLogPanel()
        {
            _logPanel = new Panel { Dock = DockStyle.Bottom, Height = 110, Visible = false };
            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill, BackColor = Theme.LogBg, ForeColor = Theme.LogText,
                Font = new Font("Consolas", 8f), ReadOnly = true
            };
            _logPanel.Controls.Add(_logBox);
            _grid.Parent.Controls.Add(_logPanel);
        }

        private void ToggleLog(ToolStripButton button)
        {
            if (!PluginLog.Enabled) return;
            _logVisible = !_logVisible;
            PluginLog.SetVisible(_logPanel, _logVisible);
            button.Text = _logVisible ? "日志 ▼" : "日志 ▲";
        }
    }
}
