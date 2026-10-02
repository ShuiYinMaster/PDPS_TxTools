// TxTools.Agent / TxAgentCommand.cs
// PS 插件入口。注意 TxButtonCommand 在 PS 2402 中真正的抽象成员只有 Name / Category / Execute(object)，
// 不要声明 DisplayName / InternalName / Tooltip (会触发 CS0115/CS0534)。
//
// v2 记忆系统:
//   注册 5 个记忆工具时,需要拿"当前对话 id"作 lambda 闭包参数。BuildToolRegistry 是静态方法,
//   此时 AgentLoop 还没构造(它在 TxAgentForm.BuildLoop 里才创建)。用 AgentLoop.Current 静态入口
//   解耦: form 构造 loop 后设置 AgentLoop.Current = loop,工具用 () => AgentLoop.Current?.CurrentConvId,
//   即使 Current 为 null(窗口未打开或刚关闭)也返回 null,不崩。

using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.Agent.Tools;
using TxTools.Agent.UI;

namespace TxTools.Agent
{
    public sealed class TxAgentCommand : TxButtonCommand
    {
        private static TxAgentForm _form;
        private static DshPetController _petController;
        private static string _petError;
        private static RecipeQuickForm _quickRecipes;

        // ── 多 PDPS 无界面执行器:插件一加载就启动 ──
        // PS 反射扫描命令类注册按钮时会触发静态构造,趁这时把 RPC 执行器跑起来。
        // 这样被控端 PDPS 什么都不用点,主控端就能发现并调用它。
        static TxAgentCommand()
        {
            try
            {
                TxAgentService.StudyNameGetter = () =>
                {
                    try { return TxApplication.ActiveDocument.CurrentStudy.Name; }
                    catch { return null; }
                };
                PsRpcServer.SystemRootGetter = () =>
                {
                    try { return TxApplication.SystemRootDirectory; }
                    catch { return null; }
                };
                TxAgentService.Start(BuildToolRegistry());
            }
            catch { /* 服务起不来不影响窗口本身 */ }
            InitializePet();
        }

        public override string Name { get { return "TxAgent"; } }
        public override string Category { get { return "TxTools"; } }
        public override string LargeBitmap { get { return "image.ai.png"; } }
        public override string Description => "可接入deepseek进行一些简单的自动化处理";

        public override void Execute(object cmdParams)
        {
            // 在 PS 主线程捕获 SynchronizationContext，供所有工具把 PS 调用路由回主线程
            // (对齐 ExportGunCmd/ExportService 的做法)。
            var psCtx = SynchronizationContext.Current ?? new SynchronizationContext();
            PsContext.CaptureFromMainThread();
            PsContext.Current = new PsContext(psCtx);

            // 本进程已开着窗口 → 前置激活，不重复创建。
            if (_form != null && !_form.IsDisposed)
            {
                if (_form.WindowState == FormWindowState.Minimized)
                    _form.WindowState = FormWindowState.Normal;
                _form.BringToFront();
                _form.Activate();
                _form.Focus();
                return;
            }

            // 【Agent 窗口全局唯一】已有其它 PDPS 进程开着窗口时，
            // 本进程不进入对话界面，只显示提示信息 —— 避免两窗口写同一份会话互相覆盖。
            if (!PsInstanceRegistry.TryAcquireWindow())
            {
                ShowOccupiedHint();
                return;
            }

            var tools = BuildToolRegistry();
            _form = new TxAgentForm(psCtx, tools);
            _form.Name = _form.GetType().FullName;   // 跨插件窗口尺寸串扰修复(双保险)
            _form.FormClosed += (s, e) =>
            {
                if (!RecipeUiActions.IsRunning) SetPetState(null);
                _form = null;
                try { PsInstanceRegistry.ReleaseWindow(); } catch { }
            };

            IWin32Window owner = TryGetPsMainWindow();
            if (owner != null) _form.Show(owner);
            else _form.Show();
            if (_petError != null) _form.ReportPetError(_petError);

            try { TxApplication.StatusBarMessage = "TxTools.Agent 已启动"; } catch { }
        }

