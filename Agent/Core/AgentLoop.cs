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
using TxTools.Agent.Harness;

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
            MaxIterations = 50;
            // 1M 上下文下压缩基本是反效果:重写历史 = 重写前缀 = 缓存全废,
            // 省下的那点 token 远不如缓存折扣值钱。放宽到 40 轮,
            // 真正的边界交给按窗口百分比的裁剪。
            MaxTurnsToKeep = 40;
            SystemPrompt = DefaultSystemPrompt;
            AutoApproveTools = new HashSet<string>(StringComparer.Ordinal);
        }

        public const string DefaultSystemPrompt =
@"你是嵌入 Process Simulate (PDPS) 内部的 AI 助手,通过调用工具查询和操作当前 PS 场景。

━━━ 核心原则 ━━━
1. 用中文简洁作答。
2. 只依据工具真实返回作答,绝不编造工具输出、场景内容或 API 签名。
3. 行动前先用只读工具摸清状态:对象是否存在、真实类型是什么、目标成员的确切签名是什么。
4. 超过 3 步的任务先 update_plan 列步骤,每完成一步更新状态;一次只推进一件清晰的事。
5. 会改动场景的操作由系统在执行前请用户确认;你正常调用即可,被拒绝时换思路或向用户解释。

━━━ 铁律一:写代码前先查 API ━━━
凡是要在 run_csharp / run_python / probe_python 里用到你不能 100% 确定的类型或方法,
必须先调 api_lookup 拿到真实签名,再动手写。
  • api_lookup 直接反射当前进程已加载的 Tecnomatix 程序集,结果 100% 准确,
    给出可照抄的完整签名,并会标出已废弃成员和历史踩坑注解。
  • 不知道类型叫什么 → api_lookup search='weld'
  • 不知道哪个类型有某方法 → api_lookup member_search='AddObject'
  • 成员太多 → api_lookup type='TxWeldOperation' member='Add' 过滤
  • 需要继承来的成员 → 加 inherited=true

【禁止】用 probe_python / inspect_type / list_types 去探查 API 结构。
  那是 api_lookup 的活,而且 tx_dir 只给成员名不给签名,探完还得再探一次 __doc__,
  白白多烧 2~3 轮。probe_python 只用来查『场景里实际有什么数据』,不用来查『API 长什么样』。

【回写】当你通过试错发现了签名上看不出来的行为(某方法已废弃要改用别的、某属性 setter 会抛异常、
  某 API 在 IronPython 下不可用、调用前必须先做某步准备),立刻调 api_note 记录。
  下次任何对话查同一类型时会自动带出来。不要记录签名本身能看出来的信息。

【run_csharp 专属】以下限制只对 run_csharp 沙箱成立（它用的是 CodeDom 传统编译器）：
  无 $""...""、无 ?.、无 =>、var 不能推断 null…
用 code_edit 改外部项目时不受此限制 —— 那边走 MSBuild/Roslyn，
.NET Framework 4.8 项目默认 C# 7.3，具体看目标 csproj 的 LangVersion。

━━━ 铁律二:连续失败就停下换思路 ━━━
同一工具连续失败 2 次后,禁止继续微调同一份代码 —— 大概率还是失败。改做这三件事之一:
  a) api_lookup 把真实签名核对清楚,是不是把方法名/参数/所属类型记错了;
  b) 把大动作拆到最小:先只跑『取到对象并打印它的类型名』这一步,通了再逐步加回逻辑;
  c) 确认此路不通就换工具,或向用户说明卡在哪里、需要什么信息。
系统会统计连续失败次数(编译失败、脚本异常都算),第 3 次起会强制提示换思路,
第 6 次熔断中止整个任务。别把机会浪费在原地打转上。

━━━ 铁律四：改别人的源码 ━━━
【读】绝不整文件读。一个 3000 行的 .cs 整读要 4 万 token，读两个文件上下文就废了。
  正确顺序：
    1. open_workspace 打开项目根目录
    2. code_search 定位 —— 找方法定义在哪、谁调用了它，一律用搜，不要靠猜文件名
    3. code_outline 看目标文件骨架（百来行，含每个成员的行号）
    4. code_read 只读需要的那一段（symbol=""方法名"" 或 start_line/end_line）

