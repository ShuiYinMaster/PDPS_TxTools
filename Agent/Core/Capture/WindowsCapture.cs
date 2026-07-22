// TxTools.Agent / Core / Capture / WindowsCapture.cs
// 纯 Win32 GDI + P/Invoke 实现的截图工具类。不依赖 PS SDK。
//
// 提供 4 种截图模式:
//   - FindPsMainWindow()      找 PS 主窗口 HWND (优先取当前进程主窗口,因为插件跑在 PS 进程内)
//   - CaptureWindow(hwnd)     截整个窗口(含标题栏)
//   - CaptureClientArea(hwnd) 只截窗口客户区(去掉标题栏边框,PS 3D 视口通常占大部分)
//   - CaptureFullScreen()     全屏
//   - CaptureRegion(rect)     指定屏幕区域

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TxTools.Agent.Core
{
    public static class WindowsCapture
    {
        /// <summary>
        /// 找 PS 主窗口 HWND。策略:
        ///   1. 当前进程主窗口 (插件跑在 PS 进程内,一命中即用)
        ///   2. 按候选进程名扫 (兜底,通常走不到)
        /// </summary>
        public static IntPtr FindPsMainWindow()
        {
            try
            {
                var cur = Process.GetCurrentProcess();
                if (cur.MainWindowHandle != IntPtr.Zero) return cur.MainWindowHandle;
            }
            catch { }

            foreach (var name in new[] { "eMPower", "eMServer", "Tecnomatix", "TxRuntime" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                        if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        /// <summary>整个窗口截图(含标题栏),保存到 outputPath (png)。</summary>
        public static string CaptureWindow(IntPtr hwnd, string outputPath)
        {
            RECT rc;
            if (!GetWindowRect(hwnd, out rc))
                throw new InvalidOperationException("GetWindowRect \u5931\u8d25");
            return CaptureScreenRect(rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, outputPath);
        }

        /// <summary>只截窗口客户区(去掉标题栏边框)。PS 主窗口客户区大部分是 3D 视图。</summary>
        public static string CaptureClientArea(IntPtr hwnd, string outputPath)
        {
            RECT rc;
            if (!GetClientRect(hwnd, out rc))
                throw new InvalidOperationException("GetClientRect \u5931\u8d25");

            var pt = new POINT { X = 0, Y = 0 };
            ClientToScreen(hwnd, ref pt);
            return CaptureScreenRect(pt.X, pt.Y, rc.right, rc.bottom, outputPath);
        }

        /// <summary>全屏截图 (主显示器)。</summary>
        public static string CaptureFullScreen(string outputPath)
        {
            var b = Screen.PrimaryScreen.Bounds;
            return CaptureScreenRect(b.X, b.Y, b.Width, b.Height, outputPath);
        }

        /// <summary>指定屏幕区域截图。x/y = 屏幕像素坐标。</summary>
        public static string CaptureRegion(int x, int y, int width, int height, string outputPath)
        {
            return CaptureScreenRect(x, y, width, height, outputPath);
        }

        // ── 内部: 统一的屏幕矩形抓取 ──

        private static string CaptureScreenRect(int x, int y, int w, int h, string outputPath)
        {
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException("\u622a\u56fe\u533a\u57df\u5c3a\u5bf8\u65e0\u6548: " + w + "x" + h);

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                bmp.Save(outputPath, ImageFormat.Png);
            }
            return outputPath;
        }

        // ── P/Invoke ──

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);
    }
}
