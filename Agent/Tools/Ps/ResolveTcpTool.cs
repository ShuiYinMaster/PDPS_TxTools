// TxTools.Agent / Tools / Ps / ResolveTcpTool.cs
// 子工具 1：TCP 坐标变换。
// 枚举操作可用的 TCP（机器人当前 TCP / 控制器系统坐标系 / 路径点 RRS_TOOL_FRAME / 工具子坐标系），
// 并计算指定 TCP 相对工具安装坐标的固定偏移 —— 这个偏移正是插枪时把 CGR 原点对齐到 TCP 所需的变换。
//
// v2 变更：
//  · 操作名找不到时会全局搜索（见 GunToolHelpers.ResolveOperations）
//  · 直接报告绑定的机器人名；操作本身没绑机器人时沿祖先链上溯，
//    免得调用方再去查一次"操作→机器人"映射
//  · show_matrix=true 输出原始 4x4，免得调用方为了精确数值去写脚本
//
// 只读工具，不改动模型。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    public sealed class ResolveTcpTool : ITxAgentTool
    {
        public string Name { get { return "ps_resolve_tcp"; } }

        public string Description
        {
            get
            {
                return "枚举焊接操作可用的 TCP 坐标系,报告绑定的机器人,并计算 TCP 相对工具的固定偏移。" +
                       "参数 operation_name (可选,留空=PS 当前选中;给名字时会全局搜索操作树," +
                       "机器人绑在父级复合操作上也能找到), tcp_name (可选,指定后额外输出偏移), " +
                       "show_matrix (可选,输出原始 4x4)。" +
                       "偏移 = Inv(工具安装矩阵) × TCP世界矩阵,插枪时用于把 CGR 原点对齐到 TCP。";
            }
        }

        public bool IsReadOnly { get { return true; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'operation_name': { 'type': 'string',  'description': '操作名(留空=PS 当前选中;找不到会全局搜索)' },
                        'tcp_name':       { 'type': 'string',  'description': 'TCP 名称;留空只列出候选' },
                        'show_matrix':    { 'type': 'boolean', 'description': '输出原始 4x4 矩阵(默认 false)' },
                        'verbose':        { 'type': 'boolean', 'description': '是否输出 PS 侧诊断日志(默认 false)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var opName = ToolInputHelpers.String(input["operation_name"]);
            var tcpName = ToolInputHelpers.String(input["tcp_name"]);
            var showMatrix = GunToolHelpers.Bool(input["show_matrix"], false);
            var verbose = GunToolHelpers.Bool(input["verbose"], false);

            var logSb = new StringBuilder();
            Action<string> log = GunToolHelpers.Collector(logSb);

            string err;
            OpResolution res = GunToolHelpers.ResolveOperations(opName, log, out err);
            if (res == null) return "Error: " + err;

            var sb = new StringBuilder();
            sb.Append("来源: ").Append(res.SourceDesc).Append('\n');

            foreach (var op in res.Operations)
            {
                sb.Append("\n操作: ").Append(op.Name)
                  .Append(" [").Append(op.TypeLabel).Append("]\n");

                // 机器人：操作本身没有就沿祖先链上溯
                string via;
                TxRobot robot = GunToolHelpers.FindRobot(op, res.Ancestors, out via);
                if (robot != null)
                {
                    string rn = null;
                    try { rn = robot.Name; } catch { }
                    sb.Append("  机器人: ").Append(rn ?? "(名字不可读)");
                    if (!string.IsNullOrEmpty(via) && via != op.Name)
                        sb.Append("  <经由 ").Append(via).Append('>');
                    sb.Append('\n');
                }
                else
                {
                    sb.Append("  ⚠ 该操作及其父级都未找到绑定的机器人\n");
                }

                List<PsReader.TcpOption> opts;
                try { opts = PsReader.EnumerateTcpOptions(op, log); }
                catch (Exception ex) { sb.Append("  Error: TCP 枚举失败 - ").Append(ex.Message).Append('\n'); continue; }

                if (opts == null || opts.Count == 0)
                {
                    sb.Append("  ⚠ 未找到任何 TCP 候选\n");
                    continue;
                }

                sb.Append("  可用 TCP ").Append(opts.Count).Append(" 个:\n");
                foreach (var o in opts)
                {
                    sb.Append("    - ").Append(o.Name);
                    if (o.IsDefault) sb.Append("  [默认]");
                    sb.Append("\n        ").Append(GunToolHelpers.FmtMatrix(o.WorldMatrix)).Append('\n');
                    if (showMatrix)
                        sb.Append(GunToolHelpers.FmtRaw4x4(o.WorldMatrix, "          ")).Append('\n');
                }

                if (string.IsNullOrEmpty(tcpName)) continue;

                // ── 指定 TCP：解析世界矩阵 + 相对工具偏移 ──
                double[] tcpWorld = null;
                try { tcpWorld = PsReader.ResolveTcpWorldByName(op, tcpName, log); }
                catch (Exception ex) { sb.Append("  Error: 解析 TCP 失败 - ").Append(ex.Message).Append('\n'); continue; }

                if (tcpWorld == null)
                {
                    sb.Append("  ⚠ 名为 \"").Append(tcpName).Append("\" 的 TCP 不在候选中,请从上面列表里选\n");
                    continue;
                }

                sb.Append("  选定 TCP: ").Append(tcpName).Append('\n')
                  .Append("    世界矩阵: ").Append(GunToolHelpers.FmtMatrix(tcpWorld)).Append('\n');
                if (showMatrix)
                    sb.Append(GunToolHelpers.FmtRaw4x4(tcpWorld, "      ")).Append('\n');

                GunInfo gun = null;
                try { gun = PsReader.GetGunFromOperation(op, null, log); }
                catch (Exception ex) { sb.Append("    ⚠ 工具信息读取失败: ").Append(ex.Message).Append('\n'); }

                if (gun == null || gun.ToolMatrix == null)
                {
                    sb.Append("    ⚠ 未取到工具安装矩阵,无法计算偏移\n");
                    continue;
                }

                sb.Append("    工具: ").Append(gun.Name ?? "(未知)").Append('\n')
                  .Append("    工具安装(CGR 原点): ").Append(GunToolHelpers.FmtMatrix(gun.ToolMatrix)).Append('\n');

                double[] relTool = null;
                try { relTool = PsReader.ToRelative(tcpWorld, gun.ToolMatrix); }
                catch (Exception ex) { sb.Append("    ⚠ 偏移计算失败: ").Append(ex.Message).Append('\n'); }

                if (relTool != null)
                {
                    sb.Append("    TCP 相对工具偏移: ").Append(GunToolHelpers.FmtMatrix(relTool)).Append('\n');
                    if (showMatrix)
                        sb.Append(GunToolHelpers.FmtRaw4x4(relTool, "      ")).Append('\n');
                    sb.Append(PsReader.IsIdentity(relTool)
                        ? "    说明: 偏移为单位阵,该 TCP 与工具原点重合\n"
                        : "    说明: 插枪时若要求枪以 TCP 为原点,需对 CGR 施加此偏移的逆变换\n");
                }
            }

            if (verbose && logSb.Length > 0)
                sb.Append("\n[PS 日志]\n").Append(logSb.ToString());

            return sb.ToString().TrimEnd();
        }
    }
}
