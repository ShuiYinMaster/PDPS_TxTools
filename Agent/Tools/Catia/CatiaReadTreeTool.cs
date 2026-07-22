// TxTools.Agent / Tools / Catia / CatiaReadTreeTool.cs
// 从当前打开的 CATIA V5 活动文档读 Product 树。
// 只读工具,不改 CATIA 也不改 PS。
//
// Agent 拿到树后能干什么:
//   - 用 count_objects 统计 PS 里已有的零件数,对比 CATIA 应有的数
//   - 用 run_csharp 在 PS 里按 CATIA 树的 PartNumber 创建/校对 TxComponent
//   - 用 export_docx 生成"CATIA 树 vs PDPS 树差异报告"
//
// 后续可加: catia_import_to_ps (把 CATIA 节点一比一建到 PDPS,变更工具,需审批)

using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core.Catia;

namespace TxTools.Agent.Core
{
    public sealed class CatiaReadTreeTool : ITxAgentTool
    {
        public string Name { get { return "catia_read_tree"; } }
        public string Description
        {
            get
            {
                return "\u4ece\u5f53\u524d\u6d3b\u52a8\u7684 CATIA V5 \u6587\u6863\u8bfb Product \u6811\u3002" +
                       "\u53ea\u8bfb\u3002\u9700\u8981 CATIA V5 \u5df2\u542f\u52a8\u4e14\u6253\u5f00\u4e00\u4e2a Product \u6587\u6863\u3002" +
                       "\u53c2\u6570 max_depth (\u9ed8\u8ba4 10),format (tree|json,\u9ed8\u8ba4 tree)\u3002";
            }
        }
        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'max_depth': { 'type': 'integer', 'default': 10 },
                        'format':    { 'type': 'string', 'enum': ['tree','json'], 'default': 'tree' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var maxDepth = ToolInputHelpers.Int(input["max_depth"], 10);
            var format = ToolInputHelpers.String(input["format"], "tree");

            CatiaProductNode root;
            try
            {
                root = CatiaTreeReader.ReadActiveTree(maxDepth);
            }
            catch (System.Exception ex)
            {
                return "Error: " + ex.Message;
            }

            var total = root.TotalDescendantCount();
            var head = "CATIA Product: " + (root.Name ?? "?") +
                       "  (PartNumber=" + (root.PartNumber ?? "-") +
                       ", \u5b50\u8282\u70b9 " + total + " \u4e2a)\n\n";

            if (format == "json")
                return head + Newtonsoft.Json.JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);

            // tree 视图
            var sb = new StringBuilder();
            sb.Append(head);
            RenderTree(sb, root, "", true);
            return sb.ToString();
        }

        private static void RenderTree(StringBuilder sb, CatiaProductNode node, string prefix, bool isLast)
        {
            var branch = isLast ? "\u2514\u2500 " : "\u251c\u2500 ";
            var line = prefix + branch + (node.Name ?? "?");
            if (!string.IsNullOrEmpty(node.PartNumber) && node.PartNumber != node.Name)
                line += "  [" + node.PartNumber + "]";
            if (node.IsAssembly) line += "  (asm)";
            sb.AppendLine(line);

            var childPrefix = prefix + (isLast ? "   " : "\u2502  ");
            for (int i = 0; i < node.Children.Count; i++)
                RenderTree(sb, node.Children[i], childPrefix, i == node.Children.Count - 1);
        }
    }
}
