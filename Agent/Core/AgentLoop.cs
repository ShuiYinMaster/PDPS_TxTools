// TxTools.Agent / Core / AgentLoop.cs
// 进程内 agent 编排循环 (DeepSeek / OpenAI 兼容)。
//
// v2 记忆系统升级:
//   [P0-1] SetConvId / LoadHistory 时切换 TaskPlan 上下文,修 per-conversation 隔离 bug
//   [P1-1] 每轮 SendAsync 按需注入相关 Snippet(完整代码)为本轮临时 system 消息,轮末移除
//   [P1-3] BuildSystemPromptWithMemory 常驻注入 FactsStore + GotchasStore 的 TopN
//   [P1-3] ExtractLessonsAsync 供 UI 层在对话末调用,萃取 facts / gotcha 正解落库
//   [P1-4] RunOneTool 里 run_csharp 输出含错误时自动 GotchasStore.Record
//
// 线程模型:
//   SendAsync 从 WinForms UI 线程的 async void 事件发起。await 的网络 I/O 在线程池完成,
//   但没有用 ConfigureAwait(false),续延回到 UI 同步上下文 —— 于是 tool.Execute(...)
//   天然在 UI 主线程上运行,可安全调用 Tecnomatix.Engineering。切勿在工具内另起线程碰 PS 对象。
//   ExtractLessonsAsync 同样从 UI 触发,SendAsync 完成后异步跑,不阻塞对话。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TxTools.Agent.Core
{
    public sealed class AgentOptions
    {
        public string Model { get; set; }
        public int MaxTokens { get; set; }
        public double Temperature { get; set; }
        public string SystemPrompt { get; set; }
        public int MaxIterations { get; set; }

        /// <summary>保留的最近完整对话回合数。超出则将老回合压缩为摘要注入上下文。0=不压缩。</summary>
        public int MaxTurnsToKeep { get; set; }

        /// <summary>
        /// 自动审批工具名白名单:在此列表内的工具调用跳过用户弹窗、直接执行(写 AUTO-OK 审计)。
        /// 建议加入的低风险高频工具:simulate_operation / add_fact / add_gotcha_correction。
        /// 注意:run_csharp 不应加入,它有专属 CodeApprovalDialog 必须经过代码审阅。
        /// </summary>
        public HashSet<string> AutoApproveTools { get; private set; }

        public AgentOptions()
        {
            Model = "deepseek-v4-pro";
            MaxTokens = 4096;
            Temperature = 0.3;
            MaxIterations = 20;
            MaxTurnsToKeep = 3;
            SystemPrompt = DefaultSystemPrompt;
            AutoApproveTools = new HashSet<string>(StringComparer.Ordinal);
        }

        public const string DefaultSystemPrompt =
@"你是嵌入 Process Simulate (PDPS) 内部的 AI 助手,通过调用工具查询和操作当前 PS 场景。

━━━ 核心原则 ━━━
1. 用中文简洁作答。
2. 行动前先用只读工具了解场景状态,不要凭空假设对象名/参数/类型。
3. 只依据工具真实返回作答,绝不编造工具输出或场景内容。
4. 一次只做一件清晰的事,复杂任务先 update_plan 列步骤,每完成一步更新状态。
5. 会改动场景的操作由系统在执行前请用户确认;你正常调用即可,被拒绝时换思路或向用户解释。

━━━ 工具优先级(遇到任务先想:有没有专属工具?) ━━━
【场景查询】count_objects / list_children / list_operations / find_objects / get_object_location。
  → 要对象数量/清单/层级,不能从 list_operations 推断,必须走遍历工具。
【机器人】check_robot_base(基座校验) / inspect_robot_kinematics(关节/TCP) / find_robot_for_op(op→机器人)。
【焊接】query_collision_sets 列碰撞组;若 SDK 无此 API 用 list_types('Collision') 再 run_csharp。
【位置对齐】scan_devices_z 先扫需要落地的设备 → align_devices_z 执行;set_object_location 设 XYZ+旋转。
【批量重命名】batch_rename 三模式(prefix_replace / suffix_replace / regex_replace)。
【仿真】simulate_operation(播放/重置/回退)。
【3D 视图截图 —— 关键】统一用 capture_viewer_image(SDK 原生 GraphicViewer.GetImage,
   纯 3D 视图无 UI 污染,可指定 width/height 任意分辨率)。
   不要用 screenshot_window(那是整个主窗口客户区,含工具栏/树/属性面板,截出来杂乱)。
【摄像机方位】set_camera_view 支持 front/back/left/right/top/bottom/iso 六向+iso,或 custom 三向量;
   可选 target=对象名以其位置为焦点;可选 capture=true『切视角+截图』一步搞定。
   多角度拍摄标准 pipeline:
     select_objects(names=['XXX'])
     set_view_to_object(use_current_selection=true)   # 让 SDK 定焦点/距离
     set_camera_view(view='front', capture=true, file_name='f')
     set_camera_view(view='iso',   capture=true, file_name='i')
     ...
【视口聚焦】set_view_to_object 把视口对准某对象;同名多实例传 use_current_selection=true
   走当前 ActiveSelection,避开歧义(先 select_objects 选中想要的实例)。
【文档生成】三件套都从零建、内置骨架、无需外部模板,一步生成到桌面 TxTools_Exports:
   - export_docx(标题/正文/表格)
   - export_pptx(每张 slide 传 title/bullets/image_path,本地图片会嵌入)
   - export_table(Excel)
   需要自定义排版/复杂占位符时才用 render_pptx_template + 自制模板 pptx
   (先 inspect_pptx_template 看占位符名)。
【CATIA】catia_read_tree 读活动文档 Product 树;import_catia_tree_to_parts 一键把 CATIA 树映射为
   PS Parts 下的 CompoundPart 空集合层级(TypeName=PartNumber)。
【PS 复合对象】create_compound_resource(Resources 树下) / create_compound_part(Parts 树下),
   parent_name 空时自动找兼容父级。
【CEE 逻辑/信号/传感器】遇到 PLC / 信号 / 联锁 / 传感器 / 智能组件 / 夹具动作 类任务,
   详见下方『CEE 逻辑速查』块。工具:get_resource_logic_status / list_cee_signals / 
   add_logic_to_resource / create_scl_container / create_cee_module / create_cee_signal / 
   create_lb_sensor / list_lb_elements / connect_signal_to_lb / copy_logic。
【记忆】search_past_conversations 跨对话搜索;save_recipe 把稳定多步流程固化成新工具;
   list_recipes 优先复用现成配方。
【兜底】没有合适工具时先 list_types / inspect_type / inspect_object 探查 PS 真实 API,
   再 run_csharp 写代码。run_csharp 是兜底,优先用现成工具。

━━━ PS SDK 速查(从踩坑里固化) ━━━
【文档】
  • TxApplication.ActiveDocument → TxDocument;doc 本身无 Name,当前 Study 名走 doc.CurrentStudy.Name
  • 场景根:doc.PhysicalRoot / doc.OperationRoot / doc.MfgRoot / doc.CollisionRoot
【选择】
  • 拿选中:TxApplication.ActiveSelection.GetItems() → TxObjectList;索引 [0] 取第一个
  • 设选中:sel.SetItems(TxObjectList) / sel.AddItems(list) / sel.Clear()  ← 不是 Add!
【视口/相机】
  • 主视口:TxApplication.ViewersManager.GraphicViewer(不是 doc.Viewers,那不存在)
  • 相机读写:((ITxGraphicDisplayer)viewer).CurrentCamera get/set
  • 构造相机:new TxCamera(refPointVector, camPosVector, upVector),new TxVector(x,y,z) mm
  • 视口自带 API:viewer.ZoomToFit()、viewer.GetImage(Size, transparent) 抓图
  • Zoom to Selection 命令:TxApplication.CommandsManager.ExecuteCommand('GraphicViewer.ZoomToSelection')
    ← 关键:命令 ID 带模块前缀 'GraphicViewer.',不是 'View.ZoomSelection' 那类
【遍历】
  • GetAllDescendants 只在具体类上(TxPhysicalRoot / TxCompoundResource 等),ITxObject / ITxCompound 接口都没暴露
  • run_csharp 里可用 dynamic 绕过静态类型:dynamic dp = parent; dp.GetAllDescendants(null);
  • TxTypeFilter 传接口类型不匹配对象,传 null(全部) 或具体类(如 typeof(TxRobot))才对
  • 按类型遍历:var pts = doc.MfgRoot.GetAllDescendants(new TxTypeFilter(typeof(TxWeldPoint)));
【对象属性】
  • 读坐标:obj.AbsoluteLocation.Translation → TxVector(mm);.RotationRPY_ZYX → 弧度
  • 设坐标(需 Undo):obj.AbsoluteLocation = new TxTransformation(...)
  • 读关节:robot.DrivingJoints → TxObjectList;joint.CurrentValue / .Type / .Name
  • ITxLeadingPart 无 Name:需 ((ITxObject)wp.LeadingPart).Name
  • TxRobot 等无 .Parent,用 ((ITxObject)o).LogicalParent
【run_csharp 环境限制(编译器缺失的引用)】
  • 不可用:System.IO.Packaging、System.IO.Compression.ZipArchive、System.Xml.*
  • 不可用:dynamic 关键字(缺 Microsoft.CSharp.RuntimeBinder) —— 用反射代替
  • 有:System.Drawing 基础类,但 Size/Bitmap.Save/ImageFormat 可能引不全
  • log() 和 return 在方法体内直接可用

━━━ CEE 逻辑速查(Process Simulate 内置 PLC = Cyclic Event Evaluator) ━━━
【核心概念】
  • CEE 是 PS 内置的 PLC 仿真引擎,逐周期扫描并执行仿真环境里的逻辑
  • 三种逻辑层次(按抽象度递增):
    1) Resource Logic Behavior (LB) —— 资源级智能组件,可视化编辑器编 Entries/Exits/Actions
       (MoveJoint/MoveToPose 等) —— 具体控制 3D 资源的动作
    2) SCL Container —— 结构化文本(IEC 61131-3),无需编译实时执行 —— 中层业务逻辑
    3) CEE Module —— Modules Viewer 顶层模块,写信号表达式(ResultSignal = 表达式),
       支持 IF/ELSE 条件分支和联锁 —— 系统级调度
