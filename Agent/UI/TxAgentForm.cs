// TxTools.Agent / UI / TxAgentForm.cs  (v3 — 全 HTML UI)
//
// 从 1400 行的多控件自绘窗口简化为 ~500 行的"WebView2 壳":
//   • 窗口内只有一个填满的 WebView2,加载 Agent/UI/chat.html
//   • 顶栏、模型选择、按钮、输入区、附件卡片、状态栏、历史抽屉、API Key 输入
//     全部在 chat.html 内
//   • 审批弹窗(普通/代码)仍用原生 —— ApprovalRequest 委托签名是同步 bool,
//     改成异步会牵连 AgentLoop 深层重构;原生弹窗自带消息 pump,不阻塞。
//     ApiKeyDialog / ConversationListDialog 代码保留在项目里,但本 form 不再引用。
//
// WebView2 不可用时:弹 MessageBox 提示装 WebView2 Runtime,关闭窗口。
// 生产环境 Windows 10/11 系统级预装 Edge,基本不会走到这条路径。
//
// 通信协议 v2 见文件末尾 <PROTOCOL> 段的说明。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.Ui;
using TxTools.Agent.Core;
using TxTools.Agent.Harness;   // HarnessAgentLoop
using TxTools.Common;   // FormUiKit

namespace TxTools.Agent.UI
{
    public sealed class TxAgentForm : TxForm
    {
        private static readonly System.Drawing.Size DesignSize = new System.Drawing.Size(560, 720);

        // ── 依赖 ──
        private readonly ToolRegistry _tools;
        private DeepSeekClient _client;
        private IAgentLoop _loop;

        private CancellationTokenSource _cts;

        /// <summary>是否正在处理一轮发送。防止用户连续点发送导致两个循环并发跑、
        /// _cts 被后一个覆盖、前一个 finally 误 Dispose 后一个的令牌。</summary>
        private bool _isSending;
        private Conversation _current;

        // ── LLM Provider / Model 状态 ──
        private string _currentProviderId = LlmProviders.DefaultProviderId;
        private string _currentModel = LlmProviders.All[0].Models[0];

        /// <summary>
        /// 审批模式,session 级(关窗归位): 
        ///   ask       每次弹窗询问(默认,安全)
        ///   auto_safe run_csharp 仍弹代码审阅框,其他变更工具自动通过
        ///   auto_all  全部自动通过(含代码执行,危险)
        /// </summary>
        private string _approvalMode = "ask";

        /// <summary>
        /// 当前挂起的 HTML 审批请求。ApprovalRequest 委托签名同步返回 bool,
        /// 我们通过 TCS 在后台线程 (Task.Run 里的线程池线程) 阻塞等 JS 响应。
        /// UI 线程收到 approvalResult 消息 → TrySetResult → 后台线程解除阻塞。
        /// </summary>
        private TaskCompletionSource<bool> _pendingApproval;
        private readonly object _pendingApprovalLock = new object();

        /// <summary>
        /// 挂起的 ask_user 请求 —— 跟 approval 同款机制。
        /// null 表示无挂起;value 是用户输入(取消返回 null)。
        /// </summary>
        private TaskCompletionSource<string> _pendingAskUser;
        private readonly object _pendingAskUserLock = new object();

        // ── WebView2 ──
        private WebView2 _webView;
        private bool _webViewReady;
        private bool _dpiApplied;

        // ── 加载覆盖 (WebView 初始化期间遮盖空白,防止用户以为界面卡死) ──
        private System.Windows.Forms.Panel _loadingOverlay;
        private System.Windows.Forms.Label _loadingLabel;
        private System.Windows.Forms.Timer _loadingTimer;
        private int _loadingDotCount;

        // 配方侧边栏的 study 轮询:前端靠 recipe.studyChanged 推送作废跨 study 绑定,
        // 宿主此前从未发过这个事件,过期检测形同虚设。轮询是唯一可靠的检测点 ——
        // study 切换发生在 PS 主界面,宿主没有任何回调可挂。
        private System.Windows.Forms.Timer _studyTimer;
        private string _lastStudyKey;
        private bool _studyPollPrimed;

        // 配方执行在飞标志(0=空闲,1=执行中)。见 HandleRecipeRun 里的防重入说明。
        private int _recipeRunInFlight;

        public TxAgentForm(SynchronizationContext psCtx, ToolRegistry tools)
        {
            _tools = tools;

            FormUiKit.InitStandardForm(this,
                "TxTools.Agent \u2014 PDPS AI \u52a9\u624b (DeepSeek)",
                DesignSize, new System.Drawing.Size(420, 480), sizable: true);

            // InitStandardForm 会关掉 FlatStyleEnabled(Siemens AFW 扁平风格)，
            // 导致 AFW 不再擦除窗体背景、客户区边缘残留系统灰色(左/下/右 10px 灰边)。
            // TxAgent 是 WebView2 全 HTML UI,原生配色依赖少,恢复扁平风格消除灰边。
            // 反射设置：旧版 PS 的 TxForm 无此属性时静默跳过。
            FormUiKit.TrySetFlatStyle(this, true);

            // 崩溃兜底 —— PS 进程内出现 unhandled exception 时,先把当前对话强制落盘
            // 应对 AI 长任务跑到一半 PS 突然崩溃的场景;不是所有崩溃都能抓到(native 崩溃抓不到),
            // 但配合 AgentLoop 里"每个工具完成即写盘"的机制,能覆盖 99% 的丢失场景
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try { SaveCurrent(); } catch { }
            };

            // TxForm 默认半模态,会挡住其它窗口;关掉才是真正的非模态
            try { SemiModal = false; } catch { }

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);

            // 加载遮罩 —— 覆盖整个 form 内容区,WebView 就绪之前显示。
            // ZOrder: Panel 后加 BringToFront 确保在 WebView 之上。
            _loadingOverlay = new System.Windows.Forms.Panel
            {
                Dock = DockStyle.Fill,
                BackColor = System.Drawing.Color.White
            };
            _loadingLabel = new System.Windows.Forms.Label
            {
                Text = "\u6b63\u5728\u52a0\u8f7d TxAgent UI",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = TxTools.Common.FormUiKit.TitleFont,
                ForeColor = TxTools.Common.FormUiKit.Theme.TextFaint,
                BackColor = System.Drawing.Color.Transparent
            };
            _loadingOverlay.Controls.Add(_loadingLabel);
            Controls.Add(_loadingOverlay);
            _loadingOverlay.BringToFront();

