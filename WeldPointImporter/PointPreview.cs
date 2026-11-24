using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using TxTools.Common;

namespace TxTools.WeldPointImporter
{
    internal static class WeldLayerColors
    {
        private static readonly Color[] Colors =
            { Color.FromArgb(37, 99, 235), Color.FromArgb(192, 82, 14), Color.FromArgb(124, 58, 237) };

        internal static Color Get(WeldLayerGroup group) => Colors[(int)group];
    }

    internal sealed class PointPreview : Control
    {
        private IReadOnlyList<ImportPoint> _points = Array.Empty<ImportPoint>();
        private IReadOnlyDictionary<int, DuplicateMatch> _duplicates = new Dictionary<int, DuplicateMatch>();
        private int[] _visible = Array.Empty<int>();
        private PointF[] _screen = Array.Empty<PointF>();
        private int _view, _selected = -1;
        private double _yaw = -Math.PI / 4, _pitch = Math.PI / 6;
        private double _x, _y, _z, _centerU, _centerV, _fit = 1, _zoom = 1;
        private PointF _pan;
        private Point _mouseDown, _lastDrag;
        private MouseButtons _dragButton;
        private bool _dragged;
        internal event Action<int> PointSelected;

        internal PointPreview()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        internal void UpdatePoints(IReadOnlyList<ImportPoint> points, IReadOnlyDictionary<int, DuplicateMatch> duplicates, int[] visible, bool fit)
        {
            _points = points;
            _duplicates = duplicates;
            _visible = visible;
            if (fit) Fit();
            else Invalidate();
        }

        internal void SelectPoint(int index)
        {
            if (_selected == index) return;
            _selected = index;
            Invalidate();
        }
        internal void SetView(int view) { _view = view; Fit(); }

        internal void Fit()
        {
            _zoom = 1;
            _pan = PointF.Empty;
            if (_visible.Length == 0) { Invalidate(); return; }
            var points = _visible.Select(index => _points[index]).ToArray();
            _x = Middle(points.Min(p => p.X), points.Max(p => p.X));
            _y = Middle(points.Min(p => p.Y), points.Max(p => p.Y));
            _z = Middle(points.Min(p => p.Z), points.Max(p => p.Z));
            var bounds = points.Select(Project).ToArray();
            double minU = bounds.Min(p => p.U), maxU = bounds.Max(p => p.U);
            double minV = bounds.Min(p => p.V), maxV = bounds.Max(p => p.V);
            _centerU = _view == 3 ? 0 : Middle(minU, maxU);
            _centerV = _view == 3 ? 0 : Middle(minV, maxV);
            RectangleF area = PlotArea;
            double spanU = maxU - minU, spanV = maxV - minV;
            if (_view == 3) spanU = spanV = 2 * points.Max(p => Hypot(p.X - _x, p.Y - _y, p.Z - _z));
            _fit = Math.Min(area.Width / Math.Max(spanU, 1), area.Height / Math.Max(spanV, 1)) * 0.9;
            Invalidate();
        }

        private static double Middle(double low, double high) => low / 2 + high / 2;
        private static double Hypot(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);
        private float Dpi => DeviceDpi / 96f;
        private RectangleF PlotArea => new RectangleF(38 * Dpi, 14 * Dpi,
            Math.Max(1, Width - 52 * Dpi), Math.Max(1, Height - 44 * Dpi));

