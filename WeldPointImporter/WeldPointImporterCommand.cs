using System;
using System.Windows.Forms;
using Tecnomatix.Engineering;

namespace TxTools.WeldPointImporter
{
    public sealed class WeldPointImporterCommand : TxButtonCommand
    {
        private static WeldPointImporterForm _form;

        public override string Name => "焊点提取与创建";
        public override string Category => "TxTools";
        public override string Description => "从 CATIA 或文件提取点位，预览和筛查重复点，在当前工程按世界坐标创建焊点。";
        public override string Bitmap => "image.Small.WeldPointImporter.bmp";
        public override string LargeBitmap => "image.WeldPointImporter.png";

        public override void Execute(object cmdParams)
        {
            try
            {
                if (_form == null || _form.IsDisposed) _form = new WeldPointImporterForm();
                if (!_form.Visible) _form.Show();
                _form.BringToFront();
                _form.Activate();
            }
            catch (Exception error)
            { MessageBox.Show(error.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
