// CatiaPartTreeCmd.cs —— 命令入口：读取 CATIA 目录树创建/归类 PS 零件树。
// TxForm 必须在 PS 主线程创建并 Show()，SynchronizationContext 传入窗体备用。
using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.CatiaPartTree
{
    public class CatiaPartTreeCmd : TxButtonCommand
    {
        public override string Bitmap => "image.Small.CatiaPartTree.bmp";
        public override string LargeBitmap => "image.CatiaPartTree.png";

        public override string Name { get { return ".零件树整理"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "从 CATIA 或 PS 零件结构预览、创建层级并归类零件"; } }
        public override string Description { get { return "读取 CATIA 或 PS 零件结构，预览目标层级并创建、复用容器和归类零件"; } }
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
