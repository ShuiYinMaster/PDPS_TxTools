// TxTools.Agent / Tools / ListRecipesTool.cs
// 只读：列出已保存的配方，便于 agent 复用而非重复创建。

using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class ListRecipesTool : TxAgentToolBase
    {
        public override string Name { get { return "list_recipes"; } }

        public override string Description
        {
            get { return "列出所有已保存的配方(可复用的多步工具)及其用途和参数。新建配方前先查这里有没有现成的。"; }
        }

        public override bool IsReadOnly { get { return true; } }

        public override string Execute(JObject input)
        {
            var recipes = RecipeStore.Load();
            if (recipes.Count == 0) return "目前没有已保存的配方。";

            var sb = new StringBuilder();
            sb.AppendLine("已保存配方 " + recipes.Count + " 条：");
            foreach (var r in recipes)
            {
                sb.Append("• ").Append(r.Name).Append(" — ").Append(r.Description ?? "");
                if (r.Parameters != null && r.Parameters.Count > 0)
                {
                    sb.Append(" [参数: ");
                    for (int i = 0; i < r.Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(r.Parameters[i].Name);
                    }
                    sb.Append("]");
                }
                int steps = r.Steps != null ? r.Steps.Count : 0;
                sb.Append(" (").Append(steps).Append(" 步)");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
