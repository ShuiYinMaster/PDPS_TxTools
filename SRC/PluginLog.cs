using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace TxTools.Common
{
    /// <summary>One gate for every plugin log view, including automatic error expansion.</summary>
    public static class PluginLog
    {
#if TXTOOLS_HIDE_LOGS
        public const bool Available = false;
        public static bool Enabled { get { return false; } set { } }
#else
        public const bool Available = true;
        private static bool _enabled = !string.Equals(Environment.GetEnvironmentVariable("TXTOOLS_SHOW_LOGS"),
            "false", StringComparison.OrdinalIgnoreCase);
        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                foreach (var entry in Entries.ToArray()) entry.Refresh();
            }
        }
#endif
        private static readonly List<Entry> Entries = new List<Entry>();

        internal static bool IsLogRegion(Control control) => Entries.Any(e => e.Region == control);

        internal static void RestoreScrolling(Control region)
        {
            if (region is TextBox text && text.Multiline) text.ScrollBars = ScrollBars.Both;
            if (region is RichTextBox rich) rich.ScrollBars = RichTextBoxScrollBars.Both;
            foreach (Control child in region.Controls) RestoreScrolling(child);
        }

        public static void Register(Control region, bool visible = true, params object[] toggles)
        {
            RestoreScrolling(region);
            var entry = new Entry(region, visible, toggles);
            Entries.Add(entry);
            region.Disposed += (s, e) => Entries.Remove(entry);
            region.VisibleChanged += (s, e) => { if (!Enabled && region.Visible) entry.Refresh(); };
            if (!Enabled) entry.HideControls();
        }

        public static void Apply(Form form)
        {
            foreach (var entry in Entries.ToArray())
                if (entry.Region.FindForm() == form) entry.Refresh();
        }

        public static void SetVisible(Control region, bool visible)
        {
            var entry = Entries.FirstOrDefault(x => x.Region == region);
            if (entry == null) { region.Visible = Enabled && visible; return; }
            entry.RequestedVisible = visible;
            entry.Refresh();
        }

        private sealed class Entry
        {
            public readonly Control Region;
            public bool RequestedVisible;
            private readonly object[] _toggles;
            private TableLayoutPanel _table;
            private RowStyle _rowStyle;
            private int _row;
            private bool _updating;
            private bool _collapsed;

            public Entry(Control region, bool visible, object[] toggles)
            { Region = region; RequestedVisible = visible; _toggles = toggles; }

            public void HideControls()
            {
                Region.Visible = false;
                foreach (var toggle in _toggles)
                {
                    var control = toggle as Control;
                    if (control != null) control.Visible = false;
                    var item = toggle as ToolStripItem;
                    if (item != null) item.Visible = false;
                }
            }

            public void Refresh()
            {
                if (Region.IsDisposed || _updating) return;
                if (Region.InvokeRequired) { Region.BeginInvoke((Action)Refresh); return; }
                _updating = true;
                try
                {
                    bool show = Enabled && RequestedVisible;
                    if (_rowStyle == null && Region.Parent is TableLayoutPanel)
                    {
                        var table = (TableLayoutPanel)Region.Parent;
                        int row = table.GetRow(Region);
                        if (row >= 0 && row < table.RowStyles.Count
                            && table.Controls.Cast<Control>().Count(c => table.GetRow(c) == row) == 1)
                        {
                            _table = table; _row = row;
                            _rowStyle = new RowStyle(table.RowStyles[row].SizeType, table.RowStyles[row].Height);
                        }
                    }
                    Region.Visible = show;
                    if (_table != null)
                    {
                        if (!show && !_collapsed)
                        {
                            _rowStyle = new RowStyle(_table.RowStyles[_row].SizeType, _table.RowStyles[_row].Height);
                            _table.RowStyles[_row].SizeType = SizeType.Absolute;
                            _table.RowStyles[_row].Height = 0;
                        }
                        else if (show && _collapsed)
                        {
                            _table.RowStyles[_row].SizeType = _rowStyle.SizeType;
                            _table.RowStyles[_row].Height = _rowStyle.Height;
                        }
                        _collapsed = !show;
                    }
                    foreach (var toggle in _toggles)
                    {
                        var control = toggle as Control;
                        if (control != null) control.Visible = Enabled;
                        var item = toggle as ToolStripItem;
                        if (item != null) item.Visible = Enabled;
                    }
                }
                finally { _updating = false; }
            }
        }
    }
}
