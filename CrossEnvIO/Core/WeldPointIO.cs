// WeldPointIO.cs  --  C# 8.0
// 跨环境焊点导出/重建（含绑定零件 + 可选投影）。
//
// 数据格式（TSV，首行头）:
//   兼容两种布局（按表头自动识别）:
//     ANV  : WeldOpName\tWeldPointName\tBindPart\tX\tY\tZ\tRX\tRY\tRZ\tParentOpPath
//     T18FL4: WeldOpName\tBindPart\tX\tY\tZ\tRX\tRY\tRZ\tParentOpPath
// ParentOpPath = 焊点所属复合操作的路径（/ 分隔，可能带 // 前缀或首段重复，均兼容）。
//
// 创建链路（对话验证）:
//   TxMfgCreationDataFactory.CreateWeldPointCreationData(name, TxVector)
//   → TxWeldLocationOperationCreationData { Name, WeldPointCreationData, ProjectedLocation }
//   → 目标复合操作 .CreateWeldLocationOperation(cd)   // 自动在 MfgRoot 建 TxWeldPoint
//   → 设 AbsoluteLocation（RPY_ZYX）→ AssignParts 绑定 → Project 投影

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.DataTypes;

namespace TxTools.CrossEnvIO
{
    public sealed class WeldPointReport
    {
        public int Exported, Created, Updated, Bound, Projected, Skipped, Failed, Rebuilt;
        public readonly List<string> Errors = new List<string>();
        public override string ToString()
            => $"新建 {Created} · 更新已有 {Updated}（更新位置 {Rebuilt} · 绑定 {Bound} · 投影 {Projected}）· 跳过 {Skipped} · 失败 {Failed}";
    }

    public static class WeldPointIO
    {
        private static void Nop(string s) { }
        private const string Fmt = "0.###";

