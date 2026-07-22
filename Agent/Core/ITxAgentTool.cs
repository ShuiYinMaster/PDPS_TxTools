// TxTools.Agent / Core / ITxAgentTool.cs
// agent 可调用的工具契约。每个工具就是一个带 JSON Schema 的可调用函数。
// Execute 始终在 PS 的 UI 主线程上下文中被调用 (见 AgentLoop 的线程说明)。

using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public interface ITxAgentTool
    {
        /// <summary>工具名，须为唯一的小写下划线标识 (如 list_selected_objects)。</summary>
        string Name { get; }

        /// <summary>给模型看的描述。写清楚"做什么、何时用"，这是模型能否正确调用的关键。</summary>
        string Description { get; }

        /// <summary>true = 只读，免审批直接执行；false = 会改动场景，执行前需用户确认。</summary>
        bool IsReadOnly { get; }

        /// <summary>JSON Schema 形式的输入参数声明 (object 类型)。</summary>
        JObject InputSchema { get; }

        /// <summary>执行工具，返回给模型的文本结果。抛异常会被循环捕获并作为错误结果回传。</summary>
        string Execute(JObject input);
    }

    /// <summary>可选基类，默认只读、空参数表。</summary>
    public abstract class TxAgentToolBase : ITxAgentTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual bool IsReadOnly { get { return true; } }
        public virtual JObject InputSchema { get { return EmptyObjectSchema(); } }
        public abstract string Execute(JObject input);

        protected static JObject EmptyObjectSchema()
        {
            return JObject.Parse("{ \"type\": \"object\", \"properties\": {} }");
        }

        /// <summary>从输入里安全取字符串，缺失时返回默认值。</summary>
        protected static string GetString(JObject input, string key, string fallback = null)
        {
            if (input == null) return fallback;
            var t = input[key];
            return t == null || t.Type == JTokenType.Null ? fallback : (string)t;
        }
    }
}
