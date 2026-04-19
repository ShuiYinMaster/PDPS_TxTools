// CatiaBridge.cs  —  C# 7.3
// CATIA V5 COM 互操作：导出点球 / 导出插枪
//
// 依赖 COM 引用（位于 CATIA 安装目录 intel_a\code\bin\）：
//   INFITF.dll / MECMOD.dll / HybridShapeTypeLib.dll / ProductStructureTypeLib.dll

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using INFITF;
using MECMOD;
using HybridShapeTypeLib;
using ProductStructureTypeLib;
using Tecnomatix.Engineering;

using SysFile = System.IO.File;
using SysPath = System.IO.Path;
using SysDir  = System.IO.Directory;

namespace MyPlugin.ExportGun
{
    public enum ExportFormat     { Xml3d, CATProduct }
    public enum BallExportOption { TrajectoryAndBall, TrajectoryOnly, BallOnly }

    public class GunExportParams
    {
        public List<OperationInfo> Operations;
        public bool   ExportTCP;
        public bool   GunOriginAtTCP;
        public string CustomModelPath;
        public ExportFormat Format;
        public string OutputPath;
        public double[] RefMatrix;
        public string   RefName;
    }

    public class BallExportParams
    {
        public List<OperationInfo> Operations;
        public bool   ExportToCurrentDoc;
        public BallExportOption Option;
        public double BallDiameter;
        public string OutputPath;
        public PointType PointFilter;
        public bool   UseMfgName;
        public string GeomSetName;
        public string NamePrefix;
        public double[] RefMatrix;
        public string   RefName;
    }

    public class ExportProgress
    {
        public int    Total;
        public int    Current;
        public string CurrentItem;
    }

    public class CatiaBridge : IDisposable
    {
        private INFITF.Application _catia;
        private bool _disposed;

        // ════════════════════════════════════════════════════════════
        //  连接 CATIA
        // ════════════════════════════════════════════════════════════
        public bool Connect(out string error)
        {
            error = null;
            try
            {
                _catia = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                _catia.Visible = true;
                return true;
            }
            catch { }
            try
            {
                Type t = Type.GetTypeFromProgID("CATIA.Application");
                if (t != null) { _catia = (INFITF.Application)Activator.CreateInstance(t); _catia.Visible = true; return true; }
            }
            catch { }
            error = "无法连接 CATIA V5，请确认 CATIA V5 已完全启动。";
            return false;
        }

        // ════════════════════════════════════════════════════════════
        //  导出点球
        // ════════════════════════════════════════════════════════════
        public void ExportBalls(BallExportParams p, Action<ExportProgress> onProgress, Action<string> onLog)
        {
            if (_catia == null) throw new InvalidOperationException("CATIA 未连接");

            string refInfo = p.RefMatrix != null ? p.RefName ?? "自定义参考系" : "世界坐标系";
            onLog($"[Catia] 导出点球，参考坐标系：{refInfo}");

            var allPts = new List<PointInfo>();
            foreach (var op in p.Operations) allPts.AddRange(op.Points);
            if (allPts.Count == 0) { onLog("  ! 无可导出的点"); return; }

            int    total       = allPts.Count;
            string geomSetName = string.IsNullOrEmpty(p.GeomSetName) ? "Geometry_Spheres" : p.GeomSetName;
            string namePrefix  = string.IsNullOrEmpty(p.NamePrefix)  ? "SPHERE" : p.NamePrefix;
            double radius      = p.BallDiameter / 2.0;

            // 获取 / 新建 PartDocument
            PartDocument partDoc = null;
            if (p.ExportToCurrentDoc)
            {
                try
                {
                    Document active = _catia.ActiveDocument;
                    if (active is PartDocument pd)
                    {
                        partDoc = pd;
                        onLog("[Catia] 使用当前 Part：" + active.get_Name());
                    }
                    else if (active is ProductDocument prodDoc)
                    {
                        Product newProd = prodDoc.Product.Products.AddNewComponent("Part", "");
                        partDoc = (PartDocument)_catia.Documents.Item(newProd.get_PartNumber() + ".CATPart");
                        onLog("[Catia] 在 Product 下新建 Part：" + newProd.get_PartNumber());
                    }
                }
                catch (Exception ex) { onLog("[Catia] ! 获取活动文档失败：" + ex.Message); }
            }
            if (partDoc == null)
            {
                try { partDoc = (PartDocument)_catia.Documents.Add("Part"); onLog("[Catia] 新建 Part：" + partDoc.get_Name()); }
                catch (Exception ex) { onLog("[Catia] x 无法新建 Part：" + ex.Message); return; }
            }

            Part part;
            HybridShapeFactory sf;
            try { part = partDoc.Part; sf = (HybridShapeFactory)part.HybridShapeFactory; }
            catch (Exception ex) { onLog("[Catia] x HybridShapeFactory 失败：" + ex.Message); return; }

            // 获取 / 创建几何集
            HybridBody geomSet = null;
            try
            {
                HybridBodies bodies = part.HybridBodies;
                for (int i = 1; i <= bodies.Count; i++)
                { HybridBody b = bodies.Item(i); if (b.get_Name() == geomSetName) { geomSet = b; break; } }
                if (geomSet == null) { geomSet = bodies.Add(); geomSet.set_Name(geomSetName); }
            }
            catch (Exception ex) { onLog("[Catia] x 几何集失败：" + ex.Message); return; }

            // 阶段1：创建点
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
                catch (Exception ex) { onLog($"  ! 点{i+1} 异常：{ex.Message}"); continue; }
                if ((i + 1) % 10 == 0 || i + 1 == total)
                    onProgress?.Invoke(new ExportProgress { Total = total * 2, Current = i + 1, CurrentItem = allPts[i].Name });
            }
            onLog($"[Catia] 已创建 {createdPoints.Count} 个点");
            try { part.Update(); } catch { }