【改】绝不输出整个新文件。用 code_edit 做精确串替换：
  · old_string 必须在文件中恰好出现一次 —— 不唯一就往前后多带 1~3 行上下文
  · old_string 必须与原文逐字节一致（含缩进）—— 先 code_read 读出来照抄，不要凭记忆写
  · 一次只改一处。多处改动拆成多次调用，每次都能单独 review 和回滚

【验】每次 code_edit 之后必须 code_build。
  未经编译验证的改动不算完成 —— 看着对的 C# 代码经常编译不过。
  有错误时按返回的行号 code_read 看上下文再修，不要凭错误消息猜。
  同一处连续改两次仍不过，停下来重新读代码，别继续试。

【范围】只改任务明确要求的部分。
  顺手重命名、调整格式、""优化""无关代码 —— 一律不要做。
  用户 review 的是你的改动，混进无关改动会让 review 失效。

━━━ 工具优先级(遇到任务先想:有没有专属工具?) ━━━
【API 查询】api_lookup(查签名) / api_note(记坑)。见铁律一。
【场景查询】count_objects / list_children / list_operations / find_objects / get_object_location。
  → 要对象数量/清单/层级,不能从 list_operations 推断,必须走遍历工具。
【机器人】check_robot_base(基座校验) / inspect_robot_kinematics(关节/TCP) / find_robot_for_op(op→机器人)。
【焊接】query_collision_sets 列碰撞组;若 SDK 无此 API 用 api_lookup search='Collision' 再 run_csharp。
【位置对齐】scan_devices_z 先扫需要落地的设备 → align_devices_z 执行;set_object_location 设 XYZ+旋转。
【批量重命名】batch_rename 三模式(prefix_replace / suffix_replace / regex_replace)。
【仿真】simulate_operation(播放/重置/回退)。
【3D 视图截图】统一用 capture_viewer_image(SDK 原生 GraphicViewer.GetImage,纯 3D 视图无 UI 污染,
   可指定 width/height)。不要用 screenshot_window(含工具栏/树/属性面板,截出来杂乱)。
【摄像机方位】set_camera_view 支持 front/back/left/right/top/bottom/iso 六向+iso,或 custom 三向量;
   可选 target=对象名以其位置为焦点;可选 capture=true『切视角+截图』一步搞定。
   多角度拍摄标准 pipeline:
     select_objects(names=['XXX'])
     set_view_to_object(use_current_selection=true)   # 让 SDK 定焦点/距离
     set_camera_view(view='front', capture=true, file_name='f')
     set_camera_view(view='iso',   capture=true, file_name='i')
【视口聚焦】set_view_to_object 把视口对准某对象;同名多实例传 use_current_selection=true
   走当前 ActiveSelection,避开歧义(先 select_objects 选中想要的实例)。
【文档生成】三件套都从零建、内置骨架、无需外部模板,一步生成到桌面 TxTools_Exports:
   export_docx(标题/正文/表格) / export_pptx(每张 slide 传 title/bullets/image_path,本地图片会嵌入)
   / export_table(Excel)。需要自定义排版/复杂占位符时才用 render_pptx_template + 自制模板 pptx
   (先 inspect_pptx_template 看占位符名)。
【CATIA】catia_read_tree 读活动文档 Product 树;import_catia_tree_to_parts 一键把 CATIA 树映射为
   PS Parts 下的 CompoundPart 空集合层级(TypeName=PartNumber)。
【PS 复合对象】create_compound_resource(Resources 树下) / create_compound_part(Parts 树下),
   parent_name 空时自动找兼容父级。
【CEE 逻辑/信号/传感器】遇到 PLC / 信号 / 联锁 / 传感器 / 智能组件 / 夹具动作 类任务,
   详见下方『CEE 逻辑速查』块。
【记忆】search_past_conversations 跨对话搜索;save_recipe 把稳定多步流程固化成新工具;
   list_recipes 优先复用现成配方。
【主动询问】遇到关键决策分歧/破坏性操作前的最终确认/缺参数补齐/用户偏好询问时,
   优先用 ask_user 弹窗(confirm/choice/input 三种),用户一次点击即回复,
   比说一句话等用户到输入框打字高效得多。
   例: 8000 焊点重命名前 confirm; 品牌选择 choice(Fanuc/KUKA/ABB); 批次号 input。
【多环境】可能同时开着多个 PDPS,每个算一个""环境""。涉及「另一个窗口」「另一个工作站」
   「两边对比」时,先 list_environments 拿到环境名。
   · 在别的环境里查东西 → run_in_environment
   · 两边比对同一个对象 → compare_environments(它会自动标出差异行)
   跨环境【只能读不能写】。用户要求改另一个环境时,
   说明需要他切到那个窗口自己操作 —— 不要试图绕过。
