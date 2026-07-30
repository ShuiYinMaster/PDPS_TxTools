// TxTools.Agent / Core / Harness / HarnessAgentLoop.cs
// 新 harness(TxAgent.Core.AgentLoop)与旧 UI(TxAgentForm)之间的桥。
//
// 本版变化:
//   • Progress 不再转发到 Info(聊天流) —— 它和 ToolStarting 产生的工具卡片是同一批事件,
//     转发会在会话里刷出一堆重复的灰色"执行 xxx / 正在建立回滚点…"。
//     Progress 现在只写审计日志,属状态栏/日志通道。
//   • 工具事件由 Core 的 ToolStarting / ToolFinished 实时抛出(执行前就出卡片)。
//   • 正文由 Core 的 ContentDelta 实时发出,不再事后补演 FinalMessage。
//   • IStreamingAgentLoop:思考内容(reasoning_content)流 + 重试重置。
//   • token 用量上报(旧版 TotalPromptTokens 一直是 0)。
//
// 保留:P1 记忆注入 / P4 Snippet / P5 历史压缩 / P6 经验萃取。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TxAgent.Core;        // AgentSession / AgentLoop / AgentLoopOptions / AgentRunResult / ITool / IAgentHost
using TxTools.Agent.Core; // ChatMessage / ITxAgentTool / ToolRegistry / AgentOptions / DeepSeekClient / LessonExtractor

// 两个命名空间都定义了 ChatMessage / ToolCall,用别名消歧义:
//   本文件里裸名 ChatMessage / ToolCall 指旧格式(TxTools.Agent.Core)侧;harness 侧一律用 TxAgent.Core. 全限定。
using ChatMessage = TxTools.Agent.Core.ChatMessage;
using ToolCall = TxTools.Agent.Core.ToolCall;

namespace TxTools.Agent.Harness
{
    public sealed class HarnessAgentLoop : IAgentLoop, IStreamingAgentLoop
    {
        // [P2] 静态入口:供 AskUserTool / 记忆工具获取当前 harness 实例的 AskUserRequest/convId
        public static HarnessAgentLoop Current { get; private set; }

        private readonly DeepSeekClient _client;
        private readonly TxTools.Agent.Core.ToolRegistry _tools;
        private readonly AgentOptions _options;
        private readonly PsAgentHost _host;
        private readonly TxAgent.Core.ToolRegistry _harnessReg;
        private readonly DeepSeekLlmClient _llm;

        private List<ChatMessage> _fullHistory = new List<ChatMessage>();
        private List<ChatMessage> _workingMemory = new List<ChatMessage>();
        private string _currentConvId;

        // 本轮思考段落状态,用于触发 ReasoningStarted / ReasoningEnded
        private bool _inReasoning;

        // ── IAgentLoop 表面 ──

        public event Action<string> AssistantText;
        public event Action<string> AssistantDelta;
        public event Action<string, JObject> ToolCalled;
        public event Action<string, string, bool> ToolCompleted;
        public event Action<string> Info;
        public event Action HistoryChanged;
        public event Action<int, int, int> TokenUsed;

        // ── IStreamingAgentLoop 表面 ──

        public event Action<string> ReasoningDelta;
        public event Action ReasoningStarted;
        public event Action ReasoningEnded;
        public event Action ContentReset;

        /// <summary>是否真的在走 token 级流式。由底层 LLM 客户端能力决定。</summary>
        public bool StreamingActive
        {
            get { return _llm != null && _llm.SupportsStreaming; }
        }

        private Func<ITxAgentTool, JObject, bool> _approvalRequest;
        private Func<string, string, string[], string> _askUserRequest;

        public Func<ITxAgentTool, JObject, bool> ApprovalRequest
        {
            get { return _approvalRequest; }
            set
            {
                _approvalRequest = value;
                if (value != null)
                    _host.ConfirmRequest = (name, input) => InvokeApproval(name, input);
                else
                    _host.ConfirmRequest = null;
            }
        }

        public Func<string, string, string[], string> AskUserRequest
        {
            get { return _askUserRequest; }
            set { _askUserRequest = value; }
        }

        public IReadOnlyList<ChatMessage> FullHistory { get { return _fullHistory; } }
        public IReadOnlyList<ChatMessage> WorkingMemory { get { return _workingMemory; } }

