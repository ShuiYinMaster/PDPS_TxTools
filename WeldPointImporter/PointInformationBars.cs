using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using TxTools.Common;
using Theme = TxTools.Common.FormUiKit.Theme;

namespace TxTools.WeldPointImporter
{
    internal sealed class PointStatisticsBar : TableLayoutPanel
    {
        private readonly Font _valueFont = new Font(FormUiKit.BaseFont, FontStyle.Bold);
        private readonly Label[] _labels = new Label[6];
        private readonly Label[] _values = new Label[6];

        internal PointStatisticsBar()
        {
            SuspendLayout();
            Dock = DockStyle.Fill;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.GridAlt;
            Padding = new Padding(5, 2, 5, 2);
            Margin = new Padding(3, 0, 3, 5);
            string[] captions = { "总点位", "重复", "保留", "双层", "三层", "四层及以上" };
            RowCount = 1;
            ColumnCount = captions.Length;
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            for (int index = 0; index < captions.Length; index++)
            {
                ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                Controls.Add(BuildMetric(index, captions[index]), index, 0);
            }
            ResumeLayout(false);
        }

        private Control BuildMetric(int index, string caption)
        {
            var metric = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty,
                Padding = new Padding(5, 4, 5, 4), RowCount = 1, ColumnCount = 3, BackColor = Color.Transparent
            };
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            metric.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            if (index >= 3)
            {
                Color color = WeldLayerColors.Get((WeldLayerGroup)(index - 3));
                var marker = new Control { Size = new Size(7, 7), Margin = new Padding(0, 0, 4, 0), Anchor = AnchorStyles.Left };
                marker.Paint += (sender, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var brush = new SolidBrush(color)) e.Graphics.FillEllipse(brush, marker.ClientRectangle);
                };
                metric.Controls.Add(marker, 0, 0);
            }

            var label = new Label
            {
                Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty, ForeColor = Theme.TextDim
            };
            var value = new Label
            {
                Text = "0", Anchor = AnchorStyles.Right, AutoSize = true, Font = _valueFont,
                Margin = new Padding(4, 0, 0, 0), ForeColor = index == 1 ? Theme.StatusWarn : Theme.HeaderFore
            };
            _labels[index] = label;
            _values[index] = value;
            metric.Controls.Add(label, 1, 0);
            metric.Controls.Add(value, 2, 0);
            return metric;
        }

        internal void UpdateCounts(IReadOnlyList<ImportPoint> points, int duplicates, int visible)
        {
            var layers = new int[3];
            foreach (var point in points) layers[(int)WeldLayers.Group(point.LayerCount)]++;
            bool filtered = visible != points.Count;
            _labels[0].Text = filtered ? "显示" : "总点位";
            SetValue(0, filtered ? Number(visible) + " / " + Number(points.Count) : Number(points.Count));
            SetValue(1, Number(duplicates));
            SetValue(2, Number(points.Count - duplicates));
            for (int index = 0; index < layers.Length; index++) SetValue(index + 3, Number(layers[index]));
        }

        private void SetValue(int index, string value)
        {
            _values[index].Text = value;
            _values[index].AccessibleName = _labels[index].Text + " " + value;
        }

        private static string Number(int count) => count.ToString("N0", CultureInfo.CurrentCulture);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var border = new Pen(Theme.GridHeader))
            {
                e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                var columns = GetColumnWidths();
                if (columns.Length >= 3)
                {
                    int x = Padding.Left + columns[0] + columns[1] + columns[2];
                    e.Graphics.DrawLine(border, x, Padding.Top + 3, x, Height - Padding.Bottom - 4);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _valueFont.Dispose();
        }
    }

    internal enum PointStatus { Ready, Reading, Success, Notice, Error }

    internal sealed class PointStatusBar : TableLayoutPanel
    {
        private readonly Font _badgeFont = new Font(FormUiKit.BaseFont, FontStyle.Bold);
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Label _badge = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 9, 0), Padding = new Padding(7, 3, 7, 3) };
        private readonly Label _message = new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty, ForeColor = Theme.TextDim
        };

        internal PointStatusBar()
        {
            Dock = DockStyle.Fill;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.GridAlt;
            Padding = new Padding(6, 4, 8, 4);
            Margin = new Padding(3, 3, 3, 0);
            ColumnCount = 2;
            RowCount = 1;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _badge.Font = _badgeFont;
            Controls.Add(_badge, 0, 0);
            Controls.Add(_message, 1, 0);
        }

        internal void UpdateStatus(PointStatus status, string text)
        {
            switch (status)
            {
                case PointStatus.Reading: SetBadge("读取中", Theme.GridHeader, Theme.HeaderFore); break;
                case PointStatus.Success: SetBadge("完成", Theme.RowOk, Theme.StatusOkFg); break;
                case PointStatus.Notice: SetBadge("提示", Theme.RowWarn, Theme.TextDim); break;
                case PointStatus.Error: SetBadge("失败", Theme.RowFail, Theme.StatusErr); break;
                default: SetBadge("就绪", Theme.GridHeader, Theme.TextDim); break;
            }
            _message.Text = text.Replace("\r", "").Replace("\n", "  ");
            _message.AccessibleName = text;
            _toolTip.SetToolTip(_message, text);
        }

        private void SetBadge(string text, Color background, Color foreground)
        {
            _badge.Text = text;
            _badge.BackColor = background;
            _badge.ForeColor = foreground;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            _toolTip.Dispose();
            _badgeFont.Dispose();
        }
    }
}
