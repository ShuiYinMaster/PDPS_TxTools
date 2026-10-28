using System;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    public sealed class QueryCollisionSetsTool : TxAgentToolBase
    {
        public override string Name { get { return "query_collision_sets"; } }
        public override string Description { get { return "通过 CollisionRoot 查询配置的碰撞对：两侧对象列表、成员数量、启用状态和近失距离。此工具只读取配置，不执行碰撞检测；列表组合数不代表实际碰撞数。"; } }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema { get { return JObject.Parse(@"{""type"":""object"",""properties"":{""name_filter"":{""type"":""string""}}}"); } }
        public override string Execute(JObject input)
        {
            return PsContext.Current.Run<string>(() => {
                try {
                    var doc = TxApplication.ActiveDocument;
                    if (doc == null || doc.CollisionRoot == null) return "Error: 当前文档没有 CollisionRoot。";
                    var root = doc.CollisionRoot;
                    var pairs = new JArray();
                    var filter = GetString(input, "name_filter", null);
                    foreach (TxCollisionPair pair in root.PairList) {
                        if (!string.IsNullOrWhiteSpace(filter) && pair.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var first = Members(pair.FirstList); var second = Members(pair.SecondList);
                        pairs.Add(new JObject { ["name"]=pair.Name, ["object_id"]=pair.Id, ["active"]=pair.Active,
                            ["first_count"]=first.Count, ["second_count"]=second.Count,
                            ["candidate_combinations"]=(long)first.Count * second.Count,
                            ["first"]=first, ["second"]=second,
                            ["near_miss_distance"]=pair.IsNearMissDefined ? (JToken)new JValue(pair.NearMissDistance) : JValue.CreateNull(),
                            ["allowed_penetration"]=pair.IsAllowedPenetrationValueDefined ? (JToken)new JValue(pair.AllowedPenetrationValue) : JValue.CreateNull() });
                    }
                    return new JObject { ["check_collisions"]=root.CheckCollisions, ["check_near_miss"]=root.CheckNearMiss,
                        ["query_mode"]=root.CollisionQueryMode.ToString(), ["count"]=pairs.Count, ["pairs"]=pairs }.ToString();
                } catch (Exception ex) { return "Error: 查询碰撞配置失败: " + ex.Message; }
            });
        }
        private static JArray Members(TxObjectList list)
        {
            var result = new JArray();
            if (list != null) foreach (ITxObject obj in list) result.Add(new JObject { ["name"]=obj.Name, ["object_id"]=obj.Id });
            return result;
        }
    }
}
