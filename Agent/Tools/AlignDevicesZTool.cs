// TxTools.Agent / Tools / AlignDevicesZTool.cs
// 变更示例工具：把选中设备对齐到世界 Z=0。
// 演示"变更 -> 执行前需用户审批"的模式 (IsReadOnly=false)，且操作本身支持 Ctrl+Z 撤销。

using Newtonsoft.Json.Linq;
using TxTools.Agent.Core;
using TxTools.Agent.Ps;

namespace TxTools.Agent.Tools
{
    public sealed class AlignDevicesZTool : TxAgentToolBase
    {
        public override string Name { get { return "align_devices_z"; } }

        public override string Description
        {
            get
            {
                return "将当前选中的设备最低点对齐到世界坐标 Z=0 (落地)。先用 select_objects 选中要对齐的设备；" +
                       "会跳过枪/机器人/工具等末端对象；执行前需用户确认，操作后可用 Ctrl+Z 撤销。";
            }
        }

        // 关键：标为非只读，循环会在执行前触发审批回调。
        public override bool IsReadOnly { get { return false; } }

        public override JObject InputSchema
        {
            get { return EmptyObjectSchema(); } // 作用于当前选中集，无需额外参数
        }

        public override string Execute(JObject input)
        {
            return PsBridge.AlignSelectedDevicesToFloor();
        }
    }
}
