// TxTools.Agent / Tools / Ps / GunToolHelpers.cs
// 焊枪导出相关工具的共享助手。
//
// 操作解析使用唯一对象标识，参考系冲突必须显式消解。
//
// 只读助手，不直接暴露为工具。

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    /// <summary>操作解析结果：命中的操作 + 来源说明 + 祖先链（从近到远）。</summary>
    internal sealed class OpResolution
    {
        public List<OperationInfo> Operations = new List<OperationInfo>();
        public List<ITxObject> Ancestors = new List<ITxObject>();
        public string SourceDesc = "";
    }

    internal static class GunToolHelpers
    {
        private const int MaxTreeDepth = 12;
        private const int MaxTreeNodes = 20000;

        public static AppearanceRef FindReference(string name, params List<AppearanceRef>[] lists)
        {
            var candidates = lists.Where(l => l != null).SelectMany(l => l).Where(a => a != null &&
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            if (candidates.Count > 1) {
                var first = candidates[0].Matrix;
                if (first == null || candidates.Any(c => c.Matrix == null || c.Matrix.Length != first.Length ||
                    c.Matrix.Where((v, i) => Math.Abs(v - first[i]) > 1e-6).Any()))
                    throw new ArgumentException("参考系名称命中多个不同位姿，请用唯一名称或明确世界系。");
            }
            return candidates.FirstOrDefault();
        }

        public static double[] Identity()
        {
            return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        // ════════════════════════════════════════════════════════════
        //  JSON 取值
        // ════════════════════════════════════════════════════════════

        public static bool Bool(JToken t, bool def)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<bool>(); }
            catch
            {
                var s = ToolInputHelpers.String(t);
                if (string.IsNullOrEmpty(s)) return def;
                s = s.Trim().ToLowerInvariant();
                if (s == "true" || s == "1" || s == "yes" || s == "是") return true;
                if (s == "false" || s == "0" || s == "no" || s == "否") return false;
                return def;
            }
        }

        public static int Int(JToken t, int def)
        {
            if (t == null || t.Type == JTokenType.Null) return def;
            try { return t.Value<int>(); }
            catch
            {
                int v;
                if (int.TryParse(ToolInputHelpers.String(t), out v)) return v;
                return def;
            }
        }

        public static PointType ParsePointFilter(string s)
        {
            if (string.IsNullOrEmpty(s)) return PointType.All;
            switch (s.Trim().ToLowerInvariant())
            {
                case "weld":
                case "weldpoint":
                case "焊点": return PointType.WeldPoint;
                case "path":
                case "pathpoint":
                case "路径点": return PointType.PathPoint;
                case "continuous":
                case "连续点": return PointType.ContinuousPoint;
                case "all": return PointType.All;
                default: throw new ArgumentException("point_filter 只支持 weld/path/continuous/all。");
            }
        }

        public static Action<string> Collector(StringBuilder sb)
        {
            return delegate (string m) { if (!string.IsNullOrEmpty(m)) sb.AppendLine("    " + m); };
        }

        // ════════════════════════════════════════════════════════════
        //  操作解析：选中 → 全局搜索 → 可操作的错误提示
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 解析目标操作。operationName 留空时用 PS 当前选中；
        /// 给了名字但选中里没有时，自动到 OperationRoot 全树搜索并带回祖先链。
        /// </summary>
        public static OpResolution ResolveOperations(string operationName,
            Action<string> log, out string error, string objectId = null)
        {
            error = null;
            var res = new OpResolution();

            // ── 1. 当前选中 ──
            List<OperationInfo> fromSel = null;
            try { fromSel = PsReader.GetOperationsFromSelection(log); }
            catch (Exception ex) { log("读取选中异常: " + ex.Message); }

            if (string.IsNullOrWhiteSpace(operationName) && string.IsNullOrWhiteSpace(objectId))
            {
                if (fromSel == null || fromSel.Count == 0)
                {
                    error = "未获取到任何操作。请在 PS 中选中焊接操作后重试，"
                          + "或传 operation_name 指定操作名（会全局搜索）。";
                    return null;
                }
                res.Operations = fromSel;
                res.SourceDesc = "PS 当前选中";
                return res;
            }

            ITxObject node;
            if (!TxTools.Agent.Ps.PsBridge.TryResolve(operationName, objectId, out node, out error)) return null;
            if (!(node is ITxOperation)) { error = "指定对象不是操作。"; return null; }
            var operations = PsReader.ParsePickedObjectToOperations(node);
            if (operations != null && operations.Count > 0) {
                res.Operations = operations;
                res.SourceDesc = "唯一操作: " + TxTools.Agent.Ps.PsBridge.Ref(node);
                var current = node.Collection as ITxObject;
                var seen = new HashSet<string>();
                while (current != null && seen.Add(current.Id)) { res.Ancestors.Add(current); current = current.Collection as ITxObject; }
                return res;
            }

            // ── 4. 找不到：给出可操作的提示 ──
            error = BuildNotFoundError(operationName, fromSel);
            return null;
        }

        private static List<OperationInfo> MatchByName(List<OperationInfo> all, string name)
        {
            var exact = new List<OperationInfo>();
            var partial = new List<OperationInfo>();
            foreach (var op in all)
            {
                if (op == null || op.Name == null) continue;
                if (string.Equals(op.Name, name, StringComparison.OrdinalIgnoreCase)) exact.Add(op);
                else if (op.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(op);
            }
            return exact.Count > 0 ? exact : partial;
        }

        private static string BuildNotFoundError(string name, List<OperationInfo> fromSel)
        {
            var sb = new StringBuilder();
            sb.Append("未找到名为 \"").Append(name).Append("\" 的操作（当前选中和整棵操作树都已查过）。\n");

            if (fromSel != null && fromSel.Count > 0)
            {
                sb.Append("当前 PS 选中解析出的操作：");
                for (int i = 0; i < fromSel.Count && i < 8; i++) sb.Append("\n  - ").Append(fromSel[i].Name);
                if (fromSel.Count > 8) sb.Append("\n  ...（共 ").Append(fromSel.Count).Append(" 个）");
                sb.Append('\n');
            }

            var near = FindSimilarInTree(name, 10);
            if (near.Count > 0)
            {
                sb.Append("操作树中名字相近的：");
                foreach (var s in near) sb.Append("\n  - ").Append(s);
                sb.Append('\n');
            }

            sb.Append("建议：留空 operation_name 直接用当前选中，或从上面的名字里挑一个重试。");
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════
        //  操作树遍历
        // ════════════════════════════════════════════════════════════

        private static ITxObject GetOperationRoot()
        {
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null) return null;
                return doc.OperationRoot as ITxObject;
            }
            catch { return null; }
        }

        private static TxObjectList Kids(ITxObject node)
        {
            if (node == null) return null;
            try
            {
                var f = new TxTypeFilter(typeof(ITxObject));
                dynamic d = node;
                return d.GetDirectDescendants(f) as TxObjectList;
            }
            catch { return null; }
        }

        private static string SafeName(ITxObject o)
        {
            try { return o != null ? o.Name : null; } catch { return null; }
        }

        /// <summary>在操作树中按名字查找（先精确后包含），并回填祖先链（从近到远）。</summary>
        private static ITxObject FindInOperationTree(string name, out List<ITxObject> ancestors)
        {
            ancestors = new List<ITxObject>();
            ITxObject root = GetOperationRoot();
            if (root == null || string.IsNullOrEmpty(name)) return null;

            for (int pass = 0; pass < 2; pass++)   // 第一轮精确，第二轮包含
            {
                var path = new List<ITxObject>();
                int budget = MaxTreeNodes;
                ITxObject found = Walk(root, name, pass == 0, path, 0, ref budget);
                if (found != null)
                {
                    path.Reverse();   // 从根到父 → 从近到远
                    ancestors = path;
                    return found;
                }
            }
            return null;
        }

        private static ITxObject Walk(ITxObject node, string name, bool exact,
            List<ITxObject> path, int depth, ref int budget)
        {
            if (node == null || depth > MaxTreeDepth || budget <= 0) return null;
            budget--;

            string nm = SafeName(node);
            if (!string.IsNullOrEmpty(nm) && depth > 0)
            {
                bool hit = exact
                    ? string.Equals(nm, name, StringComparison.OrdinalIgnoreCase)
                    : nm.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
                if (hit) return node;
            }

            TxObjectList kids = Kids(node);
            if (kids == null) return null;

            path.Add(node);
            foreach (ITxObject child in kids)
            {
                ITxObject r = Walk(child, name, exact, path, depth + 1, ref budget);
                if (r != null) return r;
                if (budget <= 0) break;
            }
            path.RemoveAt(path.Count - 1);
            return null;
        }

        /// <summary>收集操作树中名字相近的对象名（用于报错提示）。</summary>
        private static List<string> FindSimilarInTree(string name, int max)
        {
            var result = new List<string>();
            ITxObject root = GetOperationRoot();
            if (root == null || string.IsNullOrEmpty(name)) return result;

            // 用名字里最长的字母数字片段做模糊键，避免全表返回
            string key = LongestToken(name);
            if (key.Length < 3) key = name;

            int budget = MaxTreeNodes;
            CollectSimilar(root, key, result, max, 0, ref budget);
            return result;
        }

        private static void CollectSimilar(ITxObject node, string key, List<string> outList,
            int max, int depth, ref int budget)
        {
            if (node == null || depth > MaxTreeDepth || budget <= 0 || outList.Count >= max) return;
            budget--;

            string nm = SafeName(node);
            if (!string.IsNullOrEmpty(nm) && depth > 0
                && nm.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string entry = nm + " [" + node.GetType().Name + "]";
                if (!outList.Contains(entry))
                {
                    outList.Add(entry);
                    if (outList.Count >= max) return;
                }
            }

            TxObjectList kids = Kids(node);
            if (kids == null) return;
            foreach (ITxObject child in kids)
            {
                CollectSimilar(child, key, outList, max, depth + 1, ref budget);
                if (outList.Count >= max || budget <= 0) return;
            }
        }

        private static string LongestToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string best = "", cur = "";
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) cur += c;
                else { if (cur.Length > best.Length) best = cur; cur = ""; }
            }
            if (cur.Length > best.Length) best = cur;
            return best;
        }

        // ════════════════════════════════════════════════════════════
        //  机器人定位：操作本身没有就沿祖先链往上找
        //  （TxWeldOperation 没有 Parent 属性，只能靠遍历时记下的祖先链）
        // ════════════════════════════════════════════════════════════

        public static TxRobot FindRobot(OperationInfo op, List<ITxObject> ancestors, out string via)
        {
            via = null;
            if (op != null && op.PsObject != null)
            {
                try
                {
                    TxRobot r = PsReader.FindRobotForOperation(op.PsObject);
                    if (r != null) { via = op.Name; return r; }
                }
                catch { }
            }
            if (ancestors == null) return null;

            foreach (ITxObject anc in ancestors)
            {
                if (anc == null) continue;
                try
                {
                    TxRobot r = PsReader.FindRobotForOperation(anc);
                    if (r != null) { via = SafeName(anc) + "(父级)"; return r; }
                }
                catch { }
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  LeadingPart 兜底（PsReader 的候选属性表未覆盖）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 在操作子树里探测 LeadingPart，作为参考坐标的补充候选。
        /// 真实场景中焊点常只有 LeadingPart 而无 WeldedAppearances，
        /// 此时 ResolveOperationRefFrame 会回退世界系 —— 这里把它补回来。
        /// </summary>
        public static List<AppearanceRef> CollectLeadingParts(OperationInfo op, int maxDepth = 3)
        {
            var list = new List<AppearanceRef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (op == null || op.PsObject == null) return list;

            int budget = 400;
            ProbeLeadingPart(op.PsObject, list, seen, 0, maxDepth, ref budget);
            return list;
        }

        private static void ProbeLeadingPart(ITxObject node, List<AppearanceRef> list,
            HashSet<string> seen, int depth, int maxDepth, ref int budget)
        {
            if (node == null || depth > maxDepth || budget <= 0) return;
            budget--;

            AddLeadingPart(TryGetMember(node, "LeadingPart"), list, seen);

            // 焊点还挂在 WeldLocationOperations 上，那里也可能带 LeadingPart
            object wlo = TryGetMember(node, "WeldLocationOperations");
            IEnumerable we = wlo as IEnumerable;
            if (we != null)
            {
                foreach (object o in we)
                {
                    if (budget <= 0) break;
                    budget--;
                    AddLeadingPart(TryGetMember(o, "LeadingPart"), list, seen);
                }
            }

            if (depth >= maxDepth) return;
            TxObjectList kids = Kids(node);
            if (kids == null) return;
            foreach (ITxObject child in kids)
            {
                ProbeLeadingPart(child, list, seen, depth + 1, maxDepth, ref budget);
                if (budget <= 0) return;
            }
        }

        private static void AddLeadingPart(object lp, List<AppearanceRef> list, HashSet<string> seen)
        {
            if (lp == null) return;

            TxTransformation tx = null;
            try { dynamic d = lp; tx = d.AbsoluteLocation as TxTransformation; } catch { }
            if (tx == null) try { dynamic d = lp; tx = d.LocationRelativeToWorld as TxTransformation; } catch { }
            if (tx == null) return;

            string name = null;
            try { dynamic d = lp; name = d.Name as string; } catch { }
            if (string.IsNullOrEmpty(name)) return;

            double[] m = PsReader.TxToArr(tx);
            string key = name + "|" + m[3].ToString("F3") + "," + m[7].ToString("F3") + "," + m[11].ToString("F3");
            if (!seen.Add(key)) return;

            list.Add(new AppearanceRef
            {
                Name = name,
                Matrix = m,
                TypeName = lp.GetType().Name + "(LeadingPart)",
                RawObject = lp as ITxObject
            });
        }

        private static object TryGetMember(object obj, string memberName)
        {
            if (obj == null || string.IsNullOrEmpty(memberName)) return null;
            try
            {
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                var pi = obj.GetType().GetProperty(memberName, flags);
                if (pi != null && pi.CanRead) return pi.GetValue(obj);
            }
            catch { }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  矩阵格式化
        // ════════════════════════════════════════════════════════════

        public static string FmtMatrix(double[] m)
        {
            if (m == null || m.Length < 16) return "(空)";
            double rx, ry, rz;
            PsReader.MatrixToEulerDeg(m, out rx, out ry, out rz);
            return string.Format("X={0:F3} Y={1:F3} Z={2:F3}  Rx={3:F3} Ry={4:F3} Rz={5:F3}",
                m[3], m[7], m[11], rx, ry, rz);
        }

        public static string FmtPos(double[] m)
        {
            if (m == null || m.Length < 12) return "(空)";
            return string.Format("X={0:F3} Y={1:F3} Z={2:F3}", m[3], m[7], m[11]);
        }

        /// <summary>输出原始 4x4（行主序），供需要精确数值时使用，免得调用方去写脚本手算。</summary>
        public static string FmtRaw4x4(double[] m, string indent)
        {
            if (m == null || m.Length < 16) return indent + "(空)";
            var sb = new StringBuilder();
            for (int r = 0; r < 4; r++)
            {
                sb.Append(indent).Append('[');
                for (int c = 0; c < 4; c++)
                    sb.Append(string.Format("{0,12:F6}", m[r * 4 + c]));
                sb.Append(" ]");
                if (r < 3) sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
