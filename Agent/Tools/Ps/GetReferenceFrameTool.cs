// TxTools.Agent / Tools / Ps / GetReferenceFrameTool.cs
// 子工具 2：参考坐标的提取。
// 从焊接操作解析导出用的参考坐标系：优先取首个焊点绑定的零件坐标，
// 无焊点时退回首个路径点绑定的夹具坐标，再不行探测 LeadingPart，最后才回退世界系。
// 同时列出全部候选，并在"零件坐标 ≠ 夹具坐标"时给出冲突提示（需人工选定）。
//
// v2 变更：
//  · 新增 LeadingPart 兜底。PsReader 的候选属性表里没有 LeadingPart，
//    实测中绑定了零件的焊点仍被判为"无绑定"并回退世界系，
//    调用方只能自己写脚本取 LeadingPart 再手算 Inv(零件)×焊点。现在工具直接给出。
//  · 操作名找不到时全局搜索操作树
//  · show_matrix=true 输出原始 4x4
//
// 只读工具，不改动模型。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    public sealed class GetReferenceFrameTool : ITxAgentTool
    {
        public string Name { get { return "ps_get_reference_frame"; } }

        public string Description
        {
            get
            {
                return "提取焊接操作的参考坐标系(零件/夹具/LeadingPart/世界)及全部候选。" +
                       "参数 operation_name (可选,留空=PS 当前选中;给名字时全局搜索), " +
                       "fallback_world (默认 true), point_filter (weld/path/continuous/all,默认 all), " +
                       "use_mfg_name, show_matrix (输出原始 4x4)。" +
                       "返回来源、名称、位姿摘要、候选清单及零件/夹具冲突提示。";
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
                        'fallback_world': { 'type': 'boolean', 'description': '无绑定时是否退回世界系(默认 true)' },
                        'point_filter':   { 'type': 'string',  'description': 'weld / path / continuous / all(默认 all)' },
                        'use_mfg_name':   { 'type': 'boolean', 'description': '是否用制造特征名作为点名(默认 false)' },
                        'show_matrix':    { 'type': 'boolean', 'description': '输出原始 4x4 矩阵(默认 false)' },
                        'verbose':        { 'type': 'boolean', 'description': '是否输出 PS 侧诊断日志(默认 false)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var opName = ToolInputHelpers.String(input["operation_name"]);
            var fallbackWorld = GunToolHelpers.Bool(input["fallback_world"], true);
            var filter = GunToolHelpers.ParsePointFilter(ToolInputHelpers.String(input["point_filter"]));
            var useMfg = GunToolHelpers.Bool(input["use_mfg_name"], false);
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
                sb.Append("\n操作: ").Append(op.Name).Append('\n');

                try { PsReader.FillPoints(op, filter, useMfg, log); }
                catch (Exception ex) { sb.Append("  Error: 读取点失败 - ").Append(ex.Message).Append('\n'); continue; }

                if (op.Points == null || op.Points.Count == 0)
                {
                    sb.Append("  ⚠ 该操作下没有符合过滤条件的点\n");
                    continue;
                }
                sb.Append("  点数: ").Append(op.Points.Count).Append('\n');

                PsReader.RefFrameResult r;
                try { r = PsReader.ResolveOperationRefFrame(op, false, log); }
                catch (Exception ex) { sb.Append("  Error: 参考坐标解析失败 - ").Append(ex.Message).Append('\n'); continue; }

                bool resolved = r != null && r.Source != PsReader.RefFrameSource.None;

                // ── LeadingPart 兜底：标准解析没绑到东西时才跑 ──
                List<AppearanceRef> leading = null;
                if (!resolved)
                {
                    try { leading = GunToolHelpers.CollectLeadingParts(op); }
                    catch (Exception ex) { log("LeadingPart 探测异常: " + ex.Message); }
                }

                if (!resolved && leading != null && leading.Count > 0)
                {
                    var lp = leading[0];
                    sb.Append("  来源: LeadingPart(标准外观绑定为空,由 LeadingPart 兜底)\n")
                      .Append("  名称: ").Append(lp.Name).Append('\n')
                      .Append("  位姿: ").Append(GunToolHelpers.FmtMatrix(lp.Matrix)).Append('\n');
                    if (showMatrix) sb.Append(GunToolHelpers.FmtRaw4x4(lp.Matrix, "    ")).Append('\n');
                    AppendList(sb, "LeadingPart 候选", leading, showMatrix);
                    sb.Append("  提示: 用 ps_transform_points_to_reference 并传 ref_name=\"")
                      .Append(lp.Name).Append("\" 可得到焊点在该零件自身坐标系下的位姿\n");
                    continue;
                }

                if (!resolved)
                {
                    if (fallbackWorld)
                    {
                        sb.Append("  来源: World(未找到任何零件/夹具/LeadingPart 绑定)\n")
                          .Append("  名称: 世界坐标系\n")
                          .Append("  说明: 相对坐标与世界坐标等价\n");
                    }
                    else
                    {
                        sb.Append("  结果: 未解析到参考坐标(无任何绑定,且未启用世界系回退)\n");
                    }
                    continue;
                }

                sb.Append("  来源: ").Append(r.Source).Append('\n')
                  .Append("  名称: ").Append(r.Name).Append('\n')
                  .Append("  位姿: ").Append(GunToolHelpers.FmtMatrix(r.Matrix)).Append('\n');
                if (showMatrix) sb.Append(GunToolHelpers.FmtRaw4x4(r.Matrix, "    ")).Append('\n');
                if (!string.IsNullOrEmpty(r.PointName))
                    sb.Append("  依据点: ").Append(r.PointName).Append('\n');
                if (PsReader.IsIdentity(r.Matrix))
                    sb.Append("  说明: 该坐标为单位阵,相对坐标与世界坐标等价\n");

                AppendList(sb, "零件候选", r.PartCandidates, showMatrix);
                AppendList(sb, "夹具候选", r.FixtureCandidates, showMatrix);

                if (r.NeedsUserChoice)
                {
                    sb.Append("  ⚠ 冲突: ").Append(r.ConflictReason).Append('\n')
                      .Append("     导出前需明确选定一个(在 ps_transform_points_to_reference 或 ")
                      .Append("export_gun_full 里传 ref_name)\n");
                }
            }

            if (verbose && logSb.Length > 0)
                sb.Append("\n[PS 日志]\n").Append(logSb.ToString());

            return sb.ToString().TrimEnd();
        }

        private static void AppendList(StringBuilder sb, string title, List<AppearanceRef> list, bool showMatrix)
        {
            if (list == null || list.Count == 0) return;
            sb.Append("  ").Append(title).Append(" (").Append(list.Count).Append("):\n");
            foreach (var a in list)
            {
                if (a == null) continue;
                sb.Append("    - ").Append(a.Name);
                if (!string.IsNullOrEmpty(a.ParentPartName)) sb.Append("  <父零件: ").Append(a.ParentPartName).Append('>');
                if (!string.IsNullOrEmpty(a.TypeName)) sb.Append("  [").Append(a.TypeName).Append(']');
                sb.Append("\n        ").Append(GunToolHelpers.FmtMatrix(a.Matrix)).Append('\n');
                if (showMatrix) sb.Append(GunToolHelpers.FmtRaw4x4(a.Matrix, "          ")).Append('\n');
            }
        }
    }
}
