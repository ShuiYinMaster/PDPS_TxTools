using System;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using TxTools.WeldPointImporter;

namespace TxTools.ProcessDesigner
{
    public sealed class DesignerPartTreeCommand : TxButtonCommand
    {
        private static Form _form;
        public override string Name => "零件导入";
        public override string Category => "TxTools";
        public override string Description => "整理 CAD 装配的分支和名称，并导入 Process Designer 零件树。";
        public override string Bitmap => "image.Small.CatiaPartTree.bmp";
        public override string LargeBitmap => "image.CatiaPartTree.png";
        public override void Execute(object cmdParams) =>
            DesignerCommandWindow.Show(ref _form, () => new DesignerModelForm(parts: true), Name);
    }

    public sealed class DesignerResourceImporterCommand : TxButtonCommand
    {
        private static Form _form;
        public override string Name => "资源导入";
        public override string Category => "TxTools";
        public override string Description => "转换资源模型并导入 Process Designer 资源树。";
        public override string Bitmap => "image.Small.ResourceImporter.bmp";
        public override string LargeBitmap => "image.ResourceImporter.png";
        public override void Execute(object cmdParams) =>
            DesignerCommandWindow.Show(ref _form, () => new DesignerModelForm(parts: false), Name);
    }

    public sealed class DesignerWeldMfgImporterCommand : TxButtonCommand
    {
        private static Form _form;
        public override string Name => "焊点导入";
        public override string Category => "TxTools";
        public override string Description => "读取并预览焊点，将点位作为 MFG 导入 Process Designer 制造特征树。";
        public override string Bitmap => "image.Small.WeldPointImporter.bmp";
        public override string LargeBitmap => "image.WeldPointImporter.png";
        public override void Execute(object cmdParams) =>
            DesignerCommandWindow.Show(ref _form, () => new WeldPointImporterForm(designer: true), Name);
    }

    internal static class DesignerCommandWindow
    {
        internal static void Show(ref Form form, Func<Form> create, string title)
        {
            try
            {
                if (TxApplication.PlatformType != TxPlatformType.EmServer)
                    throw new InvalidOperationException("请在 Process Designer on eMServer 中打开该命令。");
                if (form == null || form.IsDisposed) form = create();
                if (!form.Visible) form.Show();
                form.Activate();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.GetBaseException().Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