            _loadingTimer = new System.Windows.Forms.Timer { Interval = 400 };
            _loadingTimer.Tick += (s, e) =>
            {
                _loadingDotCount = (_loadingDotCount + 1) % 4;
                string dots = new string('.', _loadingDotCount);
                _loadingLabel.Text = "\u6b63\u5728\u52a0\u8f7d TxAgent UI " + dots;
            };
        }

        public override void OnInitTxForm()
        {
            base.OnInitTxForm();
            try { SemiModal = false; } catch { }
            // 窗体必在 PS 主线程创建，此刻消息循环在跑 —— 最可靠的主线程上下文来源。
            // 刷新 PsContext 缓存，保证 RPC 后台线程也能封送回主线程（被控端免窗体场景尤其关键）。
            try { TxTools.Agent.Core.PsContext.CaptureFromMainThread(); } catch { }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, DesignSize);
            _loadingTimer.Start();

            // 配方变更 → 推 recipe.changed 刷新侧边栏(聊天里 save/delete 后不用手点刷新)
            TxTools.Agent.Core.RecipeStore.RecipesChanged += OnRecipesChanged;

            // study 轮询:轻量 dynamic 调用,UI 线程即 PS 主线程,2.5s 一次可忽略
            _studyTimer = new System.Windows.Forms.Timer { Interval = 2500 };
            _studyTimer.Tick += (s, ev) => PollStudyChange();
            _studyTimer.Start();

            InitWebViewAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 若正等审批,视为拒绝解除阻塞,让后台线程能退出
            ReleasePendingApproval(false);
            FireExtractLessons();                // 关窗前对当前对话跑一次经验萃取
            try { UploadStore.ClearAll(); } catch { }
            if (_studyTimer != null) { _studyTimer.Stop(); _studyTimer.Dispose(); _studyTimer = null; }
            try { TxTools.Agent.Core.RecipeStore.RecipesChanged -= OnRecipesChanged; } catch { }
            CleanupWebViewProfile();
            base.OnFormClosed(e);
        }

        // ─────────────────────────────────────────────────────
        //  WebView2 初始化 + HTML 加载
        // ─────────────────────────────────────────────────────

        /// <summary>
        /// 建一个进程独占的 WebView2 环境。目录带 PID，多个 PDPS 互不干扰。
        /// 失败时返回 null —— 调用方退回默认行为，单进程场景仍然可用。
        /// </summary>
        private static async System.Threading.Tasks.Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>
            CreateWebViewEnvironmentAsync()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "TxAgent.WebView",
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString());

                System.IO.Directory.CreateDirectory(dir);

                return await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                    .CreateAsync(null, dir, null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[TxAgent] 创建独立 WebView2 环境失败,回退默认: " + ex.Message);
                return null;
            }
        }

        /// <summary>关窗时清掉本进程的 WebView2 缓存目录，避免 %TEMP% 越堆越多。</summary>
        private static void CleanupWebViewProfile()
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "TxAgent.WebView",
                    System.Diagnostics.Process.GetCurrentProcess().Id.ToString());

                // 进程还没退出，WebView2 可能仍占着文件，删不掉就算了 ——
                // 下次同 PID 复用时会被覆盖，不会无限增长
                if (System.IO.Directory.Exists(dir))
                    System.IO.Directory.Delete(dir, true);
            }
            catch { }
        }

        private async void InitWebViewAsync()
        {
            try
            {
                // 【必须给每个进程独立的用户数据目录】
                // 传 null 时 WebView2 按 exe 名推导默认目录,两个 PDPS 是同一个 exe,
                // 于是抢同一个目录 —— 而 WebView2 对它是【独占锁】,
                // 第二个进程会一直卡在 EnsureCoreWebView2Async 不返回,
                // 界面就停在"正在加载 TxAgent UI …"。
                var env = await CreateWebViewEnvironmentAsync();
                await _webView.EnsureCoreWebView2Async(env);
                try { _webView.CoreWebView2.Settings.AreDevToolsEnabled = true; } catch { }
                try { _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true; } catch { }
                try { _webView.CoreWebView2.Settings.IsWebMessageEnabled = true; } catch { }

                _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                // chat.html 作为嵌入资源打包进 dll(Build Action = EmbeddedResource),
                // 不再依赖 bin\Agent\UI\chat.html 的物理复制。
                string html = ReadEmbeddedChatHtml();
                if (string.IsNullOrEmpty(html))
                {
                    ShowFatal(
                        "找不到嵌入资源 chat.html。请检查:" + Environment.NewLine +
                        "  1) 项目内 Agent\\UI\\chat.html 的\"生成操作(Build Action)\"= \"嵌入的资源(EmbeddedResource)\"" + Environment.NewLine +
                        "  2) 保存后已 Rebuild。" + Environment.NewLine +
                        "  3) 排查:所有嵌入资源名清单会打印到调试输出窗口。");
                    DumpResourceNames();
                    return;
                }

                // HTML 完整性自检 + Debug 输出(用户 F12 前先能在 VS 输出窗口看到基本诊断)
                var trimmed = html.TrimStart();
                System.Diagnostics.Debug.WriteLine("[TxAgent] chat.html loaded, length=" + html.Length
                    + ", startsWith=" + (trimmed.Length >= 15 ? trimmed.Substring(0, 15) : trimmed)
                    + ", endsWith=" + (html.Length >= 15 ? html.Substring(html.Length - 15) : html));
                if (!trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                    && !trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine("[TxAgent] WARN: chat.html 开头异常,可能被截断或编码错误");
                }

                // 用 WebResourceRequested 拦截 https://chathtml/* 请求。
                // 好处: 页面 URL 保持真实文件名(而非 about:blank),
                //       F12→源代码 能定位到 chat.html 真实行号,便于排错。
                var htmlBytes = System.Text.Encoding.UTF8.GetBytes(html);
                _webView.CoreWebView2.AddWebResourceRequestedFilter(
                    "https://chathtml/*", CoreWebView2WebResourceContext.All);
                _webView.CoreWebView2.WebResourceRequested += (s, ev) =>
                {
                    try
                    {
                        var uri = ev.Request.Uri ?? "";
                        if (uri.EndsWith("/chat.html", StringComparison.OrdinalIgnoreCase)
                            || uri.EndsWith("chathtml/", StringComparison.OrdinalIgnoreCase))
                        {
                            var stream = new MemoryStream(htmlBytes);
                            ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                stream, 200, "OK", "Content-Type: text/html; charset=utf-8");
                        }
                        else if (uri.EndsWith("/recipe-sidebar.css", StringComparison.OrdinalIgnoreCase))
                        {
                            var css = ReadEmbeddedWebResource("recipe-sidebar.css");
                            if (css != null)
                                ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(css)),
                                    200, "OK", "Content-Type: text/css; charset=utf-8");
                            else
                                ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    null, 404, "Not Found", "");
                        }
                        else if (uri.EndsWith("/recipe-sidebar.js", StringComparison.OrdinalIgnoreCase))
                        {
                            var js = ReadEmbeddedWebResource("recipe-sidebar.js");
                            if (js != null)
                                ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(js)),
                                    200, "OK", "Content-Type: application/javascript; charset=utf-8");
                            else
                                ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                    null, 404, "Not Found", "");
                        }
                        else
                        {
                            // favicon.ico 等资源 —— 直接 204,消掉 Console 里的 404 噪音
                            ev.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                                null, 204, "No Content", "");
                        }
                    }
                    catch (Exception rex)
                    {
                        System.Diagnostics.Debug.WriteLine("[TxAgent] WebResourceRequested err: " + rex.Message);
                    }
                };
                _webView.CoreWebView2.Navigate("https://chathtml/chat.html");
            }
            catch (Exception ex)
            {
                ShowFatal(
                    "WebView2 初始化失败: " + ex.Message + Environment.NewLine + Environment.NewLine +
                    "本插件的 UI 需要 WebView2 Runtime。请到:" + Environment.NewLine +
                    "https://developer.microsoft.com/microsoft-edge/webview2/" + Environment.NewLine +
                    "下载 Evergreen Runtime 后重试。");
            }
        }

        /// <summary>
        /// 从当前程序集读取嵌入的 chat.html 文本(UTF-8,自动跳 BOM)。
        /// 用 EndsWith("chat.html") 模糊匹配资源名,避免根命名空间/子目录变化时改代码。
        /// </summary>
        private static string ReadEmbeddedChatHtml()
        {
            return ReadEmbeddedWebResource("chat.html");
        }

        /// <summary>按文件名尾部匹配读嵌入资源。sidecar 资源(recipe-sidebar.css/js)共用。</summary>
        private static string ReadEmbeddedWebResource(string fileName)
        {
            var asm = typeof(TxAgentForm).Assembly;
            string resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(resName)) return null;

            using (var s = asm.GetManifestResourceStream(resName))
            {
                if (s == null) return null;
                // detectEncodingFromByteOrderMarks=true 会自动识别并跳过 UTF-8 BOM
                using (var r = new System.IO.StreamReader(s, System.Text.Encoding.UTF8, true))
                    return r.ReadToEnd();
            }
        }

        /// <summary>找不到资源时把全部资源名打到 VS 输出窗口,便于快速定位命名空间/大小写问题。</summary>
        private static void DumpResourceNames()
        {
            try
            {
                var names = typeof(TxAgentForm).Assembly.GetManifestResourceNames();
                System.Diagnostics.Debug.WriteLine("[TxAgent] 嵌入资源清单 (" + names.Length + " 条):");
                foreach (var n in names) System.Diagnostics.Debug.WriteLine("  - " + n);
            }
            catch { }
        }

        private void ShowFatal(string msg)
        {
            try { MessageBox.Show(this, msg, "TxTools.Agent", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
            try { Close(); } catch { }
        }

        // ─────────────────────────────────────────────────────
        //  JS → C# 消息分派
        // ─────────────────────────────────────────────────────

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw = null;
            try { raw = e.TryGetWebMessageAsString(); }
            catch
            {
                // JS 端传的可能不是纯字符串,尝试从 JSON 读
                try { raw = e.WebMessageAsJson; } catch { }
            }
            if (string.IsNullOrEmpty(raw))
            {
                PostStatus("\u6536\u5230\u7a7a\u6d88\u606f\uff08\u53ef\u80fd\u662f WebView2 \u4f20\u9012\u5f02\u5e38\uff09");
                return;
            }

            JObject msg;
            try { msg = JObject.Parse(raw); }
            catch (Exception ex)
            {
                PostStatus("\u6d88\u606f\u89e3\u6790\u5931\u8d25: " + ex.Message);
                return;
            }

            var type = (string)msg["type"];

            // 配方侧边栏消息分流:recipe.* 全部交给侧边栏处理器,不进对话逻辑
            if (type != null && type.StartsWith("recipe.", StringComparison.Ordinal))
            {
                try { OnRecipeWebMessage(msg); }
                catch (Exception ex)
                {
                    try { AuditLog.Write("[warn] [Recipe] 处理侧边栏消息失败: " + ex.Message); } catch { }
                }
                return;
            }

            try
            {
                switch (type)
                {
                    case "jsReady":
                        OnJsReady();
                        break;

                    case "setApiKey":
                        {
                            var newKey = (string)msg["key"];
                            var pid = (string)msg["providerId"];
                            if (!string.IsNullOrWhiteSpace(pid))
                                _currentProviderId = pid;
                                // 同名模型跨 provider 会重名(百炼代理了 deepseek/kimi),
                                // 路由必须知道当前是哪一家,否则按名反查会串到别家去
                                ModelRouter.CurrentProviderId = pid;
                            ApplyKey(newKey, persist: true);
                            break;
                        }

                    case "switchModel":
                        // providerId 由前端的 option.dataset 带过来。
                        // 【不能只靠模型名反查】百炼代理了 deepseek/kimi，
                        // 同名模型在两个 provider 下都存在，按名反查必然串。
                        SwitchModel((string)msg["model"], (string)msg["providerId"]);
                        break;

                    case "setApprovalMode":
                        {
                            var m = (string)msg["mode"];
                            if (m == "ask" || m == "auto_safe" || m == "auto_all")
                            {
                                _approvalMode = m;
                                var label = m == "ask" ? "\u8be2\u95ee"
                                          : m == "auto_safe" ? "\u534a\u81ea\u52a8(\u4ee3\u7801\u4ecd\u5f39\u7a97)"
                                          : "\u5168\u81ea\u52a8(\u542b\u4ee3\u7801)";
                                PostStatus("\u5df2\u5207\u6362\u5ba1\u6279\u6a21\u5f0f: " + label);
                                AuditLog.Write("APPROVAL-MODE = " + m);
                                try { UserPrefsStore.UpdateApprovalMode(m); } catch { }
                            }
                            break;
                        }

                    case "setToolGroup":
                        {
                            var g = (string)msg["group"];
                            bool on = msg["enabled"] != null && (bool)msg["enabled"];
                            if (!string.IsNullOrWhiteSpace(g) && ToolGate.SetEnabled(g, on))
                            {
                                try { UserPrefsStore.UpdateToolGroups(ToolGate.SnapshotEnabled()); } catch { }
                                PostToolGroups();
                                PostStatus("\u5df2\u5173\u542f\u5de5\u5177\u7ec4: " + ToolGate.GroupDisplayName(g)
                                    + " (\u4e0b\u6b21\u65b0\u5efa\u5bf9\u8bdd\u751f\u6548)\u3002");
                            }
                            break;
                        }

                    case "addProvider":
                        {
                            var name = (string)msg["displayName"];
                            var baseUrl = (string)msg["baseUrl"];
                            bool isLocal = msg["isLocal"] != null && (bool)msg["isLocal"];
                            string modelsCsv = msg["models"] != null ? (string)msg["models"] : null;

                            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(baseUrl))
                            {
                                PostStatus("\u6dfb\u52a0\u5931\u8d25: \u540d\u79f0\u548c Base URL \u4e0d\u80fd\u4e3a\u7a7a\u3002");
                                break;
                            }

                            var models = new List<string>();
                            if (!string.IsNullOrWhiteSpace(modelsCsv))
                                foreach (var m in modelsCsv.Split(new[] { ',', ';', '\n', '\r', ' ' },
                                         StringSplitOptions.RemoveEmptyEntries))
                                    if (!string.IsNullOrWhiteSpace(m)) models.Add(m.Trim());

                            var prov = LlmProviders.AddOrUpdateCustom(new LlmProvider
                            {
                                Id = null,
                                DisplayName = name.Trim(),
                                BaseUrl = baseUrl.Trim().TrimEnd('/'),
                                Models = models.Count > 0 ? models.ToArray() : new string[0],
                                IsLocal = isLocal,
                                IsCustom = true
                            });

                            PostStatus("\u5df2\u6dfb\u52a0\u6a21\u578b\u63d0\u4f9b\u5546: " + prov.DisplayName);
                            PostProviderAndModelList();
                            PostToolGroups();
                            break;
                        }

                    case "removeProvider":
                        {
                            var pid = (string)msg["providerId"];
                            var prov = LlmProviders.ById(pid);
                            if (!string.IsNullOrWhiteSpace(pid) && prov != null && prov.IsCustom)
                            {
                                LlmProviders.RemoveCustom(pid);
                                KeyStore.Clear(pid);
                                PostStatus("\u5df2\u5220\u9664\u6a21\u578b\u63d0\u4f9b\u5546: " + prov.DisplayName);
                                PostProviderAndModelList();
                            }
                            break;
                        }

                    case "listToolGroups":
                        PostToolGroups();
                        break;

                    case "listProviders":
                        PostProviderAndModelList();
                        break;

                    case "undoLastUser":
                        UndoLastUserSend();
                        break;

                    case "userSend":
                        _ = HandleUserSendAsync((string)msg["text"], msg["attachments"] as JArray);
                        break;

                    case "userStop":
                        try { if (_cts != null) _cts.Cancel(); } catch { }
                        // 若此时正等待审批/askUser,视为取消解除阻塞,让 SendAsync 尽快返回
                        ReleasePendingApproval(false);
                        ReleasePendingAskUser(null);
                        break;

                    case "approvalResult":
                        {
                            bool allow = msg["allow"] != null && (bool)msg["allow"];
                            ReleasePendingApproval(allow);
                            break;
                        }

                    case "openPath":
                        HandleOpenPath((string)msg["path"]);
                        break;

                    case "askUserResponse":
                        {
                            // 用户答复了 ask_user 弹窗。cancelled 时 answer 传 null
                            var cancelled = msg["cancelled"] != null && (bool)msg["cancelled"];
                            var answer = cancelled ? null : (string)msg["answer"];
                            ReleasePendingAskUser(answer);
                            break;
                        }

                    case "newConv":
                        NewConversation();
                        break;

                    case "listConvs":
                        PostConvList();
                        break;

                    case "listTools":
                        PostToolList();
                        break;

                    case "openConv":
                        OpenConversation((string)msg["id"]);
                        break;

                    case "deleteConv":
                        {
                            var delId = (string)msg["id"];
                            try { ConversationStore.Delete(delId); } catch { }
                            // 如果删的是当前对话,清空并切到新对话 —— 否则下次切换时 SaveCurrent
                            // 会把这条被删的对话原地写回,导致"删了又出现"的假象。
                            if (_current != null && string.Equals(_current.Id, delId, StringComparison.Ordinal))
                            {
                                _current = null;
                                StartFreshConversation();
                            }
                            PostConvList();
                            break;
                        }

                    case "uploadFile":
                        HandleUploadFile((string)msg["filename"], (string)msg["contentBase64"]);
                        break;

                    case "removeAttachment":
                        try { UploadStore.Remove((string)msg["id"]); } catch { }
                        break;

                    case "extractLessons":
                        FireExtractLessons();
                        PostStatus("\u5df2\u5c1d\u8bd5\u7ecf\u9a8c\u840c\u53d6\uff08\u540e\u53f0\uff09");
                        break;
                }
            }
            catch (Exception ex)
            {
                PostStatus("\u5904\u7406\u6d88\u606f\u5f02\u5e38: " + ex.Message);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  配方侧边栏(recipe.* 消息)
        // ═══════════════════════════════════════════════════════════
        //  与 chat.html 里的 window.txRecipes 配对。绑定值(ITxObject.Id)只活在
        //  侧边栏内存 + study 键校验,不落盘 —— 见 RecipeStore.cs 顶部说明。

        private void OnRecipeWebMessage(JObject msg)
        {
            var type = (string)msg["type"] ?? "";
            var seq = (int?)msg["seq"] ?? 0;

            switch (type)
            {
                case "recipe.list":          HandleRecipeList(seq); break;
                case "recipe.pickSelection": HandlePickSelection(seq, msg); break;
                case "recipe.run":           HandleRecipeRun(seq, msg); break;
                case "recipe.reveal":        HandleRecipeReveal(seq, msg); break;
                case "recipe.promote":       HandleRecipePromote(seq, msg); break;
            }
        }

        private void ReplyToWeb(int seq, JObject payload)
        {
            payload["seq"] = seq;
            // 【必须回到 UI 线程】PostWebMessageAsJson 只能在创建 WebView2 的线程上调,
            // 而下面几个 Handler 里有跑在后台线程的分支。
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => ReplyToWeb(seq, payload)));
                return;
            }
            try
            {
                _webView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(payload));
            }
            catch (Exception ex)
            {
                // 窗体正在关闭时 WebView 可能已释放 —— 配方后台线程跑到一半才回包是常态,
                // 不能让回包异常炸掉线程池
                try { AuditLog.Write("[warn] [Recipe] 回包失败(窗体可能已关闭): " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 宿主主动推送(无 seq,前端按 type 分派)。配方变更与 study 切换都走这里。
        /// </summary>
        private void PostRecipePush(JObject payload)
        {
            // WebView 可能还没就绪(窗体刚开)或已在关闭 —— 推送是尽力而为,不抛
            try
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                if (InvokeRequired) { BeginInvoke((Action)(() => PostRecipePush(payload))); return; }
                _webView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(payload));
            }
            catch { }
        }

        private void OnRecipesChanged()
        {
            PostRecipePush(new JObject { ["type"] = "recipe.changed" });
        }

        private void PollStudyChange()
        {
            var k = CurrentStudyKey();
            if (_studyPollPrimed && string.Equals(k, _lastStudyKey, StringComparison.Ordinal)) return;

            _lastStudyKey = k;
            if (!_studyPollPrimed) { _studyPollPrimed = true; return; }   // 首轮只建基线不推送

            // 前端收到后作废全部绑定并刷新 —— 绑定只在同一 study 内有效,
            // 不作废的话会拿 A study 的对象 Id 去 B study 执行,还不报错
            PostRecipePush(new JObject { ["type"] = "recipe.studyChanged", ["study"] = k ?? "" });
        }

        private void HandleRecipeList(int seq)
        {
            var recipes = new JArray();
            foreach (var r in RecipeStore.All())
            {
                var jr = new JObject
                {
                    ["id"] = r.Id, ["name"] = r.Name, ["description"] = r.Description,
                    ["lang"] = r.Lang, ["runCount"] = r.RunCount, ["failCount"] = r.FailCount
                };
                var jp = new JArray();
                foreach (var p in r.Params)
                    jp.Add(new JObject
                    {
                        ["name"] = p.Name, ["label"] = p.Label, ["kind"] = p.Kind,
                        ["typeHint"] = p.TypeHint, ["required"] = p.Required,
                        ["def"] = p.Default, ["help"] = p.Help
                    });
                jr["params"] = jp;
                recipes.Add(jr);
            }

            var cands = new JArray();
            foreach (var s in RecipeStore.PromotionCandidates())
                cands.Add(new JObject { ["name"] = s.Name, ["successCount"] = s.SuccessCount });

            var studyKey = CurrentStudyKey();
            _lastStudyKey = studyKey;              // 与轮询共用基线,避免列表刷新触发假推送

            ReplyToWeb(seq, new JObject
            {
                ["type"] = "recipe.list.result", ["ok"] = true,
                ["recipes"] = recipes, ["candidates"] = cands,
                ["study"] = studyKey
            });
        }

        /// <summary>
        /// 绑定值只在同一个 study 内有效,所以要一个能区分 study 的键。
        /// 用 study 名 —— 换了 study 前端就把绑定全部作废并要求重选。
        /// 【不要在换 study 后按名字重新解析对象】同一 study 内都可能有多台同名机器人,
        /// 跨 study 猜就是纯赌,而赌错了不会报错,只会对着错误的对象执行。
        /// </summary>
        private string CurrentStudyKey()
        {
            try
            {
                dynamic doc = TxApplication.ActiveDocument;
                if (doc == null) return null;
                dynamic study = doc.CurrentStudy;
                return study == null ? null : (string)study.Name;
            }
            catch { return null; }
        }

        private void HandlePickSelection(int seq, JObject msg)
        {
            // 【所有回包必须带 type】chat.html 的转发门只把 type 以 "recipe." 开头的
            // 消息交给侧边栏;此前 pick/run/promote 的回包没有 type,在页面侧被静默丢弃,
            // 前端只能等到超时 —— 表现为"执行/取选择永远超时",而宿主侧其实已经跑完。
            bool multi = msg["multi"] != null && (bool)msg["multi"];
            try
            {
                var sel = TxApplication.ActiveSelection.GetItems();
                if (sel == null || sel.Count == 0)
                {
                    ReplyToWeb(seq, new JObject { ["type"] = "recipe.pick.result", ["ok"] = false,
                        ["error"] = "PS 里当前没有选中任何对象。" });
                    return;
                }

                if (!multi && sel.Count > 1)
                {
                    // 【不要替它选第一个】这正是踩过四次的那个模式。
                    ReplyToWeb(seq, new JObject { ["type"] = "recipe.pick.result", ["ok"] = false,
                        ["error"] = "当前选中了 " + sel.Count + " 个对象，而这个参数只要一个。请只选一个再点。" });
                    return;
                }

                if (multi)
                {
                    // ITxObject.Id 里本身含逗号("3,57,2,1"),所以多选用 | 分隔
                    var ids = string.Join("|", sel.Select(o => o.Id));
                    ReplyToWeb(seq, new JObject { ["type"] = "recipe.pick.result", ["ok"] = true, ["id"] = ids,
                        ["name"] = sel[0].Name, ["count"] = sel.Count,
                        ["type"] = sel[0].GetType().Name });
                }
                else
                {
                    var o = sel[0];
                    ReplyToWeb(seq, new JObject { ["type"] = "recipe.pick.result", ["ok"] = true, ["id"] = o.Id,
                        ["name"] = o.Name, ["count"] = 1, ["type"] = o.GetType().Name });
                }
            }
            catch (Exception ex)
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.pick.result", ["ok"] = false, ["error"] = "取选择失败: " + ex.Message });
            }
        }

        private void HandleRecipeRun(int seq, JObject msg)
        {
            var id = (string)msg["recipeId"];
            var r = RecipeStore.Get(id);
            if (r == null)
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.run.result", ["ok"] = false,
                    ["recipeId"] = id ?? "", ["error"] = "配方不存在，可能已被删除。" });
                return;
            }

            var args = new Dictionary<string, string>();
            var jargs = msg["args"] as JObject;
            if (jargs != null)
                foreach (var kv in jargs) args[kv.Key] = kv.Value == null ? null : kv.Value.ToString();

            string err;
            var full = RecipeRunner.BuildCode(r, args, out err);
            if (full == null)
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.run.result", ["ok"] = false,
                    ["recipeId"] = r.Id, ["error"] = err });
                return;
            }

            // 【宿主侧防重入】前端超时后会重新解锁按钮,用户可能对仍在执行的配方再点一次 ——
            // 两次执行会在 PS 主线程队列里串行,表现为"点了一次跑了两遍"。
            if (Interlocked.CompareExchange(ref _recipeRunInFlight, 1, 0) != 0)
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.run.result", ["ok"] = false,
                    ["recipeId"] = r.Id, ["error"] = "已有配方正在执行中（可能已超时但仍在跑）。请等它结束或在 PS 里确认状态后再试。" });
                return;
            }

            // ── 不走审批 ──
            // 配方代码是人工固化过的,审批框里那段代码没有新信息量;
            // 参数才是这次的变量,而参数就摆在侧边栏上,比审批框好读。
            // 兜底靠 undo:下面把配方名传给 undoLabel,用户在 Ctrl+Z 历史里能认出是哪一步。

            // ── 后台线程执行,不要阻塞 UI ──
            // 原来整个 HandleRecipeRun 在 WebMessageReceived(UI 线程)里同步跑:
            // 编译在 UI 线程、执行也占着 PS 主线程,前端连"执行中"都渲染不出来。
            // 这里丢到后台线程:编译(C#)在后台做,执行仍由 RunCSharp/PythonHost 内部
            // 封送回 PS 主线程(与 AgentLoop 调工具是同一套 PsContext 路由),UI 保持响应,
            // 前端能显示执行中的反馈,完成后 ReplyToWeb 跨线程回 UI 发消息。
            var lang = SnippetStore.NormalizeLang(r.Lang);
            var runId = r.Id;
            var runName = r.Name;

            Task.Run(delegate
            {
                bool ok;
                string text;
                try
                {
                    if (lang == "python")
                    {
                        // PythonHost 未配置 MainThreadContext 时 Run 会在调用线程直接跑,
                        // 而脚本里的 PS API 必须走主线程 —— 与 RunPythonTool 同样用
                        // PsContext 包住整个 Run,避免从后台线程碰 PS API。
                        var res = default(TxTools.Agent.Scripting.PythonExecResult);
                        PsContext.Current.Run(delegate
                        {
                            res = TxTools.Agent.Scripting.PythonHostProvider.Instance.Run(
                                full, TxTools.Agent.Scripting.PythonRunMode.Execute, "配方: " + runName);
                        });
                        ok = res != null && res.Success;
                        text = res != null ? res.ToAgentText() : "(无结果)";
                    }
                    else
                    {
                        text = TxTools.Agent.Ps.PsBridge.RunCSharp(full, out ok, "配方: " + runName);
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    text = "执行异常: " + ex.Message;
                }
                finally
                {
                    Interlocked.Exchange(ref _recipeRunInFlight, 0);
                }

                RecipeStore.RecordRun(runId, ok);
                try
                {
                    AuditLog.Write((ok ? "[info]" : "[warn]") + " [Recipe] " + runName
                        + " 执行" + (ok ? "成功" : "失败") + "，参数: "
                        + string.Join(", ", args.Select(kv => kv.Key + "=" + kv.Value)));
                }
                catch { }

                // 【type + recipeId 必须带】前端超时后 pending 可能已清,
                // 靠 type 才能把迟到的结果兜底投递回卡片
                ReplyToWeb(seq, new JObject
                {
                    ["type"] = "recipe.run.result", ["ok"] = ok,
                    ["recipeId"] = runId, ["text"] = text ?? ""
                });
            });
        }

        /// <summary>recipe.reveal:把配方原文当作一条助手消息推进聊天区,不必走模型。</summary>
        private void HandleRecipeReveal(int seq, JObject msg)
        {
            var id = (string)msg["recipeId"];
            var r = RecipeStore.Get(id);
            if (r == null)
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.reveal.result", ["ok"] = false, ["error"] = "配方不存在。" });
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("**【配方 ").Append(r.Name).Append("】** ");
            if (!string.IsNullOrWhiteSpace(r.Description)) sb.AppendLine(r.Description.Trim());
            sb.AppendLine();
            sb.AppendLine("语言: ").Append(SnippetStore.NormalizeLang(r.Lang));
            if (r.Params != null && r.Params.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("参数:");
                foreach (var p in r.Params)
                    sb.Append("- ").Append(p.Name)
                      .Append(p.Label != null && !string.Equals(p.Label, p.Name, StringComparison.Ordinal)
                          ? " (" + p.Label + ")" : "")
                      .Append(" [").Append(p.Kind).AppendLine("]");
            }
            sb.AppendLine();
            sb.AppendLine("```" + SnippetStore.NormalizeLang(r.Lang));
            sb.AppendLine((r.Code ?? "").TrimEnd());
            sb.AppendLine("```");

            PostJs(new { type = "message", role = "assistant", text = sb.ToString() });
            ReplyToWeb(seq, new JObject { ["type"] = "recipe.reveal.result", ["ok"] = true });
        }

        /// <summary>
        /// recipe.promote:把片段固化成配方。参数语义需要模型读代码后判断 ——
        /// 往输入框塞一句预设提示并触发发送,走一轮正常对话。
        /// </summary>
        private void HandleRecipePromote(int seq, JObject msg)
        {
            // 兼容 "snippet" 旧字段名 —— 前后端曾各叫各的导致固化按钮永远报缺参
            var name = (string)msg["snippetName"] ?? (string)msg["snippet"];
            if (string.IsNullOrWhiteSpace(name))
            {
                ReplyToWeb(seq, new JObject { ["type"] = "recipe.promote.result", ["ok"] = false, ["error"] = "缺少 snippetName。" });
                return;
            }

            var hint = "把片段 \"" + name + "\" 固化成配方：判断其中哪些部分应该做成参数（对象/数字/文本），"
                     + "给每个参数起合法的英文变量名和中文标签，然后调用 save_recipe。";
            PostJs(new { type = "userTextPrefill", text = hint });
            ReplyToWeb(seq, new JObject { ["type"] = "recipe.promote.result", ["ok"] = true });
        }

        /// <summary>JS 侧完成初始化后调用。发初始化数据、恢复上次对话或触发 API Key 输入。</summary>
        private void OnJsReady()
        {
            _webViewReady = true;

            // 隐藏加载遮罩,让 WebView 显示出来
            try
            {
                if (_loadingTimer != null) _loadingTimer.Stop();
                if (_loadingOverlay != null) _loadingOverlay.Visible = false;
            }
            catch { }

            // v3: 加载用户偏好 —— 恢复上次的 provider/model/审批模式,和各 provider 的模型列表缓存
            try
            {
                var prefs = UserPrefsStore.Load();
                if (!string.IsNullOrWhiteSpace(prefs.ProviderId)) _currentProviderId = prefs.ProviderId;
                ModelRouter.CurrentProviderId = _currentProviderId;
                if (!string.IsNullOrWhiteSpace(prefs.Model)) _currentModel = prefs.Model;
                if (!string.IsNullOrWhiteSpace(prefs.ApprovalMode)) _approvalMode = prefs.ApprovalMode;

                if (prefs.Models != null)
                {
                    foreach (var kv in prefs.Models)
                    {
                        var p = LlmProviders.ById(kv.Key);
                        if (p != null && kv.Value != null && kv.Value.List != null && kv.Value.List.Length > 0)
                            p.Models = kv.Value.List;
                    }
                }

                // 若上次记的 model 已不在当前 provider 的 models 里(可能被清理或名字变了),回落到第一个
                var provCur = LlmProviders.ById(_currentProviderId);
                if (provCur.Models.Length > 0 && Array.IndexOf(provCur.Models, _currentModel) < 0)
                    _currentModel = provCur.Models[0];
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[TxAgent] load prefs failed: " + ex.Message);
            }

            // 1) 全部提供商 + 模型列表 (分组显示,已含缓存)
            PostProviderAndModelList();

            // 2) 工具组开关状态(设置面板)
            PostToolGroups();

            // 2) 推审批模式让前端 select 恢复选中
            PostJs(new { type = "approvalMode", value = _approvalMode });

            // 3) 加载当前 provider 的 API Key
            var saved = KeyStore.Load(_currentProviderId);
            var prov = LlmProviders.ById(_currentProviderId);
            if (!string.IsNullOrWhiteSpace(saved) || prov.IsLocal)
            {
                ApplyKey(saved ?? "ollama", persist: false);   // Ollama 无需真 key,占位一个
                LoadMostRecentOrNew();
                PostJs(new { type = "keyReady" });
                PostStatus("\u5df2\u52a0\u8f7d " + prov.DisplayName + "\u3002\u5de5\u5177 " + _tools.Count + " \u4e2a\u3002");
            }
            else
            {
                PostStatus("\u5c1a\u672a\u8bbe\u7f6e " + prov.DisplayName + " API Key\u3002");
                PostAskApiKey(prov, "\u9996\u6b21\u4f7f\u7528\u9700\u8981\u8bbe\u7f6e " + prov.DisplayName + " \u7684 API Key\u3002");
            }
        }

        /// <summary>发送全部 provider 及其模型列表(前端用 optgroup 分组显示),同时告诉当前选中。</summary>
        private void PostProviderAndModelList()
        {
            var providers = new List<object>();
            foreach (var p in LlmProviders.GetAll())
            {
                providers.Add(new
                {
                    id = p.Id,
                    displayName = p.DisplayName,
                    baseUrl = p.BaseUrl,
                    isLocal = p.IsLocal,
                    isCustom = p.IsCustom,
                    keyPageUrl = p.KeyPageUrl,
                    models = p.Models ?? new string[0]
                });
            }
            PostJs(new
            {
                type = "modelList",
                providers,
                currentProvider = _currentProviderId,
                current = _currentModel
            });
        }

        /// <summary>发送工具组开关状态(前端设置面板用)。</summary>
        private void PostToolGroups()
        {
            var groups = new List<object>();
            foreach (var g in ToolGate.AllGroups())
            {
                groups.Add(new
                {
                    id = g,
                    name = ToolGate.GroupDisplayName(g),
                    enabled = ToolGate.IsEnabled(g)
                });
            }
            PostJs(new { type = "toolGroups", groups });
        }

        /// <summary>发 askApiKey 消息给 JS,附带当前 provider 的元数据方便 modal 展示。</summary>
        private void PostAskApiKey(LlmProvider prov, string reason)
        {
            PostJs(new
            {
                type = "askApiKey",
                reason,
                providerId = prov.Id,
                providerName = prov.DisplayName,
                baseUrl = prov.BaseUrl,
                keyPageUrl = prov.KeyPageUrl,
                isLocal = prov.IsLocal
            });
        }

        // ─────────────────────────────────────────────────────
        //  C# → JS: 统一入口 PostJs + 各种便捷发送
        // ─────────────────────────────────────────────────────

        private void PostJs(object payload)
        {
            // 注意: 不能在这里判断 _webView.CoreWebView2 == null —— 那本身就是"访问 CoreWebView2",
            // 从后台线程(如 AgentLoop 事件回调 in Task.Run)进来会抛
            //   "CoreWebView2 can only be accessed from the UI thread"。
            // 所有 CoreWebView2 访问必须先跳到 UI 线程再做。
            if (!_webViewReady) return;

            string json;
            try
            {
                json = JsonConvert.SerializeObject(payload,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            }
            catch { return; }

            var js = "try{dispatchMessage(" + JsonConvert.SerializeObject(json) + ");}catch(e){}";

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SafeExecuteScript(js))); }
                catch { /* form 已释放或 handle 未创建 */ }
            }
            else
            {
                SafeExecuteScript(js);
            }
        }

        /// <summary>在 UI 线程上安全执行 JS,访问 CoreWebView2 前统一 null 检查+异常兜底。</summary>
        private void SafeExecuteScript(string js)
        {
            try
            {
                if (_webView == null || _webView.CoreWebView2 == null) return;
                _webView.CoreWebView2.ExecuteScriptAsync(js);
            }
            catch { }
        }

        private void PostStatus(string text) { PostJs(new { type = "status", text }); }
        private void PostBusy(bool busy) { PostJs(new { type = "busy", value = busy }); }
        /// <summary>
        /// 下发 token 用量。除累计输入/输出外,还附带一份【上下文占用估算】:
        ///   ctxUsed / ctxMax  当前上下文占了模型窗口的多少
        ///   parts             按 系统提示词 / 工具定义 / 对话消息 三段拆分
        /// 分项是按字符数估算的(中英文混排取 2 字符≈1 token),不是 API 精确计数 ——
        /// 页面上已标注"估算值",用途是让用户知道该压缩哪一块,不用于计费。
        /// </summary>
        /// <summary>
        /// 打开前端点击的文件路径。
        ///
        /// 文件存在 → 在资源管理器里【选中】它(/select),而不是直接执行 ——
        /// 直接 Process.Start 一个 .exe/.bat 就等于替用户运行未知程序,风险不对等。
        /// 目录存在 → 打开该目录。
        /// 都不存在 → 退到最近的存在的上级目录,并说明原因;
        /// 模型给出的路径经常是"将要生成"而不是"已经生成"的。
        /// </summary>
        private void HandleOpenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var p = path.Trim().Trim('"');

                if (System.IO.File.Exists(p))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\"");
                    PostStatus("已在资源管理器中定位: " + System.IO.Path.GetFileName(p));
                    return;
                }

                if (System.IO.Directory.Exists(p))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\"");
                    PostStatus("已打开目录: " + p);
                    return;
                }

                // 逐级上溯找一个存在的目录
                var dir = p;
                for (int i = 0; i < 6; i++)
                {
                    dir = System.IO.Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(dir)) break;
                    if (System.IO.Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                        PostStatus("文件不存在，已打开上级目录: " + dir);
                        return;
                    }
                }

                PostStatus("路径不存在: " + p);
            }
            catch (Exception ex)
            {
                PostStatus("打开失败: " + ex.Message);
            }
        }

        private void PostTokenUsage(int p, int c, int t)
        {
            // 入参来自当前 loop,加上基线才是本会话的真实累计
            p = _baseP + (_loop != null ? _loop.TotalPromptTokens : 0);
            c = _baseC + (_loop != null ? _loop.TotalCompletionTokens : 0);
            t = p + c;

            int sysTok = 0, msgTok = 0, toolTok = 0;

            try
            {
                var loop = _loop;
                if (loop != null && loop.WorkingMemory != null)
                {
                    foreach (var m in loop.WorkingMemory)
                    {
                        if (m == null) continue;
                        int n = EstimateMessageTokens(m);
                        if (m.Role == "system") sysTok += n;
                        else msgTok += n;
                    }
                }
                toolTok = EstimateToolTokens();
            }
            catch { /* 估算失败不影响主流程,前端拿不到 parts 会自动降级 */ }

            int used = sysTok + msgTok + toolTok;
            int max = ModelRouter.ContextWindowFor(_currentModel, _currentProviderId);

            PostJs(new
            {
                type = "tokenUsage",
                prompt = p,
                completion = c,
                total = t,
                ctxUsed = used,
                ctxMax = max,
                parts = new { system = sysTok, tools = toolTok, messages = msgTok }
            });
        }

        private static int EstimateMessageTokens(ChatMessage m)
        {
            if (m == null) return 0;
            int len = 0;
            if (!string.IsNullOrEmpty(m.Content)) len += m.Content.Length;
            if (m.ToolCalls != null)
            {
                foreach (var tc in m.ToolCalls)
                {
                    if (tc == null || tc.Function == null) continue;
                    len += (tc.Function.Name ?? "").Length;
                    len += (tc.Function.Arguments ?? "").Length;
                }
            }
            return len / 2;
        }

        /// <summary>工具定义整轮不变,算一次缓存住。</summary>
        private int _toolTokensCache = -1;

        private int EstimateToolTokens()
        {
            if (_toolTokensCache >= 0) return _toolTokensCache;

            int len = 0;
            try
            {
                foreach (var t in _tools.Tools)
                {
                    if (t == null) continue;
                    len += (t.Name ?? "").Length;
                    len += (t.Description ?? "").Length;
                    if (t.InputSchema != null)
                    {
                        try { len += JsonConvert.SerializeObject(t.InputSchema).Length; }
                        catch { }
                    }
                }
            }
            catch { }

            _toolTokensCache = len / 2;
            return _toolTokensCache;
        }



        private void PostConvList()
        {
            List<ConversationMeta> metas;
            try { metas = ConversationStore.List(); }
            catch { metas = new List<ConversationMeta>(); }

            var items = metas.Select(m => new
            {
                id = m.Id,
                title = string.IsNullOrEmpty(m.Title) ? "(\u65e0\u6807\u9898)" : m.Title,
                updated = m.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                // 标出正被另一个 PDPS 进程打开的对话，避免用户点进去发现被拒
                busy = m.HeldByOther
            }).ToList();
            PostJs(new { type = "convList", items });
        }

        /// <summary>把所有已注册工具的名字/描述/是否只读 发给 JS 端,供工具面板展示。</summary>
        private void PostToolList()
        {
            var items = new List<object>();
            try
            {
                foreach (var t in _tools.Tools)
                {
                    items.Add(new
                    {
                        name = t.Name,
                        description = t.Description ?? "",
                        isReadOnly = t.IsReadOnly
                    });
                }
            }
            catch { }
            // 按 只读优先 + 名称字母序 排,展示更清爽
            items.Sort((a, b) =>
            {
                dynamic da = a, db = b;
                int cmp = ((bool)db.isReadOnly).CompareTo((bool)da.isReadOnly);   // true(只读) 在前
                if (cmp != 0) return cmp;
                return string.Compare((string)da.name, (string)db.name, StringComparison.OrdinalIgnoreCase);
            });
            PostJs(new { type = "toolList", items });
        }

        // ─────────────────────────────────────────────────────
        //  上传文件
        // ─────────────────────────────────────────────────────

        private void HandleUploadFile(string filename, string contentBase64)
        {
            if (string.IsNullOrEmpty(contentBase64))
            {
                PostJs(new { type = "attachmentInfo", error = "\u4e0a\u4f20\u5185\u5bb9\u4e3a\u7a7a" });
                return;
            }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(contentBase64); }
            catch (Exception ex)
            {
                PostJs(new { type = "attachmentInfo", error = "base64 \u89e3\u7801\u5931\u8d25: " + ex.Message });
                return;
            }

            var convId = _current != null ? _current.Id : "_default";
            UploadedFile uf;
            try { uf = UploadStore.Store(convId, filename, bytes); }
            catch (Exception ex)
            {
                PostJs(new { type = "attachmentInfo", error = "\u5b58\u50a8\u5931\u8d25: " + ex.Message });
                return;
            }

            try { FileParserService.Parse(uf); }
            catch (Exception ex) { uf.ParseError = ex.Message; }

            PostJs(new
            {
                type = "attachmentInfo",
                id = uf.Id,
                name = uf.OriginalName,
                extension = uf.Extension,
                size = uf.Size,
                sizeText = FileParserService.FormatBytes(uf.Size),
                rowCount = uf.RowCount,
                colCount = uf.ColCount,
                sheetCount = uf.SheetCount,
                summary = uf.ParsedSummary,
                error = uf.ParseError
            });
        }

        // ─────────────────────────────────────────────────────
        //  用户发送(含附件摘要注入)
        // ─────────────────────────────────────────────────────

        private async Task HandleUserSendAsync(string text, JArray attachments)
        {
            if (_isSending)
            {
                PostStatus("\u6b63\u5728\u5904\u7406\u4e0a\u4e00\u6761\u8bf7\u6c42\uff0c\u8bf7\u7a0d\u5019\u3002");
                return;
            }

            if (_loop == null)
            {
                PostStatus("\u8bf7\u5148\u8bbe\u7f6e API Key\u3002");
                PostJs(new { type = "askApiKey", reason = "\u5c1a\u672a\u8bbe\u7f6e API Key\u3002" });
                return;
            }

            var body = text ?? "";
            var prefix = BuildAttachmentPrefix(attachments);
            var finalText = string.IsNullOrEmpty(prefix) ? body : (prefix + "\n\n" + body);
            if (string.IsNullOrWhiteSpace(finalText)) return;

            PostBusy(true);
            _isSending = true;
            _cts = new CancellationTokenSource();
            try
            {
                var token = _cts.Token;
                await Task.Run(() => _loop.SendAsync(finalText, token));
                PostStatus("\u5c31\u7eea\u3002");
            }
            catch (OperationCanceledException)
            {
                PostJs(new { type = "message", role = "\u7cfb\u7edf", text = "\u5df2\u53d6\u6d88\u672c\u6b21\u8bf7\u6c42\u3002" });
                PostStatus("\u5df2\u53d6\u6d88\u3002");
            }
            catch (LlmApiException apiEx)
            {
                PostJs(new { type = "message", role = "\u7cfb\u7edf", text = "API \u9519\u8bef: " + apiEx.Message });
                PostStatus("API \u9519\u8bef\u3002");
            }
            catch (Exception ex)
            {
                PostJs(new { type = "message", role = "\u7cfb\u7edf", text = "\u51fa\u9519: " + ex.Message });
                PostStatus("\u51fa\u9519\u3002");
            }
            finally
            {
                PostJs(new { type = "closeAssistant" });
                PostBusy(false);
                try { if (_cts != null) _cts.Dispose(); } catch { }
                _cts = null;
                _isSending = false;
            }
        }

        /// <summary>把附件的摘要拼成一段前缀,注入到用户消息前面。让 AI 拿到即可判断能否直接答。</summary>
        private static string BuildAttachmentPrefix(JArray attachments)
        {
            if (attachments == null || attachments.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[\u5df2\u9644\u52a0\u6587\u4ef6]");
            int i = 0;
            foreach (var a in attachments)
            {
                i++;
                var id = (string)a["id"];
                if (string.IsNullOrEmpty(id)) continue;
                var uf = UploadStore.Get(id);
                if (uf == null) continue;

                sb.AppendLine();
                sb.AppendLine(i + ") " + uf.OriginalName + "  (id=" + uf.Id
                    + ", " + FileParserService.FormatBytes(uf.Size)
                    + (uf.SheetCount > 0 ? ", " + uf.SheetCount + " sheet" : "")
                    + (uf.RowCount > 0 ? ", " + uf.RowCount + "\u884c" + (uf.ColCount > 0 ? "\u00d7" + uf.ColCount + "\u5217" : "") : "")
                    + ")");
                if (!string.IsNullOrEmpty(uf.ParseError))
                    sb.AppendLine("   \u26a0 \u89e3\u6790\u8b66\u544a: " + uf.ParseError);
                if (!string.IsNullOrEmpty(uf.ParsedSummary))
                {
                    sb.AppendLine("   \u6458\u8981:");
                    foreach (var line in uf.ParsedSummary.Split(new[] { '\n' }, StringSplitOptions.None))
                        sb.AppendLine("     " + line.TrimEnd('\r'));
                }
            }
            sb.AppendLine();
            sb.AppendLine("[\u5982\u9700\u7cbe\u8bfb\u5b8c\u6574\u5185\u5bb9,\u8c03\u7528 read_uploaded_file(file_id=...)]");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────
        //  API Key / 模型切换
        // ─────────────────────────────────────────────────────

        private void ApplyKey(string key, bool persist)
        {
            var prov = LlmProviders.ById(_currentProviderId);
            // Ollama 本地不需要 key,但 HTTP Bearer header 不能空,填占位
            if (string.IsNullOrWhiteSpace(key) && prov.IsLocal) key = "ollama";
            if (string.IsNullOrWhiteSpace(key)) { PostStatus("API Key \u4e3a\u7a7a\u3002"); return; }
            try
            {
                _client = new DeepSeekClient(key, prov.BaseUrl);
                _loop = BuildLoop(_client);
                if (_current != null) _loop.LoadHistory(_current.Messages);

                if (persist && !prov.IsLocal)
                {
                    try { KeyStore.Save(key, prov.Id); } catch { }
                    PostStatus("\u5df2\u4fdd\u5b58 " + prov.DisplayName + " API Key\u3002");
                }
                PostJs(new { type = "keyReady" });

                // 记住 provider 选择 (下次开窗自动恢复)
                try { UserPrefsStore.UpdateChoice(_currentProviderId, _currentModel); } catch { }

                // Key 就绪后异步拉取该 provider 真实的模型列表(替换硬编码默认)
                FetchProviderModelsAsync(prov);

                if (_current == null) LoadMostRecentOrNew();
            }
            catch (Exception ex)
            {
                PostStatus("\u8bbe\u7f6e Key \u5931\u8d25: " + ex.Message);
            }
        }

        /// <summary>
        /// fire-and-forget 拉取当前 provider 的真实模型列表(GET /v1/models),成功后替换
        /// LlmProviders.All 对应项的 Models 数组,并推送新的 modelList 给前端刷新下拉。
        /// 失败保留硬编码默认列表,静默(仅 Debug 输出)。同一 provider 5 分钟内不重复拉。
        /// </summary>
        private static readonly Dictionary<string, DateTime> _lastModelFetch =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private void FetchProviderModelsAsync(LlmProvider prov)
        {
            if (prov == null || _client == null) return;

            DateTime last;
            if (_lastModelFetch.TryGetValue(prov.Id, out last)
                && (DateTime.UtcNow - last) < TimeSpan.FromMinutes(5))
                return; // 5 分钟内已拉过,跳过

            var client = _client;
            var pid = prov.Id;
            Task.Run(async () =>
            {
                try
                {
                    var models = await client.ListModelsAsync(CancellationToken.None);
                    if (models == null || models.Count == 0) return;

                    int rawCount = models.Count;

                    // /v1/models 返回的是平台【全量目录】,不是"我能用的":
                    // 百炼的业务空间白名单只管调用鉴权,不影响这里的返回内容;
                    // 目录里还混着 embedding/rerank/tts、日期快照变体、
                    // 以及不支持 function calling 的小参数模型。清洗一遍再进下拉。
                    models = ModelFilter.Clean(pid, models, _currentModel);
                    if (models.Count == 0) return;

                    // 排序:让当前选中的模型排最前,其他按字母序
                    models.Sort(StringComparer.OrdinalIgnoreCase);
                    var curModel = _currentModel;
                    if (!string.IsNullOrEmpty(curModel) && models.Contains(curModel))
                    {
                        models.Remove(curModel);
                        models.Insert(0, curModel);
                    }

                    _lastModelFetch[pid] = DateTime.UtcNow;

                    // 回 UI 线程更新 provider.Models + 推 modelList + 落盘缓存
                    Action apply = () =>
                    {
                        var target = LlmProviders.ById(pid);
                        target.Models = models.ToArray();
                        PostProviderAndModelList();
                        PostStatus("\u5df2\u5237\u65b0 " + target.DisplayName + " \u6a21\u578b\u5217\u8868 ("
                            + models.Count + " \u4e2a"
                            + (rawCount > models.Count ? ", \u5df2\u8fc7\u6ee4 " + (rawCount - models.Count) : "")
                            + ")");
                        // 落盘,下次开窗立即用缓存,不再显示硬编码默认
                        try { UserPrefsStore.UpdateModels(pid, target.Models); } catch { }
                    };
                    if (InvokeRequired) BeginInvoke(apply);
                    else apply();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TxAgent] FetchProviderModels(" + pid + ") failed: " + ex.Message);
                }
            });
        }

        /// <summary>切换模型。若模型属于不同 provider,自动重载对应 key 并重建 client。</summary>
        private void SwitchModel(string model, string providerId = null)
        {
            if (string.IsNullOrWhiteSpace(model)) return;

            // 优先用前端明确指定的 provider;没给才退回按模型名猜(老协议兼容)。
            // 按名猜是有歧义的 —— "deepseek-v4-flash" 在 deepseek 和 qwen 下都有。
            LlmProvider targetProv = null;
            if (!string.IsNullOrWhiteSpace(providerId))
                targetProv = LlmProviders.ById(providerId);

            if (targetProv == null)
            {
                targetProv = LlmProviders.FindByModel(model);
                if (targetProv != null)
                    AuditLog.Write("[warn] switchModel 未带 providerId，按模型名猜为 "
                                 + targetProv.Id + "，跨 provider 同名模型可能选错。");
            }

            if (targetProv == null) { PostStatus("找不到该模型对应的 provider。"); return; }
            bool changedProvider = !string.Equals(targetProv.Id, _currentProviderId, StringComparison.Ordinal);

            _currentModel = model;

            if (changedProvider)
            {
                _currentProviderId = targetProv.Id;
                // 换 provider 需要拿新的 key + 新的 baseUrl 重建 client
                var newKey = KeyStore.Load(targetProv.Id);
                if (string.IsNullOrWhiteSpace(newKey) && !targetProv.IsLocal)
                {
                    // 新 provider 没 key —— 弹 Key modal 让用户填,填完后自动重建 client
                    _client = null;
                    _loop = null;
                    PostStatus("\u5df2\u9009\u4e2d " + targetProv.DisplayName + " / " + model + ",\u9700\u5148\u8bbe\u7f6e API Key\u3002");
                    PostAskApiKey(targetProv, "\u5207\u6362\u5230 " + targetProv.DisplayName + ",\u9700\u8981\u8bbe\u7f6e\u5bf9\u5e94\u7684 API Key\u3002");
                    return;
                }
                _client = new DeepSeekClient(newKey ?? "ollama", targetProv.BaseUrl);
            }

            // 让 ModelRouter 知道当前是哪家 —— 上下文窗口、视觉能力等都按 provider+model 双键查
            ModelRouter.CurrentProviderId = _currentProviderId;

            if (_client == null) { PostStatus("\u5df2\u9884\u9009\u6a21\u578b: " + model); return; }
            _loop = BuildLoop(_client);
            _loop.LoadHistory(_current != null ? _current.Messages : new List<ChatMessage>());
            if (_current != null) RestoreTranscriptToJs(_current.Messages);
            try { UserPrefsStore.UpdateChoice(_currentProviderId, _currentModel); } catch { }
            // 切换/选择模型后异步拉取该 provider 的真实模型列表(本地 Ollama 也适用),
            // 5 分钟内已拉过则跳过 —— 这样每次切到 Ollama 都能拿到最新已安装的模型。
            FetchProviderModelsAsync(targetProv);
            PostStatus("\u5df2\u5207\u6362\u6a21\u578b\u4e3a " + targetProv.DisplayName + " / " + model + "\uff0c\u8bb0\u5fc6\u4fdd\u7559\u3002");
        }

        // ─────────────────────────────────────────────────────
        //  对话生命周期
        // ─────────────────────────────────────────────────────

        private void NewConversation()
        {
            SaveCurrent();
            FireExtractLessons();
            StartFreshConversation();
            PostStatus("\u5df2\u5f00\u59cb\u65b0\u5bf9\u8bdd\u3002\u65e7\u5bf9\u8bdd\u5df2\u4fdd\u7559\u3002");
        }

        private void OpenConversation(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            SaveCurrent();
            FireExtractLessons();
            LoadConversation(id);
            PostStatus("\u5df2\u6253\u5f00\u5386\u53f2\u5bf9\u8bdd\u3002");
        }

        private void LoadMostRecentOrNew()
        {
            var metas = ConversationStore.List();
            // 【不能直接取最新的】另一个 PDPS 进程可能正开着它 ——
            // 两边各自往 _fullHistory 追加，SaveCurrent 整份覆盖，后保存的把先保存的抹掉。
            var pick = ConversationStore.PickAvailable();
            if (pick != null) LoadConversation(pick.Id);
            else StartFreshConversation();
        }

        // ── 多进程协同 ──
        // 同时开两个 PDPS 时，双方共享同一份磁盘数据。
        //   watcher  对方新建/更新对话 → 刷新本地列表
        //   heartbeat 定期续期占用锁，否则 3 分钟后对方会认为本会话已空闲
        private ProcessSync.Watcher _convWatcher;
        private System.Windows.Forms.Timer _lockHeartbeat;

        private void StartProcessSync()
        {
            try
            {
                _convWatcher = ConversationStore.Watch(delegate
                {
                    // FileSystemWatcher 在后台线程回调，必须封送
                    try
                    {
                        if (IsHandleCreated)
                            BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { PostConvList(); });
                    }
                    catch { }
                });
            }
            catch { }

            try
            {
                _lockHeartbeat = new System.Windows.Forms.Timer { Interval = 60000 };
                _lockHeartbeat.Tick += delegate
                {
                    if (_current != null) ConversationStore.Acquire(_current.Id);
                };
                _lockHeartbeat.Start();
            }
            catch { }
        }

        private void StopProcessSync()
        {
            try { if (_convWatcher != null) _convWatcher.Dispose(); } catch { }
            try { if (_lockHeartbeat != null) { _lockHeartbeat.Stop(); _lockHeartbeat.Dispose(); } } catch { }
            try { if (_current != null) ConversationStore.Release(_current.Id); } catch { }
        }

        // 会话累计用量的基线:本次打开之前已经花掉的部分。
        // 实际显示值 = 基线 + 当前 loop 的计数,这样切走再切回来不会归零。
        private int _baseP, _baseC;

        private void StartFreshConversation()
        {
            if (_current != null) ConversationStore.Release(_current.Id);

            _current = new Conversation { Id = ConversationStore.NewId(), CreatedUtc = DateTime.UtcNow };
            ConversationStore.Acquire(_current.Id);
            _baseP = 0; _baseC = 0;
            if (_loop != null)
            {
                _loop.SetConvId(_current.Id);
                _loop.Reset();
            }
            PostJs(new { type = "clear" });
            PostTokenUsage(0, 0, 0);   // 立即清零,别等第一轮对话才刷新
            PostJs(new { type = "message", role = "\u7cfb\u7edf", text = "\u5df2\u5c31\u7eea\uff0c\u53ef\u4ee5\u5f00\u59cb\u5bf9\u8bdd\u3002" });
        }

        /// <summary>
        /// 撤销最后一条【用户】消息:连同它之后的一切(助手回复、工具结果)一起从历史里去掉。
        /// 撤销后把该消息的正文与附件信息回传前端,塞回输入框,便于重新编辑。
        /// </summary>
        private void UndoLastUserSend()
        {
            if (_loop == null || _current == null) return;
            var hist = _loop.FullHistory;
            if (hist == null || hist.Count == 0) return;

            // 从尾部往前找最后一条 user 消息
            int lastUser = -1;
            for (int i = hist.Count - 1; i >= 0; i--)
                if (hist[i] != null && hist[i].Role == "user")
                {
                    lastUser = i;
                    break;
                }
            if (lastUser < 0)
            {
                PostStatus("没有可撤销的用户消息。");
                return;
            }

            // 取出被撤销消息的原始内容,解析出附件列表 + 正文
            var undone = hist[lastUser].Content ?? "";
            var attachments = new List<object>();
            var body = undone;
            var prefix = ExtractAttachmentPrefix(undone);
            if (prefix != null)
            {
                foreach (var id in prefix.FileIds)
                {
                    var uf = UploadStore.Get(id);
                    if (uf == null) continue;
                    attachments.Add(new
                    {
                        id = uf.Id,
                        name = uf.OriginalName,
                        sizeText = FileParserService.FormatBytes(uf.Size),
                        sheetCount = uf.SheetCount,
                        rowCount = uf.RowCount,
                        colCount = uf.ColCount,
                        summary = uf.ParsedSummary ?? ""
                    });
                }
                body = prefix.Body;
            }

            var kept = hist.Take(lastUser).ToList();
            _loop.LoadHistory(kept);
            SaveCurrent();
            RestoreTranscriptToJs(kept);
            PostStatus("已撤销,内容已放回输入框。");

            PostJs(new { type = "restoreInput", text = body ?? "", attachments });
        }

        private sealed class AttachmentPrefixInfo
        {
            public List<string> FileIds { get; set; }
            public string Body { get; set; }
        }

        /// <summary>从用户消息文本里解析出 [已附加文件] 前缀块,返回附件 id 列表 + 纯正文。没有前缀返回 null。</summary>
        private static AttachmentPrefixInfo ExtractAttachmentPrefix(string text)
        {
            const string marker = "[\u5df2\u9644\u52a0\u6587\u4ef6]";
            const string footer = "[\u5982\u9700\u7cbe\u8bfb\u5b8c\u6574\u5185\u5bb9,\u8c03\u7528 read_uploaded_file(file_id=...)]";
            if (string.IsNullOrEmpty(text) || !text.StartsWith(marker, StringComparison.Ordinal)) return null;
            var footIdx = text.IndexOf(footer, StringComparison.Ordinal);
            if (footIdx < 0) return null;

            var head = text.Substring(marker.Length, footIdx - marker.Length);
            var body = text.Substring(footIdx + footer.Length).TrimStart('\r', '\n', ' ');

            var ids = new List<string>();
            foreach (var line in head.Split('\n'))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, @"id=([A-Za-z0-9_]+)");
                if (m.Success && !ids.Contains(m.Groups[1].Value))
                    ids.Add(m.Groups[1].Value);
            }
            return new AttachmentPrefixInfo { FileIds = ids, Body = body };
        }

        private void LoadConversation(string id)
        {
            var conv = ConversationStore.Load(id);
            if (conv == null) { StartFreshConversation(); return; }

            if (!ConversationStore.Acquire(id))
            {
                PostStatus("该对话正被另一个 TxAgent 打开，已为你新建一个。");
                StartFreshConversation();
                return;
            }

            // 切走前释放上一个，别一个进程占着一堆
            if (_current != null && !string.Equals(_current.Id, id, StringComparison.Ordinal))
            {
                try { SaveCurrent(); } catch { }
                ConversationStore.Release(_current.Id);
            }

            _current = conv;
            _baseP = conv.PromptTokens;
            _baseC = conv.CompletionTokens;
            if (_loop != null)
            {
                _loop.SetConvId(id);
                _loop.LoadHistory(conv.Messages);
            }
            RestoreTranscriptToJs(conv.Messages);
            PostTokenUsage(0, 0, 0);   // 载入后立刻把累计值推给前端
        }

        private void SaveCurrent()
        {
            if (_loop == null || _current == null) return;
            if (!ConversationStore.HasUserContent(_loop.FullHistory)) return;
            _current.Messages = new List<ChatMessage>(_loop.FullHistory);
            _current.PromptTokens = _baseP + _loop.TotalPromptTokens;
            _current.CompletionTokens = _baseC + _loop.TotalCompletionTokens;
            try { ConversationStore.Save(_current); } catch { }
        }

        /// <summary>
        /// 关闭 form 前兜底保存。
        /// 覆盖:用户点关闭按钮 / Alt+F4 / PS 正常退出。
        /// 不覆盖:PS 进程被强杀 (Task Manager 结束进程 / native 崩溃) —— 那种场景靠
        ///        AgentLoop 里"每个工具完成即 SaveCurrent"的增量保存来兜底。
        /// </summary>
        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            // 先放行所有挂起的等待,否则阻塞在 tcs.Task.Result 上的后台线程会一直悬着
            try { StopProcessSync(); } catch { }
            try { AskUserBridge.Handler = null; } catch { }
            try { ReleasePendingAskUser(null); } catch { }
            try { ReleasePendingApproval(false); } catch { }
            try { if (_cts != null) _cts.Cancel(); } catch { }

            try { SaveCurrent(); } catch { }
            base.OnFormClosing(e);
        }

        /// <summary>把消息数组序列化成 restore payload,交给 chat.html 一次性渲染。</summary>
        private void RestoreTranscriptToJs(IEnumerable<ChatMessage> messages)
        {
            if (messages == null) return;
            var msgList = new List<object>();
            var idToName = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var m in messages)
            {
                if (m == null || m.Role == "system") continue;
                if (m.Role == "user")
                {
                    msgList.Add(new { role = "user", text = m.Content ?? "" });
                }
                else if (m.Role == "assistant")
                {
                    var tcList = new List<object>();
                    if (m.ToolCalls != null)
                        foreach (var tc in m.ToolCalls)
                        {
                            var nm = tc.Function != null ? tc.Function.Name : "?";
                            if (tc.Id != null) idToName[tc.Id] = nm;
                            tcList.Add(new
                            {
                                id = tc.Id ?? "",
                                name = nm,
                                input = tc.Function != null ? Truncate(tc.Function.Arguments ?? "", 200) : ""
                            });
                        }
                    msgList.Add(new
                    {
                        role = "assistant",
                        text = m.Content ?? "",
                        reasoning = m.ReasoningContent,
                        toolCalls = tcList.Count > 0 ? tcList : null
                    });
                }
                else if (m.Role == "tool")
                {
                    string nm;
                    idToName.TryGetValue(m.ToolCallId ?? "", out nm);
                    bool isErr = m.Content != null && m.Content.StartsWith("Error", StringComparison.OrdinalIgnoreCase);
                    msgList.Add(new
                    {
                        role = "tool",
                        toolCallId = m.ToolCallId ?? "",
                        name = nm ?? "",
                        result = m.Content ?? "",
                        isErr = isErr
                    });
                }
            }
            PostJs(new { type = "restore", messages = msgList });
        }

        // ─────────────────────────────────────────────────────
        //  AgentLoop 构造 + 事件转发
        // ─────────────────────────────────────────────────────

        private IAgentLoop BuildLoop(DeepSeekClient client)
        {
            var options = new AgentOptions { Model = _currentModel };

            // 记忆系统低风险写工具免弹窗
            options.AutoApproveTools.Add("add_fact");
            options.AutoApproveTools.Add("add_gotcha_correction");

            // 统一走 harness(新 AgentLoop)驱动现有工具;旧引擎已删除。
            IAgentLoop loop = new HarnessAgentLoop(client, _tools, options);

            if (_current != null && !string.IsNullOrEmpty(_current.Id))
                loop.SetConvId(_current.Id);

            loop.AssistantDelta += frag => PostJs(new { type = "delta", text = frag });
            loop.Info += t =>
            {
                PostJs(new { type = "closeAssistant" });
                PostJs(new { type = "message", role = "\u7cfb\u7edf", text = t });
            };
            loop.ToolCalled += (name, input) =>
            {
                PostJs(new { type = "closeAssistant" });
                PostStatus("\u2699 " + name + "\u2026");
                PostJs(new { type = "toolCall", name = name, input = Compact(input) });
            };
            loop.ToolCompleted += (name, result, isErr) =>
            {
                PostJs(new { type = "toolResult", name = name, result = result, isErr = isErr });
                PostStatus("\u5c31\u7eea\u3002");
            };
            loop.ApprovalRequest = AskApproval;
            loop.AskUserRequest = AskUser;

            // ask_user 富负载通道:支持 multi_choice / form / allow_custom / multiline。
            // 每次 BuildLoop 重挂一次,避免 form 重建后残留旧实例引用。
            AskUserBridge.Handler = AskUserRich;

            // 新 harness 独有能力(旧 AgentLoop 不实现该接口,as 得到 null 自动跳过)
            var streaming = loop as IStreamingAgentLoop;
            if (streaming != null)
            {
                // 思考过程(推理模型的 reasoning_content)。
                // 普通模型不返回该字段,这三个事件根本不会触发,不影响现有行为。
                // 思考经独立归档保存，历史默认折叠；网络回传由端点兼容策略控制。
                streaming.ReasoningStarted += () => PostJs(new { type = "reasoningStart" });
                streaming.ReasoningDelta += t => PostJs(new { type = "reasoningDelta", text = t });
                streaming.ReasoningEnded += () => PostJs(new { type = "reasoningEnd" });

                // LLM 重试导致已发出的半截文本作废 —— 收尾当前气泡,
                // 让重试内容另起一条,不至于和废弃内容拼在一起。
                streaming.ContentReset += () => PostJs(new { type = "closeAssistant" });
            }
            loop.HistoryChanged += SaveCurrent;
            // 会话同步回 WorkingMemory 之后再刷一次上下文估算,否则分项永远滞后一轮
            loop.HistoryChanged += () => PostTokenUsage(
                loop.TotalPromptTokens, loop.TotalCompletionTokens, loop.TotalTokens);
            loop.TokenUsed += (p, c, t) => PostTokenUsage(loop.TotalPromptTokens, loop.TotalCompletionTokens, loop.TotalTokens);

            return loop;
        }

        private void FireExtractLessons()
        {
            var loop = _loop;
            if (loop == null) return;
            if (loop.FullHistory == null || loop.FullHistory.Count < 4) return;
            Task.Run(async () =>
            {
                try { await loop.ExtractLessonsAsync(CancellationToken.None); }
                catch { }
            });
        }

        // ─────────────────────────────────────────────────────
        //  审批 —— HTML modal 优先,原生弹窗仅作 fallback
        //
        //  同步性:ApprovalRequest 委托签名要求同步返回 bool。
        //  RunOneTool 在 AgentLoop.SendAsync 内被调用,而 SendAsync 由
        //  HandleUserSendAsync 里的 Task.Run 包起来跑,处于线程池线程 —— 因此
        //  在这里同步 tcs.Task.Result 阻塞是安全的,UI 线程不受影响,依然能
        //  接收 WebMessageReceived 触发 TrySetResult 解除阻塞。
        //
        //  两个例外场景走原生 fallback:
        //   (a) WebView 尚未就绪 (初始化早期,几乎不会遇到);
        //   (b) 已经在 UI 线程调用 (会死锁 —— UI 线程等自己发消息回自己)。
        // ─────────────────────────────────────────────────────

        private bool AskApproval(ITxAgentTool tool, JObject input)
        {
            bool isCode = input != null
                          && input["code"] != null
                          && input["code"].Type == JTokenType.String;

            // 审批模式短路(与原逻辑一致)
            if (_approvalMode == "auto_all")
            {
                AuditLog.Write("AUTO-ALL tool=" + tool.Name + "  input=" + Compact(input));
                return true;
            }
            if (_approvalMode == "auto_safe" && !isCode)
            {
                AuditLog.Write("AUTO-SAFE tool=" + tool.Name + "  input=" + Compact(input));
                return true;
            }

            // WebView 未就绪或在 UI 线程 → 兜底原生弹窗
            if (!_webViewReady || !InvokeRequired)
                return AskApprovalNative(tool, input, isCode);

            return AskApprovalHtml(tool, input, isCode);
        }

        /// <summary>
        /// 通过 HTML modal 请求审批。发消息给 JS 展示 modal → JS 用户点按钮
        /// → C# WebMessageReceived 收到 approvalResult → TrySetResult 解除本方法的阻塞。
        /// 只能在非 UI 线程调用,否则会自己等自己死锁。
        /// </summary>
        private bool AskApprovalHtml(ITxAgentTool tool, JObject input, bool isCode)
        {
            var tcs = new TaskCompletionSource<bool>();
            lock (_pendingApprovalLock)
            {
                // 如果之前有挂着的(异常情况),视为拒绝先释放,再上新的
                if (_pendingApproval != null) _pendingApproval.TrySetResult(false);
                _pendingApproval = tcs;
            }

            try
            {
                object payload;
                if (isCode)
                {
                    payload = new
                    {
                        type = "askApproval",
                        kind = "code",
                        tool = tool.Name,
                        code = (string)input["code"]
                    };
                }
                else
                {
                    payload = new
                    {
                        type = "askApproval",
                        kind = "generic",
                        tool = tool.Name,
                        description = tool.Description ?? "",
                        input = Compact(input)
                    };
                }
                PostJs(payload);

                // 同步阻塞等 JS 响应(见方法顶注释,安全前提: 我们不在 UI 线程)
                return tcs.Task.Result;
            }
            catch
            {
                return false;
            }
            finally
            {
                lock (_pendingApprovalLock)
                {
                    if (_pendingApproval == tcs) _pendingApproval = null;
                }
            }
        }

        /// <summary>解除当前挂起的审批(视为传入的 allow 结果)。多次调用幂等。</summary>
        private void ReleasePendingApproval(bool allow)
        {
            TaskCompletionSource<bool> tcs;
            lock (_pendingApprovalLock) { tcs = _pendingApproval; }
            if (tcs != null) tcs.TrySetResult(allow);
        }

        // ─────────────────────────────────────────────────────
        //  ask_user 弹出提问 —— AI 主动向用户问 confirm/choice/input
        //
        //  跟 AskApproval 同款机制:
        //    1. AI 调 ask_user 工具 → 转到 AgentLoop.AskUserRequest 委托
        //    2. TxAgentForm.AskUser 建 TaskCompletionSource<string>,PostJs 显示 modal
        //    3. 后台线程阻塞 tcs.Task.Result 等 JS 消息
        //    4. 用户点按钮/输入 → JS post askUserResponse → 主线程 ReleasePendingAskUser
        //  安全前提: RunOneTool 跑在 Task.Run 的线程池线程,不阻塞 UI 线程。
        // ─────────────────────────────────────────────────────

        private string AskUser(string question, string kind, string[] options)
        {
            if (!_webViewReady) return null;   // WebView 未就绪 —— 无法弹窗,视为取消

            var tcs = new TaskCompletionSource<string>();
            lock (_pendingAskUserLock)
            {
                if (_pendingAskUser != null) _pendingAskUser.TrySetResult(null);
                _pendingAskUser = tcs;
            }

            try
            {
                PostJs(new
                {
                    type = "askUser",
                    question = question ?? "",
                    kind = kind ?? "confirm",
                    options = options ?? new string[0]
                });
                return tcs.Task.Result;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_pendingAskUserLock)
                {
                    if (_pendingAskUser == tcs) _pendingAskUser = null;
                }
            }
        }

        /// <summary>解除当前挂起的 ask_user(视为传入答复;null=取消)。多次调用幂等。</summary>
        private void ReleasePendingAskUser(string answer)
        {
            TaskCompletionSource<string> tcs;
            lock (_pendingAskUserLock) { tcs = _pendingAskUser; }
            if (tcs != null) tcs.TrySetResult(answer);
        }

        // ─────────────────────────────────────────────────────
        //  ask_user 富负载版 —— 挂到 AskUserBridge.Handler
        //
        //  旧的 AskUser(question, kind, options) 委托签名带不动
        //  default / allowCustom / multiline / fields,
        //  而扩签名要同时改 IAgentLoop、AgentLoop、HarnessAgentLoop 三处。
        //  这里直接收 JSON 负载原样转给页面,接口一处都不用动。
        //
        //  复用 _pendingAskUser 那套 TCS 机制,页面回 askUserResponse 时一起解除。
        //  抛异常不会导致整轮失败 —— AskUserTool 捕获后会降级到内置 WinForms 对话框。
        // ─────────────────────────────────────────────────────

        private string AskUserRich(string payloadJson)
        {
            if (!_webViewReady)
                throw new InvalidOperationException("WebView \u672a\u5c31\u7eea");

            // 安全网:本方法必须在后台线程执行。若跑在 UI 线程,
            // 下面的 tcs.Task.Result 会把 UI 线程占死,用户的点击永远派发不到 ——
            // 就是之前"一直等待结果、连关闭按钮都点不了"的成因。
            // 抛出后 AskUserTool 会降级到内置对话框,不至于卡死。
            if (!InvokeRequired)
                throw new InvalidOperationException(
                    "ask_user \u4e0d\u5f97\u5728 UI \u7ebf\u7a0b\u963b\u585e\u7b49\u5f85");

            JObject payload;
            try { payload = JObject.Parse(payloadJson ?? "{}"); }
            catch (Exception ex)
            {
                throw new ArgumentException("ask_user \u8d1f\u8f7d\u4e0d\u662f\u5408\u6cd5 JSON: " + ex.Message);
            }

            payload["type"] = "askUser";

            var tcs = new TaskCompletionSource<string>();
            lock (_pendingAskUserLock)
            {
                if (_pendingAskUser != null) _pendingAskUser.TrySetResult(null);
                _pendingAskUser = tcs;
            }

            try
            {
                PostJs(payload);          // 内部 BeginInvoke 异步投递,不占 UI 线程
                return tcs.Task.Result;   // 在当前(后台)线程等待
            }
            finally
            {
                lock (_pendingAskUserLock)
                {
                    if (_pendingAskUser == tcs) _pendingAskUser = null;
                }
            }
        }

        /// <summary>原生弹窗兜底 —— WebView 未就绪或已在 UI 线程时用。</summary>
        private bool AskApprovalNative(ITxAgentTool tool, JObject input, bool isCode)
        {
            if (InvokeRequired)
                return (bool)Invoke(new Func<bool>(() => AskApprovalNative(tool, input, isCode)));

            if (isCode)
                return CodeApprovalDialog.Show(this, tool.Name, (string)input["code"]) == DialogResult.Yes;

            var msg = "\u52a9\u624b\u8bf7\u6c42\u6267\u884c\u4e00\u4e2a\u4f1a\u6539\u52a8\u573a\u666f\u7684\u64cd\u4f5c\uff1a\n\n" +
                      "\u5de5\u5177: " + tool.Name + "\n\u53c2\u6570: " + Compact(input) + "\n\n\u662f\u5426\u5141\u8bb8\uff1f";
            return MessageBox.Show(this, msg, "\u64cd\u4f5c\u786e\u8ba4", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                   == DialogResult.Yes;
        }

        // ─────────────────────────────────────────────────────
        //  小工具
        // ─────────────────────────────────────────────────────

        private static string Compact(JObject input)
        {
            if (input == null) return "{}";
            var s = JsonConvert.SerializeObject(input);
            return s.Length <= 200 ? s : s.Substring(0, 200) + "\u2026";
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "\u2026";
        }

        // ─────────────────────────────────────────────────────
        //  <PROTOCOL> 通信协议 v2 参考
        //
        //  JS → C# (postMessage JSON string):
        //    { type:"jsReady" }
        //    { type:"setApiKey", key }
        //    { type:"switchModel", model }
        //    { type:"setApprovalMode", mode:"ask"|"auto_safe"|"auto_all" }
        //    { type:"userSend", text, attachments:[{id,name}] }
        //    { type:"userStop" }
        //    { type:"newConv" }
        //    { type:"listConvs" }
        //    { type:"openConv", id }
        //    { type:"deleteConv", id }
        //    { type:"uploadFile", filename, contentBase64 }
        //    { type:"removeAttachment", id }
        //    { type:"approvalResult", allow }
        //    { type:"extractLessons" }
        //
        //  C# → JS (dispatchMessage(...)):
        //    { type:"modelList", items, current }
        //    { type:"keyReady" }
        //    { type:"askApiKey", reason }
        //    { type:"askApproval", kind:"code"|"generic", tool, code?, description?, input? }
        //    { type:"convList", items:[{id,title,updated}] }
        //    { type:"clear" }
        //    { type:"restore", messages:[...] }
        //    { type:"message", role, text }
        //    { type:"delta", text }
        //    { type:"closeAssistant" }
        //    { type:"toolCall", name, input }
        //    { type:"toolResult", name, result, isErr }
        //    { type:"status", text }
        //    { type:"busy", value }
        //    { type:"tokenUsage", prompt, completion, total }
        //    { type:"attachmentInfo", id?, name?, extension?, size?, sizeText?, rowCount?, colCount?, sheetCount?, summary?, error? }
        // </PROTOCOL>
    }
}
