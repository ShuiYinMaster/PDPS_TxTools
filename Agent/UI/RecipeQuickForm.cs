using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;

namespace TxTools.Agent.UI
{
    /// <summary>A lightweight recipe surface; opening it does not create an Agent conversation.</summary>
    internal sealed class RecipeQuickForm : Form
    {
        private readonly WebView2 _web = new WebView2 { Dock = DockStyle.Fill };
        private readonly Label _loading = new Label { Dock = DockStyle.Fill, Text = "正在加载快捷配方…", TextAlign = ContentAlignment.MiddleCenter };
        private readonly Timer _studyTimer = new Timer { Interval = 800 };
        private readonly Action _manage;
        private JObject _anchor;
        private string _study;
        private bool _ready;
        private bool _loadingHtml;

        protected override bool ShowWithoutActivation => true;

        public RecipeQuickForm(Action manage)
        {
            _manage = manage;
            Text = "鲸鱼娘 · 快捷配方";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(340, 510);
            Controls.Add(_web);
            Controls.Add(_loading);
            Shown += (s, e) => { PositionNearPet(); InitializeWeb(); };
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
            RecipeStore.RecipesChanged += NotifyChanged;
            RecipeUiActions.Changed += NotifyChanged;
            Application.ApplicationExit += OnExit;
            _studyTimer.Tick += (s, e) =>
            {
                if (!Visible) return;
                string study = RecipeUiActions.CurrentStudyKey();
                if (!string.Equals(study, _study, StringComparison.Ordinal))
                {
                    _study = study;
                    Send(0, new JObject { ["type"] = "recipe.studyChanged", ["study"] = study });
                }
            };
            _studyTimer.Start();
        }

        public void Toggle(JObject anchor, IWin32Window owner)
        {
            if (Visible) { Hide(); return; }
            _anchor = anchor;
            PositionNearPet();
            if (owner != null) Show(owner); else Show();
            PositionNearPet();
            if (_ready) Send(0, new JObject { ["type"] = "quick.refresh" });
        }

        private void PositionNearPet()
        {
            var screens = Screen.AllScreens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();
            int index = (int?)_anchor?["screenIndex"] ?? Array.FindIndex(screens, s => s.Primary);
            var screen = screens[Math.Max(0, Math.Min(index, screens.Length - 1))];
            var area = screen.WorkingArea;
            double rx = (double?)_anchor?["rx"] ?? 0.9;
            double ry = (double?)_anchor?["ry"] ?? 0.85;
            double rw = (double?)_anchor?["rw"] ?? 0.08;
            double rh = (double?)_anchor?["rh"] ?? 0.1;
            if (!new[] { rx, ry, rw, rh }.All(v => !double.IsNaN(v) && !double.IsInfinity(v))) return;
            var pet = new Rectangle(area.Left + (int)(rx * area.Width), area.Top + (int)(ry * area.Height),
                Math.Max(1, (int)(rw * area.Width)), Math.Max(1, (int)(rh * area.Height)));
            var target = RecipeQuickPlacement.Place(area, pet, Size);
            Bounds = target;
        }

        private async void InitializeWeb()
        {
            try
            {
                string cache = TxToolsTemp.DirectoryFor("Agent", "RecipeQuickWebView", System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
                var env = await CoreWebView2Environment.CreateAsync(null, cache);
                if (IsDisposed) return;
                await _web.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;
                _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _web.CoreWebView2.NewWindowRequested += (s, e) => e.Handled = true;
                _web.CoreWebView2.WebMessageReceived += (s, e) =>
                {
                    JObject msg;
                    try { msg = JObject.Parse(e.WebMessageAsJson); }
                    catch { return; }
                    HandleMessage(msg);
                };
                _web.CoreWebView2.NavigationStarting += (s, e) =>
                {
                    // SDK versions represent NavigateToString as about:blank or a data URL.
                    // Permit the one embedded-document navigation, then block other pages.
                    if (e.Uri == "about:blank") return;
                    if (_loadingHtml && e.Uri.StartsWith("data:text/html;", StringComparison.Ordinal))
                    { _loadingHtml = false; return; }
                    e.Cancel = true;
                };
                var assembly = typeof(RecipeQuickForm).Assembly;
                var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("recipe-quick.html", StringComparison.Ordinal));
                using (var stream = assembly.GetManifestResourceStream(resource))
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                {
                    _loadingHtml = true;
                    _web.CoreWebView2.NavigateToString(reader.ReadToEnd());
                }
            }
            catch (Exception ex) { _loading.Text = "快捷配方界面加载失败：\n" + ex.Message; }
        }

