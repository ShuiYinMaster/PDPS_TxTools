// TxTools.Agent / Tools / SaveRecipeTool.cs
// 让 agent 把一段验证过、可复用的代码保存成配方(代码 + 参数声明)。
// 保存动作本身不改场景，故 IsReadOnly=true(免审批)；但配方"执行时"会走审批(run_csharp 同级)。
//
// 【和侧边栏同源】侧边栏"promote"功能把片段固化成配方走的是同一条 Upsert 路径；
// 本工具是模型侧的入口，params 字段与 RecipeParam 一一对应。

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class SaveRecipeTool : ITxAgentTool
    {
        private readonly ToolRegistry _registry;

        public SaveRecipeTool(ToolRegistry registry) { _registry = registry; }

        public string Name { get { return "save_recipe"; } }

        public string Description
        {
            get
            {
                return "把一段验证过、可复用的代码保存成配方(代码 + 参数声明)，供之后直接调用。" +
                       "code 是完整可执行的 C# 方法体或 Python 代码；lang 填 csharp 或 python。" +
                       "params 声明代码里哪些地方是可变的：对象类参数(object/objects)传 ITxObject.Id，number/text/bool 传字面量。" +
                       "仅在你已跑通、且确实值得复用时才保存。";
            }
        }

        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["name"] = new JObject { ["type"] = "string", ["description"] = "配方名(英文/下划线/连字符, 唯一)" },
                        ["description"] = new JObject { ["type"] = "string", ["description"] = "配方用途说明" },
                        ["lang"] = new JObject { ["type"] = "string", ["enum"] = new JArray("csharp", "python"), ["description"] = "代码语言, 默认 csharp" },
                        ["code"] = new JObject { ["type"] = "string", ["description"] = "完整可执行代码(C# 方法体或 Python 顶层语句)" },
                        ["params"] = new JObject
                        {
                            ["type"] = "array",
                            ["description"] = "参数声明: 每个参数会生成一段前置变量声明, 对象类参数在界面上绑对象",
                            ["items"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["name"] = new JObject { ["type"] = "string", ["description"] = "代码里用的变量名(合法标识符)" },
                                    ["label"] = new JObject { ["type"] = "string", ["description"] = "界面上显示的名字" },
                                    ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("object", "objects", "number", "text", "bool") },
                                    ["typeHint"] = new JObject { ["type"] = "string", ["description"] = "期望的 PS 类型如 TxRobot, 仅 object 类参数用" },
                                    ["required"] = new JObject { ["type"] = "boolean" },
                                    ["default"] = new JObject { ["type"] = "string", ["description"] = "默认值(文本)" },
                                    ["help"] = new JObject { ["type"] = "string" }
                                },
                                ["required"] = new JArray("name")
                            }
                        }
                    },
                    ["required"] = new JArray("name", "code")
                };
            }
        }

        public string Execute(JObject input)
        {
            var name = input != null ? (string)input["name"] : null;
            var code = input != null ? (string)input["code"] : null;
            if (string.IsNullOrWhiteSpace(name)) return "配方缺少 name。";
            if (string.IsNullOrWhiteSpace(code)) return "配方缺少 code。";

            // 自动净化 Name: LLM 可能会给中文名(不满足 API function.name ^[a-zA-Z0-9_-]+$)
            var originalName = name;
            var safeName = Recipe.ToApiSafeName(name);
            if (!string.Equals(safeName, originalName, StringComparison.Ordinal))
                name = safeName;   // 持久化用安全名，避免下次启动再次净化

            var recipe = new Recipe
            {
                Name = name,
                Description = input["description"] != null ? (string)input["description"] : "",
                Lang = SnippetStore.NormalizeLang(input["lang"] != null ? (string)input["lang"] : "csharp"),
                Code = code,
                Params = ParseParams(input["params"])
            };

            // 校验参数合法性(参数名会被写进生成的代码)
            var bad = RecipeStore.ValidateParams(recipe.Params);
            if (bad != null) return "Error: " + bad;

            // 不允许覆盖非配方的内置工具(防止遮蔽原语)；同名配方可更新。
            ITxAgentTool existing;
            if (_registry.TryGet(recipe.Name, out existing) && !(existing is RecipeTool))
                return "名称 " + recipe.Name + " 已被内置工具占用，请换名。";

            var msg = RecipeStore.Upsert(recipe);
            if (!msg.StartsWith("已保存", StringComparison.Ordinal)) return "Error: " + msg;

            _registry.Register(new RecipeTool(recipe, _registry));

            var nameNote = string.Equals(safeName, originalName, StringComparison.Ordinal)
                ? "" : " (原始名 \"" + originalName + "\" 已净化)";
            return msg + nameNote
                   + "，参数 " + recipe.Params.Count + " 个，现在可直接调用。";
        }

        /// <summary>把 params 数组解析成 RecipeParam 列表。宽容处理缺失字段。</summary>
        private static List<RecipeParam> ParseParams(JToken jparams)
        {
            var list = new List<RecipeParam>();
            if (jparams == null || jparams.Type != JTokenType.Array) return list;

            foreach (var jp in (JArray)jparams)
            {
                if (jp.Type != JTokenType.Object) continue;
                var jo = (JObject)jp;
                var p = new RecipeParam
                {
                    Name = (string)jo["name"],
                    Label = (string)jo["label"],
                    Kind = jo["kind"] != null ? (string)jo["kind"] : "object",
                    TypeHint = (string)jo["typeHint"],
                    Required = jo["required"] != null && (bool)jo["required"],
                    Default = (string)jo["default"],
                    Help = (string)jo["help"]
                };
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                list.Add(p);
            }
            return list;
        }
    }
}
