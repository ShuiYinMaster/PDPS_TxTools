using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using INFITF;
using ProductStructureTypeLib;
using Tecnomatix.Engineering;
using SysFile = System.IO.File;

namespace TxTools.ExportByColor
{
    public sealed class ColorGroup
    {
        public byte R, G, B;
        public List<float[]> Tris = new List<float[]>();   // 每条 float[9] = 世界坐标三角形
    }

    public sealed class DeviceData
    {
        public string Name;
        public List<string> Path = new List<string>();   // PS 目录树路径(父级容器名,不含根)
        public List<ColorGroup> Colors = new List<ColorGroup>();
    }

    public sealed class DeviceFiles
    {
        public string DeviceName;
        public List<string> Path = new List<string>();          // PS 目录树路径
        public List<int> NavIndexes = new List<int>();          // CATIA 侧导航索引(根→设备Product)
        public List<string> Files = new List<string>();
        public List<string> RgbList = new List<string>();
        public int SuccessCount;                                // 实际成功导入的 STL 数（染色对齐用）
    }

    public class ExportByColorService
    {
        private readonly SynchronizationContext _psCtx;
        private INFITF.Application _catia;
        private List<Product> _leafProducts = new List<Product>();   // 每设备叶子 Product(与 deviceFiles 顺序一致)
        private ProductDocument _productDoc;                        // 导入目标文档（RunAsync 开头确定）
        private string _docName = "";

        public ExportByColorService(SynchronizationContext psCtx)
        {
            _psCtx = psCtx ?? new SynchronizationContext();
        }

        public void InvokeOnPs(Action action) { OnPs(action); }

