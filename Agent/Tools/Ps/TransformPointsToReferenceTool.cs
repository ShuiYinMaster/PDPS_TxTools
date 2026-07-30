// TxTools.Agent / Tools / Ps / TransformPointsToReferenceTool.cs
// 子工具 3：参考坐标的变换。
// 把焊点/路径点的世界坐标换算到指定参考系（夹具、零件外观、LeadingPart 等自身坐标）下：
//     相对矩阵 = Inv(参考矩阵) × 世界矩阵
//
// v2 变更：
//  · ref_name 现在同时在 零件候选 / 夹具候选 / LeadingPart 三处匹配，
//    覆盖"焊点只有 LeadingPart"的常见场景（此前只能自己写脚本手算）
//  · 操作名找不到时全局搜索操作树
//  · show_matrix=true 同时输出世界与相对的原始 4x4
//
// 只读工具，不改动模型。

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.ExportGun;

namespace TxTools.Agent.Core
{
    public sealed class TransformPointsToReferenceTool : ITxAgentTool
    {
        public string Name { get { return "ps_transform_points_to_reference"; } }

        public string Description
        {
            get
            {
                return "把焊点坐标从世界系变换到参考系(夹具/零件外观/LeadingPart 自身坐标)。" +
                       "相对矩阵 = Inv(参考矩阵) × 世界矩阵。" +
                       "参数 operation_name (可选,找不到会全局搜索), ref_mode (auto/world,默认 auto), " +
                       "ref_name (可选,零件/夹具/LeadingPart 名,优先于 auto), " +
                       "point_filter, use_mfg_name, max_points (默认 30), show_matrix。";
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
                        'ref_mode':       { 'type': 'string',  'description': 'auto=自动解析参考系; world=保持世界坐标(默认 auto)' },
                        'ref_name':       { 'type': 'string',  'description': '指定参考系名(零件/夹具/LeadingPart),优先于 auto' },
                        'point_filter':   { 'type': 'string',  'description': 'weld / path / continuous / all(默认 all)' },
                        'use_mfg_name':   { 'type': 'boolean', 'description': '是否用制造特征名作为点名(默认 false)' },
                        'max_points':     { 'type': 'integer', 'description': '每个操作最多输出多少个点(默认 30)' },
                        'show_matrix':    { 'type': 'boolean', 'description': '输出原始 4x4 矩阵(默认 false)' },
                        'verbose':        { 'type': 'boolean', 'description': '是否输出 PS 侧诊断日志(默认 false)' }
                    }
                }");
            }
        }

        public string Execute(JObject input)
        {
            var opName = ToolInputHelpers.String(input["operation_name"]);
            var refMode = ToolInputHelpers.String(input["ref_mode"]);
            var refName = ToolInputHelpers.String(input["ref_name"]);
            var filter = GunToolHelpers.ParsePointFilter(ToolInputHelpers.String(input["point_filter"]));
            var useMfg = GunToolHelpers.Bool(input["use_mfg_name"], false);
            var maxPoints = GunToolHelpers.Int(input["max_points"], 30);
            var showMatrix = GunToolHelpers.Bool(input["show_matrix"], false);
            var verbose = GunToolHelpers.Bool(input["verbose"], false);
            if (maxPoints <= 0) maxPoints = 30;

            bool forceWorld = !string.IsNullOrEmpty(refMode)
                && refMode.Trim().Equals("world", StringComparison.OrdinalIgnoreCase);

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

                // ── 确定参考矩阵 ──
                double[] refMatrix;
                string refLabel;

                if (forceWorld)
                {
                    refMatrix = GunToolHelpers.Identity();
                    refLabel = "世界坐标系(强制)";
                }
                else
                {
                    PsReader.RefFrameResult r = null;
                    try { r = PsReader.ResolveOperationRefFrame(op, false, log); }
                    catch (Exception ex) { sb.Append("  Error: 参考坐标解析失败 - ").Append(ex.Message).Append('\n'); continue; }

                    List<AppearanceRef> leading = null;
                    try { leading = GunToolHelpers.CollectLeadingParts(op); }
                    catch { }

                    if (!string.IsNullOrEmpty(refName))
                    {
                        // 三处都找：零件候选 / 夹具候选 / LeadingPart
                        AppearanceRef picked = Find(r != null ? r.PartCandidates : null, refName)
                                            ?? Find(r != null ? r.FixtureCandidates : null, refName)
                                            ?? Find(leading, refName);
                        if (picked == null)
                        {
                            var e = new StringBuilder();
                            e.Append("  Error: 没有名为 \"").Append(refName).Append("\" 的参考系。可用:\n");
                            AppendNames(e, r != null ? r.PartCandidates : null, "零件");
                            AppendNames(e, r != null ? r.FixtureCandidates : null, "夹具");
                            AppendNames(e, leading, "LeadingPart");
                            sb.Append(e.ToString());
                            continue;
                        }
                        refMatrix = picked.Matrix;
                        refLabel = picked.Name + "(指定)";
                    }
                    else if (r != null && r.Source != PsReader.RefFrameSource.None)
                    {
                        refMatrix = r.Matrix;
                        refLabel = r.Name;
                        if (r.NeedsUserChoice)
                            sb.Append("  ⚠ 零件与夹具坐标不一致(").Append(r.ConflictReason)
                              .Append("),当前按默认候选换算;如需改用其它候选请传 ref_name\n");
                    }
                    else if (leading != null && leading.Count > 0)
                    {
                        // 标准解析为空，LeadingPart 兜底
                        refMatrix = leading[0].Matrix;
                        refLabel = leading[0].Name + "(LeadingPart 兜底)";
                    }
                    else
                    {
                        refMatrix = GunToolHelpers.Identity();
                        refLabel = "世界坐标系(无任何绑定)";
                    }
                }

                sb.Append("  参考系: ").Append(refLabel).Append('\n')
                  .Append("  参考位姿: ").Append(GunToolHelpers.FmtMatrix(refMatrix)).Append('\n');
                if (showMatrix) sb.Append(GunToolHelpers.FmtRaw4x4(refMatrix, "    ")).Append('\n');

                bool identity = PsReader.IsIdentity(refMatrix);
                if (identity) sb.Append("  说明: 参考系为单位阵,输出值等同世界坐标\n");

                // ── 逐点换算 ──
                sb.Append("  点数 ").Append(op.Points.Count)
                  .Append(op.Points.Count > maxPoints ? "(仅列出前 " + maxPoints + " 个)" : "")
                  .Append(":\n");

                int n = 0;
                foreach (var pt in op.Points)
                {
                    if (pt == null) continue;
                    if (n >= maxPoints) break;
                    n++;

                    double[] world = pt.TCPMatrix;
                    if (world == null || world.Length < 16)
                    {
                        sb.Append("    ").Append(n).Append(". ").Append(pt.Name).Append("  ⚠ 无矩阵数据\n");
                        continue;
                    }

                    double[] rel;
                    try { rel = PsReader.ToRelative(world, refMatrix); }
                    catch (Exception ex)
                    {
                        sb.Append("    ").Append(n).Append(". ").Append(pt.Name)
                          .Append("  ⚠ 换算失败: ").Append(ex.Message).Append('\n');
                        continue;
                    }

                    sb.Append("    ").Append(n).Append(". ").Append(pt.Name)
                      .Append("  [").Append(pt.Type).Append("]\n")
                      .Append("        相对: ").Append(GunToolHelpers.FmtMatrix(rel)).Append('\n');
                    if (!identity)
                        sb.Append("        世界: ").Append(GunToolHelpers.FmtMatrix(world)).Append('\n');
                    if (showMatrix)
                        sb.Append("        相对 4x4:\n").Append(GunToolHelpers.FmtRaw4x4(rel, "          ")).Append('\n');
                }
            }

            if (verbose && logSb.Length > 0)
                sb.Append("\n[PS 日志]\n").Append(logSb.ToString());

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
                if (a != null) sb.Append("    - [").Append(tag).Append("] ").Append(a.Name).Append('\n');
        }
    }
}