【查询】
  • get_resource_logic_status 查资源的 LB / SCL Container / 关联信号情况
  • list_cee_signals 列所有信号,自动区分 CEE 内部信号(无地址)和外部 PLC 信号(有 I/O 地址)
  • list_lb_elements 查 LB 的所有 Entry/Exit/Action 及信号连接状态
【创建】
  • add_logic_to_resource(3D 资源→智能组件) —— 之后在 Resource Logic Behavior Editor 编辑
  • create_scl_container(资源, SCL 代码) —— 结构化文本层
  • create_cee_module(name, 表达式) —— Modules 顶层
【信号与传感器】
  • create_cee_signal(input/output/display) —— CEE 内部信号无需填地址
  • create_lb_sensor(资源, 类型) —— 光传感器,用于工件到位/夹具开合/传送带堵塞检测
  • connect_signal_to_lb(信号名, LB, entry|exit) —— 
    entry = 传感器→LB(感知),exit = LB→执行器(输出)
【复用】
  • copy_logic(源资源, 目标资源) —— 把已有 LB 复制到同类空资源,批量创建相似逻辑
【External PLC vs CEE Internal】
  • CEE 内部:信号无地址,由 Modules Viewer 层级调度,仿真时 CEE 逐周期扫描执行
  • 外部 PLC:信号有 I/O 地址,通过 OPC/ExternalConnection 与物理/虚拟 PLC 通信
  • 两者信号共享 TxPlcProgram
