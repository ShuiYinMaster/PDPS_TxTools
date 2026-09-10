// StageProbe.cs  —  C# 7.3
// 灰边来源二分定位实验：从 hellowouldui(阶段0) 逐级加特征，直到灰边复现。
// 用法：PS 命令 StageProbeTest 打开入口面板，依次点开 阶段0~5 对比。
// 每个阶段只比上一阶段多一个特征，灰边在哪一级出现，元凶就是哪一级引入的。
//
// 特征递增表：
//   0 基线               = 等同 hellowouldui（AutoScaleMode.Dpi，无 InitStandardForm）
//   1 + InitStandardForm = AutoScaleMode.None + 白底 + Padding清0 + Font 统一
//   2 + ApplyDpiScaling  = OnLoad 手动 DPI Scale
//   3 + scroll Panel     = 外层 Panel{ Dock=Fill, AutoScroll, Padding=10, 白底 }
//   4 + stack TableLayout= TableLayoutPanel{ Dock=Top, AutoSize } + 普通 GroupBox 卡片
//   5 + ColoredGroupBox  = FormUiKit.MkCard 风格自绘卡片（最接近 AllocatorForm）

using System;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

using Label = System.Windows.Forms.Label;
using Button = System.Windows.Forms.Button;
using Panel = System.Windows.Forms.Panel;

namespace TxTools.HelloMulti
{
    using Theme = TxTools.Common.FormUiKit.Theme;

    // ====================================================================
    // 入口命令
    // ====================================================================
    public class StageProbeCmd : TxButtonCommand
    {
        public override string Name => "StageProbeTest";
        public override string Category => "TxTools";

        public override void Execute(object cmdParams)
        {
            var f = new StageProbePanel();
            f.Show();
        }
    }

