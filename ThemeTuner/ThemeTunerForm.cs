// ThemeTunerForm.cs — 主题调整窗体：
//   1) 预设：系统默认(白底黑字) / 冷色(经典蓝) / 浅色(柔和) / 琥珀金 / 草木绿 / 马卡龙(柔和粉彩) / 扁平无色
//   2) 自定义：点击色块用 ColorDialog 改关键槽位
//   3) 预览：实时反映当前调色板
//   4) [应用并保存]：写入 FormUiKit.Theme 全局字段 + 重绘已打开窗体 + 持久化 theme.cfg

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;
using FlatColorButton = TxTools.Common.FormUiKit.FlatColorButton;
using FlatColorLabel = TxTools.Common.FormUiKit.FlatColorLabel;

namespace TxTools.ThemeTuner
{
    public partial class ThemeTunerForm : TxForm
    {
        private enum LogLevel { Info, Ok, Warn, Error }

        // ── 状态 ─────────────────────────────────────────────────────────
        private Dictionary<string, string> _palette = new Dictionary<string, string>();
        private readonly Dictionary<string, FlatColorButton> _swatches =
            new Dictionary<string, FlatColorButton>();
        private static readonly Size _designSize = new Size(680, 540);
        private bool _dpiApplied;
        private bool _loadingPreset;

        // ── 控件 ─────────────────────────────────────────────────────────
        private ComboBox _cmbPreset;
        private Panel _previewHost;
        private FlatColorLabel _previewTitle;
        private FlatColorButton _previewPrimary;
        private FlatColorButton _previewSecondary;
        private RichTextBox _previewLog;
        private RichTextBox _rtbStatus;
        private FlatColorButton _btnFlat;
        private FlatColorButton _btnApply;
        private FlatColorButton _btnReset;

        // 可自定义的关键色槽位（字段名 → 中文名）
        private static readonly string[][] Slots =
        {
            new[] { "BtnPrimary", "主按钮" },
            new[] { "BtnSecondary", "次要按钮" },
            new[] { "BtnMuted", "工具按钮" },
            new[] { "BtnDanger", "危险按钮" },
            new[] { "CardTitle", "卡片标题" },
            new[] { "TitleBack", "卡片标题条" },
            new[] { "CardBack", "卡片底色" },
            new[] { "CardBorder", "卡片边框" },
            new[] { "LogBg", "日志背景" },
            new[] { "LogText", "日志文字" },
        };

        public ThemeTunerForm(SynchronizationContext psCtx)
        {
            SemiModal = false;

            FormUiKit.InitStandardForm(this, "主题调整 (ThemeTuner)",
                _designSize, new Size(640, 540), sizable: true);
            this.Padding = new Padding(4);

            foreach (var kv in Theme.SnapshotToStrings())
                _palette[kv.Key] = kv.Value;

            BuildBody();
            BuildBottomBar();

            RefreshSwatches();
            RefreshPreview();
            Log(FormUiKit.FlatStyleEnabled
                ? "已载入主题（扁平风格开启，颜色设置不生效，仅保留开关）。"
                : "已载入当前主题。选预设或点色块自定义后，点 [应用并保存] 生效。");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, _designSize);
        }

