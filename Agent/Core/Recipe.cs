// TxTools.Agent / Core / Recipe.cs
// 配方 = 一串对"现有工具"的调用 + 参数模板。是数据，不是代码。
// 能力完全被已注册的原子工具集合框死，不引入超出现有工具的破坏面。
// 局限（有意为之）：只能做"参数化的固定序列"，不支持分支/循环/条件——
// 那类逻辑由 agent 在对话里直接调用工具处理，配方只固化稳定可复用的步骤。

using System.Collections.Generic;
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
    }
}