        // ════════════════════════════════════════════════════════════
        //  导出
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 导出 TxWeldLocationOperation。
        /// selectedNames：若非空，只导出名称在此集合中的焊点操作（null/空=导出全部）。
        /// </summary>
        public static int ExportWeldPoints(string filePath, Action<string> log,
                                           HashSet<string> selectedIds = null)
        {
            log = log ?? Nop;
            var doc = TxApplication.ActiveDocument;
            if (doc == null) { log("[导出] 无活动文档"); return 0; }
            var opRoot = doc.OperationRoot;
            if (opRoot == null) { log("[导出] OperationRoot 为空"); return 0; }

            var sb = new StringBuilder();
            sb.AppendLine("WeldOpName\tWeldPointName\tBindPart\tX\tY\tZ\tRX\tRY\tRZ\tParentOpPath\tFormatVersion");

            int count = 0;
            var partPaths = BuildPartPaths();
            WalkOps(opRoot, "", (op, path) =>
            {
                var wop = op as TxWeldLocationOperation;
                if (wop == null) return;
                // 多选过滤：选中了操作则只导出选中的
                if (selectedIds != null && !selectedIds.Contains(wop.Id))
                    return;
                try
                {
                    var wp = wop.WeldPoint;
                    double x = 0, y = 0, z = 0, rx = 0, ry = 0, rz = 0;
                    TxTransformation loc = null;
                    try { loc = wop.AbsoluteLocation; } catch { }
                    if (loc == null && wp != null) { try { loc = wp.AbsoluteLocation; } catch { } }
                    if (loc != null)
                    {
                        x = loc.Translation.X; y = loc.Translation.Y; z = loc.Translation.Z;
                        TxMath.MatrixToEulerDeg(TxMath.TxToArr(loc), out rx, out ry, out rz);
                    }
                    string bind = GetBindPart(wp, partPaths);

                    sb.Append(TransferFile.Field(wop.Name)).Append('\t')
                      .Append(TransferFile.Field(wp != null ? wp.Name : "")).Append('\t')
                      .Append(bind).Append('\t')
                      .Append(x.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(y.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(z.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rx.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(ry.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rz.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(path).Append("\t2")
                      .AppendLine();
                    count++;
                }
                catch (Exception ex) { throw new InvalidOperationException("导出焊点失败: " + SafeName(wop), ex); }
            });

            TransferFile.Write(filePath, sb.ToString());
            log("[导出] ✓ 已写入焊点 " + count + " 条 → " + filePath);
            return count;
        }

        private static void WalkOps(ITxObject node, string path, Action<ITxObject, string> visit)
        {
            var stack = new Stack<KeyValuePair<ITxObject, string>>();
            stack.Push(new KeyValuePair<ITxObject, string>(node, ""));
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                var cur = kv.Key;
                if (cur == null) continue;
                string curPath = kv.Value;
                var coll = cur as ITxObjectCollection;
                if (coll == null) { visit(cur, curPath); continue; }

                var children = new List<ITxObject>();
                try { foreach (ITxObject c in (System.Collections.IEnumerable)coll) if (c != null) children.Add(c); }
                catch (Exception ex) { throw new InvalidOperationException("无法读取操作树: " + SafeName(cur), ex); }

                bool isContainer = cur is TxOperationRoot || cur.GetType().Name.IndexOf("Compound", StringComparison.Ordinal) >= 0
                                   || cur.GetType().Name.IndexOf("Weld", StringComparison.Ordinal) >= 0
                                   || cur.GetType().Name.IndexOf("Robotic", StringComparison.Ordinal) >= 0;
                foreach (var c in children)
                {
                    // 焊点(TxWeldLocationOperation)：ParentOpPath = 父操作路径，不含焊点名。
                    // 重建时焊点挂在父操作(最终段, 如 Weld_Op)下，而不是为焊点名建 CompOp。
                    if (c is TxWeldLocationOperation)
                    {
                        stack.Push(new KeyValuePair<ITxObject, string>(c, curPath));
                        continue;
                    }
                    if (isContainer)
                    {
                        string childPath = curPath.Length == 0 ? Uri.EscapeDataString(c.Name ?? "")
                            : curPath + "/" + Uri.EscapeDataString(c.Name ?? "");
                        stack.Push(new KeyValuePair<ITxObject, string>(c, childPath));
                    }
                    else
                    {
                        stack.Push(new KeyValuePair<ITxObject, string>(c, curPath));
                    }
                }
                visit(cur, curPath);
            }
        }

        private static string GetBindPart(TxWeldPoint wp, Dictionary<string, string> paths)
        {
            if (wp == null) return "";
            try
            {
                var parts = wp.AssignedParts;
                if (parts != null && parts.Count > 0)
                {
                    var names = new JArray();
                    foreach (ITxObject p in parts)
                    {
                        string path; paths.TryGetValue(p.Id, out path);
                        names.Add(new JObject { ["name"] = p.Name, ["path"] = path,
                            ["leading"] = (wp.LeadingPart as ITxObject)?.Id == p.Id });
                    }
                    return names.ToString(Formatting.None);
                }
                var lead = wp.LeadingPart as ITxObject;
                if (lead != null)
                {
                    string path; paths.TryGetValue(lead.Id, out path);
                    return new JArray(new JObject { ["name"] = lead.Name, ["path"] = path,
                        ["leading"] = true }).ToString(Formatting.None);
                }
            }
            catch (Exception ex) { throw new InvalidOperationException("无法读取焊点绑定关系", ex); }
            return "";
        }

        // ════════════════════════════════════════════════════════════
        //  重建
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 从 TSV 重建焊点。doProject=true 时绑定后投影到零件表面。
        /// bindOnly=true 时只绑定+投影已有焊点（文件已有焊点已存在，仅按 WeldOpName 绑定）。
        /// </summary>
        public static WeldPointReport RebuildWeldPoints(string filePath, bool doProject,
                                                        bool bindOnly, Action<string> log)
        {
            log = log ?? Nop;
            var rep = new WeldPointReport();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            { rep.Failed++; rep.Errors.Add("文件不存在: " + filePath); return rep; }

            var doc = TxApplication.ActiveDocument;
            if (doc == null) { rep.Failed++; rep.Errors.Add("无活动文档"); return rep; }
            var opRoot = doc.OperationRoot;
            if (opRoot == null) { rep.Failed++; rep.Errors.Add("OperationRoot 为空"); return rep; }

            var cols = DetectColumns(filePath, log);
            if (cols == null) { rep.Failed++; rep.Errors.Add("无法识别列布局"); return rep; }

            var rows = ReadRows(filePath, cols.MinCols, log);
            if (rows.Count == 0) { log("[重建] 无数据行"); return rep; }

            var partMap = BuildPartMap(log);
            var opIndex = BuildOpIndex(opRoot, log);

            object um = OpenUndo("跨环境焊点重建", log);
            using ((TxTools.Common.SceneUndoScope)um)
            try
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in rows)
                {
                    string weldName = r[cols.WeldOpNameIdx];
                    if (string.IsNullOrWhiteSpace(weldName)) { rep.Skipped++; continue; }
                    string bindName = cols.BindPartIdx >= 0 && cols.BindPartIdx < r.Length ? r[cols.BindPartIdx] : "";
                    double x, y, z, rx, ry, rz;
                    if (!TryParseLoc(r, cols.XIdx, out x, out y, out z, out rx, out ry, out rz))
                    { rep.Failed++; rep.Errors.Add("无效焊点位姿: " + weldName); continue; }
                    string rawPath = r[cols.PathIdx];
                    string groupPath = NormalizePath(rawPath);
                    if (cols.FormatVersionIdx < 0 && (groupPath.EndsWith("/" + Uri.EscapeDataString(weldName), StringComparison.Ordinal)
                        || string.Equals(groupPath, Uri.EscapeDataString(weldName), StringComparison.Ordinal)))
                    {
                        int last = groupPath.LastIndexOf('/');
                        groupPath = last > 0 ? groupPath.Substring(0, last) : "";
                    }

                    try
                    {
                        if (bindOnly)
                        {
                            ITxObject existingGroup;
                            var existingWeld = opIndex.TryGetValue(groupPath, out existingGroup)
                                ? FindOpByName(existingGroup, weldName) : null;
                            if (existingWeld == null)
                            { rep.Failed++; rep.Errors.Add("未找到指定父操作下的焊点: " + groupPath + "/" + weldName); }
                            else if (TryBind(existingWeld, bindName, partMap, doProject, rep, log)) rep.Updated++;
                            continue;
                        }

                        var group = FindOrCreateGroup(opRoot, opIndex, groupPath, log);
                        if (group == null) { rep.Failed++; rep.Errors.Add("无目标父操作: " + rawPath); continue; }

                        // 已存在同名焊点 → 只更新位置/绑定
                        var existing = FindOpByName(group, weldName);
                        if (existing != null)
                        {
                            if (SetLocation(existing, x, y, z, rx, ry, rz)) rep.Rebuilt++;
                            else { rep.Failed++; rep.Errors.Add("更新焊点位姿失败: " + weldName); }
                            TryBind(existing, bindName, partMap, doProject, rep, log);
                            rep.Updated++;
                            continue;
                        }

                        string pointName = cols.WeldPointNameIdx >= 0 && cols.WeldPointNameIdx < r.Length
                            && !string.IsNullOrWhiteSpace(r[cols.WeldPointNameIdx]) ? r[cols.WeldPointNameIdx] : weldName;
                        var wpData = TxMfgCreationDataFactory.CreateWeldPointCreationData(pointName,
                            new TxVector(x, y, z));
                        var cd = new TxWeldLocationOperationCreationData();
                        cd.Name = weldName;
                        cd.WeldPointCreationData = wpData;
                        var trans = new TxTransformation();
                        trans.Translation = new TxVector(x, y, z);
                        cd.ProjectedLocation = trans;

                        TxWeldLocationOperation wop = CreateWeldLocationOp(group, cd);
                        if (wop == null) { rep.Failed++; rep.Errors.Add("创建返回 null: " + weldName); continue; }
                        rep.Created++;
                        if (SetLocation(wop, x, y, z, rx, ry, rz)) rep.Rebuilt++;
                        else { rep.Failed++; rep.Errors.Add("设置焊点位姿失败: " + weldName); }
                        TryBind(wop, bindName, partMap, doProject, rep, log);
                    }
                    catch (Exception ex)
                    {
                        rep.Failed++;
                        rep.Errors.Add("[" + weldName + "] " + ex.Message);
                    }
                }
                CommitUndo(um, log);
            }
            catch (Exception ex)
            {
                AbortUndo(um, log);
                rep.Failed++;
                rep.Errors.Add("事务异常: " + ex.Message);
            }

            log("[重建] " + rep);
            return rep;
        }

        // ── 目标复合操作 ──

        private static Dictionary<string, ITxObject> BuildOpIndex(TxOperationRoot opRoot, Action<string> log)
        {
            var map = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
            map[""] = opRoot;
            var stack = new Stack<KeyValuePair<ITxObject, string>>();
            try
            {
                foreach (ITxObject c in (System.Collections.IEnumerable)opRoot)
                {
                    bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                    if (isOp) stack.Push(new KeyValuePair<ITxObject, string>(c, Uri.EscapeDataString(c.Name ?? "")));
                }
            }
            catch { }
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                if (map.ContainsKey(kv.Value)) map[kv.Value] = null;
                else map[kv.Value] = kv.Key;
                try
                {
                    foreach (ITxObject c in (System.Collections.IEnumerable)kv.Key)
                    {
                        bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                        if (isOp)
                            stack.Push(new KeyValuePair<ITxObject, string>(c, kv.Value + "/" + Uri.EscapeDataString(c.Name ?? "")));
                    }
                }
                catch { }
            }
            return map;
        }

