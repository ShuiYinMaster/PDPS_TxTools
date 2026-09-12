// TxTools.Agent / Core / LlmProviders.cs
// 内置 LLM 提供商元数据表 —— 全部走 OpenAI 兼容协议 (POST /v1/chat/completions),
// 差异只在 baseUrl / 模型列表 / API Key 获取方式。所以 DeepSeekClient 加个 baseUrl
// 参数就能通吃(类名沿用 DeepSeekClient 是历史命名,内部完全 provider 中立)。
//
// Ollama 本地不需要真实 key,但 Bearer header 里不能为空,填个占位字符串即可。

using System;
using System.Collections.Generic;
using System.Linq;

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
        /// <summary>用户自定义添加的 OpenAI 兼容 provider。</summary>
        public bool IsCustom { get; set; }
    }

    public static class LlmProviders
    {
        /// <summary>默认 provider id。用户没做过任何设置时的初始选择。</summary>
        public const string DefaultProviderId = "deepseek";

        /// <summary>内置 provider 的固定前缀, 用于给自定义 provider 分配不冲突的 id。</summary>
        private static readonly string[] _builtinIds =
            { "deepseek", "kimi", "qwen", "openai", "ollama" };

        /// <summary>所有内置 provider, 顺序 = UI 里 optgroup 顺序。</summary>
        public static readonly LlmProvider[] All = new[]
        {
            new LlmProvider
            {
                Id = "deepseek",
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Models = new[] { "deepseek-v4-pro", "deepseek-v4-flash", "deepseek-v4-flash-vision-exp"},
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
                // 硬编码仅是兜底默认:连接上后会被 /v1/models 拉取的真实列表覆盖。
                Models = new[] { "qwen2.5-coder:7b", "qwen2.5:7b", "qwen2.5-coder:14b", "llama3.1:8b" },
                KeyPageUrl = "https://ollama.com/library",
                IsLocal = true
            }
        };

        public static LlmProvider ById(string id)
        {
            if (string.IsNullOrEmpty(id)) return All[0];
            foreach (var p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            // 自定义 provider: 从持久化 prefs 里找
            foreach (var p in Custom())
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
            foreach (var p in Custom())
                if (p.Models != null)
                    foreach (var m in p.Models)
                        if (string.Equals(m, modelId, StringComparison.OrdinalIgnoreCase))
                            return p;
            return All[0];
        }

        // ── 自定义 provider (OpenAI 兼容) ──

        /// <summary>读取持久化的自定义 provider 列表。</summary>
        public static List<LlmProvider> Custom()
        {
            try
            {
                var prefs = UserPrefsStore.Load();
                return prefs.CustomProviders ?? new List<LlmProvider>();
            }
            catch { return new List<LlmProvider>(); }
        }

        /// <summary>全部 provider = 内置 + 自定义(自定义追加在末尾)。</summary>
        public static List<LlmProvider> GetAll()
        {
            var list = new List<LlmProvider>(All);
            list.AddRange(Custom());
            return list;
        }

        /// <summary>
        /// 新增或更新一个自定义 provider。id 冲突时按 displayName 自动分配一个不冲突的。
        /// 保存成功后返回最终生效的 provider。
        /// </summary>
        public static LlmProvider AddOrUpdateCustom(LlmProvider provider)
        {
            var prefs = UserPrefsStore.Load();
            if (prefs.CustomProviders == null)
                prefs.CustomProviders = new List<LlmProvider>();

            if (string.IsNullOrWhiteSpace(provider.Id) || IsBuiltinId(provider.Id))
                provider.Id = MakeUniqueCustomId(prefs.CustomProviders, provider.DisplayName);

            var idx = prefs.CustomProviders.FindIndex(
                p => string.Equals(p.Id, provider.Id, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) prefs.CustomProviders[idx] = provider;
            else prefs.CustomProviders.Add(provider);

            UserPrefsStore.Save(prefs);
            return provider;
        }

        public static void RemoveCustom(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || IsBuiltinId(id)) return;
            var prefs = UserPrefsStore.Load();
            if (prefs.CustomProviders == null) return;
            prefs.CustomProviders.RemoveAll(
                p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            UserPrefsStore.Save(prefs);
        }

        private static bool IsBuiltinId(string id)
        {
            foreach (var b in _builtinIds)
                if (string.Equals(b, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string MakeUniqueCustomId(List<LlmProvider> existing, string displayName)
        {
            var baseId = SanitizeId(displayName);
            var id = baseId;
            int n = 2;
            while (IsBuiltinId(id)
                   || existing.Exists(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                id = baseId + "-" + n;
                n++;
            }
            return id;
        }

        /// <summary>把显示名转成安全 id (小写字母数字、下划线、连字符)。</summary>
        private static string SanitizeId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "custom";
            var sb = new System.Text.StringBuilder();
            foreach (var c in name.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            var s = sb.ToString().Trim('-');
            return string.IsNullOrEmpty(s) ? "custom" : s;
        }
    }
}
