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

    /// <summary>复用可达性插件的完整分析。</summary>
    public sealed class CheckReachabilityTool : TxAgentToolBase
    {
        public override string Name { get { return "check_reachability"; } }
        public override string Description { get { return "使用可达性插件检查机器人操作：多工具 TCP、IK 兜底、关节限位、奇异和临界状态；可选 TCP XYZ 余量和静态干涉。operation 精确名称优先，同名用 object_id。省略目标时检查当前选择；检查期间会临时切换姿态/TCPF，结束后恢复。返回各状态总数及点位明细，干涉未检查或异常不视为无碰撞。"; } }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema { get { return JObject.Parse(@"{
            'type':'object', 'properties':{
                'operation':{'type':'string','description':'操作名，精确优先，模糊匹配必须唯一；省略则用当前选择'},
                'object_id':{'type':'string','description':'操作唯一 ID，优先于 operation'},
                'joint_margin_check':{'type':'boolean','default':true},
                'joint_margin_deg':{'type':'number','minimum':0,'default':10},
                'tcp_check':{'type':'boolean','default':false},
                'tcp_margin_mm':{'type':'number','minimum':0,'default':200},
                'interference':{'type':'boolean','default':false},
                'brand':{'type':'string','enum':['Auto','KUKA','ABB','FANUC','Other'],'default':'Auto'},
                'max_results':{'type':'integer','minimum':1,'maximum':1000,'default':100,'description':'每个操作的明细上限，优先返回异常点；汇总始终包含全部点'}
            }}"); } }
        public override string Execute(JObject input)
        {
            input = input ?? new JObject();
            try
            {
                TxTools.RobotReachabilityChecker.Models.RobotBrand brand;
                if (!System.Enum.TryParse(GetString(input, "brand", "Auto"), true, out brand)
                    || !System.Enum.IsDefined(typeof(TxTools.RobotReachabilityChecker.Models.RobotBrand), brand))
                    return "Error: brand 必须是 Auto/KUKA/ABB/FANUC/Other。";
                var limit = ToolInputHelpers.Number(input["max_results"], 100, "max_results", 1);
                if (limit > 1000 || limit != System.Math.Truncate(limit)) return "Error: max_results 必须是 1..1000 的整数。";
                var options = new TxTools.RobotReachabilityChecker.Services.CheckOptions
                {
                    ShowRecoveryDialog = false,
                    JointMarginCheckEnabled = ToolInputHelpers.Bool(input["joint_margin_check"], true),
                    JointMarginThreshDeg = ToolInputHelpers.Number(input["joint_margin_deg"], 10, "joint_margin_deg"),
                    TcpCheckEnabled = ToolInputHelpers.Bool(input["tcp_check"]),
                    TcpMarginMm = ToolInputHelpers.Number(input["tcp_margin_mm"], 200, "tcp_margin_mm"),
                    InterferenceEnabled = ToolInputHelpers.Bool(input["interference"]),
                    UserSelectedBrand = brand
                };
                return PsBridge.CheckReachability(GetString(input, "operation", null), GetString(input, "object_id", null), options, (int)limit);
            }
            catch (System.ArgumentException ex) { return "Error: " + ex.Message; }
        }
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