        private void HandleMessage(JObject msg)
        {
            int seq = (int?)msg["seq"] ?? 0;
            try
            {
                switch ((string)msg["type"])
                {
                    case "quick.ready":
                        _loadingHtml = false;
                        _ready = true;
                        _loading.Visible = false;
                        _web.BringToFront();
                        SendList(seq);
                        break;
                    case "recipe.list": SendList(seq); break;
                    case "recipe.objectTypes": Send(seq, Visible ? RecipeUiActions.ObjectTypes(msg) : RecipeUiActions.Error("recipe.objectTypes.result", "请先打开快捷配方。")); break;
                    case "recipe.pickSelection": Send(seq, RecipeUiActions.PickSelection(msg)); break;
                    case "recipe.favorite":
                        string id = (string)msg["recipeId"];
                        if (RecipeStore.Get(id) == null) throw new InvalidOperationException("配方不存在。");
                        UserPrefsStore.UpdateRecipeFavorite(id, (bool?)msg["favorite"] == true);
                        SendList(seq);
                        break;
                    case "recipe.run": RecipeUiActions.Run(seq, msg, Send); break;
                    case "recipe.detail":
                        var recipe = RecipeStore.Get((string)msg["recipeId"]);
                        if (recipe == null) throw new InvalidOperationException("配方不存在。");
                        Send(seq, new JObject { ["type"] = "recipe.detail.result", ["ok"] = true, ["code"] = recipe.Code });
                        break;
                    case "quick.openLink":
                        Uri link;
                        if (!Uri.TryCreate((string)msg["url"], UriKind.Absolute, out link)
                            || (link.Scheme != Uri.UriSchemeHttps && link.Scheme != Uri.UriSchemeHttp))
                            throw new InvalidOperationException("仅支持打开 HTTP / HTTPS 链接。");
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
                        break;
                    case "quick.manage": Hide(); _manage(); break;
                    case "quick.close": Hide(); break;
                }
            }
            catch (Exception ex) { Send(seq, RecipeUiActions.Error("quick.error", ex.Message)); }
        }

        private void SendList(int seq)
        {
            _study = RecipeUiActions.CurrentStudyKey();
            Send(seq, RecipeUiActions.List());
        }

        private void NotifyChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(NotifyChanged)); } catch (InvalidOperationException) { } return; }
            if (Visible) Send(0, new JObject { ["type"] = "quick.refresh" });
        }

        private void Send(int seq, JObject data)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Send(seq, data))); } catch (InvalidOperationException) { } return; }
            if (!_ready || _web.CoreWebView2 == null) return;
            data["seq"] = seq;
            try { _web.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(data)); } catch { }
        }

        private void OnExit(object sender, EventArgs e) { Dispose(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                RecipeStore.RecipesChanged -= NotifyChanged;
                RecipeUiActions.Changed -= NotifyChanged;
                Application.ApplicationExit -= OnExit;
                _studyTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class RecipeQuickPlacement
    {
        public static Rectangle Place(Rectangle area, Rectangle pet, Size requested)
        {
            int width = Math.Min(requested.Width, Math.Max(1, area.Width - 16));
            int height = Math.Min(requested.Height, Math.Max(1, area.Height - 16));
            int x = pet.Left - width - 12;
            if (x < area.Left + 8) x = pet.Right + 12;
            x = Math.Max(area.Left + 8, Math.Min(x, area.Right - width - 8));
            int y = Math.Max(area.Top + 8, Math.Min(pet.Bottom - height, area.Bottom - height - 8));
            return new Rectangle(x, y, width, height);
        }
    }
}
