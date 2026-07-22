// TxTools.Agent / UI / CodeApprovalDialog.cs
// 代码类操作(run_csharp)的审批框：用可滚动的只读文本框展示完整代码，
// 窗口尺寸固定且 clamp 到屏幕工作区，长代码不再顶穿屏幕。MessageBox 会按内容撑大且不可滚动，故不适用。

using System;
using System.Drawing;
using System.Windows.Forms;

// 工程引了 WPF，下列控件名会歧义(CS0104)；本窗体是 WinForms，显式别名。
using TextBox = System.Windows.Forms.TextBox;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;

namespace TxTools.Agent.UI
{
    public sealed class CodeApprovalDialog : Form
    {
        public static DialogResult Show(IWin32Window owner, string toolName, string code)
        {
            using (var dlg = new CodeApprovalDialog(toolName, code))
                return dlg.ShowDialog(owner);
        }

        private CodeApprovalDialog(string toolName, string code)
        {
            if (code == null) code = "";
            if (code.Length > 50000) code = code.Substring(0, 50000) + Environment.NewLine + "…(已截断)";

            Text = "操作确认 — 审阅代码";
            Name = GetType().FullName;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = true;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;

            // 尺寸 clamp 到工作区，绝不超屏。
            var wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(860, wa.Width - 80), Math.Min(640, wa.Height - 120));
            MinimumSize = new Size(480, 320);
            MaximumSize = new Size(wa.Width, wa.Height);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(10)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6),
                ForeColor = Color.FromArgb(150, 60, 0),
                Text = "助手请求执行一个会改动场景的操作。\n工具: " + toolName + " — 请审阅下方代码后决定是否允许。\n注意：代码在 PS 主线程同步执行，期间 PS 会短暂无响应；执行后可 Ctrl+Z 撤销。"
            };
            root.Controls.Add(header, 0, 0);

            var box = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,                       // 长行用横向滚动，不折行
                BackColor = Color.FromArgb(248, 248, 248),
                Font = MonoFont(),
                Text = code
            };
            root.Controls.Add(box, 0, 1);

            var btns = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true
            };
            var deny = new Button { Text = "拒绝", DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new Size(90, 30), Margin = new Padding(6) };
            var allow = new Button { Text = "允许执行", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(110, 30), Margin = new Padding(6) };
            btns.Controls.Add(deny);
            btns.Controls.Add(allow);
            root.Controls.Add(btns, 0, 2);

            Controls.Add(root);

            CancelButton = deny;   // Esc = 拒绝（安全默认）；不设 AcceptButton，避免回车误批
            box.Select(0, 0);
        }

        private static Font MonoFont()
        {
            try { return new Font("Consolas", 9.5f); }
            catch { return new Font(FontFamily.GenericMonospace, 9.5f); }
        }
    }
}
