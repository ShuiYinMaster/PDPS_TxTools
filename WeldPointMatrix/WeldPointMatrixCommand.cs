using System;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace TxTools.WeldPointMatrix
{
    public sealed class WeldPointMatrixCommand : TxButtonCommand
    {
        public override string Name => "焊点矩阵";
        public override string Category => "TxTools";
        public override string Description => "并列查看多个焊接操作的焊点与过渡点，并定位机器人或插入点位";
        public override string LargeBitmap => "WeldSpotAllocator.png";

        public override void Execute(object cmdParams)
        {
            try { WeldPointMatrixForm.ShowSingleton(); }
            catch (Exception ex)
            {
                MessageBox.Show("启动焊点矩阵失败：" + ex.Message, Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
