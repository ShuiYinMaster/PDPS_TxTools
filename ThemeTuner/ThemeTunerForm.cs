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
        private static readonly Size _designSize = new Size(680, 740);
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
                _designSize, new Size(600, 620), sizable: true);
            this.Padding = new Padding(4);

            foreach (var kv in Theme.SnapshotToStrings())
                _palette[kv.Key] = kv.Value;

            BuildBody();
            BuildBottomBar();

            RefreshSwatches();
            RefreshPreview();
            Log("已载入当前主题。选预设或点色块自定义后，点 [应用并保存] 生效。");
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
            var col = FormUiKit.BuildCardColumn(660);
            col.Controls.Add(BuildPresetCard());
            col.Controls.Add(BuildColorCard());
            col.Controls.Add(BuildPreviewCard());
            col.Controls.Add(BuildStatusCard());
            this.Controls.Add(col);
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
            var right = NewSlotColumn();
            for (int i = 0; i < Slots.Length; i++)
                (i % 2 == 0 ? left : right).Controls.Add(MakeSlotRow(Slots[i][0], Slots[i][1]));
            twoCol.Controls.Add(left);
            twoCol.Controls.Add(right);
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
            var lbl = FormUiKit.MkFieldLabel(label);
            lbl.Margin = new Padding(0, 8, 8, 0);
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
            _rtbStatus = new RichTextBox
            {
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
            var btnReset = FormUiKit.MkFuncButton("恢复默认", Theme.BtnMuted);
            btnReset.Click += (s, e) => ResetToDefault();
            var btnClose = FormUiKit.MkFuncButton("关闭", Theme.BtnDanger);
            btnClose.Click += (s, e) => Close();
            bar.Controls.Add(btnApply);
            bar.Controls.Add(btnReset);
            bar.Controls.Add(btnClose);
            this.Controls.Add(bar);
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

        // ── 刷色 ─────────────────────────────────────────────────────────
        private void RefreshSwatches()
        {
            foreach (var kv in _swatches)
            {
                if (!_palette.TryGetValue(kv.Key, out string v)
                    || !Theme.TryParseRgb(v, out Color c)) continue;
                var b = kv.Value;
                b.BgColor = c;
                b.BorderColor = c;
                b.ForeColor = c.GetBrightness() > 0.62f ? Color.Black : Color.White;
                b.Text = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
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
