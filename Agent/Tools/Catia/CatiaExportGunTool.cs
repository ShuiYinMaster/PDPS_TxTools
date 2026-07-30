// TxTools.Agent / Tools / Catia / CatiaExportGunTool.cs
// 子工具 4：CATIA 焊枪导出（执行器）。
// 只负责 CATIA 侧动作：连接已运行的 CATIA V5 → 在活动 Product 下建操作容器
// → 共享几何插入焊枪 CGR（Copy+Paste 复用同一份 Reference）→ 按焊点名批量改名。
// 参考坐标与 TCP 只做最直接的解析（默认世界系 + 默认 TCP）；需要冲突消解、
// 候选挑选、完整报告时用 export_gun_full。
// 变更工具（会修改 CATIA 文档），需审批。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    public sealed class CatiaExportGunTool : ITxAgentTool
    {
        public string Name { get { return "catia_export_gun"; } }

        public string Description
        {
            get
            {
                return "把 PS 焊接操作的焊枪按焊点位姿插入 CATIA V5 活动 Product(共享几何,实例名=焊点名)。" +
                       "需 CATIA 已运行。参数 operation_name (可选), model_path (可选,覆盖 CGR 路径), " +
                       "product_name (可选,容器名), gun_origin_at_tcp (默认 true), tcp_name (可选), " +
                       "ref_mode (world/auto,默认 world), point_filter, use_mfg_name。" +
                       "结果留在 CATIA 活动文档中,不自动落盘。";
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
                        'operation_name':    { 'type': 'string',  'description': '操作名(留空=PS 当前选中)' },
                        'model_path':        { 'type': 'string',  'description': '焊枪 CGR/CATPart 路径(留空=用 PS 工具自带路径)' },
                        'product_name':      { 'type': 'string',  'description': 'CATIA 容器名(留空=用操作名)' },
                        'gun_origin_at_tcp': { 'type': 'boolean', 'description': '焊枪以 TCP 为原点(默认 true)' },
                        'export_tcp':        { 'type': 'boolean', 'description': '同时导出 TCP 坐标系可视化(默认 false)' },
                        'tcp_name':          { 'type': 'string',  'description': '指定 TCP 名(留空=机器人当前 TCP)' },
                        'ref_mode':          { 'type': 'string',  'description': 'world=世界坐标(默认); auto=自动解析零件/夹具参考系' },
                        'point_filter':      { 'type': 'string',  'description': 'weld / path / continuous / all(默认 weld)' },
                        'use_mfg_name':      { 'type': 'boolean', 'description': '是否用制造特征名作为点名(默认 false)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var opName = ToolInputHelpers.String(input["operation_name"]);
            var modelPath = ToolInputHelpers.String(input["model_path"]);
            var productName = ToolInputHelpers.String(input["product_name"]);
            var tcpName = ToolInputHelpers.String(input["tcp_name"]);
            var refMode = ToolInputHelpers.String(input["ref_mode"]);
            var originAtTcp = GunToolHelpers.Bool(input["gun_origin_at_tcp"], true);
            var exportTcp = GunToolHelpers.Bool(input["export_tcp"], false);
            var useMfg = GunToolHelpers.Bool(input["use_mfg_name"], false);

            var filterStr = ToolInputHelpers.String(input["point_filter"]);
            var filter = string.IsNullOrEmpty(filterStr)
                ? PointType.WeldPoint
                : GunToolHelpers.ParsePointFilter(filterStr);

            bool autoRef = !string.IsNullOrEmpty(refMode)
                && refMode.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

            var logSb = new StringBuilder();
            Action<string> log = GunToolHelpers.Collector(logSb);

            // ── 1. PS 侧：操作 + 点 + 枪 ──
            string err;
            OpResolution res = GunToolHelpers.ResolveOperations(opName, log, out err);
            if (res == null) return "Error: " + err;
            List<OperationInfo> ops = res.Operations;

            var ready = new List<OperationInfo>();
            var skipped = new List<string>();
            int totalPts = 0;

            foreach (var op in ops)
            {
                try { PsReader.FillPoints(op, filter, useMfg, log); }
                catch (Exception ex) { skipped.Add(op.Name + " (读点失败: " + ex.Message + ")"); continue; }

                if (op.Points == null || op.Points.Count == 0)
                { skipped.Add(op.Name + " (无符合条件的点)"); continue; }

                try { op.Gun = PsReader.GetGunFromOperation(op, modelPath, log); }
                catch (Exception ex) { skipped.Add(op.Name + " (读枪失败: " + ex.Message + ")"); continue; }

                if (op.Gun == null) { skipped.Add(op.Name + " (未找到工具对象)"); continue; }
                if (string.IsNullOrEmpty(modelPath) && string.IsNullOrEmpty(op.Gun.ModelPath))
                { skipped.Add(op.Name + " (未找到焊枪模型文件,请传 model_path)"); continue; }

                totalPts += op.Points.Count;
                ready.Add(op);
            }

            if (ready.Count == 0)
            {
                var e = new StringBuilder("Error: 没有可导出的操作");
                foreach (var s in skipped) e.Append("\n  跳过: ").Append(s);
                return e.ToString();
            }

            // ── 2. 参考坐标 ──
            double[] refMatrix = null;
            string refLabel = "世界坐标系";
            if (autoRef)
            {
                try
                {
                    var r = PsReader.ResolveOperationRefFrame(ready[0], true, log);
                    if (r != null && r.Matrix != null && !PsReader.IsIdentity(r.Matrix))
                    { refMatrix = r.Matrix; refLabel = r.Name; }
                }
                catch (Exception ex) { log("参考坐标解析失败,退回世界系: " + ex.Message); }
            }

            // ── 3. CATIA 侧执行 ──
            var p = new GunExportParams
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

            string result = CatiaExportRunner.Run(p, log, out err);
            if (err != null) return "Error: " + err;

            // ── 4. 汇总 ──
            var sb = new StringBuilder();
            sb.Append("已插枪导出到 CATIA\n")
              .Append("  操作来源: ").Append(res.SourceDesc).Append('\n')
              .Append("  操作: ").Append(ready.Count).Append(" 个,共 ").Append(totalPts).Append(" 个点\n")
              .Append("  参考系: ").Append(refLabel).Append('\n')
              .Append("  枪原点: ").Append(originAtTcp ? "TCP" : "工具安装点")
              .Append(string.IsNullOrEmpty(tcpName) ? "" : " (TCP=" + tcpName + ")").Append('\n');
            foreach (var op in ready)
                sb.Append("    - ").Append(op.Name).Append(": ").Append(op.Points.Count).Append(" 点\n");
            foreach (var s in skipped)
                sb.Append("  ⚠ 跳过: ").Append(s).Append('\n');
            sb.Append("  结果留在 CATIA 活动 Product 中,如需 3DXML 请在 CATIA 内另存。");

            if (!string.IsNullOrEmpty(result)) sb.Append('\n').Append(result);
            return sb.ToString();
        }
    }

    /// <summary>CATIA 连接 + 插枪的共享执行逻辑（catia_export_gun 与 export_gun_full 共用）。</summary>
    internal static class CatiaExportRunner
    {
        /// <summary>成功返回摘要文本（可为空串），失败时 error 非空。</summary>
        public static string Run(GunExportParams p, Action<string> log, out string error)
        {
            error = null;
            var progressSb = new StringBuilder();

            using (var bridge = new CatiaBridge())
            {
                string connErr;
                bool ok;
                try { ok = bridge.Connect(out connErr); }
                catch (Exception ex) { error = "连接 CATIA 异常 - " + ex.Message; return null; }

                if (!ok) { error = connErr; return null; }

                Action<ExportProgress> onProgress = delegate (ExportProgress pg)
                {
                    if (pg != null && pg.Total > 0 && pg.Current == pg.Total)
                        progressSb.Append("  进度: ").Append(pg.Current).Append('/').Append(pg.Total).Append('\n');
                };

                try { bridge.ExportGuns(p, onProgress, log); }
                catch (Exception ex) { error = "CATIA 导出失败 - " + ex.Message; return null; }
            }

            return progressSb.ToString().TrimEnd();
        }
    }
}
