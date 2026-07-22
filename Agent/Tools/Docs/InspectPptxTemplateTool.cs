// TxTools.Agent / Tools / Docs / InspectPptxTemplateTool.cs
// 扫描 pptx 模板文件,列出每张 slide 的占位符 —— 让 AI "看清"模板结构后再调用
// render_pptx_template 填入数据,不用瞎猜占位符名。
//
// 识别两类:
//   1) 文本占位符 {{KEY}}   —— 匹配 slide 里所有文本框的内容
//   2) 图片占位符 IMG_xxx   —— 匹配形状名以 IMG_ 开头的所有 shape
//
// 跨 Run 兼容: PowerPoint 常把一段文本拆成多个 Run (不同格式),
//   {{TIT + LE}} 分开时逐 Run 扫会漏。这里把段落内所有 Run 文字拼起来再匹配。
//
// 只读工具,不改任何东西。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Newtonsoft.Json.Linq;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace TxTools.Agent.Core
{
    public sealed class InspectPptxTemplateTool : ITxAgentTool
    {
        public string Name { get { return "inspect_pptx_template"; } }
        public string Description
        {
            get
            {
                return "\u626b\u63cf pptx \u6a21\u677f\u6587\u4ef6, \u5217\u51fa\u6bcf\u5f20 slide \u91cc\u7684\u5360\u4f4d\u7b26: " +
                       "\u6587\u672c\u5360\u4f4d\u7b26 {{KEY}} + \u56fe\u7247\u5360\u4f4d\u7b26 IMG_xxx(\u5f62\u72b6\u540d)\u3002" +
                       "\u4e3a\u540e\u7eed render_pptx_template \u63d0\u4f9b\u7cbe\u786e\u7684 key \u6e05\u5355\u3002" +
                       "\u53c2\u6570 template_path\uff0c\u53ef\u9009 format=text|json (\u9ed8\u8ba4 text)\u3002";
            }
        }
        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['template_path'],
                    'properties': {
                        'template_path': { 'type': 'string', 'description': '模板 pptx 的绝对路径' },
                        'format':        { 'type': 'string', 'enum': ['text','json'], 'default': 'text' }
                    }
                }");
            }
        }

        private static readonly Regex PlaceholderRegex = new Regex(@"\{\{([A-Za-z0-9_\-\.]+)\}\}", RegexOptions.Compiled);

        public string Execute(JObject input)
        {
            var path = ToolInputHelpers.String(input["template_path"]);
            var format = ToolInputHelpers.String(input["format"], "text");

            if (string.IsNullOrWhiteSpace(path)) return "Error: template_path \u5fc5\u9700";
            if (!File.Exists(path)) return "Error: \u6587\u4ef6\u4e0d\u5b58\u5728 - " + path;

            var slides = new List<SlideInspection>();
            var allTextKeys = new SortedSet<string>(StringComparer.Ordinal);
            var allImgNames = new SortedSet<string>(StringComparer.Ordinal);

            try
            {
                using (var doc = PresentationDocument.Open(path, false))
                {
                    var presPart = doc.PresentationPart;
                    if (presPart == null) return "Error: pptx \u65e0 PresentationPart";

                    var slideParts = presPart.SlideParts.ToList();
                    for (int i = 0; i < slideParts.Count; i++)
                    {
                        var sp = slideParts[i];
                        var info = InspectSlide(sp, i + 1);
                        slides.Add(info);
                        foreach (var k in info.TextKeys) allTextKeys.Add(k);
                        foreach (var n in info.ImageNames) allImgNames.Add(n);
                    }
                }
            }
            catch (Exception ex)
            {
                return "Error: \u89e3\u6790\u5931\u8d25 - " + ex.Message;
            }

            if (format == "json")
                return BuildJsonReport(path, slides, allTextKeys, allImgNames);
            return BuildTextReport(path, slides, allTextKeys, allImgNames);
        }

        // ── 单张 slide 扫描 ──

        private sealed class SlideInspection
        {
            public int Index;
            public List<string> TextKeys = new List<string>();
            public List<string> ImageNames = new List<string>();
            /// <summary>额外:找到的普通文本(未含占位符),仅供 AI 理解 slide 结构。取前几条。</summary>
            public List<string> SampleTexts = new List<string>();
        }

        private static SlideInspection InspectSlide(SlidePart sp, int index)
        {
            var info = new SlideInspection { Index = index };
            var textKeys = new HashSet<string>(StringComparer.Ordinal);

            // 文本占位符 —— 按段落拼 Run 再匹配 (跨 Run 兼容)
            foreach (var para in sp.Slide.Descendants<A.Paragraph>())
            {
                var full = string.Concat(para.Elements<A.Run>()
                    .Select(r => r.Text != null ? r.Text.Text : ""));
                if (string.IsNullOrWhiteSpace(full)) continue;

                var matches = PlaceholderRegex.Matches(full);
                foreach (Match m in matches)
                    textKeys.Add(m.Groups[1].Value);

                // 采样: 短的非占位纯文本作为参考(前 3 条,每条 30 字符内)
                if (matches.Count == 0 && info.SampleTexts.Count < 3 && full.Length <= 40)
                    info.SampleTexts.Add(full.Trim());
            }
            info.TextKeys.AddRange(textKeys.OrderBy(x => x));

            // 图片占位符 —— 形状名以 IMG_ 开头
            foreach (var shape in sp.Slide.Descendants<P.Shape>())
            {
                var nv = shape.NonVisualShapeProperties;
                if (nv == null) continue;
                var dp = nv.NonVisualDrawingProperties;
                if (dp == null || dp.Name == null) continue;
                var n = dp.Name.Value;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("IMG_", StringComparison.OrdinalIgnoreCase))
                    info.ImageNames.Add(n);
            }
            info.ImageNames.Sort(StringComparer.Ordinal);

            return info;
        }

        // ── 报表输出 ──

        private static string BuildTextReport(string path, List<SlideInspection> slides,
            SortedSet<string> allTextKeys, SortedSet<string> allImgNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\u6a21\u677f: " + path);
            sb.AppendLine("Slides: " + slides.Count);
            sb.AppendLine("\u6587\u672c\u5360\u4f4d\u7b26\u603b\u96c6: " +
                (allTextKeys.Count == 0 ? "(\u65e0)" : string.Join(", ", allTextKeys)));
            sb.AppendLine("\u56fe\u7247\u5360\u4f4d\u7b26\u603b\u96c6: " +
                (allImgNames.Count == 0 ? "(\u65e0)" : string.Join(", ", allImgNames)));
            sb.AppendLine();

            foreach (var s in slides)
            {
                sb.AppendLine("\u2500\u2500 Slide " + s.Index + " \u2500\u2500");
                sb.AppendLine("  \u6587\u672c\u5360\u4f4d\u7b26: " +
                    (s.TextKeys.Count == 0 ? "(\u65e0)" : string.Join(", ", s.TextKeys)));
                sb.AppendLine("  \u56fe\u7247\u5360\u4f4d\u7b26: " +
                    (s.ImageNames.Count == 0 ? "(\u65e0)" : string.Join(", ", s.ImageNames)));
                if (s.SampleTexts.Count > 0)
                    sb.AppendLine("  \u5176\u4ed6\u6587\u672c: " + string.Join(" / ", s.SampleTexts));
                sb.AppendLine();
            }

            if (allTextKeys.Count > 0 || allImgNames.Count > 0)
            {
                sb.AppendLine("\u4f7f\u7528\u63d0\u793a: \u628a\u4e0a\u9762\u7684 key \u4f5c\u4e3a render_pptx_template " +
                    "\u7684 replacements/images \u5b57\u5178\u952e\u5373\u53ef\u3002");
            }
            return sb.ToString();
        }

        private static string BuildJsonReport(string path, List<SlideInspection> slides,
            SortedSet<string> allTextKeys, SortedSet<string> allImgNames)
        {
            var arr = new JArray();
            foreach (var s in slides)
            {
                arr.Add(new JObject
                {
                    ["index"] = s.Index,
                    ["text_placeholders"] = new JArray(s.TextKeys),
                    ["image_placeholders"] = new JArray(s.ImageNames),
                    ["sample_texts"] = new JArray(s.SampleTexts)
                });
            }
            var root = new JObject
            {
                ["template_path"] = path,
                ["slide_count"] = slides.Count,
                ["all_text_keys"] = new JArray(allTextKeys),
                ["all_image_names"] = new JArray(allImgNames),
                ["slides"] = arr
            };
            return Newtonsoft.Json.JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);
        }
    }
}
