using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;
using TxTools.Common;

// PS 控件替身仅提供 ListenToPick 和内部编辑器；焦点、消息过滤和 Tab 使用真实 WinForms。
namespace Tecnomatix.Engineering.Ui
{
    // TUNE 会直接调用 TxForm.OnKeyDown；基类先预处理 Esc，之后才触发 KeyDown 事件。
    public class TxForm : Form
    {
        public int PreprocessedKeys;
        protected virtual bool ShouldPreprocessKey(Keys key) { return key == Keys.Escape; }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (ShouldPreprocessKey(e.KeyCode))
            {
                PreprocessedKeys++;
                ProcessDialogKey(e.KeyData);
                return;
            }
            base.OnKeyDown(e);
        }
    }

    public class PickControl : UserControl
    {
        public bool ListenToPick { get; set; }
        public int LoseFocusCalls;
        public readonly object SelectedObject = new object();
        public readonly TextBox Editor = new TextBox { Dock = DockStyle.Fill };
        public PickControl() { Controls.Add(Editor); }
        public void LoseFocus() { LoseFocusCalls++; }
    }
    public sealed class TxObjGridCtrl : PickControl { }
    public sealed class TxObjEditBoxCtrl : PickControl { }
    public sealed class TxFrameEditBoxCtrl : PickControl { }
    public sealed class TxFrameComboBoxCtrl : PickControl { }
}

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
            CheckPicker(new TxObjGridCtrl());
            CheckPicker(new TxObjEditBoxCtrl());
            CheckPicker(new TxFrameEditBoxCtrl());
            CheckPicker(new TxFrameComboBoxCtrl());
            CheckTargetsAndIsolation();
            CheckHostKeyRoutes();
            CheckNativeViewportEscape();
            Console.WriteLine("PASS: " + _checks + " checks");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static Form CreateForm()
    {
        return new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10000, -10000),
            ClientSize = new Size(280, 150)
        };
    }

    private static bool Filter(Control target, int message, Keys key)
    {
        var m = Message.Create(target.Handle, message, (IntPtr)(int)key, IntPtr.Zero);
        return Application.FilterMessage(ref m);
    }

    private static void Assert(bool success, string description)
    {
        if (!success) throw new Exception(description);
        _checks++;
    }

    private static void Focus(Control control)
    {
        Assert(control.Focus(), "Cannot focus " + control.GetType().Name);
        Application.DoEvents();
        Assert(control.ContainsFocus, "Focus must remain in target");
    }

    private static void CheckPicker(PickControl picker)
    {
        using (var form = CreateForm())
        {
            picker.Bounds = new Rectangle(10, 10, 220, 25);
            var normal = new TextBox { Bounds = new Rectangle(10, 45, 220, 25) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            bool cancelled = false;
            cancel.Click += (s, e) => cancelled = true;
            form.Controls.AddRange(new Control[] { picker, normal, cancel });
            form.CancelButton = cancel;
            PickFocus.Wire(picker);
            PickFocus.Wire(picker); // 重复挂接不能重复注册过滤器。
            form.Show();
            Focus(picker.Editor);
            Assert(picker.ListenToPick, "Entering must enable picking");
            var selected = picker.SelectedObject;
            Assert(!Filter(picker.Editor, 0x0100, Keys.A), "Ordinary key must pass through");
            Assert(Filter(picker.Editor, 0x0100, Keys.Escape), "Child Esc must be consumed");
            Assert(!picker.ListenToPick && !picker.ContainsFocus, "Esc must release picking and focus");
            Assert(ReferenceEquals(selected, picker.SelectedObject), "Esc must retain selection");
            Assert(!cancelled && form.Visible, "Esc must not cancel the dialog");
            Assert(Filter(form, 0x0100, Keys.Escape), "Held Esc repeat must be consumed");
            Assert(Filter(form, 0x0101, Keys.Escape), "Esc release must be consumed");
            Focus(normal);
            Assert(!Filter(normal, 0x0100, Keys.Escape), "Esc outside picker must pass through");
            Focus(picker.Editor);
            Assert(picker.ListenToPick, "Reentering must restore picking");
            Assert(Filter(picker.Editor, 0x0104, Keys.Escape), "System Esc must release picking");
            Assert(Filter(form, 0x0105, Keys.Escape), "System Esc release must be consumed");
            Console.WriteLine("PASS: " + picker.GetType().Name);
        }
    }

    private static void CheckTargetsAndIsolation()
    {
        using (var form = CreateForm())
        {
            var first = new TxObjGridCtrl { Bounds = new Rectangle(10, 10, 220, 25) };
            var second = new TxObjEditBoxCtrl { Bounds = new Rectangle(10, 45, 220, 25) };
            var normal = new TextBox { Bounds = new Rectangle(10, 80, 220, 25) };
            form.Controls.AddRange(new Control[] { first, second, normal });
            PickFocus.Wire(first, normal);
            PickFocus.Wire(second, first.Editor); // 不能把焦点转移到另一个拾取器。
            form.Show();
            Focus(first.Editor);
            Assert(Filter(first.Editor, 0x0100, Keys.Escape), "First picker Esc");
            Assert(normal.Focused, "Explicit neutral target must receive focus");
            Filter(normal, 0x0101, Keys.Escape);
            Focus(second.Editor);
            Assert(Filter(second.Editor, 0x0100, Keys.Escape), "Second picker Esc");
            Assert(!first.ContainsFocus && !second.ContainsFocus, "Picker target must use neutral fallback");
            Filter(form, 0x0101, Keys.Escape);
            first.Dispose();
            second.Dispose();
            Focus(normal);
            Assert(!Filter(normal, 0x0100, Keys.Escape), "Disposed pickers must remove their filters");
            Assert(form.SelectNextControl(normal, true, true, true, true), "Tab navigation must remain available");
            Assert(!form.ActiveControl.GetType().Name.Contains("FocusSink"), "Neutral fallback must not enter Tab order");
        }
    }

    private sealed class HostForm : PickAwareTxForm
    {
        public KeyEventArgs TuneKeyDown(Keys key)
        {
            var e = new KeyEventArgs(key);
            OnKeyDown(e);
            return e;
        }

        public bool DialogKey(Keys key) { return ProcessDialogKey(key); }
        public bool CommandKey(Keys key)
        {
            var message = Message.Create(Handle, 0x0100, (IntPtr)(int)key, IntPtr.Zero);
            return ProcessCmdKey(ref message, key);
        }
    }

    private static void CheckHostKeyRoutes()
    {
        foreach (var picker in new PickControl[]
        {
            new TxObjGridCtrl(), new TxObjEditBoxCtrl(),
            new TxFrameEditBoxCtrl(), new TxFrameComboBoxCtrl()
        })
        {
            using (var form = new HostForm
            {
                ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                Location = new Point(-10000, -10000), ClientSize = new Size(280, 150)
            })
            {
                picker.Bounds = new Rectangle(10, 10, 220, 25);
                var normal = new TextBox { Bounds = new Rectangle(10, 45, 220, 25) };
                form.Controls.AddRange(new Control[] { picker, normal });
                PickFocus.Wire(picker);
                form.Show();
                bool ordinaryKeyReceived = false;
                form.KeyDown += (s, e) => ordinaryKeyReceived = e.KeyCode == Keys.A;
                Focus(picker.Editor);
                form.TuneKeyDown(Keys.A);
                Assert(ordinaryKeyReceived && picker.ContainsFocus, "TUNE ordinary key must retain behavior");

                // 这里故意不调用 Application.FilterMessage，复现 PS 的宿主直接派发路径。
                var escapeEvent = form.TuneKeyDown(Keys.Escape);
                Assert(!picker.ListenToPick && !picker.ContainsFocus, "TUNE Esc must exit picking without WinForms message filter");
                Assert(escapeEvent.Handled && escapeEvent.SuppressKeyPress, "TUNE Esc must be consumed before TxForm preprocesses it");
                Assert(form.PreprocessedKeys == 0, "TUNE base preprocessing must not receive picker Esc");
                Assert(picker.LoseFocusCalls == 1, "TUNE Esc must release SDK pick focus");

                Focus(picker.Editor);
                Assert(picker.ListenToPick, "TUNE reentry must restore picking");
                Assert(form.DialogKey(Keys.Escape), "Dialog Esc must be consumed");
                Assert(!picker.ListenToPick && !picker.ContainsFocus, "Dialog Esc must exit picking");
                Assert(picker.LoseFocusCalls == 2, "Dialog Esc must release SDK pick focus");

                Focus(picker.Editor);
                Assert(form.CommandKey(Keys.Escape), "Command Esc must be consumed");
                Assert(!picker.ListenToPick && !picker.ContainsFocus, "Command Esc must exit picking");
                Assert(picker.LoseFocusCalls == 3, "Command Esc must release SDK pick focus");

                Focus(normal);
                Assert(!form.DialogKey(Keys.Escape), "Normal input Esc must retain default behavior");
                form.TuneKeyDown(Keys.Escape);
                Assert(form.PreprocessedKeys == 1, "TUNE must still preprocess Esc outside picker");

                // PS 图形视口获得 Windows 焦点时，TxForm 的 ActiveControl 仍指向拾取框。
                Focus(picker.Editor);
                using (var viewport = CreateForm())
                {
                    var view = new TextBox { Dock = DockStyle.Fill };
                    viewport.Controls.Add(view);
                    viewport.Show();
                    Focus(view);
                    Assert(!picker.ContainsFocus && form.ActiveControl == picker, "Simulated viewport must keep picker as active control");
                    form.TuneKeyDown(Keys.Escape);
                    Assert(!picker.ListenToPick, "TUNE Esc must release pick ownership while viewport has Windows focus");
                    Assert(picker.LoseFocusCalls == 4, "Viewport Esc must release SDK pick focus");
                }
                Console.WriteLine("PASS: host routes for " + picker.GetType().Name);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    private static NativeMessage RetrieveKey(NativeWindow viewport, uint message, uint flags = 1)
    {
        NativeMessage native;
        Assert(PeekMessage(out native, viewport.Handle, message, message, flags), "Native key must be retrieved");
        return native;
    }

    private sealed class NativeHost : NativeWindow, IWin32Window, IDisposable
    {
        public void Show()
        {
            CreateHandle(new CreateParams
            {
                Style = 0x10000000, X = -10000, Y = -10000,
                Width = 280, Height = 150, Caption = "Native PS host"
            });
        }
        public void Dispose() { DestroyHandle(); }
    }
    private static void CheckNativeViewportEscape()
    {
        foreach (var picker in new PickControl[]
        {
            new TxObjGridCtrl(), new TxObjEditBoxCtrl(),
            new TxFrameEditBoxCtrl(), new TxFrameComboBoxCtrl()
        })
        {
            using (var main = new NativeHost())
            using (var form = new HostForm { ShowInTaskbar = false })
            {
                main.Show();
                var viewport = new NativeWindow();
                viewport.CreateHandle(new CreateParams
                {
                    Parent = main.Handle, Style = 0x50000000,
                    X = 0, Y = 0, Width = 100, Height = 100, Caption = "Native viewport"
                });
                try
                {
                    picker.Bounds = new Rectangle(10, 10, 220, 25);
                    var normal = new TextBox { Bounds = new Rectangle(10, 45, 220, 25) };
                    form.Controls.AddRange(new Control[] { picker, normal });
                    PickFocus.Wire(picker);
                    form.Show(main);
                    Focus(picker.Editor);
                    SetFocus(viewport.Handle);
                    Application.DoEvents();
                    Assert(!picker.ContainsFocus && GetFocus() == viewport.Handle, "Blank viewport click must move native focus");
                    form.ActiveControl = null;
                    Assert(picker.ListenToPick && GetFocus() == viewport.Handle,
                        "External blank pick must remain cancellable after TxForm loses its active control");

                    // 仅从原生队列取消息；不调用插件 OnKeyDown 或 Application.FilterMessage。
                    Assert(PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero), "Post native Esc");
                    var peeked = RetrieveKey(viewport, 0x0100, 0);
                    Assert(peeked.Message == 0x0100 && picker.ListenToPick, "PM_NOREMOVE must not cancel picking");
                    var removed = RetrieveKey(viewport, 0x0100);
                    Assert(!picker.ListenToPick && picker.LoseFocusCalls == 1, "Viewport Esc must cancel SDK picking through native queue");
                    Assert(removed.Message == 0, "Handled native Esc must become WM_NULL");
                    Assert(GetFocus() == viewport.Handle, "Cancelling external pick must retain viewport keyboard focus");
                    Assert(form.ActiveControl != picker, "Cancelled logical picker must be released");

                    PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.Escape, (IntPtr)0x40000000);
                    Assert(RetrieveKey(viewport, 0x0100).Message == 0, "Held Esc repeat must not reach PS");
                    PostMessage(viewport.Handle, 0x0101, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0101).Message == 0, "Native Esc release must be consumed");
                    PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0100).Message == 0x0100, "Next Esc without active pick must reach PS normally");

                    Focus(picker.Editor);
                    Assert(picker.ListenToPick, "Native cancel must allow picking to resume");
                    SetFocus(viewport.Handle);
                    Application.DoEvents();
                    PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.A, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0100).Message == 0x0100 && picker.ListenToPick,
                        "Native ordinary key must retain picking and host behavior");

                    using (var dialog = CreateForm())
                    {
                        dialog.Show(main);
                        var dialogContent = new NativeWindow();
                        dialogContent.CreateHandle(new CreateParams
                        {
                            Parent = dialog.Handle, Style = 0x50000000,
                            X = 0, Y = 0, Width = 100, Height = 100
                        });
                        try
                        {
                            SetFocus(dialogContent.Handle);
                            Application.DoEvents();
                            PostMessage(dialogContent.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                            Assert(RetrieveKey(dialogContent, 0x0100).Message == 0x0100 && picker.ListenToPick,
                                "Native child of another dialog must retain its Esc behavior");
                        }
                        finally { dialogContent.DestroyHandle(); }
                    }
                    Focus(picker.Editor);
                    Focus(normal);
                    SetFocus(viewport.Handle);
                    Application.DoEvents();
                    PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0100).Message == 0x0100, "Normal field must end external pick ownership");
                    Assert(picker.LoseFocusCalls == 1, "Normal field must not receive another pick cancellation");

                    Focus(picker.Editor);
                    SetFocus(viewport.Handle);
                    Application.DoEvents();
                    PostMessage(viewport.Handle, 0x0104, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0104).Message == 0 && !picker.ListenToPick,
                        "Native system Esc must cancel external picking");
                    PostMessage(viewport.Handle, 0x0105, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0105).Message == 0, "Native system Esc release must be consumed");

                    Focus(picker.Editor);
                    picker.Dispose();
                    SetFocus(viewport.Handle);
                    PostMessage(viewport.Handle, 0x0100, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
                    Assert(RetrieveKey(viewport, 0x0100).Message == 0x0100, "Disposed picker must release native hook");
                    Console.WriteLine("PASS: native viewport for " + picker.GetType().Name);
                }
                finally { viewport.DestroyHandle(); }
            }
        }
    }
}
