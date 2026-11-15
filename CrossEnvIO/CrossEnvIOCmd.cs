// CrossEnvIOCmd.cs  --  C# 8.0
// 跨环境 I/O 命令入口（TxButtonCommand 单例）。

using System;
using System.Threading;
using Tecnomatix.Engineering;

namespace TxTools.CrossEnvIO
{
    public class CrossEnvIOCmd : TxButtonCommand
    {
        public override string Bitmap => "image.Small.CrossEnvIO.bmp";
        public override string LargeBitmap => "image.CrossEnvIO.png";

        public override string Name { get { return ".跨环境IO→模型结构/焊点"; } }
        public override string Category { get { return "TxTools"; } }
        public override string Tooltip { get { return "跨工程传输模型结构与焊点，支持不同库目录布局"; } }
        public override string Description { get { return "选择任务及接收位置，检查通过后确认执行；支持模型目录规则、离线包与任务查询。"; } }

        public override void Execute(object cmdParams)
        {
            SynchronizationContext psCtx = SynchronizationContext.Current
                                          ?? new SynchronizationContext();
            // 确保 RPC ping / 工具执行能封送回 PS 主线程（对齐 TxAgentCommand.Execute 的做法）
            try
            {
                TxTools.Agent.Core.PsContext.CaptureFromMainThread();
                TxTools.Agent.Core.PsContext.Current = new TxTools.Agent.Core.PsContext(psCtx);
            }
            catch { }

            try
            {
                if (CrossEnvIOForm.Instance == null || CrossEnvIOForm.Instance.IsDisposed)
                {
                    CrossEnvIOForm.Instance = new CrossEnvIOForm(psCtx);
                    CrossEnvIOForm.Instance.FormClosed += (s, e) => CrossEnvIOForm.Instance = null;
                }
                CrossEnvIOForm.Instance.Show();
                CrossEnvIOForm.Instance.Activate();
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.ToString(), "启动失败");
            }
        }
    }
}
