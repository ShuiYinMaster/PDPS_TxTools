// TxTools.Agent / Tools / ToolGroupTools.cs
//
// 工具组开关入口。对应 Core/ToolGate —— 工具按组暴露,避免 40+ 个工具
// 的 schema 挤占模型注意力。默认 code/cee 两组关闭。
//
// 注意:改开关【下次新建对话才生效】。会话中途改会击穿 prompt 前缀缓存,
// 而且模型已经基于旧工具集做过规划。所以这里只管持久化 + 提示,
// 不实时改 harness registry。

using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    /// <summary>列出所有工具组及启用状态。只读。</summary>
    public sealed class ListToolGroupsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_tool_groups"; } }

        public override string Description
        {
            get
            {
                return "列出所有工具组及其启用状态(每组包含哪些工具)。"
                     + "工具按组暴露给模型,未启用组的工具当前不可用。"
                     + "需要知道某能力(如改源码/虚拟调试)为什么不可用时先调它。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override string Execute(JObject input)
        {
            var enabled = ToolGate.EnabledGroups();
            var sb = new StringBuilder();
            sb.AppendLine("工具组(未列出的工具为核心工具,始终可用):");
            foreach (var g in ToolGate.AllGroups())
            {
                bool on = enabled.Contains(g);
                sb.Append("  ").Append(on ? "[✓]" : "[✗]").Append(' ').Append(g);
                sb.AppendLine("  (启用后加 " + Count(g) + " 个工具)");
            }
            sb.AppendLine();
            sb.Append("当前启用: " + (enabled.Count == 0 ? "(无)" : string.Join(", ", enabled)));
            sb.AppendLine();
            sb.Append("用 set_tool_groups 调整,【新建对话后生效】。");
            return sb.ToString();
        }

        private static int Count(string group)
        {
            // 简化:组内工具数无法从 ToolGate 公开 API 拿到,直接用工具名集合统计太绕,
            // 这里返回模糊描述即可 —— 详细清单在 ToolGate 的组定义里。
            var map = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "code", 8 }, { "cee", 10 }, { "doc", 5 }, { "view", 5 }, { "catia", 2 }, { "knowledge", 4 }
            };
            int n;
            return map.TryGetValue(group, out n) ? n : 0;
        }
    }

    /// <summary>开/关工具组并持久化。变更,走审批;新建对话后生效。</summary>
    public sealed class SetToolGroupsTool : TxAgentToolBase
    {
        public override string Name { get { return "set_tool_groups"; } }

        public override string Description
        {
            get
            {
                return "开关工具组(如 code=改源码、cee=虚拟调试)。"
                     + "enable:要启用的组名数组;disable:要关闭的组名数组。"
                     + "不传则只列出当前状态。"
                     + "【改动下次新建对话才生效】—— 当前会话的工具集不会变。"
                     + "没列在组里的核心工具不受影响。";
            }
        }

        /// <summary>改变 agent 能力范围,走审批让用户确认。</summary>
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(
                    "{ \"type\":\"object\", \"properties\": {" +
                    " \"enable\": { \"type\":\"array\", \"items\":{ \"type\":\"string\" }, \"description\":\"要启用的组名,如 code\" }," +
                    " \"disable\": { \"type\":\"array\", \"items\":{ \"type\":\"string\" }, \"description\":\"要关闭的组名,如 cee\" }" +
                    "} }");
            }
        }

        public override string Execute(JObject input)
        {
            var enabled = ToolGate.EnabledGroups();
            var changed = new System.Collections.Generic.List<string>();

            var enArr = input != null ? input["enable"] as JArray : null;
            if (enArr != null)
                foreach (var t in enArr)
                {
                    var g = (string)t;
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    if (ToolGate.SetEnabled(g.Trim(), true)) changed.Add("启用 " + g.Trim());
                }

            var disArr = input != null ? input["disable"] as JArray : null;
            if (disArr != null)
                foreach (var t in disArr)
                {
                    var g = (string)t;
                    if (string.IsNullOrWhiteSpace(g)) continue;
                    if (ToolGate.SetEnabled(g.Trim(), false)) changed.Add("关闭 " + g.Trim());
                }

            // 持久化,下次启动恢复
            UserPrefsStore.UpdateToolGroups(ToolGate.SnapshotEnabled());

            var sb = new StringBuilder();
            if (changed.Count > 0)
                sb.AppendLine("已" + string.Join("、", changed) + "。");
            else
                sb.AppendLine("(未变更)");

            sb.Append("当前启用: " + (enabled.Count == 0 ? "(无)" : string.Join(", ", ToolGate.EnabledGroups())));
            sb.AppendLine();
            sb.Append("【新建对话后才生效】。未启用的能力见系统提示词底部的工具组说明。");
            return sb.ToString();
        }
    }
}