        private static ITxObject FindOrCreateGroup(TxOperationRoot opRoot,
            Dictionary<string, ITxObject> opIndex, string path, Action<string> log)
        {
            if (string.IsNullOrEmpty(path)) return opRoot;
            ITxObject cached;
            if (opIndex.TryGetValue(path, out cached)) return cached;

            // 逐段定位/创建。中间段 → TxCompoundOperation；最后一段(焊点父, 如 Weld_Op) → TxWeldOperation。
            // 焊点挂在最终段的 WeldOperation 下，而不是被套一层按焊点名创建的 CompOp。
            var segs = path.Split('/').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (segs.Count == 0) return null;

            ITxObject cur = opRoot;
            string acc = "";
            int total = segs.Count;
            for (int i = 0; i < total; i++)
            {
                string seg = Uri.UnescapeDataString(segs[i]);
                acc = acc.Length == 0 ? segs[i] : acc + "/" + segs[i];
                bool isLast = (i == total - 1);

                ITxObject found = FindChildOp(cur, seg);
                if (found == null)
                {
                    if (isLast)
                        found = CreateWeldOp(cur, seg);   // 最终段：WeldOperation（焊接操作，焊点父）
                    else
                        found = CreateCompound(cur, new TxCompoundOperationCreationData());
                    if (found == null) return null;
                    if (!TrySetName(found, seg)) throw new InvalidOperationException("父操作重命名失败: " + seg);
                }
                opIndex[acc] = found;
                cur = found;
            }
            return cur;
        }

