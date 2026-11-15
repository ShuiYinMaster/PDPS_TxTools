using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace TxTools.FootingPlanExport
{
    // 原生命令 "Save Section Contour as Component" 是交互式的半模态命令：它并不
    // 在场景里生成组件，而是弹出 "Save Component As" 另存为对话框，把截面轮廓
    // 另存成 .cojt。该对话框是统一的保存对话框，PS 会在保存前校验文件名，
    // 带路径分隔符的名字会被拒绝（The following characters are illegal for use
    // in filenames），因此这里只写纯文件名，并用 CDM_GETFOLDERPATH 读回对话框
    // 当前所在的文件夹，拼出真实落盘路径。
    internal static class SaveComponentDialog
    {
        internal static SaveDialogWatch Watch(string targetPath, TimeSpan timeout)
            => new SaveDialogWatch(Path.GetFileName(targetPath), timeout);
    }

    internal sealed class SaveDialogWatch : IDisposable
    {
        private const int WmCommand = 0x0111;
        private const int BmClick = 0x00F5;
        private const int IdOk = 1;
        private const int FileNameControlId = 1148;
        private const int PollIntervalMs = 60;
        private const int CdmGetFilePath = 0x0400 + 100 + 1;
        private const int CdmGetFolderPath = 0x0400 + 100 + 2;
        private const string DialogClass = "#32770";

        private readonly string _fileName;
        private readonly TimeSpan _timeout;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private readonly Thread _thread;

        // 已点击保存并确认对话框关闭后的完整路径；对话框被取消或超时为 null。
        internal string VerifiedPath { get; private set; }
        // 最近一次从对话框读到的文件夹，仅用于诊断输出。
        internal string LastFolder { get; private set; }
        // 是否已经点过确认，仅用于诊断输出。
        internal bool Clicked { get; private set; }

        internal SaveDialogWatch(string fileName, TimeSpan timeout)
        {
            _fileName = fileName;
            _timeout = timeout;
            _thread = new Thread(Run) { IsBackground = true, Name = "FootingPlanSaveDialog" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Dispose() { _stop.Set(); }

        private void Run()
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            var watch = Stopwatch.StartNew();
            while (!_stop.IsSet && watch.Elapsed < _timeout)
            {
                try
                {
                    IntPtr dialog = FindTargetDialog(self);
                    if (dialog != IntPtr.Zero && TrySave(dialog)) return;
                }
                catch (Exception) { }
                if (_stop.Wait(PollIntervalMs)) return;
            }
        }

        private bool TrySave(IntPtr dialog)
        {
            IntPtr edit = FindFillEdit(dialog);
            if (edit == IntPtr.Zero) return false;
            if (!string.Equals(Text(edit), _fileName, StringComparison.OrdinalIgnoreCase))
            {
                // 经典通用对话框把文件名放在 ComboBox 里，只写 Edit 可能与
                // ComboBox 的内部文本不同步，两者都写一次。
                IntPtr host = GetParent(edit);
                if (host != IntPtr.Zero)
                {
                    string hostClass = ClassName(host);
                    if (hostClass == "ComboBox" || hostClass == "ComboBoxEx32") SetWindowText(host, _fileName);
                }
                if (!SetWindowText(edit, _fileName)) return false;
                if (!string.Equals(Text(edit), _fileName, StringComparison.OrdinalIgnoreCase)) return false;
                Thread.Sleep(PollIntervalMs);
            }
            // 读目录只为诊断与验证；读不到也照样确认保存，产物由缓存快照定位。
            string folder = ReadFolder(dialog);
            LastFolder = folder;
            IntPtr confirm = GetDlgItem(dialog, IdOk);
            if (confirm == IntPtr.Zero) confirm = FindButton(dialog);
            if (confirm != IntPtr.Zero) SendMessage(confirm, BmClick, IntPtr.Zero, IntPtr.Zero);
            else PostMessage(dialog, WmCommand, (IntPtr)IdOk, IntPtr.Zero);
            Clicked = true;
            if (!WaitClosed(dialog)) return false;
            VerifiedPath = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, _fileName);
            return true;
        }

        private static bool WaitClosed(IntPtr dialog)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < 5000)
            {
                if (!IsWindow(dialog) || !IsWindowVisible(dialog)) return true;
                Thread.Sleep(50);
            }
            return false;
        }

        // 通用对话框的“保存在”目录：CDM_GETFOLDERPATH / CDM_GETFILEPATH 是
        // 通用对话框的兼容消息，经典对话框和 IFileDialog 都实现了；两者都拿
        // 不到时，退回“某个可见控件的文本本身就是存在的绝对路径”。
        private static string ReadFolder(IntPtr dialog)
        {
            string folder = ReadCdmPath(dialog, CdmGetFolderPath);
            if (folder != null && Directory.Exists(folder)) return folder;
            string full = ReadCdmPath(dialog, CdmGetFilePath);
            if (full != null)
            {
                try
                {
                    string parent = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
                }
                catch (ArgumentException) { }
                catch (PathTooLongException) { }
            }
            string found = null;
            EnumChildWindows(dialog, (handle, state) =>
            {
                if (!IsWindowVisible(handle)) return true;
                string text = Text(handle).Trim().Trim('"');
                if (text.Length < 4 || text.IndexOf(":\\", StringComparison.Ordinal) < 0) return true;
                if (!Directory.Exists(text)) return true;
                found = text;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string ReadCdmPath(IntPtr dialog, int message)
        {
            var buffer = new StringBuilder(2048);
            IntPtr length = SendMessageText(dialog, message, (IntPtr)buffer.Capacity, buffer);
            if (length.ToInt64() <= 0) return null;
            string value = buffer.ToString();
            return value.Length == 0 ? null : value;
        }

        private IntPtr FindTargetDialog(uint processId)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((handle, state) =>
            {
                if (!IsWindowVisible(handle)) return true;
                if (ClassName(handle) != DialogClass) return true;
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                if (pid != processId) return true;
                if (!IsTargetDialog(handle)) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private bool IsTargetDialog(IntPtr handle)
        {
            if (FindCojtEdit(handle) != IntPtr.Zero) return true;
            string title = Text(handle);
            return title.IndexOf("Save Component", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   title.IndexOf("保存组件", StringComparison.Ordinal) >= 0;
        }

        // 默认文件名就是“目标名.cojt”，据此定位文件名编辑框，不依赖控件 ID
        // （经典对话框与 IFileDialog 的控件树不同）。
        private IntPtr FindCojtEdit(IntPtr dialog)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dialog, (handle, state) =>
            {
                if (!IsWindowVisible(handle)) return true;
                if (ClassName(handle) != "Edit") return true;
                string text = Text(handle);
                if (!text.EndsWith(".cojt", StringComparison.OrdinalIgnoreCase)) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        // 兜底顺序：带 .cojt 的 Edit → 文件名组合框里的 Edit → 第一个可见 Edit。
        private IntPtr FindFillEdit(IntPtr dialog)
        {
            IntPtr byExtension = FindCojtEdit(dialog);
            if (byExtension != IntPtr.Zero) return byExtension;
            IntPtr combo = GetDlgItem(dialog, FileNameControlId);
            if (combo != IntPtr.Zero)
            {
                if (ClassName(combo) == "Edit") return combo;
                IntPtr inside = IntPtr.Zero;
                EnumChildWindows(combo, (handle, state) =>
                {
                    if (!IsWindowVisible(handle)) return true;
                    if (ClassName(handle) != "Edit") return true;
                    inside = handle;
                    return false;
                }, IntPtr.Zero);
                if (inside != IntPtr.Zero) return inside;
            }
            IntPtr first = IntPtr.Zero;
            EnumChildWindows(dialog, (handle, state) =>
            {
                if (first != IntPtr.Zero) return false;
                if (!IsWindowVisible(handle)) return true;
                if (ClassName(handle) != "Edit") return true;
                first = handle;
                return false;
            }, IntPtr.Zero);
            return first;
        }

        private IntPtr FindButton(IntPtr dialog)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(dialog, (handle, state) =>
            {
                if (!IsWindowVisible(handle)) return true;
                if (ClassName(handle) != "Button") return true;
                string caption = Text(handle).Replace("&", string.Empty);
                if (caption.IndexOf("保存", StringComparison.Ordinal) < 0 &&
                    caption.IndexOf("Save", StringComparison.OrdinalIgnoreCase) < 0) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        private static string ClassName(IntPtr handle)
        {
            var buffer = new StringBuilder(256);
            GetClassName(handle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private static string Text(IntPtr handle)
        {
            var buffer = new StringBuilder(1024);
            GetWindowText(handle, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        private delegate bool EnumWindowProc(IntPtr handle, IntPtr state);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowProc callback, IntPtr state);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr state);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr handle, StringBuilder buffer, int length);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder buffer, int length);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr handle, string text);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
        private static extern IntPtr SendMessageText(IntPtr handle, int message, IntPtr wParam, StringBuilder lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    }
}
