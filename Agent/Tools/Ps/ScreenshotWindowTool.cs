// TxTools.Agent / Tools / Ps / ScreenshotWindowTool.cs
// agent 可调用的截图工具。默认截 PS 主窗口客户区(3D 视口占大部分),
// 也支持全屏或整个 PS 主窗口(带标题栏)。
//
// 用途:
//   - AI 想给用户展示当前 PS 场景状态时截一张图
//   - AI 想把 PS 视图嵌入到生成的 PPT 里作为工艺卡插图 (配合 render_pptx_template)
//   - 用户"帮我截一张当前视图"
//
// 只读工具,不改场景。输出 png 到桌面 TxTools_Exports 目录。

using System;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class ScreenshotWindowTool : ITxAgentTool
    {
        public string Name { get { return "screenshot_window"; } }
        public string Description
        {
            get
            {
                return "\u622a\u56fe\u5e76\u4fdd\u5b58\u5230\u684c\u9762 TxTools_Exports \u76ee\u5f55\u3002" +
                       "\u53c2\u6570 mode: viewer|window|fullscreen (\u9ed8\u8ba4 viewer, \u5373 PS \u4e3b\u7a97\u53e3\u5ba2\u6237\u533a,\u57fa\u672c\u5c31\u662f 3D \u89c6\u56fe)\u3002" +
                       "\u53ef\u9009 file_name(\u4e0d\u542b\u6269\u5c55)\u3002" +
                       "\u8fd4\u56de\u6587\u4ef6\u8def\u5f84,\u540e\u7eed\u53ef\u4f5c\u4e3a render_pptx_template \u7684 images \u53c2\u6570\u4f20\u5165\u3002";
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
                        'mode':      { 'type': 'string', 'enum': ['viewer','window','fullscreen'], 'default': 'viewer' },
                        'file_name': { 'type': 'string', 'description': '不含扩展名' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var mode = ToolInputHelpers.String(input["mode"], "viewer");
            var fileName = ToolInputHelpers.String(input["file_name"]);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) fileName += ".png";
            fileName = DocxExportTool.SafeFileName(fileName);

            var outDir = DocxExportTool.GetExportDir();
            var fullPath = Path.Combine(outDir, fileName);

            try
            {
                switch (mode)
                {
                    case "fullscreen":
                        WindowsCapture.CaptureFullScreen(fullPath);
                        break;
                    case "window":
                        {
                            var hwnd = WindowsCapture.FindPsMainWindow();
                            if (hwnd == IntPtr.Zero) return "Error: \u627e\u4e0d\u5230 PS \u4e3b\u7a97\u53e3";
                            WindowsCapture.CaptureWindow(hwnd, fullPath);
                            break;
                        }
                    case "viewer":
                    default:
                        {
                            var hwnd = WindowsCapture.FindPsMainWindow();
                            if (hwnd == IntPtr.Zero) return "Error: \u627e\u4e0d\u5230 PS \u4e3b\u7a97\u53e3";
                            WindowsCapture.CaptureClientArea(hwnd, fullPath);
                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                return "Error: \u622a\u56fe\u5931\u8d25 - " + ex.Message;
            }

            var size = new FileInfo(fullPath).Length;
            return "\u5df2\u622a\u56fe: " + fullPath + "  (" + FileParserService.FormatBytes(size) + ", mode=" + mode + ")";
        }
    }
}