        private static void InitializePet()
        {
            if (_petController != null) return;
            try
            {
                // The command is registered on the PS UI thread during plugin loading.
                // Queue startup so it runs after registration, outside the type initializer.
                var controller = new DshPetController(UserPrefsStore.Load().DesktopPetVisible,
                    UserPrefsStore.UpdateDesktopPetVisible, () =>
                    {
                        _quickRecipes?.Hide();
                        new TxAgentCommand().Execute(null);
                    }, ToggleQuickRecipes);
                controller.Failed += message =>
                {
                    _petError = message;
                    _form?.ReportPetError(message);
                };
                controller.Changed += (s, e) =>
                {
                    _form?.PostPetVisibility();
                    if (!controller.Enabled) _quickRecipes?.Hide();
                };
                _petController = controller;
            }
            catch (Exception ex)
            {
                _petError = ex.Message;
                System.Diagnostics.Debug.WriteLine("[TxAgent] 鲸鱼娘悬浮窗创建失败: " + ex.Message);
                _form?.ReportPetError(ex.Message);
            }
        }

        internal static bool PetEnabled => _petController?.Enabled ?? UserPrefsStore.Load().DesktopPetVisible;

        private static void ToggleQuickRecipes(JObject anchor)
        {
            if ((string)anchor?["kind"] == "manage-recipes") { OpenRecipeManager(); return; }
            // This entry can run before Agent.Execute has ever created a conversation.
            PsContext.CaptureFromMainThread();
            if (_quickRecipes == null || _quickRecipes.IsDisposed) _quickRecipes = new RecipeQuickForm(OpenRecipeManager);
            _quickRecipes.Toggle(anchor, TryGetPsMainWindow());
        }

        private static void OpenRecipeManager()
        {
            _quickRecipes?.Hide();
            new TxAgentCommand().Execute(null);
            _form?.OpenRecipeManager();
        }

        internal static void SetPetEnabled(bool enabled)
        {
            InitializePet();
            _petError = null;
            _petController?.SetEnabled(enabled);
        }

        internal static void SetPetThinking(bool thinking)
        {
            _petController?.SetThinking(thinking);
        }

        internal static void CelebratePet()
        {
            _petController?.SetState("success");
        }

        internal static void SetPetState(string state, string task = null)
        {
            _petController?.SetState(state, task);
        }

