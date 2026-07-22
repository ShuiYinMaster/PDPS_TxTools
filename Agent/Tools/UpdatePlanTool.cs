// TxTools.Agent / Tools / UpdatePlanTool.cs
// 让 agent 记录/更新任务计划。复杂多步任务时先列计划，再随进度勾选。

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class UpdatePlanTool : TxAgentToolBase
    {
        public override string Name { get { return "update_plan"; } }

        public override string Description
        {
            get
            {
                return "为复杂的多步任务记录/更新一个待办清单(整表替换)。每项含 text 和 done。" +
                       "建议：开始多步任务前先列计划，每完成一步就把对应项 done 置 true 再继续。";
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
                        ""items"": {
                            ""type"": ""array"",
                            ""description"": ""完整的任务项列表(整表替换)"",
                            ""items"": {
                                ""type"": ""object"",
                                ""properties"": {
                                    ""text"": { ""type"": ""string"" },
                                    ""done"": { ""type"": ""boolean"" }
                                },
                                ""required"": [""text""]
                            }
                        }
                    },
                    ""required"": [""items""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var list = new List<TaskItem>();
            var arr = input != null ? input["items"] as JArray : null;
            if (arr != null)
                foreach (var t in arr)
                {
                    var o = t as JObject;
                    if (o == null) continue;
                    var text = o["text"] != null ? (string)o["text"] : null;
                    bool done = o["done"] != null && o["done"].Type == JTokenType.Boolean && (bool)o["done"];
                    if (!string.IsNullOrWhiteSpace(text))
                        list.Add(new TaskItem { Text = text, Done = done });
                }
            return TaskPlan.Update(list);
        }
    }
}