    public class StageProbePanel : TxForm
    {
        public StageProbePanel()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.StageProbePanel";
            Text = "灰边二分实验 —— 阶段入口";
            ClientSize = new Size(240, 320);
            Font = SystemFonts.MessageBoxFont;

            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8) };
            string[] names =
            {
                "0 基线(=hellowouldui)",
                "1 + InitStandardForm(全)",
                "1A 全-跳过FlatStyle",
                "1B 全-AutoScale保持Dpi",
                "1C 全-用ClientSize",
                "1D 全+覆盖OnPaintBackground",
                "2 + ApplyDpiScaling",
                "3 + scroll Panel",
                "4 + stack + GroupBox",
                "5 + ColoredGroupBox"
            };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var b = new Button { Text = "打开 阶段" + idx + "：" + names[i], Width = 200, Height = 36, Margin = new Padding(0, 4, 0, 4) };
                b.Click += (s, e) => OpenStage(idx);
                flow.Controls.Add(b);
            }
            Controls.Add(flow);
        }

        private void OpenStage(int idx)
        {
            TxForm f = null;
            switch (idx)
            {
                case 0: f = new Stage0Form(); break;
                case 1: f = new Stage1Form(); break;
                case 2: f = new Stage1AForm(); break;
                case 3: f = new Stage1BForm(); break;
                case 4: f = new Stage1CForm(); break;
                case 5: f = new Stage1DForm(); break;
                case 6: f = new Stage2Form(); break;
                case 7: f = new Stage3Form(); break;
                case 8: f = new Stage4Form(); break;
                case 9: f = new Stage5Form(); break;
            }
            if (f != null) f.Show();
        }
    }

    // ====================================================================
    // 阶段 0：基线，等同 hellowouldui
    // ====================================================================
    internal class Stage0Form : TxForm
    {
        public Stage0Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage0Form";
            Text = "阶段0 基线(=hellowouldui)";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(560, 460);

            var lbl = new Label
            {
                Text = "阶段0：等同 hellowouldui —— 预期无灰边",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold)
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }
    }

    // ====================================================================
    // 阶段 1：+ FormUiKit.InitStandardForm
    // ====================================================================
    internal class Stage1Form : TxForm
    {
        public Stage1Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage1Form";
            FormUiKit.InitStandardForm(this, "阶段1 +InitStandardForm",
                new Size(560, 460), new Size(420, 340), sizable: true);

            var lbl = new Label
            {
                Text = "阶段1：InitStandardForm(AutoScale None / 白底 / Padding清0)",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }
    }

    // ====================================================================
    // 阶段 1A：InitStandardForm 全设置，但跳过 TrySetFlatStyleDisabled
    // ====================================================================
    internal class Stage1AForm : TxForm
    {
        public Stage1AForm()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage1AForm";
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            Text = "阶段1A 跳过FlatStyle";
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = true;
            MinimumSize = new Size(420, 340);
            Size = new Size(560, 460);
            // ★ 唯一差异：不调 TrySetFlatStyleDisabled
            try { Padding = Padding.Empty; } catch { }
            ResumeLayout(false);

            var lbl = new Label
            {
                Text = "1A：全设置但跳过 FlatStyle 关闭 —— 若无灰边，元凶是 TrySetFlatStyleDisabled",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }
    }

    // ====================================================================
    // 阶段 1B：InitStandardForm 全设置，但 AutoScaleMode 保持 Dpi（不设 None）
    // ====================================================================
    internal class Stage1BForm : TxForm
    {
        public Stage1BForm()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage1BForm";
            SuspendLayout();
            // ★ 唯一差异：AutoScaleMode 保持 Dpi（同 hellowouldui），不设 None
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = SystemFonts.MessageBoxFont;
            Text = "阶段1B 保持Dpi";
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = true;
            MinimumSize = new Size(420, 340);
            Size = new Size(560, 460);
            FormUiKit.TrySetFlatStyleDisabled(this);
            try { Padding = Padding.Empty; } catch { }
            ResumeLayout(false);

            var lbl = new Label
            {
                Text = "1B：全设置但 AutoScale 保持 Dpi —— 若无灰边，元凶是 AutoScaleMode.None",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }
    }

    // ====================================================================
    // 阶段 1C：InitStandardForm 全设置，但用 ClientSize 替代 Size
    // ====================================================================
    internal class Stage1CForm : TxForm
    {
        public Stage1CForm()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage1CForm";
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            Text = "阶段1C 用ClientSize";
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true; MinimizeBox = true;
            MinimumSize = new Size(420, 340);
            // ★ 唯一差异：用 ClientSize 而非 Size
            ClientSize = new Size(560, 460);
            FormUiKit.TrySetFlatStyleDisabled(this);
            try { Padding = Padding.Empty; } catch { }
            ResumeLayout(false);

            var lbl = new Label
            {
                Text = "1C：全设置但 ClientSize 代替 Size —— 若无灰边，元凶是 Size/客户区不匹配",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }
    }

    // ====================================================================
    // 阶段 1D：完整 InitStandardForm + 覆盖 OnPaintBackground 画白底
    // 若灰边消失 → 灰边是基类 OnPaintBackground 绘制的，修复 = 子类覆盖背景绘制
    // ====================================================================
    internal class Stage1DForm : TxForm
    {
        public Stage1DForm()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage1DForm";
            FormUiKit.InitStandardForm(this, "阶段1D +覆盖背景",
                new Size(560, 460), new Size(420, 340), sizable: true);

            var lbl = new Label
            {
                Text = "1D：覆盖 OnPaintBackground 画白底 —— 若无灰边，元凶=基类背景绘制",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // ★ 不调 base：直接白底铺满。若基类的灰边在 OnPaintBackground 里，此覆盖应消掉它
            e.Graphics.Clear(FormUiKit.WinBg);
        }
    }

    // ====================================================================
    // 阶段 2：+ ApplyDpiScaling（OnLoad 手动 DPI 缩放）
    // ====================================================================
    internal class Stage2Form : TxForm
    {
        private static readonly Size Design = new Size(560, 460);
        private bool _scaled;

        public Stage2Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage2Form";
            FormUiKit.InitStandardForm(this, "阶段2 +ApplyDpiScaling",
                Design, new Size(420, 340), sizable: true);

            var lbl = new Label
            {
                Text = "阶段2：OnLoad 手动 Scale —— 若灰边在此出现，元凶是 DPI 缩放取整",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            Controls.Add(lbl);
            Controls.Add(btn);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
        }
    }

    // ====================================================================
    // 阶段 3：+ 外层 scroll Panel（Dock=Fill / AutoScroll / Padding=10 / 白底）
    // ====================================================================
    internal class Stage3Form : TxForm
    {
        private static readonly Size Design = new Size(560, 460);
        private bool _scaled;

        public Stage3Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage3Form";
            FormUiKit.InitStandardForm(this, "阶段3 +scrollPanel",
                Design, new Size(420, 340), sizable: true);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), BackColor = FormUiKit.WinBg };
            var lbl = new Label
            {
                Text = "阶段3：外层 scroll Panel(Padding=10) —— 若四周 10px 灰边出现，元凶是 scroll",
                Dock = DockStyle.Top, Height = 64,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            var btn = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 36 };
            btn.Click += (s, e) => Close();
            scroll.Controls.Add(lbl);
            scroll.Controls.Add(btn);
            Controls.Add(scroll);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
        }
    }

    // ====================================================================
    // 阶段 4：+ stack TableLayoutPanel（Dock=Top / AutoSize）+ 普通 GroupBox 卡片
    // ====================================================================
    internal class Stage4Form : TxForm
    {
        private static readonly Size Design = new Size(560, 460);
        private bool _scaled;

        public Stage4Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage4Form";
            FormUiKit.InitStandardForm(this, "阶段4 +stack+GroupBox",
                Design, new Size(420, 340), sizable: true);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), BackColor = FormUiKit.WinBg };
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 3, BackColor = FormUiKit.WinBg
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            stack.Controls.Add(MakePlainCard("卡片1", "普通 GroupBox"), 0, 0);
            stack.Controls.Add(MakePlainCard("卡片2", "普通 GroupBox"), 0, 1);
            stack.Controls.Add(MakePlainCard("卡片3", "普通 GroupBox"), 0, 2);

            scroll.Controls.Add(stack);
            Controls.Add(scroll);
        }

        private static GroupBox MakePlainCard(string title, string body)
        {
            var inner = new Label { Text = body, AutoSize = true, Padding = new Padding(6), BackColor = Color.Transparent };
            var g = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
            g.Controls.Add(inner);
            return g;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
        }
    }

    // ====================================================================
    // 阶段 5：+ ColoredGroupBox 自绘卡片（MkCard 风格，最接近 AllocatorForm）
    // ====================================================================
    internal class Stage5Form : TxForm
    {
        private static readonly Size Design = new Size(560, 460);
        private bool _scaled;

        public Stage5Form()
        {
            SemiModal = false;
            Name = "TxTools.HelloMulti.Stage5Form";
            FormUiKit.InitStandardForm(this, "阶段5 +ColoredGroupBox",
                Design, new Size(420, 340), sizable: true);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10), BackColor = FormUiKit.WinBg };
            var stack = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1, RowCount = 3, BackColor = FormUiKit.WinBg
            };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 3; i++) stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            stack.Controls.Add(MakeKitCard("卡片1", "ColoredGroupBox 自绘"), 0, 0);
            stack.Controls.Add(MakeKitCard("卡片2", "ColoredGroupBox 自绘"), 0, 1);
            stack.Controls.Add(MakeKitCard("卡片3", "ColoredGroupBox 自绘"), 0, 2);

            scroll.Controls.Add(stack);
            Controls.Add(scroll);
        }

        private static GroupBox MakeKitCard(string title, string body)
        {
            var inner = new Label { Text = body, AutoSize = true, Padding = new Padding(6), BackColor = Color.Transparent };
            var g = new FormUiKit.ColoredGroupBox
            {
                Text = title,
                TitleColor = Theme.CardTitle,
                BorderColor = FormUiKit.CardBorder,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 6, 10, 8),
                Margin = new Padding(0, 0, 0, 6),
                Font = FormUiKit.BoldFont,
                BackColor = FormUiKit.CardBack
            };
            g.Controls.Add(inner);
            return g;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _scaled, Design);
        }
    }
}
