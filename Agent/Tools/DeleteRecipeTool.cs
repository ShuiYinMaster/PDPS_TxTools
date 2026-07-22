// TxTools.Agent / Tools / DeleteRecipeTool.cs
// 删除一条已保存的配方(注册表 + 磁盘)。内置原语不可删。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class DeleteRecipeTool : ITxAgentTool
    {
        private readonly ToolRegistry _registry;

        public DeleteRecipeTool(ToolRegistry registry) { _registry = registry; }

        public string Name { get { return "delete_recipe"; } }

        public string Description
        {
            get { return "删除一条已保存的配方(按名)。只能删配方，内置工具不可删。"; }
        }

        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""要删除的配方名"" }
                    },
                    ""required"": [""name""]
                }");
            }
        }

        public string Execute(JObject input)
        {
            var name = input != null && input["name"] != null ? (string)input["name"] : null;
            if (string.IsNullOrWhiteSpace(name)) return "请提供要删除的配方 name。";

            ITxAgentTool t;
            if (!_registry.TryGet(name, out t) || !(t is RecipeTool))
                return "没有名为 " + name + " 的配方(内置工具不可删)。";

            bool removed = RecipeStore.Remove(name);
            _registry.Remove(name);
            return removed ? "已删除配方 " + name + "。"
                           : "已从注册表移除 " + name + "（磁盘中未找到记录）。";
        }
    }
}
