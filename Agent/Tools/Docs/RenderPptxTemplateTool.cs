// TxTools.Agent / Tools / Docs / RenderPptxTemplateTool.cs
// Agent 调用入口: 用户模板 pptx + 每张 slide 的占位符替换数据 → 生成新 pptx。
//
// 使用姿势:
//   1) 你用 PowerPoint 做一个模板文件, 里面:
//      - 文本占位符: 在文本框里写 {{TITLE}} {{CAPTION}} 等 (双花括号)
//      - 图片占位符: 加一个矩形/占位符形状, 通过 "开始 → 选择 → 选择窗格" 改形状名为 IMG_xxx
//      - 一个模板 slide 可以对应多张最终生成的 slide (克隆)
//   2) 把模板保存在 `TxTools_Exports/templates/` 或任意路径, 传绝对路径 template_path
//   3) 传 slides 数组, 每张对应最终 pptx 里的一张:
//      {
//        "template_slide_index": 0,     // 从模板第几张 slide 克隆 (默认 0)
//        "replacements": { "TITLE": "工位 1", "CAPTION": "..." },
//        "images":       { "IMG_screenshot": "C:/path/to/img.png" }
//      }
//
// 典型 pipeline:
//   screenshot_window → 拿到图片路径 → render_pptx_template 把图片嵌入指定占位符

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class RenderPptxTemplateTool : ITxAgentTool
    {
        public string Name { get { return "render_pptx_template"; } }
        public string Description
        {
            get
            {
                return "\u7528\u6a21\u677f pptx \u751f\u6210\u65b0 pptx\u3002" +
                       "\u6a21\u677f\u7528\u6cd5:\u6587\u672c\u5360\u4f4d\u7b26 {{KEY}} \u5199\u5728\u6587\u672c\u6846\u91cc; " +
                       "\u56fe\u7247\u5360\u4f4d\u7b26=\u628a\u5f62\u72b6\u540d(\u5728 PowerPoint '\u9009\u62e9\u7a97\u683c' \u91cc\u6539)\u547d\u540d\u4e3a IMG_xxx\u3002" +
                       "\u53c2\u6570 template_path (\u5fc5\u9700), file_name, slides[{template_slide_index?, replacements?, images?}]\u3002";
            }
        }
        public bool IsReadOnly { get { return false; } }   // 生成文件,视为变更

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['template_path','slides'],
                    'properties': {
                        'template_path': { 'type': 'string', 'description': '模板 pptx 的绝对路径' },
                        'file_name':     { 'type': 'string', 'description': '输出文件名(不含扩展)' },
                        'slides': {
                            'type': 'array',
                            'items': {
                                'type': 'object',
                                'properties': {
                                    'template_slide_index': { 'type': 'integer', 'default': 0 },
                                    'replacements':         { 'type': 'object', 'additionalProperties': { 'type': 'string' } },
                                    'images':               { 'type': 'object', 'additionalProperties': { 'type': 'string' } }
                                }
                            }
                        }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var templatePath = ToolInputHelpers.String(input["template_path"]);
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
                return "Error: template_path \u4e0d\u5b58\u5728 - " + templatePath;

            var fileName = ToolInputHelpers.String(input["file_name"]);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "rendered_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) fileName += ".pptx";
            fileName = DocxExportTool.SafeFileName(fileName);

            var slidesTok = input["slides"] as JArray;
            if (slidesTok == null || slidesTok.Count == 0)
                return "Error: slides \u4e3a\u7a7a";

            var slides = new List<SlideRenderData>();
            foreach (var s in slidesTok)
            {
                if (!(s is JObject so)) continue;
                var sd = new SlideRenderData
                {
                    TemplateSlideIndex = ToolInputHelpers.Int(so["template_slide_index"], 0)
                };

                var repl = so["replacements"] as JObject;
                if (repl != null)
                    foreach (var kv in repl)
                        sd.Replacements[kv.Key] = ToolInputHelpers.String(kv.Value) ?? "";

                var imgs = so["images"] as JObject;
                if (imgs != null)
                    foreach (var kv in imgs)
                        sd.Images[kv.Key] = ToolInputHelpers.String(kv.Value) ?? "";

                slides.Add(sd);
            }

            var outDir = DocxExportTool.GetExportDir();
            var fullPath = Path.Combine(outDir, fileName);

            byte[] templateBytes;
            try { templateBytes = File.ReadAllBytes(templatePath); }
            catch (Exception ex) { return "Error: \u8bfb\u53d6\u6a21\u677f\u5931\u8d25 - " + ex.Message; }

            try
            {
                PptxTemplateEngine.Render(templateBytes, slides, fullPath);
            }
            catch (Exception ex)
            {
                return "Error: \u6e32\u67d3\u5931\u8d25 - " + ex.Message;
            }

            var size = new FileInfo(fullPath).Length;
            return "\u5df2\u751f\u6210 pptx: " + fullPath +
                   "  (" + FileParserService.FormatBytes(size) + ", " + slides.Count + " \u5f20 slide)";
        }
    }
}
