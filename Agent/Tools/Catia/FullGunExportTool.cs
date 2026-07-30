// TxTools.Agent / Tools / Catia / FullGunExportTool.cs
// 完整焊枪导出工具 —— 把四个子工具串成一条完整链路：
//   ① 解析操作与焊点            (PsReader.GetOperationsFromSelection / FillPoints)
//   ② 解析焊枪与 TCP 变换        (GetGunFromOperation / EnumerateTcpOptions / ResolveTcpWorldByName)
//   ③ 提取参考坐标并检测冲突     (ResolveOperationRefFrame)
//   ④ 焊点坐标世界系 → 参考系    (ToRelative,由 CatiaBridge 内部对每个位姿施加)
//   ⑤ CATIA 插枪并按焊点名改名   (CatiaBridge.ExportGuns)
// 与 catia_export_gun 的区别：本工具做冲突消解、参数体检和全流程报告；
// 零件/夹具坐标不一致且未指定 ref_name 时会中止并要求确认，避免整批导出到错误基准上。
// 变更工具（会修改 CATIA 文档），需审批。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using Tecnomatix.Engineering;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    public sealed class FullGunExportTool : ITxAgentTool
    {
        public string Name { get { return "export_gun_full"; } }

        public string Description
        {
            get
            {
                return "完整焊枪导出流程:解析操作与焊点 → 解析焊枪与 TCP → 提取参考坐标(自动检测零件/夹具冲突) " +
                       "→ 焊点坐标变换到参考系 → CATIA 共享几何插枪并按焊点名改名。" +
                       "参数 operation_name, model_path, product_name, ref_mode (auto/world,默认 auto), " +
                       "ref_name (指定参考系候选名), tcp_name, gun_origin_at_tcp (默认 true), " +
                       "point_filter (默认 weld), use_mfg_name, dry_run (默认 false,只体检不导出)。" +
                       "参考坐标存在冲突且未指定 ref_name 时会中止并列出候选。";
            }
        }

        public bool IsReadOnly { get { return false; } }

        public JObject InputSchema
        {
            get
            {
                return JObject.Parse(@"{
                    'type': 'object',
                    'properties': {
                        'operation_name':    { 'type': 'string',  'description': '操作名(留空=PS 当前选中的全部操作)' },
                        'model_path':        { 'type': 'string',  'description': '焊枪 CGR/CATPart 路径(留空=用 PS 工具自带路径)' },
                        'product_name':      { 'type': 'string',  'description': 'CATIA 容器名(留空=用操作名)' },
                        'ref_mode':          { 'type': 'string',  'description': 'auto=自动解析零件/夹具参考系(默认); world=世界坐标' },
                        'ref_name':          { 'type': 'string',  'description': '指定参考系候选名,用于消解零件/夹具冲突' },
                        'tcp_name':          { 'type': 'string',  'description': '指定 TCP 名(留空=机器人当前 TCP)' },
                        'gun_origin_at_tcp': { 'type': 'boolean', 'description': '焊枪以 TCP 为原点(默认 true)' },
                        'export_tcp':        { 'type': 'boolean', 'description': '同时导出 TCP 坐标系可视化(默认 false)' },
                        'point_filter':      { 'type': 'string',  'description': 'weld / path / continuous / all(默认 weld)' },
                        'use_mfg_name':      { 'type': 'boolean', 'description': '是否用制造特征名作为点名(默认 false)' },
                        'dry_run':           { 'type': 'boolean', 'description': 'true=只做体检与报告,不连接 CATIA(默认 false)' },
                        'verbose':           { 'type': 'boolean', 'description': '是否附带 PS/CATIA 日志(默认 false)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var opName = ToolInputHelpers.String(input["operation_name"]);
            var modelPath = ToolInputHelpers.String(input["model_path"]);
            var productName = ToolInputHelpers.String(input["product_name"]);
            var refMode = ToolInputHelpers.String(input["ref_mode"]);
            var refName = ToolInputHelpers.String(input["ref_name"]);
            var tcpName = ToolInputHelpers.String(input["tcp_name"]);
            var originAtTcp = GunToolHelpers.Bool(input["gun_origin_at_tcp"], true);
            var exportTcp = GunToolHelpers.Bool(input["export_tcp"], false);
            var useMfg = GunToolHelpers.Bool(input["use_mfg_name"], false);
            var dryRun = GunToolHelpers.Bool(input["dry_run"], false);
            var verbose = GunToolHelpers.Bool(input["verbose"], false);

            var filterStr = ToolInputHelpers.String(input["point_filter"]);
            var filter = string.IsNullOrEmpty(filterStr)
                ? PointType.WeldPoint
                : GunToolHelpers.ParsePointFilter(filterStr);

            bool forceWorld = !string.IsNullOrEmpty(refMode)
                && refMode.Trim().Equals("world", StringComparison.OrdinalIgnoreCase);

            var logSb = new StringBuilder();
            Action<string> log = GunToolHelpers.Collector(logSb);
            var sb = new StringBuilder();

            // ══ ① 操作与焊点 ══
            string err;
            OpResolution res = GunToolHelpers.ResolveOperations(opName, log, out err);
            if (res == null) return "Error: " + err;
            List<OperationInfo> ops = res.Operations;

            var ready = new List<OperationInfo>();
            var skipped = new List<string>();
            int totalPts = 0;

            sb.Append(dryRun ? "【体检 dry_run — 不会改动 CATIA】\n\n" : "【正式导出 — 会写入 CATIA 活动文档】\n\n");
            sb.Append("① 操作与焊点  (来源: ").Append(res.SourceDesc).Append(")\n");
            foreach (var op in ops)
            {
                try { PsReader.FillPoints(op, filter, useMfg, log); }
                catch (Exception ex) { skipped.Add(op.Name + " (读点失败: " + ex.Message + ")"); continue; }

                if (op.Points == null || op.Points.Count == 0)
                { skipped.Add(op.Name + " (无符合条件的点)"); continue; }

                // ══ ② 焊枪与 TCP ══
                try { op.Gun = PsReader.GetGunFromOperation(op, modelPath, log); }
                catch (Exception ex) { skipped.Add(op.Name + " (读枪失败: " + ex.Message + ")"); continue; }

                if (op.Gun == null) { skipped.Add(op.Name + " (未找到工具对象)"); continue; }

                string resolvedModel = !string.IsNullOrEmpty(modelPath) ? modelPath : op.Gun.ModelPath;
                if (string.IsNullOrEmpty(resolvedModel))
                { skipped.Add(op.Name + " (未找到焊枪模型文件,请传 model_path)"); continue; }

                totalPts += op.Points.Count;
                ready.Add(op);

                sb.Append("  - ").Append(op.Name).Append(": ").Append(op.Points.Count)
                  .Append(" 点, 枪=").Append(op.Gun.Name).Append('\n');
            }
            foreach (var s in skipped) sb.Append("  ⚠ 跳过: ").Append(s).Append('\n');

            if (ready.Count == 0) return "Error: 没有可导出的操作\n" + sb.ToString();

            // ══ ② TCP 解析（以首个操作为准做体检） ══
            sb.Append("\n② 焊枪与 TCP\n");
            OperationInfo head = ready[0];

            string robotVia;
            var robot = GunToolHelpers.FindRobot(head, res.Ancestors, out robotVia);
            if (robot != null)
            {
                string rn = null;
                try { rn = robot.Name; } catch { }
                sb.Append("  机器人: ").Append(rn ?? "(名字不可读)");
                if (!string.IsNullOrEmpty(robotVia) && robotVia != head.Name)
                    sb.Append("  <经由 ").Append(robotVia).Append('>');
                sb.Append('\n');
            }
            sb.Append("  焊枪: ").Append(head.Gun.Name ?? "(未知)").Append('\n');
            double[] tcpWorld = null;
            if (!string.IsNullOrEmpty(tcpName))
            {
                try { tcpWorld = PsReader.ResolveTcpWorldByName(head, tcpName, log); }
                catch (Exception ex) { log("TCP 解析异常: " + ex.Message); }

                if (tcpWorld == null)
                {
                    var e = new StringBuilder();
                    e.Append("Error: 名为 \"").Append(tcpName).Append("\" 的 TCP 不在候选中。可用 TCP:");
                    try
                    {
                        foreach (var o in PsReader.EnumerateTcpOptions(head, log))
                            e.Append("\n  - ").Append(o.Name).Append(o.IsDefault ? "  [默认]" : "");
                    }
                    catch { }
                    return e.ToString();
                }
                sb.Append("  指定 TCP: ").Append(tcpName).Append('\n')
                  .Append("    世界矩阵: ").Append(GunToolHelpers.FmtMatrix(tcpWorld)).Append('\n');
            }
            else
            {
                tcpWorld = head.Gun.TcpWorldMatrix;
                sb.Append("  使用默认 TCP(机器人当前)\n");
            }

            if (head.Gun.ToolMatrix != null && tcpWorld != null)
            {
                double[] relTool = PsReader.ToRelative(tcpWorld, head.Gun.ToolMatrix);
                sb.Append("  TCP 相对工具偏移: ").Append(GunToolHelpers.FmtMatrix(relTool)).Append('\n');
            }
            sb.Append("  枪原点: ").Append(originAtTcp ? "TCP" : "工具安装点").Append('\n');

            // ══ ③ 参考坐标提取与冲突消解 ══
            sb.Append("\n③ 参考坐标\n");
            double[] refMatrix = null;
            string refLabel = "世界坐标系";

            if (forceWorld)
            {
                sb.Append("  模式: world(强制世界坐标)\n");
            }
            else
            {
                PsReader.RefFrameResult r = null;
                try { r = PsReader.ResolveOperationRefFrame(head, true, log); }
                catch (Exception ex) { return "Error: 参考坐标解析失败 - " + ex.Message; }

                if (r == null) return "Error: 参考坐标解析返回空";

                List<AppearanceRef> leading = null;
                if (r.Source == PsReader.RefFrameSource.None || r.Source == PsReader.RefFrameSource.World)
                {
                    try { leading = GunToolHelpers.CollectLeadingParts(head); }
                    catch { }
                }

                if (!string.IsNullOrEmpty(refName))
                {
                    AppearanceRef picked = Find(r.PartCandidates, refName)
                                        ?? Find(r.FixtureCandidates, refName)
                                        ?? Find(leading, refName);
                    if (picked == null)
                    {
                        var e = new StringBuilder();
                        e.Append("Error: 候选中没有名为 \"").Append(refName).Append("\" 的参考系。候选:");
                        AppendNames(e, r.PartCandidates, "零件");
                        AppendNames(e, r.FixtureCandidates, "夹具");
                        AppendNames(e, leading, "LeadingPart");
                        return e.ToString();
                    }
                    refMatrix = picked.Matrix;
                    refLabel = picked.Name + "(指定)";
                }
                else if (r.NeedsUserChoice)
                {
                    // 不擅自选边：整批焊点会挂到错误基准上，代价远大于多问一次
                    var e = new StringBuilder();
                    e.Append("Error: 参考坐标存在冲突,已中止导出。\n  ")
                     .Append(r.ConflictReason).Append('\n')
                     .Append("  请用 ref_name 指定要用哪个,或用 ref_mode=world 保持世界坐标。候选:");
                    AppendNames(e, r.PartCandidates, "零件");
                    AppendNames(e, r.FixtureCandidates, "夹具");
                    AppendNames(e, leading, "LeadingPart");
                    return e.ToString();
                }
                else if (r.Source != PsReader.RefFrameSource.None && r.Source != PsReader.RefFrameSource.World)
                {
                    refMatrix = r.Matrix;
                    refLabel = r.Name;
                    sb.Append("  来源: ").Append(r.Source).Append('\n');
                    if (!string.IsNullOrEmpty(r.PointName))
                        sb.Append("  依据点: ").Append(r.PointName).Append('\n');
                }
                else if (leading != null && leading.Count > 0)
                {
                    refMatrix = leading[0].Matrix;
                    refLabel = leading[0].Name + "(LeadingPart 兜底)";
                    sb.Append("  来源: LeadingPart(标准外观绑定为空)\n");
                }
                else
                {
                    refMatrix = null;
                    refLabel = "世界坐标系(无任何绑定)";
                }

                if (refMatrix != null && PsReader.IsIdentity(refMatrix))
                {
                    sb.Append("  说明: 参考系为单位阵,与世界坐标等价\n");
                    refMatrix = null;   // 传 null 让 CatiaBridge 走世界系快路径
                }
            }
            sb.Append("  参考系: ").Append(refLabel).Append('\n');
            if (refMatrix != null)
                sb.Append("  参考矩阵: ").Append(GunToolHelpers.FmtMatrix(refMatrix)).Append('\n');

            // ══ ④ 坐标变换预览（首个点） ══
            sb.Append("\n④ 坐标变换预览\n");
            PointInfo p0 = head.Points.Count > 0 ? head.Points[0] : null;
            if (p0 != null && p0.TCPMatrix != null)
            {
                sb.Append("  首点 ").Append(p0.Name).Append('\n')
                  .Append("    世界: ").Append(GunToolHelpers.FmtMatrix(p0.TCPMatrix)).Append('\n');
                if (refMatrix != null)
                    sb.Append("    相对: ").Append(GunToolHelpers.FmtMatrix(PsReader.ToRelative(p0.TCPMatrix, refMatrix))).Append('\n');
            }

            // ══ ⑤ CATIA 导出 ══
            sb.Append("\n⑤ CATIA 导出\n");
            if (dryRun)
            {
                sb.Append("  dry_run=true,已跳过 CATIA 导出。\n")
                  .Append("  就绪: ").Append(ready.Count).Append(" 个操作,共 ").Append(totalPts).Append(" 个点\n");
                if (verbose && logSb.Length > 0) sb.Append("\n[日志]\n").Append(logSb.ToString());
                return sb.ToString().TrimEnd();
            }

            var prm = new GunExportParams
            {
                Operations = ready,
                ExportTCP = exportTcp,
                GunOriginAtTCP = originAtTcp,
                CustomModelPath = string.IsNullOrEmpty(modelPath) ? null : modelPath,
                CustomProductName = string.IsNullOrEmpty(productName) ? null : productName,
                Format = ExportFormat.Xml3d,
                RefMatrix = refMatrix,
                RefName = refLabel,
                PointFilter = filter,
                UseMfgName = useMfg,
                TcpName = string.IsNullOrEmpty(tcpName) ? null : tcpName
            };

            string runErr;
            string runMsg = CatiaExportRunner.Run(prm, log, out runErr);
            if (runErr != null)
            {
                sb.Append("  ✗ 失败: ").Append(runErr).Append('\n');
                if (logSb.Length > 0) sb.Append("\n[日志]\n").Append(logSb.ToString());
                return "Error: CATIA 导出失败\n" + sb.ToString();
            }

            sb.Append("  ✓ 已插枪: ").Append(ready.Count).Append(" 个操作,共 ").Append(totalPts).Append(" 个实例\n")
              .Append("  实例名 = 焊点名,几何共享同一份 CGR Reference\n")
              .Append("  结果留在 CATIA 活动 Product 中,如需 3DXML 请在 CATIA 内另存。\n");
            if (!string.IsNullOrEmpty(runMsg)) sb.Append(runMsg).Append('\n');

            if (verbose && logSb.Length > 0) sb.Append("\n[日志]\n").Append(logSb.ToString());
            return sb.ToString().TrimEnd();
        }

        private static AppearanceRef Find(List<AppearanceRef> list, string name)
        {
            if (list == null) return null;
            foreach (var a in list)
                if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            foreach (var a in list)
                if (a != null && a.Name != null && a.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return a;
            return null;
        }

        private static void AppendNames(StringBuilder sb, List<AppearanceRef> list, string tag)
        {
            if (list == null) return;
            foreach (var a in list)
                if (a != null) sb.Append("\n  - [").Append(tag).Append("] ").Append(a.Name);
        }
    }
}