        // ════════════════════════════════════════════════════════════════
        //  UI
        // ════════════════════════════════════════════════════════════════
        private void BuildBody()
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2,
                Padding = new Padding(6), Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var preset = BuildPresetCard();
            preset.Dock = DockStyle.Top;
            body.Controls.Add(preset, 0, 0);
            body.SetColumnSpan(preset, 2);
            var colors = BuildColorCard();
            colors.Dock = DockStyle.Fill;
            body.Controls.Add(colors, 0, 1);
            var feedback = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            feedback.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            feedback.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            feedback.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
            var preview = BuildPreviewCard();
            preview.Dock = DockStyle.Fill;
            var status = BuildStatusCard();
            status.Dock = DockStyle.Fill;
            feedback.Controls.Add(preview, 0, 0);
            feedback.Controls.Add(status, 0, 1);
            body.Controls.Add(feedback, 1, 1);
            this.Controls.Add(body);
        }

        private Control BuildPresetCard()
        {
            var card = FormUiKit.MkCard("主题预设", 660, 96, out var content);

            var row = FormUiKit.MkRowFlow();
            row.Controls.Add(FormUiKit.MkFieldLabel("预设:"));
            _cmbPreset = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 220,
                Font = FormUiKit.BaseFont
            };
            _cmbPreset.Items.AddRange(new object[]
            {
                "系统默认（白底黑字）", "冷色（经典蓝）", "浅色（柔和）", "琥珀金", "草木绿", "马卡龙（柔和粉彩）", "扁平无色（系统灰）"
            });
            _cmbPreset.SelectedIndex = 0;
            _cmbPreset.SelectedIndexChanged += (s, e) => OnPresetChanged();
            row.Controls.Add(_cmbPreset);
            content.Controls.Add(row);

            var hint = new Label
            {
                AutoSize = true,
                Font = FormUiKit.BaseFont,
                ForeColor = Theme.TextDim,
                Text = "预设只改工作调色板；点 [应用并保存] 才写入并重绘已打开的所有插件窗口。"
            };
            content.Controls.Add(hint);
            return card;
        }

        private Control BuildColorCard()
        {
            var card = FormUiKit.MkCard("自定义颜色（点击色块选取）", 660, 200, out var content);

            var twoCol = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = Padding.Empty
            };
            var left = NewSlotColumn();
            for (int i = 0; i < Slots.Length; i++)
                left.Controls.Add(MakeSlotRow(Slots[i][0], Slots[i][1]));
            twoCol.Controls.Add(left);
            content.Controls.Add(twoCol);
            return card;
        }

        private static FlowLayoutPanel NewSlotColumn()
        {
            return new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 0, 16, 0),
                Padding = Padding.Empty
            };
        }

        private Control MakeSlotRow(string key, string label)
        {
            var row = FormUiKit.MkRowFlow();
            row.Margin = Padding.Empty;
            var lbl = FormUiKit.MkFieldLabel(label);
            lbl.Margin = new Padding(0, 2, 8, 0);
            lbl.Height = 22;
            lbl.Width = 70;
            lbl.AutoSize = false;
            row.Controls.Add(lbl);

            var sw = new FlatColorButton
            {
                Height = 26,
                Width = 150,
                Font = FormUiKit.BaseFont,
                Tag = key,
                Margin = new Padding(0, 2, 4, 2)
            };
            sw.Click += (s, e) => PickColor(key, sw);
            _swatches[key] = sw;
            row.Controls.Add(sw);
            return row;
        }

        private Control BuildPreviewCard()
        {
            var card = FormUiKit.MkCard("预览", 660, 170, out var content);

            _previewHost = new Panel
            {
                Width = 660 - 20,
                Height = 150,
                Padding = new Padding(6),
                BorderStyle = BorderStyle.FixedSingle
            };

            _previewTitle = new FlatColorLabel
            {
                Dock = DockStyle.Top,
                Height = 24,
                Font = FormUiKit.BoldFont,
                Text = "标题栏 —— 卡片标题色"
            };

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 34,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 0)
            };
            _previewPrimary = NewPreviewButton();
            _previewPrimary.Text = "主按钮";
            _previewSecondary = NewPreviewButton();
            _previewSecondary.Text = "次要按钮";
            btnRow.Controls.Add(_previewPrimary);
            btnRow.Controls.Add(_previewSecondary);

            _previewLog = new RichTextBox
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                WordWrap = false,
                Font = FormUiKit.BaseFont
            };
            PluginLog.Register(_previewLog);

            _previewHost.Controls.Add(_previewLog);
            _previewHost.Controls.Add(btnRow);
            _previewHost.Controls.Add(_previewTitle);
            content.Controls.Add(_previewHost);
            // 预览面板宽度跟随卡片内容区，窗体变窄时不会横向溢出
            FormUiKit.FillWidthInFlow(content, _previewHost);
            return card;
        }

        private static FlatColorButton NewPreviewButton()
        {
            return new FlatColorButton
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = 26,
                Font = FormUiKit.BaseFont,
                Margin = new Padding(0, 2, 8, 2),
                Padding = new Padding(10, 2, 10, 2)
            };
        }

        private Control BuildStatusCard()
        {
            var card = FormUiKit.MkCard("状态", 660, 96, out var content);
            PluginLog.Register(card);
            _rtbStatus = new RichTextBox
            {
                AutoSize = false,
                Width = 660 - 18,
                Height = 80,
                BackColor = Theme.LogBg,
                ForeColor = Theme.LogText,
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                WordWrap = true,
                Font = FormUiKit.BaseFont
            };
            content.Controls.Add(_rtbStatus);
            // 宽度跟随卡片内容区，卡片随列宽变化时日志框不会溢出/留白
            FormUiKit.FillWidthInFlow(content, _rtbStatus);
            return card;
        }

        private void BuildBottomBar()
        {
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(4),
                BackColor = FormUiKit.CardBack
            };
            var btnApply = FormUiKit.MkFuncButton("应用并保存", Theme.BtnPrimary);
            btnApply.Click += (s, e) => ApplyAndSave();
            _btnApply = (FlatColorButton)btnApply;
            var btnFlat = FormUiKit.MkFuncButton(FlatText(), Theme.BtnMuted);
            btnFlat.Click += (s, e) => ToggleFlatStyle();
            _btnFlat = (FlatColorButton)btnFlat;
            var btnReset = FormUiKit.MkFuncButton("恢复默认", Theme.BtnMuted);
            btnReset.Click += (s, e) => ResetToDefault();
            _btnReset = (FlatColorButton)btnReset;
            var btnClose = FormUiKit.MkFuncButton("关闭", Theme.BtnDanger);
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnApply);
            bar.Controls.Add(btnFlat);
            bar.Controls.Add(btnReset);
            bar.Controls.Add(btnClose);
            var showLogs = new CheckBox { Text = "显示插件日志", AutoSize = true,
                Checked = PluginLog.Enabled, Visible = PluginLog.Available, Margin = new Padding(8, 6, 0, 0) };
            showLogs.CheckedChanged += (s, e) => PluginLog.Enabled = showLogs.Checked;
            bar.Controls.Add(showLogs);
            this.Controls.Add(bar);
            SyncColorUiState();
        }

        // ════════════════════════════════════════════════════════════════
        //  逻辑
        // ════════════════════════════════════════════════════════════════
        private void OnPresetChanged()
        {
            if (_loadingPreset || _cmbPreset == null) return;
            Dictionary<string, string> p;
            switch (_cmbPreset.SelectedIndex)
            {
                case 0: p = Theme.DefaultPalette(); break;
                case 1: p = Theme.CoolPalette(); break;
                case 2: p = Theme.LightPalette(); break;
                case 3: p = Theme.AmberPalette(); break;
                case 4: p = Theme.GreenPalette(); break;
                case 5: p = Theme.MacaronPalette(); break;
                case 6: p = Theme.FlatPalette(); break;
                default: return;
            }
            _palette = p;
            RefreshSwatches();
            RefreshPreview();
            Log("已切换到预设（未保存）。");
        }

        private void PickColor(string key, FlatColorButton sw)
        {
            using (var dlg = new ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = G(key);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _palette[key] = dlg.Color.R + "," + dlg.Color.G + "," + dlg.Color.B;
                RefreshSwatches();
                RefreshPreview();
            }
        }

        private void ApplyAndSave()
        {
            try
            {
                var old = Theme.SnapshotToStrings();
                Theme.SetFrom(_palette);
                FormUiKit.RecolorOpenForms(old, Theme.SnapshotToStrings());
                var path = Theme.SaveConfig(_palette);
                Log(path != null
                    ? "已应用主题并保存到 " + path
                    : "已应用主题（保存失败，仅本次会话生效）", LogLevel.Ok);
            }
            catch (Exception ex) { Log("应用失败: " + ex.Message, LogLevel.Error); }
        }

        private void ResetToDefault()
        {
            _loadingPreset = true;
            try { _cmbPreset.SelectedIndex = 0; } finally { _loadingPreset = false; }
            _palette = Theme.DefaultPalette();
            RefreshSwatches();
            RefreshPreview();
            ApplyAndSave();
        }

        // ── 扁平风格开关 ──────────────────────────────────────────────────
        // false：自定义配色生效，但 AFW 不擦窗体背景 → 边缘残留灰边
        // true ：Siemens 扁平风格接管，无灰边，但自定义配色不生效
        private string FlatText()
        {
            return FormUiKit.FlatStyleEnabled ? "扁平风格: 开" : "扁平风格: 关";
        }

        private void ToggleFlatStyle()
        {
            FormUiKit.FlatStyleEnabled = !FormUiKit.FlatStyleEnabled;
            ApplyFlatStyleToOpenForms();
            Theme.SaveConfig(_palette);
            RefreshFlatButton();
            SyncColorUiState();
            Log(FormUiKit.FlatStyleEnabled
                ? "扁平风格: 开（无灰边，Siemens 皮肤接管配色，颜色设置已禁用）"
                : "扁平风格: 关（自定义配色生效，窗体边缘可能残留灰边）", LogLevel.Ok);
        }

        /// <summary>
        /// 同步颜色设置 UI 的可用状态：扁平风格开启时配色不生效，
        /// 禁用预设下拉、色块、预览与应用/重置按钮，避免误操作。
        /// </summary>
        private void SyncColorUiState()
        {
            bool flat = FormUiKit.FlatStyleEnabled;
            try { if (_cmbPreset != null) _cmbPreset.Enabled = !flat; } catch { }
            try
            {
                foreach (var sw in _swatches.Values)
                {
                    sw.Enabled = !flat;
                    sw.Text = flat ? "（扁平风格下不生效）" : sw.Text;
                }
            }
            catch { }
            try { if (_previewHost != null) _previewHost.Enabled = !flat; } catch { }
            try { if (_btnApply != null) _btnApply.Enabled = !flat; } catch { }
            try { if (_btnReset != null) _btnReset.Enabled = !flat; } catch { }
        }

        private void RefreshFlatButton()
        {
            if (_btnFlat == null) return;
            _btnFlat.Text = FlatText();
            Color bg = FormUiKit.FlatStyleEnabled ? Theme.BtnPrimary : Theme.BtnMuted;
            _btnFlat.BgColor = bg;
            _btnFlat.BorderColor = bg;
            _btnFlat.ForeColor = bg.GetBrightness() > 0.62f ? Color.Black : Color.White;
        }

        private static void ApplyFlatStyleToOpenForms()
        {
            try
            {
                foreach (Form f in Application.OpenForms)
                {
                    if (!FormUiKit.IsTxToolsForm(f)) continue;
                    if (f is TxForm tx)
                    {
                        // 反射设置：旧版 PS 的 TxForm 无 FlatStyleEnabled 时静默跳过
                        FormUiKit.TrySetFlatStyle(tx, FormUiKit.FlatStyleEnabled);
                    }
                    // 扁平开启：子容器默认灰底(240,240,240)统一刷白
                    if (FormUiKit.FlatStyleEnabled)
                    {
                        FormUiKit.FlattenGrayBackgrounds(f);
                    }
                    try { f.Invalidate(true); f.Refresh(); } catch { }
                }
            }
            catch { }
        }

        // ── 刷色 ─────────────────────────────────────────────────────────
        private void RefreshSwatches()
        {
            bool flat = FormUiKit.FlatStyleEnabled;
            foreach (var kv in _swatches)
            {
                if (!_palette.TryGetValue(kv.Key, out string v)
                    || !Theme.TryParseRgb(v, out Color c)) continue;
                var b = kv.Value;
                b.BgColor = c;
                b.BorderColor = c;
                b.ForeColor = c.GetBrightness() > 0.62f ? Color.Black : Color.White;
                b.Enabled = !flat;
                b.Text = flat ? "（扁平风格下不生效）"
                              : "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            }
        }

        private void RefreshPreview()
        {
            if (_previewHost == null) return;
            _previewHost.BackColor = G("CardBack");
            _previewTitle.BackColor = G("TitleBack");
            _previewTitle.ForeColor = Theme.BtnFore;
            _previewTitle.Text = "标题栏 —— 卡片标题色 " + Hex(G("CardTitle"));
            SetBtn(_previewPrimary, "主按钮", G("BtnPrimary"));
            SetBtn(_previewSecondary, "次要按钮", G("BtnSecondary"));
            _previewLog.BackColor = G("LogBg");
            _previewLog.ForeColor = G("LogText");
            RefreshPreviewLog();
        }

        private void RefreshPreviewLog()
        {
            if (_previewLog == null) return;
            _previewLog.Clear();
            AppendLogLine("正常信息", G("LogInfo"), G("LogText"));
            AppendLogLine("成功信息", G("LogOk"), G("LogText"));
            AppendLogLine("警告信息", G("LogWarn"), G("LogText"));
            AppendLogLine("错误信息", G("LogErr"), G("LogText"));
        }

        private void AppendLogLine(string text, Color color, Color fallback)
        {
            try
            {
                _previewLog.SelectionStart = _previewLog.TextLength;
                _previewLog.SelectionColor = color == Color.Empty ? fallback : color;
                _previewLog.AppendText(text + Environment.NewLine);
            }
            catch { }
        }

        private static void SetBtn(FlatColorButton b, string text, Color c)
        {
            b.Text = text + " " + Hex(c);
            b.BgColor = c;
            b.BorderColor = c;
            b.ForeColor = c.GetBrightness() > 0.62f ? Color.Black : Color.White;
        }

        private static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private Color G(string key)
        {
            if (_palette.TryGetValue(key, out string v) && Theme.TryParseRgb(v, out Color c))
                return c;
            return SystemColors.Control;
        }

        // ── 日志 ─────────────────────────────────────────────────────────
        private void Log(string msg) { Log(msg, LogLevel.Info); }

        private void Log(string msg, LogLevel level)
        {
            if (_rtbStatus == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, LogLevel>(Log), msg, level);
                return;
            }
            Color c;
            switch (level)
            {
                case LogLevel.Ok: c = Theme.LogOk; break;
                case LogLevel.Error: c = Theme.LogErr; break;
                case LogLevel.Warn: c = Theme.LogWarn; break;
                default: c = Theme.LogText; break;
            }
            _rtbStatus.SelectionStart = _rtbStatus.TextLength;
            _rtbStatus.SelectionLength = 0;
            _rtbStatus.SelectionColor = c;
            _rtbStatus.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
            try { _rtbStatus.ScrollToCaret(); } catch { }
        }
    }
}
