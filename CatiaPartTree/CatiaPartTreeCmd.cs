// CatiaPartTreeCmd.cs —— 命令入口：读取 CATIA 目录树创建/归类 PS 零件树。
// TxForm 必须在 PS 主线程创建并 Show()，SynchronizationContext 传入窗体备用。
using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.CatiaPartTree
{
    public class CatiaPartTreeCmd : TxButtonCommand
    {
        public override string Name { get { return ".目录树建零件树"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "读取 CATIA 目录树创建 PS 零件树并对已导入零件归类"; } }
        public override string Description { get { return "读取 CATIA Product 目录树，创建 PS 零件树并对已导入零件归类"; } }
        public override void Execute(object cmdParams)
        {
            SynchronizationContext psCtx = SynchronizationContext.Current
                                          ?? new SynchronizationContext();
            CatiaPartTreeForm form = new CatiaPartTreeForm(psCtx);
            form.Show();
            try { TxApplication.StatusBarMessage = "CATIA 目录树插件已启动"; } catch { }
        }
    }
}
