// TxTools.Agent / Core / Recipe.cs
// 配方 = 一串对"现有工具"的调用 + 参数模板。是数据，不是代码。
// 能力完全被已注册的原子工具集合框死，不引入超出现有工具的破坏面。
// 局限（有意为之）：只能做"参数化的固定序列"，不支持分支/循环/条件——
// 那类逻辑由 agent 在对话里直接调用工具处理，配方只固化稳定可复用的步骤。

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class RecipeParam
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("type")] public string Type { get; set; }       // string | number | boolean，默认 string
        [JsonProperty("required")] public bool Required { get; set; }
    }

    public sealed class RecipeStep
    {
        [JsonProperty("tool")] public string Tool { get; set; }       // 引用一个已注册的工具名
        [JsonProperty("input")] public JObject Input { get; set; }    // 入参模板，可含 {{param}} 占位
    }

    public sealed class Recipe
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("parameters")] public List<RecipeParam> Parameters { get; set; }
        [JsonProperty("steps")] public List<RecipeStep> Steps { get; set; }

        // ── 工具名净化 ──────────────────────────────────────────
        // LLM API 要求 function.name 匹配 ^[a-zA-Z0-9_-]+$。
        // 配方 Name 允许中文(更友好)，通过本方法生成 API 安全的工具名。
        // 规则:
        //   1. 已是纯 ASCII 安全名 → 原样返回
        //   2. 含非 ASCII 字符 → 尽量提取 ASCII 子串(中文间嵌入的英文/数字)
        //   3. 提取后仍为空 → 用稳定哈希生成 recipe_xxxxxxxx 兜底名
        // 内部使用，不持久化到 JSON (只存 Name 原文)。

        public static string ToApiSafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "recipe_unknown";

            // 已是纯 ASCII 安全名 → 不处理
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
                return name;

            // 提取所有 ASCII 安全字符
            var sb = new StringBuilder();
            foreach (char c in name)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('_');
            }
            var extracted = sb.ToString().Trim('_', '-');
            if (extracted.Length >= 2) return extracted;

            // 全部是非 ASCII 字符 → 稳定哈希兜底 (djb2, 跨 .NET 版本不变)
            uint hash = 5381;
            foreach (char c in name)
                hash = ((hash << 5) + hash) + c;
            return "recipe_" + hash.ToString("x8");
        }
    }
}
