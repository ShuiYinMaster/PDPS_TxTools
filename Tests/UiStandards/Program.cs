using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TxTools.Common;

internal static class Program
{
    private static int _assertions;
    private static void Check(bool result, string message)
    { _assertions++; if (!result) throw new InvalidOperationException(message); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles();
            VerifyButtonsAndScrolling();
            VerifyLogs();
            VerifyReachabilityLayout();
            if (args.Length >= 2) VerifyIcons(args[0], args[1]);
            if (args.Length >= 3 && args[2] == "--hidden")
            {
                var gate = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("TxTools.Common.PluginLog").GetProperty("Enabled");
                gate.SetValue(null, true);
                Check(!(bool)gate.GetValue(null), "Production release DLL must keep the log gate disabled");
            }
            Console.WriteLine("PASS: " + _assertions + " UI and icon assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private static Form HiddenForm()
    {
        return new Form { AutoScaleMode = AutoScaleMode.None, Font = SystemFonts.MessageBoxFont,
            ShowInTaskbar = false, Opacity = 0, StartPosition = FormStartPosition.Manual,
            Location = new Point(-20000, -20000), ClientSize = new Size(640, 480) };
    }

    private static void VerifyButtonsAndScrolling()
    {
        foreach (float scale in new[] { 1f, 1.75f, 2f })
        using (var form = HiddenForm())
        {
            var parameters = new Panel { Dock = DockStyle.Top, Height = 120, AutoScroll = true };
            var shortButton = new Button { Text = "开始检查", Size = new Size(140, 40) };
            var longButton = new Button { Text = "一键复制到目标环境并重建", Top = 36 };
            parameters.Controls.AddRange(new Control[] { shortButton, longButton });
            var grid = new DataGridView { Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both };
            var listArea = new Panel { AutoScroll = true, Dock = DockStyle.Bottom, Height = 80 };
            var pickerList = new Tecnomatix.Engineering.Ui.TxObjGridCtrl { MinimumSize = new Size(320, 180), AutoSize = true };
            listArea.Controls.Add(pickerList);
            PluginUiPolicy.Prepare(pickerList);
            Check(pickerList.MinimumSize.IsEmpty && !pickerList.AutoSize, "Native picker lists must fit their allocated viewport");
            PluginUiPolicy.AllowListScrolling(listArea);
            form.Controls.AddRange(new Control[] { grid, parameters, listArea });
            PluginUiPolicy.Prepare(form);
            Check(shortButton.Width == 86 && shortButton.Height == 26, "Reference button must be 86x26");
            Check(longButton.Width > shortButton.Width && longButton.Height == 26, "Long text must grow width only");
            if (scale > 1) form.Scale(new SizeF(scale, scale));
            Check(Math.Abs(shortButton.Height - 26 * scale) <= 1, "Button height must scale with DPI exactly once");
            PluginUiPolicy.Finish(form);
            Check(form.FormBorderStyle == FormBorderStyle.FixedDialog && !form.MaximizeBox, "Window must not be user resizable");
            Check(!parameters.AutoScroll && parameters.AutoScrollMinSize.IsEmpty, "Parameter scrollbar must be disabled");
            Check(grid.ScrollBars == ScrollBars.Both && listArea.AutoScroll, "List scrolling must remain available");
            form.Show();
            shortButton.Text = "在目标环境执行复制与重建";
            Check(shortButton.Width > 86, "Changing button text must remeasure width");
            form.Close();
        }
    }

    private static void VerifyLogs()
    {
        using (var form = HiddenForm())
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            var grid = new DataGridView { Dock = DockStyle.Fill };
            var log = new RichTextBox { Dock = DockStyle.Fill };
            var toggle = new Button { Text = "日志" };
            root.Controls.Add(grid, 0, 0); root.Controls.Add(log, 0, 1); form.Controls.Add(root);
            form.Controls.Add(toggle);
            PluginLog.Register(log, true, toggle);
            form.Show();
#if TXTOOLS_HIDE_LOGS
            PluginLog.Enabled = true;
            PluginLog.Apply(form);
            Check(!PluginLog.Enabled && !log.Visible && !toggle.Visible, "Release gate must not be overridden");
            Check(root.RowStyles[1].Height == 0, "Release gate must reclaim log row space");
#else
            PluginLog.Enabled = true;
            PluginLog.Apply(form);
            root.RowStyles[1].Height = 140; // local expansion after registration
            PluginLog.Enabled = false;
            Check(!log.Visible && !toggle.Visible, "One switch must hide log and its toggle");
            Check(root.RowStyles[1].SizeType == SizeType.Absolute && root.RowStyles[1].Height == 0, "Hidden logs must leave no table gap");
            PluginLog.SetVisible(log, true);
            Check(!log.Visible, "Automatic expansion must respect the global gate");
            PluginLog.Enabled = true;
            Check(log.Visible && root.RowStyles[1].Height == 140, "Restoring logs must preserve the expanded row height");
            PluginLog.SetVisible(log, false);
            PluginLog.Enabled = false; PluginLog.Enabled = true;
            Check(!log.Visible, "A locally collapsed log must stay collapsed");
#endif
            form.Close();
        }
    }

    private static void VerifyIcons(string assemblyPath, string catalogPath)
    {
        var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
        var resources = assembly.GetManifestResourceNames();
        var catalog = new JavaScriptSerializer().Deserialize<List<Dictionary<string, string>>>(File.ReadAllText(catalogPath));
        foreach (var command in catalog)
        foreach (string size in new[] { "small", "large" })
        {
            string suffix = command[size];
            string resource = resources.SingleOrDefault(n => n == suffix || n.EndsWith("." + suffix, StringComparison.Ordinal));
            Check(resource != null, "Missing embedded icon: " + command["command"] + " " + suffix);
            using (var stream = assembly.GetManifestResourceStream(resource))
            using (var bitmap = new Bitmap(stream))
            {
                int pixels = size == "small" ? 16 : 32;
                Check(bitmap.Size == new Size(pixels, pixels), "Wrong icon dimensions: " + suffix);
                Check(Image.GetPixelFormatSize(bitmap.PixelFormat) == (size == "small" ? 24 : 32), "Wrong pixel format: " + suffix);
            }
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void VerifyReachabilityLayout()
    {
        foreach (float scale in new[] { 1f, 1.75f, 2f })
        using (var form = new TxTools.RobotReachabilityChecker.Ui.ReachabilityCheckerForm())
        {
            form.ShowInTaskbar = false; form.Opacity = 0;
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
            PluginUiPolicy.Prepare(form);
            var fonts = Descendants(form).Select(c => new { Control = c, Font = c.Font }).ToArray();
            if (scale > 1)
            {
                form.Scale(new SizeF(scale, scale));
                foreach (var item in fonts) item.Control.Font = new Font(item.Font.FontFamily, item.Font.Size * scale, item.Font.Style);
                form.ClientSize = new Size((int)(900 * scale), (int)(520 * scale));
            }
            PluginUiPolicy.Finish(form);
            form.Show(); Application.DoEvents(); form.PerformLayout();
            var cards = Descendants(form).OfType<GroupBox>().Where(c => c.Text != "运行日志").ToArray();
            Check(cards.Length == 5, "Reachability must retain five functional groups");
            Check(cards.Select(c => c.PointToScreen(Point.Empty).Y).Distinct().Count() == 1, "Parameter groups must remain in one row");
            foreach (var card in cards)
                foreach (var control in Descendants(card).Where(c => c.Visible && (c is Button || c is ComboBox || c is NumericUpDown || c is TextBox)))
                {
                    Check(control.Right <= control.Parent.ClientSize.Width + 1, "Clipped parameter control at " + scale + ": " + card.Text + "/" + control.Text);
                    Check(control.Bottom <= control.Parent.ClientSize.Height + 1, "Clipped parameter row at " + scale + ": " + card.Text + "/" + control.Text);
                }
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview");
            Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(directory, "reachability-" + (int)(100 * scale) + ".png"));
                int bottom = cards.Max(c => c.PointToScreen(new Point(0, c.Height)).Y) - form.Top + 6;
                using (var band = bitmap.Clone(new Rectangle(0, 0, bitmap.Width, Math.Min(bitmap.Height, bottom)), bitmap.PixelFormat))
                    band.Save(Path.Combine(directory, "reachability-band-" + (int)(100 * scale) + ".png"));
            }
            form.Close();
        }
    }
}
