// TxTools.Agent / Tools / RobotTools.cs
// 机器人相关只读原子工具：基座校验、运动学检查、操作→机器人查找。
// 所有 PS SDK 调用经 PsBridge -> PsContext.Current.Run(...) 路由回 PS 主线程。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    // ─────────────────────────────────────────────────────────────
    // 1) check_robot_base — 机器人 BASE0 基座校验（接 RobotBaseReader）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 校验场景内所有机器人的 BASE0 是否与期望一致。
    /// 调用 RobotBaseReader.Analyze 分析当前存储 BASE0 与品牌感知的期望 BASE0，
    /// 给出各机器人的 Verdict（一致 / 存在偏差 / 无法对比 / 无当前BASE0）。
    /// 只读、不修改场景。
    /// </summary>
    public sealed class CheckRobotBaseTool : TxAgentToolBase
    {
        public override string Name { get { return "check_robot_base"; } }

        public override string Description
        {
            get
            {
                return "校验场景内所有机器人的 BASE0（控制器基坐标）是否与期望一致。" +
                       "tolerance_mm 控制平移容差(mm)，tolerance_rot 控制旋转容差(度)；" +
                       "brand_mode 可选 Auto/Fanuc/Generic（Auto 自动识别品牌）。" +
                       "只读，不修改场景。回答机器人 BASE0 是否正确时用它。";
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
                        ""tolerance_mm"": {
                            ""type"": ""number"",
                            ""description"": ""平移容差(mm)，默认 5.0""
                        },
                        ""tolerance_rot"": {
                            ""type"": ""number"",
                            ""description"": ""旋转容差(度)，默认 0.5""
                        },
                        ""brand_mode"": {
                            ""type"": ""string"",
                            ""enum"": [""Auto"", ""Fanuc"", ""Generic""],
                            ""description"": ""品牌识别模式，默认 Auto""
                        }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            double posTol = 5.0;
            double rotTol = 0.5;
            string brandMode = GetString(input, "brand_mode", "Auto");

            var tPos = input != null ? input["tolerance_mm"] : null;
            if (tPos != null && tPos.Type == JTokenType.Float) posTol = (double)tPos;
            else if (tPos != null && tPos.Type == JTokenType.Integer) posTol = (double)(int)tPos;

            var tRot = input != null ? input["tolerance_rot"] : null;
            if (tRot != null && tRot.Type == JTokenType.Float) rotTol = (double)tRot;
            else if (tRot != null && tRot.Type == JTokenType.Integer) rotTol = (double)(int)tRot;

            return PsBridge.CheckRobotBase(posTol, rotTol, brandMode);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 2) inspect_robot_kinematics — 查询机器人关节/运动学信息
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查询一台机器人的运动学信息：关节数量、各关节名称、当前角度值、TCP 数量。
    /// 只读，不驱动机器人。
    /// </summary>
    public sealed class InspectRobotKinematicsTool : TxAgentToolBase
    {
        public override string Name { get { return "inspect_robot_kinematics"; } }

        public override string Description
        {
            get
            {
                return "查询一台机器人的运动学/关节信息：关节数量、各关节名称与当前值、TCP 数量。" +
                       "给出机器人名(name)；只读，不驱动机器人。" +
                       "回答某机器人有几轴、当前姿态是什么时用它。";
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
                        ""name"": { ""type"": ""string"", ""description"": ""机器人名称"" }
                    },
                    ""required"": [""name""]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.InspectRobotKinematics(GetString(input, "name", null));
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 3) find_robot_for_op — 查找操作绑定的机器人
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 查找操作树中匹配关键字的操作，并返回各操作绑定的机器人名称。
    /// 只读。
    /// </summary>
    public sealed class FindRobotForOpTool : TxAgentToolBase
    {
        public override string Name { get { return "find_robot_for_op"; } }

        public override string Description
        {
            get
            {
                return "在操作树里按关键字查找操作，并返回各操作绑定的机器人名称。" +
                       "operation 为操作名关键字(如 OP120)；留空则检查全部操作。" +
                       "回答OP120 是哪台机器人的时用它。";
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
                        ""operation"": { ""type"": ""string"", ""description"": ""操作名关键字(模糊)，留空查全部"" }
                    }
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.FindRobotForOperation(GetString(input, "operation", null));
        }
    }
}
