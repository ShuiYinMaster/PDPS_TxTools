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
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.DataTypes;

namespace TxTools.CrossEnvIO
{
    public sealed class WeldPointReport
    {
        public int Exported, Created, Bound, Projected, Skipped, Failed, Rebuilt;
        public readonly List<string> Errors = new List<string>();
        public override string ToString()
            => $"重建 {Created}（更新位置 {Rebuilt} · 绑定 {Bound} · 投影 {Projected}）· 跳过 {Skipped} · 失败 {Failed}";
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
                                           HashSet<string> selectedNames = null)
        {
            log = log ?? Nop;
            var doc = TxApplication.ActiveDocument;
            if (doc == null) { log("[导出] 无活动文档"); return 0; }
            var opRoot = doc.OperationRoot;
            if (opRoot == null) { log("[导出] OperationRoot 为空"); return 0; }

            var sb = new StringBuilder();
            sb.AppendLine("WeldOpName\tWeldPointName\tBindPart\tX\tY\tZ\tRX\tRY\tRZ\tParentOpPath");

            int count = 0;
            WalkOps(opRoot, "", (op, path) =>
            {
                var wop = op as TxWeldLocationOperation;
                if (wop == null) return;
                // 多选过滤：选中了操作则只导出选中的
                if (selectedNames != null && selectedNames.Count > 0 && !selectedNames.Contains(wop.Name))
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
                    string bind = GetBindPart(wp);

                    sb.Append(wop.Name).Append('\t')
                      .Append(wp != null ? wp.Name : "").Append('\t')
                      .Append(bind).Append('\t')
                      .Append(x.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(y.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(z.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rx.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(ry.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(rz.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                      .Append(path)
                      .AppendLine();
                    count++;
                }
                catch (Exception ex) { log("[导出] " + SafeName(wop) + " 行异常: " + ex.Message); }
            });

            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
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
                catch { }

                bool isContainer = cur.GetType().Name.IndexOf("Compound", StringComparison.Ordinal) >= 0
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
                        string childPath = curPath.Length == 0 ? c.Name : curPath + "/" + c.Name;
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

        private static string GetBindPart(TxWeldPoint wp)
        {
            if (wp == null) return "";
            try
            {
                var parts = wp.AssignedParts;
                if (parts != null && parts.Count > 0)
                {
                    var names = new List<string>();
                    foreach (ITxObject p in parts)
                        try { names.Add(p.Name); } catch { }
                    return string.Join("+", names);
                }
                var lead = wp.LeadingPart as ITxObject;
                if (lead != null) return lead.Name;
            }
            catch { }
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
                    { rep.Skipped++; continue; }
                    string rawPath = r[cols.PathIdx];

                    try
                    {
                        if (bindOnly)
                        {
                            if (TryBindExisting(opRoot, weldName, bindName, partMap, doProject, rep))
                                rep.Bound++;
                            continue;
                        }

                        string groupPath = NormalizePath(rawPath);
                        // 兼容旧格式：路径最后一段是焊点名时去掉（旧导出 ParentOpPath 含焊点名，
                        // 新导出不含 —— 焊点应挂在父操作如 Weld_Op 下，而不是为焊点名建 CompOp）。
                        if (groupPath.EndsWith("/" + weldName, StringComparison.Ordinal)
                            || string.Equals(groupPath, weldName, StringComparison.Ordinal))
                        {
                            int last = groupPath.LastIndexOf('/');
                            groupPath = last > 0 ? groupPath.Substring(0, last) : "";
                        }
                        var group = FindOrCreateGroup(opRoot, opIndex, groupPath, log);
                        if (group == null) { rep.Skipped++; log("[重建] 无目标复合操作: " + rawPath); continue; }

                        // 已存在同名焊点 → 只更新位置/绑定
                        var existing = FindOpByName(group, weldName);
                        if (existing != null)
                        {
                            if (SetLocation(existing, x, y, z, rx, ry, rz)) rep.Rebuilt++;
                            TryBind(existing, bindName, partMap, doProject, rep);
                            rep.Created++;
                            continue;
                        }

                        var wpData = TxMfgCreationDataFactory.CreateWeldPointCreationData(weldName,
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
                        TryBind(wop, bindName, partMap, doProject, rep);
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
            var stack = new Stack<KeyValuePair<ITxObject, string>>();
            try
            {
                foreach (ITxObject c in (System.Collections.IEnumerable)opRoot)
                {
                    bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                    if (isOp) stack.Push(new KeyValuePair<ITxObject, string>(c, c.Name));
                }
            }
            catch { }
            while (stack.Count > 0)
            {
                var kv = stack.Pop();
                if (!map.ContainsKey(kv.Key.Name) && !map.ContainsKey(kv.Value))
                    map[kv.Value] = kv.Key;
                try
                {
                    foreach (ITxObject c in (System.Collections.IEnumerable)kv.Key)
                    {
                        bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                        if (isOp)
                            stack.Push(new KeyValuePair<ITxObject, string>(c, kv.Value + "/" + c.Name));
                    }
                }
                catch { }
            }
            return map;
        }

        private static ITxObject FindOrCreateGroup(TxOperationRoot opRoot,
            Dictionary<string, ITxObject> opIndex, string path, Action<string> log)
        {
            if (string.IsNullOrEmpty(path)) return null;
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
                string seg = segs[i];
                acc = acc.Length == 0 ? seg : acc + "/" + seg;
                bool isLast = (i == total - 1);

                ITxObject found = FindChildOp(cur, seg);
                if (found == null)
                {
                    if (isLast)
                        found = CreateWeldOp(cur, seg);   // 最终段：WeldOperation（焊接操作，焊点父）
                    else
                        found = CreateCompound(cur, new TxCompoundOperationCreationData());
                    if (found == null) return null;
                    TrySetName(found, seg);
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
            try
            {
                foreach (ITxObject c in (System.Collections.IEnumerable)parent)
                {
                    if (c == null) continue;
                    bool isOp = c is TxCompoundOperation || c is TxWeldOperation;
                    if (isOp && string.Equals(c.Name, name, StringComparison.Ordinal))
                        return c;
                }
            }
            catch { }
            return null;
        }

        private static TxWeldLocationOperation FindOpByName(ITxObject group, string name)
        {
            try
            {
                foreach (ITxObject c in (System.Collections.IEnumerable)group)
                {
                    var wop = c as TxWeldLocationOperation;
                    if (wop != null && string.Equals(wop.Name, name, StringComparison.Ordinal))
                        return wop;
                }
            }
            catch { }
            return null;
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
            // 去掉首段重复（T18FL4 布局: 首段出现两次）
            var segs = p.Split('/').Where(s => s.Length > 0).ToList();
            if (segs.Count >= 2 && string.Equals(segs[0], segs[1], StringComparison.Ordinal))
                segs.RemoveAt(0);
            return string.Join("/", segs);
        }

        // ── 绑定 / 投影 ──

        private static Dictionary<string, TxComponent> BuildPartMap(Action<string> log)
        {
            var map = new Dictionary<string, TxComponent>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return map;
                var stack = new Stack<ITxObject>();
                foreach (var c in DirectChildren(doc.PhysicalRoot)) stack.Push(c);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (cur == null) continue;
                    try
                    {
                        var comp = cur as TxComponent;
                        if (comp != null && !comp.Name.Contains("FeatureLine") && !map.ContainsKey(comp.Name))
                            map[comp.Name] = comp;
                        var coll = cur as ITxObjectCollection;
                        if (coll != null)
                            foreach (var c in DirectChildren(cur)) stack.Push(c);
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        private static void TryBind(TxWeldLocationOperation wop, string bindName,
            Dictionary<string, TxComponent> partMap, bool doProject, WeldPointReport rep,
            Action<string> log = null)
        {
            log = log ?? Nop;
            if (string.IsNullOrWhiteSpace(bindName)) return;
            var wp = wop.WeldPoint;
            if (wp == null) return;
            TxComponent part;
            if (!partMap.TryGetValue(bindName, out part))
            {
                rep.Failed++;
                rep.Errors.Add("[" + SafeName(wop) + "] 未找到零件: " + bindName);
                return;
            }
            try
            {
                wp.ClearAssignedParts();
                var list = new TxObjectList();
                list.Add(part);
                wp.AssignParts(list);
                rep.Bound++;
            }
            catch (Exception ex)
            {
                rep.Failed++;
                rep.Errors.Add("[" + SafeName(wop) + "] 绑定失败(" + bindName + "): " + ex.Message);
                return;
            }
            if (doProject)
            {
                try
                {
                    wop.Project(part, true);
                    rep.Projected++;
                }
                catch (Exception ex)
                {
                    log("[投影] " + SafeName(wop) + " 投影失败: " + ex.Message);
                }
            }
        }

        private static bool TryBindExisting(TxOperationRoot opRoot, string weldName, string bindName,
            Dictionary<string, TxComponent> partMap, bool doProject, WeldPointReport rep)
        {
            TxWeldLocationOperation found = null;
            var stack = new Stack<ITxObject>();
            foreach (var c in DirectChildren(opRoot)) stack.Push(c);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                var wop = cur as TxWeldLocationOperation;
                if (wop != null)
                {
                    if (string.Equals(wop.Name, weldName, StringComparison.Ordinal)) { found = wop; break; }
                }
                var coll = cur as ITxObjectCollection;
                if (coll != null) foreach (var c in DirectChildren(cur)) stack.Push(c);
            }
            if (found == null) return false;
            TryBind(found, bindName, partMap, doProject, rep);
            return true;
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
            public int WeldOpNameIdx, BindPartIdx, XIdx, PathIdx, MinCols;
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
                    var c = new Columns { WeldOpNameIdx = 0, BindPartIdx = -1, XIdx = -1, PathIdx = -1 };
                    for (int i = 0; i < head.Length; i++)
                    {
                        string h = head[i].Trim();
                        if (h == "WeldOpName") c.WeldOpNameIdx = i;
                        else if (h == "WeldPointName") { /* 略过，T18FL4 无此列 */ }
                        else if (h == "BindPart") c.BindPartIdx = i;
                        else if (h == "X") c.XIdx = i;
                        else if (h == "ParentOpPath") c.PathIdx = i;
                    }
                    if (c.XIdx >= 0 && c.PathIdx >= 0)
                    {
                        c.MinCols = Math.Max(c.PathIdx + 1, 6);
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
                if (!double.TryParse(r[idx + i], NumberStyles.Float, ci, out v[i])) return false;
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

        private static void TrySetName(ITxObject obj, string name)
        {
            try { obj.Name = name; } catch { }
        }

        private static string SafeName(ITxObject obj)
        {
            try { return obj.Name; } catch { return "?"; }
        }

        // ── Undo ──

        private static object OpenUndo(string name, Action<string> log)
        {
            try
            {
                dynamic um = TxApplication.ActiveUndoManager;
                if (um == null) return null;
                try { um.OpenUndoTransaction(name); return um; } catch { }
                try { um.OpenTransaction(name); return um; } catch { }
                try { um.StartTransaction(name); return um; } catch { }
                try { um.BeginUndoTransaction(name); return um; } catch { }
            }
            catch { }
            log("[Undo] 未开启事务（PS 仍可 Ctrl+Z）");
            return null;
        }

        private static void CommitUndo(object um, Action<string> log)
        {
            if (um == null) return;
            try { dynamic d = um; try { d.CommitUndoTransaction(); return; } catch { } try { d.CommitTransaction(); return; } catch { } try { d.Commit(); return; } catch { } } catch { }
        }

        private static void AbortUndo(object um, Action<string> log)
        {
            if (um == null) return;
            try { dynamic d = um; try { d.AbortUndoTransaction(); return; } catch { } try { d.AbortTransaction(); return; } catch { } try { d.Rollback(); return; } catch { } } catch { }
            log("[Undo] 已尝试回滚");
        }
    }
}
