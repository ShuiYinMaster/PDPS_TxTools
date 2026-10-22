using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TxTools.CrossEnvIO
{
    internal static class DirectoryLinks
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,
            StringBuilder path, uint length, uint flags);

        internal static string Resolve(string path)
        {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("无法读取目录真实路径: " + path);
                var buffer = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("无法解析目录: " + path);
                string full = buffer.ToString();
                if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) full = @"\\" + full.Substring(8);
                else if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) full = full.Substring(4);
                return full.TrimEnd('\\', '/');
            }
        }

        internal static bool PointsTo(string link, string target)
        {
            try
            {
                if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0) return false;
                return string.Equals(Resolve(link), Resolve(target), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
