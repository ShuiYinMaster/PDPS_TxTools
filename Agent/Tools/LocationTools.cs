// TxTools.Agent / Tools / LocationTools.cs
// 位置/坐标相关工具：查询世界坐标（只读）和设置对象位置（变更，需审批）。
// 所有 PS SDK 调用经 PsBridge -> PsContext.Current.Run(...) 路由回 PS 主线程。

using System;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) get_object_location — 查询对象世界坐标和位姿（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询一个对象的世界坐标系位姿：XYZ(mm) 和旋转角。
    /// format 可选 matrix(4x4矩阵)/euler/RPY(默认)。
    /// name 留空时用当前选中对象。只读。
    /// </summary>
    public sealed class GetObjectLocationTool : TxAgentToolBase
    {
        public override string Name { get { return "get_object_location"; } }

        public override string Description
        {
            get
            {
                return "查询一个对象的世界坐标系位姿(位置+旋转)。name 为对象名，留空用当前选中；" +
                       "format 可选 rpy(默认,XYZ+RPY旋转角)、euler(XYZ+Euler角)、matrix(4x4矩阵)。" +
                       "只读。回答某对象在哪、朝向如何时用它。";
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
                        ""name"": { ""type"": ""string"", ""description"": ""对象名，留空用当前选中对象"" },
                        ""format"": {
                            ""type"": ""string"",
                            ""enum"": [""rpy"", ""euler"", ""matrix""],
                            ""description"": ""输出格式，默认 rpy""
                        },
                        ""object_id"": {
                            ""type"": ""string"",
                            ""description"": ""对象的场景唯一 ID(形如 3,57,2,1)。场景内可能存在同名对象，工具报'命中多个'时用它精确指定；给了 object_id 就会忽略 name。""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var name = GetString(input, "name", null);
            var format = GetString(input, "format", "rpy");
            var objectId = GetString(input, "object_id", null);
            return PsBridge.GetObjectLocation(name, format, objectId);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) set_object_location — 设置对象位置（变更，需审批）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 设置一个对象的世界坐标系位置。包在 Undo 块中(可 Ctrl+Z 撤销)。
    /// 会改动场景，执行前需用户确认。
    /// </summary>
    public sealed class SetObjectLocationTool : TxAgentToolBase
    {
        public override string Name { get { return "set_object_location"; } }

        public override string Description
        {
            get
            {
                return "设置对象的世界坐标系位置。name 为对象名；x/y/z 为平移(mm)；" +
                       "rx/ry/rz 为可选的 RPY_ZYX 旋转角(度)，不提供则保留原旋转。" +
                       "会改动场景，执行前需用户确认，操作后可用 Ctrl+Z 撤销。";
            }
        }

        // 关键：标为非只读，循环会在执行前触发审批回调。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""对象名"" },
                        ""x"": { ""type"": ""number"", ""description"": ""X坐标(mm)"" },
                        ""y"": { ""type"": ""number"", ""description"": ""Y坐标(mm)"" },
                        ""z"": { ""type"": ""number"", ""description"": ""Z坐标(mm)"" },
                        ""rx"": { ""type"": ""number"", ""description"": ""RX旋转(度)，可选"" },
                        ""ry"": { ""type"": ""number"", ""description"": ""RY旋转(度)，可选"" },
                        ""rz"": { ""type"": ""number"", ""description"": ""RZ旋转(度)，可选"" },
                        ""object_id"": {
                            ""type"": ""string"",
                            ""description"": ""对象的场景唯一 ID(形如 3,57,2,1)。场景内可能存在同名对象，工具报'命中多个'时用它精确指定；给了 object_id 就会忽略 name。""
                        }
                    },
                    ""required"": [""x"", ""y"", ""z""],
                    ""anyOf"": [{""required"": [""name""]}, {""required"": [""object_id""]}]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            input = input ?? new JObject();
            var name = GetString(input, "name", null);
            var objectId = GetString(input, "object_id", null);
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(objectId))
                return "Error: 必须提供 name 或 object_id。";
            foreach (var axis in new[] { "x", "y", "z" })
                if (input[axis] == null || input[axis].Type == JTokenType.Null)
                    return "Error: 必须提供完整 x/y/z 坐标，缺少 " + axis + "。";
            try
            {
                double x = ToolInputHelpers.Number(input["x"], 0, "x", double.MinValue);
                double y = ToolInputHelpers.Number(input["y"], 0, "y", double.MinValue);
                double z = ToolInputHelpers.Number(input["z"], 0, "z", double.MinValue);
                double? rx = null, ry = null, rz = null;
                if (input["rx"] != null && input["rx"].Type != JTokenType.Null) rx = ToolInputHelpers.Number(input["rx"], 0, "rx", double.MinValue);
                if (input["ry"] != null && input["ry"].Type != JTokenType.Null) ry = ToolInputHelpers.Number(input["ry"], 0, "ry", double.MinValue);
                if (input["rz"] != null && input["rz"].Type != JTokenType.Null) rz = ToolInputHelpers.Number(input["rz"], 0, "rz", double.MinValue);
                return PsBridge.SetObjectLocation(name, x, y, z, rx, ry, rz, objectId);
            }
            catch (ArgumentException ex) { return "Error: " + ex.Message; }
        }
    }
}
