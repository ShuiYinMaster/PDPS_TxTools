// TxTools.Agent / Core / LlmProviders.cs
// 内置 LLM 提供商元数据表 —— 全部走 OpenAI 兼容协议 (POST /v1/chat/completions),
// 差异只在 baseUrl / 模型列表 / API Key 获取方式。所以 DeepSeekClient 加个 baseUrl
// 参数就能通吃(类名沿用 DeepSeekClient 是历史命名,内部完全 provider 中立)。
//
// Ollama 本地不需要真实 key,但 Bearer header 里不能为空,填个占位字符串即可。

using System;
using System.Collections.Generic;

namespace TxTools.Agent.Core
{
    public sealed class LlmProvider
    {
        /// <summary>唯一 id, 用作 keyfile 名(如 "deepseek" → deepseek.key)。小写、无空格。</summary>
        public string Id { get; set; }
        /// <summary>UI 显示的名字, 如 "DeepSeek" / "Kimi (Moonshot)"。</summary>
        public string DisplayName { get; set; }
        /// <summary>OpenAI 兼容 base URL, 不含 /v1/chat/completions 后缀。</summary>
        public string BaseUrl { get; set; }
        /// <summary>推荐模型 id 列表(顺序 = UI 显示顺序,第一个是默认)。</summary>
        public string[] Models { get; set; }
        /// <summary>Key 获取地址(UI 里显示成"获取:xxx")。</summary>
        public string KeyPageUrl { get; set; }
        /// <summary>是否本地服务(如 Ollama), true 表示 key 可以留空/占位。</summary>
        public bool IsLocal { get; set; }
    }

    public static class LlmProviders
    {
        /// <summary>默认 provider id。用户没做过任何设置时的初始选择。</summary>
        public const string DefaultProviderId = "deepseek";

        /// <summary>所有内置 provider, 顺序 = UI 里 optgroup 顺序。</summary>
        public static readonly LlmProvider[] All = new[]
        {
            new LlmProvider
            {
                Id = "deepseek",
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Models = new[] { "deepseek-v4-pro", "deepseek-v4-flash"},
                KeyPageUrl = "https://platform.deepseek.com"
            },
            new LlmProvider
            {
                Id = "kimi",
                DisplayName = "Kimi (Moonshot)",
                BaseUrl = "https://api.moonshot.cn",
                Models = new[] { "kimi-k3", "kimi-k2.7-code", "kimi-k2.6" },
                KeyPageUrl = "https://platform.moonshot.cn"
            },
            new LlmProvider
            {
                Id = "qwen",
                DisplayName = "\u5343\u95ee (Qwen)",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode",
                Models = new[] { "Qwen3.7-Plus", "Qwen3.7-Max", "Qwen3.6-Plus", "Qwen3.6-Max" },
                KeyPageUrl = "https://dashscope.console.aliyun.com/apiKey"
            },  
            new LlmProvider
            {
                Id = "openai",
                DisplayName = "OpenAI",
                BaseUrl = "https://api.openai.com",
                Models = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4-turbo" },
                KeyPageUrl = "https://platform.openai.com/api-keys"
            },
            new LlmProvider
            {
                Id = "ollama",
                DisplayName = "Ollama (\u672c\u5730)",
                BaseUrl = "http://localhost:11434",
                Models = new[] { "qwen2.5:14b", "qwen2.5-coder:14b", "llama3.1:8b", "deepseek-coder-v2:16b" },
                KeyPageUrl = "https://ollama.com/library",
                IsLocal = true
            }
        };

        public static LlmProvider ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return All[0];
            foreach (var p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return All[0];
        }

        /// <summary>找一个已知模型属于哪个 provider (通过精确匹配 model id)。找不到返回默认。</summary>
        public static LlmProvider FindByModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return All[0];
            foreach (var p in All)
                foreach (var m in p.Models)
                    if (string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase))
                        return p;
            return All[0];
        }
    }
}
