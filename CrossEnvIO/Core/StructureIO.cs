// StructureIO.cs  --  C# 8.0
// 跨环境目录结构 + 位姿导出/重建（资源树 TxCompoundResource / 零件树 TxCompoundPart）。
//
// 数据格式（TSV，首行头）:
//   资源树  : Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ
//   零件树  : 同上 + \tCOJT（组件库路径，导出时记录，重建时仅核对日志）
// ParentPath = 该对象「从导出根算起的自身路径」（/ 分隔，根行 = 根名）。
//
// 重建流程（与对话验证流程一致）:
//   1. 按 ParentPath 深度排序（父先子后）
//   2. 容器行（Type 含 Compound）→ 在父容器下按名字找/建 TxCompoundResource / TxCompoundPart
//   3. 叶子行（TxComponent 等）→ 按名字全场景查找 → AddObject 移入父容器
//   4. 每行设 AbsoluteLocation = RPY_ZYX 重建矩阵

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tecnomatix.Engineering;

namespace TxTools.CrossEnvIO
{
    public sealed class StructureReport
    {
        public int ExportRows, Rebuilt, Created, Moved, Skipped, Failed;
        public readonly List<string> Errors = new List<string>();
        public override string ToString()
            => $"重建 {Rebuilt}（新建 {Created} · 移动 {Moved}）· 跳过 {Skipped} · 失败 {Failed}";
    }

    public static class StructureIO
    {
        private static void Nop(string s) { }
        private const string Fmt = "0.###";

        // ════════════════════════════════════════════════════════════
        //  导出
        // ════════════════════════════════════════════════════════════

        /// <summary>导出 root 及其下所有对象的目录结构 + 位姿到 TSV。</summary>
        public static int ExportStructure(ITxObject root, string filePath,
                                          bool includeCojt, Action<string> log)
        {
            return ExportStructureMulti(
                root != null ? new List<ITxObject> { root } : new List<ITxObject>(),
                filePath, includeCojt, log);
        }

