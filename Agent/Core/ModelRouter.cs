// TxTools.Agent / Core / ModelRouter.cs
//
// 按【任务需要什么能力】而不是【我习惯用哪个模型】来选模型。
//
// 触发这套东西的直接需求是图像识别:DeepSeek 系列不支持视觉,
// 遇到图片必须换到 Kimi 或千问。但一旦要跨 provider 调度,
// 就该顺手把"便宜活别用贵模型"这件事也一起做了 —— 萃取、摘要、分类这类任务
// 用 flash 档就够,没必要走主模型。
//
// 设计原则:
//   • 主对话模型永远由用户在 UI 里选,路由【不抢】它 —— 用户选了什么就是什么。
//     路由只负责"主模型干不了的活"(视觉)和"明确该降档的活"(萃取/摘要)。
//   • 找不到合适的候选时返回主模型,让上层报错,而不是静默换一个用户没预期的模型。
//   • Key 缺失的 provider 直接跳过,不做无谓的失败调用。
//
// provider id 与 LlmProviders / KeyStore 完全一致(deepseek / kimi / qwen / openai / ollama),
// BaseUrl 一律从 LlmProviders 取,本文件不再各存一份端点。
//
// 【百炼的 base_url 有讲究】通用端点 https://dashscope.aliyuncs.com/compatible-mode
// 和业务空间专属端点是两回事:
//     https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode
// API Key 与地域强绑定,base_url 必须和 key 的地域一致。
// 代理的三方模型(deepseek / kimi / glm)在通用端点上可能是降级处理 ——
// 疑似 tool_calls 解析不出来的成因之一。
// 若 key 是业务空间下发的,请把 LlmProviders 里 qwen 的 BaseUrl 改成专属端点。

