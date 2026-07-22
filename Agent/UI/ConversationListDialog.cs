// TxTools.Agent / UI / ConversationListDialog.cs
// 历史对话列表：列出过往对话(标题+时间)，可打开或删除。返回所选对话 Id(null=取消)。

using System;
using System.Drawing;
using System.Windows.Forms;
using TxTools.Agent.Core;

using Button = System.Windows.Forms.Button;
using Label = System.Windows.Forms.Label;
using ListBox = System.Windows.Forms.ListBox;

namespace TxTools.Agent.UI
{
    public sealed class ConversationListDialog : Form
    {
        private readonly ListBox _list = new ListBox();
        public string SelectedId { get; private set; }

        public static string Pick(IWin32Window owner)
        {
            using (var dlg = new ConversationListDialog())
            {
                dlg.ShowDialog(owner);
                return dlg.SelectedId;
            }
        }

        private ConversationListDialog()
        {
            Text = "历史对话";
            Name = GetType().FullName;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new System.Drawing.Size(440, 420);
            MinimumSize = new System.Drawing.Size(360, 300);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(10) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new Label { Text = "选择要打开的对话：", AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);

            _list.Dock = DockStyle.Fill;
            _list.IntegralHeight = false;
            _list.DoubleClick += (s, e) => OpenSelected();
            root.Controls.Add(_list, 0, 1);

            var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new System.Drawing.Size(80, 30), Margin = new Padding(6) };
            var del = new Button { Text = "删除", AutoSize = true, MinimumSize = new System.Drawing.Size(80, 30), Margin = new Padding(6) };
            var open = new Button { Text = "打开", AutoSize = true, MinimumSize = new System.Drawing.Size(90, 30), Margin = new Padding(6) };
            del.Click += (s, e) => DeleteSelected();
            open.Click += (s, e) => OpenSelected();
            btns.Controls.Add(cancel);
            btns.Controls.Add(del);
            btns.Controls.Add(open);
            root.Controls.Add(btns, 0, 2);

            Controls.Add(root);
            CancelButton = cancel;

            Reload();
        }

        private void Reload()
        {
            _list.Items.Clear();
            foreach (var m in ConversationStore.List())
                _list.Items.Add(new Item(m));
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        }

        private void OpenSelected()
        {
            var item = _list.SelectedItem as Item;
            if (item == null) return;
            SelectedId = item.Meta.Id;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void DeleteSelected()
        {
            var item = _list.SelectedItem as Item;
            if (item == null) return;
            if (MessageBox.Show(this, "删除对话「" + item.Meta.Title + "」？此操作不可恢复。",
                    "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ConversationStore.Delete(item.Meta.Id);
            Reload();
        }

        private sealed class Item
        {
            public readonly ConversationMeta Meta;
            public Item(ConversationMeta m) { Meta = m; }
            public override string ToString()
            {
                var title = string.IsNullOrWhiteSpace(Meta.Title) ? "(无标题)" : Meta.Title;
                return title + "    —  " + Meta.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }
        }
    }
}