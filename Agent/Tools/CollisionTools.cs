// TxTools.Agent / Tools / CollisionTools.cs
// 碰撞检测查询工具：query_collision_sets（只读，反射+dynamic 优雅降级）。
// 用反射+dynamic 尝试访问 TxApplication.ActiveDocument 的碰撞相关成员
// (TxCollisionSet/TxInterferenceSet 等)；若 SDK 版本无此 API，返回探索指引。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) query_collision_sets — 查询碰撞检测组配置（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询场景内配置的碰撞检测组(Collision Set)，列出各组的名称、
    /// 包含的对象对数、当前是否启用。用于了解场景碰撞检测配置。
    /// 若 SDK 版本无此 API，会返回探索指引。
    /// </summary>
    public sealed class QueryCollisionSetsTool : TxAgentToolBase
    {
        public override string Name { get { return "query_collision_sets"; } }

        public override string Description
        {
            get
            {
                return "查询场景内配置的碰撞检测组(Collision Set)，列出各组的名称、" +
                       "包含的对象对数、当前是否启用。用于了解场景碰撞检测配置。" +
                       "若 SDK 版本无此 API，会返回探索指引，建议用 list_types 查找正确类型名后用 run_csharp 访问。";
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
                        ""name_filter"": { ""type"": ""string"", ""description"": ""碰撞组名称关键字(模糊匹配)，留空列出全部"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var nameFilter = GetString(input, "name_filter", null);

            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    dynamic doc = TxApplication.ActiveDocument;
                    if (doc == null) return "没有打开的研究文档。";

                    // ── 策略1: 直接在文档上找碰撞相关属性 ──
                    object collisionSets = null;
                    string source = "";

                    // 尝试多种可能的属性名/方法名
                    try { collisionSets = doc.CollisionSets; source = "CollisionSets"; } catch { }
                    if (collisionSets == null)
                        try { collisionSets = doc.InterferenceSets; source = "InterferenceSets"; } catch { }
                    if (collisionSets == null)
                        try { collisionSets = doc.GetCollisionSets(); source = "GetCollisionSets()"; } catch { }
                    if (collisionSets == null)
                        try { collisionSets = doc.GetInterferenceSets(); source = "GetInterferenceSets()"; } catch { }

                    // ── 策略2: 尝试通过已知类型名反射获取 ──
                    if (collisionSets == null)
                    {
                        // 尝试常见碰撞集合类型名
                        var candidateTypeNames = new string[]
                        {
                            "TxCollisionSetCollection",
                            "TxCollisionSets",
                            "TxInterferenceSetCollection",
                            "TxInterferenceSets",
                            "TxCollisionSetManager",
                            "TxInterferenceManager"
                        };

                        var teAssembly = typeof(TxApplication).Assembly;
                        foreach (var tn in candidateTypeNames)
                        {
                            try
                            {
                                var setType = teAssembly.GetType("Tecnomatix.Engineering." + tn);
                                if (setType != null)
                                {
                                    // 找到类型了，尝试在文档上通过反射获取对应属性
                                    string propBase = tn.Replace("Collection", "").Replace("Manager", "").Replace("Sets", "Sets");
                                    var prop = doc.GetType().GetProperty(propBase);
                                    if (prop != null)
                                    {
                                        try { collisionSets = prop.GetValue((object)doc, null); source = propBase; } catch { }
                                    }
                                    // 也尝试 Get 方法
                                    if (collisionSets == null)
                                    {
                                        var getMethod = doc.GetType().GetMethod("Get" + propBase);
                                        if (getMethod != null)
                                        {
                                            try { collisionSets = getMethod.Invoke((object)doc, null); source = "Get" + propBase + "()"; } catch { }
                                        }
                                    }
                                    if (collisionSets != null) break;
                                }
                            }
                            catch { }
                        }
                    }

                    // ── 策略3: 反射扫描碰撞相关类型 ──
                    if (collisionSets == null)
                    {
                        var teAssembly = typeof(TxApplication).Assembly;
                        var collisionTypes = new List<Type>();

                        try
                        {
                            foreach (var t in teAssembly.GetTypes())
                            {
                                if (t == null) continue;
                                var tName = t.Name;
                                if (tName.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0
                                    || tName.IndexOf("Interference", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (tName.IndexOf("Set", StringComparison.OrdinalIgnoreCase) >= 0
                                        || tName.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0
                                        || tName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
                                        || tName.IndexOf("Checker", StringComparison.OrdinalIgnoreCase) >= 0
                                        || tName.IndexOf("Detector", StringComparison.OrdinalIgnoreCase) >= 0)
                                        collisionTypes.Add(t);
                                }
                            }
                        }
                        catch (ReflectionTypeLoadException rtle)
                        {
                            // 某些类型可能加载失败，只取成功加载的
                            if (rtle.Types != null)
                            {
                                foreach (var t in rtle.Types)
                                {
                                    if (t == null) continue;
                                    var tName = t.Name;
                                    if (tName.IndexOf("Collision", StringComparison.OrdinalIgnoreCase) >= 0
                                        || tName.IndexOf("Interference", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        if (tName.IndexOf("Set", StringComparison.OrdinalIgnoreCase) >= 0
                                            || tName.IndexOf("Collection", StringComparison.OrdinalIgnoreCase) >= 0
                                            || tName.IndexOf("Manager", StringComparison.OrdinalIgnoreCase) >= 0
                                            || tName.IndexOf("Checker", StringComparison.OrdinalIgnoreCase) >= 0
                                            || tName.IndexOf("Detector", StringComparison.OrdinalIgnoreCase) >= 0)
                                            collisionTypes.Add(t);
                                    }
                                }
                            }
                        }

                        if (collisionTypes.Count > 0)
                        {
                            var sb2 = new StringBuilder();
                            sb2.AppendLine("发现碰撞检测相关类型（但未能直接获取碰撞组数据）：");
                            foreach (var t in collisionTypes)
                                sb2.AppendLine("  • " + t.FullName);
                            sb2.AppendLine();
                            sb2.AppendLine("建议：用 list_types('Collision') 查看这些类型的成员，再用 run_csharp 访问。");
                            sb2.AppendLine("示例：先用 inspect_type 查看某个类型的方法和属性，再写 C# 代码获取碰撞组数据。");
                            return sb2.ToString();
                        }

                        // ── 策略4: 完全找不到 ──
                        return "当前 SDK 版本未发现碰撞检测 API，建议用 list_types('Collision') 查找正确类型名后用 run_csharp 访问。";
                    }

                    // ── 找到了碰撞组集合，遍历输出 ──
                    var sb = new StringBuilder();
                    sb.AppendLine("碰撞检测组（来源: " + source + "）：");
                    sb.AppendLine("Name | ObjectPairs | Enabled");
                    sb.AppendLine("-----|-------------|-------");

                    int count = 0;
                    var en = collisionSets as IEnumerable;
                    if (en != null)
                    {
                        foreach (var set in en)
                        {
                            try
                            {
                                dynamic dSet = set;
                                string setName = "<unnamed>";
                                try { setName = (string)dSet.Name ?? "<unnamed>"; } catch { }

                                // 名称过滤
                                if (!string.IsNullOrWhiteSpace(nameFilter)
                                    && setName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;

                                int pairCount = 0;
                                try
                                {
                                    // 尝试多种方式读取对象对数
                                    try { pairCount = (int)dSet.PairsCount; } catch { }
                                    if (pairCount == 0)
                                    {
                                        try
                                        {
                                            dynamic pairs = dSet.Pairs;
                                            var pairEn = pairs as IEnumerable;
                                            if (pairEn != null)
                                            {
                                                foreach (var p in pairEn) pairCount++;
                                            }
                                            else if (pairs != null) pairCount = 1;
                                        }
                                        catch { }
                                    }
                                    if (pairCount == 0)
                                    {
                                        try { pairCount = (int)dSet.Count; } catch { }
                                    }
                                    if (pairCount == 0)
                                    {
                                        try
                                        {
                                            dynamic objs1 = dSet.FirstSet;
                                            dynamic objs2 = dSet.SecondSet;
                                            int c1 = 0, c2 = 0;
                                            var en1 = objs1 as IEnumerable;
                                            if (en1 != null) foreach (var x in en1) c1++;
                                            var en2 = objs2 as IEnumerable;
                                            if (en2 != null) foreach (var x in en2) c2++;
                                            pairCount = c1 * c2;
                                        }
                                        catch { }
                                    }
                                }
                                catch { }

                                bool enabled = false;
                                try { enabled = (bool)dSet.Enabled; } catch { }
                                try { if (!enabled) enabled = (bool)dSet.IsActive; } catch { }
                                try { if (!enabled) enabled = (bool)dSet.Active; } catch { }

                                sb.AppendLine(setName + " | " + pairCount + " | " + (enabled ? "是" : "否"));
                                count++;
                            }
                            catch { count++; }
                        }
                    }
                    else
                    {
                        // 可能是单个对象而非集合
                        try
                        {
                            dynamic dSet = collisionSets;
                            string singleName = "<unnamed>";
                            try { singleName = (string)dSet.Name ?? "<unnamed>"; } catch { }
                            sb.AppendLine(singleName + " | ? | ?");
                            count = 1;
                        }
                        catch { }
                    }

                    if (count == 0) sb.AppendLine("(没有碰撞检测组)");
                    sb.AppendLine();
                    sb.Append("共 " + count + " 个碰撞检测组");
                    return sb.ToString();
                }
                catch (Exception ex) { return "查询碰撞检测组失败: " + ex.Message; }
            });
        }
    }
}