        private static ITxObject CreateCompound(ITxObject parent, TxCompoundOperationCreationData cd)
        {
            try { dynamic d = parent; return d.CreateCompoundOperation(cd) as ITxObject; }
            catch { return null; }
        }

        private static ITxObject CreateWeldOp(ITxObject parent, string name)
        {
            try
            {
                var cd = new TxWeldOperationCreationData(name);
                dynamic d = parent;
                return d.CreateWeldOperation(cd) as ITxObject;
            }
            catch { return null; }
        }

        private static ITxObject FindChildOp(ITxObject parent, string name)
        {
            ITxObject found = null;
            foreach (ITxObject c in (System.Collections.IEnumerable)parent)
            {
                if (c == null) continue;
                bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                if (isOp && string.Equals(c.Name, name, StringComparison.Ordinal))
                {
                    if (found != null) throw new InvalidOperationException("同名父操作有多份: " + name);
                    found = c;
                }
            }
            return found;
        }

        private static TxWeldLocationOperation FindOpByName(ITxObject group, string name)
        {
            if (group == null) return null;
            TxWeldLocationOperation found = null;
            foreach (ITxObject c in (System.Collections.IEnumerable)group)
            {
                var wop = c as TxWeldLocationOperation;
                if (wop != null && string.Equals(wop.Name, name, StringComparison.Ordinal))
                {
                    if (found != null) throw new InvalidOperationException("同名焊点操作有多份: " + name);
                    found = wop;
                }
            }
            return found;
        }

        /// <summary>在父操作(TxCompoundOperation 或 TxWeldOperation)下创建焊点。父可为焊接操作。</summary>
        private static TxWeldLocationOperation CreateWeldLocationOp(ITxObject parent,
            TxWeldLocationOperationCreationData cd)
        {
            try { dynamic d = parent; return d.CreateWeldLocationOperation(cd) as TxWeldLocationOperation; }
            catch { return null; }
        }

        private static string NormalizePath(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string p = raw.TrimStart('/');
            var segs = p.Split('/').Where(s => s.Length > 0).ToList();
            return string.Join("/", segs);
        }

        // ── 绑定 / 投影 ──

