// TxTools.Agent / Tools / ApiTools.cs
// 让 AI 从内部读懂 PS SDK 的真实 API（反射），为 run_csharp 写代码打底。都是只读。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class ListTypesTool : TxAgentToolBase
    {
        public override string Name { get { return "list_types"; } }
        public override string Description
        {
            get { return "在已加载程序集(优先 Tecnomatix)里按关键字搜公共类型名。写代码前先用它找到要用的类型。"; }
        }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""keyword"": { ""type"": ""string"", ""description"": ""类型名关键字, 如 Robot/Weld/Collision/Frame"" }
                    },
                    ""required"": [""keyword""]
                }");
            }
        }
        public override string Execute(JObject input)
        {
            return ApiInspector.ListTypes(GetString(input, "keyword", ""), 60);
        }
    }

    public sealed class InspectTypeTool : TxAgentToolBase
    {
        public override string Name { get { return "inspect_type"; } }
        public override string Description
        {
            get { return "列出某类型的公共属性/方法/事件签名(反射)。给定 list_types 找到的全名或简单名。写代码前确认 API 用它。"; }
        }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""type_name"": { ""type"": ""string"", ""description"": ""类型全名或简单名, 如 Tecnomatix.Engineering.TxRobot 或 TxRobot"" }
                    },
                    ""required"": [""type_name""]
                }");
            }
        }
        public override string Execute(JObject input)
        {
            return ApiInspector.InspectType(GetString(input, "type_name", ""));
        }
    }

    public sealed class InspectObjectTool : TxAgentToolBase
    {
        public override string Name { get { return "inspect_object"; } }
        public override string Description
        {
            get { return "探查一个活动对象(按 name 或当前选中第一个)的运行时类型与各属性取值。用于摸清真实对象上有什么可用。"; }
        }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": { ""type"": ""string"", ""description"": ""对象名, 缺省用当前选中第一个"" },
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
            return PsBridge.InspectObject(GetString(input, "name", null), GetString(input, "object_id", null));
        }
    }
}
