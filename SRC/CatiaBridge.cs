// CatiaBridge.cs  —  C# 7.3
// CATIA V5 COM 互操作：导出点球 / 导出插枪
//
// 依赖 COM 引用（位于 CATIA 安装目录 intel_a\code\bin\）：
//   INFITF.dll / MECMOD.dll / HybridShapeTypeLib.dll / ProductStructureTypeLib.dll
//
// 修改说明：
// 1. 保持所有公开方法签名不变（Connect, ExportBalls, ExportGuns）。
// 2. 点球导出：在几何集中创建由原点 + 三条直线段组成的“基准坐标系”（通过 AddNewLinePtPt）。
// 3. 插枪导出：勾选“导出TCP坐标”时，为每个焊枪操作创建公共零件，
//    在其中为每个焊点创建由原点和三轴线表示的 TCP 坐标系。
// 4. 解决 AddNewLinePtDir 重载错误，统一使用 AddNewLinePtPt 生成轴线。
// 5. Connect() 不再自动启动 CATIA：仅尝试附加到已运行的 CATIA 实例，
//    未检测到时返回 false 并提示用户先打开 CATIA。
// 6. 其他资源管理、性能优化与异常处理。
// 7. 修复 ExportToCurrentDoc=true + 活动文档为 Product 时新建子 Part 失败：
//    AddNewComponent("Part",...) 不会切换 _catia.ActiveDocument，
//    必须经 newProd.ReferenceProduct.Parent 取宿主 PartDocument。
//    新增 ResolvePartDocFromComponent + TryFindNewlyAddedPartDoc 辅助方法。
//    同时把 ExportGuns 里 TCP 公共零件的同类逻辑一起修了。
// 8. ExportToCurrentDoc=true 且活动文档已是 Part 时，若用户填了自定义零件名，
//    现在会应用到当前 Part 上（仅 set_Name，不动 PartNumber），与其它分支一致。
// 9. 插枪导出统一为「共享几何」单一模式：所有实例复用同一份 CGR Reference，
//    实例名改为焊点名。原「独立命名」模式（每焊点复制一份 CGR）已移除。
//    实例改名根因：实例名存储在**父级 Reference** 中，必须经
//      container.ReferenceProduct.Products.Item(i) 写入；
//    写在 instance 路径 container.Products.Item(i) 上会被解析层静默丢弃
//    （不抛异常、读回不变）。与线程模型（MTA/STA）和调用时机均无关。

using HybridShapeTypeLib;
using INFITF;
using MECMOD;
using ProductStructureTypeLib;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms.DataVisualization.Charting;
using Tecnomatix.Engineering;
using SysDir = System.IO.Directory;
using SysFile = System.IO.File;
using SysPath = System.IO.Path;

namespace TxTools.ExportGun
{
    public enum ExportFormat { Xml3d, CATProduct }
    public enum BallExportOption { TrajectoryAndBall, TrajectoryOnly, BallOnly }

    public class GunExportParams
    {
        public List<OperationInfo> Operations;
        public bool ExportTCP;
        public bool GunOriginAtTCP;
        public string CustomModelPath;
        public Dictionary<string, string> PerOpModelPaths;
        public string CustomProductName;
        public ExportFormat Format;
        public string OutputPath;
        public double[] RefMatrix;
        public string RefName;
        public PointType PointFilter;
        public bool UseMfgName;

        // —— 选中点白名单（key=PointKey(opName,ptName)）；null=不过滤(全部) ——
        public HashSet<string> SelectedKeys;

        // —— TCP 覆盖：TcpCustomMatrix 优先；否则按 TcpName 在该操作工具上解析 ——
        //    两者都为空 → 沿用 GunInfo 中已解析的默认 TCP
        public string TcpName;
        public double[] TcpCustomMatrix;

        // —— 焊枪导出模式（默认共享几何，体积最小） ——
    }

    public class BallExportParams
    {
        public List<OperationInfo> Operations;
        public bool ExportToCurrentDoc;
        public BallExportOption Option;
        public double BallDiameter;
        public string OutputPath;
        public PointType PointFilter;
        public bool UseMfgName;
        public string GeomSetName;
        public string NamePrefix;
        public string CustomPartName;
        public double[] RefMatrix;
        public string RefName;

        // —— 选中点白名单（key=PointKey(opName,ptName)）；null=不过滤(全部) ——
        public HashSet<string> SelectedKeys;
    }

    public class ExportProgress
    {
        public int Total;
        public int Current;
        public string CurrentItem;
    }

    public class CatiaBridge : IDisposable
    {
        private INFITF.Application _catia;
        private bool _disposed;

        // ════════════════════════════════════════════════════════════
        //  选中点过滤辅助
        // ════════════════════════════════════════════════════════════
        /// <summary>选中点唯一键：操作名 + 点名（与 UI 侧保持一致）。</summary>
        public static string PointKey(string opName, string ptName)
        {
            return (opName ?? "") + "\u0001" + (ptName ?? "");
        }

        /// <summary>白名单为 null 时视为全选；否则按 key 判断。</summary>
        private static bool IsPointSelected(HashSet<string> keys, string opName, string ptName)
        {
            return keys == null || keys.Contains(PointKey(opName, ptName));
        }

        /// <summary>计算 TCP 相对工具的局部偏移 = Inv(toolWorld) * tcpWorld。</summary>
        private static double[] ComputeTcpRelTool(double[] toolWorld, double[] tcpWorld)
        {
            try
            {
                if (toolWorld == null || tcpWorld == null) return null;
                var rel = TxTransformation.Multiply(
                    PsReader.ArrToTxPublic(toolWorld).Inverse,
                    PsReader.ArrToTxPublic(tcpWorld));
                return PsReader.TxToArr(rel);
            }
            catch { return null; }
        }