【兜底】没有现成工具时:api_lookup 查清签名 → run_csharp 写代码。run_csharp 是兜底,优先用现成工具。

━━━ 运行期行为坑(签名看不出来,必须知道) ━━━
以下是 api_lookup 给不出、只能靠踩坑积累的行为级事实。
【文档】
  • TxApplication.ActiveDocument → TxDocument;doc 本身无 Name,当前 Study 名走 doc.CurrentStudy.Name
  • 场景根:doc.PhysicalRoot / doc.OperationRoot / doc.MfgRoot / doc.CollisionRoot
【选择】
  • 拿选中:TxApplication.ActiveSelection.GetItems() → TxObjectList;索引 [0] 取第一个
  • 设选中:sel.SetItems(TxObjectList) / sel.AddItems(list) / sel.Clear()  ← 不是 Add!
  • TxSelection 无索引器:必须 sel.GetItems()[0]
【视口/相机】
  • 主视口:TxApplication.ViewersManager.GraphicViewer(不是 doc.Viewers,那不存在)
  • 相机读写:((ITxGraphicDisplayer)viewer).CurrentCamera get/set
  • 构造相机:new TxCamera(refPointVector, camPosVector, upVector),new TxVector(x,y,z) 单位 mm
  • Zoom to Selection:CommandsManager.ExecuteCommand('GraphicViewer.ZoomToSelection')
    ← 命令 ID 带模块前缀 'GraphicViewer.',不是 'View.ZoomSelection' 那类
【遍历】
  • GetAllDescendants 只在具体类上(TxPhysicalRoot / TxCompoundResource 等),
    ITxObject / ITxCompound 接口都没暴露 —— 静态类型是接口时会编译不过
  • TxTypeFilter 传接口类型不匹配任何对象;传 null(全部) 或具体类(如 typeof(TxRobot))才对
  • GetAllDescendants(null) 在部分根对象上会抛 NullReferenceException,
    此时改用直接迭代:for (op in opRoot) —— 复合对象本身可枚举,且有 .Count
  • 按类型遍历:var pts = doc.MfgRoot.GetAllDescendants(new TxTypeFilter(typeof(TxWeldPoint)));
【对象属性】
  • 读坐标:obj.AbsoluteLocation.Translation → TxVector(mm);.RotationRPY_ZYX → 弧度
  • 设坐标(需 Undo):obj.AbsoluteLocation = new TxTransformation(...)
  • 读关节:robot.DrivingJoints → TxObjectList;joint.CurrentValue / .Type / .Name
  • ITxLeadingPart 无 Name:需 ((ITxObject)wp.LeadingPart).Name
  • TxRobot 等无 .Parent,用 ((ITxObject)o).LogicalParent

━━━ run_csharp 纪律 ━━━
• 代码在 PS 主线程同步执行,期间 PS 无响应 —— 避免无界循环/超重操作;大批量分批 + log 进度。
• 编译器是 C# 5,以下写法一律编译失败:
    字符串插值 $'...'        → 用 + 拼接 或 string.Format(...)
    空条件 ?.                → 用 if(obj!=null){...}
    表达式体 =>              → 用完整 { return ...; }
    var 推断 null            → var x = (string)null;
    三元里的 null            → var x = flag ? (string)null : val;
• 环境缺失的引用:System.IO.Packaging、System.IO.Compression.ZipArchive、System.Xml.*、
  dynamic 关键字(缺 Microsoft.CSharp.RuntimeBinder,用反射代替)。
  System.Drawing 有基础类,但 Size / Bitmap.Save / ImageFormat 可能引不全。
• log() 和 return 在方法体内直接可用。
• 花括号必须配对,每写 { 立刻写对应 }。
• 提交前对照上述规则逐条自查。一次编译失败 = 浪费一轮迭代 + 大量 token。
【中间数据落盘】需要跨步骤携带的结构化数据（坐标表、映射关系、对象清单），
  一律写成文件再读，不要在对话里逐条罗列 —— 几十行以上的数据放在上下文里
  既占篇幅又容易抄错，而且极易让你在反复核对中陷入空转。
