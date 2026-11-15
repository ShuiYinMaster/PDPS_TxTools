using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace TxTools.FootingPlanExport
{
    [Guid("DC8C3016-251F-4A8B-B65F-EE6011315B61")]
    public sealed class FootingPlanExportCommand : TxButtonCommand
    {
        private static FootingPlanExportForm _form;
        public override string Name => "导出地脚图";
        public override string Bitmap => "image.Small.FootingPlanExport.bmp";
        public override string LargeBitmap => "image.FootingPlanExport.png";
        public override string Category => "TxTools";
        public override string Description => "沿全局 Z 轴指定高度，自动截取整个场景并导出毫米制 DXF，无需选择设备";
        public override string Tooltip => "导出地脚图：截平面 → 截面边线 → COJT → DXF";

        public override void Execute(object cmdParams)
        {
            try
            {
                if (TxApplication.ActiveDocument == null) throw new InvalidOperationException("请先打开 PS 工程。");
                if (_form == null || _form.IsDisposed)
                {
                    _form = new FootingPlanExportForm();
                    _form.FormClosed += (s, e) => _form = null;
                    var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                    if (hwnd != IntPtr.Zero) _form.Show(new Owner(hwnd)); else _form.Show();
                }
                _form.Activate();
            }
            catch (Exception error)
            { MessageBox.Show(error.Message, "导出地脚图", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        private sealed class Owner : IWin32Window
        {
            public Owner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
