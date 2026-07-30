// TxTools.Agent / Core / ModelFilter.cs
//
// 模型下拉列表的清洗。
//
// 问题:GET /v1/models 返回的是【平台全量目录】,不是"我能用的"。
//   • 百炼的业务空间白名单只管调用鉴权,不影响 /models 的返回内容 ——
//     授权了 5 个模型,列表里照样几十个。
//   • 目录里混着大量非对话模型(embedding / rerank / tts / 图像生成),
//     以及同一系列的日期快照变体(Qwen3.7-Flash 和 Qwen3.7-Flash-2026-07-15…)。
//   • 还有一批小参数/蒸馏模型不支持 function calling,选中后工具全废,
//     而且失败方式很难看:模型会用自然语言说"我要调用 xxx 工具"而不发 tool_calls,
//     agent 循环空转烧 token,用户还以为是插件坏了。
//
// 所以在客户端做一次清洗:剔除不可用的、折叠快照变体、可选按用户白名单收窄。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TxTools.Agent.Core
{
    public static class ModelFilter
    {
        /// <summary>
        /// 用户白名单:provider id -> 允许出现的模型名(或前缀)。
        /// 配了就只留这些,不配则只做规则清洗。
        /// 例:Whitelist["qwen"] = new[]{ "qwen3.8-max", "qwen3.7-plus", "deepseek-v4-flash-0731" };
        /// </summary>
        public static readonly Dictionary<string, string[]> Whitelist =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否把同系列的日期快照折叠成一条(只留不带日期的基础名)。</summary>
        public static bool CollapseSnapshots = true;

        /// <summary>清洗后的条数上限。超出按名字排序截断,避免下拉长到没法用。</summary>
        public static int MaxPerProvider = 40;

        // ── 明确不是对话模型的 ──
        private static readonly string[] NonChatMarkers =
        {
            "embedding", "embed-", "text-embedding",
            "rerank", "reranker",
            "tts", "asr", "audio", "speech", "voice", "sambert", "cosyvoice", "paraformer",
            "wanx", "image-", "-image", "imagen", "stable-diffusion", "flux",
            "video", "wan2", "i2v", "t2v",
            "ocr", "translation", "mt-", "-mt",
            "moderation", "safety", "guard",
            "background", "segment", "matting", "colorization",
        };

        // ── 小参数 / 蒸馏模型:function calling 基本不可靠 ──
        private static readonly Regex SmallParamRe = new Regex(
            @"(^|[-_.])(0\.5b|1\.5b|1b|3b|4b|7b|8b|9b|13b|14b)([-_.]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] DistillMarkers = { "distill", "-instruct-1", "-chat-1" };

        // ── 日期快照:xxx-2026-07-15 / xxx-20260715 / xxx-0731 ──
        private static readonly Regex SnapshotRe = new Regex(
            @"[-_](\d{4}-\d{2}-\d{2}|\d{8}|\d{4})$",
            RegexOptions.Compiled);

        /// <summary>
        /// 清洗某 provider 拉回来的模型列表。
        /// keepAlways 里的名字(通常是当前选中的模型)一定保留,免得用户正在用的被过滤掉。
        /// </summary>
        public static List<string> Clean(string providerId, IEnumerable<string> models,
                                         params string[] keepAlways)
        {
            var input = models == null ? new List<string>() : models.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
            if (input.Count == 0) return input;

            var keep = new HashSet<string>(
                (keepAlways ?? new string[0]).Where(k => !string.IsNullOrWhiteSpace(k)),
                StringComparer.OrdinalIgnoreCase);

            // 1) 用户白名单优先 —— 配了就以它为准,规则清洗不再介入
            string[] allow;
            if (Whitelist.TryGetValue(providerId ?? "", out allow) && allow != null && allow.Length > 0)
            {
                var picked = input.Where(m =>
                    keep.Contains(m) ||
                    allow.Any(a => m.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

                return Finish(picked, keep);
            }

            // 2) 规则清洗
            var result = input.Where(m => keep.Contains(m) || IsUsable(m)).ToList();

            // 3) 折叠日期快照:同一基础名只留一条,优先留不带日期的
            if (CollapseSnapshots) result = Collapse(result, keep);

            return Finish(result, keep);
        }

        /// <summary>是否值得出现在下拉里。</summary>
        public static bool IsUsable(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;
            var m = model.ToLowerInvariant();

            foreach (var marker in NonChatMarkers)
                if (m.IndexOf(marker, StringComparison.Ordinal) >= 0) return false;

            foreach (var marker in DistillMarkers)
                if (m.IndexOf(marker, StringComparison.Ordinal) >= 0) return false;

            // 小参数模型:工具调用不可靠,留在列表里只会误导
            if (SmallParamRe.IsMatch(m)) return false;

            return true;
        }

        /// <summary>同系列的日期快照折叠成一条。</summary>
        private static List<string> Collapse(List<string> models, HashSet<string> keep)
        {
            // 基础名 -> 候选(基础名本身优先,否则取日期最大的那个)
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var passthrough = new List<string>();

            foreach (var m in models)
            {
                if (keep.Contains(m)) { passthrough.Add(m); continue; }

                var mt = SnapshotRe.Match(m);
                if (!mt.Success) { passthrough.Add(m); continue; }

                var baseName = m.Substring(0, mt.Index);

                // 基础名本身也在列表里 → 快照直接丢弃
                if (models.Any(x => string.Equals(x, baseName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string cur;
                if (!groups.TryGetValue(baseName, out cur)
                    || string.CompareOrdinal(m, cur) > 0)   // 日期串比较,大的更新
                    groups[baseName] = m;
            }

            var outList = new List<string>(passthrough);
            outList.AddRange(groups.Values);
            return outList;
        }

        private static List<string> Finish(List<string> list, HashSet<string> keep)
        {
            var distinct = list
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinct.Count <= MaxPerProvider) return distinct;

            // 截断时保证 keep 里的不被切掉
            var must = distinct.Where(keep.Contains).ToList();
            var rest = distinct.Where(m => !keep.Contains(m)).Take(MaxPerProvider - must.Count);
            return must.Concat(rest).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
