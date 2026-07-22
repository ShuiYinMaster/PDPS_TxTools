// TxTools.Agent / Core / Ps / PsViewerHelper.cs
// PS 3D 视口的截图 + 相机控制底层封装。API 摸自 WeldAnnotator + AutoRecorder + PsReader。
//
// 关键 API:
//   viewer.GetImage(Size, bool)                          → 抓 3D 视图 Bitmap (WeldAnnotator 用)
//   ((ITxGraphicDisplayer)viewer).CurrentCamera get/set  → 相机读写 (PsReader 用)
//   new TxCamera(refPoint, camPos, upVec)                → 构造相机 (PsReader.ComputeOptimalCamera:507)
//
// 为什么不用 WindowsCapture:
//   - 会截到 PS 主窗口的所有 UI (工具栏/树/属性面板都进图)
//   - 遮挡时抓不到
//   - DPI 缩放坑
//   - 不能指定输出分辨率 (超采样出高清图不可能)
// GetImage 直接问 GraphicViewer 要 3D 视图渲染 —— 干净、可指定任意分辨率、无 UI 污染。
//
// v2 修复 (对照 AutoRecorder/PsReader + WeldAnnotator/PsReader):
//   - 截图尺寸改用 viewer.ContainerWindow.Bounds (WeldAnnotator CaptureActiveViewer:2444-2446)
//     而非 viewer.ViewRectangle (后者会 NRE)
//   - 截图前隐藏导航辅助控件 (ShowNavigationCube / ShowNavigationFrame)
//   - Dynamic 兜底: GetCurrentCamera, SetCurrentCamera, GetCameraDistance 都加了
//     dynamic 路径 (AutoRecorder PsReader:411-437), 防止 ITxGraphicDisplayer 转型失败
//   - 用 TxApplication.RefreshDisplay() 替代 Application.DoEvents()

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Core
{
    public static class PsViewerHelper
    {
        /// <summary>拿主 GraphicViewer。null = 没有活动视口。</summary>
        public static TxGraphicViewer GetViewer()
        {
            try { return TxApplication.ViewersManager.GraphicViewer; }
            catch { return null; }
        }

        // ── 截图 ──

        /// <summary>
        /// 抓 3D 视图为 png。size=Empty 时从 viewer.ContainerWindow.Bounds 拿真实尺寸
        /// (WeldAnnotator CaptureActiveViewer:2444-2446 的做法, 比 ViewRectangle 稳定)。
        /// transparent=true 会去掉背景色。截图前临时隐藏导航辅助控件。
        /// 抛异常表示 SDK 侧返回 null 或 IO 失败。
        /// </summary>
        public static string CaptureToPng(string outputPath, Size size, bool transparent)
        {
            var viewer = GetViewer();
            if (viewer == null) throw new InvalidOperationException("无活动 GraphicViewer");

            // Size.Empty 时,从 ContainerWindow.Bounds 拿视口实际尺寸 (WeldAnnotator 做法)
            if (size.IsEmpty)
            {
                try
                {
                    var tvw = viewer.ContainerWindow;
                    if (tvw != null && tvw.Bounds.Width > 0)
                        size = new Size(tvw.Bounds.Width, tvw.Bounds.Height);
                }
                catch { }
                if (size.IsEmpty) size = new Size(1920, 1080);   // WeldAnnotator 同款默认值
            }

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 强制刷新渲染管线, 确保前面 Zoom/相机切换生效
            try { TxApplication.RefreshDisplay(); } catch { }

            // 截图前隐藏导航辅助控件 (导航方块 + 三轴指示器)
            // 参考: WeldAnnotator PsReader.CaptureActiveViewer:2456-2468
            var savedCube = false; var savedFrame = false;
            var hadCube = false; var hadFrame = false;
            try
            {
                savedCube = TxGraphicViewer.ShowNavigationCube;
                hadCube = true;
                if (savedCube) TxGraphicViewer.ShowNavigationCube = false;
            }
            catch { }
            try
            {
                savedFrame = TxGraphicViewer.ShowNavigationFrame;
                hadFrame = true;
                if (savedFrame) TxGraphicViewer.ShowNavigationFrame = false;
            }
            catch { }

            try
            {
                try { TxApplication.RefreshDisplay(); } catch { }
                using (var bmp = viewer.GetImage(size, transparent))
                {
                    if (bmp == null)
                        throw new InvalidOperationException("GraphicViewer.GetImage 返回 null");
                    bmp.Save(outputPath, ImageFormat.Png);
                }
                return outputPath;
            }
            finally
            {
                // 恢复导航辅助控件
                if (hadCube && savedCube)
                {
                    try { TxGraphicViewer.ShowNavigationCube = true; } catch { }
                }
                if (hadFrame && savedFrame)
                {
                    try { TxGraphicViewer.ShowNavigationFrame = true; } catch { }
                }
            }
        }

        // ── 相机 ──

        /// <summary>
        /// 读当前相机。先走 ITxGraphicDisplayer 转型, 失败则 dynamic 兜底
        /// (AutoRecorder PsReader:400-416)。
        /// </summary>
        public static TxCamera GetCurrentCamera(TxGraphicViewer viewer)
        {
            if (viewer == null) return null;
            // 路径 1: ITxGraphicDisplayer 转型
            try { return ((ITxGraphicDisplayer)viewer).CurrentCamera; }
            catch { }
            // 路径 2: dynamic 兜底
            try
            {
                dynamic d = viewer;
                return d.CurrentCamera as TxCamera;
            }
            catch { return null; }
        }

        /// <summary>
        /// 写相机。先走 ITxGraphicDisplayer, 失败则 dynamic 兜底
        /// (AutoRecorder PsReader:418-437)。
        /// </summary>
        public static bool SetCurrentCamera(TxGraphicViewer viewer, TxCamera cam)
        {
            if (viewer == null || cam == null) return false;
            // 路径 1: ITxGraphicDisplayer
            try
            {
                ((ITxGraphicDisplayer)viewer).CurrentCamera = cam;
                try { TxApplication.RefreshDisplay(); } catch { }
                return true;
            }
            catch { }
            // 路径 2: dynamic 兜底
            try
            {
                dynamic d = viewer;
                d.CurrentCamera = cam;
                try { TxApplication.RefreshDisplay(); } catch { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 拿当前 CameraDistance, 读不到就返回 fallback (通常给 1000mm 或算好的值)。
        /// PS 内部单位是 mm。先走 viewer.CameraDistance 属性, 失败则 dynamic 兜底。
        /// </summary>
        public static double GetCameraDistance(TxGraphicViewer viewer, double fallback)
        {
            if (viewer == null) return fallback;
            // 路径 1: 直接属性
            try
            {
                var d = viewer.CameraDistance;
                return d > 0 ? d : fallback;
            }
            catch { }
            // 路径 2: dynamic 兜底
            try
            {
                dynamic d = viewer;
                double val = Convert.ToDouble(d.CameraDistance);
                return val > 0 ? val : fallback;
            }
            catch { return fallback; }
        }

        /// <summary>Zoom To Fit (SDK 内置方法)。相当于按 F 全场景聚焦。</summary>
        public static bool ZoomToFit()
        {
            var v = GetViewer();
            if (v == null) return false;
            try { v.ZoomToFit(); return true; }
            catch { return false; }
        }
    }
}
