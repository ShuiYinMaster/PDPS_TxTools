// ThemeTunerCmd.cs —— 命令入口：打开主题调整窗体，运行时更换套件配色主题。
// TxForm 必须在 PS 主线程创建并 Show()。
using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.ThemeTuner
{
    public class ThemeTunerCmd : TxButtonCommand
    {
        public override string Name { get { return ".主题调整"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "更换套件窗体的主题配色（暖色/冷色/浅色/琥珀/草木绿/自定义）"; } }
        public override string Description { get { return "调整 FormUiKit 主题配色并持久化到 theme.cfg"; } }
        public override void Execute(object cmdParams)
        {
            SynchronizationContext psCtx = SynchronizationContext.Current
                                          ?? new SynchronizationContext();
            ThemeTunerForm form = new ThemeTunerForm(psCtx);
            form.Show();
            try { TxApplication.StatusBarMessage = "主题调整插件已启动"; } catch { }
        }
    }
}
