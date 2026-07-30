// TxTools.Agent / Core / ITxOffUiThreadTool.cs
//
// 标记接口:实现它的工具【禁止】被封送到 PS 主线程执行。
//
// 背景:绝大多数工具要调 Tecnomatix.Engineering,那些 API 非线程安全,
// 所以 TxAgentToolAdapter 默认用 host.Invoke(SynchronizationContext.Send) 把调用
// 封送回主线程。但对"阻塞等待用户交互"的工具,这一封送会直接死锁:
//
//   主线程 --Send--> 工具阻塞等用户点击
//        ^                    |
//        |                    v
//   用户点击需要主线程的消息循环来派发 —— 而主线程正卡在 Send 里
//
// 结果是整个 PS UI 冻结,连对话框的关闭按钮都点不动。
//
// 实现本接口的工具会在后台线程直接执行,主线程的消息循环保持畅通,
// 用户的点击才能被处理。代价是这类工具内部不得直接触碰 PS SDK 或 UI 控件 ——
// 需要访问 UI 时必须自行封送(例如自建 STA 线程跑对话框,或用 Control.Invoke)。

namespace TxTools.Agent.Core
{
    public interface ITxOffUiThreadTool
    {
    }
}