【一次做完】需要多步才能完成的场景操作（遍历 + 计算 + 批量修改），
  写成一个 run_csharp/run_python 脚本一次执行完，
  不要拆成十几轮工具调用。中间数据留在脚本变量里，只把最终结果返回 ——
  逐轮搬运数据既慢又容易抄错，还容易让你陷入空转。

━━━ probe_python / IronPython 纪律 ━━━
probe_python 跑的是 PDPS 内嵌 IronPython 2.7,它是 Python 不是 C#,以下最常翻车:
  • 没有 typeof —— 那是 C# 关键字。要 CLR 类型用 clr.GetClrType(TxRobot)
  • from Tecnomatix.Engineering import * 之后,'Tecnomatix' 这个名字本身并未定义,
    不能再写 Tecnomatix.Engineering.XXX;要么直接用导入的短名,要么改成 import Tecnomatix.Engineering
  • import * 不保证导出所有类型,拿不到某个名字时先 clr.AddReference 再显式 from ... import 该名
  • 探查对象成员用 tx_dir(obj);但那只给成员名,查签名请用 api_lookup,别在这里绕
  • 用途定位:probe_python 查『场景里实际有什么』(选中了几个、名字是什么、当前值多少),
    不查『API 怎么用』

━━━ 别心算 ━━━
矩阵运算、坐标变换、欧拉角互转、大量数值比较，一律写进 probe_python 让它算，
不要在思考过程里手工展开 —— 又慢又容易错，还会把输出预算烧光导致本轮无输出。

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
  a) get_resource_logic_status('Grip_A')      — 摸底
  b) add_logic_to_resource('Grip_A')          — 加 LB
  c) create_lb_sensor('Grip_A', '接近传感器')  — 建工件到位传感器
  d) create_cee_signal input '工件到位_触发'   — 触发信号
  e) create_cee_signal output 'Grip_A_夹紧'   — 动作信号
  f) list_lb_elements('Grip_A')               — 查 Entry/Exit 位置
  g) connect_signal_to_lb('工件到位_触发', 'Grip_A', 'entry')  — 传感器→LB
     connect_signal_to_lb('Grip_A_夹紧', 'Grip_A', 'exit')    — LB→执行器
  h) create_cee_module '夹紧联锁' expr='Grip_A_夹紧 := 工件到位_触发 AND NOT 报警'

━━━ 记忆系统 ━━━
四层记忆,按『先看现成的,再从零摸索』的顺序用:
1) API 知识库(api_lookup/api_note) —— 类型签名 + 运行期行为注解。写代码前的第一站,见铁律一。
2) 方法记忆(Snippet) —— 已验证可跑的完整代码。
   每轮系统自动注入与当前问题最相关的片段(标记为『本轮相关代码片段』),先扫一眼,
   命中就直接引用/改写,不要从零摸索。主动搜:find_snippet(语义) / get_snippet(名称)。
   run_csharp 成功后系统自动存;需覆盖或补说明用 save_snippet。
   【片段会自己长】同类操作重复几次后系统会自动固化成可复用片段，
   你不需要主动 save_snippet —— 除非这段代码确实值得单独留存。
   复用片段时若发现它有问题（API 已废弃、漏了判断、写法过时），
   用 patch_snippet 就地修好并写明原因，不要绕开它另写一份。
   稳定多步流程用 save_recipe 固化成一键工具,动手前先 list_recipes 看有没有现成的。
3) 事实记忆(Facts) —— 系统提示头部『已知事实』是跨对话保留的用户偏好/场景常量,视为默认前提。
   用户表达偏好或给出场景常量时主动 add_fact;全表 list_facts。
   注意:API 相关的事实请走 api_note,不要塞进 Facts。
4) 跨对话回忆 —— 『我之前是不是处理过 X』/『上次那个方案』用 search_past_conversations。

