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
using TxTools.Common;   // FormUiKit

namespace TxTools.Agent.UI
{
    public sealed class TxAgentForm : TxForm
    {
        private static readonly System.Drawing.Size DesignSize = new System.Drawing.Size(560, 720);

        // ── 依赖 ──
        private readonly ToolRegistry _tools;
        private DeepSeekClient _client;
        private AgentLoop _loop;
        private CancellationTokenSource _cts;
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

        // ── WebView2 ──
        private WebView2 _webView;
        private bool _webViewReady;
        private bool _dpiApplied;

        // ── 加载覆盖 (WebView 初始化期间遮盖空白,防止用户以为界面卡死) ──
        private System.Windows.Forms.Panel _loadingOverlay;
        private System.Windows.Forms.Label _loadingLabel;
        private System.Windows.Forms.Timer _loadingTimer;
        private int _loadingDotCount;

        public TxAgentForm(SynchronizationContext psCtx, ToolRegistry tools)
        {
            _tools = tools;

            FormUiKit.InitStandardForm(this,
                "TxTools.Agent \u2014 PDPS AI \u52a9\u624b (DeepSeek)",
                DesignSize, new System.Drawing.Size(420, 480), sizable: true);

            // TxForm 默认半模态,会挡住其它窗口;关掉才是真正的非模态
            try { SemiModal = false; } catch { }
            try
            {
                var flatStyleProp = this.GetType().GetProperty("FlatStyleEnabled");
                if (flatStyleProp != null && flatStyleProp.CanWrite)
                    flatStyleProp.SetValue(this, false, null);
            }
            catch { }

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
                Font = new System.Drawing.Font("Microsoft YaHei UI", 11f),
                ForeColor = System.Drawing.Color.FromArgb(120, 120, 120),
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
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FormUiKit.ApplyDpiScaling(this, ref _dpiApplied, DesignSize);
            _loadingTimer.Start();
            InitWebViewAsync();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 若正等审批,视为拒绝解除阻塞,让后台线程能退出
            ReleasePendingApproval(false);
            FireExtractLessons();                // 关窗前对当前对话跑一次经验萃取
            try { UploadStore.ClearAll(); } catch { }
            AgentLoop.Current = null;
            base.OnFormClosed(e);
        }

        // ─────────────────────────────────────────────────────
        //  WebView2 初始化 + HTML 加载
        // ─────────────────────────────────────────────────────

        private async void InitWebViewAsync()
        {
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
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
            var asm = typeof(TxAgentForm).Assembly;
            string resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("chat.html", StringComparison.OrdinalIgnoreCase));
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
                            ApplyKey(newKey, persist: true);
                            break;
                        }

                    case "switchModel":
                        SwitchModel((string)msg["model"]);
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

                    case "userSend":
                        _ = HandleUserSendAsync((string)msg["text"], msg["attachments"] as JArray);
                        break;

                    case "userStop":
                        try { if (_cts != null) _cts.Cancel(); } catch { }
                        // 若此时正等待审批,视为拒绝解除阻塞,让 SendAsync 尽快返回
                        ReleasePendingApproval(false);
                        break;

                    case "approvalResult":
                        {
                            bool allow = msg["allow"] != null && (bool)msg["allow"];
                            ReleasePendingApproval(allow);
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
            foreach (var p in LlmProviders.All)
            {
                providers.Add(new
                {
                    id = p.Id,
                    displayName = p.DisplayName,
                    baseUrl = p.BaseUrl,
                    isLocal = p.IsLocal,
                    keyPageUrl = p.KeyPageUrl,
                    models = p.Models
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
        private void PostTokenUsage(int p, int c, int t) { PostJs(new { type = "tokenUsage", prompt = p, completion = c, total = t }); }

        private void PostConvList()
        {
            List<ConversationMeta> metas;
            try { metas = ConversationStore.List(); }
            catch { metas = new List<ConversationMeta>(); }

            var items = metas.Select(m => new
            {
                id = m.Id,
                title = string.IsNullOrEmpty(m.Title) ? "(\u65e0\u6807\u9898)" : m.Title,
                updated = m.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
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
                            + models.Count + " \u4e2a)");
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
        private void SwitchModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return;

            var targetProv = LlmProviders.FindByModel(model);
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

            if (_client == null) { PostStatus("\u5df2\u9884\u9009\u6a21\u578b: " + model); return; }
            _loop = BuildLoop(_client);
            _loop.LoadHistory(_current != null ? _current.Messages : new List<ChatMessage>());
            if (_current != null) RestoreTranscriptToJs(_current.Messages);
            try { UserPrefsStore.UpdateChoice(_currentProviderId, _currentModel); } catch { }
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
            if (metas.Count > 0) LoadConversation(metas[0].Id);
            else StartFreshConversation();
        }

        private void StartFreshConversation()
        {
            _current = new Conversation { Id = ConversationStore.NewId(), CreatedUtc = DateTime.UtcNow };
            if (_loop != null)
            {
                _loop.SetConvId(_current.Id);
                _loop.Reset();
            }
            PostJs(new { type = "clear" });
            PostJs(new { type = "message", role = "\u7cfb\u7edf", text = "\u5df2\u5c31\u7eea\uff0c\u53ef\u4ee5\u5f00\u59cb\u5bf9\u8bdd\u3002" });
        }

        private void LoadConversation(string id)
        {
            var conv = ConversationStore.Load(id);
            if (conv == null) { StartFreshConversation(); return; }
            _current = conv;
            if (_loop != null)
            {
                _loop.SetConvId(id);
                _loop.LoadHistory(conv.Messages);
            }
            RestoreTranscriptToJs(conv.Messages);
        }

        private void SaveCurrent()
        {
            if (_loop == null || _current == null) return;
            if (!ConversationStore.HasUserContent(_loop.FullHistory)) return;
            _current.Messages = new List<ChatMessage>(_loop.FullHistory);
            try { ConversationStore.Save(_current); } catch { }
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

        private AgentLoop BuildLoop(DeepSeekClient client)
        {
            var options = new AgentOptions { Model = _currentModel };

            // 记忆系统低风险写工具免弹窗
            options.AutoApproveTools.Add("add_fact");
            options.AutoApproveTools.Add("add_gotcha_correction");

            var loop = new AgentLoop(client, _tools, options);
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
            loop.HistoryChanged += SaveCurrent;
            loop.TokenUsed += (p, c, t) => PostTokenUsage(loop.TotalPromptTokens, loop.TotalCompletionTokens, loop.TotalTokens);

            AgentLoop.Current = loop;
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
