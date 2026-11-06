using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using TxTools.Common;

namespace TxTools.ThemeTuner
{
    public partial class ThemeTunerForm
    {
        private Control _themeBody, _themeActions, _aboutBody;
        private readonly CancellationTokenSource _aboutCancellation = new CancellationTokenSource();
        private Label _updateStatus;

        private void BuildAboutNavigation()
        {
            var nav = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 2, 0, 0), WrapContents = false };
            var toggle = FormUiKit.MkFuncButton("关于与更新", FormUiKit.Theme.BtnMuted);
            var title = new Label { AutoSize = true, Text = "TxTools · 主题设置", Font = FormUiKit.BoldFont, Margin = new Padding(6, 6, 0, 0) };
            toggle.Click += (s, e) =>
            {
                if (_aboutBody == null)
                {
                    _aboutBody = BuildAboutBody();
                    PluginUiPolicy.Prepare(_aboutBody);
                    using (var graphics = CreateGraphics())
                        _aboutBody.Scale(new SizeF(graphics.DpiX / 96f, graphics.DpiY / 96f));
                    Controls.Add(_aboutBody);
                    _aboutBody.BringToFront();
                    nav.BringToFront();
                }
                bool about = _themeBody.Visible;
                _themeBody.Visible = _themeActions.Visible = !about;
                _aboutBody.Visible = about;
                toggle.Text = about ? "返回主题设置" : "关于与更新";
                title.Text = about ? "TxTools · 关于与更新" : "TxTools · 主题设置";
            };
            nav.Controls.Add(toggle); nav.Controls.Add(title);
            Controls.Add(nav); nav.BringToFront();
            FormClosed += (s, e) => _aboutCancellation.Cancel();
        }

        private Control BuildAboutBody()
        {
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10), Font = FormUiKit.BaseFont };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            body.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "TxTools — Process Simulate 工具集\n" + ReleaseChecker.BuildLabel,
                Font = FormUiKit.BoldFont }, 0, 0);
            var links = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            AddLink(links, "项目主页", ReleaseChecker.ProjectUrl);
            AddLink(links, "发行版与下载", ReleaseChecker.ProjectUrl + "/releases");
            AddLink(links, "问题反馈", ReleaseChecker.ProjectUrl + "/issues");
            AddLink(links, "使用文档", ReleaseChecker.ProjectUrl + "#readme");
            body.Controls.Add(links, 0, 1);
            var updates = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            updates.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); updates.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var check = FormUiKit.MkFuncButton("检查更新", FormUiKit.Theme.BtnPrimary);
            _updateStatus = new Label { Dock = DockStyle.Fill, Text = "仅手动联网检查；不会自动下载或安装更新。", AutoEllipsis = false };
            check.Click += async (s, e) =>
            {
                check.Enabled = false; _updateStatus.Text = "正在查询 GitHub 最新稳定版…";
                try
                {
                    string message = await ReleaseChecker.CheckAsync(_aboutCancellation.Token);
                    if (!IsDisposed) _updateStatus.Text = message;
                }
                catch (OperationCanceledException) { if (!IsDisposed) _updateStatus.Text = "检查超时或已取消，请稍后重试。"; }
                catch (Exception) { if (!IsDisposed) _updateStatus.Text = "检查失败，请检查网络或代理，也可直接打开发行版页面。"; }
                finally { if (!IsDisposed) check.Enabled = true; }
            };
            updates.Controls.Add(check, 0, 0); updates.Controls.Add(_updateStatus, 0, 1); body.Controls.Add(updates, 0, 2);
            body.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "开源鸣谢 · 感谢以下项目及其贡献者（双击条目查看来源）", TextAlign = ContentAlignment.MiddleLeft }, 0, 3);
            var credits = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, MultiSelect = false };
            credits.Columns.Add("Project", "项目"); credits.Columns.Add("Use", "用途"); credits.Columns.Add("License", "许可证");
            foreach (var item in Credits)
            {
                int row = credits.Rows.Add(item[0], item[1], item[2]); credits.Rows[row].Tag = item[3];
            }
            credits.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) OpenProjectLink((string)credits.Rows[e.RowIndex].Tag); };
            credits.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && credits.CurrentRow != null) { e.Handled = true; e.SuppressKeyPress = true; OpenProjectLink((string)credits.CurrentRow.Tag); } };
            body.Controls.Add(credits, 0, 4);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 6, 0, 0) };
            var licenses = FormUiKit.MkFuncButton("许可原文", FormUiKit.Theme.BtnMuted);
            licenses.Click += (s, e) => ShowLicenseTexts();
            var diagnostics = FormUiKit.MkFuncButton("复制诊断信息", FormUiKit.Theme.BtnMuted);
            diagnostics.Click += (s, e) =>
            {
                try { Clipboard.SetText("TxTools: " + ReleaseChecker.BuildLabel + "\n.NET: " + Environment.Version + "\n进程: " + (Environment.Is64BitProcess ? "64" : "32") + " 位\n系统: " + Environment.OSVersion.VersionString + "\n项目: " + ReleaseChecker.ProjectUrl); _updateStatus.Text = "已复制版本与运行环境信息，不包含日志、路径、用户名或密钥。"; }
                catch { _updateStatus.Text = "剪贴板暂不可用，请稍后重试。"; }
            };
            footer.Controls.Add(licenses); footer.Controls.Add(diagnostics); body.Controls.Add(footer, 0, 5);
            return body;
        }

        private void AddLink(Control parent, string text, string url)
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 5, 18, 0), Font = FormUiKit.BaseFont };
            link.LinkClicked += (s, e) => OpenProjectLink(url); parent.Controls.Add(link);
        }

        private void OpenProjectLink(string url)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return;
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { if (_updateStatus != null) _updateStatus.Text = "无法打开浏览器，请访问 " + url; }
        }

        private void ShowLicenseTexts()
        {
            var assembly = typeof(ThemeTunerForm).Assembly;
            string text = string.Join("\r\n\r\n", assembly.GetManifestResourceNames().Where(n => n.StartsWith("TxTools.Licenses.", StringComparison.Ordinal)).OrderBy(n => n)
                .Select(n => { using (var stream = assembly.GetManifestResourceStream(n)) using (var reader = new StreamReader(stream)) return "===== " + n.Substring("TxTools.Licenses.".Length) + " =====\r\n" + reader.ReadToEnd(); }));
            using (var dialog = new Form { Text = "TxTools · 许可证与第三方声明", StartPosition = FormStartPosition.CenterParent, Size = new Size(700, 500), MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog })
            {
                dialog.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Text = text });
                dialog.ShowDialog(this);
            }
        }

        private static readonly string[][] Credits = {
            new[] { "Newtonsoft.Json", "JSON 读写", "MIT", "https://github.com/JamesNK/Newtonsoft.Json" },
            new[] { "Open XML SDK", "Office 文档", "MIT", "https://github.com/dotnet/Open-XML-SDK" },
            new[] { "ONNX Runtime", "本地模型推理", "MIT", "https://github.com/microsoft/onnxruntime" },
            new[] { "JTReader", "JT 几何解码（含本地修改）", "MIT", "https://github.com/svr2kos2/JTReader" },
            new[] { "EarCut", "多边形三角化", "MIT", "https://github.com/MadWorldNL/EarCut" },
            new[] { "dsh-pet", "桌面宠物（含本地修改）", "MIT", "https://github.com/PC2005-cloud/dsh-pet" },
            new[] { "Lobe Icons", "模型品牌图标", "MIT / 品牌权利另属原权利人", "https://github.com/lobehub/lobe-icons" },
            new[] { "SharpCompress", "JT 解码压缩依赖", "MIT", "https://github.com/adamhathcock/sharpcompress" },
            new[] { "Electron / Chromium", "可选桌面宠物运行时", "MIT / 第三方声明见运行时目录", "https://github.com/electron/electron" },
            new[] { ".NET 配套库", "内存、向量及异步支持", "MIT / 随附许可", "https://github.com/dotnet/runtime" },
            new[] { "WebView2", "嵌入式浏览器", "Microsoft 专有许可（非开源）", "https://developer.microsoft.com/microsoft-edge/webview2/" },
            new[] { "Process Simulate / CATIA / C1", "宿主 SDK、COM 与表格控件", "专有许可，不属于本项目 MIT", ReleaseChecker.ProjectUrl + "/blob/main/NOTICE.md" }
        };
    }
}
