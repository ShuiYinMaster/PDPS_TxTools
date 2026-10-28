// TxTools.Agent / Tools / RobotTools.cs
// 机器人工具：BASE0 检查/校正、运动学检查、操作→机器人查找。
// 所有 PS SDK 调用经 PsBridge -> PsContext.Current.Run(...) 路由回 PS 主线程。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    /// <summary>检查或校正机器人 BASE0，复用插件品牌基准和同步实现。</summary>
    public class CheckRobotBaseTool : TxAgentToolBase
    {
        public override string Name { get { return "check_robot_base"; } }
        public override string Description { get { return "检查机器人 BASE0 与品牌期望基准的一致性；区分一致、偏差、无 BASE0 和无法对比。可用 name/object_id 限定机器人，省略则检查全部。容差默认与 BASE0 插件一致：位置 0.5mm，旋转 0.01（PS RPY 原生单位，与插件旋转容差相同）。"; } }
        public override bool IsReadOnly { get { return true; } }
        public override JObject InputSchema { get { return JObject.Parse(@"{
            'type':'object','properties':{
                'name':{'type':'string','description':'机器人名称；同名时使用 object_id'},
                'object_id':{'type':'string','description':'机器人唯一 ID，优先于 name；省略目标则处理全部机器人'},
                'tolerance_mm':{'type':'number','minimum':0,'default':0.5},
                'tolerance_rot':{'type':'number','minimum':0,'default':0.01,'description':'PS RPY 原生单位，与插件旋转容差一致'},
                'brand_mode':{'type':'string','enum':['Auto','Fanuc','Generic'],'default':'Auto'}
            }}"); } }
        public override string Execute(JObject input)
        {
            input = input ?? new JObject();
            try
            {
                var brand = GetString(input, "brand_mode", "Auto");
                TxTools.RobotBaseChecker.BrandMode mode;
                if (!System.Enum.TryParse(brand, true, out mode) || !System.Enum.IsDefined(typeof(TxTools.RobotBaseChecker.BrandMode), mode))
                    return "Error: brand_mode 必须是 Auto/Fanuc/Generic。";
                return PsBridge.CheckRobotBase(
                    ToolInputHelpers.Number(input["tolerance_mm"], 0.5, "tolerance_mm"),
                    ToolInputHelpers.Number(input["tolerance_rot"], 0.01, "tolerance_rot"),
                    brand, GetString(input, "name", null), GetString(input, "object_id", null), !IsReadOnly);
            }
            catch (System.ArgumentException ex) { return "Error: " + ex.Message; }
        }
    }

    public sealed class CorrectRobotBaseTool : CheckRobotBaseTool
    {
        public override string Name { get { return "correct_robot_base"; } }
        public override string Description { get { return "校正机器人 BASE0：重新检查后，仅把可比较且存在偏差的 BASE0 同步到品牌期望坐标。FANUC 使用 J1/J2 运动学交点；Generic 使用机器人安装坐标。同名用 object_id，省略目标则校正全部偏差机器人。需要审批，使用一个场景撤销分组，可 Ctrl+Z 撤销；返回成功/跳过/失败和校正后复查结果。容差单位与 check_robot_base 一致。"; } }
        public override bool IsReadOnly { get { return false; } }
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
                        ""name"": { ""type"": ""string"", ""description"": ""机器人名称"" },
                        ""object_id"": {
                            ""type"": ""string"",
                            ""description"": ""对象的场景唯一 ID(形如 3,57,2,1)。场景内可能存在同名对象，工具报'命中多个'时用它精确指定；给了 object_id 就会忽略 name。""
                        }
                    },
                    ""anyOf"": [{""required"": [""name""]}, {""required"": [""object_id""]}]
                }");
            }
        }

        public override string Execute(JObject input)
        {
            return PsBridge.InspectRobotKinematics(GetString(input, "name", null), GetString(input, "object_id", null));
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
