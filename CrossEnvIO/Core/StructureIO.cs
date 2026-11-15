// StructureIO.cs  --  C# 8.0
// 跨环境目录结构 + 位姿导出/重建（资源树 TxCompoundResource / 零件树 TxCompoundPart）。
//
// 数据格式（TSV，首行头）:
//   资源树  : Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ
//   零件树  : 同上 + \tCOJT（组件库路径，导出时记录，重建时仅核对日志）
// ParentPath = 该对象「从导出根算起的自身路径」（/ 分隔，根行 = 根名）。
//
// 重建流程:
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
        public readonly Dictionary<string, string> ObjectMap = new Dictionary<string, string>(StringComparer.Ordinal);
        public override string ToString()
            => $"重建 {Rebuilt}（新建 {Created} · 移动 {Moved}）· 跳过 {Skipped} · 失败 {Failed}";
    }

    public static class StructureIO
    {
        private static void Nop(string s) { }
        private const string Fmt = "R";

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
                                               bool includeCojt, Action<string> log,
                                               Func<ITxObject, Dictionary<string, string>> classify = null)
        {
            log = log ?? Nop;
            if (roots == null || roots.Count == 0) { log("[导出] 未选择导出根"); return 0; }
            if (string.IsNullOrWhiteSpace(filePath)) { log("[导出] 文件路径为空"); return 0; }

            var sb = new StringBuilder();
            sb.AppendLine(includeCojt
                ? "Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ\tCOJT\tSourceLibraryRoot\tSourceObjectId\tTypeKeys\tSourceParentId\tSourceStudy\tSourceDocument\tSourceStudyId\tPrototypeData\tTargetName\tWorldTransform"
                : "Name\tType\tParentPath\tX\tY\tZ\tRX\tRY\tRZ");

            int count = 0;
            // treeKind=ROOT：根自身，下钻时按子节点类型分流 RES/PART
            var uniqueRoots=roots.Where(r=>r!=null).GroupBy(r=>r.Id,StringComparer.Ordinal).Select(g=>g.First()).ToList();
            var collisions=new HashSet<string>(uniqueRoots.GroupBy(r=>r.Name??r.GetType().Name,StringComparer.Ordinal).Where(g=>g.Count()>1).Select(g=>g.Key),StringComparer.Ordinal);
            var usedNames=new HashSet<string>(uniqueRoots.Select(r=>r.Name??r.GetType().Name),StringComparer.Ordinal);
            foreach (var root in uniqueRoots.OrderBy(r=>r.Id,StringComparer.Ordinal))
            {
                string original=root.Name??root.GetType().Name,segment=Uri.EscapeDataString(original),targetName=null;
                if(collisions.Contains(original))
                {
                    string hash=PackageFiles.HashBytes(Encoding.UTF8.GetBytes(TransferCapture.StudyId()+"\n"+root.Id));
                    string context=SceneAccess.Parent(root)?.Name??"文档根";
                    for(int length=8;length<=64;length*=2)
                    {
                        string candidate=original+" ["+context+" · "+hash.Substring(0,length)+"]";
                        if(usedNames.Add(candidate)){targetName=candidate;break;}
                    }
                    if(targetName==null)throw new InvalidOperationException("无法为同名导出根生成独立目标名称："+original);
                    // Braces are escaped in ordinary object names, so this namespace cannot
                    // collide with a literal source name. No extra parent node is introduced.
                    segment+="@{"+Uri.EscapeDataString(root.Id)+"}";
                    log("[导出] 同名根独立重建："+context+" / "+original+" → "+targetName);
                }
                WriteNode(sb, root, segment, "ROOT", includeCojt, ref count, log, classify,"",targetName);
            }
            TransferFile.Write(filePath, sb.ToString());
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
                                      Action<string> log, Func<ITxObject, Dictionary<string, string>> classify, string sourceParentId = "",string targetName=null)
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
                var worldPose=GetLocation(obj, out x, out y, out z, out rx, out ry, out rz);
                string cojt = "";
                if (includeCojt) cojt = GetCojtPath(obj);
                sb.Append(TransferFile.Field(!includeCojt&&targetName!=null?targetName:obj.Name)).Append('\t')
                  .Append(obj.GetType().Name).Append('\t')
                  .Append(path).Append('\t')
                  .Append(x.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(y.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(z.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(rx.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(ry.ToString(Fmt, CultureInfo.InvariantCulture)).Append('\t')
                  .Append(rz.ToString(Fmt, CultureInfo.InvariantCulture));
                if (includeCojt)
                {
                    sb.Append('\t').Append(TransferFile.Field(cojt)).Append('\t').Append(TransferFile.Field(EnvLibrary.CurrentRoot()))
                      .Append('\t').Append(obj.Id).Append('\t').Append(Newtonsoft.Json.JsonConvert.SerializeObject(
                          classify == null ? new Dictionary<string, string> { [obj.GetType().FullName] = obj.GetType().Name } : classify(obj)))
                      .Append('\t').Append(sourceParentId)
                      .Append('\t').Append(TransferFile.Field(TxApplication.ActiveDocument.CurrentStudy.Name))
                      .Append('\t').Append(TransferFile.Field(TxApplication.ActiveDocument.FinalDestination ?? ""))
                      .Append('\t').Append(TransferCapture.StudyId())
                      .Append('\t').Append(Newtonsoft.Json.JsonConvert.SerializeObject(DevicePrototypes.Capture(obj,cojt)))
                      .Append('\t').Append(TransferFile.Field(targetName??""))
                      .Append('\t').Append(Newtonsoft.Json.JsonConvert.SerializeObject(TxMath.WorldValues(worldPose)));
                }
                sb.AppendLine();
                count++;
            }
            catch (Exception ex) { throw new InvalidOperationException("导出节点失败: " + rawPath, ex); }

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
                foreach (ITxObject c in SceneAccess.Children(coll))
                    children.Add(c);
                // 子级继承当前树类型（资源容器的子仍是资源树，零件容器的子仍是零件树）
                string childKind = treeKind;
                if (obj is TxCompoundResource) childKind = "RES";
                else if (obj is TxCompoundPart) childKind = "PART";
                foreach (var c in children)
                    WriteNode(sb, c, rawPath + "/" + Uri.EscapeDataString(c.Name ?? ""), childKind, includeCojt, ref count, log, classify, obj.Id);
            }
            catch (Exception ex) { throw new InvalidOperationException("导出子树不完整: " + rawPath, ex); }
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
                                                       double originZ = 0, string sourceLibraryRoot = null,
                                                       IDictionary<string, string> cojtMap = null,
                                                       bool includeAllTrees = false, ISet<string> skippedPackages = null,
                                                       double[] sourceFrame=null,double[] targetFrame=null)
        {
            log = log ?? Nop;
            var rep = new StructureReport();
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            { rep.Failed++; rep.Errors.Add("文件不存在: " + filePath); return rep; }
            if (targetRoot == null) { rep.Failed++; rep.Errors.Add("目标容器为空"); return rep; }
            bool alignFrames=sourceFrame!=null||targetFrame!=null;
            TxTransformation sourceReference=null,targetReference=null;
            try
            {
                if(alignFrames)
                {
                    sourceReference=sourceFrame==null?new TxTransformation():TxMath.FromWorldValues(sourceFrame);
                    targetReference=targetFrame==null?new TxTransformation():TxMath.FromWorldValues(targetFrame);
                }
            }
            catch(Exception ex){rep.Failed++;rep.Errors.Add("双端参考坐标无效："+ex.Message);return rep;}

            List<string[]> rows;
            int targetNameIndex=-1,worldTransformIndex=-1;
            try
            {
                rows = ReadRows(filePath, log);
                string header=File.ReadLines(filePath,Encoding.UTF8).FirstOrDefault(line=>!string.IsNullOrWhiteSpace(line));
                if(header!=null)
                {
                    var fields=header.TrimStart('\uFEFF').Split('\t');
                    targetNameIndex=Array.IndexOf(fields,"TargetName");worldTransformIndex=Array.IndexOf(fields,"WorldTransform");
                }
            }
            catch (Exception ex) { rep.Failed++; rep.Errors.Add("读取失败: " + ex.Message); return rep; }
            if (rows.Count == 0) { log("[重建] 无数据行"); return rep; }
            var duplicatePaths = rows.GroupBy(row => row[2], StringComparer.Ordinal).Where(group => group.Count() > 1).ToList();
            if (duplicatePaths.Count > 0)
            { rep.Failed++; rep.Errors.Add("结构文件有重复对象路径: " + duplicatePaths[0].Key); return rep; }
            ComponentIO.ResetInsertedPaths();
            LibraryPersistence.Reset();

            // 深度排序（父先子后），容器行优先于同深度叶子
            var ordered = rows
                .Select(r => new { Row = r, Depth = r[2].Count(c => c == '/') })
                .OrderBy(o => o.Depth)
                .ThenBy(o => IsCompound(o.Row[1]) ? 0 : 1)
                .ToList();

            object um = OpenUndo(includeAllTrees ? "跨环境重建模型结构" : isResource ? "跨环境重建资源树" : "跨环境重建零件树", log);
            using ((TxTools.Common.SceneUndoScope)um)
            try
            {
                // 路径 -> 容器对象。key 用含 R:/P: 前缀的完整路径，
                // 使资源树与零件树（PhysicalRoot 下可能同名容器）不互相覆盖。
                var pathToObj = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
                var sourceIdToObj = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
                var assignedLeafIds=new HashSet<string>(StringComparer.Ordinal);
                var packageTemplates=new Dictionary<string,ITxObject>(StringComparer.OrdinalIgnoreCase);
                var placements=new List<Tuple<ITxObject,TxTransformation,string>>();
                var skippedIds = new HashSet<string>(StringComparer.Ordinal);
                pathToObj[targetRoot.Name ?? ""] = targetRoot;

                foreach (var o in ordered)
                {
                    var r = o.Row;
                    if (r.Length < 9) { rep.Skipped++; continue; }
                    string ownPath = r[2];
                    string desiredName=targetNameIndex>=0&&r.Length>targetNameIndex&&!string.IsNullOrWhiteSpace(r[targetNameIndex])?r[targetNameIndex]:r[0];

                    // 按目标树类型过滤：资源页(isResource)只建 R: 资源树，零件页只建 P: 零件树。
                    // 根行/无前缀行(如 VAN)两种模式都保留（根行随后跳过）。
                    if (!includeAllTrees && isResource && ownPath.StartsWith("P:", StringComparison.Ordinal))
                    { rep.Skipped++; continue; }
                    if (!includeAllTrees && !isResource && ownPath.StartsWith("R:", StringComparison.Ordinal))
                    { rep.Skipped++; continue; }
                    if (r.Length > 9 && !string.IsNullOrWhiteSpace(r[9]) && skippedPackages != null
                        && skippedPackages.Contains(Path.GetFullPath(r[9])))
                    { rep.Skipped++; if (r.Length > 11) skippedIds.Add(r[11]); log("[跳过] 已明确跳过组件包及其实例: " + ownPath); continue; }
                    if (r.Length > 13 && skippedIds.Contains(r[13]))
                    { rep.Skipped++; skippedIds.Add(r[11]); continue; }

                    string parentPath = GetParentPath(ownPath);
                    double x, y, z, rx, ry, rz;
                    if (!TryParseLoc(r, 3, out x, out y, out z, out rx, out ry, out rz))
                    { rep.Failed++; rep.Errors.Add("无效位姿: " + ownPath); continue; }
                    TxTransformation pose;
                    try
                    {
                        pose=ReadPose(r,worldTransformIndex);
                        if(alignFrames)pose=TxMath.AlignWorldPose(pose,sourceReference,targetReference);
                        else
                        {
                            var translation=pose.Translation;
                            pose.Translation=new TxVector(translation.X-originX,translation.Y-originY,translation.Z-originZ);
                        }
                    }
                    catch(Exception ex){rep.Failed++;rep.Errors.Add("读取完整位姿失败："+ownPath+" / "+ex.Message);continue;}

                    ITxObject parent;
                    // ── 父解析 ──
                    // parentPath 为空 → 导出树的根（导出根对象自己，如 R:ANV 或 VAN）。
                    if (r.Length > 13 && !string.IsNullOrEmpty(r[13]))
                    {
                        if (!sourceIdToObj.TryGetValue(r[13], out parent))
                        { rep.Failed++; rep.Errors.Add("源父对象尚未重建: " + ownPath); continue; }
                    }
                    else if (string.IsNullOrEmpty(parentPath))
                    {
                        if (r[1].IndexOf("PhysicalRoot", StringComparison.Ordinal) >= 0)
                        {
                            pathToObj[ownPath] = targetRoot;
                            pathToObj["R:" + StripTreePrefix(ownPath)] = targetRoot;
                            pathToObj["P:" + StripTreePrefix(ownPath)] = targetRoot;
                            if (r.Length > 11) sourceIdToObj[r[11]] = targetRoot;
                            rep.Skipped++;
                            continue;
                        }
                        parent = targetRoot;
                    }
                    else
                    {
                        if (!pathToObj.TryGetValue(parentPath, out parent))
                        {
                            rep.Failed++;
                            rep.Errors.Add("父容器缺失: " + parentPath);
                            log("[重建] 父容器缺失: " + parentPath);
                            continue;
                        }
                    }

                    try
                    {
                        if (IsCompound(r[1]))
                        {
                            var beforeIds = new HashSet<string>(DirectChildren(parent).Select(c => c.Id));
                            var container = EnsureContainer(parent, desiredName, r[1], log);
                            if (container == null) { rep.Failed++; rep.Errors.Add("建容器失败: " + ownPath); continue; }
                            pathToObj[ownPath] = container;
                            if (r.Length > 11 && !string.IsNullOrEmpty(r[11])) rep.ObjectMap[r[11]] = container.Id;
                            if (r.Length > 11) sourceIdToObj[r[11]] = container;
                            placements.Add(Tuple.Create(container,pose,ownPath));
                            if (!beforeIds.Contains(container.Id)) rep.Created++;
                        }
                        else
                        {
                            // 叶子：① 父容器直接子同名 → 复用；② 场景其它位置同名 → 复制新实例（不移动原件，
                            // 避免重名资源场景只剩一份）；③ 都没有 → 用 COJT 列自动插入。
                            bool createdLeaf;
                            var leaf = FindOrCloneLeaf(parent, desiredName, r[1], ownPath,
                                                       r.Length > 9 ? (r[9] ?? "").Trim() : "",
                                                       x, y, z, log,
                                                       r.Length > 10 ? r[10] : sourceLibraryRoot, cojtMap, out createdLeaf,r[0],assignedLeafIds,packageTemplates);
                            if (leaf == null)
                            {
                                rep.Failed++;
                                rep.Errors.Add("组件缺失或身份有歧义: " + ownPath);
                                log("[重建] ✗ 未找到叶子且复制/自动插入失败: " + ownPath
                                    + "（类型 " + r[1] + "）"
                                    + "—— 请检查 cojt 是否已复制到指定库内子文件夹，以及 SDK 插入详情");
                                continue;
                            }
                            if (createdLeaf) rep.Created++;
                            if(!assignedLeafIds.Add(leaf.Id))throw new InvalidOperationException("多个源对象意外复用了同一个接收实例，已停止该对象："+ownPath);
                            if(r.Length>9&&!string.IsNullOrWhiteSpace(r[9]))
                            {
                                string expected=ResolveCojtPath(r[9],r.Length>10?r[10]:sourceLibraryRoot,cojtMap,log);
                                try { LibraryPersistence.Ensure(leaf,expected,r[9],log); }
                                catch(NativeLibraryException){throw;}
                                catch(Exception ex){throw new NativeLibraryException("组件入库校验失败："+leaf.Name+" / "+ex.Message,ex);}
                            }
                            bool moved; string moveError;
                            if (!MoveInto(parent, leaf, out moved, out moveError))
                            { rep.Failed++; rep.Errors.Add("挂载叶子失败: " + ownPath + "；" + moveError); continue; }
                            if (moved) rep.Moved++;
                            placements.Add(Tuple.Create(leaf,pose,ownPath));
                            if(r.Length>9&&!string.IsNullOrWhiteSpace(r[9]))
                            {
                                string expected=ResolveCojtPath(r[9],r.Length>10?r[10]:sourceLibraryRoot,cojtMap,log);
                                string templateKey=expected+"|"+r[1];
                                if(!packageTemplates.ContainsKey(templateKey))packageTemplates.Add(templateKey,leaf);
                            }
                            if (r.Length > 11 && !string.IsNullOrEmpty(r[11])) rep.ObjectMap[r[11]] = leaf.Id;
                            if (r.Length > 11) sourceIdToObj[r[11]] = leaf;
                        }
                    }
                    catch (Exception ex)
                    {
                        rep.Failed++;
                        rep.Errors.Add("[" + Uri.UnescapeDataString(ownPath) + "] " + ex.Message);
                        if(ex is NativeLibraryException)
                        {
                            rep.Created+=((NativeLibraryException)ex).UncountedCreated;
                            log("[引用] 已停止后续重建，请先核对接收端实例及其实际存储引用。");
                            break;
                        }
                    }
                }
                // Finish all insert/paste/reparent operations before placing anything.
                // Parent-first world transforms prevent later parent moves or paste
                // side effects from changing a previously positioned instance.
                foreach(var placement in placements)
                {
                    if(SetLocation(placement.Item1,placement.Item2))rep.Rebuilt++;
                    else{rep.Failed++;rep.Errors.Add("设置世界位姿失败："+placement.Item3);}
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
            var existing = DirectChildren(parent).Where(c => c.Name == name
                && (wantPart ? c is TxCompoundPart : c is TxCompoundResource)).ToList();
            if (existing.Count > 1) { log("[重建] 同名容器有多份: " + name); return null; }
            if (existing.Count == 1) return existing[0];
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
                                                 Action<string> log, string sourceLibraryRoot,
                                                 IDictionary<string, string> cojtMap, out bool created,string sourceName=null,ISet<string> assignedLeafIds=null,
                                                 IDictionary<string,ITxObject> packageTemplates=null)
        {
            created = false;
            string expectedPath = string.IsNullOrWhiteSpace(cojtAbs) ? null
                : ResolveCojtPath(cojtAbs, sourceLibraryRoot, cojtMap, log);
            // 1) 父容器直接子 → 直接复用（已正确挂载）
            var directMatches = DirectChildren(parent).Where(c => c.Name == name).ToList();
            if (directMatches.Count > 1) { log("[重建] 同名直接子对象有多份: " + ownPath); return null; }
            foreach (var c in directMatches)
            {
                if (string.Equals(c.Name, name, StringComparison.Ordinal))
                {
                    if (!string.Equals(c.GetType().Name, type, StringComparison.Ordinal)
                        || (!string.IsNullOrWhiteSpace(cojtAbs) && (expectedPath == null
                            || LibraryPersistence.ReferenceIssue(c,expectedPath,TxApplication.SystemRootDirectory)!=null)))
                    { log("[重建] 同名直接子对象类型/组件路径不一致，已跳过: " + ownPath); return null; }
                    if(assignedLeafIds!=null&&assignedLeafIds.Contains(c.Id))
                    {
                        var copy=ComponentIO.CloneResource(c,parent,name,log);
                        if(copy==null)throw new InvalidOperationException("共用模型的独立实例复制失败："+name);
                        created=true;return copy;
                    }
                    log("[重建]   ✓ 父容器直接子找到: " + name + " (" + c.GetType().Name + ")");
                    return c;
                }
            }

            if(expectedPath!=null&&packageTemplates!=null&&packageTemplates.TryGetValue(expectedPath+"|"+type,out var template))
            {
                var copy=ComponentIO.CloneResource(template,parent,name,log);
                if(copy==null)throw new NativeLibraryException("共用 cojt 的独立实例复制未完成："+name+"；已停止批次，未重复插入。",null);
                created=true;log("[多实例] 复用本批次模板复制："+name+"；源模型："+Path.GetFileName(expectedPath));return copy;
            }

            // 2) 全场景同名对象（在其它容器下）→ 复制新实例到目标容器，不移动原件
            var matches = TxApplication.ActiveDocument.PhysicalRoot
                .GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>()
                .Where(c => c.Name == (sourceName??name) && c.GetType().Name == type
                    && (string.IsNullOrWhiteSpace(cojtAbs) || (expectedPath != null
                        && LibraryPersistence.ReferenceIssue(c,expectedPath,TxApplication.SystemRootDirectory)==null)))
                .ToList();
            ITxObject scene = matches.Count == 1 ? matches[0] : null;
            if (matches.Count > 1 && string.IsNullOrWhiteSpace(cojtAbs))
            { log("[重建] 同名场景对象有多份，缺少组件身份: " + name); return null; }
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
                        created = true;
                        log("[重建]   ✓ 已复制新实例: " + name);
                        return clone;
                    }
                    log("[重建]   ⚠ 复制新实例失败，已停止该行以免重复插入: " + name);
                    return null;
                }
            }

            // 3) 全场景都没有（或复制失败）→ COJT 列自动插入
            if (!string.IsNullOrWhiteSpace(cojtAbs))
            {
                var reusable=LibraryPersistence.FindReusable(expectedPath,type);
                if(reusable!=null)
                {
                    var clone=ComponentIO.CloneResource(reusable,parent,name,log);
                    if(clone==null)throw new InvalidOperationException("复制已入库组件失败，未重复插入临时资源: "+name);
                    created=true; return clone;
                }
                var inserted = TryAutoInsertLeaf(parent, name, type, expectedPath, x, y, z, log);
                if (inserted != null)
                {
                    // Only the explicit disambiguation name is applied after insertion.
                    // Ordinary source names never override the SDK's model-document name.
                    if(sourceName!=null&&!string.Equals(name,sourceName,StringComparison.Ordinal))
                    {inserted.Name=name;log("[重建] 同名导出根独立命名："+name);}
                    created = true; return inserted;
                }
            }

            log("[重建]   ✗ 全场景无同名对象: " + name + " —— 组件 cojt 可能未插入对端场景");
            return null;
        }

        /// <summary>
        /// 叶子未找到时，用 COJT 列(组件磁盘物理路径)自动插入组件。
        /// 从已解析的最终库文件夹直接插入，保留 SDK 名称并核验正式引用。
        /// </summary>
        private static ITxObject TryAutoInsertLeaf(ITxObject parent, string name, string type,
                                                   string cojtAbs, double x, double y, double z,
                                                   Action<string> log)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cojtAbs)) return null;

                // 只对组件/资源类型尝试插入（容器已由 EnsureContainer 处理）
                bool isInsertable = IsInsertable(type);
                if (!isInsertable) return null;

                // 1) 取 cojt 目录名
                string cojtName = Path.GetFileName(cojtAbs.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(cojtName)) return null;

                // 2) 使用计划中的精确接收位置，不猜测库根或其他同名版本。
                string libRoot = null;
                try { libRoot = TxApplication.SystemRootDirectory; } catch { }
                if (string.IsNullOrWhiteSpace(libRoot)) return null;
                string cojtLocal = cojtAbs;
                if (string.IsNullOrEmpty(cojtLocal))
                {
                    log("[重建]   ⚠ 对端库根下未找到 cojt: " + cojtName + "（库根 " + libRoot + "）");
                    return null;
                }
                log("[重建]   → 定位对端 cojt: " + cojtLocal);

                // 3) 从最终库位置直接插入，不创建中转模型或改变库根。
                string summary;
                ITxObject inserted;
                bool ok = ComponentIO.InsertCojt(cojtLocal, string.Empty, null, libRoot, x, y, z, log, out summary, out inserted);
                if (!ok)
                {
                    if(inserted!=null)throw new NativeLibraryException("SDK 已创建实例，但直接插入后的校验未通过："+inserted.Name+"\n"+summary,null,1);
                    throw new InvalidOperationException("SDK 插入失败: " + name + " / " + cojtLocal + "\n" + summary);
                }
                if (inserted == null || inserted.GetType().Name != type)
                { throw new NativeLibraryException("插入后的设备类型不符: " + name + "；源端=" + type + "；接收端=" + (inserted?.GetType().Name ?? "null")
                    + "。请在接收端核对或撤销该实例；Planning 原型与 cojt 内部运动学定义需同时有效。\n" + summary,null,inserted==null?0:1); }
                log("[重建]   ✓ 已自动插入组件（SDK 原名称）: " + inserted.Name+"；模型文件："+Path.GetFileName(cojtLocal));

                // 4) 按名在场景找新组件
                return inserted;
            }
            catch (NativeLibraryException){throw;}
            catch (Exception ex)
            {
                log("[重建]   ✗ 自动插入异常: " + ex.Message);
                throw new InvalidOperationException("重建组件失败: " + name + "\n" + ex.Message, ex);
            }
        }

        internal static bool IsInsertable(string type)
        {
            var sdkType = typeof(ITxComponent).Assembly.GetType("Tecnomatix.Engineering." + type, false);
            return sdkType != null && typeof(ITxComponent).IsAssignableFrom(sdkType);
        }

        private static string ResolveCojtPath(string source, string sourceRoot,
            IDictionary<string, string> map, Action<string> log)
        {
            string targetRoot = TxApplication.SystemRootDirectory;
            string mapped;
            if (map != null && map.TryGetValue(Path.GetFullPath(source), out mapped))
                return Directory.Exists(mapped) && CojtTransfer.RelativeToRoot(mapped, targetRoot) != null ? mapped : null;
            string relative = CojtTransfer.RelativeToRoot(source, sourceRoot);
            if (relative != null)
            {
                string exact = Path.Combine(targetRoot, relative);
                if (Directory.Exists(exact)) return exact;
                log("[定位] 精确映射不存在，未猜测同名版本: " + exact);
                return null;
            }
            if (!string.IsNullOrWhiteSpace(sourceRoot))
            { log("[定位] 组件不属于声明的源库根，已停止匹配: " + source); return null; }
            if (CojtTransfer.RelativeToRoot(source, targetRoot) != null && Directory.Exists(source)) return source;
            // 兼容旧 TSV：只允许唯一同名组件，歧义返回 null。
            return PszPathFixer.FindRealCojtInRoot(targetRoot, Path.GetFileName(source), log);
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
                foreach (ITxObject c in SceneAccess.Children(coll))
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

        private static TxTransformation GetLocation(ITxObject obj, out double x, out double y, out double z,
                                        out double rx, out double ry, out double rz)
        {
            x = y = z = rx = ry = rz = 0;
            var loc=(obj as ITxLocatableObject)?.AbsoluteLocation;
            if(loc==null)
            {
                if(obj is TxPhysicalRoot)return new TxTransformation();
                throw new InvalidOperationException("对象没有可读取的世界位姿："+obj.Name);
            }
            x = loc.Translation.X; y = loc.Translation.Y; z = loc.Translation.Z;
            TxMath.MatrixToEulerDeg(TxMath.TxToArr(loc), out rx, out ry, out rz);
            return loc;
        }

        internal static TxTransformation ReadPose(string[] row,int matrixIndex)
        {
            if(matrixIndex>=0&&matrixIndex<row.Length&&!string.IsNullOrWhiteSpace(row[matrixIndex]))
                return TxMath.FromWorldValues(Newtonsoft.Json.JsonConvert.DeserializeObject<double[]>(row[matrixIndex]));
            double x,y,z,rx,ry,rz;
            if(!TryParseLoc(row,3,out x,out y,out z,out rx,out ry,out rz))throw new InvalidDataException("无效位姿");
            return TxMath.BuildTransform(x,y,z,rx,ry,rz);
        }

        private static bool SetLocation(ITxObject obj, TxTransformation pose)
        {
            try
            {
                if(!(obj is ITxLocatableObject locatable))return false;
                locatable.AbsoluteLocation = pose;
                return true;
            }
            catch { return false; }
        }

        internal static string GetCojtPath(ITxObject obj)
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
                    return ComponentIO.NormalizeStoragePath(v);
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
                if (!double.TryParse(r[idx + i], NumberStyles.Float, ci, out v[i])
                    || double.IsNaN(v[i]) || double.IsInfinity(v[i])) return false;
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

        private static bool MoveInto(ITxObject parent, ITxObject child, out bool moved, out string error)
        {
            moved = false; error = null;
            try
            {
                // InsertComponent 和复制可能已经把实例挂在正确位置；SDK 不保证允许重复 Add。
                if (SceneAccess.Parent(child)?.Id == parent.Id) return true;
                var collection = parent as ITxObjectCollection;
                if (collection == null) throw new InvalidOperationException("目标不是 SDK 对象集合: " + parent.GetType().Name);
                if (!collection.CanBeAdded(child)) throw new InvalidOperationException("SDK 不允许把此类型挂到目标容器: " + parent.Name);
                collection.AddObject(child);
                if (SceneAccess.Parent(child)?.Id != parent.Id)
                    throw new InvalidOperationException("SDK 挂载后父集合与目标不一致");
                moved = true; return true;
            }
            catch (Exception ex)
            {
                error = ex.Message + "；目标 " + parent.GetType().Name + " [" + parent.Id + "]";
                try { var actual = SceneAccess.Parent(child); error += "；实际父集合 " + (actual == null ? "<无>" : actual.GetType().Name + " [" + actual.Id + "]"); }
                catch { }
                return false;
            }
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
