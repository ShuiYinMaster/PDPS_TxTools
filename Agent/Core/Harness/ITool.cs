using System;

namespace TxAgent.Core
{
    /// <summary>工具执行结果。失败不抛异常，一律以结果形式回灌给模型自修。</summary>
    public sealed class ToolResult
    {
        public bool Success { get; set; }

        /// <summary>回灌给模型的正文。成功时是数据，失败时是错误描述 + 修复线索。</summary>
        public string Content { get; set; }

        /// <summary>错误分类，用于熔断统计与日志聚合，例如 "compile" / "api" / "arg" / "host"。</summary>
        public string ErrorKind { get; set; }

        /// <summary>true 表示这次调用实际修改了场景（用于判断是否已产生不可逆变更）。</summary>
        public bool MutatedScene { get; set; }

        public static ToolResult Ok(string content)
        {
            return new ToolResult { Success = true, Content = content };
        }

        public static ToolResult OkMutated(string content)
        {
            return new ToolResult { Success = true, Content = content, MutatedScene = true };
        }

        public static ToolResult Fail(string errorKind, string content)
        {
            return new ToolResult { Success = false, ErrorKind = errorKind, Content = content };
        }
    }

    /// <summary>
    /// 工具契约。Core 不依赖任何 Tecnomatix 类型；
    /// PS 相关工具在适配层实现本接口，通过 host.Invoke 封送到主线程。
    /// </summary>
    public interface ITool
    {
        /// <summary>工具名，必须唯一，建议 snake_case。</summary>
        string Name { get; }

        /// <summary>给模型看的说明。写清用途、前置条件、已知陷阱。</summary>
        string Description { get; }

        /// <summary>参数的 JSON Schema 原文。Core 原样透传给 ILlmClient。</summary>
        string ParametersJsonSchema { get; }

        /// <summary>是否可能修改场景。true 会触发回滚点创建。</summary>
        bool IsWrite { get; }

        /// <summary>是否为破坏性操作（删除、批量改写），true 会强制用户确认。</summary>
        bool IsDestructive { get; }

        /// <summary>
        /// 同步执行。PS SDK 调用请在实现内用 host.Invoke 封送主线程。
        /// 实现方不应抛异常——捕获后返回 ToolResult.Fail，让模型看到错误并自修。
        /// </summary>
        ToolResult Execute(string argumentsJson, IAgentHost host);
    }

    /// <summary>把 ITool 的元数据打平，供 ILlmClient 序列化成各家 API 的 tools 字段。</summary>
    public sealed class ToolSchema
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string ParametersJsonSchema { get; set; }
    }
}