        private void OnPs(Action action)
        {
            Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { action(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) throw ex;
        }

        private T OnPs<T>(Func<T> func)
        {
            T val = default(T); Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { val = func(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) throw ex;
            return val;
        }

        // ── 枚举场景所有可见设备（保留可见性过滤）───────────────────────
        public List<ITxObject> EnumerateVisibleDevices(Action<string> onLog)
        {
            return OnPs(delegate()
            {
                var list = new List<ITxObject>();
                var doc = TxApplication.ActiveDocument;
                if (doc == null)
                {
                    SafeLog(onLog, "[PS] 无活动文档");
                    return list;
                }
                foreach (var rootName in new[] { "PhysicalRoot", "ResourceRoot", "ComponentRoot" })
                {
                    object root = null;
                    try { root = TryGetProp(doc, rootName); } catch { }
                    if (root == null) continue;
                    var rootObj = root as ITxObject;
                    foreach (var d in ExpandPicked(rootObj, onLog))
                        list.Add(d);
                }
                var visible = new List<ITxObject>();
                foreach (var d in list)
                    if (IsVisible(d)) visible.Add(d);
                SafeLog(onLog, "[PS] 场景资源(展开后) " + list.Count + " 个, 可见 " + visible.Count + " 个");
                return visible;
            });
        }

        private static object TryGetProp(object o, string name)
        {
            if (o == null) return null;
            var pi = o.GetType().GetProperty(name);
            return pi != null ? pi.GetValue(o, null) : null;
        }

        private static bool IsVisible(ITxObject o)
        {
            var d = o as ITxDisplayableObject;
            if (d == null) return true;
            try { return d.Visibility != TxDisplayableObjectVisibility.None; }
            catch { return true; }
        }

        // ── 一键导出入口 ────────────────────────────────────────────────
        // mode: 0=设备×颜色拆分, 1=每设备1STL(不按颜色), 2=按工位颜色拆分
        public void RunAsync(List<ITxObject> picked, string originName, bool cgrTest,
                             bool noColorSplit, bool byStation,
                             Action<string> onLog, Action<bool, string> onComplete)
        {
            ThreadPool.QueueUserWorkItem(delegate(object s)
            {
                try
                {
                    // 0) 前置检查：CATIA 连接与目标文档——避免进行到一半才因 CATIA 失败
                    string err;
                    if (!Connect(out err))
                    {
                        SafeComplete(onComplete, false, err);
                        return;
                    }
                    EnsureProductDocument(onLog);

                    SafeLog(onLog, "[PS] 展开设备并分组几何...");
                    var devData = CollectDeviceGroups(picked, originName, onLog);
                    int totalTris = 0;
                    foreach (var dd in devData)
                        foreach (var cg in dd.Colors) totalTris += cg.Tris.Count;
                    if (devData.Count == 0 || totalTris == 0)
                    { SafeComplete(onComplete, false, "无颜色分组可导出，请确认已选中设备/组合"); return; }

                    // 临时目录存放中间 STL（导入后清理）
                    string tmpDir = Path.Combine(Path.GetTempPath(),
                        "TxTools_ExportByColor_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(tmpDir);
                    SafeLog(onLog, "[STL] 临时目录: " + tmpDir);

                    int mode = noColorSplit ? 1 : byStation ? 2 : 0;
                    var deviceFiles = BuildDeviceFiles(devData, tmpDir, mode, onLog);
                    if (deviceFiles.Count == 0)
                    {
                        TryDeleteDir(tmpDir);
                        SafeComplete(onComplete, false, "所有分组三角形数均为 0");
                        return;
                    }

                    ImportDeviceProducts(deviceFiles, onLog);

                    // 不按颜色拆分时不染色；否则按色染色
                    bool skipColoring = (mode == 1);
                    bool colored = skipColoring || TryColorStrongTyped(deviceFiles, onLog);
                    if (!skipColoring && !colored)
                    {
                        SafeLog(onLog, "[Catia] 强类型染色不可用，回退 cscript VBS ...");
                        ColorViaVbs(_docName, deviceFiles, tmpDir, onLog);
                    }

                    // 测试：把设备集合导出为 CGR 再导回，测速
                    if (cgrTest)
                    {
                        TestCgrRoundtrip(tmpDir, onLog);
                    }

                    // 导入完成，清理临时 STL
                    TryDeleteDir(tmpDir);

                    int totalFiles = 0;
                    foreach (var df in deviceFiles) totalFiles += df.Files.Count;
                    SafeComplete(onComplete, true,
                        "完成: " + deviceFiles.Count + " 个设备 Product, " + totalFiles + " 个 STL 已导入并上色，临时文件已清理");
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[错误] " + ex.Message);
                    SafeComplete(onComplete, false, ex.Message);
                }
            });
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (dir != null && Directory.Exists(dir))
                {
                    foreach (string f in Directory.GetFiles(dir))
                        try { SysFile.Delete(f); } catch { }
                    Directory.Delete(dir, true);
                    try { SafeLog(null, "[STL] 已清理临时目录: " + dir); } catch { }
                }
            }
            catch { }
        }

        // ── 展开：叶子设备直接加入；纯容器递归展开；静态资源兜底整体导出 ──
        // 判定逻辑（实测验证）：
        //   · 叶子设备 = 实现 ITxStorable（有 StorageObject）的对象
        //     (TxComponent/TxServoGun/TxRobot 等)——几何藏在 StorageObject 内部，
        //     需 Reload 解锁后由 EnumDeviceGeometries 枚举；它们大多也是
        //     ITxObjectCollection，若按"容器"递归展开子级，子级几何被 IsDeviceLike
        //     过滤后为空，兜底 HasGeometry(未Reload) 又返回 False → 真设备被丢弃。
        //   · 纯容器 = ITxObjectCollection 且无 ITxStorable
        //     (TxCompoundPart/TxCompoundResource/围栏/底座/柜体/夹具)
        //     → 自身不导出，递归展开子级
        //   · 根对象(TxPhysicalRoot)也实现 ITxStorable，必须排除，只递归不导出
        //   · 几何叶子(ITxGeometry)不单独作为导出单元，由所属对象统一收集
        private static List<ITxObject> ExpandPicked(ITxObject obj, Action<string> onLog)
        {
            var list = new List<ITxObject>();
            if (obj == null) return list;
            if (!IsDeviceLike(obj)) return list;

            // 根对象：只递归展开子级，自身不导出
            if (obj is TxPhysicalRoot)
            {
                foreach (var kid in EnumerateKids(obj))
                    list.AddRange(ExpandPicked(kid, onLog));
                return list;
            }

            // 叶子设备：实现 ITxStorable → 直接加入，不再当容器展开
            if (obj is ITxStorable)
            {
                list.Add(obj);
                return list;
            }

            // 纯容器（无 ITxStorable 的 ITxObjectCollection）→ 递归展开子级
            var coll = obj as ITxObjectCollection;
            if (coll != null)
            {
                foreach (var kid in EnumerateKids(obj))
                    list.AddRange(ExpandPicked(kid, onLog));
                if (list.Count > 0)
                {
                    SafeLog(onLog, "  组合展开: " + obj.Name + " → " + list.Count + " 个单元");
                    return list;
                }

                // 容器无子设备但自身含几何 → 整体导出（静态资源）
                if (HasGeometry(obj))
                {
                    list.Add(obj);
                    SafeLog(onLog, "  静态资源整体导出: " + obj.Name);
                }
                else
                {
                    SafeLog(onLog, "  skip(无几何): " + obj.Name);
                }
                return list;
            }

            // 其他非容器非几何 → 直接加入
            list.Add(obj);
            return list;
        }

        /// <summary>
        /// 枚举对象的设备类直接子级（多路径兜底）。
        /// 路径1：IEnumerable 枚举直接子级；
        /// 路径2：GetAllDescendants(TxNoTypeFilter) 全后代（某些集合不实现 IEnumerable 时兜底，
        ///        例如只有 ITxObjectCollection 的容器）。用 IsDeviceLike 过滤，仅收设备类。
        /// </summary>
        private static List<ITxObject> EnumerateKids(ITxObject obj)
        {
            var kids = new List<ITxObject>();
            if (obj == null) return kids;
            var coll = obj as ITxObjectCollection;
            if (coll == null) return kids;

            // 路径1：IEnumerable 直接子级
            try
            {
                var en = obj as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (object k in en)
                    {
                        var itx = k as ITxObject;
                        if (itx != null && IsDeviceLike(itx)) kids.Add(itx);
                    }
                }
            }
            catch { }

            // 路径2：GetAllDescendants 全后代兜底（防中间层容器不实现 IEnumerable 断链）
            if (kids.Count == 0)
            {
                try
                {
                    var all = coll.GetAllDescendants(new TxNoTypeFilter());
                    if (all != null)
                        foreach (object o in all)
                        {
                            var itx = o as ITxObject;
                            if (itx != null && IsDeviceLike(itx)) kids.Add(itx);
                        }
                }
                catch { }
            }
            return kids;
        }

        /// <summary>
        /// 判断对象是否为可导出的设备/资源（黑名单式）：
        /// 排除操作类(ITxOperation)、焊点、坐标系、相机、注释、标注、零件外观、扫掠体、纯几何叶子。
        /// 注意：不用白名单(ITxDevice 等)，因为围栏/底座/柜体/夹具是 TxCompoundResource，
        /// 不实现 ITxDevice 但含几何、应整体导出——黑名单才能保住它们。
        /// </summary>
        private static bool IsDeviceLike(ITxObject o)
        {
            if (o == null) return false;
            // 几何一律排除：用 ITxGeometry 接口判断，
            // 因为 TxSolid 基类是 TxObjectBase 只实现 ITxGeometry（不是 TxGeometry 子类），
            // 单查 TxGeometry 会漏掉它。
            if (o is ITxGeometry) return false;
            if (o is ITxOperation) return false;
            if (o is TxWeldPoint) return false;
            if (o is TxFrame) return false;
            if (o is TxViewCamera) return false;
            if (o is TxNote) return false;
            if (o is TxLinearDimension) return false;
            if (o is TxPartAppearance) return false;
            if (o is TxSweptVolume) return false;
            return true;
        }

        /// <summary>唯一性键：优先 ITxObject.Id（场景内唯一，形如 3,57,2,1），读取失败回退名称。</summary>
        private static string ObjKey(ITxObject o)
        {
            if (o == null) return "";
            try { var id = o.Id; if (!string.IsNullOrEmpty(id)) return "id:" + id; } catch { }
            try { return "name:" + o.Name; } catch { }
            return "";
        }

        /// <summary>
        /// 沿父容器链向上收集设备所在目录树路径（父级容器名数组，根级容器止步）。
        /// 用于在 CATIA 中按 PS 目录树归类导入的资源。
        /// 需在 PS 主线程调用。
        ///
        /// 关键：ITxObject 没有 Parent 属性（v6.5 文档确认），正确取父是
        ///   o.Collection（返回 ITxObjectCollection，即父容器）；多属性兜底
        ///   OwningObject/Owner/Container 兼容不同 SDK 版本。
        /// </summary>
        private static List<string> GetTreePath(ITxObject dev, Action<string> onLog)
        {
            var path = new List<string>();
            if (dev == null) return path;
            try
            {
                object cur = dev;
                var guard = new HashSet<string>();
                while (cur != null)
                {
                    ITxObject curObj = cur as ITxObject;
                    if (curObj == null) break;
                    string key = ObjKey(curObj);
                    if (!guard.Add(key)) break;               // 防环
                    object parent = GetParentObject(curObj);
                    if (parent == null) break;
                    ITxObject pObj = parent as ITxObject;
                    if (pObj == null) break;
                    string pName = null;
                    try { pName = pObj.Name; } catch { }
                    if (string.IsNullOrEmpty(pName)) break;
                    // 根级容器止步（PhysicalRoot/ResourceRoot/ComponentRoot 等顶层）
                    string pType = null;
                    try { pType = pObj.GetType().Name; } catch { }
                    if (pType != null &&
                        (pType.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0
                         || pName == "PhysicalRoot" || pName == "ResourceRoot" || pName == "ComponentRoot"))
                        break;
                    path.Insert(0, pName);
                    cur = parent;
                }
            }
            catch { }
            if (path.Count > 0)
                SafeLog(onLog, "  目录树: " + dev.Name + " → " + string.Join("/", path));
            return path;
        }

        /// <summary>
        /// 取 PS 对象的父容器。优先强类型 ITxObject.Collection；
        /// 再反射尝试 Parent/OwningObject/Owner/Container 多属性兜底。
        /// </summary>
        private static object GetParentObject(ITxObject o)
        {
            if (o == null) return null;
            // 1) 强类型 ITxObject.Collection → 父容器
            try
            {
                var coll = o.Collection;
                if (coll != null && !ReferenceEquals(coll, o)) return coll;
            }
            catch { }
            // 2) 反射多属性兜底（不同 SDK 版本命名不同）
            try
            {
                var t = o.GetType();
                foreach (var name in new[] { "Parent", "OwningObject", "Owner", "Container" })
                {
                    var p = t.GetProperty(name);
                    if (p != null)
                    {
                        var v = p.GetValue(o, null);
                        if (v is ITxObject && !ReferenceEquals(v, o)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>对象自身或其子树是否含几何（用于静态资源整体导出判断）。</summary>
        private static bool HasGeometry(ITxObject o)
        {
            var coll = o as ITxObjectCollection;
            if (coll == null) return false;
            try
            {
                // 注意：TxSolid 不是 TxGeometry 子类，需两个类型一起过滤
                var geomFilter = new TxTypeFilter(new Type[] { typeof(TxGeometry), typeof(TxSolid) });
                var geoms = coll.GetAllDescendants(geomFilter);
                return geoms != null && geoms.Count > 0;
            }
            catch { return false; }
        }

        // ── 分组：PS 主线程枚举几何，按(设备×颜色)收集世界三角形 ──────────
        private List<DeviceData> CollectDeviceGroups(List<ITxObject> picked, string originName, Action<string> onLog)
        {
            return OnPs(delegate()
            {
                // 1) 展开设备（无去重）
                var devices = new List<ITxObject>();
                foreach (var o in picked)
                {
                    var expanded = ExpandPicked(o, onLog);
                    if (expanded.Count == 0)
                        SafeLog(onLog, "  skip(无可导出): " + (o != null ? o.Name : "null"));
                    devices.AddRange(expanded);
                }
                SafeLog(onLog, "[PS] 展开资源数: " + devices.Count);
                if (devices.Count == 0) return new List<DeviceData>();

                // 2) 原点偏移
                double ox = 0, oy = 0, oz = 0;
                if (!string.IsNullOrEmpty(originName))
                {
                    var lst = TxApplication.ActiveDocument.GetObjectsByName(originName);
                    if (lst != null && lst.Count > 0)
                    {
                        var lo = lst[0] as ITxLocatableObject;
                        if (lo != null)
                        {
                            var t = lo.AbsoluteLocation.Translation;
                            ox = t.X; oy = t.Y; oz = t.Z;
                            SafeLog(onLog, "[PS] 原点: " + originName);
                        }
                    }
                }

                // 3) 每个设备按颜色分组收集三角形
                var result = new List<DeviceData>();
                foreach (var dev in devices)
                {
                    var dd = new DeviceData { Name = dev.Name };
                    dd.Path = GetTreePath(dev, onLog);

                    TxLibraryStorage st = null;
                    List<ITxGeometry> geoms = EnumDeviceGeometries(dev, ref st, onLog);
                    if (geoms == null || geoms.Count == 0)
                    {
                        // 区分：无几何（对象本身就不含几何） vs 枚举失败（有 StorageObject 但拿不到）
                        bool hasAny = HasGeometry(dev);
                        if (hasAny)
                            SafeLog(onLog, "  " + dev.Name + ": 枚举几何失败(含几何但未解锁)，已跳过");
                        else
                            SafeLog(onLog, "  " + dev.Name + ": 无几何，已跳过");
                        continue;
                    }

                    var colorIndex = new Dictionary<string, ColorGroup>();
                    foreach (var g in geoms)
                    {
                        byte r = 25, gg2 = 25, bb2 = 25;
                        try
                        {
                            // TxGeometry 与 TxSolid 各有自己的 GetColors()，按具体类型分发
                            HashSet<TxColor> cs = null;
                            var tg = g as TxGeometry;
                            if (tg != null) cs = tg.GetColors();
                            else
                            {
                                var ts = g as TxSolid;
                                if (ts != null) cs = ts.GetColors();
                            }
                            if (cs != null && cs.Count > 0)
                            {
                                // 取色：优先取第一个【非纯黑】颜色。
                                // 原因：ReferenceRep（引用表示）几何的 GetColors() 常把占位黑
                                // (0,0,0) 排在前面，真实设计色在集合里；直接 break 取第一个
                                // 会让主体导出成黑色。仅当颜色全是黑时才回退用黑。
                                bool picked = false;
                                foreach (object c in cs)
                                {
                                    var col = c as TxColor;
                                    if (col != null)
                                    {
                                        if (col.Red == 0 && col.Green == 0 && col.Blue == 0) continue;
                                        r = col.Red; gg2 = col.Green; bb2 = col.Blue;
                                        picked = true;
                                        break;
                                    }
                                }
                                if (!picked)
                                {
                                    foreach (object c in cs)
                                    {
                                        var col = c as TxColor;
                                        if (col != null) { r = col.Red; gg2 = col.Green; bb2 = col.Blue; break; }
                                    }
                                }
                            }
                        }
                        catch { }
                        string rgb = r + "," + gg2 + "," + bb2;
                        ColorGroup cg;
                        if (!colorIndex.TryGetValue(rgb, out cg))
                        {
                            cg = new ColorGroup { R = r, G = gg2, B = bb2 };
                            colorIndex[rgb] = cg;
                            dd.Colors.Add(cg);
                        }
                        try
                        {
                            var a = g.Approximation;
                            if (a == null || a.Points == null || a.Points.Length < 3) continue;
                            var pts = a.Points;
                            var loc = ((ITxLocatableObject)g).AbsoluteLocation;
                            foreach (var prim in a.Primitives)
                            {
                                var idx = prim.Indices;
                                if (idx == null || idx.Length < 3) continue;
                                var p0 = loc.Transform(pts[idx[0]]);
                                var p1 = loc.Transform(pts[idx[1]]);
                                var p2 = loc.Transform(pts[idx[2]]);
                                cg.Tris.Add(new float[]
                                {
                                    (float)(p0.X - ox), (float)(p0.Y - oy), (float)(p0.Z - oz),
                                    (float)(p1.X - ox), (float)(p1.Y - oy), (float)(p1.Z - oz),
                                    (float)(p2.X - ox), (float)(p2.Y - oy), (float)(p2.Z - oz)
                                });
                            }
                        }
                        catch { }
                    }
                    try { if (st != null) st.Reload(TxRepresentationLevel.United); } catch { }
                    if (dd.Colors.Count > 0) result.Add(dd);
                }
                return result;
            });
        }

        // ── 生成设备 STL 文件列表（按导出模式）──────────────────────────
        // mode 0：设备×颜色 拆分（每个颜色一个 STL）
        // mode 1：每设备 1 个 STL（合并全部颜色，不记录 RGB，导入后不染色）
        // mode 2：按工位×颜色 拆分（同工位同色合并为 1 个 STL，工位=路径顶层容器）
        private static List<DeviceFiles> BuildDeviceFiles(List<DeviceData> devData, string tmpDir, int mode,
            Action<string> onLog)
        {
            var deviceFiles = new List<DeviceFiles>();

            if (mode == 1)
            {
                // ── 模式1：每设备 1 个 STL（合并所有颜色） ──
                foreach (var dd in devData)
                {
                    var allTris = new List<float[]>();
                    foreach (var cg in dd.Colors)
                        if (cg.Tris.Count > 0) allTris.AddRange(cg.Tris);
                    if (allTris.Count == 0) continue;
                    string fileName = SafeFileName(dd.Name) + ".stl";
                    string outPath = Path.Combine(tmpDir, fileName);
                    WriteBinaryStl(outPath, allTris);
                    var df = new DeviceFiles { DeviceName = dd.Name };
                    df.Path.AddRange(dd.Path);
                    df.Files.Add(outPath);
                    // RgbList 留空 → 不染色
                    deviceFiles.Add(df);
                    SafeLog(onLog, "  " + fileName);
                }
                return deviceFiles;
            }

            if (mode == 2)
            {
                // ── 模式2：按工位×颜色 拆分（工位 = 路径顶层容器名） ──
                // 先按工位分组：工位名 → 其下所有设备的 (Path, Colors)
                var stationOrder = new List<string>();
                var stationColors = new Dictionary<string, Dictionary<string, List<float[]>>>();
                var stationPath = new Dictionary<string, List<string>>();
                var stationDevName = new Dictionary<string, string>();
                foreach (var dd in devData)
                {
                    string station = dd.Path.Count > 0 ? dd.Path[0] : dd.Name;
                    if (!stationColors.ContainsKey(station))
                    {
                        stationOrder.Add(station);
                        stationColors[station] = new Dictionary<string, List<float[]>>();
                        stationPath[station] = new List<string>();
                        stationDevName[station] = station;
                        // 工位的路径 = 去掉工位自身的那层（保留更深容器作为 CATIA 目录）
                        for (int i = 0; i < dd.Path.Count; i++)
                            if (dd.Path[i] != station)
                                stationPath[station].Add(dd.Path[i]);
                    }
                    foreach (var cg in dd.Colors)
                    {
                        if (cg.Tris.Count == 0) continue;
                        string rgb = cg.R + "," + cg.G + "," + cg.B;
                        List<float[]> tris;
                        if (!stationColors[station].TryGetValue(rgb, out tris))
                        {
                            tris = new List<float[]>();
                            stationColors[station][rgb] = tris;
                        }
                        tris.AddRange(cg.Tris);
                    }
                }
                foreach (string station in stationOrder)
                {
                    var df = new DeviceFiles { DeviceName = stationDevName[station] };
                    df.Path.AddRange(stationPath[station]);
                    foreach (var kv in stationColors[station])
                    {
                        if (kv.Value.Count == 0) continue;
                        string rgb = kv.Key;
                        string[] pc = rgb.Split(',');
                        string fileName = SafeFileName(station) + "_C" + pc[0] + "_" + pc[1] + "_" + pc[2] + ".stl";
                        string outPath = Path.Combine(tmpDir, fileName);
                        WriteBinaryStl(outPath, kv.Value);
                        df.Files.Add(outPath);
                        df.RgbList.Add(rgb);
                        SafeLog(onLog, "  " + fileName);
                    }
                    if (df.Files.Count > 0) deviceFiles.Add(df);
                }
                return deviceFiles;
            }

            // ── 模式0（默认）：设备×颜色 拆分 ──
            foreach (var dd in devData)
            {
                var df = new DeviceFiles { DeviceName = dd.Name };
                df.Path.AddRange(dd.Path);
                foreach (var cg in dd.Colors)
                {
                    if (cg.Tris.Count == 0) continue;
                    string fileName = SafeFileName(dd.Name) + "_C" + cg.R + "_" + cg.G + "_" + cg.B + ".stl";
                    string outPath = Path.Combine(tmpDir, fileName);
                    WriteBinaryStl(outPath, cg.Tris);
                    df.Files.Add(outPath);
                    df.RgbList.Add(cg.R + "," + cg.G + "," + cg.B);
                    SafeLog(onLog, "  " + fileName);
                }
                if (df.Files.Count > 0) deviceFiles.Add(df);
            }
            return deviceFiles;
        }

        // ── 几何体枚举：无条件递归 Reload(Detailed) 解锁后统一收集 ────────
        // 实测：设备默认 United Representation 加载时子几何不加载，枚举为 0；
        // 必须 obj.StorageObject.Reload(TxRepresentationLevel.Detailed)（GUI 的 Load Entity Level）
        // 解锁子几何后枚举。StorageObject 实际类型须是 TxLibraryStorage（TxLocalStorage 无 Reload）。
        // 策略：对设备及其所有后代组件无条件递归 Reload(Detailed)，再做一次整体枚举。
        private static List<ITxGeometry> EnumDeviceGeometries(ITxObject dev, ref TxLibraryStorage st, Action<string> onLog)
        {
            var list = new List<ITxGeometry>();

            // 1) 无条件递归 Reload(Detailed)：设备自身 + 所有后代组件
            ReloadRecursive(dev, onLog);

            // 2) 设备自身 StorageObject 引用（供调用方最终 United 卸载）
            try
            {
                object so = null;
                try { so = ((ITxStorable)dev).StorageObject; } catch { }
                if (so != null)
                {
                    st = so as TxLibraryStorage;
                    if (st == null)
                        SafeLog(onLog, "  ! " + dev.Name + " StorageObject 非 TxLibraryStorage(" + so.GetType().Name + ")，无 Reload");
                }
            }
            catch { }

            // 3) 统一枚举全部子几何（含 2D/3D 与 1D 几何）
            //    注意：TxSolid 不是 TxGeometry 子类（基类是 TxObjectBase，仅实现 ITxGeometry），
            //    所以过滤器必须同时包含 TxGeometry 与 TxSolid 两个具体类型。
            try
            {
                var coll = dev as ITxObjectCollection;
                if (coll != null)
                {
                    var geomFilter = new TxTypeFilter(new Type[] { typeof(TxGeometry), typeof(TxSolid) });
                    var geoms = coll.GetAllDescendants(geomFilter);
                    if (geoms != null)
                        foreach (object o in geoms)
                        {
                            var g = o as ITxGeometry;
                            if (g != null) list.Add(g);
                        }
                }
            }
            catch { }

            if (list.Count > 0)
                SafeLog(onLog, "  " + dev.Name + ": 几何 " + list.Count + " 个");
            return list;
        }

        /// <summary>递归遍历设备所有后代组件，无条件对每个对象 StorageObject.Reload(Detailed)。</summary>
        private static void ReloadRecursive(ITxObject node, Action<string> onLog)
        {
            if (node == null) return;

            // 当前节点 Reload(Detailed)
            try
            {
                object so = null;
                try { so = ((ITxStorable)node).StorageObject; } catch { }
                var nodeSt = so as TxLibraryStorage;
                if (nodeSt != null)
                    try { nodeSt.Reload(TxRepresentationLevel.Detailed); } catch { }
            }
            catch { }

            // 递归所有直接子组件（多路径：IEnumerable 或 GetAllDescendants 兜底）
            try
            {
                var kids = new List<ITxObject>();
                try
                {
                    foreach (object k in (System.Collections.IEnumerable)node)
                    {
                        var itx = k as ITxObject;
                        if (itx != null) kids.Add(itx);
                    }
                }
                catch { }
                if (kids.Count == 0)
                {
                    var coll = node as ITxObjectCollection;
                    if (coll != null)
                    {
                        try
                        {
                            var all = coll.GetAllDescendants(new TxNoTypeFilter());
                            if (all != null)
                                foreach (object o in all)
                                {
                                    var itx = o as ITxObject;
                                    if (itx != null) kids.Add(itx);
                                }
                        }
                        catch { }
                    }
                }
                foreach (var kid in kids)
                    if (kid != null)
                        ReloadRecursive(kid, onLog);
            }
            catch { }
        }

        // ── 写二进制 STL ────────────────────────────────────────────────
        private static void WriteBinaryStl(string outPath, List<float[]> tris)
        {
            using (var fs = new FileStream(outPath, FileMode.Create))
            using (var bw = new BinaryWriter(fs))
            {
                byte[] header = new byte[80];
                byte[] nb = Encoding.ASCII.GetBytes("STL " + Path.GetFileName(outPath));
                Array.Copy(nb, header, Math.Min(nb.Length, 80));
                bw.Write(header);
                bw.Write((uint)tris.Count);
                foreach (float[] t in tris)
                {
                    float x0 = t[0], y0 = t[1], z0 = t[2];
                    float x1 = t[3], y1 = t[4], z1 = t[5];
                    float x2 = t[6], y2 = t[7], z2 = t[8];
                    float ux = x1 - x0, uy = y1 - y0, uz = z1 - z0;
                    float vx = x2 - x0, vy = y2 - y0, vz = z2 - z0;
                    float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    float len = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len > 1e-12f) { nx /= len; ny /= len; nz /= len; } else { nx = 0; ny = 0; nz = 0; }
                    bw.Write(nx); bw.Write(ny); bw.Write(nz);
                    bw.Write(x0); bw.Write(y0); bw.Write(z0);
                    bw.Write(x1); bw.Write(y1); bw.Write(z1);
                    bw.Write(x2); bw.Write(y2); bw.Write(z2);
                    bw.Write((ushort)0);
                }
            }
        }

        // ── 连接 CATIA（强类型 INFITF）──────────────────────────────────
        private bool Connect(out string error)
        {
            error = null;
            try
            {
                _catia = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                try { _catia.Visible = true; } catch { }
                SafeLog(null, "✓ 已连接 CATIA");
                return true;
            }
            catch
            {
                _catia = null;
                error = "未检测到运行中的 CATIA V5，请先打开 CATIA";
                return false;
            }
        }

        // ── 前置确定导入目标文档：无活动文档/非 Product 文档时新建 ──────
        private void EnsureProductDocument(Action<string> onLog)
        {
            _productDoc = null;
            _docName = "";
            Document activeDoc = null;
            try { activeDoc = _catia.ActiveDocument; } catch { }
            if (activeDoc is ProductDocument existing)
            {
                _productDoc = existing;
                SafeLog(onLog, "[Catia] 使用当前 Product 文档");
            }
            else
            {
                try
                {
                    _productDoc = (ProductDocument)_catia.Documents.Add("Product");
                    SafeLog(onLog, "[Catia] 新建 Product 文档");
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[Catia] 新建 Product 文档失败: " + ex.Message);
                    throw;
                }
            }
            _docName = _productDoc.get_Name();
        }

        // ── 按 PS 目录树归类导入：逐级创建/复用嵌套 Product ─────────────
        private void ImportDeviceProducts(List<DeviceFiles> deviceFiles, Action<string> onLog)
        {
            ProductDocument productDoc = _productDoc;
            Product rootProduct = productDoc.Product;
            Products rootProducts = rootProduct.Products;
            _leafProducts = new List<Product>();
            int totalFiles = 0;
            for (int di = 0; di < deviceFiles.Count; di++)
            {
                var df = deviceFiles[di];
                // 沿 PS 路径逐级定位/创建嵌套 Product
                Products current = rootProducts;
                df.NavIndexes.Clear();
                for (int pi = 0; pi < df.Path.Count; pi++)
                {
                    string segName = df.Path[pi];
                    int segIdx = FindChildIndex(current, segName);
                    if (segIdx <= 0)
                    {
                        // 唯一命名（CATIA 同父级 Product 名不能重复，避免自动变 Product.1）
                        string segUnique = GetUniqueChildName(current, SafeFileName(segName));
                        Product segProd;
                        try { segProd = current.AddNewProduct(""); }
                        catch { segProd = current.AddNewComponent("Product", segUnique); }
                        try { segProd.set_PartNumber(segUnique); } catch { }
                        segIdx = current.Count;
                        SafeLog(onLog, "[Catia]  新建目录: " + segUnique + " (index " + segIdx + ")");
                    }
                    df.NavIndexes.Add(segIdx);
                    current = current.Item(segIdx).Products;
                }
                // 设备叶子 Product（唯一命名，避免与同名设备/目录冲突）
                string devUnique = GetUniqueChildName(current, SafeFileName(df.DeviceName));
                Product devProduct;
                try { devProduct = current.AddNewProduct(""); }
                catch { devProduct = current.AddNewComponent("Product", devUnique); }
                try { devProduct.set_PartNumber(devUnique); } catch { }
                int devIdx = current.Count;
                df.NavIndexes.Add(devIdx);
                _leafProducts.Add(devProduct);
                // 导入该设备全部颜色 STL
                Products target = devProduct.Products;
                df.SuccessCount = 0;
                for (int k = 0; k < df.Files.Count; k++)
                {
                    try
                    {
                        target.AddComponentsFromFiles(new object[] { df.Files[k] }, "All");
                        totalFiles++;
                        df.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        SafeLog(onLog, "  x 导入失败 " + Path.GetFileName(df.Files[k]) + ": " + ex.Message);
                    }
                }
                string full = df.Path.Count > 0 ? string.Join("/", df.Path) + "/" + df.DeviceName : df.DeviceName;
                SafeLog(onLog, "[Catia] 设备 " + full + " → Product, " + df.Files.Count + " 个 STL");
            }
            SafeLog(onLog, "[Catia] 导入完成: " + totalFiles + " 个 STL, " + deviceFiles.Count + " 个设备 Product");
        }

        /// <summary>在 Products 集合里按名字找子 Product 的索引(1-based)；不存在返回 0。</summary>
        private static int FindChildIndex(Products prods, string name)
        {
            try
            {
                for (int i = 1; i <= prods.Count; i++)
                {
                    Product p = prods.Item(i);
                    string pn = null, pn2 = null;
                    try { pn = p.get_PartNumber(); } catch { }
                    try { pn2 = p.get_Name(); } catch { }
                    if ((!string.IsNullOrEmpty(pn) && string.Equals(pn, SafeFileName(name), StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrEmpty(pn2) && string.Equals(pn2, SafeFileName(name), StringComparison.OrdinalIgnoreCase)))
                        return i;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 生成父 Products 下的唯一名：若 baseName 已被占用，追加 _2/_3/... 区分标记，
        /// 避免 CATIA 同父级 Product 重名被自动改成 Product.1 之类的默认命名。
        /// </summary>
        private static string GetUniqueChildName(Products parent, string baseName)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cnt = 0;
            try { cnt = parent.Count; } catch { }
            for (int i = 1; i <= cnt; i++)
            {
                try
                {
                    Product ch = parent.Item(i);
                    try { existing.Add(ch.get_Name()); } catch { }
                    try { existing.Add(ch.get_PartNumber()); } catch { }
                }
                catch { }
            }
            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; i <= 999; i++)
            {
                string tryName = baseName + "_" + i;
                if (!existing.Contains(tryName)) return tryName;
            }
            return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // ── 测试：把设备集合导出为 CGR 再导回，测速 ──────────────────────
        // 用 Product.ExportDocument(path, "CGR") 导出根下全部设备为一个 .cgr，
        // 再用 AddComponentsFromFiles 导回新建的容器，打印两段时间。
        private void TestCgrRoundtrip(string tmpDir, Action<string> onLog)
        {
            try
            {
                SafeLog(onLog, "[CGR测试] 开始：导出设备集合为 CGR → 导回");

                ProductDocument productDoc = _productDoc;
                if (productDoc == null)
                {
                    SafeLog(onLog, "[CGR测试] 无目标文档，跳过");
                    return;
                }
                Product rootProduct = productDoc.Product;
                Products rootProducts = rootProduct.Products;

                // 1) 导出 CGR（实测结论：doc.ExportData(path,"cgr") 才生成完整几何，
                //    ExportData 是 ProductDocument(文档) 的方法，不是 Product 的方法）
                string cgrPath = Path.Combine(tmpDir, "cgr_test_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".cgr");
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    object docObj = productDoc;
                    Type t = docObj.GetType();
                    // 首选 ExportData(path, "cgr")——实测唯一生成完整几何的通道
                    t.InvokeMember("ExportData",
                        System.Reflection.BindingFlags.InvokeMethod, null, docObj,
                        new object[] { cgrPath, "cgr" });
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[CGR测试] 导出 CGR 失败: " + ex.Message);
                    return;
                }
                sw1.Stop();
                long exportMs = sw1.ElapsedMilliseconds;
                long size = 0;
                try { size = new FileInfo(cgrPath).Length; } catch { }
                SafeLog(onLog, "[CGR测试] 导出耗时 " + exportMs + " ms, 文件 " + size + " 字节");

                // 2) 导回 CGR（新建容器，避免污染当前树）
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                string containerName = GetUniqueChildName(rootProducts, "CGR_Test_Import");
                Product container;
                try { container = rootProducts.AddNewProduct(""); }
                catch { container = rootProducts.AddNewComponent("Product", containerName); }
                try { container.set_PartNumber(containerName); } catch { }
                try
                {
                    container.Products.AddComponentsFromFiles(new object[] { cgrPath }, "All");
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[CGR测试] 导回 CGR 失败: " + ex.Message);
                    return;
                }
                sw2.Stop();
                long importMs = sw2.ElapsedMilliseconds;
                SafeLog(onLog, "[CGR测试] 导回耗时 " + importMs + " ms");

                SafeLog(onLog, "[CGR测试] 完成：导出 " + exportMs + " ms + 导回 " + importMs + " ms = " + (exportMs + importMs) + " ms");
            }
            catch (Exception ex)
            {
                SafeLog(onLog, "[CGR测试] 异常: " + ex.Message);
            }
        }

        // ── 强类型染色（遍历 设备叶子Product → 子STL实例，失败回退 VBS）──
        private bool TryColorStrongTyped(List<DeviceFiles> deviceFiles, Action<string> onLog)
        {
            try
            {
                ProductDocument productDoc = _catia.ActiveDocument as ProductDocument;
                if (productDoc == null) return false;
                Selection sel = productDoc.Selection;
                for (int di = 0; di < deviceFiles.Count; di++)
                {
                    var df = deviceFiles[di];
                    if (di >= _leafProducts.Count) break;
                    Product devProduct = _leafProducts[di];
                    Products devProducts = devProduct.Products;
                    int n = Math.Min(df.SuccessCount, df.RgbList.Count);
                    for (int k = 0; k < n; k++)
                    {
                        Product prod = devProducts.Item(k + 1);
                        string[] pc = df.RgbList[k].Split(',');
                        int r = int.Parse(pc[0]);
                        int g = int.Parse(pc[1]);
                        int b = int.Parse(pc[2]);
                        sel.Clear();
                        sel.Add(prod);
                        INFITF.VisPropertySet vp = sel.VisProperties;
                        vp.SetRealColor(r, g, b, 1);
                        int rr, gg, bb;
                        vp.GetRealColor(out rr, out gg, out bb);
                        string ok = (Math.Abs(rr - r) < 2 && Math.Abs(gg - g) < 2 && Math.Abs(bb - b) < 2)
                                    ? "OK" : "MISMATCH";
                        SafeLog(onLog, "  [" + di + "." + k + "] " + prod.get_Name() + " (" + r + "," + g + "," + b + ") => ("
                            + rr + "," + gg + "," + bb + ") " + ok);
                        sel.Clear();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                SafeLog(onLog, "[Catia] 强类型染色失败: " + ex.Message);
                return false;
            }
        }

        // ── cscript VBS 染色（已验证通道；按 NavIndexes 定位嵌套叶子Product）──
        private void ColorViaVbs(string docName, List<DeviceFiles> deviceFiles, string outDir, Action<string> onLog)
        {
            // 颜色映射：按导出顺序(ci 从1递增)对应每个 STL 的 RGB
            var mapSb = new StringBuilder();
            int ciTotal = 0;
            foreach (var df in deviceFiles)
            {
                int n = Math.Min(df.SuccessCount, df.RgbList.Count);
                for (int k = 0; k < n; k++)
                {
                    var rgb = df.RgbList[k];
                    ciTotal++;
                    string[] pc = rgb.Split(',');
                    mapSb.Append(ciTotal == 1 ? "    If ci = " + ciTotal + " Then\n"
                                              : "    ElseIf ci = " + ciTotal + " Then\n");
                    mapSb.Append("      r = " + pc[0] + " : g = " + pc[1] + " : b = " + pc[2] + "\n");
                }
            }
            mapSb.Append("    End If\n");

            var sb = new StringBuilder();
            sb.Append("On Error Resume Next\n");
            sb.Append("Dim CATIA, docs, doc, products1, devProd, devProds, prod, sel\n");
            sb.Append("Dim r, g, b, rr, gg, bb, ok, ci, m, si\n");
            sb.Append("Set CATIA = GetObject(, \"CATIA.Application\")\n");
            sb.Append("If Err.Number <> 0 Then WScript.Echo \"FATAL connect\" : WScript.Quit 1\n");
            sb.Append("CATIA.Visible = True\n");
            sb.Append("Set docs = CATIA.Documents\n");
            sb.Append("If \"\" <> \"" + docName + "\" Then\n");
            sb.Append("  Set doc = docs.Item(\"" + docName + "\")\n");
            sb.Append("  If Err.Number <> 0 Then Err.Clear : Set doc = docs.Item(\"" + docName + ".CATProduct\")\n");
            sb.Append("  If Err.Number <> 0 Then Err.Clear : Set doc = CATIA.ActiveDocument\n");
            sb.Append("Else\n");
            sb.Append("  Set doc = CATIA.ActiveDocument\n");
            sb.Append("End If\n");
            sb.Append("If Err.Number <> 0 Then WScript.Echo \"FATAL find doc\" : WScript.Quit 1\n");
            sb.Append("doc.Activate\n");
            sb.Append("Err.Clear\n");
            sb.Append("Set products1 = doc.Product.Products\n");
            sb.Append("Set sel = doc.Selection\n");
            sb.Append("ci = 0\n");

            int di = 0;
            foreach (var df in deviceFiles)
            {
                if (df.NavIndexes == null || df.NavIndexes.Count == 0) continue;
                string nav = "products1";
                for (int m = 0; m < df.NavIndexes.Count; m++)
                {
                    // nav[0] 是 products1 的子索引；其后的每一项是前一项 .Products 的子索引
                    if (m == 0)
                        nav = nav + ".Item(" + df.NavIndexes[m] + ")";
                    else
                        nav = nav + ".Products.Item(" + df.NavIndexes[m] + ")";
                }
                sb.Append("Set devProd = " + nav + "\n");
                sb.Append("Set devProds = devProd.Products\n");
                sb.Append("For si = 1 To devProds.Count\n");
                sb.Append("  Set prod = devProds.Item(si)\n");
                sb.Append("  ci = ci + 1\n");
                sb.Append(mapSb.ToString());
                sb.Append("  sel.Clear : sel.Add prod\n");
                sb.Append("  If Err.Number = 0 Then\n");
                sb.Append("    sel.VisProperties.SetRealColor r, g, b, 1\n");
                sb.Append("    If Err.Number = 0 Then\n");
                sb.Append("      rr = 0 : gg = 0 : bb = 0\n");
                sb.Append("      sel.VisProperties.GetRealColor rr, gg, bb\n");
                sb.Append("      If rr = r And gg = g And bb = b Then ok = \"OK\" Else ok = \"MISMATCH\"\n");
                sb.Append("      WScript.Echo \"[" + di + ".\" & si & \"] \" & prod.Name & \" (\" & r & \",\" & g & \",\" & b & \") => (\" & rr & \",\" & gg & \",\" & bb & \") \" & ok\n");
                sb.Append("    Else\n");
                sb.Append("      WScript.Echo \"[" + di + ".\" & si & \"] SetColor失败: \" & Err.Description\n");
                sb.Append("    End If\n");
                sb.Append("    Err.Clear\n");
                sb.Append("  End If\n");
                sb.Append("  sel.Clear\n");
                sb.Append("Next\n");
                di++;
            }
            sb.Append("WScript.Echo \"DONE\"\n");

            string scriptPath = Path.Combine(outDir, "color_auto.vbs");
            SysFile.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);

            var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = "cscript.exe";
            proc.StartInfo.Arguments = "//NoLogo \"" + scriptPath + "\"";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.RedirectStandardOutput = true;
            proc.Start();
            string vbsOut = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            SafeLog(onLog, "=== cscript 染色输出 ===");
            if (!string.IsNullOrEmpty(vbsOut))
            {
                string[] lines = vbsOut.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                for (int li = 0; li < lines.Length; li++) SafeLog(onLog, lines[li]);
            }
            else SafeLog(onLog, "(无输出 - cscript 可能连接失败)");
        }

        private static string SafeFileName(string name)
        {
            var inv = Path.GetInvalidFileNameChars();
            foreach (char c in inv) name = name.Replace(c.ToString(), "_");
            return name.Replace("(", "").Replace(")", "").Replace(" ", "_");
        }

        private static void SafeLog(Action<string> cb, string msg)
        { if (cb != null) try { cb(msg); } catch { } }
        private static void SafeComplete(Action<bool, string> cb, bool ok, string msg)
        { if (cb != null) try { cb(ok, msg); } catch { } }
    }
}