        private static Dictionary<string, string> BuildPartPaths()
        {
            var paths = new Dictionary<string, string>();
            var root = TxApplication.ActiveDocument.PhysicalRoot;
            var pending = new Stack<KeyValuePair<ITxObject, string>>();
            foreach (var child in DirectChildren(root))
                pending.Push(new KeyValuePair<ITxObject, string>(child, PartSegment(child)));
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                paths[item.Key.Id] = item.Value;
                foreach (var child in DirectChildren(item.Key))
                    pending.Push(new KeyValuePair<ITxObject, string>(child,
                        item.Value + "/" + PartSegment(child)));
            }
            return paths;
        }

        private static string PartSegment(ITxObject obj)
        {
            // 同名资源树与零件树也必须区分；路径同时保留容器角色。
            return (obj is TxCompoundPart ? "P:" : obj is TxCompoundResource ? "R:" : "")
                + Uri.EscapeDataString(obj.Name ?? "");
        }

        private static Dictionary<string, TxComponent> BuildPartMap(Action<string> log)
        {
            var map = new Dictionary<string, TxComponent>(StringComparer.Ordinal);
            var paths = BuildPartPaths();
            foreach (ITxObject obj in TxApplication.ActiveDocument.PhysicalRoot
                .GetAllDescendants(new TxTypeFilter(typeof(TxComponent))))
            {
                var comp = obj as TxComponent;
                if (comp == null) continue;
                string path;
                if (paths.TryGetValue(comp.Id, out path))
                    AddUniquePart(map, "@path:" + path, comp);
                AddUniquePart(map, comp.Name, comp);
            }
            return map;
        }

        private static void AddUniquePart(Dictionary<string, TxComponent> map, string key, TxComponent part)
        {
            if (map.ContainsKey(key)) map[key] = null; // 重名不能静默覆盖
            else map[key] = part;
        }

