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
                       "rx/ry/rz 为可选的旋转角(度)，不提供则保留原旋转。" +
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
                    ""required"": [""name"", ""x"", ""y"", ""z""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var name = GetString(input, "name", null);
            if (string.IsNullOrWhiteSpace(name))
                return "必须提供 name 参数（对象名）。";

            double x = 0, y = 0, z = 0;

            // 解析 XYZ — 允许 int 或 float
            var txTok = input != null ? input["x"] : null;
            if (txTok != null) x = ToDouble(txTok);
            var tyTok = input != null ? input["y"] : null;
            if (tyTok != null) y = ToDouble(tyTok);
            var tzTok = input != null ? input["z"] : null;
            if (tzTok != null) z = ToDouble(tzTok);

            // 解析可选旋转（null 表示保留原值）
            double? rx = null, ry = null, rz = null;
            var rxTok = input != null ? input["rx"] : null;
            if (rxTok != null && rxTok.Type != JTokenType.Null) rx = ToDouble(rxTok);
            var ryTok = input != null ? input["ry"] : null;
            if (ryTok != null && ryTok.Type != JTokenType.Null) ry = ToDouble(ryTok);
            var rzTok = input != null ? input["rz"] : null;
            if (rzTok != null && rzTok.Type != JTokenType.Null) rz = ToDouble(rzTok);

            var objectId = GetString(input, "object_id", null);

            return PsBridge.SetObjectLocation(name, x, y, z, rx, ry, rz, objectId);
        }

        private static double ToDouble(JToken tok)
        {
            if (tok.Type == JTokenType.Float) return (double)tok;
            if (tok.Type == JTokenType.Integer) return (double)(int)tok;
            return Convert.ToDouble(tok);
        }
    }
}
