using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TxAgent.Core
{
    public sealed class AgentLoopOptions
    {
        /// <summary>单次任务最多允许的模型往返轮数，防止死循环。</summary>
        public int MaxIterations { get; set; }

        /// <summary>
        /// 同一工具连续失败达到此次数则熔断中止。
        /// 注意别设太小：探查类工具失败是正常的试错过程，实测模型常在第 4~5 次才脱困，
        /// 设成 3 会误杀本来能成功的任务。真正起作用的是下面的提示阈值。
        /// </summary>
        public int MaxConsecutiveToolFailures { get; set; }

        /// <summary>
        /// 同一工具连续失败达到此次数后，在回灌文本里追加"换思路"提示。
        /// 模型脱困往往靠的是改变策略（先探查再写），而不是继续微调同一份代码。
        /// </summary>
        public int FailureHintThreshold { get; set; }

        /// <summary>LLM 调用层失败（网络/限流）的重试次数。</summary>
        public int MaxLlmRetries { get; set; }

        /// <summary>
        /// 单次响应的输出预算(max_tokens)。
        /// 【推理模型的 reasoning_content 计入这个预算】—— 给小了会在思考中途被截断，
        /// 返回空 content 且无 tool_calls，表现为任务莫名其妙结束。
        /// 给大不花钱：只按实际生成量计费，这只是上限。
        /// </summary>
        public int MaxTokens { get; set; }

        /// <summary>true 表示本轮只暴露只读工具（两阶段执行的分析阶段）。</summary>
        public bool ReadOnlyPhase { get; set; }

        /// <summary>首次写操作前是否自动建立回滚点。</summary>
        public bool AutoRestorePoint { get; set; }

        /// <summary>
        /// 是否启用流式。仅当 ILlmClient 同时实现 IStreamingLlmClient
        /// 且其 SupportsStreaming 为 true 时生效，否则自动退回非流式。
        /// </summary>
        public bool EnableStreaming { get; set; }

        public AgentLoopOptions()
        {
            MaxIterations = 25;
            MaxConsecutiveToolFailures = 6;
            FailureHintThreshold = 3;
            MaxLlmRetries = 2;
            MaxTokens = 16384;
            ReadOnlyPhase = false;
            AutoRestorePoint = true;
            EnableStreaming = true;
        }
    }

    public sealed class AgentRunResult
    {
        public bool Completed { get; set; }

        /// <summary>
        /// 最后一轮的文本。注意：正文已通过 ContentDelta 事件实时发出，
        /// UI 不应再拿这个字段重复显示，它只用于日志与结果判定。
        /// </summary>
        public string FinalMessage { get; set; }

        public string StopReason { get; set; }
        public int Iterations { get; set; }
        public int ToolCallCount { get; set; }
        public int ToolFailureCount { get; set; }
        public bool SceneMutated { get; set; }
        public RestorePoint RestorePoint { get; set; }
        public int TotalPromptTokens { get; set; }
        public int TotalCompletionTokens { get; set; }
    }

    /// <summary>
    /// harness 核心循环。对 PS / CATIA 均无感知，全部能力经 IAgentHost 与 ITool 注入。
    /// 所有事件均在后台线程触发，UI 侧须自行封送。
    /// </summary>
    public sealed class AgentLoop
    {
        private readonly ILlmClient _llm;
        private readonly IStreamingLlmClient _streamLlm;
        private readonly ToolRegistry _registry;
        private readonly IAgentHost _host;
        private readonly AgentLoopOptions _options;

        /// <summary>
        /// 本轮流式已发出的正文。取消时用它把"界面上看得见的半截回复"补进会话 ——
        /// 否则用户点停止后重开对话，会发现刚才明明显示了内容却没保存。
        /// </summary>
        private readonly StringBuilder _partial = new StringBuilder();

        /// <summary>本次运行累计触发重复循环的次数。</summary>
        private int _repetitionStrikes;

        /// <summary>连续几次重复就放弃。给两次机会足够 —— 三次还在转说明任务本身有问题。</summary>
        private const int MaxRepetitionStrikes = 3;

        public AgentLoop(ILlmClient llm, ToolRegistry registry, IAgentHost host, AgentLoopOptions options)
        {
            if (llm == null) throw new ArgumentNullException("llm");
            if (registry == null) throw new ArgumentNullException("registry");
            if (host == null) throw new ArgumentNullException("host");

            _llm = llm;
            _streamLlm = llm as IStreamingLlmClient;
            _registry = registry;
            _host = host;
            _options = options ?? new AgentLoopOptions();
        }

        /// <summary>本次运行是否会走真流式。</summary>
        public bool StreamingActive
        {
            get { return _options.EnableStreaming && _streamLlm != null && _streamLlm.SupportsStreaming; }
        }

        // ── 事件（全部在后台线程触发） ──

        /// <summary>
        /// 阶段性状态文本。这是"日志/状态栏"通道，不是聊天流 —— 转发到会话气泡会和
        /// ToolStarting 产生的工具卡片重复。
        /// </summary>
        public event Action<string> Progress;

        /// <summary>
        /// 正文增量。流式下是 token 级分片；非流式下每轮结束时把整段文本作为一个增量发出。
        /// UI 只需订阅这一个事件即可拿到全部助手正文。
        /// </summary>
        public event Action<string> ContentDelta;

        /// <summary>思考内容增量（推理模型的 reasoning_content）。非推理模型不会触发。</summary>
        public event Action<string> ReasoningDelta;

        /// <summary>
        /// LLM 重试导致已发出的增量作废。UI 收到后应清空当前这一轮的文本缓冲。
        /// </summary>
        public event Action ContentReset;

        /// <summary>一轮模型输出结束（无论有无工具调用）。参数为该轮完整正文，可能为空。</summary>
        public event Action<string> TurnCompleted;

        /// <summary>工具即将执行（已通过审批）。</summary>
        public event Action<ToolCall> ToolStarting;

        /// <summary>工具执行结束。</summary>
        public event Action<ToolCall, ToolResult> ToolFinished;

        /// <summary>单次 LLM 调用的 token 用量（prompt, completion）。</summary>
        public event Action<int, int> Usage;

        public async Task<AgentRunResult> RunAsync(AgentSession session, CancellationToken ct)
        {
            try
            {
                return await RunCoreAsync(session, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户点了停止。把本轮已流式发出、但还没入会话的半截正文补进去，
                // 让"界面显示的"和"保存下来的"一致。
                FlushPartial(session);
                throw;
            }
        }

        private async Task<AgentRunResult> RunCoreAsync(AgentSession session, CancellationToken ct)
        {
            var result = new AgentRunResult();
            _partial.Length = 0;
            _repetitionStrikes = 0;
            var failureCounter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            RestorePoint restorePoint = null;

            var tools = _registry.ExportSchemas(_options.ReadOnlyPhase);

            for (int iter = 1; iter <= _options.MaxIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();
                result.Iterations = iter;

                session.TrimToBudget();

                var response = await CallLlmWithRetryAsync(session, tools, ct).ConfigureAwait(false);

                if (response.IsError)
                {
                    result.Completed = false;
                    result.StopReason = "LLM 调用失败: " + response.ErrorMessage;
                    result.RestorePoint = restorePoint;
                    return result;
                }

                result.TotalPromptTokens += response.PromptTokens;
                result.TotalCompletionTokens += response.CompletionTokens;
                RaiseUsage(response.PromptTokens, response.CompletionTokens);

                // 非流式路径：整段正文在这里补发一次，让 UI 只需订阅 ContentDelta
                if (!response.AlreadyStreamed)
                {
                    if (!string.IsNullOrEmpty(response.ReasoningContent))
                        RaiseReasoningDelta(response.ReasoningContent);
                    if (!string.IsNullOrEmpty(response.Content))
                        RaiseContentDelta(response.Content);
                }

                if (response.Truncated)
                    _host.Log("warn", "本轮输出被 max_tokens 截断 (completion="
                        + response.CompletionTokens + ", limit=" + _options.MaxTokens + ")");

                // 本轮内容已经或即将入会话，缓冲作废
                _partial.Length = 0;

                RaiseTurnCompleted(response.Content);

                // 只在这一轮确实产出了内容或工具调用时才入会话。
                // 模型偶尔会返回"空内容 + 无 tool_calls"(上下文被撑爆时常见),
                // 这种消息一旦进了历史,下一轮原样发回去会被 API 拒:
                //   400 Invalid assistant message: content or tool_calls must be set
                bool hasPayload = !string.IsNullOrWhiteSpace(response.Content) || response.HasToolCalls;
                if (hasPayload)
                {
                    session.Add(ChatMessage.CreateAssistant(
                        response.Content,
                        response.ToolCalls == null ? null : new List<ToolCall>(response.ToolCalls)));
                }
                else
                {
                    _host.Log("warn", "模型返回空内容且无工具调用，已丢弃该轮消息以免污染历史");
                }

                // 输出陷入重复循环，客户端已主动截断。
                // 【不能当正常结束】那一大坨重复文字不是答案。
                // 把纠正提示作为一条 user 消息入会话，让模型换个做法继续 ——
                // 用 user 角色而不是 tool，因为这不是某个工具的返回，
                // 而是对模型行为本身的干预。
                if (!string.IsNullOrEmpty(response.RepetitionHint))
                {
                    _repetitionStrikes++;

                    _host.Log("warn", "第 " + _repetitionStrikes + " 次检测到重复循环");

                    if (_repetitionStrikes >= MaxRepetitionStrikes)
                    {
                        result.Completed = false;
                        result.StopReason = "模型连续 " + _repetitionStrikes
                            + " 次陷入重复循环，已中止。当前这一步可能超出了模型的处理能力 —— "
                            + "建议把任务拆小，或把中间数据写成文件再读，不要在对话里搬运。";
                        result.RestorePoint = restorePoint;
                        return result;
                    }

                    session.AddUser(response.RepetitionHint);
                    continue;   // 不结束，让它重来一轮
                }

                // 没有工具调用 = 模型认为任务结束
                if (!response.HasToolCalls)
                {
                    result.Completed = hasPayload;
                    result.FinalMessage = response.Content;

                    if (hasPayload)
                        result.StopReason = "正常结束";
                    else if (response.Truncated)
                        result.StopReason = "模型输出预算(max_tokens=" + _options.MaxTokens
                            + ")在思考阶段就耗尽了，没能产出回答。"
                            + "推理模型的思考链计入输出预算 —— 调大 AgentLoopOptions.MaxTokens，"
                            + "或换用非思考模式";
                    else
                        result.StopReason = "模型返回空响应(无内容也无工具调用)"
                            + (string.IsNullOrEmpty(response.FinishReason)
                                ? "，可能触发了内容过滤"
                                : "，finish_reason=" + response.FinishReason);

                    result.RestorePoint = restorePoint;
                    return result;
                }

                // 本轮已执行过的 (工具名 + 参数)。只在轮内去重 ——
                // 跨轮相同调用往往是合理的（先查一次、改完再查一次核对）。
                var doneThisTurn = new HashSet<string>(StringComparer.Ordinal);

                foreach (var call in response.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    result.ToolCallCount++;

                    // 参数不同的并行调用是合理的（同一工具查不同 scope），不能一起挡掉，
                    // 所以键必须带上参数，只挡完全一样的那种。
                    var dedupKey = (call.Name ?? "") + "|" + (call.ArgumentsJson ?? "");
                    if (!doneThisTurn.Add(dedupKey))
                    {
                        _host.Log("warn", "跳过本轮重复调用: " + call.Name);
                        session.Add(ChatMessage.CreateToolResult(call.Id,
                            "跳过:本轮已经用完全相同的参数调用过 " + call.Name + "，"
                            + "结果见上一条。不要重复发同一个调用。"));
                        continue;
                    }

                    // 参数 JSON 残缺最常见的原因是输出预算耗尽，不是模型写错。
                    // 上一轮 finish_reason=length 时尤其可疑 —— 明确说出来，
                    // 否则模型会以为格式写错了，原样重发一遍再次被截断。
                    if (response.Truncated && !string.IsNullOrEmpty(call.ArgumentsJson))
                    {
                        _host.Log("warn", "本轮被 max_tokens 截断，工具 "
                            + call.Name + " 的参数可能不完整");
                    }

                    var tool = _registry.Find(call.Name);

                    if (tool == null)
                    {
                        session.Add(ChatMessage.CreateToolResult(call.Id,
                            "错误：不存在名为 \"" + call.Name + "\" 的工具。可用工具：" +
                            _registry.DescribeAvailable() + "。请改用其中之一重试。"));
                        continue;
                    }

                    if (_options.ReadOnlyPhase && tool.IsWrite)
                    {
                        session.Add(ChatMessage.CreateToolResult(call.Id,
                            "错误：当前处于只读分析阶段，禁止调用写入类工具 \"" + tool.Name +
                            "\"。请先完成分析并输出变更清单，写入将在用户确认后的下一阶段执行。"));
                        continue;
                    }

                    // 首次写操作前建立回滚点
                    if (tool.IsWrite && _options.AutoRestorePoint && restorePoint == null)
                    {
                        RaiseProgress("正在建立回滚点…");
                        restorePoint = SafeCreateRestorePoint("即将执行写入工具: " + tool.Name);

                        RaiseProgress(restorePoint.Created
                            ? "回滚点已建立: " + restorePoint.HowToRollback
                            : "未能建立回滚点: " + restorePoint.HowToRollback);

                        if (!restorePoint.Created)
                        {
                            bool goOn = _host.Confirm(
                                "无法建立回滚点",
                                "原因：" + restorePoint.HowToRollback +
                                "\n继续执行将无法自动回退，是否继续？",
                                true);

                            if (!goOn)
                            {
                                session.Add(ChatMessage.CreateToolResult(call.Id,
                                    "用户拒绝在无回滚点的情况下执行写入操作。请停止修改，改为输出建议方案。"));
                                continue;
                            }
                        }
                    }

                    if (tool.IsDestructive)
                    {
                        bool ok = _host.Confirm(
                            "确认破坏性操作",
                            "工具：" + tool.Name + "\n参数：" + Truncate(call.ArgumentsJson, 800),
                            true);

                        if (!ok)
                        {
                            session.Add(ChatMessage.CreateToolResult(call.Id,
                                "用户否决了这次调用。请不要重复相同调用，改为说明意图或换一种非破坏性方式。"));
                            continue;
                        }
                    }

                    // 工具卡片在执行「前」就发出，UI 才能看到"正在执行"的中间态
                    RaiseToolStarting(call);
                    RaiseProgress("执行 " + tool.Name);

                    ToolResult toolResult = SafeExecute(tool, call.ArgumentsJson);

                    RaiseToolFinished(call, toolResult);

                    if (toolResult.MutatedScene) result.SceneMutated = true;

                    // 注:片段观察(Observe)放在工具层(RunCSharpTool/RunPythonTool)做 ——
                    // 内核不该硬编码"run_csharp"这个工具名去感知片段库;
                    // 且只有工具层才知道编译失败 vs 返回值成功,harness 的 toolResult.Success
                    // 会把编译失败的代码当成成功。放工具层还能让 python 通道共用同一套挂钩。

                    // 先更新连续失败计数，再据此决定回灌文本要不要带"换思路"提示
                    int consecutive = 0;
                    if (toolResult.Success)
                    {
                        failureCounter[tool.Name] = 0;
                    }
                    else
                    {
                        result.ToolFailureCount++;
                        failureCounter.TryGetValue(tool.Name, out consecutive);
                        consecutive++;
                        failureCounter[tool.Name] = consecutive;
                        _host.Log("warn", "工具 " + tool.Name + " 连续第 " + consecutive
                            + " 次失败: " + toolResult.ErrorKind);
                    }

                    session.Add(ChatMessage.CreateToolResult(call.Id,
                        FormatForModel(tool, toolResult, consecutive)));

                    if (!toolResult.Success && consecutive >= _options.MaxConsecutiveToolFailures)
                    {
                        result.Completed = false;
                        result.StopReason = "工具 " + tool.Name + " 连续失败 " + consecutive + " 次，已熔断中止";
                        result.FinalMessage = toolResult.Content;
                        result.RestorePoint = restorePoint;
                        return result;
                    }
                }
            }

            result.Completed = false;
            result.StopReason = "达到最大轮数 " + _options.MaxIterations + "，任务未完成";
            result.RestorePoint = restorePoint;
            return result;
        }

        private async Task<LlmResponse> CallLlmWithRetryAsync(
            AgentSession session, IList<ToolSchema> tools, CancellationToken ct)
        {
            LlmResponse last = null;
            bool useStream = StreamingActive;

            // 本轮是否已经往 UI 发过增量。重试前需要通知 UI 丢弃这些半截内容。
            bool emitted = false;

            var handlers = new LlmStreamHandlers
            {
                OnReasoningDelta = text => { emitted = true; RaiseReasoningDelta(text); },
                OnContentDelta = text =>
                {
                    emitted = true;
                    _partial.Append(text);       // 供取消时落库
                    RaiseContentDelta(text);
                }
            };

            for (int attempt = 0; attempt <= _options.MaxLlmRetries; attempt++)
            {
                if (attempt > 0)
                {
                    if (emitted)
                    {
                        RaiseContentReset();
                        _partial.Length = 0;     // 这半截已作废，别再落库
                        emitted = false;
                    }
                    _host.Log("warn", "LLM 重试第 " + attempt + " 次");
                    RaiseProgress("模型调用失败，正在重试 (" + attempt + ")…");
                    await Task.Delay(500 * attempt, ct).ConfigureAwait(false);
                }

                var request = new LlmRequest
                {
                    Messages = new List<ChatMessage>(session.Messages),
                    Tools = tools,
                    MaxTokens = _options.MaxTokens > 0 ? _options.MaxTokens : 16384
                };

                try
                {
                    last = useStream
                        ? await _streamLlm.CompleteStreamAsync(request, handlers, ct).ConfigureAwait(false)
                        : await _llm.CompleteAsync(request, ct).ConfigureAwait(false);

                    if (last != null && !last.IsError) return last;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _host.Log("error", "LLM 调用异常: " + ex);
                    last = LlmResponse.Error(ex.GetType().Name + ": " + ex.Message);
                }
            }

            return last ?? LlmResponse.Error("未知错误");
        }

        /// <summary>把中断时的半截正文补进会话。无内容则什么都不做。</summary>
        private void FlushPartial(AgentSession session)
        {
            try
            {
                if (session == null) return;
                var text = _partial.ToString();
                _partial.Length = 0;

                // 【必须判空】空 assistant 消息(既无 content 又无 tool_calls)
                // 一旦进历史，下一轮原样发回去就是 400 Invalid assistant message，
                // 而且之后每一轮都会 400。
                if (string.IsNullOrWhiteSpace(text)) return;

                session.Add(ChatMessage.CreateAssistant(text + "\n\n[本轮被用户中断]", null));
                _host.Log("info", "已保存中断前的半截回复(" + text.Length + " 字符)");
            }
            catch { }
        }

        private ToolResult SafeExecute(ITool tool, string argumentsJson)
        {
            try
            {
                var r = tool.Execute(argumentsJson, _host);
                return r ?? ToolResult.Fail("host", "工具返回了 null 结果。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 工具实现漏掉的异常在这里兜底，转成可回灌的文本而不是崩掉整个循环
                _host.Log("error", "工具 " + tool.Name + " 抛出未捕获异常: " + ex);
                return ToolResult.Fail("host",
                    "工具执行抛出异常：" + ex.GetType().Name + ": " + ex.Message +
                    "\n请检查参数是否符合 schema，或换一种实现方式重试。");
            }
        }

        private RestorePoint SafeCreateRestorePoint(string reason)
        {
            try
            {
                var rp = _host.CreateRestorePoint(reason);
                if (rp == null) return RestorePoint.None("宿主返回 null");

                if (rp.Created)
                    _host.Log("info", "已建立回滚点: " + rp.HowToRollback);
                else
                    _host.Log("warn", "未建立回滚点: " + rp.HowToRollback);

                return rp;
            }
            catch (Exception ex)
            {
                _host.Log("error", "建立回滚点失败: " + ex);
                return RestorePoint.None(ex.Message);
            }
        }

        /// <summary>
        /// 统一回灌格式。失败时显式标注，促使模型自修而不是继续往下走；
        /// 连续失败到阈值后追加换思路提示 —— 实测模型脱困靠的是改变策略，
        /// 而不是在同一份代码上继续微调。
        /// </summary>
        private string FormatForModel(ITool tool, ToolResult r, int consecutiveFailures)
        {
            if (r.Success) return r.Content ?? "(执行成功，无输出)";

            var sb = new StringBuilder();
            sb.Append("【执行失败】工具: ").Append(tool.Name);
            if (!string.IsNullOrEmpty(r.ErrorKind))
                sb.Append(" | 类型: ").Append(r.ErrorKind);
            sb.Append(" | 连续第 ").Append(consecutiveFailures).Append(" 次");
            sb.AppendLine();
            sb.AppendLine(r.Content ?? "(无错误详情)");

            if (consecutiveFailures >= _options.FailureHintThreshold)
            {
                sb.AppendLine("【停下来换思路】同一工具已连续失败 " + consecutiveFailures +
                    " 次，继续微调同一份代码大概率还会失败。请改变策略：");
                sb.AppendLine("  1. 先用只读方式把真实情况查清楚 —— 对象是否存在、真实类型名是什么、" +
                    "目标成员的确切签名是什么，不要凭记忆假设 API。");
                sb.AppendLine("  2. 把一次大动作拆成最小的一步先跑通，再逐步加回其余逻辑。");
                sb.AppendLine("  3. 若确认此路不通，换用其它工具，或向用户说明卡在哪里。");
                int left = _options.MaxConsecutiveToolFailures - consecutiveFailures;
                if (left > 0)
                    sb.AppendLine("  （该工具还剩 " + left + " 次失败机会，之后任务将被中止。）");
            }
            else
            {
                sb.Append("请根据以上错误修正后重试。");
            }

            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…(已截断)";
        }

        // ── 事件触发（一律吞掉订阅方异常，UI 出错不应中断 agent 循环） ──

        private void RaiseProgress(string text)
        {
            var h = Progress;
            if (h != null) { try { h(text); } catch { } }
        }

        private void RaiseContentDelta(string text)
        {
            var h = ContentDelta;
            if (h != null && !string.IsNullOrEmpty(text)) { try { h(text); } catch { } }
        }

        private void RaiseReasoningDelta(string text)
        {
            var h = ReasoningDelta;
            if (h != null && !string.IsNullOrEmpty(text)) { try { h(text); } catch { } }
        }

        private void RaiseContentReset()
        {
            var h = ContentReset;
            if (h != null) { try { h(); } catch { } }
        }

        private void RaiseTurnCompleted(string text)
        {
            var h = TurnCompleted;
            if (h != null) { try { h(text); } catch { } }
        }

        private void RaiseToolStarting(ToolCall call)
        {
            var h = ToolStarting;
            if (h != null) { try { h(call); } catch { } }
        }

        private void RaiseToolFinished(ToolCall call, ToolResult r)
        {
            var h = ToolFinished;
            if (h != null) { try { h(call, r); } catch { } }
        }

        private void RaiseUsage(int prompt, int completion)
        {
            var h = Usage;
            if (h != null && (prompt > 0 || completion > 0)) { try { h(prompt, completion); } catch { } }
        }
    }
}