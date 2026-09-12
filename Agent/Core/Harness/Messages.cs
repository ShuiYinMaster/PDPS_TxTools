using System;
using System.Collections.Generic;

namespace TxAgent.Core
{
    /// <summary>消息角色。</summary>
    public enum MessageRole
    {
        System = 0,
        User = 1,
        Assistant = 2,
        Tool = 3
    }

    /// <summary>模型发起的一次工具调用。</summary>
    public sealed class ToolCall
    {
        /// <summary>调用 ID，工具结果必须原样回填，供模型配对。</summary>
        public string Id { get; set; }

        /// <summary>工具名。</summary>
        public string Name { get; set; }

        /// <summary>参数 JSON 原文。Core 不解析，交由具体工具处理。</summary>
        public string ArgumentsJson { get; set; }

        public override string ToString()
        {
            return Name + "(" + (ArgumentsJson ?? "") + ")";
        }
    }

    /// <summary>会话中的一条消息。</summary>
    public sealed class ChatMessage
    {
        public MessageRole Role { get; set; }

        public string Content { get; set; }
        /// <summary>Provider-returned reasoning, retained for archive and compatible replay.</summary>
        public string ReasoningContent { get; set; }

        /// <summary>Role==Assistant 时可能携带工具调用。</summary>
        public List<ToolCall> ToolCalls { get; set; }

        /// <summary>Role==Tool 时必须回填对应的 ToolCall.Id。</summary>
        public string ToolCallId { get; set; }

        /// <summary>粗略 token 估算，用于上下文裁剪。0 表示未估算。</summary>
        public int ApproxTokens { get; set; }

        /// <summary>置为 true 的消息永不被裁剪（系统提示、任务目标等）。</summary>
        public bool Pinned { get; set; }

        /// <summary>创建时间，仅用于日志与排障。</summary>
        public DateTime CreatedUtc { get; set; }

        public ChatMessage()
        {
            CreatedUtc = DateTime.UtcNow;
        }

        public bool HasToolCalls
        {
            get { return ToolCalls != null && ToolCalls.Count > 0; }
        }

        public static ChatMessage CreateSystem(string text)
        {
            return new ChatMessage { Role = MessageRole.System, Content = text, Pinned = true };
        }

        public static ChatMessage CreateUser(string text)
        {
            return new ChatMessage { Role = MessageRole.User, Content = text };
        }

        public static ChatMessage CreateAssistant(string text, List<ToolCall> toolCalls)
        {
            return new ChatMessage { Role = MessageRole.Assistant, Content = text, ToolCalls = toolCalls };
        }

        public static ChatMessage CreateToolResult(string toolCallId, string content)
        {
            return new ChatMessage
            {
                Role = MessageRole.Tool,
                ToolCallId = toolCallId,
                Content = content
            };
        }
    }
}