            // 阶段2：创建球体
            onLog($"[Catia] 创建球体，半径={radius}mm...");
            int ok = 0, fail = 0;
            for (int i = 0; i < createdPoints.Count; i++)
            {
                string ballName = namePrefix + "_" + (i < allPts.Count ? allPts[i].Name : (i + 1).ToString());
                try
                {
                    Reference ptRef = part.CreateReferenceFromObject(createdPoints[i]);
                    HybridShapeSphere sphere = sf.AddNewSphere(ptRef, null, radius, -90.0, 90.0, 0.0, 360.0);
                    geomSet.AppendHybridShape(sphere);
                    sphere.set_Name(Sanitize(ballName));
                    ok++;
                    if (i % 10 == 0) try { _catia.Windows.Item(1).Activate(); } catch { }
                }
                catch (Exception ex) { onLog($"  ! [{ballName}] {ex.Message}"); fail++; continue; }
                onProgress?.Invoke(new ExportProgress { Total = createdPoints.Count * 2, Current = createdPoints.Count + i + 1, CurrentItem = ballName });
            }
            try { part.Update(); } catch { }
            try { if (_catia.ActiveDocument is ProductDocument pd2) pd2.Product.Update(); _catia.Windows.Item(1).Activate(); } catch { }
            onLog($"[Catia] 点球完成：成功 {ok}，失败 {fail}");

            if (!p.ExportToCurrentDoc)
            {
                string outFile = BuildPath(p.OutputPath, "WeldPoints_Spheres", "CATPart");
                try { partDoc.SaveAs(outFile); onLog("[Catia] 已保存：" + outFile); }
                catch (Exception ex) { onLog("[Catia] 保存失败：" + ex.Message); }
            }
            else { try { partDoc.Save(); } catch { } }
        }