【典型 pipeline (以带传感器夹具为例)】
  用户说:『给夹具 Grip_A 添加逻辑,当接近传感器检测到工件时允许夹紧』
  a) get_resource_logic_status('Grip_A')     — 摸底
  b) add_logic_to_resource('Grip_A')         — 加 LB
  c) create_lb_sensor('Grip_A', '接近传感器') — 建工件到位传感器
  d) create_cee_signal input '工件到位_触发'  — 触发信号
  e) create_cee_signal output 'Grip_A_夹紧'  — 动作信号
  f) list_lb_elements('Grip_A')              — 查 Entry/Exit 位置
  g) connect_signal_to_lb('工件到位_触发', 'Grip_A', 'entry')  — 传感器→LB
     connect_signal_to_lb('Grip_A_夹紧', 'Grip_A', 'exit')    — LB→执行器
  h) create_cee_module '夹紧联锁' expr='Grip_A_夹紧 := 工件到位_触发 AND NOT 报警'
     — 顶层联锁逻辑

━━━ run_csharp 纪律 ━━━
6. 代码在 PS 主线程同步执行,期间 PS 无响应 —— 避免无界循环/超重操作;大批量分批 + log 进度。
7. C# 5 语法陷阱(避免编译失败):
  • 三元 null 必须转型:var x = flag ? (string)null : val;
  • 无字符串插值 $:用 + 拼接 或 string.Format(...)
  • 无 ?. 空条件:用 if(obj!=null){obj.Prop} 模式
  • 无 => 表达式体:用完整 { return ...; }
  • TxSelection 无索引器:用 sel.GetItems()[0]
  • var 不能推断 null:var x = (string)null;
  • 花括号必须配对,每写 { 立刻写对应 }
