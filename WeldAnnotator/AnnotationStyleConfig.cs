using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TxTools.WeldAnnotator
{
    internal static class AnnotationStyleConfig
    {
        internal static string FilePath
        {
            get
            {
                return Path.Combine(Path.GetDirectoryName(typeof(AnnotationStyleConfig).Assembly.Location),
                    "config", "weld-annotation-style.json");
            }
        }

        internal static AnnotationStyle Load(Action<string> warning = null, string path = null)
        {
            path = path ?? FilePath;
            if (!File.Exists(path)) return new AnnotationStyle();
            try
            {
                var json = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                if ((int?)json["Version"] != 1) throw new FormatException("不支持的配置版本");
                var style = new AnnotationStyle
                {
                    DotColor = ReadColor(json, "DotColor"),
                    DotRadius = ReadInt(json, "DotRadius", 2, 30),
                    LineColor = ReadColor(json, "LineColor"),
                    LineWidth = (float)ReadNumber(json, "LineWidth", 1, 10),
                    BoxBorderColor = ReadColor(json, "BoxBorderColor"),
                    BoxFillColor = ReadColor(json, "BoxFillColor"),
                    TextColor = ReadColor(json, "TextColor"),
                    BoxPadding = ReadInt(json, "BoxPadding", 1, 20),
                    OffsetX = ReadInt(json, "OffsetX", 10, 300),
                    OffsetY = ReadInt(json, "OffsetY", 10, 300)
                };
                var font = json["TextFont"] as JObject;
                if (font == null) throw new FormatException("缺少字体配置");
                string name = (string)font["Name"];
                if (string.IsNullOrWhiteSpace(name)) throw new FormatException("字体名称为空");
                float size = (float)ReadNumber(font, "SizeInPoints", 1, 200);
                int flags = ReadInt(font, "Style", 0, 15);
                var loadedFont = new Font(name, size, (FontStyle)flags, GraphicsUnit.Point);
                try
                {
                    ReadCategories(json, style);
                    style.TextFont.Dispose();
                    style.TextFont = loadedFont;
                    return style;
                }
                catch { loadedFont.Dispose(); style.TextFont.Dispose(); throw; }
            }
            catch (Exception ex)
            {
                warning?.Invoke("标注样式配置读取失败，已使用默认样式：" + ex.Message);
                return new AnnotationStyle();
            }
        }

        internal static void Save(AnnotationStyle style, string path = null)
        {
            if (style == null) throw new ArgumentNullException(nameof(style));
            path = path ?? FilePath;
            var json = new JObject
            {
                ["Version"] = 1,
                ["DotColor"] = ColorText(style.DotColor),
                ["DotRadius"] = style.DotRadius,
                ["LineColor"] = ColorText(style.LineColor),
                ["LineWidth"] = style.LineWidth,
                ["BoxBorderColor"] = ColorText(style.BoxBorderColor),
                ["BoxFillColor"] = ColorText(style.BoxFillColor),
                ["TextColor"] = ColorText(style.TextColor),
                ["TextFont"] = new JObject
                {
                    ["Name"] = style.TextFont.Name, ["SizeInPoints"] = style.TextFont.SizeInPoints,
                    ["Style"] = (int)style.TextFont.Style
                },
                ["BoxPadding"] = style.BoxPadding, ["OffsetX"] = style.OffsetX, ["OffsetY"] = style.OffsetY,
                ["CategoryShapes"] = JObject.FromObject(style.CategoryShapes),
                ["CategoryFilled"] = JObject.FromObject(style.CategoryFilled)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            // Publish a complete file atomically so an interrupted write preserves the saved settings.
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string ColorText(Color color) { return "#" + color.ToArgb().ToString("X8"); }

        private static Color ReadColor(JObject json, string key)
        {
            string text = (string)json[key];
            if (text == null || text.Length != 9 || text[0] != '#' ||
                !uint.TryParse(text.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
                throw new FormatException("颜色配置无效：" + key);
            return Color.FromArgb(unchecked((int)argb));
        }

        private static double ReadNumber(JObject json, string key, double min, double max)
        {
            var token = json[key];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new FormatException("数值配置无效：" + key);
            double value = (double)token;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new FormatException("数值配置超出范围：" + key);
            return value;
        }

        private static int ReadInt(JObject json, string key, int min, int max)
        {
            double value = ReadNumber(json, key, min, max);
            if (value != Math.Truncate(value)) throw new FormatException("需要整数：" + key);
            return (int)value;
        }

        private static void ReadCategories(JObject json, AnnotationStyle style)
        {
            var shapes = json["CategoryShapes"] as JObject;
            var filled = json["CategoryFilled"] as JObject;
            if (shapes == null || filled == null) throw new FormatException("缺少分类样式");
            foreach (string category in new System.Collections.Generic.List<string>(style.CategoryShapes.Keys))
            {
                if (shapes[category] != null)
                {
                    int shape = ReadInt(shapes, category, 1, 12);
                    if (shape != 1 && shape != 4 && shape != 5 && shape != 7 && shape != 9 && shape != 10 && shape != 12)
                        throw new FormatException("分类形状无效：" + category);
                    style.CategoryShapes[category] = shape;
                }
                if (filled[category] != null)
                {
                    if (filled[category].Type != JTokenType.Boolean) throw new FormatException("分类填充配置无效");
                    style.CategoryFilled[category] = (bool)filled[category];
                }
            }
        }
    }
}
