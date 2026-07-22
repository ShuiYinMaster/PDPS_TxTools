// TxTools.Agent / Tools / SceneQueryTool.cs
// 只读示例工具：查询当前 PS 场景状态 (选中对象 / 当前文档)。
// 演示"只读 -> 免审批直跑"的模式，是模型行动前了解场景的基础工具。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class SceneQueryTool : TxAgentToolBase
    {
        public override string Name { get { return "query_scene"; } }

        public override string Description
        {
            get
            {
                return "查询当前 Process Simulate 场景的状态。" +
                       "scope=\"selection\" 返回当前选中的对象列表；" +
                       "scope=\"document\" 返回当前文档信息。行动前先用它了解现状。";
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
                        ""scope"": {
                            ""type"": ""string"",
                            ""enum"": [""selection"", ""document""],
                            ""description"": ""查询范围：selection=选中对象, document=当前文档""
                        }
                    },
                    ""required"": [""scope""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var scope = GetString(input, "scope", "selection");
            switch (scope)
            {
                case "document":
                    return PsBridge.GetActiveDocumentSummary();
                case "selection":
                default:
                    return PsBridge.GetSelectedObjectsSummary();
            }
        }
    }
}
