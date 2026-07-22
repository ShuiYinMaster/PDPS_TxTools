// TxTools.Agent / Tools / Ps / SetCameraViewTool.cs
// PS 摄像机方位控制 —— 快速切到"前/后/左/右/顶/底/等轴"或自定义 3 向量视角。
//
// 核心 API (从 PsReader.ComputeOptimalCamera:490-507 摸出):
//   var refPoint = new TxVector(Tx, Ty, Tz);
//   var camPos = new TxVector(Tx + dx * dist, ...);
//   var upVec = new TxVector(0, 0, 1);
//   var cam = new TxCamera(refPoint, camPos, upVec);
//   ((ITxGraphicDisplayer)viewer).CurrentCamera = cam;
//
// 6 个预设方向 (世界坐标, Z-up 场景):
//   front  : 相机在 -Y 方向,朝 +Y 看
//   back   : 相机在 +Y 方向,朝 -Y 看
//   left   : 相机在 -X 方向,朝 +X 看
//   right  : 相机在 +X 方向,朝 -X 看
//   top    : 相机在 +Z 方向,朝 -Z 看 (从上往下)
//   bottom : 相机在 -Z 方向,朝 +Z 看 (从下往上)
//   iso    : 等轴视图 (右-前-上方向)
//
// 相机中心点 (ReferencePoint):
//   1) target 参数为对象名 → 用该对象 AbsoluteLocation.Translation
//   2) 参数为空 → 保留当前 ReferencePoint (只旋转视角,不移动焦点)
//
// 相机距离 (CameraDistance):
//   - distance 参数指定 → 用它 (mm)
//   - 否则用当前 viewer.CameraDistance 保持不变
//
// 组合 pipeline (多角度拍摄):
//   select_objects(names=[...])
//   set_view_to_object(use_current_selection=true)              → ZoomToSelection 让 SDK 定焦点
//   set_camera_view(view="front", capture=true, file_name="f")  → 保存 → 截图
//   set_camera_view(view="left",  capture=true, file_name="l")
//   set_camera_view(view="iso",   capture=true, file_name="i")

using System;
using System.Drawing;
using System.IO;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;

namespace TxTools.Agent.Core
{
    public sealed class SetCameraViewTool : ITxAgentTool
    {
        public string Name { get { return "set_camera_view"; } }
        public string Description
        {
            get
            {
                return "\u63a7\u5236 PS \u4e3b\u89c6\u53e3\u76f8\u673a\u65b9\u4f4d\u3002" +
                       "\u53c2\u6570 view: front|back|left|right|top|bottom|iso|custom, " +
                       "target(\u53ef\u9009,\u5bf9\u8c61\u540d\uff0c\u4ee5\u5176\u4f4d\u7f6e\u4e3a\u76f8\u673a\u7126\u70b9), " +
                       "distance(\u53ef\u9009,mm), " +
                       "custom \u9700\u989d\u5916\u4f20 ref_x/y/z, pos_x/y/z, up_x/y/z\u3002" +
                       "\u53ef\u9009 capture=true \u9644\u5e26\u622a\u56fe (file_name)\u3002";
            }
        }
        public bool IsReadOnly { get { return true; } }   // 视角/相机变化不进 undo 栈

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'view':      { 'type': 'string', 'enum': ['front','back','left','right','top','bottom','iso','custom'], 'default': 'iso' },
                        'target':    { 'type': 'string', 'description': '对象名,以其 AbsoluteLocation 为相机聚焦点;空=保留当前' },
                        'distance':  { 'type': 'number',  'description': '相机距离 mm;缺省=用当前 CameraDistance' },
                        'ref_x': { 'type': 'number' }, 'ref_y': { 'type': 'number' }, 'ref_z': { 'type': 'number' },
                        'pos_x': { 'type': 'number' }, 'pos_y': { 'type': 'number' }, 'pos_z': { 'type': 'number' },
                        'up_x':  { 'type': 'number', 'default': 0 }, 'up_y': { 'type': 'number', 'default': 0 }, 'up_z': { 'type': 'number', 'default': 1 },
                        'capture':   { 'type': 'boolean', 'default': false, 'description': 'true=切换后立即调 GraphicViewer.GetImage 保存 png' },
                        'file_name': { 'type': 'string', 'description': 'capture=true 时的输出文件名(不含扩展)' },
                        'width':     { 'type': 'integer', 'default': 0 },
                        'height':    { 'type': 'integer', 'default': 0 }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var view = ToolInputHelpers.String(input["view"], "iso").ToLowerInvariant();

