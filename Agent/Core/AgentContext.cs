// TxTools.Agent / Core / AgentContext.cs
//
// Agent 运行期的宿主注入点。凡是"宿主环境才知道、工具需要读"的单例委托都放这里，
// 避免每个工具各自挂一个静态委托 —— 宿主漏设一个的表现是静默降级（如 conv_id 为空），
// 收拢成一处后漏设的风险面最小。
//
// 现状只有一个成员:ConvIdProvider。以后有别的注入（审批回调、审计器、环境名…）
// 统一放这个类。

using System;

namespace TxTools.Agent.Core
{
    /// <summary>Agent 运行期上下文：宿主的单例注入点。</summary>
    public static class AgentContext
    {
        /// <summary>
        /// 取当前对话 id，供片段固化/归因记录 convId。由宿主在注册工具时注入。
        /// 返回 null 时调用方按"无对话上下文"处理（不崩）。
        /// </summary>
        public static Func<string> ConvIdProvider { get; set; }

        /// <summary>取当前对话 id；未注入或取不到时返回 null。</summary>
        public static string CurrentConvId()
        {
            try
            {
                var p = ConvIdProvider;
                return p != null ? p() : null;
            }
            catch { return null; }
        }
    }
}
