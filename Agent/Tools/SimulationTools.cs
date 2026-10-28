// TxTools.Agent / Tools / SimulationTools.cs
// 仿真与对齐扫描相关工具：播放仿真操作（变更）、扫描设备 Z 对齐情况（只读）。
// 所有 PS SDK 调用经 PsBridge -> PsContext.Current.Run(...) 路由回 PS 主线程。

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) simulate_operation — 播放/重置操作仿真（变更，需审批）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 在 PS 中播放/重置/回退一个操作的仿真。
    /// 用 TxSimulationPlayer 控制：play 启动仿真播放，reset 重置状态，rewind 回退到起点。
    /// 会触发 PS 仿真运行，执行前需用户确认。
    /// </summary>
    public sealed class SimulateOperationTool : TxAgentToolBase
    {
        public override string Name { get { return "simulate_operation"; } }

        public override string Description
        {
            get
            {
                return "播放/重置/回退一个操作的仿真。operation 为操作名；" +
                       "action 可选 play、pause、stop、reset(停止并回退)、rewind(回退到起点)。" +
                       "会触发 PS 仿真运行，执行前需用户确认。";
            }
        }

        // 关键：仿真播放属于变更操作，执行前需审批。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""object_id"": { ""type"": ""string"", ""description"": ""操作唯一 ID，优先于名称"" },
                        ""operation"": { ""type"": ""string"", ""description"": ""操作名(模糊匹配)"" },
                        ""action"": {
                            ""type"": ""string"",
                            ""enum"": [""play"", ""pause"", ""stop"", ""reset"", ""rewind""],
                            ""description"": ""动作：play 播放仿真、reset 重置、rewind 回退到起点""
                        }
                    },
                    ""required"": [""action""], ""anyOf"": [{""required"":[""operation""]},{""required"":[""object_id""]}]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var operation = GetString(input, "operation", null);
            var action = GetString(input, "action", "play");
            return PsBridge.SimulateOperation(operation, action, GetString(input, "object_id", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) scan_devices_z — 扫描场景设备的 Z 向对齐情况（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>三角网格最低点与底部支撑平面检测。</summary>
    public sealed class ScanDevicesZTool : TxAgentToolBase
    {
        public override string Name { get { return "scan_devices_z"; } }
        public override string Description { get { return "读取资源完整的世界坐标三角网格，返回真实最低点、底部支撑面倾角、世界 X/Y 方向高差、共面误差和支撑几何 ID。默认扫描场景资源，可用 scope=selected 或 object_ids 限定。扫描不修改资源位姿，临时显示和实体加载后恢复；没有几何时报告错误，不使用原点代替最低点。支撑不明或不共面会明确标记，校正前应检查这些结果。"; } }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema { get { return DeviceZToolArguments.Schema(false); } }
        public override string Execute(JObject input)
        {
            try{return PsBridge.ScanDevicesZ(DeviceZToolArguments.Parse(input,false));}
            catch(System.ArgumentException ex){return "Error: "+ex.Message;}
        }
    }
}
