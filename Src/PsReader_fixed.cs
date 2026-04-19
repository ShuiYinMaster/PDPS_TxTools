// PsReader.cs  —  C# 7.3
// Process Simulate 数据读取：操作解析 / 焊点提取 / 焊枪信息 / 参考坐标系

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Tecnomatix.Engineering;

namespace MyPlugin.ExportGun
{
    public enum PointType { WeldPoint, PathPoint, ContinuousPoint, All }

    public class PointInfo
    {
        public string   Name;
        public PointType Type;
        public double[] TCPMatrix;
        public double[] Position;
        public double[] Normal;
    }

    public class GunInfo
    {
        public string   Name;
        public string   ModelPath;
        public double[] ToolMatrix;     // 工具安装坐标（世界系），= CGR 自然原点
        public double[] TcpWorldMatrix; // TCP 在世界系的绝对位置
        public double[] TcpRelTool;     // TCP 相对工具自身的局部固定偏移 = Inv(ToolMatrix)*TcpWorldMatrix
    }

    public class OperationInfo
    {
        public string        Name;
        public string        TypeLabel;
        public ITxObject     PsObject;
        public List<PointInfo> Points = new List<PointInfo>();
        public GunInfo       Gun;
    }

    public static class PsReader
    {
        // ════════════════════════════════════════════════════════════
        //  1. 解析选中对象 → 操作列表
        // ════════════════════════════════════════════════════════════
        public static List<OperationInfo> GetOperationsFromSelection(Action<string> log)
        {
            if (log == null) log = Nop;
            var result = new List<OperationInfo>();
            try
            {
                TxObjectList sel = TxApplication.ActiveSelection.GetItems();
                if (sel != null && sel.Count > 0)
                    foreach (ITxObject obj in sel)
                        ParseItem(obj, result, log);
            }
            catch (Exception ex) { log($"[PS] 读取选中异常：{ex.Message}"); }

            if (result.Count == 0)
            {
                log("[PS] 无有效选中，读取 OperationRoot...");
                ReadRoot(result, log);
            }
            log($"[PS] 解析完成：{result.Count} 个操作");
            return result;
        }

        private static void ParseItem(ITxObject obj, List<OperationInfo> list, Action<string> log)
        {
            if (obj == null) return;
            string tn = obj.GetType().Name;

            if (tn == "TxWeldOperation" || (tn.Contains("TxWeld") && obj is ITxRoboticOperation))
            { list.Add(MakeOp(obj, "焊接操作")); return; }

            if (obj is TxCompoundOperation)
            { ExpandCompound((TxCompoundOperation)obj, list, log); return; }

            if (obj is ITxRoboticOperation)
            { list.Add(MakeOp(obj, LabelOp(tn))); return; }

            if (obj is TxWeldPoint wp)
            {
                // 选中单个焊点：向上找父 WeldOperation，保留 Robot/Tool 信息
                var wpTx = SafeGetTx(() => wp.AbsoluteLocation);
                ITxObject parentOp = FindParentWeldOperation(wp);
                OperationInfo op = parentOp != null
                    ? MakeOp(parentOp, "焊接操作(单点)")
                    : MakeOp(obj, "焊点");
                op.Name = wp.Name;
                if (wpTx != null) op.Points.Add(MakePt(wpTx, wp.Name, PointType.WeldPoint));
                list.Add(op);
                return;
            }

            if (obj is ITxMfgFeature)
            { list.Add(MakeOp(obj, "MFG特征")); return; }

            TxObjectList kids = GetKids(obj);
            if (kids != null && kids.Count > 0)
                foreach (ITxObject child in kids)
                    ParseItem(child, list, log);
        }

        private static void ExpandCompound(TxCompoundOperation comp, List<OperationInfo> list, Action<string> log)
        {
            TxObjectList kids = GetKids(comp);
            if (kids == null || kids.Count == 0) { list.Add(MakeOp(comp, "复合操作")); return; }
            bool added = false;
            foreach (ITxObject child in kids)
            {
                string tn = child.GetType().Name;
                if (tn == "TxWeldOperation" || tn.Contains("TxWeld"))
                { added = true; list.Add(MakeOp(child, "焊接操作")); }
                else if (child is TxCompoundOperation)
                { added = true; ExpandCompound((TxCompoundOperation)child, list, log); }
                else if (child is ITxRoboticOperation)
                { added = true; list.Add(MakeOp(child, LabelOp(tn))); }
            }
            if (!added) list.Add(MakeOp(comp, "复合操作"));
        }