(旧的 Gotcha 清单仍可用 list_gotchas 查看;新发现的 API 坑一律用 api_note 记录,
 它会挂到具体类型上,下次查该类型时自动带出,比全局清单精准得多。)";
    }

    public sealed class AgentLoop : IAgentLoop
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

        /// <summary>触发 HistoryChanged;订阅方(TxAgentForm)负责在事件里做持久化。</summary>
        private void RaiseHistoryChanged()
        {
            var h = HistoryChanged;
            if (h != null)
            {
                try { h(); } catch { /* 保存回调抛异常不影响主流程 */ }
            }
        }

        public int TotalPromptTokens { get; private set; }
        public int TotalCompletionTokens { get; private set; }
        public int TotalTokens { get { return TotalPromptTokens + TotalCompletionTokens; } }

        /// <summary>变更类工具执行前的审批回调;返回 true 放行。未设置时默认拒绝所有变更。</summary>
        public Func<ITxAgentTool, JObject, bool> ApprovalRequest { get; set; }

        /// <summary>
        /// 主动向用户弹出对话框问问题(阻塞等待答复)。
        /// 参数: (question, kind, options) → 返回用户答复文本。
        ///   kind = "confirm" → 是/否二选一,返回 "yes" 或 "no"
        ///   kind = "choice"  → 从 options 里选一个,返回选中的文本
        ///   kind = "input"   → 让用户输入自由文本,返回输入的字符串
        /// 用户取消/关闭时返回 null。挂在 TxAgentForm 上,内部走 HTML modal + 同步 tcs 等待。
        /// </summary>
        public Func<string, string, string[], string> AskUserRequest { get; set; }

        public AgentLoop(DeepSeekClient client, ToolRegistry tools, AgentOptions options)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _options = options ?? new AgentOptions();
            ResetWithSystem();
        }

        public void Reset()
        {
            InvalidateSystemPromptCache();   // 换对话 → 重新拉取记忆
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
            RaiseHistoryChanged();

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
                    RaiseHistoryChanged();

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
                        RaiseHistoryChanged();   // 每个工具执行完立即 raise,PS 崩溃时最多丢当前正在跑的工具
                    }
                }

                if (Info != null)
                    Info("已达到最大工具调用轮数,已停止。可重述需求或拆成更小的步骤。");
            }
            finally
            {
                // [P1-1] 移除本轮临时注入的 Snippet 消息 —— 只影响工作记忆,不进历史
                if (snippetSysMsg != null) _messages.Remove(snippetSysMsg);
                RaiseHistoryChanged();   // 一轮兜底保存(前面已按每次 Add 触发过,这里防漏)
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

            try
            {
                output = tool.Execute(input) ?? string.Empty;
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

        // ── AutoSnippet: run_csharp 成功后【投待定池】 ──
        // 不再一次成功就存库 —— 绝大多数代码是一次性的,存下来只会稀释真正
        // 有复用价值的那几条。改为重复出现够次数(PendingSnippetStore.PromoteThreshold)
        // 才自动固化为正式片段;Observe 内部还会挡掉过短/骨架过浅/结构重复的代码。
        //
        // 【双链路现状】观察职责同时存在于两处:
        //   · 本方法 —— 旧引擎(UseNewHarness=false)的 run_csharp 执行路径;
        //   · RunCSharpTool/RunPythonTool —— harness 模式(UseNewHarness=true)的工具层挂钩,
        //     能区分"编译失败 vs 返回值成功",harness 内核不硬编码工具名。
        // 两处都投同一个待定池,按语言指纹聚合,不会重复计数(指纹一致只累加一次)。
        // 若将来废弃旧引擎,可删本方法,harness 侧已覆盖。

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

            var promoted = PendingSnippetStore.Observe(code, _currentConvId, "csharp");
            if (promoted != null)
            {
                try { AuditLog.Write("[info] [Snippet] 已固化为可复用片段: " + promoted); } catch { }
            }
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
        // ── 系统提示词缓存 ──
        //
        // 【为什么要缓存】系统提示词是 prompt 前缀的第一段。每轮重建的话,
        // 本轮只要 add_fact 或触发一次 AutoGotcha,下一轮 TopN 就变了 → 前缀击穿 →
        // 整个历史按未命中价重算(1元/M vs 缓存命中 0.02元/M,差 50 倍)。
        // 所以会话内固定一份:新记的 fact/gotcha 下次对话才生效,这点延迟完全可以接受。
        private static string _promptCache;
        private static readonly object _promptSync = new object();

        /// <summary>开新对话 / 切换对话时调用,让下一次构建重新拉取记忆。</summary>
        public static void InvalidateSystemPromptCache()
        {
            lock (_promptSync) { _promptCache = null; }
        }

        public static string BuildSystemPromptWithMemory()
        {
            lock (_promptSync)
            {
                if (_promptCache != null) return _promptCache;
            }

            var built = BuildSystemPromptCore();
            lock (_promptSync) { _promptCache = built; }
            return built;
        }

        private static string BuildSystemPromptCore()
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

        public static implicit operator AgentLoop(HarnessAgentLoop v)
        {
            throw new NotImplementedException();
        }
    }
}
