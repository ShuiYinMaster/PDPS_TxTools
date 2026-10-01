using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    public class ExportByColorCmd : TxButtonCommand
    {
        public override string Name { get { return ".直出3DXML / CGR / 网格"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "从 Process Simulate 直出保留装配层级的 3DXML，也可使用 CGR→CATIA 或通用网格兼容模式"; } }
        public override string Description { get { return "选中设备或组合，直接生成包含独立 CFV3 3DRep 与 PS 装配层级的 3DXML，无需 CATIA 中转"; } }

        public override void Execute(object cmdParams)
        {
            SynchronizationContext psCtx = SynchronizationContext.Current
                                          ?? new SynchronizationContext();

            try
            {
                if (ExportByColorForm.Instance == null || ExportByColorForm.Instance.IsDisposed)
                {
                    ExportByColorForm.Instance = new ExportByColorForm(psCtx);
                    ExportByColorForm.Instance.FormClosed += (s, e) => ExportByColorForm.Instance = null;
                }
                ExportByColorForm.Instance.Show();
                ExportByColorForm.Instance.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.ToString(), "启动失败");
            }
        }
    }
}
