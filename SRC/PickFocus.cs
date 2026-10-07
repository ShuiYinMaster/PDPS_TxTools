using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.Common
{
    /// <summary>统一处理原生拾取控件及其子控件的 Esc 退出与重新拾取。</summary>
    public static class PickFocus
    {
        private static readonly ConditionalWeakTable<Control, PickBinding> Bindings =
            new ConditionalWeakTable<Control, PickBinding>();
        private static readonly ConditionalWeakTable<Form, FocusSink> FocusSinks =
            new ConditionalWeakTable<Form, FocusSink>();
        [ThreadStatic]
        private static NativeEscapeHook _nativeEscapeHook;

        /// <summary>挂接拾取控件；Esc 保留已选对象，关闭拾取监听并移走焦点。</summary>
        public static void Wire(Control picker, Control focusTarget = null)
        {
            if (picker == null || picker.IsDisposed) return;
            PickBinding existing;
            if (Bindings.TryGetValue(picker, out existing)) return;

            Action<bool> setListening;
            Action loseFocus;
            if (picker is TxObjGridCtrl grid)
            {
                setListening = value => grid.ListenToPick = value;
                loseFocus = grid.LoseFocus;
            }
            else if (picker is TxObjEditBoxCtrl objectBox)
            {
                setListening = value => objectBox.ListenToPick = value;
                loseFocus = objectBox.LoseFocus;
            }
            else if (picker is TxFrameEditBoxCtrl frameBox)
            {
                setListening = value => frameBox.ListenToPick = value;
                loseFocus = frameBox.LoseFocus;
            }
            else if (picker is TxFrameComboBoxCtrl frameCombo)
            {
                setListening = value => frameCombo.ListenToPick = value;
                loseFocus = frameCombo.LoseFocus;
            }
            else
                throw new ArgumentException("不支持的拾取控件类型。", nameof(picker));

            var binding = new PickBinding(picker, focusTarget, setListening, loseFocus);
            Bindings.Add(picker, binding);
        }

        /// <summary>由 TxForm 的宿主键盘入口调用；ActiveControl 可保留图形视口拾取时的逻辑焦点。</summary>
        internal static bool TryCancel(Form host, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) != Keys.Escape || host == null || host.IsDisposed)
                return false;

            Control active = host.ActiveControl;
            while (active is ContainerControl container && container.ActiveControl != null)
                active = container.ActiveControl;
            for (var current = active; current != null && current != host; current = current.Parent)
            {
                PickBinding binding;
                if (!current.IsDisposed && current.Visible && current.Enabled &&
                    Bindings.TryGetValue(current, out binding))
                {
                    binding.Cancel();
                    return true;
                }
            }
            return false;
        }

        // Form.Focus() 可能把焦点交回原 ActiveControl，用不参与 Tab 顺序的落点退出拾取。
        private sealed class FocusSink : Control
        {
            public FocusSink()
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = false;
                Bounds = new Rectangle(-100, -100, 1, 1);
            }
        }

        private sealed class PickBinding : IMessageFilter
        {
            private const int WmKeyDown = 0x0100;
            private const int WmKeyUp = 0x0101;
            private const int WmSysKeyDown = 0x0104;
            private const int WmSysKeyUp = 0x0105;
            private readonly Control _picker;
            private readonly Control _focusTarget;
            private readonly Action<bool> _setListening;
            private readonly Action _loseFocus;
            private readonly NativeEscapeHook _nativeEscape;
            private bool _suppressEscape;

            public PickBinding(Control picker, Control focusTarget, Action<bool> setListening, Action loseFocus)
            {
                _picker = picker;
                _focusTarget = focusTarget;
                _setListening = setListening;
                _loseFocus = loseFocus;
                _nativeEscape = _nativeEscapeHook ?? (_nativeEscapeHook = new NativeEscapeHook());
                _nativeEscape.Register();
                picker.Enter += OnEnter;
                picker.Leave += OnLeave;
                picker.Disposed += OnDisposed;
                Application.AddMessageFilter(this);
            }

            private void OnEnter(object sender, EventArgs e)
            {
                _suppressEscape = false;
                _nativeEscape.Activate(this);
                // 重新注册 PS 拾取提供者，Esc 退出后再次进入即可继续选取。
                try { _setListening(false); _setListening(true); } catch { }
            }

            private void OnLeave(object sender, EventArgs e)
            {
                var host = _picker.FindForm();
                if (host == null || !host.IsHandleCreated) return;
                // 等焦点转移结束：进入普通输入框时退出会话，移到 PS 视口时保留会话。
                try
                {
                    host.BeginInvoke((MethodInvoker)(() =>
                    {
                        if (!host.IsDisposed && host.ContainsFocus && !_picker.ContainsFocus)
                            _nativeEscape.Clear(this);
                    }));
                }
                catch { }
            }

            public bool PreFilterMessage(ref Message m)
            {
                bool keyDown = m.Msg == WmKeyDown || m.Msg == WmSysKeyDown;
                bool keyUp = m.Msg == WmKeyUp || m.Msg == WmSysKeyUp;
                if (!keyDown && !keyUp) return false;
                if (m.WParam.ToInt32() != (int)Keys.Escape)
                {
                    if (keyDown) _suppressEscape = false;
                    return false;
                }

                // 消费本次 Esc 的重复消息与 KeyUp，避免退出后同一次按键关闭对话框。
                if (_suppressEscape)
                {
                    var host = _picker.FindForm();
                    if (keyUp) _suppressEscape = false;
                    if (host != null && host.ContainsFocus) return true;
                    _suppressEscape = false;
                }
                if (!keyDown || _picker.IsDisposed || !_picker.ContainsFocus) return false;

                _suppressEscape = true;
                Cancel();
                return true;
            }

            public void Cancel()
            {
                var host = _picker.FindForm();
                bool hadWindowFocus = host != null && host.ContainsFocus;
                _nativeEscape.Clear(this);
                // SDK LoseFocus 同时解除 PS 拾取提供者的逻辑焦点，不能只改变 Windows 焦点。
                try { _loseFocus(); } catch { }
                try { _setListening(false); } catch { }
                if (host == null || host.IsDisposed) return;

                if (_focusTarget != null && !_focusTarget.IsDisposed &&
                    _focusTarget != host && _focusTarget.FindForm() == host &&
                    _focusTarget.CanFocus && !IsPickerTarget(_focusTarget))
                {
                    host.ActiveControl = _focusTarget;
                    if (hadWindowFocus) _focusTarget.Focus();
                    if (!_picker.ContainsFocus) return;
                }

                var sink = FocusSinks.GetValue(host, form =>
                {
                    var control = new FocusSink();
                    form.Controls.Add(control);
                    return control;
                });
                host.ActiveControl = sink;
                if (hadWindowFocus) sink.Focus();
            }

            public bool CanHandleNativeEscape(IntPtr window)
            {
                var host = _picker.FindForm();
                if (_picker.IsDisposed || !_picker.Visible || !_picker.Enabled ||
                    host == null || host.IsDisposed || !host.Visible || !host.Enabled ||
                    !host.IsHandleCreated || window == IntPtr.Zero)
                    return false;

                var target = Control.FromHandle(window);
                if (target != null)
                {
                    if (target.FindForm() != host) return false;
                    if (target == host) return _picker.ContainsFocus;
                    for (var current = target; current != null; current = current.Parent)
                        if (current == _picker) return true;
                    return false;
                }

                // 仅接受本宿主主窗口下的原生视口/树控件；其它插件和模态对话框不接管。
                var root = NativeEscapeHook.GetAncestor(window, 2);
                var rootOwner = NativeEscapeHook.GetAncestor(window, 3);
                var hostRootOwner = NativeEscapeHook.GetAncestor(host.Handle, 3);
                if (root == IntPtr.Zero || rootOwner == IntPtr.Zero) return false;
                if (root != rootOwner && root != host.Handle) return false;
                if (rootOwner == hostRootOwner) return true;
                // 未设置 Win32 owner 的 TxForm，接受同 UI 线程无 owner 的原生主窗体。
                return hostRootOwner == host.Handle && root == rootOwner &&
                    Control.FromHandle(root) == null;
            }

            private static bool IsPickerTarget(Control target)
            {
                for (var current = target; current != null; current = current.Parent)
                {
                    if (current is TxObjGridCtrl || current is TxObjEditBoxCtrl ||
                        current is TxFrameEditBoxCtrl || current is TxFrameComboBoxCtrl)
                        return true;
                }
                return false;
            }

            private void OnDisposed(object sender, EventArgs e)
            {
                Application.RemoveMessageFilter(this);
                _picker.Enter -= OnEnter;
                _picker.Leave -= OnLeave;
                _picker.Disposed -= OnDisposed;
                _nativeEscape.Unregister(this);
                Bindings.Remove(_picker);
            }
        }

        // 点击插件外空白区域后，TUNE 不再向 TxForm 派发 Esc。
        // 在当前 PS UI 线程取消息时处理，无需 WinForms 消息循环，也不安装全局键盘钩子。
        private sealed class NativeEscapeHook
        {
            private readonly HookCallback _callback;
            private IntPtr _hook;
            private int _registrations;
            private PickBinding _activePick;
            private bool _swallowEscape;
            private IntPtr _escapeRoot;

            public NativeEscapeHook() { _callback = OnGetMessage; }
            public void Register() { _registrations++; }

            public void Activate(PickBinding binding)
            {
                _activePick = binding;
                _swallowEscape = false;
                if (_hook != IntPtr.Zero) return;
                _hook = SetWindowsHookEx(3, _callback, IntPtr.Zero, GetCurrentThreadId());
                if (_hook == IntPtr.Zero)
                    Trace.WriteLine("[PickFocus] 安装 UI 线程 Esc 消息钩子失败: " + Marshal.GetLastWin32Error());
            }

            public void Clear(PickBinding binding)
            {
                if (ReferenceEquals(_activePick, binding)) _activePick = null;
            }

            public void Unregister(PickBinding binding)
            {
                Clear(binding);
                if (--_registrations != 0) return;
                if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                _swallowEscape = false;
            }

            private IntPtr OnGetMessage(int code, IntPtr remove, IntPtr messagePointer)
            {
                if (code == 0 && remove == (IntPtr)1 && messagePointer != IntPtr.Zero)
                {
                    try
                    {
                        uint message = unchecked((uint)Marshal.ReadInt32(messagePointer, IntPtr.Size));
                        if (message == 0x0100 || message == 0x0101 || message == 0x0104 || message == 0x0105)
                        {
                            var header = Marshal.PtrToStructure<MessageHeader>(messagePointer);
                            if (HandleKey(header))
                                Marshal.WriteInt32(messagePointer, IntPtr.Size, 0); // WM_NULL
                        }
                    }
                    catch (Exception ex) { Trace.WriteLine("[PickFocus] Esc 消息处理失败: " + ex.Message); }
                }
                return CallNextHookEx(_hook, code, remove, messagePointer);
            }

            private bool HandleKey(MessageHeader message)
            {
                bool keyUp = message.Message == 0x0101 || message.Message == 0x0105;
                if (message.WParam != (UIntPtr)(uint)Keys.Escape)
                {
                    if (!keyUp) _swallowEscape = false;
                    return false;
                }
                var root = GetAncestor(message.Window, 3);
                if (_swallowEscape)
                {
                    bool repeat = (message.LParam.ToInt64() & 0x40000000L) != 0;
                    if (root == _escapeRoot && (keyUp || repeat))
                    {
                        if (keyUp) _swallowEscape = false;
                        return true;
                    }
                    _swallowEscape = false;
                }
                if (keyUp || _activePick == null || !_activePick.CanHandleNativeEscape(message.Window))
                    return false;

                var binding = _activePick;
                _swallowEscape = true;
                _escapeRoot = root;
                binding.Cancel();
                return true;
            }

            // 只读取 MSG 的头部，修改时仅写 message 字段，保留时间、坐标及系统私有数据。
            [StructLayout(LayoutKind.Sequential)]
            private struct MessageHeader
            {
                public IntPtr Window;
                public uint Message;
                public UIntPtr WParam;
                public IntPtr LParam;
            }
            private delegate IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int type, HookCallback callback, IntPtr module, uint thread);
            [DllImport("user32.dll")]
            private static extern bool UnhookWindowsHookEx(IntPtr hook);
            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();
            [DllImport("user32.dll")]
            public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        }
    }
}