8. 提交前对照上述规则逐条检查,一次编译通过。每次编译失败=浪费一轮迭代+大量 token。

━━━ 记忆系统(重要) ━━━
9. 方法记忆(Snippet):
  • 每轮系统自动检索并注入与你当前问题最相关的 Snippet(完整代码,标记为『本轮相关代码片段』)
    ——先扫一眼,命中就直接引用/改写,不要从零摸索。
  • 主动搜索:find_snippet 按语义关键字、get_snippet 按名称取。
  • run_csharp 执行成功后系统自动存(auto_ 前缀+语义标签);需覆盖或补说明用 save_snippet。
  • 稳定多步流程用 save_recipe 固化成一键调用工具,先 list_recipes 看有没有现成的。
10. 踩坑避免(Gotcha):
  • 系统提示末尾会列出常踩清单(签名+正解);写 run_csharp 前先扫,遇相同签名直接用正解。
  • 全表 list_gotchas。run_csharp 失败自动落库;学到正解主动 add_gotcha_correction。
11. 事实记忆(Facts):
  • 系统提示头部『已知事实』是跨对话保留的用户偏好/场景常量/API 事实,视为默认前提。
  • 用户表达偏好、给场景常量、你验证一条 SDK 事实,主动 add_fact 存档;全表 list_facts。
