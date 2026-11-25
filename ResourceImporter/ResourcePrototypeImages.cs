using System;
using System.Drawing;
using System.Windows.Forms;
using Tecnomatix.Engineering.Ui;

namespace TxTools.ResourceImporter
{
    internal static class ResourcePrototypeImages
    {
        private static readonly Color[] Colors =
            { Color.SteelBlue, Color.SeaGreen, Color.Firebrick, Color.SlateBlue, Color.DarkCyan };

        internal static void Add(ImageList images, ResourcePrototype type, FontFamily fontFamily)
        {
            bool prefixed = type.Type.StartsWith("Pm", StringComparison.Ordinal) && type.Type.Length > 2;
            string alias = prefixed ? type.Type.Substring(2) : "Pm" + type.Type;
            foreach (string key in new[] { type.Type, alias })
            {
                Image image = null;
                try { image = TxImageProvider.GetImageByPlanningType(key); }
                catch (Exception) { /* Some planning classes have no SDK icon. */ }
                if (image == null) continue;
                images.Images.Add(type.Type, image);
                return;
            }

            int size = images.ImageSize.Width;
            int index = (StringComparer.OrdinalIgnoreCase.GetHashCode(type.Type) & int.MaxValue) % Colors.Length;
            string name = prefixed ? alias : type.Type;
            string initials = name.Substring(0, Math.Min(2, name.Length)).ToUpperInvariant();
            using (var fallback = new Bitmap(size, size))
            using (var graphics = Graphics.FromImage(fallback))
            using (var brush = new SolidBrush(Colors[index]))
            using (var font = new Font(fontFamily, size * .53f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(brush, 1, 1, size - 2, size - 2);
                graphics.DrawString(initials, font, Brushes.White, new RectangleF(0, 0, size, size), centered);
                images.Images.Add(type.Type, (Image)fallback.Clone());
            }
        }
    }
}