        private static void ReadRoot(List<OperationInfo> list, Action<string> log)
        {
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc == null) return;
                TxObjectList kids = GetKidsFromRoot(doc.OperationRoot);
                if (kids == null) return;
                foreach (ITxObject obj in kids) ParseItem(obj, list, log);
            }
            catch (Exception ex) { log($"[PS] ReadRoot 异常：{ex.Message}"); }
        }

        // 通过 WeldLocationOperations 反向找父 TxWeldOperation
        private static ITxObject FindParentWeldOperation(TxWeldPoint wp)
        {
            try
            {
                TxObjectList weldLocOps = wp.WeldLocationOperations;
                if (weldLocOps == null || weldLocOps.Count == 0) return null;
                foreach (ITxObject locOp in weldLocOps)
                {
                    try
                    {
                        dynamic d = locOp;
                        ITxObject parent = null;
                        try { parent = d.Operation as ITxObject; } catch { }
                        if (parent == null) try { parent = d.ParentOperation as ITxObject; } catch { }
                        if (parent == null) try { parent = d.Owner as ITxObject; } catch { }
                        if (parent != null && (parent.GetType().Name.Contains("Weld") || parent is ITxRoboticOperation))
                            return parent;
                        if (locOp is ITxRoboticOperation) return locOp;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  2. 填充焊点数据
        // ════════════════════════════════════════════════════════════
        public static void FillPoints(OperationInfo op, PointType ptFilter, bool useMfg, Action<string> log)
        {
            if (log == null) log = Nop;
            if (op?.PsObject == null) return;
            if (op.Points.Count > 0) return;

            bool wWeld = ptFilter == PointType.WeldPoint || ptFilter == PointType.All;
            bool wPath = ptFilter == PointType.PathPoint  || ptFilter == PointType.All;
            bool wCont = ptFilter == PointType.ContinuousPoint || ptFilter == PointType.All;

            string tn = op.PsObject.GetType().Name;
            if (tn == "TxWeldOperation" || tn.Contains("TxWeld"))
                L1_WeldOpEnum(op, useMfg, wWeld, wPath, wCont, log);

            if (wPath || wCont) L2_Locations(op, wPath, wCont, log);
            if (wWeld && op.Points.Count == 0) L3_MfgFeatures(op, useMfg, log);
            if (op.Points.Count == 0) L4_DeepWalk(op.PsObject, op.Points, wWeld, wPath, wCont, 0, log);

            log($"[PS] [{op.Name}] {op.Points.Count} 个焊点");
        }

        private static void L1_WeldOpEnum(OperationInfo op, bool useMfg,
            bool wWeld, bool wPath, bool wCont, Action<string> log)
        {
            try
            {
                IEnumerable src = op.PsObject as IEnumerable;
                if (src == null) return;
                foreach (object item in src)
                {
                    if (item == null) continue;
                    try
                    {
                        PointType ptKind = PmKindOf(item.GetType().Name);
                        bool want = (ptKind == PointType.WeldPoint && wWeld)
                                 || (ptKind == PointType.PathPoint && wPath)
                                 || (ptKind == PointType.ContinuousPoint && wCont);
                        if (!want) continue;
                        TxTransformation tx = GetTxFromPm(item);
                        if (tx == null) continue;
                        string nm = GetNameFromPm(item, useMfg) ?? $"{op.Name}_{op.Points.Count + 1}";
                        if (!Dup(op.Points, nm, ptKind)) op.Points.Add(MakePt(tx, nm, ptKind));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { log($"[PS] L1 异常：{ex.Message}"); }
        }

        private static void L2_Locations(OperationInfo op, bool wPath, bool wCont, Action<string> log)
        {
            try
            {
                dynamic d = op.PsObject;
                IEnumerable locsEnum = null;
                TryGetEnum(d, "Locations",        ref locsEnum);
                if (locsEnum == null) TryGetEnum(d, "LocationList",     ref locsEnum);
                if (locsEnum == null) TryGetEnum(d, "RoboticLocations", ref locsEnum);
                if (locsEnum == null) return;
                foreach (object loc in locsEnum)
                {
                    try
                    {
                        PointType k = KindOf(loc.GetType().Name);
                        if (k == PointType.WeldPoint) continue;
                        if (k == PointType.PathPoint && !wPath) continue;
                        if (k == PointType.ContinuousPoint && !wCont) continue;
                        TxTransformation tx = GetTx(loc);
                        if (tx == null) continue;
                        string nm = SafeNameObj(loc);
                        if (!Dup(op.Points, nm, k)) op.Points.Add(MakePt(tx, nm, k));
                    }
                    catch { }
                }
            }
            catch (Exception ex) { log($"[PS] L2 异常：{ex.Message}"); }
        }

        private static void L3_MfgFeatures(OperationInfo op, bool useMfg, Action<string> log)
            => L3_Recurse(op.PsObject, op.Points, useMfg, 0);

        private static void L3_Recurse(ITxObject node, List<PointInfo> pts, bool useMfg, int depth)
        {
            if (node == null || depth > 20) return;
            bool found = false;
            try
            {
                dynamic d = node;
                object raw = null;
                try { raw = d.AssignedMfgFeatures; } catch { }
                if (raw is IEnumerable ie)
                {
                    found = true;
                    foreach (object f in ie)
                    {
                        if (!(f is TxWeldPoint wp)) continue;
                        TxTransformation tx = SafeGetTx(() => wp.AbsoluteLocation);
                        if (tx == null) continue;
                        string nm = useMfg ? (MfgOpName(wp) ?? wp.Name) : wp.Name;
                        if (!Dup(pts, nm, PointType.WeldPoint)) pts.Add(MakePt(tx, nm, PointType.WeldPoint));
                    }
                }
            }
            catch { }
            if (found) return;
            TxObjectList kids = GetKids(node);
            if (kids == null) return;
            foreach (ITxObject child in kids) L3_Recurse(child, pts, useMfg, depth + 1);
        }

        private static void L4_DeepWalk(ITxObject node, List<PointInfo> pts,
            bool wWeld, bool wPath, bool wCont, int depth, Action<string> log)
        {
            if (node == null || depth > 30) return;
            string tn = node.GetType().Name;
            if (node is TxWeldPoint wp && wWeld)
            {
                TxTransformation tx = SafeGetTx(() => wp.AbsoluteLocation);
                if (tx != null && !Dup(pts, wp.Name, PointType.WeldPoint))
                    pts.Add(MakePt(tx, wp.Name, PointType.WeldPoint));
            }
            else if (IsLocNode(tn))
            {
                PointType k = KindOf(tn);
                bool want = (k == PointType.WeldPoint && wWeld)
                         || (k == PointType.PathPoint && wPath)
                         || (k == PointType.ContinuousPoint && wCont);
                if (want)
                {
                    TxTransformation tx = GetTx(node);
                    string nm = SafeName(node);
                    if (tx != null && !Dup(pts, nm, k)) pts.Add(MakePt(tx, nm, k));
                }
            }
            else
            {
                IEnumerable asEnum = node as IEnumerable;
                if (asEnum != null)
                {
                    foreach (object item in asEnum)
                    {
                        if (item is ITxObject child) { L4_DeepWalk(child, pts, wWeld, wPath, wCont, depth + 1, log); continue; }
                        if (item == null) continue;
                        PointType pk = PmKindOf(item.GetType().Name);
                        bool want = (pk == PointType.WeldPoint && wWeld)
                                 || (pk == PointType.PathPoint && wPath)
                                 || (pk == PointType.ContinuousPoint && wCont);
                        if (!want) continue;
                        TxTransformation tx = GetTxFromPm(item) ?? GetTx(item);
                        if (tx != null) { string nm = GetNameFromPm(item, false) ?? SafeNameObj(item); if (!Dup(pts, nm, pk)) pts.Add(MakePt(tx, nm, pk)); }
                    }
                    return;
                }
            }
            TxObjectList kids = GetKids(node);
            if (kids == null) return;
            foreach (ITxObject child in kids) L4_DeepWalk(child, pts, wWeld, wPath, wCont, depth + 1, log);
        }

        // ════════════════════════════════════════════════════════════
        //  3. 焊枪信息
        // ════════════════════════════════════════════════════════════
        // modelPath：外部传入的兜底路径（可为 null，优先从 PS 资源自动解析）
        public static GunInfo GetGunFromOperation(OperationInfo op, string modelPath, Action<string> log)
        {
            if (log == null) log = Nop;
            try
            {
                // ── Step1：获取工具对象 ───────────────────────────────────
                // TxWeldOperation.Tool → 带 TCPF 的工具（优先）
                // TxWeldOperation.Gun  → 焊枪资源本身
                object toolObj = null;

                if (op.PsObject is TxWeldOperation weldOp)
                {
                    try { var t = weldOp.Tool; if (t != null) toolObj = t; } catch { }
                    if (toolObj == null) try { var g = weldOp.Gun; if (g != null) toolObj = g; } catch { }
                }
                if (toolObj == null)
                {
                    dynamic dOp = op.PsObject;
                    try { toolObj = dOp.Tool;       } catch { }
                    if (toolObj == null) try { toolObj = dOp.Gun;        } catch { }
                    if (toolObj == null) try { toolObj = dOp.ActiveTool; } catch { }
                }

                TxRobot robot = FindRobot(op.PsObject);
                if (toolObj == null && robot != null)
                {
                    try { dynamic dr = robot; toolObj = dr.ActiveTool;  } catch { }
                    if (toolObj == null) try { dynamic dr = robot; toolObj = dr.CurrentTool; } catch { }
                    if (toolObj == null) toolObj = FindGunTool(op.PsObject);
                }

                if (toolObj == null && robot == null)
                { log($"[PS] [{op.Name}] 未找到工具对象"); return null; }

                string toolName = robot?.Name ?? "UnknownTool";
                try { dynamic dt = toolObj; toolName = (dt.Name as string) ?? toolName; } catch { }

                // ── Step2：解析 CGR 文件路径 ─────────────────────────────
                string resolvedPath = ResolveModelPath(toolObj, toolName, modelPath, log);

                // ── Step3：工具安装坐标（= CGR 自然原点）────────────────
                TxTransformation toolTx = null;
                if (toolObj != null)
                {
                    try { dynamic dt = toolObj; toolTx = dt.AbsoluteLocation as TxTransformation; } catch { }
                    if (toolTx == null) try { dynamic dt = toolObj; toolTx = dt.LocationRelativeToWorld as TxTransformation; } catch { }
                }
                if (toolTx == null && robot != null)
                    try { toolTx = robot.Baseframe.AbsoluteLocation; } catch { }
                if (toolTx == null && robot != null)
                    try { toolTx = robot.AbsoluteLocation; } catch { }

                // ── Step4：TCP 世界系坐标 ────────────────────────────────
                TxTransformation tcpWorldTx = null;
                if (toolObj != null)
                {
                    dynamic dt = toolObj;
                    try { tcpWorldTx = dt.TCPF.AbsoluteLocation as TxTransformation; } catch { }
                    if (tcpWorldTx == null) try { tcpWorldTx = dt.AbsoluteTCPLocation as TxTransformation; } catch { }
                    if (tcpWorldTx == null) try { tcpWorldTx = dt.TCPFrame.AbsoluteLocation as TxTransformation; } catch { }
                }
                if (tcpWorldTx == null && robot != null)
                    try { tcpWorldTx = robot.TCPF.AbsoluteLocation; } catch { }
                if (tcpWorldTx == null) tcpWorldTx = toolTx;

                // ── Step5：TcpRelTool = Inv(ToolMatrix) * TcpWorldMatrix ─
                TxTransformation tcpRelTool = null;
                if (toolTx != null && tcpWorldTx != null)
                    try { tcpRelTool = TxTransformation.Multiply(toolTx.Inverse, tcpWorldTx); } catch { }

                return new GunInfo
                {
                    Name          = toolName,
                    ModelPath     = resolvedPath,
                    ToolMatrix    = TxToArr(toolTx),
                    TcpWorldMatrix = TxToArr(tcpWorldTx),
                    TcpRelTool    = TxToArr(tcpRelTool)
                };
            }
            catch (Exception ex)
            { log($"[PS] GetGun 异常：{ex.Message}"); return null; }
        }

        // ── CGR 路径解析 ──────────────────────────────────────────────
        // 策略1：ITxStorable.StorageObject → TxLibraryStorage.FullPath（官方 API）
        // 策略2：反射读取路径属性
        // 策略3：StudyPath 附近按工具名搜索
        // 策略4：外部 fallback 路径
        private static string ResolveModelPath(object toolObj, string toolName, string fallback, Action<string> log)
        {
            // 策略1：官方 API
            if (toolObj is ITxStorable storable)
            {
                try
                {
                    TxStorage storage = storable.StorageObject;
                    TxLibraryStorage libStorage = storage as TxLibraryStorage;
                    if (libStorage != null)
                    {
                        string fullPath = libStorage.FullPath;
                        if (!string.IsNullOrEmpty(fullPath))
                        {
                            if (SysFile.Exists(fullPath) && IsSupportedModel(fullPath)) return fullPath;
                            if (SysDir.Exists(fullPath))
                            {
                                string found = SearchCgrInDir(fullPath, toolName, log);
                                if (found != null) return found;
                            }
                            string dir = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(dir) && SysDir.Exists(dir))
                            {
                                string same = Path.Combine(dir, Path.GetFileNameWithoutExtension(fullPath) + ".cgr");
                                if (SysFile.Exists(same)) return same;
                                string found = SearchCgrInDir(dir, toolName, log);
                                if (found != null) return found;
                            }
                        }
                    }
                    else if (storage != null)
                    {
                        try
                        {
                            dynamic dyn = storage;
                            string p = dyn.FullPath as string;
                            if (!string.IsNullOrEmpty(p))
                            {
                                if (SysFile.Exists(p) && IsSupportedModel(p)) return p;
                                string dir = Path.GetDirectoryName(p);
                                if (SysDir.Exists(dir)) { string f = SearchCgrInDir(dir, toolName, log); if (f != null) return f; }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // 策略2：反射属性
            string[] pathProps = {
                "ExternalFilePath", "FilePath", "ModelFilePath", "SourceFilePath",
                "CgrFilePath", "GeometryFilePath", "ResourceFilePath", "ResourcePath",
                "ExternalFile", "FileLocation", "DataFilePath", "JtFilePath"
            };
            foreach (string prop in pathProps)
            {
                try
                {
                    var pi = toolObj.GetType().GetProperty(prop, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (pi == null) continue;
                    string s = pi.GetValue(toolObj) as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    if (SysFile.Exists(s) && IsSupportedModel(s)) return s;
                    string ext = Path.GetExtension(s).ToLowerInvariant();
                    string rawDir = Path.GetDirectoryName(s);
                    if ((ext == ".jt" || ext == ".xml") && SysDir.Exists(rawDir))
                    {
                        string same = Path.Combine(rawDir, Path.GetFileNameWithoutExtension(s) + ".cgr");
                        if (SysFile.Exists(same)) return same;
                        string found = SearchCgrInDir(rawDir, toolName, log);
                        if (found != null) return found;
                    }
                    if (SysDir.Exists(s)) { string found = SearchCgrInDir(s, toolName, log); if (found != null) return found; }
                }
                catch { }
            }

            // 策略3：StudyPath 搜索
            foreach (string root in GetPsSearchRoots())
            {
                string toolDir = Path.Combine(root, toolName);
                if (SysDir.Exists(toolDir)) { string f = SearchCgrInDir(toolDir, toolName, log); if (f != null) return f; }
                { string f = SearchCgrInDir(root, toolName, log); if (f != null) return f; }
            }

            return fallback;
        }

        private static string SearchCgrInDir(string dir, string toolName, Action<string> log)
        {
            if (!SysDir.Exists(dir)) return null;
            foreach (string ext in new[] { "*.cgr", "*.CATPart", "*.CATProduct" })
            {
                try
                {
                    var files = Directory.GetFiles(dir, ext, SearchOption.TopDirectoryOnly);
                    if (files.Length == 0) continue;
                    foreach (string f in files)
                        if (string.Equals(Path.GetFileNameWithoutExtension(f), toolName, StringComparison.OrdinalIgnoreCase))
                            return f;
                    foreach (string f in files)
                    {
                        string stem = Path.GetFileNameWithoutExtension(f);
                        if (stem.IndexOf(toolName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                            toolName.IndexOf(stem,  StringComparison.OrdinalIgnoreCase) >= 0)
                            return f;
                    }
                    if (files.Length == 1) return files[0];
                }
                catch { }
            }
            return null;
        }

        private static bool IsSupportedModel(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".cgr" || ext == ".catpart" || ext == ".catproduct";
        }

        private static string[] GetPsSearchRoots()
        {
            var roots = new List<string>();
            void TryAdd(string p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !roots.Contains(p)) roots.Add(p); }
            try
            {
                dynamic doc = TxApplication.ActiveDocument;
                if (doc != null)
                {
                    foreach (string prop in new[] { "StudyPath", "LibraryPath", "RootPath", "FilePath", "FolderPath" })
                        try { TryAdd(doc.GetType().GetProperty(prop)?.GetValue(doc) as string); } catch { }
                    try { string fp = doc.FilePath as string; TryAdd(Path.GetDirectoryName(fp)); } catch { }
                }
            }
            catch { }
            try { TryAdd(Directory.GetCurrentDirectory()); } catch { }
            return roots.ToArray();
        }

        private static object FindGunTool(ITxObject root)
        {
            if (root == null) return null;
            try { string tn = root.GetType().Name; if (tn.Contains("Gun") || tn.Contains("Tool") || tn.Contains("Gripper")) return root; } catch { }
            TxObjectList kids = GetKids(root);
            if (kids == null) return null;
            foreach (ITxObject child in kids) { var f = FindGunTool(child); if (f != null) return f; }
            return null;
        }

        private static TxRobot FindRobot(ITxObject obj)
        {
            if (obj == null) return null;
            try { dynamic d = obj; TxRobot r = d.Robot as TxRobot; if (r != null) return r; } catch { }
            TxObjectList kids = GetKids(obj);
            if (kids == null) return null;
            foreach (ITxObject child in kids) { TxRobot r = FindRobot(child); if (r != null) return r; }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  4. 参考坐标系
        // ════════════════════════════════════════════════════════════
        public static Tuple<string, double[]> GetReferenceFrame()
        {
            try
            {
                TxObjectList sel = TxApplication.ActiveSelection.GetItems();
                if (sel != null && sel.Count > 0)
                {
                    foreach (ITxObject obj in sel)
                    {
                        if (obj == null) continue;
                        string tn = obj.GetType().Name;
                        if (obj is TxFrame fr)
                        {
                            TxTransformation tx = SafeGetTx(() => fr.AbsoluteLocation);
                            if (tx != null && !IsIdentity(TxToArr(tx)))
                                return Tuple.Create(fr.Name, TxToArr(tx));
                        }
                        if (tn.Contains("Component") || tn.Contains("Product") || tn.Contains("Physical") || tn.Contains("Resource"))
                        {
                            TxTransformation tx = null;
                            try { dynamic d = obj; tx = d.AbsoluteLocation as TxTransformation; } catch { }
                            if (tx == null) try { dynamic d = obj; tx = d.LocationRelativeToWorkingFrame as TxTransformation; } catch { }
                            if (tx != null && !IsIdentity(TxToArr(tx)))
                                return Tuple.Create(SafeName(obj) + "(组件)", TxToArr(tx));
                        }
                        try
                        {
                            dynamic d = obj;
                            TxTransformation tx = d.AbsoluteLocation as TxTransformation;
                            if (tx != null && !IsIdentity(TxToArr(tx)))
                                return Tuple.Create(SafeName(obj), TxToArr(tx));
                        }
                        catch { }
                    }
                }
            }
            catch { }
            try
            {
                TxDocument doc = TxApplication.ActiveDocument;
                if (doc != null)
                {
                    TxTransformation wf = doc.WorkingFrame;
                    if (wf != null) { double[] arr = TxToArr(wf); if (!IsIdentity(arr)) return Tuple.Create("产品坐标系", arr); }
                }
            }
            catch { }
            return Tuple.Create("世界坐标系", new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 });
        }

        // ════════════════════════════════════════════════════════════
        //  5. 公共矩阵工具
        // ════════════════════════════════════════════════════════════
        public static double[] ToRelative(double[] absTx, double[] refMatrix)
        {
            if (refMatrix == null || IsIdentity(refMatrix)) return absTx;
            try
            {
                TxTransformation rel = TxTransformation.Multiply(ArrToTx(refMatrix).Inverse, ArrToTx(absTx));
                return TxToArr(rel);
            }
            catch { return absTx; }
        }

        public static bool IsIdentity(double[] m)
        {
            if (m == null || m.Length < 16) return true;
            return Math.Abs(m[0]-1)<1e-9 && Math.Abs(m[5]-1)<1e-9 && Math.Abs(m[10]-1)<1e-9
                && Math.Abs(m[1])<1e-9 && Math.Abs(m[2])<1e-9 && Math.Abs(m[4])<1e-9
                && Math.Abs(m[6])<1e-9 && Math.Abs(m[8])<1e-9 && Math.Abs(m[9])<1e-9
                && Math.Abs(m[3])<1e-9 && Math.Abs(m[7])<1e-9 && Math.Abs(m[11])<1e-9;
        }

        public static TxTransformation ArrToTxPublic(double[] m) => ArrToTx(m);

        public static double[] TxToArr(TxTransformation tx)
        {
            if (tx == null) return new double[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
            return new double[] {
                tx[0,0], tx[0,1], tx[0,2], tx[0,3],
                tx[1,0], tx[1,1], tx[1,2], tx[1,3],
                tx[2,0], tx[2,1], tx[2,2], tx[2,3],
                tx[3,0], tx[3,1], tx[3,2], tx[3,3]
            };
        }

        // ════════════════════════════════════════════════════════════
        //  私有工具方法
        // ════════════════════════════════════════════════════════════
        private static TxTransformation ArrToTx(double[] m)
        {
            var tx = new TxTransformation();
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) tx[r,c] = m[r*4+c];
            return tx;
        }

        private static TxTransformation GetTxFromPm(object item)
        {
            dynamic d = item;
            try { TxTransformation tx = d.AbsoluteLocation as TxTransformation; if (tx != null) return tx; } catch { }
            try { object loc = d.Location; if (loc is TxTransformation t) return t; } catch { }
            try { dynamic ld = d.LocationData; TxTransformation tx = ld?.Frame as TxTransformation; if (tx != null) return tx; } catch { }
            try { TxWeldPoint wp = d.MfgFeature as TxWeldPoint; if (wp != null) return wp.AbsoluteLocation; } catch { }
            try { TxWeldPoint wp = d.Feature    as TxWeldPoint; if (wp != null) return wp.AbsoluteLocation; } catch { }
            try { TxWeldPoint wp = d.WeldPoint  as TxWeldPoint; if (wp != null) return wp.AbsoluteLocation; } catch { }
            return null;
        }

        private static TxTransformation GetTx(object node)
        {
            TxTransformation tx = null;
            try { dynamic d = node; tx = d.AbsoluteLocation as TxTransformation; } catch { }
            if (tx != null) return tx;
            try { dynamic d = node; tx = d.LocationData.Frame as TxTransformation; } catch { }
            if (tx != null) return tx;
            try { dynamic d = node; tx = d.Location as TxTransformation; } catch { }
            return tx;
        }

        private static string GetNameFromPm(object item, bool useMfg)
        {
            dynamic d = item;
            if (useMfg)
            {
                try { TxWeldPoint wp = d.MfgFeature as TxWeldPoint; if (wp != null) return MfgOpName(wp) ?? wp.Name; } catch { }
                try { TxWeldPoint wp = d.Feature    as TxWeldPoint; if (wp != null) return MfgOpName(wp) ?? wp.Name; } catch { }
            }
            try { string nm = d.Name        as string; if (!string.IsNullOrEmpty(nm)) return nm; } catch { }
            try { string nm = d.DisplayName as string; if (!string.IsNullOrEmpty(nm)) return nm; } catch { }
            return null;
        }

        private static void TryGetEnum(dynamic d, string prop, ref IEnumerable result)
        {
            if (result != null) return;
            try { object raw = d.GetType().GetProperty(prop)?.GetValue(d); if (raw is IEnumerable ie) result = ie; } catch { }
        }

        private static TxObjectList GetKids(ITxObject node)
        {
            if (node == null) return null;
            TxTypeFilter f = new TxTypeFilter(typeof(ITxObject));
            if (node is TxCompoundOperation co) try { return co.GetDirectDescendants(f); } catch { }
            if (node is TxOperationRoot    ro) try { return ro.GetDirectDescendants(f); } catch { }
            try { dynamic d = node; return d.GetDirectDescendants(f) as TxObjectList; } catch { }
            return null;
        }

        private static TxObjectList GetKidsFromRoot(TxOperationRoot root)
        {
            if (root == null) return null;
            TxTypeFilter f = new TxTypeFilter(typeof(ITxObject));
            try { return root.GetDirectDescendants(f); } catch { }
            try { dynamic d = root; return d.GetDirectDescendants(f) as TxObjectList; } catch { }
            return null;
        }

        private static string MfgOpName(TxWeldPoint wp)
        {
            try { TxObjectList ops = wp.WeldLocationOperations; if (ops?.Count > 0) { dynamic f = ops[0]; string s = f.Name as string; if (!string.IsNullOrEmpty(s)) return s; } } catch { }
            return null;
        }

        private static PointInfo MakePt(TxTransformation tx, string name, PointType pt)
        {
            double[] m = TxToArr(tx);
            return new PointInfo { Name = name, Type = pt, TCPMatrix = m, Position = new[] { m[3], m[7], m[11] }, Normal = new[] { m[2], m[6], m[10] } };
        }

        private static OperationInfo MakeOp(ITxObject obj, string label)
            => new OperationInfo { Name = SafeName(obj), TypeLabel = label, PsObject = obj };

        private static TxTransformation SafeGetTx(Func<TxTransformation> fn) { try { return fn(); } catch { return null; } }
        private static string SafeName(ITxObject obj) { try { dynamic d = obj; return (d.Name as string) ?? obj.GetType().Name; } catch { return obj.GetType().Name; } }
        private static string SafeNameObj(object obj) { try { dynamic d = obj; return (d.Name as string) ?? obj.GetType().Name; } catch { return obj.GetType().Name; } }
        private static bool IsLocNode(string tn) => tn.Contains("Location") || tn.Contains("Via") || tn.Contains("Arc") || tn.Contains("PathPoint") || tn.Contains("WeldLoc");
        private static PointType KindOf(string tn) { if (tn.Contains("Weld")) return PointType.WeldPoint; if (tn.Contains("Continuous")) return PointType.ContinuousPoint; return PointType.PathPoint; }
        private static PointType PmKindOf(string tn) { if (tn.Contains("Weld")) return PointType.WeldPoint; if (tn.Contains("Via") || tn.Contains("Arc")) return PointType.PathPoint; if (tn.Contains("Continuous")) return PointType.ContinuousPoint; return PointType.WeldPoint; }
        private static bool Dup(List<PointInfo> pts, string nm, PointType t) { foreach (var p in pts) if (p.Name == nm && p.Type == t) return true; return false; }
        private static string LabelOp(string tn) { if (tn.Contains("Weld")) return "焊接操作"; if (tn.Contains("Compound")) return "复合操作"; if (tn.Contains("Robotic")) return "机器人操作"; return "操作"; }
        private static void Nop(string s) { }

        private static class SysFile { public static bool Exists(string p) => File.Exists(p); }
        private static class SysDir  { public static bool Exists(string p) => Directory.Exists(p); }
    }
}
