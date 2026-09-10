using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    public class ExportByColorCmd : TxButtonCommand
    {
        public override string Name { get { return ".布局/资源导出至CATIA"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "将资源导出为CGR并导入Catia，可导出STL，OBJ，FBX等网格资源"; } }
        public override string Description { get { return "将资源导出为CGR并导入Catia，可导出STL，OBJ，FBX等网格资源"; } }

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
