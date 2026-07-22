// TxTools.Agent / Tools / PlcTools.cs
// CEE 内部逻辑控制工具集（Cyclic Event Evaluator — Process Simulate 内置 PLC）。
//
// 严格区分两种使用场景：
//   External PLC：信号带 I/O 地址(I1.0/Q1.0)，通过 OPC 连接外部 PLC
//   CEE 内部逻辑：信号无地址需求，连接 Logic Block Entry/Exit，
//                 由 Modules Viewer 层级调度，SCL 文本编程
//
// CEE 三种逻辑创建方式（均通过 Tecnomatix.Engineering.Plc 命名空间接口）：
//   1. Logic Block (LB) — "Add Logic to Resource" → Smart Component
//   2. SCL Editor — "Create SCL Container" → 结构化文本编程
//   3. Modules Viewer — 信号表达式层级，IF/ELSE 条件分支
//
// 所有 PS SDK 调用经 PsBridge → PsContext.Current.Run(...) 路由回 PS 主线程。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) get_resource_logic_status — 资源 CEE 逻辑状态（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询资源的完整 CEE 逻辑状态：HasPlcAspect、LogicBehavior（Entries/Exits/Actions 数量）、
    /// SclContainer（SCL 代码行数）、关联信号。只读。
    /// </summary>
    public sealed class GetResourceLogicStatusTool : TxAgentToolBase
    {
        public override string Name { get { return "get_resource_logic_status"; } }

        public override string Description
        {
            get
            {
                return "查询资源的 CEE 内部逻辑状态：LogicBehavior（Entry/Exit/Action 数量）、" +
                       "SCL 容器是否存在、关联信号列表、HasPlcAspect 是否加载。" +
                       "name 为资源名（留空用当前选中）。只读。是创建逻辑前的第一步。" +
                       "与外部 PLC 不同，CEE 内部逻辑不需要 I/O 地址。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": {
                            ""type"": ""string"",
                            ""description"": ""资源名称，留空则使用当前选中对象""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.GetResourceLogicStatus(GetString(input, "name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) list_cee_signals — 列出信号（CEE 内外部共用，只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 列出 PLC 程序的所有信号，自动标注 CEE 内部（无地址）vs 外部 PLC（有地址）。
    /// 可选 name_filter 过滤。只读。
    /// </summary>
    public sealed class ListCeeSignalsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_cee_signals"; } }

        public override string Description
        {
            get
            {
                return "列出 PLC 程序中的所有信号，自动区分 CEE 内部信号（无 I/O 地址）" +
                       "和外部 PLC 信号（有 I/O 地址，如 I1.0）。" +
                       "name_filter 可选按信号名过滤。只读。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name_filter"": {
                            ""type"": ""string"",
                            ""description"": ""信号名称关键字(模糊)，留空列出全部""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.ListPlcSignals(GetString(input, "name_filter", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3) list_cee_modules — 列出 CEE 模块层级（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 列出 Modules Viewer 中的所有 CEE 模块及其条目。
    /// 模块用于编写信号表达式和 IF/ELSE 分支。只读。
    /// </summary>
    public sealed class ListCeeModulesTool : TxAgentToolBase
    {
        public override string Name { get { return "list_cee_modules"; } }

        public override string Description
        {
            get
            {
                return "列出 Modules Viewer 中的所有 CEE 模块及其条目（表达式/调用/语句条目）。" +
                       "模块层级结构在 CEE 仿真中决定操作的执行顺序。只读。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override string Execute(JObject input)
        {
            return PsBridge.ListCeeModules();
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 4) create_cee_signal — 创建信号（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 创建 PLC 信号。CEE 内部使用时 address 留空即可，外部 PLC 连接时填写 I/O 地址。
    /// signal_type: input(输入)/output(输出)/display(显示)。变更操作，需审批。
    /// </summary>
    public sealed class CreateCeeSignalTool : TxAgentToolBase
    {
        public override string Name { get { return "create_cee_signal"; } }

        public override string Description
        {
            get
            {
                return "创建 PLC 信号。signal_type: input(输入)/output(输出)/display(显示)。" +
                       "CEE 内部逻辑：address 留空；外部 PLC：填写 I/O 地址(如 I1.0)。" +
                       "data_type: BOOL/INT/REAL/DINT(默认 BOOL)。comment 为注释。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""signal_type"": {
                            ""type"": ""string"",
                            ""enum"": [""input"", ""output"", ""display""],
                            ""description"": ""信号类型：input(输入)/output(输出)/display(显示)""
                        },
                        ""name"": {
                            ""type"": ""string"",
                            ""description"": ""信号名称(必填)""
                        },
                        ""address"": {
                            ""type"": ""string"",
                            ""description"": ""I/O地址(CEE内部留空，外部PLC如 I1.0/Q2.3)""
                        },
                        ""data_type"": {
                            ""type"": ""string"",
                            ""description"": ""数据类型：BOOL/INT/REAL/DINT，默认BOOL""
                        },
                        ""comment"": {
                            ""type"": ""string"",
                            ""description"": ""注释说明""
                        }
                    },
                    ""required"": [""signal_type"", ""name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CreatePlcSignal(
                GetString(input, "signal_type", "input"),
                GetString(input, "name", null),
                GetString(input, "address", null),
                GetString(input, "data_type", "BOOL"),
                GetString(input, "comment", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 5) add_logic_to_resource — 为资源添加逻辑行为（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 为现有 3D 资源添加逻辑行为，创建智能组件（Smart Component）。
    /// 资源必须实现 ITxPlcLogicBehaviorCreation 接口。
    /// 成功后可在 Resource Logic Behavior Editor 中编辑 Entries/Exits/Actions。
    /// </summary>
    public sealed class AddLogicToResourceTool : TxAgentToolBase
    {
        public override string Name { get { return "add_logic_to_resource"; } }

        public override string Description
        {
            get
            {
                return "为现有 3D 资源添加 CEE 逻辑行为（创建智能组件/Smart Component）。" +
                       "添加后资源同时具备 3D 表示、运动学和逻辑行为。" +
                       "成功后可在 Resource Logic Behavior Editor 中编辑 Entries/Exits/Actions（MoveJoint/MoveToPose等）。" +
                       "name 为资源名（留空用当前选中）。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": {
                            ""type"": ""string"",
                            ""description"": ""目标资源名称，留空则使用当前选中对象""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.AddLogicToResource(GetString(input, "name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 6) create_scl_container — 为资源创建 SCL 容器（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 为资源创建 SCL (Structured Control Language) 容器。
    /// SCL 基于 IEC 61131-3 标准，无需编译即可实时执行，适合虚拟调试。
    /// 资源必须实现 ITxPlcSclCreation 接口。
    /// </summary>
    public sealed class CreateSclContainerTool : TxAgentToolBase
    {
        public override string Name { get { return "create_scl_container"; } }

        public override string Description
        {
            get
            {
                return "为资源创建 SCL（结构化控制语言）容器，用于文本编程。" +
                       "SCL 基于 IEC 61131-3 西门子 TIA Portal 版本，无需编译即可实时执行。" +
                       "成功后可在 SCL Editor 中编写功能块代码。" +
                       "name 为资源名（留空用当前选中）。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": {
                            ""type"": ""string"",
                            ""description"": ""资源名称，留空则使用当前选中对象""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CreateSclContainer(GetString(input, "name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 7) copy_logic — 复制逻辑到同类资源（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 将源资源的逻辑行为复制到目标资源。源必须已有 LogicBehavior，目标必须为空。
    /// 适用于批量创建具有相似运动学行为的资源。
    /// </summary>
    public sealed class CopyLogicTool : TxAgentToolBase
    {
        public override string Name { get { return "copy_logic"; } }

        public override string Description
        {
            get
            {
                return "将源资源的 CEE 逻辑行为复制到目标同类资源。" +
                       "源必须已有 LogicBehavior，目标必须为空且同类型。" +
                       "适用于批量创建具有相似运动学逻辑的设备。" +
                       "source_name/ target_name 均为必填。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""source_name"": {
                            ""type"": ""string"",
                            ""description"": ""已有逻辑的源资源名称(必填)""
                        },
                        ""target_name"": {
                            ""type"": ""string"",
                            ""description"": ""要复制到的目标资源名称(必填)""
                        }
                    },
                    ""required"": [""source_name"", ""target_name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CopyLogic(
                GetString(input, "source_name", null),
                GetString(input, "target_name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 8) create_cee_module — 创建 CEE 模块（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 创建 CEE 模块（Modules Viewer Hierarchy 中的模块节点）。
    /// 模块用于编写信号表达式（ResultSignal = 信号表达式），在每个仿真扫描周期计算。
    /// 支持创建 IF/ELSE 条件分支实现联锁和流程控制。
    /// </summary>
    public sealed class CreateCeeModuleTool : TxAgentToolBase
    {
        public override string Name { get { return "create_cee_module"; } }

        public override string Description
        {
            get
            {
                return "创建 CEE 模块（Modules Viewer 层级节点）。" +
                       "模块用于编写信号表达式：ResultSignal = 信号运算符表达式，" +
                       "每个仿真扫描周期自动计算。支持 IF/ELSE 分支实现联锁逻辑和流程控制。" +
                       "name 为模块名(必填)。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""name"": {
                            ""type"": ""string"",
                            ""description"": ""CEE 模块名称(必填，如 MainControl/ConveyorLogic)""
                        }
                    },
                    ""required"": [""name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CreateCeeModule(GetString(input, "name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 9) create_lb_sensor — 在资源上创建光传感器（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 在已有 LogicBehavior 的资源上创建光传感器。
    /// 光传感器检测物体遮挡，输出信号可连接到 LB Entry。
    /// 资源必须实现 ITxPlcSensorCreation 接口，且建议已有 LB。
    /// </summary>
    public sealed class CreateLbSensorTool : TxAgentToolBase
    {
        public override string Name { get { return "create_lb_sensor"; } }

        public override string Description
        {
            get
            {
                return "在已有 LogicBehavior 的资源上创建光传感器（如接近/光电传感器）。" +
                       "传感器检测到物体遮挡时发出信号，可连接到 LB Entry 作为逻辑触发条件。" +
                       "适用场景：夹具开合检测、工件到位检测、传送带堵塞检测等。" +
                       "resource_name 为资源名(留空用选中)，sensor_name 为传感器名(必填)。变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""resource_name"": {
                            ""type"": ""string"",
                            ""description"": ""目标资源名称，留空则使用当前选中对象""
                        },
                        ""sensor_name"": {
                            ""type"": ""string"",
                            ""description"": ""传感器名称(必填，如 ClampOpenSensor)""
                        }
                    },
                    ""required"": [""sensor_name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.CreatePlcSensor(
                GetString(input, "resource_name", null),
                "light_sensor",
                GetString(input, "sensor_name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 10) list_lb_elements — 列出 LB 元素（只读）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 列出资源 LogicBehavior 的所有元素：Entries(入口)、Exits(出口)、
    /// Actions(动作如 MoveJoint/MoveToPose)、Parameters(参数)、Constants(常量)。
    /// 显示各元素是否已连接信号。只读。
    /// </summary>
    public sealed class ListLbElementsTool : TxAgentToolBase
    {
        public override string Name { get { return "list_lb_elements"; } }

        public override string Description
        {
            get
            {
                return "列出资源 LogicBehavior 的所有元素分类汇总：" +
                       "Entry(入口,信号输入端)、Exit(出口,信号输出端)、" +
                       "Action(动作,MoveJoint/MoveToPose 等)、Parameter(参数)、Constant(常量)。" +
                       "显示各元素连接的信号。" +
                       "resource_name 为资源名(留空用选中)。只读。在连接信号前用它确认 LB 引脚状态。";
            }
        }

        public override bool IsReadOnly { get { return true; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""resource_name"": {
                            ""type"": ""string"",
                            ""description"": ""资源名称，留空则使用当前选中对象""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.ListLogicBehaviorElements(GetString(input, "resource_name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 11) connect_signal_to_lb — 连接信号到 LB 引脚（变更）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 将 PLC 信号连接到资源 LogicBehavior 的 Entry（入口）或 Exit（出口）引脚。
    /// 连接后信号值的变化将触发 LB 执行相应的逻辑行为。
    /// Entry 为输入：外部信号 → LB；Exit 为输出：LB → 外部信号。
    /// </summary>
    public sealed class ConnectSignalToLbTool : TxAgentToolBase
    {
        public override string Name { get { return "connect_signal_to_lb"; } }

        public override string Description
        {
            get
            {
                return "将 PLC 信号连接到资源的 LogicBehavior 引脚：" +
                       "entry(入口,传感器/指令信号→LB)、exit(出口,LB→执行信号)。" +
                       "连接后信号可触发或反映 LB 逻辑行为。" +
                       "signal_name/pin_type 均为必填，pin_name 可为空(自动匹配第一个同类型引脚)。" +
                       "变更操作，需审批。";
            }
        }

        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""resource_name"": {
                            ""type"": ""string"",
                            ""description"": ""目标资源名称，留空则使用当前选中""
                        },
                        ""signal_name"": {
                            ""type"": ""string"",
                            ""description"": ""要连接的 PLC 信号名(必填)""
                        },
                        ""pin_type"": {
                            ""type"": ""string"",
                            ""enum"": [""entry"", ""exit""],
                            ""description"": ""连接到的引脚类型：entry(入口,输入) / exit(出口,输出)""
                        },
                        ""pin_name"": {
                            ""type"": ""string"",
                            ""description"": ""引脚名称(模糊匹配)，留空匹配第一个同名类型引脚""
                        }
                    },
                    ""required"": [""signal_name"", ""pin_type""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.ConnectSignalToLB(
                GetString(input, "resource_name", null),
                GetString(input, "signal_name", null),
                GetString(input, "pin_type", "entry"),
                GetString(input, "pin_name", null));
        }
    }
}
