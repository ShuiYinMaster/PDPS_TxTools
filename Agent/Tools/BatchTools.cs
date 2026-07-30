// TxTools.Agent / Tools / BatchTools.cs
// 批量操作工具：find_objects（只读搜索）+ batch_rename（变更，需审批，可撤销）。
// find_objects 是 count_objects 的补充，按名称/类型关键字搜索对象列表后可定位操作；
// batch_rename 支持前缀/后缀/正则三种模式重命名，包 Undo 块可 Ctrl+Z 撤销。

using System;
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
                        ""max_results"": { ""type"": ""number"", ""description"": ""最大返回数量，默认 50"" }
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
            try { dynamic d = o; dynamic p = d.Parent; if (p != null) return (string)p.Name ?? ""; } catch { }
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
                    ""required"": [""names"", ""mode"", ""old_str"", ""new_str""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            // 解析参数
            var nameList = new List<string>();
            var arr = input != null ? input["names"] as JArray : null;
            if (arr != null)
                foreach (var t in arr)
                    if (t != null && t.Type == JTokenType.String) nameList.Add((string)t);

            if (nameList.Count == 0) return "未提供要重命名的对象名列表(names)。";

            var mode = GetString(input, "mode", "prefix_replace");
            var oldStr = GetString(input, "old_str", null);
            var newStr = GetString(input, "new_str", null);

            if (string.IsNullOrEmpty(oldStr)) return "未提供 old_str(要替换的字符串/正则)。";
            if (newStr == null) return "未提供 new_str(替换结果字符串)。";

            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var doc = TxApplication.ActiveDocument;
                    if (doc == null) return "没有打开的研究文档。";

                    // 收集场景对象，建立名称→对象映射
                    var all = CollectAllSceneObjects(doc);
                    var nameMap = new Dictionary<string, ITxObject>(StringComparer.OrdinalIgnoreCase);
                    foreach (var o in all)
                    {
                        var n = SafeName(o);
                        if (n != null && !nameMap.ContainsKey(n)) nameMap[n] = o;
                    }

                    // 找出要重命名的对象
                    var targets = new List<ITxObject>();
                    var missingNames = new List<string>();
                    foreach (var nm in nameList)
                    {
                        ITxObject obj;
                        if (nameMap.TryGetValue(nm, out obj))
                        {
                            targets.Add(obj);
                        }
                        else
                        {
                            // 模糊匹配回退
                            bool found = false;
                            foreach (var o in all)
                            {
                                var n = SafeName(o);
                                if (n != null && n.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    targets.Add(o);
                                    found = true;
                                    break;
                                }
                            }
                            if (!found) missingNames.Add(nm);
                        }
                    }

                    if (targets.Count == 0)
                        return "没有找到任何要重命名的对象。未找到: " + string.Join(", ", missingNames);

                    // 开启 Undo 块（多策略反射，同 PsBridge 的模式）
                    bool undo = BeginUndo(doc, "batch_rename");

                    int renamed = 0, skipped = 0;
                    var details = new List<string>();
                    try
                    {
                        foreach (var obj in targets)
                        {
                            var currentName = SafeName(obj);
                            if (currentName == null) { skipped++; continue; }

                            string newName;
                            switch (mode)
                            {
                                case "regex_replace":
                                    try
                                    {
                                        newName = Regex.Replace(currentName, oldStr, newStr);
                                    }
                                    catch (Exception rex)
                                    {
                                        skipped++;
                                        details.Add(currentName + " → 正则替换失败: " + rex.Message);
                                        continue;
                                    }
                                    break;
                                case "suffix_replace":
                                case "prefix_replace":
                                default:
                                    newName = currentName.Replace(oldStr, newStr);
                                    break;
                            }

                            if (string.Equals(newName, currentName, StringComparison.Ordinal))
                            {
                                skipped++;
                                details.Add(currentName + " → 无变化(old_str 未匹配)");
                                continue;
                            }

                            // 检查新名是否已被占用（排除自身，因为重命名后旧名会释放）
                            ITxObject existingObj;
                            if (nameMap.TryGetValue(newName, out existingObj) && existingObj != obj)
                            {
                                skipped++;
                                details.Add(currentName + " → " + newName + " (名称已存在，跳过)");
                                continue;
                            }

                            // 设置新名称
                            try
                            {
                                dynamic d = obj;
                                d.Name = newName;
                                renamed++;
                                details.Add(currentName + " → " + newName);
                            }
                            catch (Exception ex)
                            {
                                skipped++;
                                details.Add(currentName + " → 设置名称失败: " + ex.Message);
                            }
                        }
                    }
                    finally
                    {
                        if (undo) EndUndo(doc);
                    }

                    try { TxApplication.RefreshDisplay(); } catch { }

                    var sb = new StringBuilder();
                    sb.AppendLine("批量重命名完成：重命名 " + renamed + " 个 / 总共 " + targets.Count + " 个 / 跳过 " + skipped + " 个。");
                    if (missingNames.Count > 0)
                        sb.AppendLine("未找到的对象: " + string.Join(", ", missingNames));
                    sb.AppendLine("变更明细:");
                    int detailCap = Math.Min(details.Count, 40);
                    for (int i = 0; i < detailCap; i++) sb.AppendLine("  " + details[i]);
                    if (details.Count > detailCap)
                        sb.AppendLine("  ...(其余 " + (details.Count - detailCap) + " 条省略)");
                    if (undo) sb.AppendLine("可 Ctrl+Z 撤销");
                    return sb.ToString();
                }
                catch (Exception ex) { return "批量重命名失败: " + ex.Message; }
            });
        }

        // ─── Undo 辅助（多策略反射，同 PsBridge 的模式）───

        private static bool BeginUndo(TxDocument doc, string desc)
        {
            try { dynamic d = doc; dynamic ur = d.UndoRedo; if (ur != null) { ur.BeginCommand(desc); return true; } } catch { }
            try { dynamic d = doc; dynamic ctx = d.UndoContext; if (ctx != null) { ctx.Open(desc); return true; } } catch { }
            try { dynamic d = doc; dynamic um = d.UndoManager; if (um != null) { um.BeginUndoStep(desc); return true; } } catch { }
            return false;
        }

        private static void EndUndo(TxDocument doc)
        {
            try { dynamic d = doc; d.UndoRedo.EndCommand(); return; } catch { }
            try { dynamic d = doc; d.UndoContext.Close(); return; } catch { }
            try { dynamic d = doc; d.UndoManager.EndUndoStep(); return; } catch { }
        }

        // ─── 场景遍历辅助 ───

        private static List<ITxObject> CollectAllSceneObjects(TxDocument doc)
        {
            var list = new List<ITxObject>();
            var seen = new HashSet<int>();
            dynamic dDoc = doc;

            foreach (var rn in new string[] { "PhysicalRoot", "ComponentRoot", "ResourceRoot" })
            {
                try
                {
                    object root = null;
                    switch (rn)
                    {
                        case "PhysicalRoot": root = dDoc.PhysicalRoot; break;
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