        /// <summary>
        /// 尝试连接到 <b>已经运行</b> 的 CATIA V5 实例。
        /// 若 CATIA 未启动，本方法不会自动启动 CATIA，而是返回 false 并通过 error 给出提示。
        /// 调用方在失败时必须停止后续 Export* 调用。
        /// </summary>
        public bool Connect(out string error)
        {
            error = null;
            try
            {
                _catia = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                // 仅在成功附加到已运行实例时尝试设置可见性，避免任何额外副作用。
                try { _catia.Visible = true; } catch { }
                return true;
            }
            catch (COMException)
            {
                // ROT 中未找到 CATIA.Application（典型 HRESULT MK_E_UNAVAILABLE 0x800401E3），
                // 说明 CATIA 没有运行；按需求不再自动启动 CATIA。
                _catia = null;
                error = "未检测到正在运行的 CATIA V5，请先手动打开 CATIA 后再执行此操作。";
                return false;
            }
            catch (Exception ex)
            {
                _catia = null;
                error = "连接 CATIA V5 失败：" + ex.Message + "。请确认 CATIA V5 已完全启动后再试。";
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  导出点球（含基准坐标系）
        // ════════════════════════════════════════════════════════════
        public void ExportBalls(BallExportParams p, Action<ExportProgress> onProgress, Action<string> onLog)
        {
            if (_catia == null) throw new InvalidOperationException("CATIA 未连接");

            INFITF.Window initialWindow = null;
            try { initialWindow = _catia.ActiveWindow; } catch { }

            string refInfo = p.RefMatrix != null ? p.RefName ?? "自定义参考系" : "世界坐标系";
            onLog($"[Catia] 导出点球，参考坐标系：{refInfo}");

            string customPartName = string.IsNullOrWhiteSpace(p.CustomPartName) ? null : p.CustomPartName.Trim();

            var allPts = new List<PointInfo>();
            foreach (var op in p.Operations)
            {
                if (op?.Points == null) continue;
                foreach (var pt in op.Points)
                    if (IsPointSelected(p.SelectedKeys, op.Name, pt.Name))
                        allPts.Add(pt);
            }
            if (allPts.Count == 0) { onLog("  ! 无可导出的点（请检查勾选）"); return; }

            int total = allPts.Count;
            string geomSetName = string.IsNullOrEmpty(p.GeomSetName) ? "Geometry_Spheres" : p.GeomSetName;
            string namePrefix = string.IsNullOrEmpty(p.NamePrefix) ? "SPHERE" : p.NamePrefix;
            double radius = p.BallDiameter / 2.0;

            PartDocument partDoc = null;
            try
            {
                // —— 获取/创建 PartDocument ——
                if (p.ExportToCurrentDoc)
                {
                    Document active = _catia.ActiveDocument;
                    if (active is PartDocument pd)
                    {
                        partDoc = pd;
                        // 用户在 PartDocument 下指定了自定义零件名，应用到当前 Part 上
                        // 仅改 Part.Name，不动 PartNumber，避免破坏用户已有的零件号体系
                        if (customPartName != null)
                            try { partDoc.Part.set_Name(customPartName); } catch { }
                        onLog("[Catia] 使用当前 Part：" + active.get_Name());
                    }
                    else if (active is ProductDocument prodDoc)
                    {
                        // 在 Product 下新建 Part 子组件
                        string newPartNum = customPartName ?? "";
                        Product newProd = prodDoc.Product.Products.AddNewComponent("Part", newPartNum);
                        if (customPartName != null) try { newProd.set_Name(customPartName); } catch { }

                        // 关键修复：AddNewComponent 不会切换 _catia.ActiveDocument，
                        // 直接 _catia.ActiveDocument as PartDocument 会拿到 null（仍是 Product 文档）。
                        // 正确做法：从新建的 Product 上经 ReferenceProduct.Parent 取宿主 PartDocument。
                        partDoc = ResolvePartDocFromComponent(newProd, onLog);

                        // 退化路径：极个别版本若 ReferenceProduct.Parent 不可用，尝试从 Documents 集合中按文件名匹配新建文档
                        if (partDoc == null) partDoc = TryFindNewlyAddedPartDoc(newProd, onLog);

                        if (partDoc != null && customPartName != null)
                            try { partDoc.Part.set_Name(customPartName); } catch { }

                        onLog("[Catia] 在 Product 下新建 Part：" + (customPartName ?? newProd.get_PartNumber()));
                    }
                }
                else
                {
                    partDoc = (PartDocument)_catia.Documents.Add("Part");
                    if (customPartName != null)
                    {
                        try { partDoc.Part.set_Name(customPartName); } catch { }
                        try { partDoc.Product.set_PartNumber(customPartName); } catch { }
                        try { partDoc.Product.set_Name(customPartName); } catch { }
                    }
                    onLog("[Catia] 新建 Part：" + partDoc.get_Name());
                }

                if (partDoc == null) { onLog("[Catia] x 无法获取/创建 PartDocument"); return; }

                Part part = partDoc.Part;
                HybridShapeFactory sf = (HybridShapeFactory)part.HybridShapeFactory;

                // —— 获取/创建几何集 ——
                HybridBody geomSet = null;
                HybridBodies bodies = part.HybridBodies;
                for (int i = 1; i <= bodies.Count; i++)
                {
                    HybridBody b = bodies.Item(i);
                    if (b.get_Name() == geomSetName) { geomSet = b; break; }
                }
                if (geomSet == null) { geomSet = bodies.Add(); geomSet.set_Name(geomSetName); }

                // [新增] 创建基准坐标系（由原点 + 三条线段表示）
                double[] identity = new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                CreateAxisVisual(part, geomSet, sf, p.RefMatrix ?? identity, "参考坐标系", onLog);

                // —— 阶段1：创建点 ——
                onLog($"[Catia] 创建 {total} 个点...");
                var createdPoints = new List<HybridShapePointCoord>();
                for (int i = 0; i < allPts.Count; i++)
                {
                    try
                    {
                        double[] m = allPts[i].TCPMatrix;
                        if (p.RefMatrix != null && !PsReader.IsIdentity(p.RefMatrix))
                            m = PsReader.ToRelative(m, p.RefMatrix);
                        HybridShapePointCoord pt = sf.AddNewPointCoord(m[3], m[7], m[11]);
                        geomSet.AppendHybridShape(pt);
                        createdPoints.Add(pt);
                    }
                    catch (Exception ex) { onLog($"  ! 点{i + 1} 异常：{ex.Message}"); continue; }
                    onProgress?.Invoke(new ExportProgress { Total = total * 2, Current = i + 1, CurrentItem = allPts[i].Name });
                }
                onLog($"[Catia] 已创建 {createdPoints.Count} 个点");
                part.Update();

                // —— 阶段2：创建球体 ——
                onLog($"[Catia] 创建球体，半径={radius}mm...");
                int ok = 0, fail = 0;
                for (int i = 0; i < createdPoints.Count; i++)
                {
                    string ballName = Sanitize(namePrefix + "_" + (i < allPts.Count ? allPts[i].Name : (i + 1).ToString()));
                    try
                    {
                        Reference ptRef = part.CreateReferenceFromObject(createdPoints[i]);
                        HybridShapeSphere sphere = sf.AddNewSphere(ptRef, null, radius, -90.0, 90.0, 0.0, 360.0);
                        geomSet.AppendHybridShape(sphere);
                        sphere.set_Name(ballName);
                        ok++;
                        Marshal.ReleaseComObject(ptRef);
                    }
                    catch (Exception ex) { onLog($"  ! [{ballName}] {ex.Message}"); fail++; continue; }
                    onProgress?.Invoke(new ExportProgress { Total = createdPoints.Count * 2, Current = createdPoints.Count + i + 1, CurrentItem = ballName });
                }
                part.Update();
                onLog($"[Catia] 点球完成：成功 {ok}，失败 {fail}");

                if (!p.ExportToCurrentDoc)
                {
                    string outFile = BuildPath(p.OutputPath, Sanitize(customPartName ?? "WeldPoints_Spheres"), "CATPart");
                    partDoc.SaveAs(outFile);
                    onLog("[Catia] 已保存：" + outFile);
                }
                else partDoc.Save();
            }
            catch (Exception ex) { onLog($"[Catia] x 导出点球异常：{ex.Message}"); }
            finally { if (initialWindow != null) try { initialWindow.Activate(); } catch { } }
        }

        // [新增] 创建由原点 + 三轴线段表示的坐标系（使用 AddNewLinePtPt）
        private void CreateAxisVisual(Part part, HybridBody geomSet, HybridShapeFactory sf,
            double[] matrix, string baseName, Action<string> onLog)
        {
            try
            {
                double ox = matrix[3], oy = matrix[7], oz = matrix[11];
                double xx = matrix[0], xy = matrix[4], xz = matrix[8];
                double yx = matrix[1], yy = matrix[5], yz = matrix[9];
                double zx = matrix[2], zy = matrix[6], zz = matrix[10];
                const double LENGTH = 5.0;

                // 原点
                HybridShapePointCoord origin = sf.AddNewPointCoord(ox, oy, oz);
                geomSet.AppendHybridShape(origin);
                origin.set_Name(baseName + "_原点");

                // 创建 X 轴线
                CreateAxisLine(part, geomSet, sf, origin, ox, oy, oz, xx, xy, xz, LENGTH, baseName + "_X轴");
                // 创建 Y 轴线
                CreateAxisLine(part, geomSet, sf, origin, ox, oy, oz, yx, yy, yz, LENGTH, baseName + "_Y轴");
                // 创建 Z 轴线
                CreateAxisLine(part, geomSet, sf, origin, ox, oy, oz, zx, zy, zz, LENGTH, baseName + "_Z轴");

                part.Update();
                onLog($"  基准坐标系 '{baseName}' 已创建（原点 ({ox:F3},{oy:F3},{oz:F3})）");
            }
            catch (Exception ex) { onLog($"  ! 创建基准坐标系失败：{ex.Message}"); }
        }

        // 辅助：创建一条轴线（从原点沿方向创建端点，再连直线）
        private void CreateAxisLine(Part part, HybridBody geomSet, HybridShapeFactory sf,
            HybridShapePointCoord origin,
            double ox, double oy, double oz,
            double dx, double dy, double dz, double length, string name)
        {
            double ex = ox + dx * length, ey = oy + dy * length, ez = oz + dz * length;
            HybridShapePointCoord endPt = sf.AddNewPointCoord(ex, ey, ez);
            geomSet.AppendHybridShape(endPt);

            Reference refOrigin = part.CreateReferenceFromObject(origin);
            Reference refEnd = part.CreateReferenceFromObject(endPt);
            HybridShapeLinePtPt line = sf.AddNewLinePtPt(refOrigin, refEnd);
            geomSet.AppendHybridShape(line);
            line.set_Name(name);

            Marshal.ReleaseComObject(refEnd);
            Marshal.ReleaseComObject(refOrigin);
        }

        // ════════════════════════════════════════════════════════════
        //  从一个新建的 Part 子组件解析其宿主 PartDocument
        //
        //  背景：ProductDocument.Product.Products.AddNewComponent("Part", ...)
        //        会创建一个新的 Part 子组件，但 _catia.ActiveDocument 仍指向
        //        外层的 Product，直接 cast 为 PartDocument 会得到 null。
        //
        //  正确链路（CATIA V5 标准）：
        //        Product (子组件) → ReferenceProduct → Parent → 即对应的 PartDocument
        // ════════════════════════════════════════════════════════════
        private PartDocument ResolvePartDocFromComponent(Product component, Action<string> onLog)
        {
            if (component == null) return null;
            try
            {
                Product refProd = component.ReferenceProduct;   // 实际承载几何的 Product
                if (refProd == null) return null;
                Document parent = refProd.Parent as Document;   // 该 Product 所属文档
                PartDocument pd = parent as PartDocument;
                if (pd != null) return pd;
                if (parent != null && onLog != null)
                    onLog("    ! ReferenceProduct.Parent 不是 PartDocument，实际类型：" + parent.GetType().Name);
            }
            catch (Exception ex)
            {
                if (onLog != null) onLog("    ! ReferenceProduct.Parent 解析失败：" + ex.Message);
            }
            return null;
        }

        // 退化路径：遍历 Documents 集合按候选名匹配（仅在 ReferenceProduct.Parent 失败时使用）
        private PartDocument TryFindNewlyAddedPartDoc(Product component, Action<string> onLog)
        {
            if (component == null || _catia == null) return null;
            string partNumber = null;
            string name = null;
            try { partNumber = component.get_PartNumber(); } catch { }
            try { name = component.get_Name(); } catch { }

            try
            {
                Documents docs = _catia.Documents;
                int count = docs.Count;
                // 从后向前找，新建的文档通常排在末尾
                for (int i = count; i >= 1; i--)
                {
                    Document d;
                    try { d = docs.Item(i); } catch { continue; }
                    PartDocument pd = d as PartDocument;
                    if (pd == null) continue;

                    string dname = null;
                    try { dname = d.get_Name(); } catch { }
                    // CATIA 文档名通常形如 "PartXX.CATPart" 或 "<PartNumber>.CATPart"
                    if (!string.IsNullOrEmpty(dname))
                    {
                        string stem = SysPath.GetFileNameWithoutExtension(dname);
                        if (!string.IsNullOrEmpty(partNumber) &&
                            string.Equals(stem, partNumber, StringComparison.OrdinalIgnoreCase)) return pd;
                        if (!string.IsNullOrEmpty(name) &&
                            string.Equals(stem, name, StringComparison.OrdinalIgnoreCase)) return pd;
                    }
                }
                if (onLog != null) onLog("    ! 遍历 Documents 未匹配到新建 Part 文档");
            }
            catch (Exception ex)
            {
                if (onLog != null) onLog("    ! Documents 集合遍历失败：" + ex.Message);
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  导出插枪：共享几何（Copy+Paste 复用同一份 CGR Reference）
        //  实例名通过父级 ReferenceProduct 路径改为焊点名
        // ════════════════════════════════════════════════════════════
        public void ExportGuns(GunExportParams p, Action<ExportProgress> onProgress, Action<string> onLog)
        {
            if (_catia == null) throw new InvalidOperationException("CATIA 未连接");
            onLog?.Invoke("[Catia] 共享几何导出（实例复用同一份 CGR，实例名=焊点名）");
            ExportGunsShared(p, onProgress, onLog);
        }

        private void ExportGunsShared(GunExportParams p, Action<ExportProgress> onProgress, Action<string> onLog)
        {
            // 保存并关闭弹窗 / 刷新
            bool savedDisplayAlerts = true;
            bool savedRefreshDisplay = true;
            try { savedDisplayAlerts = (bool)((dynamic)_catia).DisplayFileAlerts; ((dynamic)_catia).DisplayFileAlerts = false; } catch { }
            try { savedRefreshDisplay = (bool)((dynamic)_catia).RefreshDisplay; ((dynamic)_catia).RefreshDisplay = false; } catch { }

            INFITF.Window initialWindow = null;
            try { initialWindow = _catia.ActiveWindow; } catch { }

            try
            {
                // 1. Product 文档：优先复用活动 Product，否则新建
                ProductDocument productDoc;
                if (_catia.ActiveDocument is ProductDocument existingPd)
                {
                    productDoc = existingPd;
                    onLog?.Invoke("[Catia] 使用当前 Product 文档");
                }
                else
                {
                    productDoc = (ProductDocument)_catia.Documents.Add("Product");
                    if (!string.IsNullOrWhiteSpace(p.CustomProductName))
                        try { productDoc.Product.set_PartNumber(p.CustomProductName.Trim()); } catch { }
                    onLog?.Invoke("[Catia] 新建 Product 文档");
                }

                Product rootProduct = productDoc.Product;
                Products rootProducts = rootProduct.Products;
                Selection sel = productDoc.Selection;

                // 2. 统计总数
                int total = 0;
                foreach (var op in p.Operations)
                {
                    if (op.Gun == null || op.Points == null) continue;
                    foreach (var pt in op.Points)
                        if (IsPointSelected(p.SelectedKeys, op.Name, pt.Name)) total++;
                }
                int current = 0;

                // 3. Reference 复用缓存（跨 Operation）
                var sourceCache = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);

                // 待改名任务：两阶段处理（插入循环只收集，全部插入完成后统一改名）
                var pendingRenames = new List<PendingCatiaRename>();

                // 4. 阶段 A：主循环（只做插入+定位，收集改名任务）
                foreach (var op in p.Operations)
                {
                    var gun = op.Gun;
                    if (gun == null || op.Points == null) continue;

                    string modelPath = ResolveModelPath(p, op, gun);
                    if (modelPath == null)
                    {
                        onLog?.Invoke($"  ! [{op.Name}] 未找到模型文件");
                        continue;
                    }

                    var opPts = new List<PointInfo>();
                    foreach (var pt in op.Points)
                        if (IsPointSelected(p.SelectedKeys, op.Name, pt.Name)) opPts.Add(pt);
                    if (opPts.Count == 0) continue;

                    // TCP 覆盖
                    ApplyTcpOverride(p, op, gun, onLog);

                    // 容器：同名冲突自动加后缀
                    // 关键修正：op.Name 含 ':' 等 CATIA 非法字符，必须 Sanitize
                    string containerName = GetUniqueChildName(rootProducts, Sanitize(op.Name));
                    Product container = rootProducts.AddNewComponent("Product", containerName);
                    int containerIndex = rootProducts.Count;   // 按索引锁定，不依赖名字
                    try { container.set_PartNumber(containerName); } catch { }
                    // 容器改名同样并入批处理（ContainerIndex=0 表示根层级）——
                    // 它是根层级普通 Product，作为"CGR 实例不可改名"假设的对照组
                    pendingRenames.Add(new PendingCatiaRename
                    {
                        ProductsColl = rootProducts,
                        ContainerIndex = 0,
                        InstanceIndex = containerIndex,
                        CurrentName = GetInstanceNameByReflection(rootProducts, containerIndex),
                        TargetName = containerName,
                        Kind = "容器"
                    });
                    Products targetProducts = container.Products;

                    // 准备源实例
                    Product sourceInst;
                    int startIdx = 0;

                    if (!sourceCache.TryGetValue(modelPath, out sourceInst))
                    {
                        try
                        {
                            targetProducts.AddComponentsFromFiles(new object[] { modelPath }, "All");
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"    x [{op.Name}] 加载 CGR 失败: {ex.Message}");
                            continue;
                        }

                        sourceInst = targetProducts.Item(targetProducts.Count);
                        int sourceIdx = targetProducts.Count;   // 记录源实例在 targetProducts 里的索引
                        sourceCache[modelPath] = sourceInst;

                        // 首焊点：源实例定位 + 反射改名
                        var pt0 = opPts[0];
                        current++;
                        onProgress?.Invoke(new ExportProgress { Total = total, Current = current, CurrentItem = pt0.Name });

                        try
                        {
                            double[] placed0 = ComputePlaced(p, gun, pt0);
                            sourceInst.Position.SetComponents(ToSetComp(placed0));

                            // 记录改名任务：读当前 Name（CATIA 分配的，如 FFM130-X-0759.1）作为锚点
                            string curName = GetInstanceNameByReflection(targetProducts, sourceIdx);
                            if (!string.IsNullOrEmpty(curName))
                            {
                                pendingRenames.Add(new PendingCatiaRename
                                {
                                    ProductsColl = targetProducts,
                                    ContainerIndex = containerIndex,
                                    InstanceIndex = sourceIdx,
                                    CurrentName = curName,
                                    TargetName = Sanitize(pt0.Name),
                                    Kind = "实例"
                                });
                            }
                            else
                            {
                                onLog?.Invoke($"    ! 首实例读 Name 失败 [{pt0.Name}]，改名将跳过");
                            }
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"    x 首实例设置失败 [{pt0.Name}]: {ex.Message}");
                        }

                        startIdx = 1;
                        onLog?.Invoke($"    Reference 加载: {System.IO.Path.GetFileName(modelPath)}");
                    }
                    else
                    {
                        onLog?.Invoke($"    Reference 复用: {System.IO.Path.GetFileName(modelPath)}");
                    }

                    // Copy 源
                    try
                    {
                        sel.Clear();
                        sel.Add(sourceInst);
                        sel.Copy();
                    }
                    catch (Exception ex)
                    {
                        onLog?.Invoke($"    x [{op.Name}] Copy 源实例失败: {ex.Message}");
                        continue;
                    }

                    // Paste 剩余焊点：每次 Paste 后立即定位 + 反射改名
                    for (int i = startIdx; i < opPts.Count; i++)
                    {
                        var pt = opPts[i];
                        current++;
                        onProgress?.Invoke(new ExportProgress { Total = total, Current = current, CurrentItem = pt.Name });

                        try
                        {
                            int beforeCount = targetProducts.Count;
                            int afterCount = beforeCount;

                            // Paste 偶发 E_FAIL（跨线程调用抖动），重试一次
                            for (int attempt = 1; attempt <= 2 && afterCount <= beforeCount; attempt++)
                            {
                                try
                                {
                                    sel.Clear();
                                    sel.Add(container);
                                    sel.Paste();
                                }
                                catch (Exception pex)
                                {
                                    if (attempt == 2) throw;
                                    onLog?.Invoke($"    … [{pt.Name}] Paste 第1次失败({pex.Message.Trim()})，重试");
                                    System.Threading.Thread.Sleep(80);
                                    // 重试前重新 Copy 源，防止剪贴板被清
                                    try { sel.Clear(); sel.Add(sourceInst); sel.Copy(); } catch { }
                                    continue;
                                }
                                afterCount = targetProducts.Count;
                                if (afterCount <= beforeCount && attempt == 1)
                                {
                                    System.Threading.Thread.Sleep(80);
                                    try { sel.Clear(); sel.Add(sourceInst); sel.Copy(); } catch { }
                                }
                            }

                            if (afterCount <= beforeCount)
                                throw new Exception("Paste 后实例数未增加");

                            Product inst = targetProducts.Item(afterCount);
                            double[] placed = ComputePlaced(p, gun, pt);
                            inst.Position.SetComponents(ToSetComp(placed));

                            // 记录改名任务（阶段 A 只收集，阶段 B 批量改名）
                            string curName2 = GetInstanceNameByReflection(targetProducts, afterCount);
                            if (!string.IsNullOrEmpty(curName2))
                            {
                                pendingRenames.Add(new PendingCatiaRename
                                {
                                    ProductsColl = targetProducts,
                                    ContainerIndex = containerIndex,
                                    InstanceIndex = afterCount,
                                    CurrentName = curName2,
                                    TargetName = Sanitize(pt.Name),
                                    Kind = "实例"
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"    x 插入失败 [{pt.Name}]: {ex.Message}");
                        }
                    }

                    onLog?.Invoke($"  [{op.Name}] 已插入 {opPts.Count} 个焊点实例（共享 Reference）");
                }

                // 阶段 A 结束：所有实例已插入并定位

                // 阶段 B：批量改名
                // 关键：改名必须在"与手动验证脚本相同的环境"下进行——
                //   1) 清空 Selection，释放 Copy/Paste 命令残留状态
                //   2) 恢复 RefreshDisplay / DisplayFileAlerts（脚本验证时这两个标志是正常的）
                //   3) 激活目标 Product 文档，保证 ActiveDocument 解析链指向它
                onLog?.Invoke($"[Catia] 插枪完成（共享几何：{sourceCache.Count} 份几何，{current}/{total} 个实例）");
                try { sel.Clear(); } catch { }
                try { ((dynamic)_catia).RefreshDisplay = savedRefreshDisplay; } catch { }
                try { ((dynamic)_catia).DisplayFileAlerts = savedDisplayAlerts; } catch { }
                try { productDoc.Activate(); } catch { }
                onLog?.Invoke($"      开始批量改名（共 {pendingRenames.Count} 个实例）...");
                BatchRename(pendingRenames, rootProduct, onLog);
            }
            finally
            {
                try { ((dynamic)_catia).RefreshDisplay = savedRefreshDisplay; } catch { }
                try { ((dynamic)_catia).DisplayFileAlerts = savedDisplayAlerts; } catch { }
                if (initialWindow != null) try { initialWindow.Activate(); } catch { }
            }
        }

        // ---------- 辅助：解析模型路径（自定义 > 每 Op 覆盖 > GunInfo 默认） ----------
        private static string ResolveModelPath(GunExportParams p, OperationInfo op, GunInfo gun)
        {
            if (p.PerOpModelPaths != null &&
                p.PerOpModelPaths.TryGetValue(op.Name, out string perOpPath) &&
                !string.IsNullOrEmpty(perOpPath) && SysFile.Exists(perOpPath))
                return perOpPath;

            if (!string.IsNullOrEmpty(p.CustomModelPath) && SysFile.Exists(p.CustomModelPath))
                return p.CustomModelPath;

            if (!string.IsNullOrEmpty(gun.ModelPath) && SysFile.Exists(gun.ModelPath))
                return gun.ModelPath;

            return null;
        }

        // ---------- 辅助：TCP 覆盖（GunOriginAtTCP=true 且指定了 TCP 时应用） ----------
        private static void ApplyTcpOverride(GunExportParams p, OperationInfo op, GunInfo gun, Action<string> onLog)
        {
            if (!p.GunOriginAtTCP) return;
            if (p.TcpCustomMatrix == null && string.IsNullOrEmpty(p.TcpName)) return;

            double[] tcpWorld = p.TcpCustomMatrix ?? PsReader.ResolveTcpWorldByName(op, p.TcpName, onLog);
            if (tcpWorld == null)
            {
                onLog?.Invoke($"    ! 未能解析所选 TCP（{p.TcpName}），改用默认 TCP");
                return;
            }

            gun.TcpWorldMatrix = tcpWorld;
            double[] rel = ComputeTcpRelTool(gun.ToolMatrix, tcpWorld);
            if (rel != null) gun.TcpRelTool = rel;
            onLog?.Invoke($"    TCP 覆盖：{(p.TcpCustomMatrix != null ? "自定义坐标" : p.TcpName)}");
        }

        // ---------- 辅助：容器同名冲突处理（自动加 _2 / _3 后缀） ----------
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

        // ---------- 辅助：待改名任务 ----------
        // 阶段 A（插入循环）记录 (ProductsColl, CurrentName, TargetName)
        // 阶段 B（Update 之后）用 CurrentName 反射 Item("...") 定位到 __ComObject 版实例改名
        private class PendingCatiaRename
        {
            public object ProductsColl;   // 插入时缓存的 Products RCW（当前线程对照用）
            public int ContainerIndex;    // 容器在根 Products 中的索引；0 = 目标本身位于根层级
            public int InstanceIndex;     // 目标在其父 Products 中的索引
            public string CurrentName;    // 插入时读到的名字（仅用于日志核对）
            public string TargetName;     // 目标名
            public string Kind;           // "容器" / "实例"，用于分类统计
        }

        // ---------- 晚绑定反射小助手 ----------
        private static object ComGet(object o, string prop)
        {
            return o.GetType().InvokeMember(prop,
                System.Reflection.BindingFlags.GetProperty, null, o, null);
        }

        private static object ComCall(object o, string method, params object[] args)
        {
            return o.GetType().InvokeMember(method,
                System.Reflection.BindingFlags.InvokeMethod, null, o, args);
        }

        private static void ComSet(object o, string prop, object value)
        {
            o.GetType().InvokeMember(prop,
                System.Reflection.BindingFlags.SetProperty, null, o, new object[] { value });
        }

        // ---------- 辅助：按索引解析目标实例（两种父级策略） ----------
        // CATIA 的实例名存储在**父级 Reference** 中，而不是父级 instance 中。
        //   根层级：doc.Product 本身就是 reference → 写入生效（已被"容器改名成功"证实）
        //   容器内：container 是 instance；container.Products 返回沿实例路径解析的对象，
        //           对其 PROPERTYPUT "Name" 会被解析层丢弃（无异常、读回不变）
        //   → 正确路径是 container.ReferenceProduct.Products.Item(i)
        // useReference=true 走 ReferenceProduct（主策略），false 走旧的 instance 路径（兜底对照）。
        private static object ResolveInstanceByIndex(object catiaRoot, string docName,
            int containerIndex, int instanceIndex, bool useReference)
        {
            object docs = ComGet(catiaRoot, "Documents");
            object doc = ComCall(docs, "Item", docName);
            object rootProd = ComGet(doc, "Product");
            object coll = ComGet(rootProd, "Products");

            if (containerIndex > 0)
            {
                object container = ComCall(coll, "Item", containerIndex);
                object father = useReference ? ComGet(container, "ReferenceProduct") : container;
                coll = ComGet(father, "Products");
            }
            return ComCall(coll, "Item", instanceIndex);
        }

        // ---------- 辅助：在专用 STA 线程上执行 CATIA 操作 ----------
        // CATIA V5 是 STA 单线程服务端。导出流程若跑在后台工作线程上，
        // 读属性 / 调方法（Position.SetComponents、AddComponentsFromFiles、Copy/Paste）大多能通过，
        // 但部分 PROPERTYPUT（如 Product.Name）会被**静默丢弃且不抛异常**——与实测现象完全一致。
        // 因此改名统一在新建的 STA 线程里、用该线程自己 GetActiveObject 拿到的 CATIA 上执行，
        // 使其与已验证成功的 PS 脚本控制台（PS 主 STA 线程）在线程模型上完全一致。
        private static void RunOnSta(Action<object> work, Action<string> onLog)
        {
            Exception err = null;
            var t = new System.Threading.Thread(() =>
            {
                object catiaSta = null;
                try
                {
                    catiaSta = Marshal.GetActiveObject("CATIA.Application");
                    work(catiaSta);
                }
                catch (Exception ex) { err = ex; }
                finally
                {
                    if (catiaSta != null) try { Marshal.ReleaseComObject(catiaSta); } catch { }
                }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (err != null) onLog?.Invoke("      ! STA 线程异常: " + err.Message);
        }

        // ---------- 辅助：通过反射读取实例当前 Name ----------
        private static string GetInstanceNameByReflection(object productsColl, int index)
        {
            if (productsColl == null || index < 1) return null;
            try
            {
                object childObj = productsColl.GetType().InvokeMember(
                    "Item",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null, productsColl, new object[] { index });
                if (childObj == null) return null;
                object nameObj = childObj.GetType().InvokeMember(
                    "Name",
                    System.Reflection.BindingFlags.GetProperty,
                    null, childObj, null);
                return nameObj as string;
            }
            catch { return null; }
        }

        // ---------- 辅助：改名核心（对齐已验证成功配方） ----------
        //   inst.GetType().InvokeMember("Name", SetProperty, ..., new object[]{ newName });
        //   写入后必须读回验证——CATIA 在非 STA 线程上会静默丢弃写入而不抛异常。
        private static bool TrySetName(object inst, string targetName, out string debug)
        {
            debug = "";
            if (inst == null) { debug = "inst=null"; return false; }
            if (string.IsNullOrEmpty(targetName)) { debug = "targetName 空"; return false; }

            string before = "?";
            try { before = ComGet(inst, "Name") as string ?? "?"; } catch { }
            if (before == targetName) return true;

            try { ComSet(inst, "Name", targetName); }
            catch (System.Reflection.TargetInvocationException tie)
            {
                var ce = tie.InnerException as COMException;
                string hr = ce != null ? "HRESULT=0x" + ce.ErrorCode.ToString("X8") + " " : "";
                debug = $"SetProperty COM 异常: {hr}{tie.InnerException?.Message ?? tie.Message}";
                return false;
            }
            catch (Exception ex) { debug = "SetProperty 反射异常: " + ex.Message; return false; }

            string after = "?";
            try { after = ComGet(inst, "Name") as string ?? "null"; }
            catch (Exception ex) { debug = "读回 Name 失败: " + ex.Message; return false; }

            if (after == targetName) return true;
            debug = $"静默拒绝: before='{before}' target='{targetName}' after='{after}'";
            return false;
        }

        // ---------- 辅助：批量改名 ----------
        // 主路径：container.ReferenceProduct.Products.Item(i) —— 实例名存储在父级 Reference 中，
        //         写在 instance 路径（container.Products）上会被解析层静默丢弃（无异常、读回不变）。
        // 兜底：instance 路径 → 专用 STA 线程重试，用于容错，正常情况下不会触发。
        private void BatchRename(List<PendingCatiaRename> tasks, Product rootProduct, Action<string> onLog)
        {
            if (tasks == null || tasks.Count == 0) return;

            try { rootProduct.Update(); }
            catch (Exception ex) { onLog?.Invoke("      ! 改名前 Update 异常: " + ex.Message); }

            string docName = null;
            try { docName = ComGet(ComGet(_catia, "ActiveDocument"), "Name") as string; } catch { }
            if (string.IsNullOrEmpty(docName))
            {
                onLog?.Invoke("      ! 无法解析文档名，改名中止");
                return;
            }

            var remaining = new List<PendingCatiaRename>();
            int okRef = 0, okInst = 0;
            string sampleRefDbg = null, sampleInstDbg = null;

            // ── 第 1 遍：当前线程，先走 ReferenceProduct 路径，再退回 instance 路径 ──
            object catiaCur = _catia;
            foreach (var pr in tasks)
            {
                bool done = false;

                foreach (bool useRef in new[] { true, false })
                {
                    object inst = null;
                    string dbg = "";
                    try { inst = ResolveInstanceByIndex(catiaCur, docName, pr.ContainerIndex, pr.InstanceIndex, useRef); }
                    catch (Exception ex) { dbg = $"解析失败({(useRef ? "Ref" : "Inst")}): {ex.Message}"; }

                    if (inst != null && TrySetName(inst, pr.TargetName, out dbg))
                    {
                        if (useRef) okRef++; else okInst++;
                        done = true;
                        break;
                    }
                    if (useRef) sampleRefDbg = sampleRefDbg ?? dbg;
                    else sampleInstDbg = sampleInstDbg ?? dbg;
                }

                if (!done) remaining.Add(pr);
            }

            onLog?.Invoke($"      [改名] Reference路径 {okRef}，Instance路径 {okInst}，剩余 {remaining.Count}/{tasks.Count}");
            if (sampleRefDbg != null) onLog?.Invoke("        Reference路径样本：" + sampleRefDbg);
            if (sampleInstDbg != null) onLog?.Invoke("        Instance路径样本：" + sampleInstDbg);

            // ── 第 2 遍：仅当仍有失败时，用专用 STA 线程重试 Reference 路径 ──
            int okSta = 0, okStaContainer = 0, okStaInstance = 0;
            var failSamples = new List<string>();

            if (remaining.Count > 0)
            {
                RunOnSta(catiaSta =>
                {
                    foreach (var pr in remaining)
                    {
                        object inst = null;
                        string dbg = "";
                        try { inst = ResolveInstanceByIndex(catiaSta, docName, pr.ContainerIndex, pr.InstanceIndex, true); }
                        catch (Exception ex) { dbg = $"解析失败: {ex.Message}"; }

                        if (inst != null && TrySetName(inst, pr.TargetName, out dbg))
                        {
                            okSta++;
                            if (pr.Kind == "容器") okStaContainer++; else okStaInstance++;
                        }
                        else if (failSamples.Count < 5)
                        {
                            failSamples.Add($"[{pr.Kind} 容器{pr.ContainerIndex}/项{pr.InstanceIndex} " +
                                            $"'{pr.CurrentName}' → '{pr.TargetName}'] {dbg}");
                        }
                    }
                }, onLog);

                if (okSta > 0)
                    onLog?.Invoke($"      [STA线程] 追加成功 {okSta}（容器 {okStaContainer}，实例 {okStaInstance}）");
            }

            int ok = okRef + okInst + okSta;
            onLog?.Invoke($"      批量改名合计: {ok}/{tasks.Count}");
            if (failSamples.Count > 0)
            {
                onLog?.Invoke("      改名失败样本：");
                foreach (var s in failSamples) onLog?.Invoke("        " + s);
            }

            try { rootProduct.Update(); } catch { }
        }


        // ---------- 辅助：位置矩阵计算 ----------
        private double[] ComputePlaced(GunExportParams p, dynamic gun, PointInfo pt)
        {
            double[] placed = p.GunOriginAtTCP
                ? CalcGunPlacedMatrix(pt.TCPMatrix, gun.ToolMatrix, gun.TcpWorldMatrix, gun.TcpRelTool)
                : pt.TCPMatrix;

            if (p.RefMatrix != null && !PsReader.IsIdentity(p.RefMatrix))
                placed = PsReader.ToRelative(placed, p.RefMatrix);

            return placed;
        }

        // ════════════════════════════════════════════════════════════
        //  矩阵计算
        // ════════════════════════════════════════════════════════════
        private static double[] CalcGunPlacedMatrix(double[] weldPt, double[] toolWorld, double[] tcpWorld, double[] tcpRelTool)
        {
            try
            {
                var weldT = PsReader.ArrToTxPublic(weldPt);
                TxTransformation tRel;
                if (tcpRelTool != null && !PsReader.IsIdentity(tcpRelTool))
                    tRel = PsReader.ArrToTxPublic(tcpRelTool);
                else if (toolWorld != null && tcpWorld != null)
                    tRel = TxTransformation.Multiply(PsReader.ArrToTxPublic(toolWorld).Inverse, PsReader.ArrToTxPublic(tcpWorld));
                else return weldPt;
                return PsReader.TxToArr(TxTransformation.Multiply(weldT, tRel.Inverse));
            }
            catch { return weldPt; }
        }

        private static object[] ToSetComp(double[] m) => new object[]
        {
            (object)m[0], (object)m[4], (object)m[8],
            (object)m[1], (object)m[5], (object)m[9],
            (object)m[2], (object)m[6], (object)m[10],
            (object)m[3], (object)m[7], (object)m[11]
        };

        private void InsertMarker(Products products, string name, double[] m)
        {
            try
            {
                Product p = products.AddNewComponent("Part", "");
                p.set_Name(Sanitize(name));
                p.Position.SetComponents(ToSetComp(m));
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  工具方法
        // ════════════════════════════════════════════════════════════
        private static string Sanitize(string n)
        {
            if (string.IsNullOrEmpty(n)) return "Item";
            n = Regex.Replace(n, @"[/\\:*?""<>|]", "_");
            return n.Length > 80 ? n.Substring(0, 80) : n;
        }

        private static string BuildPath(string basePath, string name, string ext)
        {
            if (string.IsNullOrEmpty(basePath))
                basePath = SysPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "CatiaExport");
            if (!ext.StartsWith(".")) ext = "." + ext;
            if (!SysDir.Exists(basePath)) SysDir.CreateDirectory(basePath);
            return SysPath.Combine(basePath, Sanitize(name) + ext);
        }

        // ════════════════════════════════════════════════════════════
        //  IDisposable
        // ════════════════════════════════════════════════════════════
        public void Dispose()
        {
            if (!_disposed)
            {
                if (_catia != null) { try { Marshal.ReleaseComObject(_catia); } catch { } _catia = null; }
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
        ~CatiaBridge() { Dispose(); }
    }
}