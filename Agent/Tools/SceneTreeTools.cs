// TxTools.Agent / Tools / SceneTreeTools.cs
// 真实遍历场景对象树的只读工具。给模型"有据可依"的数据，避免从操作列表脑补对象/数量。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    /// <summary>按类型统计/枚举场景对象。回答"场景里有多少机器人/夹具/…"。</summary>
    public sealed class CountObjectsTool : TxAgentToolBase
    {
        public override string Name { get { return "count_objects"; } }

        public override string Description
        {
            get
            {
                return "遍历整个场景(物理/组件/资源根)统计对象。type_keyword 为空时返回各类型数量直方图；" +
                       "给定关键字(如 Robot/机器人/Gun/Fixture)时列出匹配的对象。" +
                       "需要场景对象数量或清单时用它，不要从操作列表推断。";
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
                        ""type_keyword"": {
                            ""type"": ""string"",
                            ""description"": ""类型名关键字(模糊匹配)，留空则输出全场景类型直方图""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CountObjectsByType(GetString(input, "type_keyword", null));
        }
    }

    /// <summary>展开一个组件，按类型统计其子对象。回答"CD_L 下有多少设备"。</summary>
    public sealed class ListChildrenTool : TxAgentToolBase
    {
        public override string Name { get { return "list_children"; } }

        public override string Description
        {
            get
            {
                return "展开一个组件并按类型统计其子对象数量。name 为对象名(缺省用当前选中第一个)；" +
                       "recursive=true 递归到底，false 仅直接子级。回答'某组件下有多少设备/子件'时用它。";
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
                        ""name"": { ""type"": ""string"", ""description"": ""组件名，缺省用当前选中对象"" },
                        ""recursive"": { ""type"": ""boolean"", ""description"": ""是否递归到底，默认 false (仅直接子级)"" },
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
            bool recursive = false;
            var t = input != null ? input["recursive"] : null;
            if (t != null && t.Type == JTokenType.Boolean) recursive = (bool)t;
            return PsBridge.ListChildren(name, recursive, GetString(input, "object_id", null));
        }
    }
}