        private static bool TryBind(TxWeldLocationOperation wop, string bindName,
            Dictionary<string, TxComponent> partMap, bool doProject, WeldPointReport rep,
            Action<string> log = null)
        {
            log = log ?? Nop;
            if (string.IsNullOrWhiteSpace(bindName))
            {
                if (doProject) { rep.Failed++; rep.Errors.Add("[" + SafeName(wop) + "] 无绑定零件，无法投影"); }
                return !doProject;
            }
            var wp = wop.WeldPoint;
            if (wp == null) { rep.Failed++; rep.Errors.Add("[" + SafeName(wop) + "] 没有焊点对象"); return false; }
            try
            {
                var assignments = new List<TxComponent>();
                TxComponent leading = null;
                JArray entries;
                if (bindName.TrimStart().StartsWith("[", StringComparison.Ordinal))
                    entries = JArray.Parse(bindName);
                else
                {
                    // 旧格式优先按完整名称查找，兼容名称本身含 + 的零件。
                    entries = partMap.ContainsKey(bindName) ? new JArray(bindName)
                        : new JArray(bindName.Split('+'));
                }
                foreach (var token in entries)
                {
                    var entry = token as JObject;
                    string name = entry != null ? (string)entry["name"] : (string)token;
                    string path = entry != null ? (string)entry["path"] : null;
                    string key = !string.IsNullOrEmpty(path) ? "@path:" + path : name;
                    TxComponent part;
                    if (string.IsNullOrWhiteSpace(key) || !partMap.TryGetValue(key, out part) || part == null)
                        throw new InvalidOperationException("零件不存在或同名有多份: " + key);
                    if (!assignments.Any(p => p.Id == part.Id)) assignments.Add(part);
                    if (entry != null && (bool?)entry["leading"] == true) leading = part;
                }
                if (assignments.Count == 0) throw new InvalidOperationException("绑定零件列表为空");
                var previousParts = new TxObjectList();
                foreach (ITxObject part in wp.AssignedParts) previousParts.Add(part);
                var previousLeading = wp.LeadingPart;
                try
                {
                    var list = new TxObjectList();
                    foreach (var part in assignments) list.Add(part);
                    wp.ClearAssignedParts();
                    wp.AssignParts(list);
                    wp.LeadingPart = leading ?? assignments[0];
                }
                catch
                {
                    // 赋值失败尽量恢复原绑定；整个批次仍保留 Ctrl+Z 撤销分组。
                    try { wp.ClearAssignedParts(); wp.AssignParts(previousParts); wp.LeadingPart = previousLeading; }
                    catch (Exception restore) { rep.Errors.Add("恢复原绑定失败: " + restore.Message); }
                    throw;
                }
                rep.Bound++;
                if (doProject)
                {
                    try { wop.Project(leading ?? assignments[0], true); rep.Projected++; }
                    catch (Exception ex)
                    {
                        rep.Failed++;
                        string message = "[" + SafeName(wop) + "] 投影失败: " + ex.Message;
                        rep.Errors.Add(message); log("[投影] " + message);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add("[" + SafeName(wop) + "] 绑定失败: " + ex.Message);
                return false;
            }
        }

        private static List<ITxObject> DirectChildren(ITxObject obj)
        {
            var result = new List<ITxObject>();
            try
            {
                var coll = obj as ITxObjectCollection;
                if (coll == null) return result;
                foreach (ITxObject c in (System.Collections.IEnumerable)coll)
                    if (c != null) result.Add(c);
            }
            catch { }
            return result;
        }

        // ── 列布局 / 解析 ──

        private sealed class Columns
        {
            public int WeldOpNameIdx, WeldPointNameIdx, BindPartIdx, XIdx, PathIdx, FormatVersionIdx, MinCols;
        }

        private static Columns DetectColumns(string filePath, Action<string> log)
        {
            try
            {
                foreach (var l in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    var head = l.Split('\t');
                    if (head.Length < 2) continue;
                    var c = new Columns { WeldOpNameIdx = 0, WeldPointNameIdx = -1, BindPartIdx = -1, XIdx = -1, PathIdx = -1, FormatVersionIdx = -1 };
                    for (int i = 0; i < head.Length; i++)
                    {
                        string h = head[i].Trim();
                        if (h == "WeldOpName") c.WeldOpNameIdx = i;
                        else if (h == "WeldPointName") c.WeldPointNameIdx = i;
                        else if (h == "BindPart") c.BindPartIdx = i;
                        else if (h == "X") c.XIdx = i;
                        else if (h == "ParentOpPath") c.PathIdx = i;
                        else if (h == "FormatVersion") c.FormatVersionIdx = i;
                    }
                    if (c.XIdx >= 0 && c.PathIdx >= 0)
                    {
                        c.MinCols = Math.Max(c.PathIdx + 1, c.XIdx + 6);
                        return c;
                    }
                    break;
                }
            }
            catch (Exception ex) { log("[重建] 表头识别失败: " + ex.Message); }
            return null;
        }

        private static List<string[]> ReadRows(string filePath, int minCols, Action<string> log)
        {
            var rows = new List<string[]>();
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (l.StartsWith("WeldOpName\t", StringComparison.Ordinal)) continue;
                var p = l.Split('\t');
                if (p.Length >= minCols) rows.Add(p);
            }
            return rows;
        }

        private static bool TryParseLoc(string[] r, int idx, out double x, out double y,
                                        out double z, out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            double[] v = new double[6];
            var ci = CultureInfo.InvariantCulture;
            for (int i = 0; i < 6; i++)
            {
                if (idx + i >= r.Length) return false;
                if (!double.TryParse(r[idx + i], NumberStyles.Float, ci, out v[i])
                    || double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
            }
            x = v[0]; y = v[1]; z = v[2]; rx = v[3]; ry = v[4]; rz = v[5];
            return true;
        }

        private static bool SetLocation(ITxObject obj, double x, double y, double z,
                                        double rx, double ry, double rz)
        {
            try
            {
                dynamic d = obj;
                d.AbsoluteLocation = TxMath.BuildTransform(x, y, z, rx, ry, rz);
                return true;
            }
            catch { return false; }
        }

        private static bool TrySetName(ITxObject obj, string name)
        {
            try { obj.Name = name; return obj.Name == name; } catch { return false; }
        }

        private static string SafeName(ITxObject obj)
        {
            try { return obj.Name; } catch { return "?"; }
        }

        // ── Undo ──

        private static object OpenUndo(string name, Action<string> log)
        {
            return TxTools.Common.SceneUndoScope.Begin(name);
        }

        private static void CommitUndo(object um, Action<string> log)
        {
            if (um != null) ((TxTools.Common.SceneUndoScope)um).Dispose();
        }

        private static void AbortUndo(object um, Action<string> log)
        {
            if (um != null) ((TxTools.Common.SceneUndoScope)um).Dispose();
            log("[Undo] 执行未完整完成，已关闭撤销分组；如已有部分场景变更，请在当前 PS 工程按 Ctrl+Z 撤销本批次。未自动回滚。");
        }
    }
}