            var viewer = PsViewerHelper.GetViewer();
            if (viewer == null) return "Error: \u65e0\u6d3b\u52a8 GraphicViewer";

            TxCamera newCam;
            try
            {
                if (view == "custom")
                    newCam = BuildCustomCamera(input);
                else
                    newCam = BuildPresetCamera(viewer, input, view);
            }
            catch (Exception ex)
            {
                return "Error: \u6784\u9020\u76f8\u673a\u5931\u8d25 - " + ex.Message;
            }

            if (!PsViewerHelper.SetCurrentCamera(viewer, newCam))
                return "Error: \u8bbe\u7f6e\u76f8\u673a\u5931\u8d25";

            var msg = "\u5df2\u5207\u6362\u89c6\u89d2 " + view +
                      "  ref=" + FmtVec(newCam.ReferencePoint) +
                      "  pos=" + FmtVec(newCam.Position);

            // 可选:切换后立即截图
            if (ToolInputHelpers.Bool(input["capture"], false))
            {
                var fileName = ToolInputHelpers.String(input["file_name"]);
                if (string.IsNullOrWhiteSpace(fileName))
                    fileName = "cam_" + view + "_" + DateTime.Now.ToString("HHmmss");
                if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
                fileName = DocxExportTool.SafeFileName(fileName);

                var outDir = DocxExportTool.GetExportDir();
                var fullPath = Path.Combine(outDir, fileName);

                var w = ToolInputHelpers.Int(input["width"], 0);
                var h = ToolInputHelpers.Int(input["height"], 0);
                Size size = (w > 0 && h > 0) ? new Size(w, h) : Size.Empty;

                try
                {
                    PsViewerHelper.CaptureToPng(fullPath, size, false);
                    msg += "\n\u5df2\u622a\u56fe: " + fullPath;
                }
                catch (Exception ex)
                {
                    msg += "\n\u26a0 \u622a\u56fe\u5931\u8d25: " + ex.Message;
                }
            }

            return msg;
        }

        // ── 相机构造 ──

        /// <summary>custom 模式:三向量都从参数读,任缺就用 (0,0,0) 或默认。</summary>
        private static TxCamera BuildCustomCamera(JObject input)
        {
            var refPt = new TxVector(D(input["ref_x"]), D(input["ref_y"]), D(input["ref_z"]));
            var pos = new TxVector(D(input["pos_x"]), D(input["pos_y"]), D(input["pos_z"]));
            var up = new TxVector(D(input["up_x"]), D(input["up_y"]), D(input["up_z"], 1.0));
            return new TxCamera(refPt, pos, up);
        }

        /// <summary>弹性读 double,缺省 0。</summary>
        private static double D(JToken tok, double fallback = 0.0)
        {
            if (tok == null || tok.Type == JTokenType.Null) return fallback;
            try
            {
                if (tok.Type == JTokenType.Float || tok.Type == JTokenType.Integer)
                    return (double)tok;
                if (tok.Type == JTokenType.String)
                {
                    double d;
                    if (double.TryParse((string)tok, out d)) return d;
                }
            }
            catch { }
            return fallback;
        }

        /// <summary>预设 6 方向 + iso。</summary>
        private static TxCamera BuildPresetCamera(TxGraphicViewer viewer, JObject input, string view)
        {
            // 1) 焦点 ReferencePoint
            var refPt = ResolveReferencePoint(viewer, ToolInputHelpers.String(input["target"]));

            // 2) 距离
            double distance = 0;
            var distTok = input["distance"];
            if (distTok != null && distTok.Type != JTokenType.Null)
            {
                try { distance = (double)distTok; } catch { }
            }
            if (distance <= 0)
                distance = PsViewerHelper.GetCameraDistance(viewer, 3000.0);

            // 3) 方向偏移向量 (相机 = ref + offset)
            double dx, dy, dz;
            TxVector up;
            switch (view)
            {
                case "front": dx = 0; dy = -1; dz = 0; up = new TxVector(0, 0, 1); break;
                case "back": dx = 0; dy = 1; dz = 0; up = new TxVector(0, 0, 1); break;
                case "left": dx = -1; dy = 0; dz = 0; up = new TxVector(0, 0, 1); break;
                case "right": dx = 1; dy = 0; dz = 0; up = new TxVector(0, 0, 1); break;
                case "top": dx = 0; dy = 0; dz = 1; up = new TxVector(0, 1, 0); break;
                case "bottom": dx = 0; dy = 0; dz = -1; up = new TxVector(0, 1, 0); break;
                case "iso":
                default:
                    // 等轴:右-前-上 (右前上方看焦点),更贴近工艺卡典型摆位
                    dx = 0.5773; dy = -0.5773; dz = 0.5773;   // 单位向量近似
                    up = new TxVector(0, 0, 1);
                    break;
            }

            var pos = new TxVector(
                refPt.X + dx * distance,
                refPt.Y + dy * distance,
                refPt.Z + dz * distance);

            return new TxCamera(refPt, pos, up);
        }

