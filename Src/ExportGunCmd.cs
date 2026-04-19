// ExportGunCmd.cs  —  C# 7.3
//
// 窗口层级方案（修复被PS主窗口覆盖）：
//   Application.Run(form) 没有 owner，窗体与PS窗口无Z序关系
//   → 改为 form.Show(psOwner)，建立所有权后窗体始终在PS主窗口之上
//   同时在 OnLoad 中调用 SetWindowPos(Handle, psHwnd, ...) 确保Z序正确
//   这样不是全局置顶，只确保插件在PS上方

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace MyPlugin.ExportGun
{
    public class ExportGunCmd : TxButtonCommand
    {
        public override string Name        { get { return "MyPlugin.ExportGun"; } }
        public override string Category    { get { return "My Plugins"; } }
        public override string Tooltip     { get { return "导出插枪和点云到 Catia"; } }
        public override string Description { get { return "将插枪及焊点云数据导出到 Catia（3dxml / CATProduct）"; } }

        public override void Execute(object cmdParams)
        {
            // ★ 在 PS 主线程捕获 SynchronizationContext
            SynchronizationContext psCtx = SynchronizationContext.Current
                                          ?? new SynchronizationContext();

            // 获取 PS 进程主窗口句柄
            IntPtr psHwnd = IntPtr.Zero;
            try { psHwnd = Process.GetCurrentProcess().MainWindowHandle; } catch { }

            IntPtr capturedHwnd = psHwnd;

            // 在独立 STA 线程运行窗体，不阻塞 PS 主线程
            Thread thread = new Thread(delegate()
            {
                Application.EnableVisualStyles();
                ExportGunForm form = new ExportGunForm(psCtx, capturedHwnd);

                // Application.Run(form) 内部会调用 form.Show()，
                // 但无法传递 owner。改用消息循环 + 手动 Show(owner)：
                //   form.Show(owner)  → 建立 Z 序所有权（在PS主窗口上方）
                //   Application.Run() → 无参版本，使用已有的消息泵
                // 注意：Application.Run() 无参会在当前线程上启动消息循环
                // 直到所有窗体关闭，行为等同 Application.Run(form)
                if (capturedHwnd != IntPtr.Zero)
                {
                    IWin32Window owner = new NativeWindow(capturedHwnd);
                    form.Show(owner);
                }
                else
                {
                    form.Show();
                }
                Application.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            try { TxApplication.StatusBarMessage = "导出插枪插件已启动"; } catch { }
        }

        // 轻量 IWin32Window 包装
        private sealed class NativeWindow : IWin32Window
        {
            private readonly IntPtr _h;
            internal NativeWindow(IntPtr h) { _h = h; }
            public IntPtr Handle { get { return _h; } }
        }
    }
}

