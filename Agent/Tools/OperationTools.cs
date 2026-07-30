// TxTools.Agent / Tools / OperationTools.cs
// 一组基于 PsReader 的只读原子工具。粒度细 -> 配方能组合出的解法越多。
// 都作用于"当前选择"，返回文本摘要；PS 调用经 PsBridge -> PsContext 路由回主线程。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    /// <summary>列出当前选中的操作：名称 / 类型 / 绑定工具名。</summary>
    public sealed class ListOperationsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_operations"; } }
        public override string Description
        {
            get { return "列出当前选中的操作 (焊接/路径等)，含名称、类型标签和绑定的工具名。了解选了哪些操作时用。"; }
        }
        public override bool IsReadOnly { get { return true; } }
        public override string Execute(JObject input) { return PsBridge.ListOperations(); }
    }

    /// <summary>列出当前选中第一个操作可用的 TCP 选项。</summary>
    public sealed class ListTcpOptionsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_tcp_options"; } }
        public override string Description
        {
            get { return "列出当前选中(第一个)操作可用的 TCP 选项 (默认 TCP + 各点工具坐标 + 工具子坐标系)。"; }
        }
        public override bool IsReadOnly { get { return true; } }
        public override string Execute(JObject input) { return PsBridge.ListTcpOptions(); }
    }

    /// <summary>对选中操作做快速可达性摘要(可达/不可达计数)。</summary>
    public sealed class CheckReachabilityTool : TxAgentToolBase
    {
        public override string Name { get { return "check_reachability"; } }

        public override string Description
        {
            get
            {
                return "对机器人操作做快速可达性检查：给 operation(操作名，如 OP120)即可——会在操作树里按名找到该操作、" +
                       "枚举其下机器人点位，逐点用 GetPoseAtLocation 判定可达，给出可达/不可达计数。" +
                       "不必先选中；operation 省略时则检查当前选中的操作。只读、不驱动机器人；不含余量/奇异/碰撞分析。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""operation"": { ""type"": ""string"", ""description"": ""操作名(模糊匹配, 如 OP120)。省略则用当前选中的操作。"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input) { return PsBridge.CheckReachability(GetString(input, "operation", null)); }
    }

    /// <summary>在操作树(OperationRoot)里按名查找操作。</summary>
    public sealed class FindOperationsTool : TxAgentToolBase
    {
        public override string Name { get { return "find_operations"; } }

        public override string Description
        {
            get
            {
                return "在操作树(OperationRoot)里按关键字查找操作，返回名称与类型。" +
                       "操作(机器人操作/焊接操作等)住在操作树里，不在物理场景树——找操作用它，别用 select_objects/count_objects。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""keyword"": { ""type"": ""string"", ""description"": ""操作名关键字(模糊)，留空列全部"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input) { return PsBridge.FindOperations(GetString(input, "keyword", null)); }
    }
}