        public string CurrentConvId { get { return _currentConvId; } }
        public int TotalPromptTokens { get; private set; }
        public int TotalCompletionTokens { get; private set; }
        public int TotalTokens { get { return TotalPromptTokens + TotalCompletionTokens; } }

        public HarnessAgentLoop(DeepSeekClient client, TxTools.Agent.Core.ToolRegistry tools, AgentOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _options = options ?? new AgentOptions();

            _host = new PsAgentHost(SynchronizationContext.Current);
            if (_options.AutoApproveTools != null)
                _host.AutoApproveTools.UnionWith(_options.AutoApproveTools); // setter 私有,拷贝条目而非替换引用

            _llm = new DeepSeekLlmClient(_client, _options.Model);

            // 诊断 Newtonsoft.Json 版本冲突:PS 宿主 bin 自带一份,强名称绑定会顶掉插件引用的 13.x。
            try
            {
                var asm = typeof(Newtonsoft.Json.JsonConvert).Assembly;
                TxTools.Agent.Core.AuditLog.Write(
                    "[info] [TxAgent.Harness] Newtonsoft.Json 实际加载: "
                    + asm.GetName().Version + " @ " + asm.Location);
            }
            catch { }

            // 从持久化设置恢复工具组开关(没有保存过则用 ToolGate 代码默认值)
            TxTools.Agent.Core.ToolGate.RestoreFromPrefs();

            // 把所有现有工具包成 ITool(事件由 Core 直接抛,不需要 Tracing 装饰器)
            // 按 ToolGate 组过滤:未启用组的工具不注册,模型看不到、也不占 prompt 前缀。
            _harnessReg = new TxAgent.Core.ToolRegistry();
            foreach (var t in _tools.Tools)
            {
                if (t == null) continue;
                if (!TxTools.Agent.Core.ToolGate.ShouldExpose(t.Name))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TxAgent.Harness] 工具被 ToolGate 挡下(组未启用): " + (t.Name ?? "?"));
                    continue;
                }
                var adapter = new TxAgentToolAdapter(t, _host, () => _currentConvId); // [P3] 传入 convId 供 AutoGotcha
                try { _harnessReg.Register(adapter); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TxAgent.Harness] 注册工具失败,跳过: " + (t.Name ?? "?") + " -> " + ex.Message);
                }
            }

            Reset();

            // [P2] 注册静态入口,供 AskUserTool / 记忆工具获取 convId/AskUserRequest
            Current = this;
        }

        // ── 生命周期 ──

        public void SetConvId(string convId)
        {
            _currentConvId = convId;
        }

        public void Reset()
        {
            _fullHistory = new List<ChatMessage>();
            _workingMemory = new List<ChatMessage>();
            // [P1] 注入 Facts + Gotchas 到系统提示(复用旧引擎的 BuildSystemPromptWithMemory)
            // 换对话 → 让系统提示词重新拉一次记忆(会话内则固定,保住前缀缓存)
            TxTools.Agent.Core.AgentLoop.InvalidateSystemPromptCache();
            var sysPrompt = TxTools.Agent.Core.AgentLoop.BuildSystemPromptWithMemory();

            // 工具组开关说明:告诉模型哪些能力当前被禁用,避免它反复尝试不可用工具
            var gateNote = TxTools.Agent.Core.ToolGate.DescribeDisabled();
            if (!string.IsNullOrEmpty(gateNote))
                sysPrompt = sysPrompt.TrimEnd() + "\n\n" + gateNote;

            var sys = new ChatMessage("system", sysPrompt);
            _fullHistory.Add(sys);
            _workingMemory.Add(sys);
        }

        public void LoadHistory(IEnumerable<ChatMessage> msgs)
        {
            _fullHistory = new List<ChatMessage>();
            _workingMemory = new List<ChatMessage>();
            if (msgs != null)
            {
                foreach (var m in msgs)
                {
                    if (m == null) continue;
                    _fullHistory.Add(m);
                    _workingMemory.Add(m);
                }
            }
            // [P1] 加载历史后,若首条是 system 消息,替换为含 Facts+Gotchas 的最新版本
            if (_workingMemory.Count > 0 && _workingMemory[0].Role == "system")
            {
                _workingMemory[0] = new ChatMessage("system", TxTools.Agent.Core.AgentLoop.BuildSystemPromptWithMemory());
                _fullHistory[0] = _workingMemory[0];
            }
            // 旧版本残留在归档里的临时片段块,载入时一并清掉(单条可达 6KB)
            PurgeEphemeral(_workingMemory);
            PurgeEphemeral(_fullHistory);

            if (_workingMemory.Count == 0) Reset();
        }

        public async Task SendAsync(string userText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userText)) return;

            // 1) 追加用户输入(旧格式,供持久化/还原)
            var userMsg = new ChatMessage("user", userText);
            _workingMemory.Add(userMsg);
            _fullHistory.Add(userMsg);
            RaiseHistoryChanged();

            // [P4] 按需注入 Top-3 相关 Snippet(只进工作记忆,本轮临时上下文)
            ChatMessage snippetSysMsg = InjectRelevantSnippets(userText);

            _inReasoning = false;

            // 会话与基线提到 try 外面 —— 用户中途点停止时,
            // RunAsync 会抛 OperationCanceledException,若这两个变量在 try 内声明,
            // finally 里就拿不到它们,已经产生的助手回复和工具结果会连同异常一起丢掉。
            AgentSession session = null;
            int baseCount = 0;

            try
            {
                // [P5] 历史压缩:超轮数时把旧消息压缩为摘要
                CompressHistory();

                // 2) 用当前工作记忆重建 harness 会话(每次重建,harness 自行裁剪)
                session = BuildSessionFromHistory();

                // 上下文预算按模型窗口的 70% 动态设定。
                // 写死 48000 的话:窗口大的模型白白浪费,窗口小的模型根本挡不住 ——
                // 而工具输出(一次场景 dump 可达 3 万 token)累积极快,不设边界必然溢出。
                session.TokenBudget = (int)(ModelRouter.ContextWindowFor(_options.Model, ModelRouter.CurrentProviderId) * 0.70);

                // 归档基线:本轮开始时会话里已有的消息数。
                // RunAsync 之后,索引 >= baseCount 的才是本轮新产生的 assistant/tool 消息,
                // 只把这些追加进 _fullHistory —— 基线之前的内容是压缩后的工作记忆,
                // 拿它覆盖归档会把原始对话物理销毁。
                baseCount = session.Messages.Count;

                // 3) 组装并订阅 harness 循环
                var loopOptions = new AgentLoopOptions
                {
                    MaxIterations = Math.Max(1, _options.MaxIterations),
                    MaxTokens = OutputBudgetFor(_options.Model),
                    ReadOnlyPhase = false,
                    AutoRestorePoint = true,
                    EnableStreaming = true
                };

                var loop = new TxAgent.Core.AgentLoop(_llm, _harnessReg, _host, loopOptions);

                loop.Progress += OnProgress;
                loop.ContentDelta += OnContentDelta;
                loop.ReasoningDelta += OnReasoningDelta;
                loop.ContentReset += OnContentReset;
                loop.TurnCompleted += OnTurnCompleted;
                loop.ToolStarting += OnToolStarting;
                loop.ToolFinished += OnToolFinished;
                loop.Usage += OnUsage;

                AgentRunResult result;
                try
                {
                    result = await loop.RunAsync(session, ct);
                }
                finally
                {
                    loop.Progress -= OnProgress;
                    loop.ContentDelta -= OnContentDelta;
                    loop.ReasoningDelta -= OnReasoningDelta;
                    loop.ContentReset -= OnContentReset;
                    loop.TurnCompleted -= OnTurnCompleted;
                    loop.ToolStarting -= OnToolStarting;
                    loop.ToolFinished -= OnToolFinished;
                    loop.Usage -= OnUsage;

                    EndReasoningIfNeeded();
                }

                // 4) 归档同步在下方 finally 里统一做 —— 取消路径也要走到

                // 5) 正文已由 ContentDelta 实时发出,这里只补一次完整文本事件供需要整段的订阅方
                if (!string.IsNullOrEmpty(result.FinalMessage))
                {
                    var at = AssistantText;
                    if (at != null) { try { at(result.FinalMessage); } catch { } }
                }

                // 6) 只有异常结束才打扰用户;正常结束不往聊天流写任何状态文字
                if (!result.Completed)
                {
                    var info = Info;
                    if (info != null) info("任务未正常结束: " + (result.StopReason ?? "未知原因"));
                }
            }
            finally
            {
                // 【无论正常结束、报错还是用户点停止,都要把本轮已产生的消息归档】
                // 中途停止时,模型此前说过的话、调过的工具结果都已经在 session 里,
                // 不同步的话界面上看得见、重开对话却没了。
                if (session != null)
                {
                    try { SyncHistoryFromSession(session, baseCount); }
                    catch (Exception ex)
                    {
                        try { AuditLog.Write("[warn] [Harness] 归档同步失败: " + ex.Message); } catch { }
                    }
                }

                // [P4] 清掉本轮临时注入的 Snippet 消息。
                // 注意不能用 Remove(snippetSysMsg) —— SyncHistoryFromSession 已经把
                // _workingMemory 换成了新 List,按对象引用删是删不掉的,
                // 旧版就是因此让 6KB 的片段块永久堆积在历史里。改为按内容前缀过滤。
                if (snippetSysMsg != null) PurgeEphemeral(_workingMemory);
            }
        }

        // ── Core 事件 -> IAgentLoop 事件 ──

        /// <summary>
        /// Progress 是日志/状态栏通道,不是聊天流。
        /// 转发到 Info 会和工具卡片重复,在会话里刷出一堆灰色的"执行 xxx"。
        /// </summary>
        private void OnProgress(string text)
        {
            _host.Log("info", "[harness] " + text);
        }

        private void OnContentDelta(string text)
        {
            // 正文开始 = 思考段结束
            EndReasoningIfNeeded();
            RaiseAssistantDelta(text);
        }

        private void OnReasoningDelta(string text)
        {
            if (!_inReasoning)
            {
                _inReasoning = true;
                var s = ReasoningStarted;
                if (s != null) { try { s(); } catch { } }
            }
            var h = ReasoningDelta;
            if (h != null) { try { h(text); } catch { } }
        }

        private void OnContentReset()
        {
            EndReasoningIfNeeded();
            var h = ContentReset;
            if (h != null) { try { h(); } catch { } }
        }

        private void OnTurnCompleted(string text)
        {
            // 一轮结束(可能后面还要调工具),思考段一定已经结束
            EndReasoningIfNeeded();
        }

        private void OnToolStarting(TxAgent.Core.ToolCall call)
        {
            EndReasoningIfNeeded();

            JObject input = null;
            try
            {
                if (!string.IsNullOrEmpty(call.ArgumentsJson))
                    input = JObject.Parse(call.ArgumentsJson);
            }
            catch { }

            var h = ToolCalled;
            if (h != null) { try { h(call.Name, input); } catch { } }
        }

        private void OnToolFinished(TxAgent.Core.ToolCall call, TxAgent.Core.ToolResult r)
        {
            var h = ToolCompleted;
            if (h != null) { try { h(call.Name, r.Content ?? string.Empty, !r.Success); } catch { } }
        }

        private void OnUsage(int prompt, int completion)
        {
            TotalPromptTokens += prompt;
            TotalCompletionTokens += completion;
            var h = TokenUsed;
            if (h != null) { try { h(TotalPromptTokens, TotalCompletionTokens, TotalTokens); } catch { } }
        }

        private void EndReasoningIfNeeded()
        {
            if (!_inReasoning) return;
            _inReasoning = false;
            var e = ReasoningEnded;
            if (e != null) { try { e(); } catch { } }
        }

        public async Task<LessonExtractor.ExtractResult> ExtractLessonsAsync(CancellationToken ct)
        {
            // [P6] 复用旧引擎的 LessonExtractor 做经验萃取
            var extractor = new LessonExtractor(_client, "deepseek-v4-flash");
            return await extractor.ExtractAsync(_currentConvId, _fullHistory, ct);
        }

        // ── [P4] 按需 Snippet 注入(复用旧引擎逻辑) ──

        /// <summary>
        /// 根据本轮用户消息即时召回 Top-3 相关 Snippet,以独立 system 消息插入工作记忆。
        /// 返回插入的消息对象,供 SendAsync finally 里移除。无命中返回 null。
        /// 注意:只加到 _workingMemory,不加 _fullHistory —— 本轮临时上下文,不进永久历史。
        /// </summary>
        private ChatMessage InjectRelevantSnippets(string userText)
        {
            List<TxTools.Agent.Core.Snippet> snippets;
            try { snippets = TxTools.Agent.Core.SnippetStore.FindByTagOrKeyword(userText).Take(3).ToList(); }
            catch { return null; }

            if (snippets == null || snippets.Count == 0) return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(SNIPPET_PREFIX + "以下是与用户当前问题匹配的已验证 run_csharp 代码。" +
                          "若需求相近可直接引用或改写,不必再从零摸索:");
            foreach (var s in snippets)
            {
                var tagStr = s.Tags != null && s.Tags.Count > 0
                    ? "[" + string.Join(",", s.Tags) + "]" : "";
                sb.AppendLine();
                sb.AppendLine("--- " + s.Name + " " + tagStr
                    + " (复用 " + s.SuccessCount + " 次) ---");
                if (!string.IsNullOrEmpty(s.Description)) sb.AppendLine(s.Description);
                sb.AppendLine("```csharp");
                sb.AppendLine(s.Code);
                sb.AppendLine("```");
            }

            var msg = new ChatMessage("system", sb.ToString());
            _workingMemory.Add(msg);
            return msg;
        }

        // ── [P5] 回合级历史压缩(复用旧引擎逻辑) ──

        private const string SUMMARY_PREFIX = "[前序对话摘要] ";

        /// <summary>临时注入的代码片段块的识别前缀。改这里要同步改 InjectRelevantSnippets。</summary>
        private const string SNIPPET_PREFIX = "【本轮相关代码片段】";

        private void CompressHistory()
        {
            int keepTurns = _options.MaxTurnsToKeep;
            if (keepTurns <= 0) return;
            if (_workingMemory.Count <= 2) return;

            bool hasSys = _workingMemory.Count > 0 && _workingMemory[0].Role == "system";
            int startIdx = hasSys ? 1 : 0;

            string prevSummary = "";
            int summaryUserIdx = -1;
            int summaryAsstIdx = -1;
            for (int i = startIdx; i < _workingMemory.Count - 1; i++)
            {
                if (_workingMemory[i].Role == "user"
                    && _workingMemory[i].Content != null
                    && _workingMemory[i].Content.StartsWith(SUMMARY_PREFIX))
                {
                    summaryUserIdx = i;
                    prevSummary = _workingMemory[i].Content.Substring(SUMMARY_PREFIX.Length);
                    if (i + 1 < _workingMemory.Count && _workingMemory[i + 1].Role == "assistant")
                        summaryAsstIdx = i + 1;
                    break;
                }
            }

            var clean = new List<ChatMessage>();
            if (hasSys) clean.Add(_workingMemory[0]);
            for (int i = startIdx; i < _workingMemory.Count; i++)
            {
                if (i == summaryUserIdx || i == summaryAsstIdx) continue;
                clean.Add(_workingMemory[i]);
            }

            var turnStarts = new List<int>();
            int cleanStart = hasSys ? 1 : 0;
            for (int i = cleanStart; i < clean.Count; i++)
                if (clean[i].Role == "user") turnStarts.Add(i);

            if (turnStarts.Count <= keepTurns) return;

            int keepFrom = turnStarts[turnStarts.Count - keepTurns];

            var toCompress = new List<ChatMessage>();
            for (int i = cleanStart; i < keepFrom; i++)
                toCompress.Add(clean[i]);

            string newPart = GenerateTurnSummary(toCompress);
            string merged = prevSummary.Length > 0
                ? prevSummary + "\n---\n(后续) " + newPart
                : newPart;
            if (merged.Length > 1200)
                merged = merged.Substring(0, 1200) + "\n...(更多历史省略)";

            var rebuilt = new List<ChatMessage>();
            if (hasSys) rebuilt.Add(_workingMemory[0]);
            rebuilt.Add(new ChatMessage("user", SUMMARY_PREFIX + merged));
            rebuilt.Add(new ChatMessage("assistant", "[确认] 已了解前序对话内容,基于以上上下文继续当前任务。"));
            for (int i = keepFrom; i < clean.Count; i++)
                rebuilt.Add(clean[i]);

            _workingMemory.Clear();
            _workingMemory.AddRange(rebuilt);
        }

        private static string GenerateTurnSummary(List<ChatMessage> msgs)
        {
            if (msgs == null || msgs.Count == 0) return "";
            var sb = new System.Text.StringBuilder();

            var subTurns = new List<List<ChatMessage>>();
            var cur = new List<ChatMessage>();
            foreach (var m in msgs)
            {
                if (m.Role == "user" && cur.Count > 0)
                {
                    subTurns.Add(cur);
                    cur = new List<ChatMessage>();
                }
                cur.Add(m);
            }
            if (cur.Count > 0) subTurns.Add(cur);

            foreach (var sub in subTurns)
            {
                var userMsg = sub.FirstOrDefault(m2 => m2.Role == "user");
                if (userMsg != null && userMsg.Content != null)
                    sb.AppendLine("用户: " + Truncate(userMsg.Content, 100));

                var calledTools = new List<string>();
                foreach (var m in sub)
                    if (m.Role == "assistant" && m.ToolCalls != null)
                        foreach (var tc in m.ToolCalls)
                            calledTools.Add(tc.Function != null ? tc.Function.Name : "?");
                if (calledTools.Count > 0)
                    sb.AppendLine("  调用: " + string.Join(" -> ", calledTools));

                var results = new List<ChatMessage>();
                foreach (var m in sub)
                    if (m.Role == "tool" && m.Content != null) results.Add(m);
                if (results.Count > 0)
                {
                    var keyInfo = new List<string>();
                    int take = Math.Min(results.Count, 5);
                    for (int ri = 0; ri < take; ri++)
                        keyInfo.Add(Truncate(ExtractKeyInfo(results[ri].Content), 70));
                    sb.AppendLine("  结果: " + string.Join("; ", keyInfo));
                }

                var conclusions = new List<ChatMessage>();
                foreach (var m in sub)
                    if (m.Role == "assistant"
                        && (m.ToolCalls == null || m.ToolCalls.Count == 0)
                        && m.Content != null && m.Content.Length > 0)
                        conclusions.Add(m);
                if (conclusions.Count > 0)
                    sb.AppendLine("  结论: " + Truncate(conclusions[conclusions.Count - 1].Content, 120));
            }

            return sb.ToString();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", "").Replace("\n", " ").Trim();
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "...";
        }

        private static string ExtractKeyInfo(string content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            content = content.Replace("\r", "");
            int nl = content.IndexOf('\n');
            if (nl < 0) return content.Trim();
            var first = content.Substring(0, nl).Trim();
            if (first.Length < 30)
            {
                int nl2 = content.IndexOf('\n', nl + 1);
                if (nl2 > nl)
                    return first + " " + content.Substring(nl + 1, nl2 - nl - 1).Trim();
            }
            return first;
        }

        /// <summary>
        /// 单次响应的输出预算。
        ///
        /// 推理模型的思考链计入这个预算 —— 实测一次 7000 字的手工矩阵推导就要 5000+ token。
        /// 给小了会在思考中途被截断,返回 content 和 tool_calls 全空,
        /// 表现为"任务未正常结束",而真正的原因(finish_reason=length)完全看不出来。
        /// 给大不花钱:只按实际生成量计费,预算只是上限。
        /// 【别贪大】模型陷入重复循环时,预算有多大就烧多少 ——
        /// 实测 32768 让一次退化烧掉一万多 token。12k 对思考链已经够用。
        /// </summary>
        private static int OutputBudgetFor(string model)
        {
            var m = (model ?? "").ToLowerInvariant();

            // 支持超长输出的新一代模型
            if (m.Contains("deepseek-v4") || m.Contains("v4-flash") || m.Contains("v4-pro")) return 12288;
            if (m.Contains("kimi-k3") || m.Contains("kimi-k2")) return 12288;
            if (m.Contains("qwen3")) return 12288;

            return 8192;
        }

        // ── 会话 <-> 历史 互转 ──

        private AgentSession BuildSessionFromHistory()
        {
            var session = new AgentSession(null);
            foreach (var m in _workingMemory)
                session.Add(TranslateToHarness(m));
            return session;
        }

        /// <summary>
        /// 把 harness 会话同步回旧格式历史。
        ///
        /// 【职责必须分开】
        ///   _workingMemory —— 喂给 LLM 的上下文,可被压缩、可注入临时内容,随时重建。
        ///   _fullHistory   —— 归档,只增不改,SaveCurrent 会把它落盘。
        ///
        /// 旧版把两者写成同一份(_fullHistory = new List(all)),而 all 来自压缩过的
        /// 工作记忆,于是每压缩一次,磁盘上的原始对话就被摘要覆盖一次 ——
        /// 不是"检索不到",是内容真的没了,search_past_conversations 自然搜不出东西。
        /// </summary>
        private void SyncHistoryFromSession(AgentSession session, int baseCount)
        {
            var all = new List<ChatMessage>(session.Messages.Count);
            foreach (var hm in session.Messages)
                all.Add(TranslateBack(hm));

            // 工作记忆:整份接过来,但剔除本轮临时注入的内容
            _workingMemory = new List<ChatMessage>(all);
            PurgeEphemeral(_workingMemory);

            // 归档:只追加本轮新产生的消息(assistant / tool),
            // 基线之前的是压缩产物,不能进归档
            if (baseCount < 0) baseCount = 0;
            for (int i = baseCount; i < all.Count; i++)
            {
                var m = all[i];
                if (IsEphemeral(m)) continue;
                _fullHistory.Add(m);
            }

            RaiseHistoryChanged();
        }

        /// <summary>本轮临时注入、不该进归档也不该跨轮残留的消息。</summary>
        private static bool IsEphemeral(ChatMessage m)
        {
            if (m == null) return true;
            if (m.Role != "system") return false;
            if (string.IsNullOrEmpty(m.Content)) return false;
            return m.Content.StartsWith(SNIPPET_PREFIX, StringComparison.Ordinal);
        }

        private static void PurgeEphemeral(List<ChatMessage> list)
        {
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
                if (IsEphemeral(list[i])) list.RemoveAt(i);
        }

        private static TxAgent.Core.ChatMessage TranslateToHarness(ChatMessage m)
        {
            var hm = new TxAgent.Core.ChatMessage
            {
                Role = ToHarnessRole(m.Role),
                Content = m.Content,
                ToolCallId = m.ToolCallId,
                Pinned = m.Role == "system"
            };
            if (m.ToolCalls != null && m.ToolCalls.Count > 0)
            {
                hm.ToolCalls = new List<TxAgent.Core.ToolCall>(m.ToolCalls.Count);
                foreach (var tc in m.ToolCalls)
                {
                    hm.ToolCalls.Add(new TxAgent.Core.ToolCall
                    {
                        Id = tc.Id,
                        Name = tc.Function != null ? tc.Function.Name : null,
                        ArgumentsJson = tc.Function != null ? tc.Function.Arguments : null
                    });
                }
            }
            return hm;
        }

        private static ChatMessage TranslateBack(TxAgent.Core.ChatMessage hm)
        {
            var m = new ChatMessage(ToOldRole(hm.Role), hm.Content);
            m.ToolCallId = hm.ToolCallId;
            if (hm.ToolCalls != null && hm.ToolCalls.Count > 0)
            {
                m.ToolCalls = new List<ToolCall>(hm.ToolCalls.Count);
                foreach (var tc in hm.ToolCalls)
                {
                    m.ToolCalls.Add(new ToolCall
                    {
                        Id = tc.Id,
                        Type = "function",
                        Function = new FunctionCall { Name = tc.Name, Arguments = tc.ArgumentsJson }
                    });
                }
            }
            return m;
        }

        private static TxAgent.Core.MessageRole ToHarnessRole(string role)
        {
            if (role == "system") return TxAgent.Core.MessageRole.System;
            if (role == "user") return TxAgent.Core.MessageRole.User;
            if (role == "assistant") return TxAgent.Core.MessageRole.Assistant;
            if (role == "tool") return TxAgent.Core.MessageRole.Tool;
            return TxAgent.Core.MessageRole.User;
        }

        private static string ToOldRole(TxAgent.Core.MessageRole role)
        {
            switch (role)
            {
                case TxAgent.Core.MessageRole.System: return "system";
                case TxAgent.Core.MessageRole.User: return "user";
                case TxAgent.Core.MessageRole.Assistant: return "assistant";
                case TxAgent.Core.MessageRole.Tool: return "tool";
                default: return "user";
            }
        }

        // ── 审批桥接 ──

        private bool InvokeApproval(string name, JObject input)
        {
            if (_approvalRequest == null) return false;
            ITxAgentTool tool;
            if (!_tools.TryGet(name ?? string.Empty, out tool)) tool = null;
            if (tool == null) return false; // 解析不出工具则拒绝,避免空引用
            try { return _approvalRequest(tool, input ?? new JObject()); }
            catch { return false; }
        }

        // ── 事件触发 ──

        private void RaiseAssistantDelta(string text)
        {
            var h = AssistantDelta;
            if (h != null) { try { h(text); } catch { } }
        }

        private void RaiseHistoryChanged()
        {
            var h = HistoryChanged;
            if (h != null) { try { h(); } catch { } }
        }
    }
}
