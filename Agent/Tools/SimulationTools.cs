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
                       "action 可选 play(播放仿真)、reset(重置)、rewind(回退到起点)。" +
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
                        ""operation"": { ""type"": ""string"", ""description"": ""操作名(模糊匹配)"" },
                        ""action"": {
                            ""type"": ""string"",
                            ""enum"": [""play"", ""reset"", ""rewind""],
                            ""description"": ""动作：play 播放仿真、reset 重置、rewind 回退到起点""
                        }
                    },
                    ""required"": [""operation"", ""action""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var operation = GetString(input, "operation", null);
            var action = GetString(input, "action", "play");
            return PsBridge.SimulateOperation(operation, action);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) scan_devices_z — 扫描场景设备的 Z 向对齐情况（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 扫描场景中所有设备的 Z 向对齐情况：各设备当前 Z 坐标、最低点 Z、偏移量、是否需要对齐。
    /// 只读，不修改场景。是 align_devices_z 的只读前置检查。
    /// </summary>
    public sealed class ScanDevicesZTool : TxAgentToolBase
    {
        public override string Name { get { return "scan_devices_z"; } }

        public override string Description
        {
            get
            {
                return "扫描场景中所有设备的 Z 向对齐情况(只读版)：报告各设备的当前 Z 坐标、" +
                       "最低点 Z(MinZ)、偏移量(OffsetZ)、是否需要对齐(落地)。" +
                       "keywords 可指定要忽略的名称关键字(如“输送线”、“夹具”)。" +
                       "只读，不修改场景。用它先检查哪些设备需要落地，再决定是否用 align_devices_z 对齐。";
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
                        ""keywords"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""要忽略的设备名称关键字列表(如排除输送线、夹具等)""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var ignoreKeywords = new List<string>();
            var arr = input != null ? input["keywords"] as JArray : null;
            if (arr != null)
                foreach (var t in arr)
                    if (t != null && t.Type == JTokenType.String) ignoreKeywords.Add((string)t);
            return PsBridge.ScanDevicesZ(ignoreKeywords.ToArray());
        }
    }
}
