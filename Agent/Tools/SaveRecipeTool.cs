// TxTools.Agent / Tools / SaveRecipeTool.cs
// 让 agent 把一段验证过、可复用的多步操作保存成新工具(配方)。
// 保存动作本身不改场景，故 IsReadOnly=true(免审批)；但配方一旦含变更步骤，
// 它"执行时"仍会按变更处理、需用户确认。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
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
                return "把一段验证过、可复用的多步操作保存为新工具(配方)，供之后直接调用。" +
                       "steps 里的 tool 必须是已存在的工具名；input 模板里可用 {{参数名}} 引用 parameters。" +
                       "仅在你已用现有工具跑通、且确实值得复用时才保存。";
            }
        }

        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""配方名(英文/下划线/连字符, 唯一)"" },
                        ""description"": { ""type"": ""string"", ""description"": ""配方用途说明"" },
                        ""parameters"": {
                            ""type"": ""array"",
                            ""description"": ""配方参数表"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""name"": { ""type"": ""string"" },
                                    ""description"": { ""type"": ""string"" },
                                    ""type"": { ""type"": ""string"", ""enum"": [""string"",""number"",""boolean""] },
                                    ""required"": { ""type"": ""boolean"" }
                                },
                                ""required"": [""name""]
                            }
                        },
                        ""steps"": {
                            ""type"": ""array"",
                            ""description"": ""按序执行的步骤"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""tool"": { ""type"": ""string"", ""description"": ""已存在的工具名"" },
                                    ""input"": { ""type"": ""object"", ""description"": ""该工具入参, 可含 {{参数名}}"" }
                                },
                                ""required"": [""tool""]
                            }
                        }
                    },
                    ""required"": [""name"", ""description"", ""steps""]
                }");
            }
        }

        public string Execute(JObject input)
        {
            Recipe recipe;
            try { recipe = input.ToObject<Recipe>(); }
            catch (Exception ex) { return "解析配方失败: " + ex.Message; }

            if (recipe == null || string.IsNullOrWhiteSpace(recipe.Name))
                return "配方缺少 name。";
            if (!IsValidName(recipe.Name))
                return "配方名只能含字母、数字、下划线、连字符。";
            if (recipe.Steps == null || recipe.Steps.Count == 0)
                return "配方至少要有一个步骤。";

            // 不允许覆盖非配方的内置工具(防止遮蔽原语)；同名配方可更新。
            ITxAgentTool existing;
            if (_registry.TryGet(recipe.Name, out existing) && !(existing is RecipeTool))
                return "名称 " + recipe.Name + " 已被内置工具占用，请换名。";

            // 校验每个步骤引用的工具都存在。
            var missing = new List<string>();
            foreach (var step in recipe.Steps)
            {
                ITxAgentTool t;
                if (string.IsNullOrEmpty(step.Tool) || !_registry.TryGet(step.Tool, out t))
                    missing.Add(step.Tool ?? "<空>");
            }
            if (missing.Count > 0)
                return "以下步骤工具不存在: " + string.Join(", ", missing.ToArray()) + "。请只引用已有工具。";

            // 持久化 + 即时注册(下一轮模型就能调用)。
            string path;
            try { path = RecipeStore.Upsert(recipe); }
            catch (Exception ex) { return "保存失败: " + ex.Message; }

            _registry.Register(new RecipeTool(recipe, _registry));

            return "已保存配方 \"" + recipe.Name + "\"(" + recipe.Steps.Count + " 步)到 " + path +
                   "，现在可直接调用。";
        }

        private static bool IsValidName(string s)
        {
            foreach (var c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            return s.Length > 0 && s.Length <= 64;
        }
    }
}