using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.Agent.Core
{
    /// <summary>任务场景 —— 决定需要什么能力、可以降到多便宜。</summary>
    public enum TaskScene
    {
        /// <summary>主对话:工具调用 + 多轮推理。用用户选的模型。</summary>
        Chat = 0,
        /// <summary>看图:必须视觉能力。</summary>
        Vision = 1,
        /// <summary>轻量结构化任务(经验萃取、摘要、分类)。要便宜,不要工具。</summary>
        Cheap = 2,
        /// <summary>长文本处理:优先大窗口。</summary>
        LongContext = 3
    }

    public sealed class ModelSpec
    {
        /// <summary>provider id，与 LlmProviders / KeyStore 一致:deepseek / kimi / qwen / openai / ollama</summary>
        public string Provider { get; set; }
        public string ModelId { get; set; }

        /// <summary>
        /// 端点。不在这里硬编码 —— 统一从 LlmProviders 取，
        /// 免得同一个 BaseUrl 在两处各存一份、改了一处忘另一处。
        /// </summary>
        public string BaseUrl
        {
            get
            {
                try
                {
                    var p = LlmProviders.ById(Provider);
                    if (p != null && !string.IsNullOrEmpty(p.BaseUrl)) return p.BaseUrl;
                }
                catch { }
                return null;
            }
        }

        public bool SupportsVision { get; set; }
        public bool SupportsTools { get; set; }
        public int ContextWindow { get; set; }

        /// <summary>相对成本档:1=最便宜。同等能力下取小的。</summary>
        public int CostTier { get; set; }

        public override string ToString()
        {
            return Provider + "/" + ModelId;
        }
    }

    public static class ModelRouter
    {
        // ── 候选表 ──
        //
        // 只列"确实要用"的几个,不做全量目录 —— 模型迭代快,维护全量表是负担。
        // 新增 provider 在这里加一行 + 在 KeyName 里给出取 key 的名字即可。

        private static readonly List<ModelSpec> Catalog = new List<ModelSpec>
        {
            // ── DeepSeek 官方 ── 主力对话,1M 上下文,【不支持视觉】
            new ModelSpec { Provider = "deepseek", ModelId = "deepseek-v4-flash",
                            SupportsVision = false, SupportsTools = true,
                            ContextWindow = 1000000, CostTier = 1 },
            new ModelSpec { Provider = "deepseek", ModelId = "deepseek-v4-pro",
                            SupportsVision = false, SupportsTools = true,
                            ContextWindow = 1000000, CostTier = 3 },

            // ── Kimi(Moonshot) 官方 ──
            // k3 原生支持视觉 + 1M 上下文 + Agent 能力,是目前视觉任务的首选:
            // 它能同时看图和调工具,不像纯 vision 模型那样只能回文字。
            new ModelSpec { Provider = "kimi", ModelId = "kimi-k3",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 1000000, CostTier = 3 },
            new ModelSpec { Provider = "kimi", ModelId = "kimi-k2.6",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 256000, CostTier = 2 },
            new ModelSpec { Provider = "kimi", ModelId = "kimi-k2.5",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 256000, CostTier = 2 },
            // 纯编程模型,无视觉;长上下文里指令遵循更可靠
            new ModelSpec { Provider = "kimi", ModelId = "kimi-k2.7-code",
                            SupportsVision = false, SupportsTools = true,
                            ContextWindow = 256000, CostTier = 2 },
            // 旧 vision-preview 系列:窗口小,仅作兜底
            new ModelSpec { Provider = "kimi", ModelId = "moonshot-v1-128k-vision-preview",
                            SupportsVision = true, SupportsTools = false,
                            ContextWindow = 128000, CostTier = 4 },
            new ModelSpec { Provider = "kimi", ModelId = "moonshot-v1-32k-vision-preview",
                            SupportsVision = true, SupportsTools = false,
                            ContextWindow = 32000, CostTier = 4 },

            // ── 阿里百炼(DashScope) ──
            // 【注意】百炼是聚合平台,同一把 key 还能直接调 kimi-k3 / deepseek-v4-pro /
            // glm-5.2 / MiniMax-M3。所以只配这一家 key 也能拿到视觉能力,
            // 不必再去 Moonshot 单独开户。下面把常用的几个都列进来。
            new ModelSpec { Provider = "qwen", ModelId = "qwen3.7-plus",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 131072, CostTier = 2 },
            new ModelSpec { Provider = "qwen", ModelId = "qwen3.5-omni-plus",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 131072, CostTier = 2 },
            new ModelSpec { Provider = "qwen", ModelId = "qwen3.8-max-preview",
                            SupportsVision = false, SupportsTools = true,
                            ContextWindow = 131072, CostTier = 4 },
            new ModelSpec { Provider = "qwen", ModelId = "qwen3.7-flash",
                            SupportsVision = false, SupportsTools = true,
                            ContextWindow = 131072, CostTier = 1 },
            // 百炼代理的三方模型 —— 用 qwen(百炼) 的 key 就能调 kimi-k3。
            // 与上面官方 kimi 那条同 ModelId:两条都在候选里，
            // 谁的 key 配了就用谁；都配了按 CostTier 排序，这里设得略高，优先走官方直连。
            new ModelSpec { Provider = "qwen", ModelId = "kimi-k3",
                            SupportsVision = true, SupportsTools = true,
                            ContextWindow = 1000000, CostTier = 4 },
        };

        /// <summary>
        /// 参与路由的 provider。id 与 LlmProviders / KeyStore 完全一致，
        /// 所以取 key 就是 KeyStore.Load(providerId)，无需再做名称映射。
        /// </summary>
        private static readonly string[] RoutableProviders =
            { "deepseek", "kimi", "qwen", "openai" };

        /// <summary>
        /// 视觉任务默认走哪个 provider。
        ///
        /// 默认 qwen(百炼千问):识图约 1 分/张，是目前性价比最高的。
        /// 主对话继续用 DeepSeek(1M 上下文、缓存命中 0.02元/M)，看图才委托出去 ——
        /// 这个组合比把主模型整体换成视觉模型便宜一个量级。
        /// 置空则按 CostTier 自动挑；UI 可做成下拉:自动 / 千问 / Kimi。
        /// </summary>
        public static string PreferredVisionProvider { get; set; }

        static ModelRouter()
        {
            PreferredVisionProvider = "qwen";
        }

        /// <summary>
        /// 当前主对话选的 provider。由 TxAgentForm 在切换 provider 时设置。
        ///
        /// 【为什么必须有这个】模型名在跨 provider 时会重名 ——
        /// 百炼(qwen)代理了 deepseek-v4-flash / kimi-k3 等一堆三方模型,
        /// 名字和官方完全一样。只按模型名反查必然歧义:
        /// 用户选的是"千问组下的 deepseek-v4-flash",查表却可能命中官方那条。
        /// 所以任何"由模型名推断规格"的地方都要带上 provider 一起定位。
        /// </summary>
        public static string CurrentProviderId { get; set; }

        // ── 取 key ──

        /// <summary>取某 provider 的 API key。未配置返回 null。</summary>
        public static string GetKey(string provider)
        {
            if (string.IsNullOrEmpty(provider)) return null;
            try
            {
                var k = KeyStore.Load(provider);
                return string.IsNullOrWhiteSpace(k) ? null : k;
            }
            catch { return null; }
        }

        public static bool HasKey(string provider)
        {
            return !string.IsNullOrEmpty(GetKey(provider));
        }

        /// <summary>已配置 key 的 provider 列表,供 UI 展示。</summary>
        public static List<string> AvailableProviders()
        {
            return RoutableProviders.Where(HasKey).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        // ── 选型 ──

        /// <summary>
        /// 按场景选模型。currentModel 是用户当前选的主模型。
        /// 返回 null 表示没有可用候选(通常是没配对应 provider 的 key),调用方应给出明确提示。
        /// </summary>
        public static ModelSpec Select(TaskScene scene, string currentModel)
        {
            switch (scene)
            {
                case TaskScene.Vision:
                    return SelectVision();

                case TaskScene.Cheap:
                    return Candidates(m => m.SupportsTools == false || true)
                           .OrderBy(m => m.CostTier)
                           .FirstOrDefault()
                        ?? FindByModelId(currentModel, CurrentProviderId);

                case TaskScene.LongContext:
                    return Candidates(m => true)
                           .OrderByDescending(m => m.ContextWindow)
                           .ThenBy(m => m.CostTier)
                           .FirstOrDefault()
                        ?? FindByModelId(currentModel, CurrentProviderId);

                default:
                    // 主对话不抢用户的选择
                    return FindByModelId(currentModel, CurrentProviderId);
            }
        }

        private static ModelSpec SelectVision()
        {
            var vision = Candidates(m => m.SupportsVision).ToList();
            if (vision.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(PreferredVisionProvider))
            {
                var pref = vision
                    .Where(m => string.Equals(m.Provider, PreferredVisionProvider, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.CostTier)
                    .FirstOrDefault();
                if (pref != null) return pref;
            }

            return vision.OrderBy(m => m.CostTier).ThenBy(m => m.ModelId, StringComparer.Ordinal).First();
        }

        private static IEnumerable<ModelSpec> Candidates(Func<ModelSpec, bool> filter)
        {
            return Catalog.Where(m => filter(m) && HasKey(m.Provider));
        }

        /// <summary>
        /// 按 provider + 模型名反查规格。
        /// provider 为空时退回 CurrentProviderId,再没有才按模型名猜 —— 这一步是有歧义的,
        /// 只作最后兜底,调用方应尽量把 provider 传进来。
        /// </summary>
        public static ModelSpec FindByModelId(string modelId, string provider = null)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return null;

            if (string.IsNullOrWhiteSpace(provider)) provider = CurrentProviderId;

            // provider 已知 → 双键精确定位,不会被跨 provider 的同名模型串味
            if (!string.IsNullOrWhiteSpace(provider))
            {
                var exact = Catalog.FirstOrDefault(m =>
                    string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
                if (exact != null) return exact;

                // 表里没登记(百炼代理的模型太多,不可能全列),按该 provider 现造一条
                return Synthesize(modelId, provider);
            }

            var hit = Catalog.FirstOrDefault(m =>
                string.Equals(m.ModelId, modelId, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;

            // provider 完全未知时才走到这里:只能按模型名猜归属。
            // 【这一步有歧义】百炼代理了 deepseek/kimi 等同名模型,猜出来的可能不是用户实际在用的那家。
            // 所以调用方应尽量传 provider,或提前设好 CurrentProviderId。
            var lower = modelId.ToLowerInvariant();
            var guessed = "deepseek";
            if (lower.Contains("kimi") || lower.Contains("moonshot")) guessed = "kimi";
            else if (lower.Contains("qwen") || lower.Contains("glm") || lower.Contains("minimax")) guessed = "qwen";
            else if (lower.Contains("gpt") || lower.StartsWith("o")) guessed = "openai";

            return Synthesize(modelId, guessed);
        }

        /// <summary>表里没登记时按模型名特征现造一条规格。</summary>
        private static ModelSpec Synthesize(string modelId, string provider)
        {
            var lower = (modelId ?? "").ToLowerInvariant();
            return new ModelSpec
            {
                Provider = provider,
                ModelId = modelId,
                SupportsVision = lower.Contains("vision") || lower.Contains("-vl-")
                                 || lower.Contains("kimi-k3") || lower.Contains("kimi-k2.6")
                                 || lower.Contains("kimi-k2.5") || lower.Contains("omni"),
                SupportsTools = true,
                ContextWindow = ContextWindowFor(modelId, provider),
                CostTier = 2
            };
        }

        /// <summary>上下文窗口。取不到按 128k 兜底 —— 宁可保守也不要撑爆。</summary>
        public static int ContextWindowFor(string model, string provider = null)
        {
            var m = (model ?? "").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(provider)) provider = CurrentProviderId;

            // 先查目录。带 provider 时双键精确匹配 —— 同名模型在不同 provider 上
            // 窗口可能不同(如百炼代理版常有更小的限制),不能只按名字取。
            var spec = Catalog.Find(delegate (ModelSpec x)
            {
                if (!string.Equals(x.ModelId, model, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrWhiteSpace(provider)) return true;
                return string.Equals(x.Provider, provider, StringComparison.OrdinalIgnoreCase);
            });
            if (spec != null) return spec.ContextWindow;

            if (m.Contains("deepseek-v4") || m.Contains("v4-flash") || m.Contains("v4-pro")) return 1000000;
            if (m.Contains("kimi-k3")) return 1000000;
            if (m.Contains("kimi-k2") || m.Contains("kimi")) return 256000;
            if (m.Contains("moonshot-v1-128k")) return 128000;
            if (m.Contains("moonshot-v1-32k")) return 32000;
            if (m.Contains("moonshot-v1-8k")) return 8000;
            if (m.Contains("qwen")) return 131072;
            if (m.Contains("gpt-4.1") || m.Contains("o3") || m.Contains("o4")) return 200000;
            if (m.Contains("claude")) return 200000;
            if (m.Contains("deepseek")) return 128000;
            return 128000;
        }

        // ── 客户端复用 ──
        //
        // DeepSeekClient 内部是静态 HttpClient,但每个实例持有自己的 key/endpoint,
        // 所以按 provider 缓存一份,避免每次调用都新建。

        private static readonly Dictionary<string, DeepSeekClient> ClientCache =
            new Dictionary<string, DeepSeekClient>(StringComparer.OrdinalIgnoreCase);
        private static readonly object ClientSync = new object();

        /// <summary>取该规格对应的客户端。key 未配置返回 null。</summary>
        public static DeepSeekClient GetClient(ModelSpec spec)
        {
            if (spec == null) return null;

            var key = GetKey(spec.Provider);
            if (string.IsNullOrEmpty(key)) return null;

            var baseUrl = spec.BaseUrl;
            if (string.IsNullOrEmpty(baseUrl)) return null;   // LlmProviders 里没登记该 provider

            var cacheKey = spec.Provider + "|" + baseUrl;
            lock (ClientSync)
            {
                DeepSeekClient c;
                if (ClientCache.TryGetValue(cacheKey, out c)) return c;

                c = new DeepSeekClient(key, baseUrl);
                ClientCache[cacheKey] = c;
                return c;
            }
        }

        /// <summary>换 key 后调用,让下次取客户端时重建。</summary>
        public static void ResetClients()
        {
            lock (ClientSync) { ClientCache.Clear(); }
        }
    }
}
