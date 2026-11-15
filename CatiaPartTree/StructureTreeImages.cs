using System;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.CatiaPartTree
{
    internal static class StructureTreeImages
    {
        internal static ImageList Create(int dpi)
        {
            int size = Math.Max(16, (int)Math.Round(16 * dpi / 96.0));
            var images = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
            Add(images, "container", new[] { "PmCompoundPart", "CompoundPart" }, true);
            Add(images, "part", new[] { "PmPartInstance", "PartInstance", "PmPart", "Part", "PartPrototype", "PmPartPrototype" }, false);
            return images;
        }
        private static void Add(ImageList images, string key, string[] types, bool compound)
        {
            Image native = null;
            try
            {
                foreach (string type in types)
                {
                    try { native = TxImageProvider.GetImageByPlanningType(type); }
                    catch { /* Missing planning icons must not block the workflow. */ }
                    if (native != null) break;
                }
            }
            catch { }
            using (var bitmap = new Bitmap(images.ImageSize.Width, images.ImageSize.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                if (native != null) graphics.DrawImage(native, new Rectangle(Point.Empty, bitmap.Size));
                else
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.ScaleTransform(bitmap.Width / 16f, bitmap.Height / 16f);
                    if (compound)
                    {
                        Cube(graphics, 5, 1, 6, Color.SteelBlue);
                        Cube(graphics, 1, 7, 6, Color.SteelBlue);
                        Cube(graphics, 9, 7, 6, Color.SteelBlue);
                    }
                    else Cube(graphics, 2, 2, 12, Color.DarkGoldenrod);
                }
                images.Images.Add(key, (Image)bitmap.Clone());
            }
        }
        private static void Cube(Graphics graphics, float x, float y, float size, Color color)
        {
            float half = size / 2, quarter = size / 4;
            var top = new[] { new PointF(x + half, y), new PointF(x + size, y + quarter), new PointF(x + half, y + half), new PointF(x, y + quarter) };
            var left = new[] { top[3], top[2], new PointF(x + half, y + size), new PointF(x, y + size - quarter) };
            var right = new[] { top[2], top[1], new PointF(x + size, y + size - quarter), left[2] };
            using (var brush = new SolidBrush(ControlPaint.Light(color))) graphics.FillPolygon(brush, top);
            using (var brush = new SolidBrush(color)) graphics.FillPolygon(brush, left);
            using (var brush = new SolidBrush(ControlPaint.Dark(color))) graphics.FillPolygon(brush, right);
            using (var pen = new Pen(ControlPaint.DarkDark(color), 0.65f))
            { graphics.DrawPolygon(pen, top); graphics.DrawPolygon(pen, left); graphics.DrawPolygon(pen, right); }
        }
    }
}