        /// <summary>
        /// 导出多个根（每个根遍历其复合资源子集）到同一 TSV。写入表头一次。
        /// </summary>
        public static int ExportStructureMulti(List<ITxObject> roots, string filePath,
                                               bool includeCojt, Action<string> log)
        {
            log = log ?? Nop;
            if (roots == null || roots.Count == 0) { log("[导出] 未选择导出根"); return 0; }
            if (string.IsNullOrWhiteSpace(filePath)) { log("[导出] 文件路径为空"); return 0; }

            var sb = new StringBuilder();
            sb.AppendLine(includeCojt
                ? "Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ\tCOJT"
                : "Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ");

            int count = 0;
            // treeKind=ROOT：根自身，下钻时按子节点类型分流 RES/PART
            foreach (var root in roots)
            {
                if (root == null) continue;
                WriteNode(sb, root, root.Name ?? root.GetType().Name, "ROOT", includeCojt, ref count, log);
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            log("[导出] ✓ 已写入 " + count + " 行（" + roots.Count + " 个导出根）→ " + filePath);
            return count;
        }

        /// <summary>
        /// 从导出 TSV 读取 COJT 列（第 10 列），返回所有非空 cojt 绝对路径（去重）。
        /// 用于「复制 cojt」与导出结构保持一致的范围。
        /// </summary>
        public static List<string> ReadCojtList(string filePath, Action<string> log)
        {
            log = log ?? Nop;
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("Name\t", StringComparison.Ordinal)) continue;
                    var p = line.Split('\t');
                    // COJT 在索引 9（Name/Type/ParentPath/X/Y/Z/RX/RY/RZ/COJT）
                    if (p.Length > 9)
                    {
                        string cojt = (p[9] ?? "").Trim();
                        if (cojt.Length > 0 && seen.Add(cojt))
                            list.Add(cojt);
                    }
                }
            }
            catch (Exception ex) { log("[读取cojt] 异常: " + ex.Message); }
            log("[读取cojt] 从导出文件读到 " + list.Count + " 个 cojt");
            return list;
        }

        /// <summary>
        /// 递归写节点。treeKind: "ROOT"(根) / "RES"(资源树) / "PART"(零件树)。
        /// 资源树(TxCompoundResource)与零件树(TxCompoundPart)在 PhysicalRoot 下可能是
        /// 同名容器，用 R:/P: 前缀区分路径，避免重建时资源/零件路径互相覆盖导致父容器缺失。
        /// </summary>
        private static void WriteNode(StringBuilder sb, ITxObject obj, string rawPath,
                                      string treeKind, bool includeCojt, ref int count,
                                      Action<string> log)
        {
            // 排除制造特征（焊点 TxWeldPoint 等）：它们挂在 PhysicalRoot 下，不属于资源/零件
            // 结构，由 WeldPointIO 单独导出/重建。若不排除，会被误当成叶子导出，
            // 重建时因未插入而报"未找到叶子"。
            if (IsMfgFeature(obj)) return;

            try
            {
                // 确定本节点所属树类型：资源容器→RES，零件容器→PART，其余继承父
                string kind = treeKind;
                if (obj is TxCompoundResource) kind = "RES";
                else if (obj is TxCompoundPart) kind = "PART";

                string path = rawPath;
                if (kind == "RES") path = "R:" + rawPath;
                else if (kind == "PART") path = "P:" + rawPath;

                double x, y, z, rx, ry, rz;
                GetLocation(obj, out x, out y, out z, out rx, out ry, out rz);
                string cojt = "";
                if (includeCojt) cojt = GetCojtPath(obj);
                sb.Append(obj.Name).Append('\t')
                  .Append(obj.GetType().Name).Append('\t')
                  .Append(path).Append('\t')
                  .Append(x.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(y.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(z.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(rx.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(ry.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(rz.ToString(Fmt, CultureInfo.InvariantCulture));
                if (includeCojt) sb.Append('\t').Append(cojt);
                sb.AppendLine();
                count++;
            }
            catch (Exception ex) { log("[导出] " + rawPath + " 行异常: " + ex.Message); }

            // 只下钻容器型节点（TxCompoundResource / TxCompoundPart / TxPhysicalRoot）
            bool isRes = obj is TxCompoundResource;
            bool isPart = obj is TxCompoundPart;
            bool isPhysRoot = obj is TxPhysicalRoot;
            if (!isRes && !isPart && !isPhysRoot) return;
            var coll = obj as ITxObjectCollection;
            if (coll == null) return;
            try
            {
                var children = new List<ITxObject>();
                foreach (ITxObject c in (System.Collections.IEnumerable)coll)
                    children.Add(c);
                // 子级继承当前树类型（资源容器的子仍是资源树，零件容器的子仍是零件树）
                string childKind = treeKind;
                if (obj is TxCompoundResource) childKind = "RES";
                else if (obj is TxCompoundPart) childKind = "PART";
                foreach (var c in children)
                    WriteNode(sb, c, rawPath + "/" + c.Name, childKind, includeCojt, ref count, log);
            }
            catch (Exception ex) { log("[导出] 遍历子级异常: " + ex.Message); }
        }

        // ════════════════════════════════════════════════════════════
        //  重建
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 从 TSV 重建结构到 targetRoot 之下。isResource=true 建 TxCompoundResource，
        /// =false 建 TxCompoundPart（setTypeName=false，保标准 CompoundPart）。
        /// </summary>
        public static StructureReport RebuildStructure(string filePath, ITxObject targetRoot,
                                                       bool isResource, Action<string> log,
                                                       double originX = 0, double originY = 0,
                                                       double originZ = 0)
        {
            log = log ?? Nop;
            var rep = new StructureReport();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            { rep.Failed++; rep.Errors.Add("文件不存在: " + filePath); return rep; }
            if (targetRoot == null) { rep.Failed++; rep.Errors.Add("目标容器为空"); return rep; }

            List<string[]> rows;
            try { rows = ReadRows(filePath, log); }
            catch (Exception ex) { rep.Failed++; rep.Errors.Add("读取失败: " + ex.Message); return rep; }
            if (rows.Count == 0) { log("[重建] 无数据行"); return rep; }

            // 深度排序（父先子后），容器行优先于同深度叶子
            var ordered = rows
                .Select(r => new { Row = r, Depth = r[2].Count(c => c == '/') })
                .OrderBy(o => o.Depth)
                .ThenBy(o => IsCompound(o.Row[1]) ? 0 : 1)
                .ToList();

            object um = OpenUndo(isResource ? "跨环境重建资源树" : "跨环境重建零件树", log);
            try
            {
                // 路径 -> 容器对象。key 用含 R:/P: 前缀的完整路径，
                // 使资源树与零件树（PhysicalRoot 下可能同名容器）不互相覆盖。
                var pathToObj = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
                pathToObj[targetRoot.Name ?? ""] = targetRoot;

                // 导出根名：TSV 里 parentPath 为空的那行（导出根对象自己）的 Name。
                // 跨环境时导出端根名(VAN)与对端根名(test)可能不同，父路径 R:VAN 需按此映射到对端根。
                string exportRootName = targetRoot.Name;
                foreach (var r0 in rows)
                {
                    if (string.IsNullOrEmpty(GetParentPath(r0[2])))
                    { exportRootName = r0[0]; break; }
                }

                foreach (var o in ordered)
                {
                    var r = o.Row;
                    if (r.Length < 9) { rep.Skipped++; continue; }
                    string ownPath = r[2];

                    // 按目标树类型过滤：资源页(isResource)只建 R: 资源树，零件页只建 P: 零件树。
                    // 根行/无前缀行(如 VAN)两种模式都保留（根行随后跳过）。
                    if (isResource && ownPath.StartsWith("P:", StringComparison.Ordinal))
                    { rep.Skipped++; continue; }
                    if (!isResource && ownPath.StartsWith("R:", StringComparison.Ordinal))
                    { rep.Skipped++; continue; }

                    string parentPath = GetParentPath(ownPath);
                    double x, y, z, rx, ry, rz;
                    if (!TryParseLoc(r, 3, out x, out y, out z, out rx, out ry, out rz))
                    { rep.Skipped++; continue; }

                    ITxObject parent;
                    // ── 父解析 ──
                    // parentPath 为空 → 导出树的根（导出根对象自己，如 R:ANV 或 VAN）。
                    if (string.IsNullOrEmpty(parentPath))
                    {
                        if (r[1].IndexOf("PhysicalRoot", StringComparison.Ordinal) >= 0)
                        {
                            pathToObj[ownPath] = targetRoot;
                            rep.Skipped++;
                            continue;
                        }
                        parent = targetRoot;
                    }
                    else
                    {
                        // 父路径去掉前缀等于导出根名(R:VAN/P:VAN → VAN) → 父就是对端根
                        string parentStrip = StripTreePrefix(parentPath);
                        if (!string.IsNullOrEmpty(exportRootName)
                            && string.Equals(parentStrip, exportRootName, StringComparison.Ordinal))
                            parent = targetRoot;
                        else if (!pathToObj.TryGetValue(parentPath, out parent))
                        {
                            rep.Skipped++;
                            log("[重建] 父容器缺失: " + parentPath);
                            continue;
                        }
                    }

                    try
                    {
                        if (IsCompound(r[1]))
                        {
                            var container = EnsureContainer(parent, r[0], r[1], log);
                            if (container == null) { rep.Failed++; rep.Errors.Add("建容器失败: " + ownPath); continue; }
                            pathToObj[ownPath] = container;
                            if (SetLocation(container, x - originX, y - originY, z - originZ, rx, ry, rz)) rep.Rebuilt++;
                            rep.Created++;
                        }
                        else
                        {
                            // 叶子：① 父容器直接子同名 → 复用；② 场景其它位置同名 → 复制新实例（不移动原件，
                            // 避免重名资源场景只剩一份）；③ 都没有 → 用 COJT 列自动插入。
                            var leaf = FindOrCloneLeaf(parent, r[0], r[1], ownPath,
                                                       r.Length > 9 ? (r[9] ?? "").Trim() : "",
                                                       x, y, z, log);
                            if (leaf == null)
                            {
                                rep.Skipped++;
                                log("[重建] ✗ 未找到叶子且复制/自动插入失败: " + ownPath
                                    + "（类型 " + r[1] + "）"
                                    + "—— 需先在库浏览器拖入组件，或检查 cojt 是否已复制到对端库根");
                                continue;
                            }
                            if (MoveInto(parent, leaf)) rep.Moved++;
                            else log("[重建] ⚠ 移动叶子失败: " + ownPath + " → " + (SafeName(parent)));
                            if (SetLocation(leaf, x - originX, y - originY, z - originZ, rx, ry, rz)) rep.Rebuilt++;
                        }
                    }
                    catch (Exception ex)
                    {
                        rep.Failed++;
                        rep.Errors.Add("[" + ownPath + "] " + ex.Message);
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

        // ── 容器 / 叶子 ──

        private static ITxObject EnsureContainer(ITxObject parent, string name, string type,
                                                 Action<string> log)
        {
            bool wantPart = type.IndexOf("CompoundPart", StringComparison.Ordinal) >= 0;

            // 已有同名且同类型子容器则复用（资源 ANV 与零件 ANV 同名，必须校验类型，
            // 否则零件树会错误复用资源容器，导致层级错乱）
            foreach (var c in DirectChildren(parent))
            {
                if (!string.Equals(c.Name, name, StringComparison.Ordinal)) continue;
                bool isPart = c is TxCompoundPart;
                if (isPart == wantPart) return c;
            }
            try
            {
                // 用带 name 的构造函数直接创建（参考 Siemens 官方样例：new TxCompoundResourceCreationData(name)），
                // 比 PsCompoundHelper 的无参构造 + 反射 TryRename 可靠 —— TryRename 失败会导致容器名字不对/创建失败。
                if (wantPart)
                {
                    var cd = new TxCompoundPartCreationData(name);
                    var creator = parent as ITxCompoundPartCreation;
                    if (creator == null)
                    {
                        log("[重建] 父对象不支持创建 CompoundPart: " + parent.GetType().Name);
                        return null;
                    }
                    var cp = creator.CreateCompoundPart(cd);
                    return cp as ITxObject;
                }
                else
                {
                    var cd = new TxCompoundResourceCreationData(name);
                    var creator = parent as ITxCompoundResourceCreation;
                    if (creator == null)
                    {
                        log("[重建] 父对象不支持创建 CompoundResource: " + parent.GetType().Name);
                        return null;
                    }
                    var cr = creator.CreateCompoundResource(cd);
                    return cr as ITxObject;
                }
            }
            catch (Exception ex)
            {
                log("[重建] 建容器异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 查找叶子（区分父容器直接子 vs 全场景），找不到再用 COJT 列自动插入。
        /// 关键：场景其它位置有同名对象时，用【复制新实例】而非移动原件 ——
        /// 复制(Ctrl+C/Ctrl+V)+AddObject，避免重名资源在场景里只剩一份。
        /// </summary>
        private static ITxObject FindOrCloneLeaf(ITxObject parent, string name, string type,
                                                 string ownPath, string cojtAbs,
                                                 double x, double y, double z,
                                                 Action<string> log)
        {
            // 1) 父容器直接子 → 直接复用（已正确挂载）
            foreach (var c in DirectChildren(parent))
            {
                if (string.Equals(c.Name, name, StringComparison.Ordinal))
                {
                    log("[重建]   ✓ 父容器直接子找到: " + name + " (" + c.GetType().Name + ")");
                    return c;
                }
            }

            // 2) 全场景同名对象（在其它容器下）→ 复制新实例到目标容器，不移动原件
            ITxObject scene = FindByNameInScene(name);
            if (scene != null)
            {
                string want = (type ?? "").Trim();
                string got = scene.GetType().Name;
                bool typeOk = want.Length == 0 || string.Equals(want, got, StringComparison.Ordinal);
                if (!typeOk)
                {
                    log("[重建]   ⚠ 全场景有同名但类型不同: " + name
                        + " 期望 " + want + " 实际 " + got + "（不复制，位置可能在别处）");
                }
                else
                {
                    log("[重建]   ✓ 全场景同名同类型: " + name + " (" + got + ") → 复制新实例");
                    ITxObject clone = ComponentIO.CloneResource(scene, parent, name, log);
                    if (clone != null)
                    {
                        log("[重建]   ✓ 已复制新实例: " + name);
                        return clone;
                    }
                    log("[重建]   ⚠ 复制新实例失败，尝试 COJT 自动插入: " + name);
                }
            }

            // 3) 全场景都没有（或复制失败）→ COJT 列自动插入
            if (!string.IsNullOrWhiteSpace(cojtAbs))
            {
                var inserted = TryAutoInsertLeaf(parent, name, type, cojtAbs, x, y, z, log);
                if (inserted != null) return inserted;
            }

            log("[重建]   ✗ 全场景无同名对象: " + name + " —— 组件 cojt 可能未插入对端场景");
            return null;
        }

        /// <summary>
        /// 叶子未找到时，用 COJT 列(组件磁盘物理路径)自动插入组件。
        /// 流程：取 cojt 目录名 → 在对端库根(SystemRootDirectory)下递归找同名 cojt →
        ///       ComponentIO.InsertCojt 插入(含全角路径 junction 绕路) → 返回插入后的组件。
        /// </summary>
        private static ITxObject TryAutoInsertLeaf(ITxObject parent, string name, string type,
                                                   string cojtAbs, double x, double y, double z,
                                                   Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cojtAbs)) return null;

                // 只对组件/资源类型尝试插入（容器已由 EnsureContainer 处理）
                bool isInsertable = type.IndexOf("Component", StringComparison.Ordinal) >= 0
                                    || type.IndexOf("Robot", StringComparison.Ordinal) >= 0
                                    || type.IndexOf("Gun", StringComparison.Ordinal) >= 0
                                    || type.IndexOf("Device", StringComparison.Ordinal) >= 0
                                    || type.IndexOf("Equipment", StringComparison.Ordinal) >= 0
                                    || type.IndexOf("Fixture", StringComparison.Ordinal) >= 0;
                if (!isInsertable) return null;

                // 1) 取 cojt 目录名
                string cojtName = Path.GetFileName(cojtAbs.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(cojtName)) return null;

                // 2) 在对端库根下递归查找同名 cojt（跳过 junction 残留，选最深真实路径）
                string libRoot = null;
                try { libRoot = TxApplication.SystemRootDirectory; } catch { }
                if (string.IsNullOrWhiteSpace(libRoot)) return null;
                string cojtLocal = PszPathFixer.FindRealCojtInRoot(libRoot, cojtName, log);
                if (string.IsNullOrEmpty(cojtLocal))
                {
                    log("[重建]   ⚠ 对端库根下未找到 cojt: " + cojtName + "（库根 " + libRoot + "）");
                    return null;
                }
                log("[重建]   → 定位对端 cojt: " + cojtLocal);

                // 3) 插入组件（InsertCojt 内含全角路径 junction 绕路 + 改库路径）
                string summary;
                bool ok = ComponentIO.InsertCojt(cojtLocal, name, null, libRoot, x, y, z, log, out summary);
                if (!ok)
                {
                    log("[重建]   ✗ 插入失败: " + name + " | " + summary);
                    return null;
                }
                log("[重建]   ✓ 已自动插入组件: " + name);

                // 4) 按名在场景找新组件
                return FindByNameInScene(name);
            }
            catch (Exception ex)
            {
                log("[重建]   ✗ 自动插入异常: " + ex.Message);
                return null;
            }
        }

        private static ITxObject FindByNameInScene(string name)
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return null;
                // 显式栈 DFS（PhysicalRoot.GetAllDescendants(null) 会抛 NRE）
                var stack = new Stack<ITxObject>();
                foreach (var c in DirectChildren(doc.PhysicalRoot))
                    stack.Push(c);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (cur == null) continue;
                    try
                    {
                        if (string.Equals(cur.Name, name, StringComparison.Ordinal))
                            return cur;
                        var coll = cur as ITxObjectCollection;
                        if (coll != null)
                            foreach (var c in DirectChildren(cur))
                                stack.Push(c);
                    }
                    catch { }
                }
            }
            catch { }
            return null;
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

        private static string SafeName(ITxObject obj)
        {
            try { return obj == null ? "?" : obj.Name; }
            catch { return "?"; }
        }

        // ── 位姿 ──

        private static void GetLocation(ITxObject obj, out double x, out double y, out double z,
                                        out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            try
            {
                dynamic d = obj;
                var loc = d.AbsoluteLocation as TxTransformation;
                if (loc == null) return;
                x = loc.Translation.X; y = loc.Translation.Y; z = loc.Translation.Z;
                TxMath.MatrixToEulerDeg(TxMath.TxToArr(loc), out rx, out ry, out rz);
            }
            catch { }
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

        private static string GetCojtPath(ITxObject obj)
        {
            try
            {
                var storable = obj as ITxStorable;
                if (storable == null) return "";
                var storage = storable.StorageObject;
                if (storage == null) return "";
                var p = storage.GetType().GetProperty("FullPath");
                if (p != null)
                {
                    var v = p.GetValue(storage, null) as string;
                    return v ?? "";
                }
            }
            catch { }
            return "";
        }

        // ── 解析 ──

        private static bool IsCompound(string type)
            => type.IndexOf("Compound", StringComparison.Ordinal) >= 0;

        /// <summary>是否为制造特征（焊点/焊缝等），这类对象不属于资源/零件结构，导出时跳过。</summary>
        private static bool IsMfgFeature(ITxObject obj)
        {
            try
            {
                if (obj == null) return true;
                var t = obj.GetType();
                // 焊点、焊缝等制造特征：TxWeldPoint / TxMfgFeature / TxSeam...
                string n = t.Name;
                if (n.IndexOf("WeldPoint", StringComparison.Ordinal) >= 0) return true;
                if (n.IndexOf("Mfg", StringComparison.Ordinal) >= 0) return true;
                if (n.IndexOf("Seam", StringComparison.Ordinal) >= 0) return true;
                // 制造特征基类
                while (t != null)
                {
                    if (t.Name == "TxMfgFeature" || t.Name == "TxMfgObject") return true;
                    t = t.BaseType;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>去掉 R:/P: 树前缀，返回纯路径（如 "R:VAN/ANV" → "VAN/ANV"）。</summary>
        private static string StripTreePrefix(string ownPath)
        {
            if (string.IsNullOrEmpty(ownPath)) return ownPath;
            if (ownPath.StartsWith("R:", StringComparison.Ordinal)
                || ownPath.StartsWith("P:", StringComparison.Ordinal))
                return ownPath.Substring(2);
            return ownPath;
        }

        private static string GetParentPath(string ownPath)
        {
            int i = ownPath.LastIndexOf('/');
            return i > 0 ? ownPath.Substring(0, i) : "";
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

        private static List<string[]> ReadRows(string filePath, Action<string> log)
        {
            var rows = new List<string[]>();
            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            foreach (var l in lines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                if (l.StartsWith("Name\t", StringComparison.Ordinal)
                    || l.StartsWith("WeldOpName\t", StringComparison.Ordinal)) continue;
                var p = l.Split('\t');
                if (p.Length >= 9) rows.Add(p);
            }
            return rows;
        }

        // ── 移动 ──

        private static bool MoveInto(ITxObject parent, ITxObject child)
        {
            try { dynamic d = parent; d.AddObject(child); return true; }
            catch
            {
                try
                {
                    dynamic d = parent;
                    var l = new TxObjectList();
                    l.Add(child);
                    d.AddObjects(l);
                    return true;
                }
                catch { return false; }
            }
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
