// TxTools.Agent / Tools / Ps / CaptureViewerImageTool.cs
// 用 PS SDK 原生 GraphicViewer.GetImage 抓 3D 视图,输出 png 到桌面 TxTools_Exports。
//
// vs screenshot_window(mode=viewer):
//   - screenshot_window: Windows GDI 截屏 -> 会截到工具栏/树/其他 UI,受遮挡/DPI 影响
//   - capture_viewer_image: SDK 内部渲染 -> 纯 3D 视图,任意分辨率,不受 UI 干扰
//
// 优先用这个;screenshot_window 保留作为主窗口整体截图的通道。

using System;
using System.Drawing;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class CaptureViewerImageTool : ITxAgentTool
    {
        public string Name { get { return "capture_viewer_image"; } }
        public string Description
        {
            get
            {
                return "\u7528 PS SDK \u539f\u751f GraphicViewer.GetImage \u6293 3D \u89c6\u56fe(\u4e0d\u53d7\u7a97\u53e3\u906e\u6321/DPI \u5f71\u54cd\u3001\u65e0 UI \u6c61\u67d3\u3001\u53ef\u81ea\u5b9a\u4e49\u5206\u8fa8\u7387)\u3002" +
                       "\u53c2\u6570 file_name(\u4e0d\u542b\u6269\u5c55)\u3001width/height (\u53ef\u9009,\u9ed8\u8ba4\u7528\u89c6\u53e3\u539f\u751f\u5c3a\u5bf8)\u3001transparent (\u9ed8\u8ba4 false)\u3002";
            }
        }
        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'file_name':   { 'type': 'string', 'description': '不含扩展名' },
                        'width':       { 'type': 'integer', 'description': '像素宽,0 或缺省=用视口原生尺寸' },
                        'height':      { 'type': 'integer', 'description': '像素高,0 或缺省=用视口原生尺寸' },
                        'transparent': { 'type': 'boolean', 'default': false, 'description': 'true=背景透明' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var fileName = ToolInputHelpers.String(input["file_name"]);
            var width = ToolInputHelpers.Int(input["width"], 0);
            var height = ToolInputHelpers.Int(input["height"], 0);
            var transparent = ToolInputHelpers.Bool(input["transparent"], false);

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "viewer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
            fileName = DocxExportTool.SafeFileName(fileName);

            var outDir = DocxExportTool.GetExportDir();
            var fullPath = Path.Combine(outDir, fileName);

            Size size = (width > 0 && height > 0) ? new Size(width, height) : Size.Empty;

            try
            {
                PsViewerHelper.CaptureToPng(fullPath, size, transparent);
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }

            var info = new FileInfo(fullPath);
            var dimText = (width > 0 && height > 0) ? " " + width + "x" + height : " (\u89c6\u53e3\u539f\u751f)";
            return "\u5df2\u622a\u56fe: " + fullPath + "  (" + FileParserService.FormatBytes(info.Length) + dimText + ")";
        }
    }
}
