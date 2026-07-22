// TxTools.Agent / Tools / Ps / ImportCatiaTreeToPartsTool.cs
// 组合工具:
//   1) 从当前打开的 CATIA V5 活动 Product 文档读整棵 Product 树
//   2) 递归在 PS Parts 树 (默认 PhysicalRoot 下) 创建对应的 CompoundPart 空集合层级
//      TypeName = CATIA PartNumber, 便于后续按 PartNumber 匹配零件
//
// 用户场景 (对话 [146]):
//   "根据 CATIA 架构树在 parts 中创建对应的空集合"
//   —— 骨架先搭好, 具体零件后续按 PartNumber 匹配填入 (或者手动拖拽)。
//
// 变更工具, 需审批。审批时前端 modal 显示 CATIA 侧总节点数, 用户可以基于规模判断。

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core.Catia;

namespace TxTools.Agent.Core
{
    public sealed class ImportCatiaTreeToPartsTool : ITxAgentTool
    {
        public string Name { get { return "import_catia_tree_to_parts"; } }
        public string Description
        {
            get
            {
                return "\u4ece\u5f53\u524d\u6d3b\u52a8 CATIA V5 \u6587\u6863\u8bfb Product \u6811, " +
                       "\u9012\u5f52\u5728 PS Parts \u4e0b\u521b\u5efa\u5bf9\u5e94 CompoundPart \u7a7a\u96c6\u5408\u5c42\u7ea7\u3002" +
                       "\u53c2\u6570 parent_name (\u9ed8\u8ba4 PhysicalRoot), max_depth (\u9ed8\u8ba4 20), " +
                       "include_root (\u9ed8\u8ba4 true, false = \u53ea\u5efa\u6839\u4e0b\u5b50\u9879)\u3002" +
                       "TypeName = CATIA PartNumber\u3002";
            }
        }
        public bool IsReadOnly { get { return false; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'parent_name':  { 'type': 'string' },
                        'max_depth':    { 'type': 'integer', 'default': 20 },
                        'include_root': { 'type': 'boolean', 'default': true }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var parentName = ToolInputHelpers.String(input["parent_name"]);
            var maxDepth = ToolInputHelpers.Int(input["max_depth"], 20);
            var includeRoot = ToolInputHelpers.Bool(input["include_root"], true);

            // 1) 读 CATIA 树
            CatiaProductNode root;
            try { root = CatiaTreeReader.ReadActiveTree(maxDepth); }
            catch (Exception ex) { return "Error: \u8bfb CATIA \u6811\u5931\u8d25 - " + ex.Message; }

            var total = root.TotalDescendantCount() + 1;

            // 2) 定位 PS 侧父级
            ITxObject psParent;
            try { psParent = PsCompoundHelper.ResolveParent(parentName, typeof(Tecnomatix.Engineering.ITxCompoundPartCreation)); }
            catch (Exception ex) { return "Error: \u627e\u4e0d\u5230 PS \u7236\u5bf9\u8c61 - " + ex.Message; }

            // 3) 递归建 CompoundPart
            var log = new System.Text.StringBuilder();
            log.AppendLine("CATIA \u6811: \u6839=" + root.Name + " (\u603b " + total + " \u4e2a\u8282\u70b9)");
            log.AppendLine("PS \u7236\u7ea7: " + (psParent as ITxObject).Name);
            log.AppendLine();

            int created = 0, failed = 0;

            if (includeRoot)
            {
                // 先建根,再递归子
                var rootCp = TryCreate(psParent, root, log, ref created, ref failed);
                if (rootCp != null) CreateChildren(rootCp, root, log, ref created, ref failed);
            }
            else
            {
                // 直接把 CATIA 根的子项建到 psParent 下
                CreateChildren(psParent, root, log, ref created, ref failed);
            }

            log.AppendLine();
            log.AppendLine("\u5b8c\u6210: \u65b0\u5efa " + created + " \u4e2a CompoundPart" +
                           (failed > 0 ? "  \u5931\u8d25 " + failed + " \u4e2a" : ""));
            return log.ToString();
        }

        // ── 递归 ──

        private static void CreateChildren(ITxObject psParent, CatiaProductNode catiaParent,
            System.Text.StringBuilder log, ref int created, ref int failed)
        {
            foreach (var child in catiaParent.Children)
            {
                var cp = TryCreate(psParent, child, log, ref created, ref failed);
                if (cp != null && child.Children.Count > 0)
                    CreateChildren(cp, child, log, ref created, ref failed);
            }
        }

        private static TxCompoundPart TryCreate(ITxObject psParent, CatiaProductNode node,
            System.Text.StringBuilder log, ref int created, ref int failed)
        {
            // TypeName 优先用 PartNumber (业务标识),兜底用 Name
            var typeName = !string.IsNullOrWhiteSpace(node.PartNumber) ? node.PartNumber : node.Name;
            var desiredName = !string.IsNullOrWhiteSpace(node.Name) ? node.Name : node.PartNumber;

            try
            {
                var cp = PsCompoundHelper.CreatePart(psParent, typeName, desiredName);
                created++;
                // 只打印前若干条,防止 log 爆
                if (created <= 30)
                    log.AppendLine("  + " + (cp as ITxObject).Name + "  [" + typeName + "]");
                else if (created == 31)
                    log.AppendLine("  ... (\u4ee5\u4e0b\u7701\u7565)");
                return cp;
            }
            catch (Exception ex)
            {
                failed++;
                log.AppendLine("  ! " + (node.Name ?? "?") + " \u5931\u8d25: " + ex.Message);
                return null;
            }
        }
    }
}
