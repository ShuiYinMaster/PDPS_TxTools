// TxTools.Agent / Tools / Docs / PptxExportTool.cs
//
// 从零生成 pptx (与 export_docx 使用体验一致,不需要外部模板)。
//
// 实现:
//   1. BlankPptxData.GetBytes() 拿到内嵌的最小空白 pptx (28 KB, gzip+base64 硬编码)
//   2. 拷到目标路径 → 打开 → 拿 SlideMaster+SlideLayout (模板已有)
//   3. 按用户输入的 slides[] 逐张:
//      - 建 SlidePart, 引用 layout
//      - 每张 slide 上加: title 文本框 (顶部) + bullets 文本框 (中部) + 可选图片 (中下部)
//      - 加进 SlideIdList
//   4. 保存
//
// 用户不用管模板、不用配 EmbeddedResource,export_pptx({slides:[...]}) 一步到位。
// 需要自定义排版/占位符请用 render_pptx_template + 自己做的模板 pptx。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Newtonsoft.Json.Linq;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace TxTools.Agent.Core
{
    public sealed class PptxSlideInput
    {
        public string Title { get; set; }
        public List<string> Bullets { get; set; } = new List<string>();
        /// <summary>本地文件绝对路径,存在则嵌入到 slide 中部;不存在或为空则不加图。</summary>
        public string ImagePath { get; set; }
    }

    public sealed class PptxExportTool : ITxAgentTool
    {
        public string Name { get { return "export_pptx"; } }
        public string Description
        {
            get
            {
                return "\u4ece\u96f6\u751f\u6210 PowerPoint (.pptx) \u5230\u684c\u9762 TxTools_Exports \u76ee\u5f55\u3002" +
                       "\u4e0d\u9700\u5916\u90e8\u6a21\u677f,\u5185\u7f6e\u7a7a\u767d\u9aa8\u67b6\u3002" +
                       "\u53c2\u6570 file_name(\u53ef\u9009), slides[{title, bullets[], image_path(\u53ef\u9009,\u672c\u5730\u56fe\u7247\u7edd\u5bf9\u8def\u5f84)}]\u3002" +
                       "\u9700\u81ea\u5b9a\u4e49\u6392\u7248/\u5360\u4f4d\u7b26\u8bf7\u7528 render_pptx_template + \u81ea\u505a\u6a21\u677f\u3002";
            }
        }
        public bool IsReadOnly { get { return false; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'required': ['slides'],
                    'properties': {
                        'file_name': { 'type': 'string' },
                        'slides': {
                            'type': 'array',
                            'items': {
                                'type': 'object',
                                'properties': {
                                    'title':      { 'type': 'string' },
                                    'bullets':    { 'type': 'array', 'items': { 'type': 'string' } },
                                    'image_path': { 'type': 'string', 'description': '本地图片绝对路径(png/jpg),如 capture_viewer_image 返回的路径' }
                                }
                            }
                        }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var fileName = ToolInputHelpers.String(input["file_name"]);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "presentation_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            if (!fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) fileName += ".pptx";
            fileName = DocxExportTool.SafeFileName(fileName);

            var slidesTok = input["slides"] as JArray;
            if (slidesTok == null || slidesTok.Count == 0)
                return "Error: slides \u4e3a\u7a7a";

            var slides = new List<PptxSlideInput>();
            foreach (var s in slidesTok)
            {
                var so = s as JObject;
                if (so == null) continue;
                slides.Add(new PptxSlideInput
                {
                    Title = ToolInputHelpers.String(so["title"]) ?? "",
                    Bullets = ToolInputHelpers.StringList(so["bullets"]),
                    ImagePath = ToolInputHelpers.String(so["image_path"])
                });
            }
            if (slides.Count == 0) return "Error: slides \u4e3a\u7a7a";

            var outDir = DocxExportTool.GetExportDir();
            var fullPath = Path.Combine(outDir, fileName);

            try
            {
                // 1) 从内嵌 blank 拷到目标路径
                File.WriteAllBytes(fullPath, BlankPptxData.GetBytes());

                // 2) 打开,删示例 slide, 追加新 slide
                using (var pres = PresentationDocument.Open(fullPath, true))
                {
                    var presPart = pres.PresentationPart;
                    var layoutPart = presPart.SlideMasterParts.FirstOrDefault()?.SlideLayoutParts.FirstOrDefault();
                    if (layoutPart == null)
                        return "Error: \u5185\u7f6e\u6a21\u677f\u6ca1\u6709 SlideLayout(\u4e0d\u5e94\u53d1\u751f)";

                    RemoveAllSlides(presPart);

                    var idList = presPart.Presentation.SlideIdList ?? (presPart.Presentation.SlideIdList = new SlideIdList());
                    uint idBase = 256U;

                    for (int i = 0; i < slides.Count; i++)
                    {
                        var slidePart = presPart.AddNewPart<SlidePart>();
                        slidePart.Slide = BuildSlide(slides[i], slidePart);
                        slidePart.AddPart(layoutPart);

                        idList.Append(new SlideId
                        {
                            Id = (UInt32Value)(idBase + (uint)i),
                            RelationshipId = presPart.GetIdOfPart(slidePart)
                        });
                    }

                    presPart.Presentation.Save();
                }
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }

            var size = new FileInfo(fullPath).Length;
            return "\u5df2\u751f\u6210 pptx: " + fullPath +
                   "  (" + FileParserService.FormatBytes(size) + ", " + slides.Count + " \u5f20 slide)";
        }

        // ── slide 构造 ──

        // Slide 4:3 尺寸: 9144000 x 6858000 EMU  (10" x 7.5")
        private const long TitleX = 457200L, TitleY = 274638L, TitleCx = 8229600L, TitleCy = 900000L;
        private const long BulletsX = 457200L, BulletsY = 1250000L, BulletsCx = 8229600L, BulletsCy = 1400000L;
        private const long ImageX = 457200L, ImageY = 2750000L, ImageCx = 8229600L, ImageCy = 3800000L;

        private static Slide BuildSlide(PptxSlideInput data, SlidePart slidePart)
        {
            var tree = new ShapeTree();
            tree.Append(new NonVisualGroupShapeProperties(
                new NonVisualDrawingProperties { Id = 1U, Name = "" },
                new NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()));
            tree.Append(new GroupShapeProperties(new A.TransformGroup()));

            // 标题
            if (!string.IsNullOrEmpty(data.Title))
                tree.Append(BuildTextShape(2U, "Title", data.Title,
                    TitleX, TitleY, TitleCx, TitleCy, fontSizeHalfPt: 32, bold: true, isBullets: false, bulletList: null));

            // Bullets
            if (data.Bullets != null && data.Bullets.Count > 0)
                tree.Append(BuildTextShape(3U, "Content", null,
                    BulletsX, BulletsY, BulletsCx, BulletsCy, fontSizeHalfPt: 20, bold: false, isBullets: true, bulletList: data.Bullets));

            // 图片 (如果有本地文件)
            if (!string.IsNullOrEmpty(data.ImagePath) && File.Exists(data.ImagePath))
            {
                var pic = BuildPicture(slidePart, data.ImagePath, 4U, "Image",
                    ImageX, ImageY, ImageCx, ImageCy);
                if (pic != null) tree.Append(pic);
            }

            var slide = new Slide(new CommonSlideData(tree),
                new ColorMapOverride(new A.MasterColorMapping()));
            return slide;
        }

        private static Shape BuildTextShape(uint id, string name, string singleText,
            long xEmu, long yEmu, long cxEmu, long cyEmu,
            int fontSizeHalfPt, bool bold, bool isBullets, IList<string> bulletList)
        {
            var body = new TextBody(
                new A.BodyProperties { Anchor = A.TextAnchoringTypeValues.Top, Wrap = A.TextWrappingValues.Square },
                new A.ListStyle());

            if (isBullets && bulletList != null)
            {
                foreach (var line in bulletList)
                {
                    body.Append(new A.Paragraph(
                        new A.ParagraphProperties(new A.CharacterBullet { Char = "\u2022" })
                        { Level = 0, Indent = -228600, LeftMargin = 342900 },
                        new A.Run(
                            new A.RunProperties { FontSize = fontSizeHalfPt * 100, Language = "zh-CN" },
                            new A.Text(line ?? ""))));
                }
            }
            else
            {
                body.Append(new A.Paragraph(
                    new A.Run(
                        new A.RunProperties { FontSize = fontSizeHalfPt * 100, Bold = bold, Language = "zh-CN" },
                        new A.Text(singleText ?? ""))));
            }

            return new Shape(
                new NonVisualShapeProperties(
                    new NonVisualDrawingProperties { Id = id, Name = name },
                    new NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new ApplicationNonVisualDrawingProperties()),
                new ShapeProperties(
                    new A.Transform2D(new A.Offset { X = xEmu, Y = yEmu }, new A.Extents { Cx = cxEmu, Cy = cyEmu }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
                body);
        }

        private static P.Picture BuildPicture(SlidePart slidePart, string imagePath,
            uint id, string name, long xEmu, long yEmu, long cxEmu, long cyEmu)
        {
            try
            {
                var contentType = ContentTypeFromExt(imagePath);
                var imagePart = slidePart.AddImagePart(contentType);
                using (var s = File.OpenRead(imagePath)) imagePart.FeedData(s);
                var relId = slidePart.GetIdOfPart(imagePart);

                return new P.Picture(
                    new P.NonVisualPictureProperties(
                        new P.NonVisualDrawingProperties { Id = id, Name = name },
                        new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.BlipFill(
                        new A.Blip { Embed = relId },
                        new A.Stretch(new A.FillRectangle())),
                    new P.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = xEmu, Y = yEmu }, new A.Extents { Cx = cxEmu, Cy = cyEmu }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
            }
            catch { return null; }
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

        private static void RemoveAllSlides(PresentationPart presPart)
        {
            var idList = presPart.Presentation.SlideIdList;
            var slides = presPart.SlideParts.ToList();
            foreach (var sp in slides)
            {
                var relId = presPart.GetIdOfPart(sp);
                if (idList != null)
                {
                    var sid = idList.Elements<SlideId>().FirstOrDefault(x => x.RelationshipId == relId);
                    if (sid != null) sid.Remove();
                }
                presPart.DeletePart(sp);
            }
        }
    }
}