        /// <summary>Agent 窗口已被其它实例打开时的提示窗口（不进入对话，避免会话覆盖）。</summary>
        private void ShowOccupiedHint()
        {
            try
            {
                var owner = TryGetPsMainWindow();
                var live = PsInstanceRegistry.Live();
                string who = "另一个 PDPS 实例";
                foreach (var i in live)
                    if (i.HasWindow && i.IsAlive && !i.IsSelf)
                    { who = "「" + (string.IsNullOrEmpty(i.Name) ? "?" : i.Name) + "」"; break; }

                string msg = "TxAgent 窗口已在 " + who + " 中打开。\n\n"
                           + "为避免两窗口写同一份会话互相覆盖，本实例不进入对话界面，"
                           + "仅作为跨环境执行器运行。\n\n"
                           + "如需在此环境对话，请先关闭另一实例中的 Agent 窗口。";
                if (owner != null)
                    System.Windows.Forms.MessageBox.Show(owner, msg, "TxAgent — 窗口已被占用",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
                else
                    System.Windows.Forms.MessageBox.Show(msg, "TxAgent — 窗口已被占用",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch
            {
                System.Windows.Forms.MessageBox.Show(
                    "TxAgent 窗口已在其它实例中打开。为避免会话覆盖，本实例不进入对话界面。",
                    "TxAgent — 窗口已被占用",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Information);
            }
        }

        /// <summary>注册全部工具：原子工具 + 记忆系统 + 配方机制 + 已保存的配方。</summary>
        private static ToolRegistry BuildToolRegistry()
        {
            var reg = new ToolRegistry();

            // 1) 原子工具 (能力的基本积木；越细，配方能组合出的解法越多)
            reg.Register(new SceneQueryTool());     // 只读：查询选中对象 / 当前文档
            reg.Register(new CountObjectsTool());    // 只读：全场景按类型统计/枚举(数机器人等)
            reg.Register(new ListChildrenTool());    // 只读：展开组件数子对象(数 CD_L 下设备)
            reg.Register(new ListOperationsTool());  // 只读：选中操作 名称/类型/工具
            reg.Register(new CountPointsTool());     // 只读：按类型统计点数
            reg.Register(new ListTcpOptionsTool());  // 只读：操作的 TCP 选项
            reg.Register(new CheckReachabilityTool()); // 只读：快速可达性摘要
            reg.Register(new GetReferenceFrameTool()); // 只读：当前参考坐标系
            reg.Register(new ListTypesTool());       // 只读：搜 SDK 类型
            reg.Register(new InspectTypeTool());     // 只读：列类型成员
            reg.Register(new InspectObjectTool());   // 只读：探查活动对象
            reg.Register(new UpdatePlanTool());      // 任务：维护多步计划
            reg.Register(new SaveSnippetTool());     // 记忆：存可复用代码片段
            reg.Register(new ListSnippetsTool());    // 记忆：列片段
            reg.Register(new GetSnippetTool());      // 记忆：取片段代码(+复用计数)
            reg.Register(new FindSnippetTool());     // 记忆：按语义描述智能搜索片段
            reg.Register(new ExportTableTool());     // 导出：把汇总信息写成 xlsx
            reg.Register(new ExportPointsExcelTool()); // 导出：选中操作的焊点坐标(ExportGun 同款)
            reg.Register(new ExportObjectListTool());  // 导出：遍历→对象清单一步导出(机器人/设备清单)
            reg.Register(new SelectObjectsTool());   // 动作：按名选中(查到→选中→操作)
            reg.Register(new AlignDevicesZTool());  // 变更：Z 对齐 (需审批, 可撤销)
            reg.Register(new RunCSharpTool());       // 变更：写 C# 在 PS 内执行 (兜底, 强制审批, 可撤销, 审计)
            // reg.Register(new RunReachabilityTool());  // TODO: 包装 RobotReachabilityChecker
            // reg.Register(new ExportGunTool());        // TODO: 包装 ExportService

            // ─── 机器人 / 位置 / 仿真 工具 ───
            reg.Register(new CheckRobotBaseTool());       // 只读：BASE0 校验
            reg.Register(new InspectRobotKinematicsTool());// 只读：运动学信息
            reg.Register(new FindRobotForOpTool());       // 只读：查找操作绑定机器人
            reg.Register(new GetObjectLocationTool());    // 只读：查询对象位置/姿态
            reg.Register(new SetObjectLocationTool());    // 变更：设置对象位置 (需审批, 可撤销)
            reg.Register(new ScanDevicesZTool());         // 只读：扫描设备 Z 向落地状态
            reg.Register(new FindObjectsTool());          // 只读：按名称/类型关键字搜索对象
            reg.Register(new BatchRenameTool());          // 变更：批量重命名 (需审批, 可撤销)
            reg.Register(new QueryCollisionSetsTool());   // 只读：查碰撞检测组配置
            reg.Register(new SimulateOperationTool());    // 变更：播放/暂停/停止仿真 (需审批)

            // 2) 配方机制：让 agent 自己把多步操作存成可复用工具
            reg.Register(new ListRecipesTool());
            reg.Register(new SaveRecipeTool(reg));
            reg.Register(new DeleteRecipeTool(reg));

            // 3) 记忆系统工具 (v2) —— 跨对话记忆的读写入口
            //    convId 通过 HarnessAgentLoop.Current 静态入口获取。
            //    form 构造 loop 后写入 Current,lambda 每次调用时读取,即使 Current 为 null 也返回 null 不崩。
            var getConvId = new Func<string>(() =>
                TxTools.Agent.Harness.HarnessAgentLoop.Current?.CurrentConvId);

            // 片段固化/归因需要 convId:统一注入 AgentContext,工具层挂钩共用同一个来源
            AgentContext.ConvIdProvider = getConvId;

            reg.Register(new SearchPastConversationsTool(getConvId));
            reg.Register(new ListGotchasTool());
            reg.Register(new AddGotchaCorrectionTool());
            reg.Register(new ListFactsTool());
            reg.Register(new AddFactTool(getConvId));

            // 4) 上传文件解析工具 (v2)
            reg.Register(new ListUploadedFilesTool(getConvId));
            reg.Register(new ReadUploadedFileTool());

            // 4.5) 弹窗提问工具 —— AI 主动向用户问 confirm/choice/input,阻塞等答复
            //      简化传统"AI 说话→用户输入框回复"的多步流程,一次点击就返回
            reg.Register(new AskUserTool());

            // 4.6) 源码工作区工具 —— AI 改项目源码(读/搜/改/建/回滚/编译验证)
            //      全部限定在 open_workspace 指定的根目录内,防路径穿越。
            //      code_edit/code_create_file/code_revert 是变更操作,走审批(不加 AutoApprove)。
            //      code_build 实现了 ITxOffUiThreadTool,编译在后台线程跑,不冻结 PS 主线程。
            reg.Register(new OpenWorkspaceTool());     // 读：打开工作区(项目根目录)
            reg.Register(new CodeOutlineTool());       // 读：C# 文件骨架(类型/成员/行号)
            reg.Register(new CodeReadTool());          // 读：按符号/行号读源码片段
            reg.Register(new CodeSearchTool());        // 读：跨文件搜索(定位首选)
            reg.Register(new CodeEditTool());          // 变更：精确串替换(唯一匹配硬约束)
            reg.Register(new CodeCreateFileTool());    // 变更：新建源码文件
            reg.Register(new CodeRevertTool());        // 变更：回滚到会话首版(从 .txagent_backup)
            reg.Register(new CodeBuildTool());         // 读：编译工作区项目(只回错误诊断)

            // 4.7) 工具组开关 —— 工具按组暴露(ToolGate),code/cee 默认关;
            //      开关持久化,新建对话后生效
            reg.Register(new ListToolGroupsTool());    // 读：列出工具组及启用状态
            reg.Register(new SetToolGroupsTool());     // 变更：开/关工具组(需审批)

            // 4.8) 多 PDPS 环境 —— 同时开多个 PDPS 时跨窗口查询/对比(只读)
            reg.Register(new ListEnvironmentsTool());  // 读：列出所有 PDPS 环境
            reg.Register(new RunInEnvironmentTool());  // 读：在指定环境执行只读工具
            reg.Register(new CompareEnvironmentsTool());// 读：两环境跑同一工具并排对比

            // 4.9) 片段健康 —— 修补丁 / 体检(配合待定池自动固化)
            reg.Register(new PatchSnippetTool());      // 变更：就地修片段一小段(记修订历史)
            reg.Register(new SnippetHealthTool());     // 读：片段库健康体检 + 待定池状态

            // 5) 加载已保存的配方,注册成工具 (启动即可用)
            foreach (var recipe in RecipeStore.All())
                reg.Register(new RecipeTool(recipe, reg));

            // 6) 预置内置配方 —— 已随配方模型升级(工具序列 → 代码)移除:
            // 6) 预置内置配方 —— 已随配方模型升级(工具序列 → 代码)移除:
            //    旧的内置配方基于 RecipeStep 步骤序列,新模型是"代码 + 参数声明"。
            //    模型侧如需固化,用 save_recipe 或侧边栏的 promote 功能。

            // 7) 自动发现:反射扫程序集里所有实现 ITxAgentTool 且有公共无参构造的类,
            //    自动补注册。这样以后新加 XxxTool.cs 只要有默认构造函数,扔进项目即可,
            //    不用再回来改这里。同名已注册的一律跳过 —— 上面手工注册的带参构造工具优先。
            AutoRegisterTools(reg);

            return reg;
        }

        /// <summary>需要依赖注入的工具 (如 SaveRecipeTool(reg) / AddFactTool(convIdSupplier) / RecipeTool(recipe,reg))</summary>
        private static void AutoRegisterTools(ToolRegistry reg)
        {
            var asm = typeof(TxAgentCommand).Assembly;
            var toolType = typeof(ITxAgentTool);

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                // 某些类型的依赖 dll 缺失,只保留能加载的部分继续
                types = ex.Types.Where(t => t != null).ToArray();
            }

            int registered = 0, skipped = 0, failed = 0;
            foreach (var t in types)
            {
                if (t == null) continue;
                if (t.IsAbstract || t.IsInterface || !t.IsClass) continue;
                if (!toolType.IsAssignableFrom(t)) continue;

                // 只处理有 public 无参构造的类;带参构造的靠上面手工注册
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor == null) continue;

                try
                {
                    var tool = (ITxAgentTool)ctor.Invoke(null);
                    if (string.IsNullOrWhiteSpace(tool.Name))
                    {
                        System.Diagnostics.Debug.WriteLine("[TxAgent] 跳过空名工具类型: " + t.FullName);
                        continue;
                    }

                    ITxAgentTool existing;
                    if (reg.TryGet(tool.Name, out existing))
                    {
                        skipped++;
                        continue; // 已注册,不覆盖
                    }

                    reg.Register(tool);
                    registered++;
                    System.Diagnostics.Debug.WriteLine(
                        "[TxAgent] auto-registered: " + tool.Name + "  (" + t.FullName + ")");
                }
                catch (Exception ex)
                {
                    failed++;
                    System.Diagnostics.Debug.WriteLine(
                        "[TxAgent] auto-register failed: " + t.FullName + " -> " + ex.Message);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "[TxAgent] AutoRegisterTools 完成: new=" + registered
                + " skipped=" + skipped + " failed=" + failed);
        }

        private static IWin32Window TryGetPsMainWindow()
        {
            // 反射容忍 SDK 版本差异 (静态成员 dynamic 仍在编译期解析，故必须反射)。
            // 优先 MainForm(IWin32Window)，回退 MainScreenHandle(IntPtr) 包一层。
            try
            {
                var t = typeof(TxApplication);
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

                var mainForm = t.GetProperty("MainForm", flags);
                if (mainForm != null)
                {
                    var w = mainForm.GetValue(null, null) as IWin32Window;
                    if (w != null) return w;
                }

                var handleProp = t.GetProperty("MainScreenHandle", flags);
                if (handleProp != null)
                {
                    var v = handleProp.GetValue(null, null);
                    if (v is IntPtr) return new Win32Window((IntPtr)v);
                }
            }
            catch { }
            return null;
        }

        /// <summary>把一个窗口句柄包成 IWin32Window，供 Form.Show(owner) 让窗口归属 PS 主窗口。</summary>
        private sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }

    /// <summary>Process lifetime owner. Its dispatcher survives every Agent window.</summary>
    internal sealed class DshPetController : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Control _dispatcher;
        private readonly Action<bool> _saveVisibility;
        private readonly Action _openAssistant;
        private readonly Action<JObject> _openRecipes;
        private DshPetHost _host;
        private string _state;
        private string _task;
        private bool _enabled;
        private bool _disposed;
        public event EventHandler Changed;
        public event Action<string> Failed;
        public bool Enabled { get { lock (_gate) return _enabled; } }

        public DshPetController(bool enabled, Action<bool> saveVisibility, Action openAssistant, Action<JObject> openRecipes = null)
        {
            _enabled = enabled;
            _saveVisibility = saveVisibility;
            _openAssistant = openAssistant;
            _openRecipes = openRecipes;
            _dispatcher = new Control();
            var handle = _dispatcher.Handle;
            Application.ApplicationExit += OnApplicationExit;
            AppDomain.CurrentDomain.ProcessExit += OnApplicationExit;
            _dispatcher.BeginInvoke(new Action(EnsurePet));
        }

        public void SetEnabled(bool enabled)
        {
            if (_dispatcher.InvokeRequired)
            {
                _dispatcher.BeginInvoke(new Action(() => SetEnabled(enabled)));
                return;
            }
            DshPetHost old = null;
            bool changed;
            lock (_gate)
            {
                if (_disposed) return;
                changed = _enabled != enabled;
                _enabled = enabled;
                if (!enabled) { old = _host; _host = null; }
            }
            if (changed)
            {
                try { _saveVisibility(enabled); }
                catch (Exception ex) { Failed?.Invoke("桌宠显示状态保存失败: " + ex.Message); }
            }
            old?.Dispose();
            if (enabled) EnsurePet();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void EnsurePet()
        {
            lock (_gate)
            {
                if (_disposed || !_enabled || (_host != null && !_host.IsDisposed)) return;
                try
                {
                    var host = new DshPetHost(_dispatcher);
                    host.OpenAssistantRequested += (s, e) => _openAssistant();
                    host.OpenRecipesRequested += anchor => _openRecipes?.Invoke(anchor);
                    host.HideRequested += (s, e) =>
                    {
                        if (ReferenceEquals(_host, host)) SetEnabled(false);
                    };
                    host.Failed += message => Failed?.Invoke(message);
                    host.Closed += (s, e) =>
                    {
                        lock (_gate) { if (ReferenceEquals(_host, host)) _host = null; }
                        Changed?.Invoke(this, EventArgs.Empty);
                    };
                    _host = host;
                    host.SetState(_state, _task);
                }
                catch (Exception ex) { Failed?.Invoke(ex.Message); }
            }
        }

        public void SetState(string state, string task = null)
        {
            lock (_gate)
            {
                _state = state;
                _task = task;
                _host?.SetState(state, task);
            }
        }

        public void SetThinking(bool thinking)
        {
            lock (_gate)
            {
                if (thinking) SetState("thinking");
                else if (_state != "success" && _state != "error") SetState(null);
            }
        }

        private void OnApplicationExit(object sender, EventArgs e) { Dispose(); }

        public void Dispose()
        {
            DshPetHost host;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                host = _host;
                _host = null;
            }
            Application.ApplicationExit -= OnApplicationExit;
            AppDomain.CurrentDomain.ProcessExit -= OnApplicationExit;
            host?.Dispose();
            // ProcessExit can run off the UI thread; the process will release the HWND.
            if (!_dispatcher.InvokeRequired) _dispatcher.Dispose();
        }
    }

