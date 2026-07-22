// TxTools.Agent / UI / ApiKeyDialog.cs
// 输入 DeepSeek API Key 的模态弹窗。默认掩码，可勾选临时显示明文。
// 确定后由调用方 (TxAgentForm) 负责加密落盘。

using System;
using System.Drawing;
using System.Windows.Forms;

// 工程引了 WPF，下列控件名会歧义(CS0104)；本窗体是 WinForms，显式别名。
using TextBox = System.Windows.Forms.TextBox;
using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using CheckBox = System.Windows.Forms.CheckBox;

namespace TxTools.Agent.UI
{
    public sealed class ApiKeyDialog : Form
    {
        private TextBox _keyBox;
        private CheckBox _showBox;

        /// <summary>用户输入的 key (确定时有效)。</summary>
        public string ApiKey { get; private set; }

        public ApiKeyDialog()
        {
            BuildUi();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = SystemFonts.MessageBoxFont;
        }

        private void BuildUi()
        {
            Text = "设置 DeepSeek API Key";
            Name = GetType().FullName;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(420, 150);

            var tip = new Label
            {
                Text = "请输入 DeepSeek API Key (https://platform.deepseek.com)。\n将按当前 Windows 用户加密保存到插件文件夹。",
                AutoSize = false,
                Location = new Point(12, 12),
                Size = new Size(396, 40)
            };

            _keyBox = new TextBox
            {
                Location = new Point(12, 58),
                Size = new Size(396, 24),
                UseSystemPasswordChar = true
            };

            _showBox = new CheckBox { Text = "显示", AutoSize = true, Location = new Point(12, 88) };
            _showBox.CheckedChanged += (s, e) => _keyBox.UseSystemPasswordChar = !_showBox.Checked;

            var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(232, 110), Size = new Size(80, 28) };
            ok.Click += Ok_Click;
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(328, 110), Size = new Size(80, 28) };

            Controls.Add(tip);
            Controls.Add(_keyBox);
            Controls.Add(_showBox);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private void Ok_Click(object sender, EventArgs e)
        {
            var key = (_keyBox.Text ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                MessageBox.Show(this, "API Key 不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.None; // 阻止关闭
                return;
            }
            ApiKey = key;
        }
    }
}
