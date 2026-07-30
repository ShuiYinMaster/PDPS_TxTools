// TxTools.Agent / UI / AskUserDialog.cs
// ask_user 的内置对话框。
//
// 【统一渲染】所有形态(confirm/choice/multi_choice/input)内部都走同一套字段渲染管线,
// 单一问题只是"只有一个字段的表单"。这样多选不会再长得跟单选是两个控件家族 ——
// 之前用 CheckedListBox(下沉白色列表框)配 RadioButton(平铺单选),视觉上明显割裂。
// 现在多选统一用 CheckBox 平铺,与单选一致。
//
// 【混合表单】kind=form 时可在一个弹窗里同时放单选/多选/输入框/是否,一次问完,
// 避免连弹三四次打断用户。
//
// 【线程】在自建 STA 线程上 ShowDialog,拥有独立消息循环。
// 因为 ask_user 被标记为 ITxOffUiThreadTool 跑在后台线程:既不能直接操作 PS 主窗口控件,
// 也不能把等待放回主线程(会与 UI 消息循环互锁,整个 PS 冻死)。
// TopMost 以免被 PS 主窗口盖住(它不是 PS 窗口的子窗口)。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TxTools.Agent.Ui
{
    /// <summary>表单里的一个字段。单一问题 = 只有一个字段的表单。</summary>
    public sealed class AskField
    {
        /// <summary>结果字典里的键。单字段场景可留空。</summary>
        public string Name { get; set; }

        /// <summary>字段标题。留空时用 Name。</summary>
        public string Label { get; set; }

        /// <summary>confirm | choice | multi_choice | input</summary>
        public string Type { get; set; }

        public IList<string> Options { get; set; }

        /// <summary>input 的预填值 / choice 的默认选中项 / confirm 的默认值(yes|no)。</summary>
        public string Default { get; set; }

        /// <summary>choice 时额外提供"其他(自行填写)"。</summary>
        public bool AllowCustom { get; set; }

        /// <summary>input 时使用多行输入框。</summary>
        public bool Multiline { get; set; }
    }

    public static class AskUserDialog
    {
        /// <summary>用户长时间不理会时的默认超时,避免整个 agent 无限期挂起。</summary>
        public const int DefaultTimeoutMs = 10 * 60 * 1000;

        /// <summary>超时返回的标记值。</summary>
        public const string TimedOut = "(timeout)";

        // 统一的版式常量,所有形态共用,保证视觉一致
        private const int ClientWidth = 520;
        private const int ContentWidth = 470;
        private const int ComboThreshold = 7;   // 选项超过这个数就用下拉框,避免弹窗过高

        // ── 单一问题入口 ──

        /// <summary>
        /// 弹出单一问题。返回用户输入;取消返回 null;超时返回 TimedOut。
        /// 可在任意线程调用 —— 内部自建 STA 线程。
        /// </summary>
        public static string Show(
            string question,
            string kind,
            IList<string> options,
            string defaultValue,
            bool allowCustom,
            bool multiline,
            int timeoutMs = DefaultTimeoutMs)
        {
            kind = (kind ?? "confirm").ToLowerInvariant();

            var field = new AskField
            {
                Name = "value",
                Label = null,               // 单字段时标题就是问题本身,不重复显示
                Type = kind,
                Options = options,
                Default = defaultValue,
                AllowCustom = allowCustom,
                Multiline = multiline
            };

            var result = ShowForm(question, new List<AskField> { field }, timeoutMs, kind == "confirm");

            if (result == null) return null;
            if (result.Count == 1 && result.ContainsKey(TimeoutKey)) return TimedOut;

            string v;
            return result.TryGetValue("value", out v) ? v : null;
        }

        // ── 混合表单入口 ──

        private const string TimeoutKey = "__timeout__";

        /// <summary>
        /// 弹出多字段表单。返回 字段名 -> 值 的字典;用户取消返回 null;
        /// 超时返回只含 TimeoutKey 的字典(调用方用 IsTimeout 判断)。
        /// </summary>
        public static Dictionary<string, string> ShowForm(
            string question,
            IList<AskField> fields,
            int timeoutMs = DefaultTimeoutMs)
        {
            return ShowForm(question, fields, timeoutMs, false);
        }

        public static bool IsTimeout(Dictionary<string, string> result)
        {
            return result != null && result.Count == 1 && result.ContainsKey(TimeoutKey);
        }

        private static Dictionary<string, string> ShowForm(
            string question, IList<AskField> fields, int timeoutMs, bool confirmButtons)
        {
            Dictionary<string, string> result = null;
            Exception failure = null;
            Form liveForm = null;

            var thread = new Thread(() =>
            {
                try
                {
                    result = ShowCore(question, fields, confirmButtons, f => { liveForm = f; });
                }
                catch (Exception ex) { failure = ex; }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Name = "TxAgent.AskUserDialog";
            thread.Start();

            if (!thread.Join(timeoutMs <= 0 ? Timeout.Infinite : timeoutMs))
            {
                try
                {
                    var f = liveForm;
                    if (f != null && f.IsHandleCreated)
                        f.BeginInvoke((MethodInvoker)(() => { try { f.Close(); } catch { } }));
                }
                catch { }

                thread.Join(3000);
                return new Dictionary<string, string> { { TimeoutKey, "1" } };
            }

            if (failure != null) throw failure;
            return result;
        }

        // ── 渲染 ──

        private static Dictionary<string, string> ShowCore(
            string question, IList<AskField> fields, bool confirmButtons, Action<Form> onCreated)
        {
            if (fields == null || fields.Count == 0)
                fields = new List<AskField> { new AskField { Name = "value", Type = "confirm" } };

            var readers = new List<Func<KeyValuePair<string, string>>>();

            using (var form = new Form())
            {
                form.Text = "TxAgent 提问";
                form.StartPosition = FormStartPosition.CenterScreen;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.ShowInTaskbar = false;
                form.TopMost = true;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.Font = SystemFonts.MessageBoxFont;
                form.Padding = new Padding(16);
                form.ClientSize = new Size(ClientWidth, 240);

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 3
                };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                form.Controls.Add(root);

                // 问题文本
                var head = new Label
                {
                    Text = question ?? "",
                    AutoSize = true,
                    MaximumSize = new Size(ContentWidth + 20, 0),
                    Margin = new Padding(0, 0, 0, 12)
                };
                root.Controls.Add(head, 0, 0);

                // 内容区:可滚动容器 + 纵向堆叠
                var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                var stack = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0)
                };
                scroll.Controls.Add(stack);
                root.Controls.Add(scroll, 0, 1);

                bool multiField = fields.Count > 1;

                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i];
                    var type = (f.Type ?? "input").ToLowerInvariant();

                    // 多字段时显示每个字段的标题;单字段时问题本身就是标题,不重复
                    if (multiField)
                    {
                        var caption = new Label
                        {
                            Text = string.IsNullOrWhiteSpace(f.Label) ? (f.Name ?? "") : f.Label,
                            AutoSize = true,
                            MaximumSize = new Size(ContentWidth, 0),
                            Font = new Font(form.Font, FontStyle.Bold),
                            Margin = new Padding(0, i == 0 ? 0 : 14, 0, 6)
                        };
                        stack.Controls.Add(caption);
                    }

                    // confirm 在混合表单里渲染成两个单选,不能用窗口按钮
                    if (type == "confirm" && !confirmButtons)
                        type = "confirm_inline";

                    switch (type)
                    {
                        case "choice":
                            readers.Add(BuildChoice(stack, form, f));
                            break;
                        case "multi_choice":
                            readers.Add(BuildMultiChoice(stack, form, f));
                            break;
                        case "input":
                            readers.Add(BuildInput(stack, f));
                            break;
                        case "confirm_inline":
                            readers.Add(BuildConfirmInline(stack, f));
                            break;
                        default:
                            // confirm 走窗口按钮,内容区不放控件
                            break;
                    }
                }

                // 按钮区
                var btnPanel = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    AutoSize = true,
                    Margin = new Padding(0, 12, 0, 0)
                };

                var btnCancel = new Button
                {
                    Text = "取消",
                    DialogResult = DialogResult.Cancel,
                    AutoSize = true,
                    MinimumSize = new Size(88, 32)
                };

                var btnOk = new Button
                {
                    Text = confirmButtons ? "是" : "确定",
                    DialogResult = DialogResult.OK,
                    AutoSize = true,
                    MinimumSize = new Size(88, 32),
                    Margin = new Padding(8, 0, 0, 0)
                };

                btnPanel.Controls.Add(btnCancel);
                if (confirmButtons)
                {
                    btnPanel.Controls.Add(new Button
                    {
                        Text = "否",
                        DialogResult = DialogResult.No,
                        AutoSize = true,
                        MinimumSize = new Size(88, 32),
                        Margin = new Padding(8, 0, 0, 0)
                    });
                }
                btnPanel.Controls.Add(btnOk);
                root.Controls.Add(btnPanel, 0, 2);

                form.AcceptButton = btnOk;
                form.CancelButton = btnCancel;

                // 按内容自适应高度,上限为屏幕的 70%
                form.Shown += (s, e) =>
                {
                    try
                    {
                        int needed = head.Height + stack.PreferredSize.Height + btnPanel.Height + 70;
                        var wa = Screen.PrimaryScreen.WorkingArea;
                        int maxH = (int)(wa.Height * 0.7);
                        form.ClientSize = new Size(ClientWidth, Math.Max(170, Math.Min(needed, maxH)));

                        // CenterToScreen() 是 protected，外部调不了 —— 手动居中
                        form.Location = new Point(
                            wa.Left + Math.Max(0, (wa.Width - form.Width) / 2),
                            wa.Top + Math.Max(0, (wa.Height - form.Height) / 2));

                        form.Activate();
                        SelectFirstInput(stack);
                    }
                    catch { }
                };

                form.HandleCreated += (s, e) => { if (onCreated != null) onCreated(form); };

                var dr = form.ShowDialog();

                if (dr == DialogResult.Cancel) return null;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);

                if (confirmButtons)
                {
                    map["value"] = dr == DialogResult.OK ? "yes" : "no";
                    return map;
                }

                foreach (var reader in readers)
                {
                    var kv = reader();
                    map[kv.Key] = kv.Value;
                }
                return map;
            }
        }

        // ── 各字段控件 ──

        private static Func<KeyValuePair<string, string>> BuildChoice(
            FlowLayoutPanel stack, Form form, AskField f)
        {
            var key = f.Name ?? "value";
            var opts = f.Options ?? new List<string>();

            // 选项多时改用下拉框,否则弹窗会被撑得很高
            if (opts.Count >= ComboThreshold && !f.AllowCustom)
            {
                var combo = new ComboBox
                {
                    Width = ContentWidth,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Margin = new Padding(0, 0, 0, 4)
                };
                foreach (var o in opts) combo.Items.Add(o);

                int idx = string.IsNullOrEmpty(f.Default) ? 0 : combo.Items.IndexOf(f.Default);
                combo.SelectedIndex = idx >= 0 ? idx : 0;
                stack.Controls.Add(combo);

                return () => new KeyValuePair<string, string>(
                    key, combo.SelectedItem == null ? "" : combo.SelectedItem.ToString());
            }

            var radios = new List<RadioButton>();
            TextBox customBox = null;
            RadioButton rbOther = null;

            foreach (var o in opts)
            {
                var rb = new RadioButton
                {
                    Text = o,
                    AutoSize = true,
                    MaximumSize = new Size(ContentWidth, 0),
                    Margin = new Padding(2, 3, 0, 3)
                };
                if (!string.IsNullOrEmpty(f.Default) && string.Equals(o, f.Default, StringComparison.Ordinal))
                    rb.Checked = true;
                radios.Add(rb);
                stack.Controls.Add(rb);
            }

            if (f.AllowCustom)
            {
                rbOther = new RadioButton
                {
                    Text = "其他(自行填写)",
                    AutoSize = true,
                    Margin = new Padding(2, 6, 0, 3)
                };
                radios.Add(rbOther);
                stack.Controls.Add(rbOther);

                customBox = new TextBox { Width = ContentWidth - 24, Margin = new Padding(24, 0, 0, 4) };
                var other = rbOther;
                customBox.GotFocus += (s, e) => { other.Checked = true; };
                stack.Controls.Add(customBox);
            }

            if (radios.Count > 0 && !radios.Exists(r => r.Checked))
                radios[0].Checked = true;

            return () =>
            {
                foreach (var rb in radios)
                {
                    if (!rb.Checked) continue;
                    if (rbOther != null && ReferenceEquals(rb, rbOther))
                        return new KeyValuePair<string, string>(key, (customBox.Text ?? "").Trim());
                    return new KeyValuePair<string, string>(key, rb.Text);
                }
                return new KeyValuePair<string, string>(key, "");
            };
        }

        private static Func<KeyValuePair<string, string>> BuildMultiChoice(
            FlowLayoutPanel stack, Form form, AskField f)
        {
            var key = f.Name ?? "value";
            var opts = f.Options ?? new List<string>();
            var boxes = new List<CheckBox>();

            // 用平铺 CheckBox 而不是 CheckedListBox —— 后者是下沉白色列表框,
            // 和单选用的 RadioButton 视觉上完全不是一套东西。
            foreach (var o in opts)
            {
                var cb = new CheckBox
                {
                    Text = o,
                    AutoSize = true,
                    MaximumSize = new Size(ContentWidth, 0),
                    Margin = new Padding(2, 3, 0, 3)
                };
                if (!string.IsNullOrEmpty(f.Default) && string.Equals(o, f.Default, StringComparison.Ordinal))
                    cb.Checked = true;
                boxes.Add(cb);
                stack.Controls.Add(cb);
            }

            if (boxes.Count > 5)
            {
                var bar = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    AutoSize = true,
                    WrapContents = false,
                    Margin = new Padding(0, 4, 0, 0)
                };

                var all = new LinkLabel { Text = "全选", AutoSize = true, Margin = new Padding(2, 3, 12, 0) };
                var none = new LinkLabel { Text = "清空", AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
                all.LinkClicked += (s, e) => { foreach (var b in boxes) b.Checked = true; };
                none.LinkClicked += (s, e) => { foreach (var b in boxes) b.Checked = false; };

                bar.Controls.Add(all);
                bar.Controls.Add(none);
                stack.Controls.Add(bar);
            }

            return () =>
            {
                var picked = new List<string>();
                foreach (var b in boxes) if (b.Checked) picked.Add(b.Text);
                return new KeyValuePair<string, string>(
                    key, picked.Count == 0 ? "(未选择任何项)" : string.Join(", ", picked));
            };
        }

        private static Func<KeyValuePair<string, string>> BuildInput(FlowLayoutPanel stack, AskField f)
        {
            var key = f.Name ?? "value";

            var tb = new TextBox
            {
                Width = ContentWidth,
                Multiline = f.Multiline,
                Height = f.Multiline ? 110 : 0,
                ScrollBars = f.Multiline ? ScrollBars.Vertical : ScrollBars.None,
                Text = f.Default ?? "",
                Margin = new Padding(2, 0, 0, 4)
            };
            if (f.Multiline) tb.AcceptsReturn = true;
            tb.SelectionStart = tb.Text.Length;
            stack.Controls.Add(tb);

            return () => new KeyValuePair<string, string>(key, (tb.Text ?? "").Trim());
        }

        private static Func<KeyValuePair<string, string>> BuildConfirmInline(FlowLayoutPanel stack, AskField f)
        {
            var key = f.Name ?? "value";

            var yes = new RadioButton { Text = "是", AutoSize = true, Margin = new Padding(2, 3, 0, 3) };
            var no = new RadioButton { Text = "否", AutoSize = true, Margin = new Padding(2, 3, 0, 3) };

            if (string.Equals(f.Default, "no", StringComparison.OrdinalIgnoreCase)) no.Checked = true;
            else yes.Checked = true;

            stack.Controls.Add(yes);
            stack.Controls.Add(no);

            return () => new KeyValuePair<string, string>(key, yes.Checked ? "yes" : "no");
        }

        private static void SelectFirstInput(Control container)
        {
            foreach (Control c in container.Controls)
            {
                if (c is TextBox) { c.Focus(); return; }
                if (c.HasChildren) SelectFirstInput(c);
            }
        }
    }
}