    /// <summary>Owns dsh-pet's transparent Electron window and its local status channel.</summary>
    internal sealed class DshPetHost : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Control _dispatcher;
        private readonly string _stateFile;
        private readonly object _logGate = new object();
        public string LogPath { get; private set; }
        private System.Threading.Timer _startupTimer;
        private Process _process;
        private long _revision;
        private string _state;
        private string _task;
        private bool _closed;
        private bool _ready;
        private bool _shutdownRequested;
        private bool _failureReported;
        public event Action<string> Failed;
        public event EventHandler OpenAssistantRequested;
        public event EventHandler HideRequested;
        public event Action<JObject> OpenRecipesRequested;
        public event EventHandler Closed;
        public bool IsDisposed { get { lock (_gate) return _closed; } }
        public bool IsReady { get { lock (_gate) return _ready && !_closed; } }

        public DshPetHost(Control dispatcher)
        {
            _dispatcher = dispatcher;
            string temp = TxToolsTemp.DirectoryFor("Agent", "DshPet", Process.GetCurrentProcess().Id.ToString());
            _stateFile = System.IO.Path.Combine(temp, Guid.NewGuid().ToString("N") + ".json");
            LogPath = _stateFile + ".log";
            string assemblyDir = System.IO.Path.GetDirectoryName(typeof(TxAgentCommand).Assembly.Location);
            string runtime = System.IO.Path.Combine(assemblyDir, "Agent", "UI", "pet", "dsh-pet");
            // A shadow-copied assembly may not have its sidecar files next to Location.
            Uri origin;
            if (!System.IO.File.Exists(System.IO.Path.Combine(runtime, "entry.js")) &&
                Uri.TryCreate(typeof(TxAgentCommand).Assembly.CodeBase, UriKind.Absolute, out origin) && origin.IsFile)
            {
                string originalRuntime = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(origin.LocalPath), "Agent", "UI", "pet", "dsh-pet");
                if (System.IO.File.Exists(System.IO.Path.Combine(originalRuntime, "entry.js"))) runtime = originalRuntime;
            }
            Log("Assembly: " + typeof(TxAgentCommand).Assembly.Location + "\nRuntime: " + runtime);
            string electron = System.IO.Path.Combine(runtime, "electron", "electron.exe");
            string entry = System.IO.Path.Combine(runtime, "entry.js");
            var missing = new[] { "entry.js", "startup-monitor.js", "quick-launch.js", "host-routes.js", "config.json",
                "electron/electron.exe", "electron/ffmpeg.dll", "electron/icudtl.dat", "electron/resources.pak",
                "upstream/index.html", "upstream/main.js", "upstream/preload.js", "upstream/shared-core.js" }
                .Where(name => !System.IO.File.Exists(System.IO.Path.Combine(runtime, name))).ToArray();
            if (missing.Length != 0)
            {
                string error = "桌宠文件缺失: " + string.Join(", ", missing) + "\n资源目录: " + runtime;
                Log(error);
                throw new System.IO.FileNotFoundException(error + "\n日志: " + LogPath);
            }
            WriteSnapshot();
            var start = new ProcessStartInfo(electron, "\"" + entry + "\"")
            {
                WorkingDirectory = runtime,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            start.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            start.EnvironmentVariables.Remove("DSH_PET_DPI_PROBE");
            start.EnvironmentVariables.Remove("DSH_PET_SMOKE");
            start.EnvironmentVariables["DSH_PET_HOST_PID"] = Process.GetCurrentProcess().Id.ToString();
            start.EnvironmentVariables["TXAGENT_PET_STATE"] = _stateFile;
            start.EnvironmentVariables["TXAGENT_PET_PROFILE"] = _stateFile + ".profile";
            start.EnvironmentVariables["TXAGENT_PET_CLICK_DELAY"] = GetDoubleClickTime().ToString();
            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null) Log(e.Data);
                HandleHelperMessage(e.Data);
            };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            _process.Exited += (s, e) =>
            {
                bool expected;
                lock (_gate) expected = _shutdownRequested;
                // A user close request may race stdout delivery.
                try { expected |= (bool?)JObject.Parse(System.IO.File.ReadAllText(_stateFile))["closed"] == true; } catch { }
                string exit = "unknown";
                try { exit = ((Process)s).ExitCode.ToString(); } catch { }
                Log("Helper exit code: " + exit + "; expected: " + expected);
                _startupTimer?.Dispose();
                if (!expected) ReportFailure("桌宠进程退出，退出码: " + exit);
                lock (_gate) _closed = true;
                try { System.IO.File.Delete(_stateFile); } catch { }
                Dispatch(() => Closed?.Invoke(this, EventArgs.Empty));
            };
            try
            {
                _startupTimer = new System.Threading.Timer(_ =>
                {
                    lock (_gate) { if (_ready || _closed || _shutdownRequested) return; }
                    ReportFailure("桌宠启动超时，尚未检测到可见且正在播放的动画。");
                    Dispose();
                }, null, 70000, System.Threading.Timeout.Infinite);
                _process.Start();
                Log("Helper PID: " + _process.Id);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _startupTimer?.Dispose();
                Log(ex.ToString());
                _process.Dispose();
                _process = null;
                try { System.IO.File.Delete(_stateFile); } catch { }
                throw new InvalidOperationException("桌宠启动失败: " + ex.Message + "\n日志: " + LogPath, ex);
            }
        }

        private void Log(string message)
        {
            Debug.WriteLine("[TxAgent pet] " + message);
            try
            {
                lock (_logGate) System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private void HandleHelperMessage(string line)
        {
            if (line == null || !line.StartsWith("txagent-pet:", StringComparison.Ordinal)) return;
            try
            {
                var message = JObject.Parse(line.Substring("txagent-pet:".Length));
                switch ((string)message["kind"])
                {
                    case "open-recipes":
                    case "manage-recipes":
                        Dispatch(() => OpenRecipesRequested?.Invoke(message));
                        break;
                    case "open-assistant":
                        Dispatch(() => OpenAssistantRequested?.Invoke(this, EventArgs.Empty));
                        break;
                    case "ready":
                        lock (_gate) _ready = true;
                        _startupTimer?.Dispose();
                        break;
                    case "hidden":
                        lock (_gate) _shutdownRequested = true;
                        Dispatch(() => HideRequested?.Invoke(this, EventArgs.Empty));
                        break;
                    case "closed":
                        lock (_gate) _shutdownRequested = true;
                        break;
                    case "failed":
                        ReportFailure((string)message["message"]);
                        break;
                }
            }
            catch (Exception ex) { Log(ex.ToString()); }
        }

        private void ReportFailure(string message)
        {
            lock (_gate)
            {
                if (_failureReported || _shutdownRequested) return;
                _failureReported = true;
            }
            Log("FAILED: " + message);
            Dispatch(() => Failed?.Invoke(message + "\n桌宠日志: " + LogPath));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDoubleClickTime();

        private void Dispatch(Action action)
        {
            try
            {
                if (_dispatcher != null && !_dispatcher.IsDisposed && _dispatcher.IsHandleCreated)
                    _dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException) { }
        }

        public void SetState(string state, string task = null)
        {
            if (state != null && !new[] { "thinking", "working", "result", "waiting", "success", "error" }.Contains(state))
                throw new ArgumentOutOfRangeException(nameof(state));
            lock (_gate)
            {
                if (_closed || (_state == state && _task == task)) return;
                _state = state;
                _task = task;
                _revision++;
                WriteSnapshot();
            }
        }

        public void SetThinking(bool thinking)
        {
            lock (_gate)
            {
                if (thinking) SetState("thinking");
                else if (_state != "success" && _state != "error") SetState(null);
            }
        }
        public void Celebrate() { SetState("success"); }

        private void WriteSnapshot()
        {
            string temporary = _stateFile + ".tmp";
            try
            {
                var value = new JObject
                {
                    ["state"] = _state == null ? JValue.CreateNull() : new JValue(_state),
                    ["task"] = _task == null ? JValue.CreateNull() : new JValue(_task),
                    ["ts"] = _revision,
                    ["closed"] = _closed
                };
                // The PS host may preload an older Json.NET without ToString(Formatting).
                // Parameterless ToString is supported there too; whitespace is valid JSON.
                System.IO.File.WriteAllText(temporary, value.ToString(), new System.Text.UTF8Encoding(false));
                if (System.IO.File.Exists(_stateFile)) System.IO.File.Replace(temporary, _stateFile, null);
                else System.IO.File.Move(temporary, _stateFile);
            }
            catch (Exception ex) { Log("status write: " + ex.Message); }
            finally { try { System.IO.File.Delete(temporary); } catch { } }
        }

        public void Dispose()
        {
            Process process;
            lock (_gate)
            {
                process = _process;
                if (_closed) return;
                _shutdownRequested = true;
                _closed = true;
                _startupTimer?.Dispose();
                _revision++;
                WriteSnapshot();
            }
            // Avoid blocking the Process Simulate UI while Electron drains its renderer.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { if (process != null && !process.WaitForExit(2500)) process.Kill(); } catch { }
                finally
                {
                    if (process != null) process.Dispose();
                    try { System.IO.File.Delete(_stateFile); } catch { }
                }
            });
        }
    }

}
