// WeldSpotGrouperCmd.cs — C# 7.3
// 插件入口（TxButtonCommand）：打开焊点自动分组窗体。
// 静态字段持有窗体引用，防 GC；非模态 Show()，在 PS 主线程。

using System;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace TxTools.WeldSpotGrouper
{
    public class WeldSpotGrouperCmd : TxButtonCommand
    {
        public override string Bitmap => "image.Small.WeldSpotGrouper.bmp";
        public override string LargeBitmap => "image.WeldSpotGrouper.png";

        private static GrouperForm _form;

        public override string Name { get { return ".快速分序"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Description => "按零件层级或绑定零件实例组合创建工序并分配焊点，支持预览与人工调整";
        public override void Execute(object cmdParams)
        {
            try
            {
                if (TxApplication.ActiveDocument == null)
                {
                    MessageBox.Show("请先打开一个研究。", "焊点自动分组", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (_form == null || _form.IsDisposed || _form.Closed)
                {
                    _form = new GrouperForm();
                    _form.FormClosed += (s, e) => { if (ReferenceEquals(_form, s)) _form = null; };
                }
                _form.Show();
                try { _form.BringToFront(); _form.Activate(); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开窗体失败：" + ex.Message, "焊点自动分组", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