        /// <summary>
        /// 焦点解析(多层兜底,防止相机对准原点(0,0,0)而目标其实在别处):
        ///   1) target 是对象名 → 该对象 AbsoluteLocation.Translation
        ///   2) 当前相机 ReferencePoint (若非零)
        ///   3) ActiveSelection 首个对象 AbsoluteLocation.Translation
        ///   4) (0,0,0) 兜底
        /// 对话摸出:仅走 (2) 时会拿到 SDK 初始的 (0,0,0),视图对准空气 —— 必须加 (3)。
        /// </summary>
        private static TxVector ResolveReferencePoint(TxGraphicViewer viewer, string targetName)
        {
            // (1) 明确 target
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                var doc = TxApplication.ActiveDocument;
                if (doc != null)
                {
                    var obj = FindByName(doc.PhysicalRoot, targetName);
                    var loc = TryGetTranslation(obj);
                    if (loc != null) return loc;
                }
            }

            // (2) 当前相机 ReferencePoint (排除明显无效的 (0,0,0))
            var cur = PsViewerHelper.GetCurrentCamera(viewer);
            if (cur != null)
            {
                try
                {
                    var rp = cur.ReferencePoint;
                    if (rp != null && (Math.Abs(rp.X) > 0.01 || Math.Abs(rp.Y) > 0.01 || Math.Abs(rp.Z) > 0.01))
                        return rp;
                }
                catch { }
            }

            // (3) ActiveSelection 首个对象 AbsoluteLocation —— 对话摸出的核心兜底
            try
            {
                var sel = TxApplication.ActiveSelection;
                if (sel != null && sel.Count > 0)
                {
                    var items = sel.GetItems();
                    for (int i = 0; i < items.Count; i++)
                    {
                        var loc = TryGetTranslation(items[i] as ITxObject);
                        if (loc != null) return loc;
                    }
                }
            }
            catch { }

            // (4) 最终原点
            return new TxVector(0, 0, 0);
        }

        /// <summary>拿 ITxObject 的 AbsoluteLocation.Translation → TxVector,失败返 null。</summary>
        private static TxVector TryGetTranslation(ITxObject obj)
        {
            if (obj == null) return null;
            try
            {
                var locObj = obj as ITxLocatableObject;
                if (locObj == null) return null;
                var loc = locObj.AbsoluteLocation;
                if (loc == null) return null;
                var t = loc.Translation;
                return new TxVector(t.X, t.Y, t.Z);
            }
            catch { return null; }
        }

        /// <summary>
        /// 按名字找对象。用 dynamic 调 GetAllDescendants(null)
        /// (AutoRecorder PsReader:63-77 的做法, 比反射稳定)。
        /// </summary>
        private static ITxObject FindByName(object root, string name)
        {
            if (root == null) return null;

            // 路径 A: dynamic GetAllDescendants(null) —— 最稳, 不依赖反射
            try
            {
                dynamic dp = root;
                var all = dp.GetAllDescendants(null) as System.Collections.IEnumerable;
                if (all != null)
                {
                    foreach (var item in all)
                    {
                        var o = item as ITxObject;
                        if (o == null) continue;
                        try
                        {
                            if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                                return o;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 路径 B: 反射兜底 (对某些 SDK 版本 GetType 隐藏了该方法)
            try
            {
                foreach (var m in root.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (m.Name == "GetAllDescendants" && m.GetParameters().Length == 1)
                    {
                        var all = m.Invoke(root, new object[] { null }) as System.Collections.IEnumerable;
                        if (all != null)
                        {
                            foreach (var item in all)
                            {
                                var o = item as ITxObject;
                                if (o == null) continue;
                                try
                                {
                                    if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                                        return o;
                                }
                                catch { }
                            }
                        }
                        break;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string FmtVec(TxVector v)
        {
            return "(" + v.X.ToString("F0") + "," + v.Y.ToString("F0") + "," + v.Z.ToString("F0") + ")";
        }
    }
}