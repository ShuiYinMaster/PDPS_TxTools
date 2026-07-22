// TxTools.Agent / Core / Pptx / PptxTemplateEngine.cs
// PPT 模板渲染引擎 —— 拿一个 .pptx 模板,对每张 slide 做两类替换:
//
//   1) 文本占位符: 遍历 slide 里所有 <a:t> 节点, 把 {{KEY}} 替换成 replacements[key] 的值
//      支持段内多 <a:t> 分片(PowerPoint 常见,不同格式的 span 会分开存),
//      我们把整段拼起来再替换,替换后放回第一个 run,其他 run 清空。
//
//   2) 图片占位符: 找到形状名 (NonVisualDrawingProperties.Name) 匹配 images 里 key 的 shape,
//      记录其 x/y/cx/cy, 移除 shape, 在同位置以同尺寸插入 Picture (引用新增的 ImagePart)。
//      形状名规范:模板里给你想放图的形状(通常是矩形/占位符)改名为 IMG_xxx (如 IMG_screenshot1)。
//      Agent 调用时 images = { "IMG_screenshot1": "C:/path/to/img.png" } 即可。
//
// 使用者负责准备模板 pptx。占位符命名不限,只要 agent 调用时的 key 与模板里一致就行。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace TxTools.Agent.Core
{
    /// <summary>渲染一张 slide 的数据:文本替换 + 图片替换。</summary>
    public sealed class SlideRenderData
    {
        /// <summary>用模板里第几张 slide 作为源(0-based)。默认 0 即第 1 张。</summary>
        public int TemplateSlideIndex { get; set; } = 0;
        /// <summary>文本占位符 {{KEY}} → 值</summary>
        public Dictionary<string, string> Replacements { get; set; } = new Dictionary<string, string>();
        /// <summary>图片占位符 (形状名) → 图片本地文件路径</summary>
        public Dictionary<string, string> Images { get; set; } = new Dictionary<string, string>();
    }

    public static class PptxTemplateEngine
    {
        /// <summary>
        /// 用 templateBytes 里的模板生成新 pptx 到 outputPath。
        /// slides 里每条描述一张要生成的 slide (通过 clone 模板中的某张 slide 得到)。
        /// </summary>
        public static void Render(byte[] templateBytes, IList<SlideRenderData> slides, string outputPath)
        {
            if (templateBytes == null || templateBytes.Length == 0)
                throw new ArgumentException("templateBytes \u4e3a\u7a7a", nameof(templateBytes));
            if (slides == null || slides.Count == 0)
                throw new ArgumentException("slides \u4e3a\u7a7a", nameof(slides));

            // 拷贝模板到目标路径
            File.WriteAllBytes(outputPath, templateBytes);

            using (var doc = PresentationDocument.Open(outputPath, true))
            {
                var presPart = doc.PresentationPart;
                if (presPart == null || presPart.Presentation == null)
                    throw new InvalidOperationException("\u6a21\u677f\u65e0 PresentationPart");

                // 缓存所有模板 slide part 的引用(供 clone 用)
                var templateSlideParts = new List<SlidePart>(presPart.SlideParts);
                if (templateSlideParts.Count == 0)
                    throw new InvalidOperationException("\u6a21\u677f pptx \u91cc\u4e00\u5f20 slide \u90fd\u6ca1\u6709");

                // 先克隆出所有目标 slide (还在同一 SlideIdList 里排在末尾)
                var newSlideParts = new List<SlidePart>();
                foreach (var data in slides)
                {
                    var idx = Math.Max(0, Math.Min(templateSlideParts.Count - 1, data.TemplateSlideIndex));
                    var src = templateSlideParts[idx];
                    newSlideParts.Add(CloneSlide(presPart, src));
                }

                // 移除模板原始的 slide 引用(SlideIdList 里对应的 SlideId 项,以及关系)
                RemoveTemplateSlides(presPart, templateSlideParts);

                // 对每张新 slide 做替换
                for (int i = 0; i < newSlideParts.Count; i++)
                {
                    ApplyReplacements(newSlideParts[i], slides[i]);
                }

                presPart.Presentation.Save();
            }
        }

        // ── clone slide (含 layout/media 关系) ──

        private static SlidePart CloneSlide(PresentationPart presPart, SlidePart src)
        {
            // 新建 SlidePart,复制 XML
            var newPart = presPart.AddNewPart<SlidePart>();
            using (var srcStream = src.GetStream())
            using (var dstStream = newPart.GetStream(FileMode.Create))
            {
                srcStream.CopyTo(dstStream);
            }
            // 复用同 layout (关键:不复制 layout,直接引用原 layout part)
            if (src.SlideLayoutPart != null)
                newPart.AddPart(src.SlideLayoutPart);

            // 复制 image parts (如果模板 slide 本身含图,克隆后要保持链接)
            foreach (var img in src.ImageParts)
            {
                var relId = src.GetIdOfPart(img);
                var newImg = newPart.AddImagePart(img.ContentType);
                using (var s = img.GetStream())
                using (var d = newImg.GetStream(FileMode.Create))
                    s.CopyTo(d);
                // 保持原关系 id (让 slide xml 里的 r:embed="rIdX" 还有效)
                var newRelId = newPart.GetIdOfPart(newImg);
                if (newRelId != relId)
                    RenameRelationshipInXml(newPart, newRelId, relId);
            }

            // 加进 SlideIdList
            var idList = presPart.Presentation.SlideIdList
                        ?? (presPart.Presentation.SlideIdList = new SlideIdList());
            uint maxId = 255U;
            foreach (var sid in idList.Elements<SlideId>())
                if (sid.Id != null && sid.Id.Value > maxId) maxId = sid.Id.Value;
            idList.Append(new SlideId
            {
                Id = (UInt32Value)(maxId + 1),
                RelationshipId = presPart.GetIdOfPart(newPart)
            });

            return newPart;
        }

        private static void RenameRelationshipInXml(SlidePart part, string oldRel, string newRel)
        {
            // 简单文本替换 —— 因为 rId 只在 embed/link 属性里出现,冲突概率极低
            using (var s = part.GetStream(FileMode.Open))
            {
                var reader = new StreamReader(s);
                var xml = reader.ReadToEnd();
                xml = xml.Replace("r:embed=\"" + oldRel + "\"", "r:embed=\"" + newRel + "\"");
                xml = xml.Replace("r:link=\"" + oldRel + "\"", "r:link=\"" + newRel + "\"");
                s.SetLength(0);
                using (var w = new StreamWriter(s))
                    w.Write(xml);
            }
        }

        private static void RemoveTemplateSlides(PresentationPart presPart, List<SlidePart> templateParts)
        {
            var idList = presPart.Presentation.SlideIdList;
            if (idList == null) return;
            foreach (var tp in templateParts)
            {
                string relId;
                try { relId = presPart.GetIdOfPart(tp); }
                catch { continue; }
                var sid = idList.Elements<SlideId>().FirstOrDefault(x => x.RelationshipId == relId);
                if (sid != null) sid.Remove();
                presPart.DeletePart(tp);
            }
        }

        // ── 替换文本 + 图片 ──

        private static readonly Regex PlaceholderRegex = new Regex(@"\{\{([A-Za-z0-9_\-\.]+)\}\}", RegexOptions.Compiled);

        private static void ApplyReplacements(SlidePart slidePart, SlideRenderData data)
        {
            // 1) 文本占位符替换
            if (data.Replacements != null && data.Replacements.Count > 0)
            {
                foreach (var para in slidePart.Slide.Descendants<A.Paragraph>())
                    ReplaceInParagraph(para, data.Replacements);
            }

            // 2) 图片占位符 —— 按 shape.Name 匹配
            if (data.Images != null && data.Images.Count > 0)
            {
                var shapes = slidePart.Slide.Descendants<P.Shape>().ToList();
                foreach (var sp in shapes)
                {
                    var nv = sp.NonVisualShapeProperties?.NonVisualDrawingProperties;
                    var name = nv?.Name?.Value;
                    if (string.IsNullOrEmpty(name)) continue;

                    string imgPath;
                    if (!data.Images.TryGetValue(name, out imgPath)) continue;
                    if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath)) continue;

                    ReplaceShapeWithImage(slidePart, sp, imgPath, name);
                }
            }
        }

        /// <summary>
        /// 对一个 paragraph 做 {{KEY}} 替换。
        /// 关键坑:PowerPoint 常把一段文本拆成多个 Run(不同格式),
        /// 直接遍历 Run 会导致 "{{TIT" 和 "LE}}" 在两个 Run 里,匹配不到。
        /// 做法:把整段 InnerText 拼起来做替换,替换结果放到第一个 Run,其他 Run 清空文本。
        /// </summary>
        private static void ReplaceInParagraph(A.Paragraph para, Dictionary<string, string> repl)
        {
            var runs = para.Elements<A.Run>().ToList();
            if (runs.Count == 0) return;

            var full = string.Concat(runs.Select(r => r.Text?.Text ?? ""));
            if (!PlaceholderRegex.IsMatch(full)) return;   // 无占位符,跳过

            var replaced = PlaceholderRegex.Replace(full, m =>
            {
                var key = m.Groups[1].Value;
                string v;
                return repl.TryGetValue(key, out v) ? v ?? "" : m.Value;
            });

            // 结果放第一个 run,其他清空
            if (runs[0].Text == null) runs[0].Text = new A.Text();
            runs[0].Text.Text = replaced;
            // 注: A.Text (Drawing.Text) 无 Space 属性(那是 Wordprocessing.Text 才有的),
            // Drawing 命名空间下空白处理由 SDK 自动完成
            for (int i = 1; i < runs.Count; i++)
                if (runs[i].Text != null) runs[i].Text.Text = "";
        }

        /// <summary>
        /// 把 shape 替换为图片。位置/尺寸继承原 shape 的 Transform2D。
        /// </summary>
        private static void ReplaceShapeWithImage(SlidePart slidePart, P.Shape sp, string imagePath, string name)
        {
            // 记录原 shape 的位置/尺寸
            var xfrm = sp.ShapeProperties?.Transform2D;
            long x = 0, y = 0, cx = 3000000, cy = 3000000;
            if (xfrm != null)
            {
                if (xfrm.Offset != null) { x = xfrm.Offset.X ?? 0; y = xfrm.Offset.Y ?? 0; }
                if (xfrm.Extents != null) { cx = xfrm.Extents.Cx ?? cx; cy = xfrm.Extents.Cy ?? cy; }
            }

            // 添加 ImagePart
            var contentType = ContentTypeFromExt(imagePath);
            var imagePart = slidePart.AddImagePart(contentType);
            using (var s = File.OpenRead(imagePath))
                imagePart.FeedData(s);
            var relId = slidePart.GetIdOfPart(imagePart);

            // 分配一个新 shape id (拿全 slide 里已有 id + 1)
            uint newId = 100U;
            foreach (var e in slidePart.Slide.Descendants<A.NonVisualDrawingProperties>())
                if (e.Id != null && e.Id.Value >= newId) newId = e.Id.Value + 1;
            foreach (var e in slidePart.Slide.Descendants<P.NonVisualDrawingProperties>())
                if (e.Id != null && e.Id.Value >= newId) newId = e.Id.Value + 1;

            // 构造 Picture
            var pic = new P.Picture(
                new P.NonVisualPictureProperties(
                    new P.NonVisualDrawingProperties { Id = newId, Name = name + "_img" },
                    new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.BlipFill(
                    new A.Blip { Embed = relId },
                    new A.Stretch(new A.FillRectangle())),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = x, Y = y },
                        new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })
            );

            // 用 Picture 替换 shape
            sp.Parent.ReplaceChild(pic, sp);
        }

        private static string ContentTypeFromExt(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            switch (ext)
            {
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".bmp": return "image/bmp";
                default: return "image/png";
            }
        }
    }
}