        // ════════════════════════════════════════════════════════════
        //  导出插枪
        // ════════════════════════════════════════════════════════════
        public void ExportGuns(GunExportParams p, Action<ExportProgress> onProgress, Action<string> onLog)
        {
            if (_catia == null) throw new InvalidOperationException("CATIA 未连接");

            // 获取活动 Product 文档，提取基础名
            ProductDocument productDoc = null;
            string baseName = "ExportedGuns";
            try
            {
                if (_catia.ActiveDocument is ProductDocument pd)
                {
                    productDoc = pd;
                    string raw = SysPath.GetFileNameWithoutExtension(pd.get_Name());
                    raw = Regex.Replace(raw, @"-?\d{6,}GUN.*$", "", RegexOptions.IgnoreCase);
                    raw = Regex.Replace(raw, @"-?GUN\d{8}.*$", "", RegexOptions.IgnoreCase);
                    baseName = string.IsNullOrEmpty(raw) ? SysPath.GetFileNameWithoutExtension(pd.get_Name()) : raw;
                    onLog($"[Catia] 活动文档：{pd.get_Name()}");
                }
            }
            catch { }
            if (productDoc == null)
            {
                try { productDoc = (ProductDocument)_catia.Documents.Add("Product"); productDoc.Product.set_PartNumber("ExportedGuns"); onLog("[Catia] 已新建 Product 文档"); }
                catch (Exception ex) { onLog("[Catia] x 创建 Product 失败：" + ex.Message); return; }
            }

            Product  rootProduct  = productDoc.Product;
            Products rootProducts = rootProduct.Products;

            // 填充焊点
            foreach (var op in p.Operations)
                if (op.Points == null || op.Points.Count == 0)
                    PsReader.FillPoints(op, PointType.WeldPoint, true, onLog);

            // 计算总数
            int total = 0;
            foreach (var op in p.Operations) if (op.Gun != null && op.Points != null) total += op.Points.Count;
            int idx = 0;

            onLog($"[Catia] 参考坐标系：{(p.RefMatrix != null ? p.RefName ?? "自定义参考系" : "世界坐标系")}");

            foreach (var op in p.Operations)
            {
                GunInfo gun = op.Gun;
                if (gun == null) { onLog($"  ! [{op.Name}] 无焊枪信息，跳过"); continue; }

                // 解析模型路径
                string modelPath = null;
                if (!string.IsNullOrEmpty(p.CustomModelPath) && SysFile.Exists(p.CustomModelPath))
                    modelPath = p.CustomModelPath;
                else if (!string.IsNullOrEmpty(gun.ModelPath) && SysFile.Exists(gun.ModelPath))
                    modelPath = gun.ModelPath;

                if (modelPath == null)
                { onLog($"  ! [{op.Name}] 未找到模型文件（可在 GUI 手动指定），跳过"); continue; }
                if (op.Points == null || op.Points.Count == 0)
                { onLog($"  ! [{op.Name}] 无焊点，跳过"); continue; }

                onLog($"  [{op.Name}] {op.Points.Count} 个焊点，模型：{SysPath.GetFileName(modelPath)}");

                // 创建容器 Product：{baseName}-GUN-{yyyyMMdd}.{序号}
                string dateStr       = DateTime.Now.ToString("yyyyMMdd");
                string containerBase = $"{baseName}-GUN-{dateStr}";
                var    seqRegex      = new Regex($@"^{Regex.Escape(containerBase)}\.(\d{{3}})$");
                int    maxSeq        = 0;
                for (int ci = 1; ci <= rootProducts.Count; ci++)
                    try { Match m2 = seqRegex.Match(rootProducts.Item(ci).get_Name()); if (m2.Success) { int s = int.Parse(m2.Groups[1].Value); if (s > maxSeq) maxSeq = s; } } catch { }
                string containerName = Sanitize($"{containerBase}.{(maxSeq + 1):D3}");

                Product gunContainer = null;
                try
                {
                    gunContainer = rootProducts.AddNewComponent("Product", "");
                    gunContainer.set_Name(containerName);
                    try { gunContainer.set_PartNumber(containerName); } catch { }
                    onLog($"    容器：{containerName}");
                }
                catch (Exception ex) { onLog($"    x 容器创建失败：{ex.Message}"); }

                Products targetProducts = gunContainer?.Products ?? rootProducts;
                string   modelExt       = SysPath.GetExtension(modelPath);
                string   tempDir        = SysPath.Combine(SysPath.GetTempPath(), "CatiaGunExport_" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                SysDir.CreateDirectory(tempDir);

                foreach (var pt in op.Points)
                {
                    idx++;
                    onProgress?.Invoke(new ExportProgress { Total = total, Current = idx, CurrentItem = pt.Name });
                    onLog($"    [{idx}/{total}] {pt.Name}");
                    try
                    {
                        // 复制到临时目录并以焊点名命名
                        string ptFile = SysPath.Combine(tempDir, Sanitize(pt.Name) + modelExt);
                        SysFile.Copy(modelPath, ptFile, overwrite: true);

                        targetProducts.AddComponentsFromFiles(new object[] { ptFile }, "All");
                        Product inserted = targetProducts.Item(targetProducts.Count);
                        try { inserted.set_Name(Sanitize(pt.Name)); } catch { }

                        // 计算放置矩阵
                        double[] placed = p.GunOriginAtTCP
                            ? CalcGunPlacedMatrix(pt.TCPMatrix, gun.ToolMatrix, gun.TcpWorldMatrix, gun.TcpRelTool)
                            : pt.TCPMatrix;
                        double[] tcpSrc = pt.TCPMatrix;
                        if (p.RefMatrix != null && !PsReader.IsIdentity(p.RefMatrix))
                        {
                            placed = PsReader.ToRelative(placed, p.RefMatrix);
                            tcpSrc = PsReader.ToRelative(tcpSrc, p.RefMatrix);
                        }
                        inserted.Position.SetComponents(ToSetComp(placed));

                        if (p.ExportTCP) InsertMarker(targetProducts, pt.Name + "_TCP", tcpSrc);
                    }
                    catch (Exception ex) { onLog($"    x 插入失败：{ex.Message}"); }
                }

                // 清理临时目录
                try { SysDir.Delete(tempDir, true); }
                catch { onLog($"    ! 临时目录清理失败，请手动删除：{tempDir}"); }
            }

            try { rootProduct.Update(); } catch { }
            onLog("[Catia] 插枪完成（未保存，请手动保存）");
        }

        // ════════════════════════════════════════════════════════════
        //  矩阵计算
        // ════════════════════════════════════════════════════════════
        // 令 CGR 的 TCP 对齐焊点：M_placed = weldPt * Inv(TcpRelTool)
        // TcpRelTool = Inv(ToolMatrix) * TcpWorldMatrix（固定几何偏移）
        private static double[] CalcGunPlacedMatrix(
            double[] weldPt, double[] toolWorld, double[] tcpWorld, double[] tcpRelTool)
        {
            try
            {
                var weldT = PsReader.ArrToTxPublic(weldPt);
                TxTransformation tRel;
                if (tcpRelTool != null && !PsReader.IsIdentity(tcpRelTool))
                {
                    tRel = PsReader.ArrToTxPublic(tcpRelTool);
                }
                else if (toolWorld != null && tcpWorld != null)
                {
                    tRel = TxTransformation.Multiply(
                        PsReader.ArrToTxPublic(toolWorld).Inverse,
                        PsReader.ArrToTxPublic(tcpWorld));
                }
                else return weldPt;
                return PsReader.TxToArr(TxTransformation.Multiply(weldT, tRel.Inverse));
            }
            catch { return weldPt; }
        }

        // CATIA SetComponents 列主序：[X轴(3), Y轴(3), Z轴(3), 平移(3)]
        // 行主序矩阵 m[] → col0=(m[0],m[4],m[8]), col1=(m[1],m[5],m[9]),
        //                   col2=(m[2],m[6],m[10]), pos=(m[3],m[7],m[11])
        private static object[] ToSetComp(double[] m) => new object[]
        {
            (object)m[0], (object)m[4], (object)m[8],
            (object)m[1], (object)m[5], (object)m[9],
            (object)m[2], (object)m[6], (object)m[10],
            (object)m[3], (object)m[7], (object)m[11]
        };

        private void InsertMarker(Products products, string name, double[] m)
        {
            try { Product p = products.AddNewComponent("Part", ""); p.set_Name(Sanitize(name)); p.Position.SetComponents(ToSetComp(m)); }
            catch { }
        }

        // ════════════════════════════════════════════════════════════
        //  工具方法
        // ════════════════════════════════════════════════════════════
        private static string Sanitize(string n)
        {
            if (string.IsNullOrEmpty(n)) return "Item";
            foreach (char c in SysPath.GetInvalidFileNameChars()) n = n.Replace(c, '_');
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
            if (!_disposed) { if (_catia != null) { try { Marshal.ReleaseComObject(_catia); } catch { } _catia = null; } _disposed = true; }
            GC.SuppressFinalize(this);
        }
        ~CatiaBridge() { Dispose(); }
    }
}
