using System;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace TxTools.ResourceImporter
{
    public sealed class ResourceImporterCommand : TxButtonCommand
    {
        public override string Bitmap => "image.Small.ResourceImporter.bmp";
        public override string LargeBitmap => "image.ResourceImporter.png";
        public override string Name => ".资源导入";
        public override string Category => "TxTools";
        public override string Tooltip => "批量转换模型并按指定原型插入工程根树";
        public override string Description => "批量导入文件，为每个模型指定设备原型及 COJT 存放目录";

        public override void Execute(object cmdParams)
        {
            try
            {
                new ResourceImporterForm(new ResourceImportSession()).Show();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.GetBaseException().Message, "资源导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
