// TxTools.Agent / Tools / ListRecipesTool.cs
// 只读：列出已保存的配方(代码 + 参数声明)，便于 agent 复用而非重复创建。

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
            get { return "列出所有已保存的配方(可复用代码)及其语言、参数和成功率。新建配方前先查这里有没有现成的。"; }
        }

        public override bool IsReadOnly { get { return true; } }

        public override string Execute(JObject input)
        {
            var recipes = RecipeStore.All();
            if (recipes.Count == 0) return "目前没有已保存的配方。";

            var sb = new StringBuilder();
            sb.AppendLine("已保存配方 " + recipes.Count + " 条：");
            foreach (var r in recipes)
            {
                // 工具名(API function.name)必须是 ^[a-zA-Z0-9_-]+$,
                // 显示 API 安全名;若与原文不同则附上原文。
                var apiName = Recipe.ToApiSafeName(r.Name);
                var display = apiName;
                if (!string.Equals(apiName, r.Name, System.StringComparison.Ordinal))
                    display = apiName + " (显示名: " + r.Name + ")";

                sb.Append("• ").Append(display)
                  .Append(" <").Append(SnippetStore.NormalizeLang(r.Lang)).Append("> — ")
                  .Append(r.Description ?? "");
                if (r.Params != null && r.Params.Count > 0)
                {
                    sb.Append(" [参数: ");
                    for (int i = 0; i < r.Params.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var p = r.Params[i];
                        sb.Append(p.Name);
                        if (!string.IsNullOrWhiteSpace(p.Label)
                            && !string.Equals(p.Label, p.Name, System.StringComparison.Ordinal))
                            sb.Append("(").Append(p.Label).Append(")");
                    }
                    sb.Append("]");
                }
                if (r.RunCount + r.FailCount > 0)
                    sb.Append(" 跑过 ").Append(r.RunCount + r.FailCount)
                      .Append(" 次, 成功 ").Append(r.RunCount)
                      .Append(", 失败 ").Append(r.FailCount);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
