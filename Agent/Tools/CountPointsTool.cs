// TxTools.Agent / Tools / CountPointsTool.cs
// 只读：统计当前选中操作里指定类型的点数 (按 op 分列 + 合计)。
// 带参数的原语，适合做配方里的"先点检"步骤。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class CountPointsTool : TxAgentToolBase
    {
        public override string Name { get { return "count_points"; } }

        public override string Description
        {
            get
            {
                return "统计当前选中操作里的点数。point_type 可选 WeldPoint/PathPoint/ContinuousPoint/All; " +
                       "use_mfg_name 控制是否按制造特征名读取。导出前核对点数量时用。";
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
                        ""point_type"": {
                            ""type"": ""string"",
                            ""enum"": [""WeldPoint"", ""PathPoint"", ""ContinuousPoint"", ""All""],
                            ""description"": ""点类型过滤，默认 All""
                        },
                        ""use_mfg_name"": {
                            ""type"": ""boolean"",
                            ""description"": ""是否按制造特征名读取，默认 false""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var pointType = GetString(input, "point_type", "All");
            bool useMfg = false;
            var t = input != null ? input["use_mfg_name"] : null;
            if (t != null && t.Type == JTokenType.Boolean) useMfg = (bool)t;
            return PsBridge.CountPoints(pointType, useMfg);
        }
    }
}
