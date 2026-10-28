// TxTools.Agent / Tools / BatchTools.cs
// 批量操作工具：find_objects（只读搜索）+ batch_rename（变更，需审批，可撤销）。
// find_objects 是 count_objects 的补充，按名称/类型关键字搜索对象列表后可定位操作；
// batch_rename 支持前缀/后缀/正则三种模式重命名，包 Undo 块可 Ctrl+Z 撤销。

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) find_objects — 按名称/类型关键字搜索场景对象（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 在场景中按名称关键字或类型关键字搜索对象，返回匹配的对象列表（名称/类型/父级）。
    /// 是 count_objects 的补充，用于定位具体对象后再操作。
    /// </summary>
    public sealed class FindObjectsTool : TxAgentToolBase
    {
        public override string Name { get { return "find_objects"; } }

        public override string Description
        {
            get
            {
                return "在场景中按名称关键字或类型关键字搜索对象，返回匹配的对象列表（名称/类型/父级）。" +
                       "是 count_objects 的补充，用于定位具体对象后再操作。" +
                       "root 指定搜索范围：physical(物理树)、operation(操作树)、mfg(制造树)、all(全部)。";
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
                        ""name_keyword"": { ""type"": ""string"", ""description"": ""名称关键字(模糊匹配)"" },
                        ""type_keyword"": { ""type"": ""string"", ""description"": ""类型名关键字(如 Robot/Weld/Device)"" },
                        ""root"": {
                            ""type"": ""string"",
                            ""enum"": [""physical"", ""operation"", ""mfg"", ""all""],
                            ""description"": ""搜索范围：physical=物理树, operation=操作树, mfg=制造树, all=全部，默认 all""
                        },
                        ""max_results"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 5000, ""description"": ""最大返回数量，默认 50"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var nameKeyword = GetString(input, "name_keyword", null);
            var typeKeyword = GetString(input, "type_keyword", null);
            var root = GetString(input, "root", "all");

            int maxResults = 50;
            var tMax = input != null ? input["max_results"] : null;
            if (tMax != null && (tMax.Type == JTokenType.Integer || tMax.Type == JTokenType.Float))
                maxResults = (int)tMax;

            if (!new[] { "physical", "operation", "mfg", "all" }.Contains(root)) return "Error: root 必须为 physical/operation/mfg/all。";
            if (maxResults < 1 || maxResults > 5000) return "Error: max_results 应为 1..5000。";
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var doc = TxApplication.ActiveDocument;
                    if (doc == null) return "没有打开的研究文档。";

                    // 收集搜索范围的根节点后代
                    var candidates = new List<ITxObject>();
                    var seen = new HashSet<int>();
                    var roots = GetRoots(doc, root);
                    foreach (var rootObj in roots)
                    {
                        var descendants = GetAllDescendants(rootObj);
                        if (descendants != null)
                        {
                            foreach (ITxObject o in descendants)
                            {
                                if (o != null && seen.Add(RuntimeHelpers.GetHashCode(o)))
                                    candidates.Add(o);
                            }
                        }
                    }

                    if (candidates.Count == 0) return "指定范围内没有可遍历的对象。";

                    // 过滤
                    var matches = new List<ITxObject>();
                    bool hasNameFilter = !string.IsNullOrWhiteSpace(nameKeyword);
                    bool hasTypeFilter = !string.IsNullOrWhiteSpace(typeKeyword);
                    bool wantRobot = hasTypeFilter && (
                        typeKeyword.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0
                        || typeKeyword.Contains("机器人"));

                    foreach (var o in candidates)
                    {
                        string objName = SafeName(o);
                        string typeName = o.GetType().Name;

                        bool nameHit = !hasNameFilter ||
                            (objName != null && objName.IndexOf(nameKeyword, StringComparison.OrdinalIgnoreCase) >= 0);
                        bool typeHit = !hasTypeFilter ||
                            typeName.IndexOf(typeKeyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (wantRobot && o is TxRobot);

                        if (nameHit && typeHit) matches.Add(o);
                    }

                    if (matches.Count == 0) return "没有匹配的对象。";

                    // 输出表格
                    int cap = Math.Min(matches.Count, maxResults);
                    var sb = new StringBuilder();
                    sb.AppendLine("匹配对象 " + matches.Count + " 个(显示 " + cap + " 个)：");
                    sb.AppendLine("Name [Id] | Type | Parent");
                    sb.AppendLine("----------|------|-------");
                    for (int i = 0; i < cap; i++)
                    {
                        var o = matches[i];
                        sb.AppendLine(PsBridge.Ref(o) + " | " + o.GetType().Name + " | " + ParentName(o));
                    }
                    if (matches.Count > cap)
                        sb.AppendLine("...(其余 " + (matches.Count - cap) + " 个省略，可用 max_results 增大返回量)");

                    return sb.ToString();
                }
                catch (Exception ex) { return "搜索对象失败: " + ex.Message; }
            });
        }

        // ─── 内部辅助（均在 PsContext.Run 内调用，不碰 PS 对象）───

        private static List<object> GetRoots(TxDocument doc, string root)
        {
            var roots = new List<object>();
            dynamic dDoc = doc;

            switch (root)
            {
                case "physical":
                    try { var r = dDoc.PhysicalRoot; if (r != null) roots.Add(r); } catch { }
                    break;
                case "operation":
                    try { var r = dDoc.OperationRoot; if (r != null) roots.Add(r); } catch { }
                    break;
                case "mfg":
                    try { var r = dDoc.MfgRoot; if (r != null) roots.Add(r); } catch { }
                    break;
                case "all":
                default:
                    foreach (var rn in new string[] { "PhysicalRoot", "ComponentRoot", "ResourceRoot", "OperationRoot", "MfgRoot" })
                    {
                        try
                        {
                            object r = null;
                            switch (rn)
                            {
                                case "PhysicalRoot": r = dDoc.PhysicalRoot; break;
                                case "ComponentRoot": r = dDoc.ComponentRoot; break;
                                case "ResourceRoot": r = dDoc.ResourceRoot; break;
                                case "OperationRoot": r = dDoc.OperationRoot; break;
                                case "MfgRoot": r = dDoc.MfgRoot; break;
                            }
                            if (r != null) roots.Add(r);
                        }
                        catch { }
                    }
                    break;
            }
            return roots;
        }

        private static TxObjectList GetAllDescendants(object root)
        {
            var f = new TxTypeFilter(typeof(ITxObject));
            try { dynamic d = root; return d.GetAllDescendants(f) as TxObjectList; } catch { }
            try { dynamic d = root; return d.GetAllDescendants() as TxObjectList; } catch { }
            return null;
        }

        private static string SafeName(object o)
        {
            try { dynamic d = o; string n = (string)d.Name; return string.IsNullOrEmpty(n) ? o.ToString() : n; }
            catch { return o == null ? "<null>" : o.ToString(); }
        }

        private static string ParentName(object o)
        {
            try { dynamic d = o; dynamic p = d.Collection; if (p != null) return (string)p.Name ?? ""; } catch { }
            return "";
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) batch_rename — 批量重命名场景对象（变更，需审批，可撤销）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 批量重命名场景对象：按前缀/后缀/序号规则重命名一批对象。
    /// supports 三种模式: prefix_replace(前缀替换), suffix_replace(后缀替换), regex_replace(正则替换)。
    /// 变更操作，需审批，可 Ctrl+Z 撤销。
    /// </summary>
    public sealed class BatchRenameTool : TxAgentToolBase
    {
        public override string Name { get { return "batch_rename"; } }

        public override string Description
        {
            get
            {
                return "批量重命名场景对象：按前缀/后缀/正则规则重命名一批对象。" +
                       "支持三种模式: prefix_replace(前缀替换), suffix_replace(后缀替换), " +
                       "regex_replace(正则替换，C#语法)。变更操作，需审批，可 Ctrl+Z 撤销。";
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
                        ""object_ids"": {""type"":""array"",""items"":{""type"":""string""},""description"":""优先按 ID 定位，避免同名歧义""},
                        ""names"": {
                            ""type"": ""array"",
                            ""items"": { ""type"": ""string"" },
                            ""description"": ""要重命名的对象名列表""
                        },
                        ""mode"": {
                            ""type"": ""string"",
                            ""enum"": [""prefix_replace"", ""suffix_replace"", ""regex_replace""],
                            ""description"": ""重命名模式：prefix_replace=前缀替换, suffix_replace=后缀替换, regex_replace=正则替换，默认 prefix_replace""
                        },
                        ""old_str"": { ""type"": ""string"", ""description"": ""要替换的字符串或正则表达式"" },
                        ""new_str"": { ""type"": ""string"", ""description"": ""替换结果字符串"" }
                    },
                    ""required"": [""mode"", ""old_str"", ""new_str""], ""anyOf"":[{""required"":[""names""]},{""required"":[""object_ids""]}]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            var ids = input["object_ids"] as JArray;
            var names = input["names"] as JArray;
            var keys = ids != null && ids.Count > 0 ? ids : names;
            if (keys == null || keys.Count == 0 || keys.Any(k => k.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)k)))
                return "Error: 提供非空 names 或 object_ids 字符串数组。";
            var mode = GetString(input, "mode", "prefix_replace");
            var oldText = GetString(input, "old_str"); var newText = GetString(input, "new_str");
            if (string.IsNullOrEmpty(oldText) || newText == null) return "Error: old_str 不能为空，new_str 必须显式提供。";
            if (mode != "prefix_replace" && mode != "suffix_replace" && mode != "regex_replace") return "Error: 无效的重命名模式。";
            return PsContext.Current.Run<string>(() => {
                try {
                    var doc = TxApplication.ActiveDocument;
                    if (doc == null) return "Error: 没有打开的文档。";
                    var targets = new List<ITxObject>(); var seen = new HashSet<string>();
                    foreach (var key in keys) {
                        ITxObject obj; string error;
                        if (!PsBridge.TryResolve(keys == ids ? null : (string)key, keys == ids ? (string)key : null, out obj, out error)) return "Error: " + error;
                        if (seen.Add(obj.Id)) targets.Add(obj);
                    }
                    var sceneObjects = CollectAllSceneObjects(doc);
                    var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var changes = new List<Tuple<ITxObject, string, string>>();
                    foreach (var obj in targets) {
                        var current = obj.Name;
                        var next = mode == "regex_replace" ? Regex.Replace(current, oldText, newText, RegexOptions.None, TimeSpan.FromMilliseconds(500)) :
                            mode == "prefix_replace" ? (current.StartsWith(oldText, StringComparison.Ordinal) ? newText + current.Substring(oldText.Length) : current) :
                            (current.EndsWith(oldText, StringComparison.Ordinal) ? current.Substring(0, current.Length - oldText.Length) + newText : current);
                        if (next == current) continue;
                        if (!obj.CanBeRenamed || !obj.CanBeModified || string.IsNullOrWhiteSpace(next)) return "Error: 对象不可重命名或新名称无效: " + PsBridge.Ref(obj);
                        if (sceneObjects.Any(other => other.Id != obj.Id && string.Equals(other.Name, next, StringComparison.OrdinalIgnoreCase)) || !planned.Add(next)) return "Error: 新名称已被占用或批内冲突: " + next + "；未执行任何修改。";
                        changes.Add(Tuple.Create(obj, current, next));
                    }
                    var details = new JArray();
                    using (var undo = TxTools.Common.SceneUndoScope.Begin(doc, "批量重命名")) {
                        foreach (var change in changes) {
                            undo.EnsureDocument();
                            try {
                                change.Item1.Name = change.Item3;
                                if (change.Item1.Name != change.Item3) throw new InvalidOperationException("写入后名称不匹配。");
                                details.Add(new JObject { ["object_id"]=change.Item1.Id, ["old_name"]=change.Item2, ["new_name"]=change.Item3 });
                            } catch (Exception ex) { return "Error: 批量重命名中断；此前已完成 " + details.Count + " 个，可 Ctrl+Z 撤销。" + ex.Message + "\n" + details; }
                        }
                    }
                    TxApplication.RefreshDisplay();
                    return new JObject { ["renamed"]=details.Count, ["unchanged"]=targets.Count - details.Count, ["changes"]=details }.ToString();
                } catch (Exception ex) { return "Error: 批量重命名失败: " + ex.Message; }
            });
        }

        // ─── Undo 辅助（多策略反射，同 PsBridge 的模式）───

        private static TxTools.Common.SceneUndoScope BeginUndo(TxDocument doc, string description)
        {
            return TxTools.Common.SceneUndoScope.Begin(doc, description);
        }

        private static void EndUndo(TxTools.Common.SceneUndoScope undo)
        {
            if (undo != null) undo.Dispose();
        }

        // ─── 场景遍历辅助 ───

        private static List<ITxObject> CollectAllSceneObjects(TxDocument doc)
        {
            var list = new List<ITxObject>();
            var seen = new HashSet<int>();
            dynamic dDoc = doc;

            foreach (var rn in new string[] { "PhysicalRoot", "ComponentRoot", "ResourceRoot", "OperationRoot" })
            {
                try
                {
                    object root = null;
                    switch (rn)
                    {
                        case "PhysicalRoot": root = dDoc.PhysicalRoot; break;
                        case "OperationRoot": root = dDoc.OperationRoot; break;
                        case "ComponentRoot": root = dDoc.ComponentRoot; break;
                        case "ResourceRoot": root = dDoc.ResourceRoot; break;
                    }
                    if (root == null) continue;

                    var f = new TxTypeFilter(typeof(ITxObject));
                    TxObjectList kids = null;
                    try { dynamic d = root; kids = d.GetAllDescendants(f) as TxObjectList; } catch { }
                    if (kids == null) try { dynamic d = root; kids = d.GetAllDescendants() as TxObjectList; } catch { }
                    if (kids == null) continue;

                    foreach (ITxObject o in kids)
                    {
                        if (o == null) continue;
                        if (seen.Add(RuntimeHelpers.GetHashCode(o))) list.Add(o);
                    }
                }
                catch { }
            }
            return list;
        }

        private static string SafeName(object o)
        {
            try { dynamic d = o; string n = (string)d.Name; return string.IsNullOrEmpty(n) ? o.ToString() : n; }
            catch { return o == null ? "<null>" : o.ToString(); }
        }
    }
}
