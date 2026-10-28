using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace TxTools.Common
{
    /// <summary>96-DPI design dimensions, scaled once by FormUiKit.</summary>
    public static class PluginUiPolicy
    {
        public const int ButtonWidth = 86;
        public const int ButtonHeight = 26;
        private static readonly ConditionalWeakTable<Control, object> ListAreas = new ConditionalWeakTable<Control, object>();

        public static void AllowListScrolling(Control region) { ListAreas.GetValue(region, c => new object()); }

        public static int MeasureButtonWidth(string text, Font font)
        {
            using (var bitmap = new Bitmap(1, 1))
            {
                bitmap.SetResolution(96, 96);
                using (var graphics = Graphics.FromImage(bitmap))
                    return Math.Max(ButtonWidth, (int)Math.Ceiling(
                        graphics.MeasureString((text ?? "").Replace("&", ""), font).Width) + 18);
            }
        }

        public static string PrepareWebDocument(string html)
        {
            if (html == null) return null;
            var assembly = typeof(PluginUiPolicy).Assembly;
            string name = assembly.GetManifestResourceNames().SingleOrDefault(
                n => n.EndsWith(".plugin-ui.css", StringComparison.Ordinal));
            if (name == null) return html;
            using (var stream = assembly.GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                return html.Replace("</head>", "<style id=\"tx-plugin-ui\">" + reader.ReadToEnd() + "</style></head>");
        }

        public static void Attach(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.Load += (s, e) => { Prepare(form, GetScale(form)); Finish(form); };
        }

        private static float GetScale(Control control)
        {
            try { using (var graphics = control.CreateGraphics()) return Math.Max(1f, graphics.DpiX / 96f); }
            catch { return 1f; }
        }

        public static void Prepare(Control root, float scale = 1f)
        {
            if (root.GetType().Name == "TxObjGridCtrl")
            {
                // PS lists can otherwise force their host panel beyond the allocated list row.
                root.MinimumSize = Size.Empty;
                root.AutoSize = false;
                return;
            }
            if (IsListOrNativeControl(root)) return;
            var button = root as Button;
            if (button != null)
            {
                button.AutoSize = false;
                button.Padding = new Padding(4, 0, 4, 0);
                button.MinimumSize = button.MaximumSize = Size.Empty;
                button.Size = new Size((int)Math.Round(MeasureButtonWidth(button.Text, button.Font) * scale),
                    (int)Math.Round(ButtonHeight * scale));
                if (button.Dock != DockStyle.None) button.Dock = DockStyle.None;
                button.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                button.TextChanged += (s, e) =>
                {
                    float dpi = GetScale(button);
                    button.Size = new Size((int)Math.Round(MeasureButtonWidth(button.Text, button.Font) * dpi),
                        (int)Math.Round(ButtonHeight * dpi));
                };
            }
            var strip = root as ToolStrip;
            if (strip != null)
                foreach (ToolStripItem item in strip.Items)
                    if (item is ToolStripButton || item is ToolStripDropDownButton)
                    {
                        item.AutoSize = false;
                        item.Size = new Size((int)Math.Round(MeasureButtonWidth(item.Text, item.Font) * scale),
                            (int)Math.Round(ButtonHeight * scale));
                    }
            var flow = root as FlowLayoutPanel;
            if (flow != null && flow.FlowDirection == FlowDirection.LeftToRight && flow.Parent != null
                && !(flow.Parent is FlowLayoutPanel parentFlow && parentFlow.FlowDirection == FlowDirection.LeftToRight))
                FitFlowRow(flow, flow.Parent);
            foreach (Control child in root.Controls.Cast<Control>().ToArray()) Prepare(child, scale);
        }

        public static void Finish(Form form)
        {
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            RemoveNonListScrollbars(form);
            PluginLog.Apply(form);
        }

        private static bool IsListOrNativeControl(Control c)
        {
            object marker;
            if (ListAreas.TryGetValue(c, out marker) || c is DataGridView || c is ListView || c is TreeView || c is ListBox) return true;
            if (c is Form || c is ToolStrip) return false;
            string name = c.GetType().FullName ?? "";
            return name.StartsWith("Tecnomatix.", StringComparison.Ordinal)
                || name.StartsWith("C1.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Web.WebView2.", StringComparison.Ordinal);
        }

        private static void RemoveNonListScrollbars(Control root)
        {
            if (PluginLog.IsLogRegion(root)) { PluginLog.RestoreScrolling(root); return; }
            if (IsListOrNativeControl(root)) return;
            var status = root as StatusStrip;
            if (status != null) status.SizingGrip = false;
            foreach (Control child in root.Controls.Cast<Control>().ToArray()) RemoveNonListScrollbars(child);
            var text = root as TextBox;
            if (text != null && text.Multiline) { text.ScrollBars = ScrollBars.None; text.WordWrap = true; }
            var rich = root as RichTextBox;
            if (rich != null) { rich.ScrollBars = RichTextBoxScrollBars.None; rich.WordWrap = true; }
            var scroll = root as ScrollableControl;
            if (scroll != null && !(root is ToolStrip))
            { scroll.AutoScroll = false; scroll.AutoScrollMinSize = Size.Empty; }
        }

        /// <summary>Allow a parameter/action row to wrap at the actual available width.</summary>
        public static void FitFlowRow(FlowLayoutPanel flow, Control parent)
        {
            var cell = parent as TableLayoutPanel;
            if (cell != null)
            {
                int column = cell.GetColumn(flow);
                if (column >= 0 && column < cell.ColumnStyles.Count
                    && cell.ColumnStyles[column].SizeType == SizeType.AutoSize)
                    return; // Let this column measure the complete action group first.
            }
            flow.WrapContents = true;
            int designHeight = flow.Height;
            bool fitting = false;
            EventHandler fit = (s, e) =>
            {
                if (fitting) return;
                fitting = true;
                try
                {
                    int width = parent.ClientSize.Width - parent.Padding.Horizontal - flow.Margin.Horizontal
                        - (flow.Dock == DockStyle.None && !(parent is FlowLayoutPanel) ? flow.Left : 0);
                    var table = parent as TableLayoutPanel;
                    if (table != null)
                    {
                        int column = table.GetColumn(flow);
                        int span = table.GetColumnSpan(flow);
                        var widths = table.GetColumnWidths();
                        if (column >= 0 && column < widths.Length)
                            width = widths.Skip(column).Take(span).Sum() - flow.Margin.Horizontal;
                    }
                    if (width > 0 && flow.MaximumSize.Width != width)
                    {
                        flow.MaximumSize = new Size(width, 0);
                        // AutoSize action groups keep their natural width and right alignment.
                        // Docked rows and vertical stacks already supply the width they need.
                        if (!flow.AutoSize) flow.Width = width;
                    }
                    if (!flow.AutoSize && width > 0)
                        flow.Height = Math.Max(designHeight, flow.GetPreferredSize(new Size(width, 0)).Height);
                }
                finally { fitting = false; }
            };
            parent.ClientSizeChanged += fit;
            parent.Layout += (s, e) => fit(s, e);
            fit(parent, EventArgs.Empty);
        }
    }
}
