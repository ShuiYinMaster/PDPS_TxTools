using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    public class ExportByColorCmd : TxButtonCommand
    {
        public override string Name { get { return ".按颜色导出STL→CATIA"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "按(设备×颜色)分组导出STL，自动导入CATIA并按原色上色"; } }
        public override string Description { get { return "选中多个设备(焊枪/抓手/机器人/夹具)，按(设备×颜色)分组导出单色STL，自动导入CATIA并上色"; } }

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