        private (double U, double V) Project(ImportPoint point)
        {
            switch (_view)
            {
                case 0: return (point.X - _x, point.Z - _z);
                case 1: return (point.X - _x, point.Y - _y);
                case 2: return (point.Y - _y, point.Z - _z);
                default:
                    double x = point.X - _x, y = point.Y - _y, z = point.Z - _z;
                    double u = Math.Cos(_yaw) * x - Math.Sin(_yaw) * y;
                    double depth = Math.Sin(_yaw) * x + Math.Cos(_yaw) * y;
                    return (u, Math.Cos(_pitch) * z - Math.Sin(_pitch) * depth);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            var palette = FormUiKit.Theme.GridHeader;
            graphics.Clear(FormUiKit.Theme.GridRowEven);
            RectangleF area = PlotArea;
            using (var grid = new Pen(palette))
            {
                graphics.DrawRectangle(grid, area.X, area.Y, area.Width, area.Height);
                for (int i = 1; i < 5; i++)
                {
                    float x = area.Left + area.Width * i / 5, y = area.Top + area.Height * i / 5;
                    graphics.DrawLine(grid, x, area.Top, x, area.Bottom);
                    graphics.DrawLine(grid, area.Left, y, area.Right, y);
                }
            }
            using (var text = new SolidBrush(FormUiKit.Theme.TextDim))
            {
                string horizontal = _view == 2 ? "Y" : "X";
                string vertical = _view == 1 ? "Y" : "Z";
                if (_view == 3) graphics.DrawString("XYZ (mm)", Font, text, area.Left, area.Bottom + 4 * Dpi);
                else
                {
                    graphics.DrawString(horizontal + " (mm)", Font, text, area.Left + area.Width / 2, area.Bottom + 4 * Dpi);
                    graphics.DrawString(vertical, Font, text, 12 * Dpi, area.Top + area.Height / 2);
                }
                if (_visible.Length == 0) graphics.DrawString("暂无点位", Font, text, area.Left + 10 * Dpi, area.Top + 10 * Dpi);
            }
            if (_screen.Length != _visible.Length) _screen = new PointF[_visible.Length];
            var state = graphics.Save();
            graphics.SetClip(area);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var two = new SolidBrush(WeldLayerColors.Get(WeldLayerGroup.Two)))
            using (var three = new SolidBrush(WeldLayerColors.Get(WeldLayerGroup.Three)))
            using (var fourOrMore = new SolidBrush(WeldLayerColors.Get(WeldLayerGroup.FourOrMore)))
            using (var duplicate = new Pen(FormUiKit.Theme.TextDim, Dpi) { DashStyle = DashStyle.Dot })
            using (var selection = new Pen(FormUiKit.Theme.BtnHover, 2 * Dpi))
            {
                var markers = new[] { two, three, fourOrMore };
                float dot = 4 * Dpi, outline = 6 * Dpi;
                int selected = -1;
                for (int index = 0; index < _visible.Length; index++)
                {
                    int id = _visible[index];
                    var point = Project(_points[id]);
                    var location = new PointF(area.Left + area.Width / 2 + _pan.X + (float)((point.U - _centerU) * _fit * _zoom),
                        area.Top + area.Height / 2 + _pan.Y - (float)((point.V - _centerV) * _fit * _zoom));
                    _screen[index] = location;
                    if (!area.Contains(location)) continue;
                    var marker = markers[(int)WeldLayers.Group(_points[id].LayerCount)];
                    graphics.FillEllipse(marker, location.X - dot, location.Y - dot, 2 * dot, 2 * dot);
                    if (_duplicates.ContainsKey(id))
                        graphics.DrawEllipse(duplicate, location.X - outline, location.Y - outline, 2 * outline, 2 * outline);
                    if (id == _selected) selected = index;
                }
                if (selected >= 0)
                {
                    PointF point = _screen[selected];
                    var marker = markers[(int)WeldLayers.Group(_points[_visible[selected]].LayerCount)];
                    graphics.FillEllipse(marker, point.X - dot, point.Y - dot, 2 * dot, 2 * dot);
                    float ring = 8 * Dpi;
                    graphics.DrawEllipse(selection, point.X - ring, point.Y - ring, 2 * ring, 2 * ring);
                }
            }
            graphics.Restore(state);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!PlotArea.Contains(e.Location)) return;
            Focus();
            _dragButton = e.Button;
            _mouseDown = _lastDrag = e.Location;
            _dragged = false;
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragButton == MouseButtons.None) return;
            if (!_dragged && Math.Abs(e.X - _mouseDown.X) + Math.Abs(e.Y - _mouseDown.Y) < 4 * Dpi) return;
            _dragged = true;
            int dx = e.X - _lastDrag.X, dy = e.Y - _lastDrag.Y;
            _lastDrag = e.Location;
            if (_view == 3 && _dragButton == MouseButtons.Left)
            {
                _yaw += dx * 0.012 / Dpi;
                _pitch = Math.Max(-1.5, Math.Min(1.5, _pitch + dy * 0.012 / Dpi));
            }
            else _pan = new PointF(_pan.X + dx, _pan.Y + dy);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragButton == MouseButtons.Left && !_dragged && PlotArea.Contains(e.Location))
            {
                int nearest = -1;
                double best = Math.Pow(8 * Dpi, 2);
                for (int index = 0; index < Math.Min(_screen.Length, _visible.Length); index++)
                {
                    double dx = e.X - _screen[index].X, dy = e.Y - _screen[index].Y;
                    double squared = dx * dx + dy * dy;
                    if (squared >= best) continue;
                    nearest = index;
                    best = squared;
                }
                if (nearest >= 0) PointSelected?.Invoke(_visible[nearest]);
            }
            _dragButton = MouseButtons.None;
            Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) _dragButton = MouseButtons.None;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            RectangleF area = PlotArea;
            if (!area.Contains(e.Location)) return;
            double next = Math.Max(0.08, Math.Min(100, _zoom * Math.Pow(1.22, e.Delta / 120.0)));
            double relative = next / _zoom;
            float x = e.X - (area.Left + area.Width / 2), y = e.Y - (area.Top + area.Height / 2);
            _pan = new PointF((float)(x - (x - _pan.X) * relative), (float)(y - (y - _pan.Y) * relative));
            _zoom = next;
            Invalidate();
        }
    }
}
