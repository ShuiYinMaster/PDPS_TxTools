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
                       "name 必须为简短中文标题，description 用一句中文说明用途，省略 API、实现原理和代码说明。" +
                       "提供合理默认值；颜色用 color 类型和 #RRGGBB 默认值；对象查询可用 objectFilter；有限选项用 choices；同一功能的常用模式用 actions 生成多个中文执行按钮。" +
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
                        ["name"] = new JObject { ["type"] = "string", ["description"] = "简短中文标题，建议 6–16 字，如“批量显示或隐藏对象”；不要用英文变量名作标题" },
                        ["description"] = new JObject { ["type"] = "string", ["description"] = "一句简短中文说明用途和效果，建议不超过 50 字；只保留必要限制，不写技术实现" },
                        ["id"] = new JObject { ["type"] = "string", ["description"] = "更新已有配方时填写其 id，以保留文件身份和运行记录" },
                        ["source_snippet"] = new JObject { ["type"] = "string", ["description"] = "来源片段名；从片段固化时必填" },
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
                                    ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("object", "objects", "number", "text", "bool", "color") },
                                    ["typeHint"] = new JObject { ["type"] = "string", ["description"] = "期望的 PS 类型如 TxRobot, 仅 object 类参数用" },
                                    ["objectFilter"] = new JObject { ["type"] = "boolean", ["description"] = "对象参数启用类型下拉框和名称筛选" },
                                    ["required"] = new JObject { ["type"] = "boolean" },
                                    ["default"] = new JObject { ["type"] = "string", ["description"] = "默认值(文本)" },
                                    ["help"] = new JObject { ["type"] = "string" },
                                    ["choices"] = new JObject
                                    {
                                        ["type"] = "array", ["description"] = "文本或数字的有限选项，显示为下拉选择；default 必须是其中的 value",
                                        ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject
                                        {
                                            ["label"] = new JObject { ["type"] = "string" },
                                            ["value"] = new JObject { ["type"] = "string" }
                                        }, ["required"] = new JArray("label", "value") }
                                    }
                                },
                                ["required"] = new JArray("name")
                            }
                        },
                        ["actions"] = new JObject
                        {
                            ["type"] = "array", ["description"] = "可选的多个执行按钮，共用代码和对象绑定，例如显示/隐藏。args 覆盖标量参数，不能存对象 ID",
                            ["items"] = new JObject { ["type"] = "object", ["properties"] = new JObject
                            {
                                ["id"] = new JObject { ["type"] = "string", ["description"] = "唯一英文标识，如 show/hide" },
                                ["label"] = new JObject { ["type"] = "string", ["description"] = "简短中文按钮文字" },
                                ["args"] = new JObject { ["type"] = "object", ["additionalProperties"] = new JObject { ["type"] = "string" } }
                            }, ["required"] = new JArray("id", "label", "args") }
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

            name = name.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"[\u4e00-\u9fff]"))
                return "Error: 配方标题须使用中文，请用简短中文用途命名。";
            var safeName = Recipe.ToApiSafeName(name);

            var recipe = new Recipe
            {
                Name = name,
                Description = input["description"] != null ? (string)input["description"] : "",
                Lang = SnippetStore.NormalizeLang(input["lang"] != null ? (string)input["lang"] : "csharp"),
                Code = code,
                Params = ParseParams(input["params"]),
                Actions = input["actions"] == null ? new List<RecipeAction>() : input["actions"].ToObject<List<RecipeAction>>(),
                SourceSnippet = (string)input["source_snippet"]
            };
            var previous = input["id"] == null
                ? RecipeStore.All().FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
                : RecipeStore.Get((string)input["id"]);
            if (input["id"] != null && previous == null)
                return "Error: 要更新的配方不存在，请先 list_recipes。";
            if (previous != null)
            {
                recipe.Id = previous.Id; recipe.CreatedUtc = previous.CreatedUtc;
                recipe.RunCount = previous.RunCount; recipe.FailCount = previous.FailCount; recipe.LastRunUtc = previous.LastRunUtc;
                if (recipe.SourceSnippet == null) recipe.SourceSnippet = previous.SourceSnippet;
            }

            // 校验参数合法性(参数名会被写进生成的代码)
            var bad = RecipeStore.ValidateDefinition(recipe);
            if (bad != null) return "Error: " + bad;

            // 不允许覆盖非配方的内置工具(防止遮蔽原语)；同名配方可更新。
            ITxAgentTool existing;
            if (_registry.TryGet(safeName, out existing) && !(existing is RecipeTool))
                return "名称 " + recipe.Name + " 已被内置工具占用，请换名。";
            if (RecipeStore.All().Any(r => r.Id != recipe.Id && Recipe.ToApiSafeName(r.Name) == safeName))
                return "Error: 此标题的工具名与已有配方冲突，请换一个中文标题。";

            var msg = RecipeStore.Upsert(recipe);
            if (!msg.StartsWith("已保存", StringComparison.Ordinal)) return "Error: " + msg;

            if (previous != null && Recipe.ToApiSafeName(previous.Name) != safeName)
                _registry.Remove(Recipe.ToApiSafeName(previous.Name));
            _registry.Register(new RecipeTool(recipe, _registry));

            return msg + "，现在可在配方栏执行。";
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
                    ObjectFilter = (bool?)jo["objectFilter"] == true,
                    Required = jo["required"] == null || (bool)jo["required"],
                    Default = (string)jo["default"],
                    Help = (string)jo["help"],
                    Choices = jo["choices"] == null ? null : jo["choices"].ToObject<List<RecipeChoice>>()
                };
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                list.Add(p);
            }
            return list;
        }
    }
}