12. 跨对话回忆:『我之前是不是处理过 X』/『上次那个方案』用 search_past_conversations 搜历史。";
    }

    public sealed class AgentLoop
    {
        /// <summary>
        /// 当前活动的 AgentLoop 实例(单窗口应用,单例)。
        /// 供 TxAgentCommand 里注册 SearchPastConversationsTool / AddFactTool 时的
        /// lambda 使用: () => AgentLoop.Current?.CurrentConvId
        /// TxAgentForm 在 BuildLoop 后设置, OnFormClosed 时清空。
        /// </summary>
        public static AgentLoop Current { get; set; }

        private readonly DeepSeekClient _client;
        private readonly ToolRegistry _tools;
        private readonly AgentOptions _options;
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();      // API 工作记忆(可裁剪)
        private readonly List<ChatMessage> _fullHistory = new List<ChatMessage>();   // 完整对话(永不裁剪,供持久化)

        private LessonExtractor _lessonExtractor;

        public event Action<string> AssistantText;
        public event Action<string> AssistantDelta;
        public event Action<string, JObject> ToolCalled;
        public event Action<string, string, bool> ToolCompleted;
        public event Action<string> Info;
        public event Action HistoryChanged;
        public event Action<int, int, int> TokenUsed;

        public int TotalPromptTokens { get; private set; }
        public int TotalCompletionTokens { get; private set; }
        public int TotalTokens { get { return TotalPromptTokens + TotalCompletionTokens; } }

        /// <summary>变更类工具执行前的审批回调;返回 true 放行。未设置时默认拒绝所有变更。</summary>
        public Func<ITxAgentTool, JObject, bool> ApprovalRequest;

        public AgentLoop(DeepSeekClient client, ToolRegistry tools, AgentOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _options = options ?? new AgentOptions();
            ResetWithSystem();
        }

        public void Reset()
        {
            _messages.Clear();
            _fullHistory.Clear();
            TotalPromptTokens = 0;
            TotalCompletionTokens = 0;
            ResetWithSystem();
        }

        private void ResetWithSystem()
        {
            var prompt = BuildSystemPromptWithMemory();
            if (!string.IsNullOrEmpty(prompt))
            {
                var sysMsg = new ChatMessage("system", prompt);
                _messages.Add(sysMsg);
                _fullHistory.Add(sysMsg);
            }
        }

        public IReadOnlyList<ChatMessage> FullHistory { get { return _fullHistory; } }
        public IReadOnlyList<ChatMessage> WorkingMemory { get { return _messages; } }

        public void LoadHistory(IEnumerable<ChatMessage> msgs)
        {
            _messages.Clear();
            _fullHistory.Clear();
            ResetWithSystem();
            if (msgs == null) return;
            foreach (var m in msgs)
                if (m != null && m.Role != "system")
                {
                    _messages.Add(m);
                    _fullHistory.Add(m);
                }
        }

        public async Task SendAsync(string userText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(userText)) return;
            var userMsg = new ChatMessage("user", userText);
            _messages.Add(userMsg);
            _fullHistory.Add(userMsg);

            // [P1-1] 按需 Snippet 注入:根据本轮用户问题即时召回 Top-3 完整代码,
            // 作为独立 system 消息插入到工作记忆,仅本轮有效,finally 里移除。
            ChatMessage snippetSysMsg = InjectRelevantSnippets(userText);

            try
            {
                for (int iter = 0; iter < _options.MaxIterations; iter++)
                {
                    ct.ThrowIfCancellationRequested();
                    CompressHistory();

                    var request = new ChatRequest
                    {
                        Model = _options.Model,
                        MaxTokens = _options.MaxTokens,
                        Temperature = _options.Temperature,
                        Stream = false,
                        Messages = _messages,
                        Tools = _tools.ToToolDefs()
                    };

                    var assistant = await _client.SendStreamAsync(request,
                        frag => { if (AssistantDelta != null) AssistantDelta(frag); }, ct,
                        usage =>
                        {
                            if (usage != null)
                            {
                                TotalPromptTokens += usage.PromptTokens;
                                TotalCompletionTokens += usage.CompletionTokens;
                                if (TokenUsed != null)
                                    TokenUsed(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
                            }
                        });

                    assistant.Role = "assistant";
                    _messages.Add(assistant);
                    _fullHistory.Add(assistant);

                    var calls = assistant.ToolCalls;
                    if (calls == null || calls.Count == 0) return;

                    foreach (var tc in calls)
                    {
                        var name = tc.Function != null ? tc.Function.Name : null;
                        var input = ParseArguments(tc.Function != null ? tc.Function.Arguments : null);

                        if (ToolCalled != null) ToolCalled(name, input);

                        string output;
                        bool isError;
                        RunOneTool(name, input, out output, out isError);

                        if (ToolCompleted != null) ToolCompleted(name, output, isError);
                        var toolMsg = new ChatMessage("tool", output) { ToolCallId = tc.Id };
                        _messages.Add(toolMsg);
                        _fullHistory.Add(toolMsg);
                    }
                }

                if (Info != null)
                    Info("已达到最大工具调用轮数,已停止。可重述需求或拆成更小的步骤。");
            }
            finally
            {
                // [P1-1] 移除本轮临时注入的 Snippet 消息 —— 只影响工作记忆,不进历史
                if (snippetSysMsg != null) _messages.Remove(snippetSysMsg);
                if (HistoryChanged != null) HistoryChanged();
            }
        }

        // ── 工具执行 ──

        private void RunOneTool(string name, JObject input, out string output, out bool isError)
        {
            isError = false;

            ITxAgentTool tool;
            if (string.IsNullOrEmpty(name) || !_tools.TryGet(name, out tool))
            {
                output = "未知工具: " + (name ?? "<null>");
                isError = true;
                return;
            }

            if (!tool.IsReadOnly)
            {
                bool autoApproved = _options.AutoApproveTools != null
                                    && _options.AutoApproveTools.Contains(name);
                bool approved = autoApproved
                    || (ApprovalRequest != null && ApprovalRequest(tool, input));
                if (!approved)
                {
                    output = "用户拒绝执行该变更操作。";
                    isError = true;
                    AuditLog.Write("DENIED  tool=" + name + "  input=" + Compact(input));
                    return;
                }
                if (autoApproved)
                    AuditLog.Write("AUTO-OK tool=" + name + "  input=" + Compact(input));
            }

            // 关键修复: tool.Execute 中的 PS SDK 调用(GetImage/CurrentCamera/ZoomToSelection
            // 等)必须在 PS 主线程(STA)上执行。SendAsync 在 Task.Run 中跑(线程池线程),
            // 直接调用会导致 API 静默失败(视口不变、截图返回 null)。
            // PsContext.Current.Run 通过 SynchronizationContext.Send 把执行体同步路由
            // 回 PS 主线程 —— 与 ExportService/AutoRecorder/WeldAnnotator 中 OnPs 模式一致。
            try
            {
                output = PsContext.Current.Run(() => tool.Execute(input)) ?? string.Empty;
            }
            catch (Exception ex)
            {
                output = "工具执行异常: " + ex.Message;
                isError = true;
            }

            if (!tool.IsReadOnly)
                AuditLog.Write((isError ? "FAILED  " : "APPLIED ") + "tool=" + name
                               + "  input=" + Compact(input) + "  result=" + FirstLine(output));

            // AutoSnippet: run_csharp 成功后自动存片段
            if (!isError && name == "run_csharp")
            {
                try { AutoSaveSnippet(input, output); }
                catch { }
            }

            // [P1-4] AutoGotcha: run_csharp 输出含错误特征时自动落库
            // 注意: run_csharp 编译失败通常 isError=false,只是把错误作为文本返回,所以看 output 而非 isError
            if (name == "run_csharp" && IsGotchaWorthy(output))
            {
                try
                {
                    var code = GetStringFromInput(input, "code");
                    GotchasStore.Record(code, output, _currentConvId);
                }
                catch { }
            }
        }

        private static bool IsGotchaWorthy(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            // 编译错误 CSxxxx / TxNotImplementedException / 明显的异常关键字
            if (output.IndexOf("CS0", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("CS1", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("编译失败", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("TxNotImplementedException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("MissingMemberException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("MissingMethodException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("NullReferenceException", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("未知成员", StringComparison.Ordinal) >= 0) return true;
            if (output.IndexOf("找不到方法", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Compact(JObject input)
        {
            if (input == null) return "{}";
            var s = Newtonsoft.Json.JsonConvert.SerializeObject(input);
            return s.Length <= 300 ? s : s.Substring(0, 300) + "…";
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int nl = s.IndexOf('\n');
            var line = nl >= 0 ? s.Substring(0, nl) : s;
            return line.Length <= 200 ? line : line.Substring(0, 200) + "…";
        }

        private static JObject ParseArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return new JObject();
            try { return JObject.Parse(argumentsJson); }
            catch { return new JObject(); }
        }

        // ── [P1-1] 按需 Snippet 注入 ──

        /// <summary>
        /// 根据本轮用户消息即时召回 Top-3 相关 Snippet,以独立 system 消息插入工作记忆。
        /// 返回插入的消息对象,供 SendAsync finally 里移除。无命中返回 null。
        /// 注意:只加到 _messages,不加 _fullHistory —— 本轮临时上下文,不进永久历史。
        /// </summary>
        private ChatMessage InjectRelevantSnippets(string userText)
        {
            List<Snippet> snippets;
            try { snippets = SnippetStore.FindByTagOrKeyword(userText).Take(3).ToList(); }
            catch { return null; }

            if (snippets == null || snippets.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("【本轮相关代码片段】以下是与用户当前问题匹配的已验证 run_csharp 代码。" +
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
            _messages.Add(msg);
            return msg;
        }

        // ── 回合级压缩 + 摘要注入 ──

        private const string SUMMARY_PREFIX = "[前序对话摘要] ";

        private void CompressHistory()
        {
            int keepTurns = _options.MaxTurnsToKeep;
            if (keepTurns <= 0) return;
            if (_messages.Count <= 2) return;

            bool hasSys = _messages.Count > 0 && _messages[0].Role == "system";
            int startIdx = hasSys ? 1 : 0;

            string prevSummary = "";
            int summaryUserIdx = -1;
            int summaryAsstIdx = -1;
            for (int i = startIdx; i < _messages.Count - 1; i++)
            {
                if (_messages[i].Role == "user"
                    && _messages[i].Content != null
                    && _messages[i].Content.StartsWith(SUMMARY_PREFIX))
                {
                    summaryUserIdx = i;
                    prevSummary = _messages[i].Content.Substring(SUMMARY_PREFIX.Length);
                    if (i + 1 < _messages.Count && _messages[i + 1].Role == "assistant")
                        summaryAsstIdx = i + 1;
                    break;
                }
            }

            var clean = new List<ChatMessage>();
            if (hasSys) clean.Add(_messages[0]);
            for (int i = startIdx; i < _messages.Count; i++)
            {
                if (i == summaryUserIdx || i == summaryAsstIdx) continue;
                clean.Add(_messages[i]);
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
            if (hasSys) rebuilt.Add(_messages[0]);
            rebuilt.Add(new ChatMessage("user", SUMMARY_PREFIX + merged));
            rebuilt.Add(new ChatMessage("assistant", "[确认] 已了解前序对话内容,基于以上上下文继续当前任务。"));
            for (int i = keepFrom; i < clean.Count; i++)
                rebuilt.Add(clean[i]);

            _messages.Clear();
            _messages.AddRange(rebuilt);
        }

        private string GenerateTurnSummary(List<ChatMessage> msgs)
        {
            if (msgs == null || msgs.Count == 0) return "";
            var sb = new StringBuilder();

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

        // ── AutoSnippet: run_csharp 成功后自动存片段 ──

        private void AutoSaveSnippet(JObject input, string output)
        {
            var code = GetStringFromInput(input, "code");
            if (string.IsNullOrWhiteSpace(code)) return;
            var lines = code.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3) return;
            if (output != null && (output.IndexOf("编译失败", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("CS0", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("异常", StringComparison.OrdinalIgnoreCase) >= 0))
                return;
            if (output != null && output.Trim().Length < 20) return;
            if (SnippetStore.HasSimilarCode(code, 0.6)) return;

            var tags = SnippetStore.ExtractTags(code);
            var autoName = SnippetStore.AutoName(code);
            var desc = SnippetStore.AutoDescription(code, tags);

            var savedCode = code.Length > 2000 ? code.Substring(0, 2000) + "\n// …(截断)" : code;

            SnippetStore.Upsert(new Snippet
            {
                Name = autoName,
                Description = desc,
                Code = savedCode,
                Tags = tags,
                Origin = "auto",
                ConvId = _currentConvId
            });
        }

        private static string GetStringFromInput(JObject input, string key)
        {
            if (input == null) return null;
            var val = input[key];
            if (val == null) return null;
            return val.ToString();
        }

        // ── [P1-3/P1-4] 系统提示构建:注入 Facts + Gotchas (不再静态注入 Snippet) ──

        /// <summary>
        /// 构建含记忆的系统提示 = DefaultSystemPrompt + FactsStore.TopN + GotchasStore.TopN。
        /// Snippet 改为每轮 SendAsync 里按需注入(完整代码),此处不再列名单,避免双重注入。
        /// </summary>
        public static string BuildSystemPromptWithMemory()
        {
            var prompt = AgentOptions.DefaultSystemPrompt;
            var sb = new StringBuilder();

            // 事实记忆 (Facts) —— 用户偏好/场景常量/API事实/流程
            var facts = FactsStore.TopN(10);
            if (facts.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【已知事实】(跨对话保留,视为对话默认前提):");
                foreach (var f in facts)
                    sb.AppendLine("  • [" + f.Category + "] " + f.Content);
            }

            // 踩坑清单 (Gotchas) —— 已知报错的签名与正解
            var gotchas = GotchasStore.TopN(15);
            if (gotchas.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("【避坑清单】(写 run_csharp 前核对,遇到相同签名直接用正解写法):");
                foreach (var g in gotchas)
                {
                    var fix = string.IsNullOrEmpty(g.Correction) ? "(暂无正解)" : g.Correction;
                    sb.AppendLine("  • [" + g.Signature + "] " + fix);
                }
            }

            return prompt + sb.ToString();
        }

        // ── 当前对话 ID + TaskPlan 切换 ──

        private string _currentConvId;

        public string CurrentConvId { get { return _currentConvId; } }

        /// <summary>
        /// 设置当前对话 ID。同时切换 TaskPlan 的活动对话(P0-1: 修 per-conversation 隔离 bug)。
        /// 外部在切换对话时应先 SetConvId,再 LoadHistory。
        /// </summary>
        public void SetConvId(string convId)
        {
            _currentConvId = convId;
            TaskPlan.SetActiveConversation(convId);   // [P0-1]
        }

        // ── [P1-3] 对话末经验萃取 ──

        /// <summary>
        /// 对当前对话跑一次经验萃取:提取 facts 落入 FactsStore,补充 gotchas 正解到 GotchasStore。
        /// 独立一次 LLM 调用,建议在 UI 层"结束对话/切换对话前"或对话消息数超阈值时触发。
        /// 不阻塞对话主循环,可 fire-and-forget。
        /// </summary>
        public async Task<LessonExtractor.ExtractResult> ExtractLessonsAsync(CancellationToken ct)
        {
            if (_lessonExtractor == null)
                _lessonExtractor = new LessonExtractor(_client, "deepseek-v4-flash");
            return await _lessonExtractor.ExtractAsync(_currentConvId, _fullHistory, ct);
        }

        /// <summary>兼容旧调用点(如 UI 层已有代码调用了这个名字)。等价于 BuildSystemPromptWithMemory。</summary>
        [Obsolete("改用 BuildSystemPromptWithMemory")]
        public static string BuildSystemPromptWithSnippets()
        {
            return BuildSystemPromptWithMemory();
        }
    }
}