// TxTools.Agent / Ps / PsBridge.cs
// PS 场景访问门面：所有工具对 Tecnomatix.Engineering / PsReader 的调用都收敛到这里。
// 套路：dynamic + try/catch 兜 SDK 版本差异；经 PsContext.Current.Run(...) 路由回 PS 主线程。
//
// 依赖：引用 TxTools.ExportGun.PsReader / OperationInfo / PointType 等(仅本文件)。
// 物理树遍历照搬 PsReader.EnumDisplayableObjects 的健壮策略：
//   PhysicalRoot/ComponentRoot/ResourceRoot + GetAllDescendants(TxTypeFilter) + 无参回退。

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json.Linq;
using TxTools.RobotReachabilityChecker.Services;
using TxTools.RobotReachabilityChecker.Models;
using TxTools.WeldAnnotator;
using Tecnomatix.Engineering;
using TxTools.Agent.Core;
using TxTools.ExportGun;
using RobotBaseResult = TxTools.RobotBaseChecker.RobotBaseResult;
using RobotBaseReader = TxTools.RobotBaseChecker.RobotBaseReader;
using BrandMode = TxTools.RobotBaseChecker.BrandMode;
using RobotKinematics = TxTools.RobotBaseChecker.RobotKinematics;

namespace TxTools.Agent.Ps
{
    public static class PsBridge
    {
        private static readonly Action<string> Nolog = delegate (string s) { };

        // 仿真播放器引用（供 simulate_operation 的 stop/pause/rewind 后续控制）
        private static TxSimulationPlayer _lastSimPlayer;
        private static TxDocument _lastSimDocument;
        private static string _lastSimOperationId;
        private static long _lastSimGeneration;

        // ───────── 基础查询 ─────────

        public static string GetSelectedObjectsSummary()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var names = new List<string>();
                    dynamic sel = TxApplication.ActiveSelection;
                    dynamic items = sel.GetItems();
                    var en = items as IEnumerable;
                    if (en != null) foreach (var o in en) names.Add(SafeName(o));

                    if (names.Count == 0) return "当前没有选中任何对象。";
                    var sb = new StringBuilder();
                    sb.AppendLine("当前选中 " + names.Count + " 个对象：");
                    for (int i = 0; i < names.Count; i++) sb.AppendLine((i + 1) + ". " + names[i]);
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 读取选中对象失败: " + ex.Message; }
            });
        }

        // 在不知道目标属性精确类型时，从字符串尝试设置属性（支持 string/enum/ctor(string)/Parse/Convert）
        private static void SetPropertyFromString(object target, string propName, string value)
        {
            if (target == null || string.IsNullOrWhiteSpace(propName) || value == null) return;
            try
            {
                var t = target.GetType();
                var p = t.GetProperty(propName);
                if (p == null || !p.CanWrite) return;
                var pt = p.PropertyType;

                if (pt == typeof(string)) { p.SetValue(target, value, null); return; }
                if (pt.IsEnum)
                {
                    try { var ev = Enum.Parse(pt, value, true); p.SetValue(target, ev, null); return; } catch { }
                }
                var ctor = pt.GetConstructor(new[] { typeof(string) });
                if (ctor != null)
                {
                    try { var obj = ctor.Invoke(new object[] { value }); p.SetValue(target, obj, null); return; } catch { }
                }
                var mi = pt.GetMethod("Parse", new[] { typeof(string) });
                if (mi != null && mi.IsStatic)
                {
                    try { var obj = mi.Invoke(null, new object[] { value }); p.SetValue(target, obj, null); return; } catch { }
                }
                try { var cv = Convert.ChangeType(value, pt); p.SetValue(target, cv, null); return; } catch { }
            }
            catch { }
        }

        public static string GetActiveDocumentSummary()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    dynamic doc = TxApplication.ActiveDocument;
                    if (doc == null) return "当前没有打开的文档。";
                    string name = "未知";
                    try { name = (string)doc.Name; } catch { }
                    return "当前文档: " + name;
                }
                catch (Exception ex) { return "Error: 读取文档信息失败: " + ex.Message; }
            });
        }

        // ───────── 场景树遍历 / 统计 (信息汇总核心) ─────────

        /// <summary>
        /// 按类型统计场景对象。typeKeyword 为空 -> 输出整场景类型直方图；
        /// 非空 -> 列出类型名包含该关键字的对象(机器人额外用 is TxRobot 兜)。
        /// 用它精确回答"场景里有多少机器人/夹具/…"，不要从操作列表推断。
        /// </summary>
        public static string CountObjectsByType(string typeKeyword)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var all = CollectScene(true);
                    if (all.Count == 0)
                        return "未能遍历到场景对象(可能没有打开文档，或该 SDK 版本根不可用)。";

                    if (string.IsNullOrWhiteSpace(typeKeyword))
                    {
                        var hist = Histogram(all);
                        var sb = new StringBuilder();
                        sb.AppendLine("场景对象类型统计 (共 " + all.Count + " 个)：");
                        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(25))
                            sb.AppendLine("• " + kv.Key + ": " + kv.Value);
                        if (hist.Count > 25) sb.AppendLine("…(其余类型省略)");
                        return sb.ToString();
                    }

                    var key = typeKeyword.Trim();
                    bool wantRobot = key.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0
                                     || key.Contains("机器人");
                    var matches = new List<string>();
                    foreach (var o in all)
                    {
                        var tn = o.GetType().Name;
                        bool hit = tn.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!hit && wantRobot && o is TxRobot) hit = true;
                        if (hit) matches.Add(SafeName(o) + " [" + tn + "]");
                    }

                    var sb2 = new StringBuilder();
                    sb2.AppendLine("匹配 \"" + key + "\" 的对象 " + matches.Count + " 个：");
                    int cap = Math.Min(matches.Count, 40);
                    for (int i = 0; i < cap; i++) sb2.AppendLine((i + 1) + ". " + matches[i]);
                    if (matches.Count > cap) sb2.AppendLine("…(其余 " + (matches.Count - cap) + " 个省略)");
                    return sb2.ToString();
                }
                catch (Exception ex) { return "Error: 统计场景对象失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 展开一个组件(按 name 查找，缺省用当前选中第一个)，按类型统计其子对象数量。
        /// 用它回答"CD_L 下有多少设备"这类层级问题。recursive=true 递归到底，false 仅直接子级。
        /// </summary>
        public static string ListChildren(string name, bool recursive, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject target = null;
                    if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(objectId))
                    {
                        string rerr;
                        if (!TryResolve(name, objectId, out target, out rerr)) return "Error: " + rerr;
                    }
                    if (target == null) target = FirstSelected();
                    if (target == null)
                        return string.IsNullOrWhiteSpace(name)
                            ? "请先选中一个对象，或提供 name 参数。"
                            : "未找到名为 " + name + " 的对象。";

                    var kids = DescendantsOf(target, recursive);
                    if (kids == null || kids.Count == 0)
                        return SafeName(target) + " 下没有可枚举的子对象。";

                    var hist = new Dictionary<string, int>(StringComparer.Ordinal);
                    var sample = new List<string>();
                    foreach (ITxObject o in kids)
                    {
                        if (o == null) continue;
                        var tn = o.GetType().Name;
                        int c; hist[tn] = hist.TryGetValue(tn, out c) ? c + 1 : 1;
                        if (sample.Count < 40) sample.Add(SafeName(o) + " [" + tn + "]");
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine(SafeName(target) + " 下" + (recursive ? "(递归)" : "(直接子级)")
                                  + "共 " + kids.Count + " 个对象，按类型：");
                    foreach (var kv in hist.OrderByDescending(k => k.Value))
                        sb.AppendLine("• " + kv.Key + ": " + kv.Value);
                    sb.AppendLine("示例：");
                    foreach (var n in sample) sb.AppendLine("  - " + n);
                    if (kids.Count > sample.Count) sb.AppendLine("  …(其余省略)");
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 列出子对象失败: " + ex.Message; }
            });
        }

        // ───────── PsReader 支撑的只读原语 ─────────

        public static string ListOperations()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var ops = PsReader.GetOperationsFromSelection(Nolog);
                    if (ops == null || ops.Count == 0) return "当前选择里没有可识别的操作。";

                    var sb = new StringBuilder();
                    sb.AppendLine("选中操作 " + ops.Count + " 个：");
                    int i = 0;
                    foreach (var op in ops)
                    {
                        i++;
                        string tool = "";
                        try { tool = PsReader.GetToolNameFromOperation(op); } catch { }
                        sb.Append(i).Append(". ").Append(op.Name);
                        if (!string.IsNullOrEmpty(op.TypeLabel)) sb.Append(" [").Append(op.TypeLabel).Append("]");
                        if (!string.IsNullOrEmpty(tool)) sb.Append("  工具=").Append(tool);
                        sb.AppendLine();
                    }
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 读取操作失败: " + ex.Message; }
            });
        }

        public static string CountPoints(string pointType, bool useMfgName)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var ops = PsReader.GetOperationsFromSelection(Nolog);
                    if (ops == null || ops.Count == 0) return "当前选择里没有可识别的操作。";

                    var pt = ParsePointType(pointType);
                    int total = 0;
                    var sb = new StringBuilder();
                    sb.AppendLine("点统计 (类型=" + pt + ", useMfgName=" + useMfgName + ")：");
                    foreach (var op in ops)
                    {
                        op.Points.Clear();
                        PsReader.FillPoints(op, pt, useMfgName, Nolog);
                        total += op.Points.Count;
                        sb.AppendLine("• " + op.Name + ": " + op.Points.Count + " 点");
                    }
                    sb.Append("合计: ").Append(total).Append(" 点");
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 统计点失败: " + ex.Message; }
            });
        }

        public static string ListTcpOptions()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var ops = PsReader.GetOperationsFromSelection(Nolog);
                    if (ops == null || ops.Count == 0) return "当前选择里没有可识别的操作。";
                    var op = ops[0];
                    var tcps = PsReader.EnumerateTcpOptions(op, Nolog);
                    if (tcps == null || tcps.Count == 0) return "操作 " + op.Name + " 没有可用的 TCP 选项。";

                    var sb = new StringBuilder();
                    sb.AppendLine("操作 " + op.Name + " 的 TCP 选项 " + tcps.Count + " 个：");
                    foreach (var t in tcps) sb.AppendLine("• " + t.Name + (t.IsDefault ? "  (默认)" : ""));
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 读取 TCP 选项失败: " + ex.Message; }
            });
        }

        public static string GetReferenceFrameSummary()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var rf = PsReader.GetReferenceFrame();
                    if (rf == null || rf.Item2 == null) return "未取得参考坐标系，按世界坐标系处理。";
                    bool isWorld = PsReader.IsIdentity(rf.Item2);
                    return "参考坐标系: " + (rf.Item1 ?? "未知") + (isWorld ? " (等同世界系)" : "");
                }
                catch (Exception ex) { return "Error: 读取参考坐标系失败: " + ex.Message; }
            });
        }

        // ───────── 动作：选中 / 真实焊点导出 ─────────

        /// <summary>在主线程执行插件完整分析，不弹出恢复提示窗口。</summary>
        public static string CheckReachability(string operationName, string objectId = null,
            CheckOptions options = null, int maxResults = 100)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    if (TxApplication.ActiveDocument == null) return "Error: 没有打开的研究文档。";
                    List<ITxObject> roots; string error;
                    if (!ResolveToolOperations(operationName, objectId, out roots, out error)) return "Error: " + error;
                    options = options ?? new CheckOptions();
                    options.ShowRecoveryDialog = false;
                    var output = new JArray();
                    var all = new List<PathPointResult>();
                    int expectedTotal = 0;
                    foreach (var root in roots)
                    {
                        var locations = LocationEnumerator.EnumerateLocations(root);
                        expectedTotal += locations.Count;
                        // 复合节点可能包含多台机器人，不能把全部点交给第一台机器人求解。
                        var robots = locations.Select(l => RobotFinder.FindAssociatedRobotSilent(l))
                            .Where(r => r != null).Select(r => r.Id).Distinct().ToList();
                        if (robots.Count > 1)
                        {
                            output.Add(new JObject { ["operation"] = Ref(root), ["error"] = "操作包含多台机器人，请用 object_id 指定单台机器人的操作。", ["expected_points"] = locations.Count });
                            continue;
                        }
                        var log = new ReachabilityToolLog();
                        var results = ReachabilityChecker.Check(root, options, null, log);
                        all.AddRange(results);
                        var task = new RobotPathCheckTask { Results = results };
                        var details = new JArray();
                        foreach (var r in results.OrderBy(r => r.Status == ReachabilityStatus.Reachable ? 1 : 0).Take(maxResults))
                            details.Add(new JObject
                            {
                                ["point"] = r.PointName, ["object_id"] = (r.LocationRef as ITxObject)?.Id,
                                ["robot"] = r.RobotName, ["status"] = r.Status.ToString(),
                                ["joints_deg"] = r.Status == ReachabilityStatus.Unreachable || r.Status == ReachabilityStatus.NotChecked
                                    ? null : new JArray(r.J1, r.J2, r.J3, r.J4, r.J5, r.J6),
                                ["joint_margin_deg"] = r.JointMargin == 999 || double.IsNaN(r.JointMargin) || double.IsInfinity(r.JointMargin)
                                    ? (JToken)JValue.CreateNull() : new JValue(r.JointMargin),
                                ["collision_state"] = r.CollisionState.ToString(), ["message"] = r.ErrorMessage
                            });
                        output.Add(new JObject
                        {
                            ["operation"] = Ref(root), ["expected_points"] = locations.Count,
                            ["complete"] = results.Count > 0 && results.Count == locations.Count,
                            ["summary"] = ReachabilitySummary(task), ["points"] = details,
                            ["omitted_points"] = results.Count - details.Count,
                            ["diagnostics"] = new JArray(log.Messages)
                        });
                    }
                    return new JObject
                    {
                        ["complete"] = output.Count > 0 && output.All(o => (bool?)o["complete"] == true),
                        ["expected_points"] = expectedTotal,
                        ["summary"] = ReachabilitySummary(new RobotPathCheckTask { Results = all }),
                        ["joint_margin_check"] = options.JointMarginCheckEnabled,
                        ["tcp_check"] = options.TcpCheckEnabled, ["interference"] = options.InterferenceEnabled,
                        ["operations"] = output
                    }.ToString();
                }
                catch (Exception ex) { return "Error: 可达性检查失败: " + ex.Message; }
            });
        }

        private sealed class ReachabilityToolLog : TxTools.RobotReachabilityChecker.Diagnostics.ILogger
        {
            public readonly List<string> Messages = new List<string>();
            public void Log(string message, string level = "INFO")
            {
                if ((level == "WARN" || level == "ERR") && Messages.Count < 30)
                    Messages.Add(level + ": " + message);
            }
        }

        private static JObject ReachabilitySummary(RobotPathCheckTask task)
        {
            return new JObject
            {
                ["total"] = task.TotalPoints, ["reachable"] = task.ReachableCount,
                ["critical"] = task.CriticalCount, ["near_limit"] = task.NearLimitCount,
                ["singular"] = task.SingularCount, ["unreachable"] = task.UnreachableCount,
                ["not_checked"] = task.Results.Count(r => r.Status == ReachabilityStatus.NotChecked),
                ["reachability_rate_percent"] = task.ReachabilityRate,
                ["collision"] = task.Results.Count(r => r.CollisionState == CollisionCheckState.Collision),
                ["collision_clean"] = task.Results.Count(r => r.CollisionState == CollisionCheckState.Clean),
                ["collision_unknown"] = task.Results.Count(r => r.CollisionState == CollisionCheckState.Error),
                ["collision_not_checked"] = task.Results.Count(r => r.CollisionState == CollisionCheckState.NotChecked)
            };
        }

        private static bool ResolveToolOperations(string name, string objectId, out List<ITxObject> roots, out string error)
        {
            roots = new List<ITxObject>(); error = null;
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                ITxObject target;
                if (!TryResolve(null, objectId, out target, out error)) return false;
                if (!(target is ITxOperation)) { error = "object_id 必须指向操作树中的操作。"; return false; }
                roots.Add(target);
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                var key = name.Trim();
                var operations = CollectOperations();
                var matches = operations.Where(o => string.Equals(SafeName(o), key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) matches = operations.Where(o => SafeName(o).IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (matches.Count != 1)
                {
                    error = matches.Count == 0 ? "操作树中未找到 " + key : "匹配多个操作，请使用 object_id：\n" + Candidates(matches);
                    return false;
                }
                roots.Add(matches[0]);
            }
            else
            {
                var selected = PsReader.GetOperationsFromSelection(Nolog);
                if (selected != null) roots.AddRange(selected.Where(o => o.PsObject != null).Select(o => o.PsObject));
                if (roots.Count == 0) { error = "请指定 operation/object_id，或在操作树中选择操作。"; return false; }
            }
            // 父子同时选中时保留外层，避免重复检查和导出相同点位。
            var unique = roots.GroupBy(o => o.Id).Select(g => g.First()).ToList();
            var descendants = new HashSet<string>();
            foreach (var root in unique)
                if (root is ITxObjectCollection collection)
                {
                    var children = collection.GetAllDescendants(new TxTypeFilter(typeof(ITxOperation)));
                    foreach (ITxObject child in children) descendants.Add(child.Id);
                }
            roots = unique.Where(o => !descendants.Contains(o.Id)).ToList();
            return roots.Count > 0;
        }

        /// <summary>在操作树(OperationRoot)里按名查找操作。回答"OP120 这个操作在哪/是什么"。</summary>
        public static string FindOperations(string keyword)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var ops = CollectOperations();
                    if (ops.Count == 0) return "未能遍历到操作树(OperationRoot)。";

                    var key = (keyword ?? "").Trim();
                    var matches = new List<ITxObject>();
                    foreach (var o in ops)
                    {
                        var n = SafeName(o);
                        if (key.Length == 0 || (n != null && n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0))
                            matches.Add(o);
                    }
                    if (matches.Count == 0) return "操作树里没有名称含 \"" + key + "\" 的操作。";

                    var sb = new StringBuilder();
                    sb.AppendLine("匹配操作 " + matches.Count + " 个：");
                    int cap = Math.Min(matches.Count, 40);
                    for (int i = 0; i < cap; i++)
                        sb.AppendLine((i + 1) + ". " + Ref(matches[i]) + " [" + matches[i].GetType().Name + "]");
                    if (matches.Count > cap) sb.AppendLine("…(其余 " + (matches.Count - cap) + " 个省略)");
                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 查找操作失败: " + ex.Message; }
            });
        }

        /// <summary>复用焊点标注插件的点位读取、投影及可编辑 Excel 导出。</summary>
        public static string ExportWeldScreenshots(string operation, string objectId, AnnotationExportOptions options, bool autoThickness)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    if (TxApplication.ActiveDocument == null) return "Error: 没有打开的研究文档。";
                    List<ITxObject> roots; string error;
                    if (!ResolveToolOperations(operation, objectId, out roots, out error)) return "Error: " + error;
                    var points = new List<WeldAnnotationPoint>();
                    var categories = new Dictionary<int, string>();
                    var diagnostics = new List<string>();
                    Action<string> log = message => { if (diagnostics.Count < 30) diagnostics.Add(message); };
                    foreach (var root in roots)
                    {
                        var op = new OperationInfo { Name = SafeName(root), TypeLabel = root.GetType().Name, PsObject = root };
                        PsReader.FillPoints(op, PointType.WeldPoint, false, log);
                        foreach (var pi in op.Points)
                        {
                            if (pi == null || pi.Type != PointType.WeldPoint || pi.Position == null || pi.Position.Length < 3) continue;
                            int parts = pi.AllAppearances == null ? 0 : pi.AllAppearances.Where(a => a != null)
                                .Select(a => !string.IsNullOrEmpty(a.ParentPartName) ? a.ParentPartName : a.Name)
                                .Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                            var point = new WeldAnnotationPoint
                            {
                                Index = points.Count + 1, Name = pi.Name, OpName = op.Name,
                                X = pi.Position[0], Y = pi.Position[1], Z = pi.Position[2], BoundPartsCount = parts
                            };
                            points.Add(point);
                            categories[point.Index] = autoThickness && parts >= 3 ? "三层板及以上点焊" : AnnotationExportService.DefaultCategory;
                        }
                    }
                    if (points.Count == 0) return "Error: 指定操作中未读取到焊点，未写入 Excel。";
                    AnnotationExportResult exportResult;
                    using (var bitmap = PsReader.CaptureActiveViewer(System.Drawing.Size.Empty, false, log))
                    {
                        if (bitmap == null) return "Error: PS 视口截图失败。";
                        PsReader.ProjectPointsToScreen(points, bitmap.Width, bitmap.Height, log);
                        var style = new AnnotationStyle();
                        try
                        {
                            exportResult = AnnotationExportService.ExportToActiveExcel(bitmap, points, categories, style, options,
                                (level, message) => log(level + ": " + message));
                        }
                        finally { style.TextFont?.Dispose(); }
                    }
                    return new JObject
                    {
                        ["exported"] = true, ["destination"] = "活动 Excel 工作簿（未自动保存）",
                        ["points"] = points.Count, ["annotated_points"] = exportResult.AnnotatedPoints,
                        ["shape_count"] = exportResult.ShapeCount, ["warnings"] = exportResult.Warnings,
                        ["partial"] = exportResult.Warnings > 0 || exportResult.AnnotatedPoints < points.Count(p => p.InViewport),
                        ["outside_viewport"] = points.Count(p => !p.InViewport),
                        ["write_list"] = options.WriteList, ["new_sheet"] = options.NewSheet,
                        ["diagnostics"] = new JArray(diagnostics)
                    }.ToString();
                }
                catch (Exception ex) { return "Error: 焊点截图导出失败，Excel 可能已部分写入: " + ex.Message; }
            });
        }

        /// <summary>遍历操作树(OperationRoot)的全部后代操作。</summary>
        private static List<ITxObject> CollectOperations()
        {
            var list = new List<ITxObject>();
            try
            {
                dynamic doc = TxApplication.ActiveDocument;
                if (doc == null) return list;
                var opRoot = doc.OperationRoot as ITxObjectCollection; // TxOperationRoot : ITxObjectCollection
                if (opRoot != null)
                {
                    var all = opRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)));
                    if (all != null) foreach (ITxObject o in all) if (o != null) list.Add(o);
                }
            }
            catch { }
            return list;
        }

        private static TxRobot FindRobotForOps(ITxObject root, List<ITxRoboticLocationOperation> locs)
        {
            try { var r = PsReader.FindRobotForOperation(root); if (r != null) return r; } catch { }
            if (locs != null)
                foreach (var loc in locs)
                {
                    try { var r = PsReader.FindRobotForOperation(loc); if (r != null) return r; } catch { }
                }
            return null;
        }

        private static List<ITxRoboticLocationOperation> EnumerateLocationOps(ITxObject operation)
        {
            var list = new List<ITxRoboticLocationOperation>();
            if (operation == null) return list;
            var f = new TxTypeFilter(typeof(ITxRoboticLocationOperation));

            if (operation is ITxCompoundOperation comp)
            {
                try
                {
                    var objs = comp.GetAllDescendants(f);
                    if (objs != null) foreach (ITxObject o in objs) if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch { }
            }
            if (list.Count == 0)
            {
                try
                {
                    dynamic d = operation;
                    TxObjectList objs = d.GetAllDescendants(f);
                    if (objs != null) foreach (ITxObject o in objs) if (o is ITxRoboticLocationOperation l) list.Add(l);
                }
                catch { }
            }
            if (list.Count == 0 && operation is ITxRoboticLocationOperation self) list.Add(self);
            return list;
        }

        /// <summary>按名称/ID 在场景里查找并设为当前选中(替换)。打通"查到 -> 选中 -> 操作"。</summary>
        public static string SelectObjects(IList<string> names, IList<string> objectIds = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var hasIds = objectIds != null && objectIds.Count > 0;
                    if ((names == null || names.Count == 0) && !hasIds) return "未提供要选中的名称或 ID。";

                    var all = CollectScene(true);
                    var map = new Dictionary<string, ITxObject>(StringComparer.Ordinal);
                    foreach (var o in all) { var n = SafeName(o); if (n != null && !map.ContainsKey(n)) map[n] = o; }

                    var list = new TxObjectList();
                    var found = new List<string>();
                    var missing = new List<string>();
                    var missingIds = new List<string>();

                    if (hasIds)
                    {
                        // 走 ID 精确路径：同名对象只能用 ID 区分，绝不按名称模糊
                        var doc0 = TxApplication.ActiveDocument;
                        foreach (var id in objectIds)
                        {
                            if (string.IsNullOrWhiteSpace(id)) continue;
                            ITxObject o = null;
                            try { o = doc0.GetObjectById(id.Trim()); } catch { }
                            if (o != null) { list.Add(o); found.Add(Ref(o)); }
                            else missingIds.Add(id.Trim());
                        }
                    }
                    else
                    {
                        foreach (var nm in names)
                        {
                            ITxObject o;
                            if (map.TryGetValue(nm, out o)) { list.Add(o); found.Add(Ref(o)); }
                            else
                            {
                                var c = all.FirstOrDefault(x =>
                                {
                                    var n = SafeName(x);
                                    return n != null && n.IndexOf(nm, StringComparison.OrdinalIgnoreCase) >= 0;
                                });
                                if (c != null) { list.Add(c); found.Add(Ref(c)); }
                                else missing.Add(nm);
                            }
                        }
                    }

                    if (found.Count == 0)
                        return missingIds.Count > 0
                            ? "没有匹配到任何对象。未找到的 ID: " + string.Join(", ", missingIds)
                            : "没有匹配到任何对象。未找到: " + string.Join(", ", missing);

                    try { var sel = TxApplication.ActiveSelection; sel.Clear(); sel.AddItems(list); }
                    catch
                    {
                        try { TxApplication.ActiveSelection.SetItems(list); }
                        catch (Exception ex) { return "Error: 设置选中失败: " + ex.Message; }
                    }

                    var msg = "已选中 " + found.Count + " 个对象: " + string.Join(", ", found.Take(20));
                    if (missing.Count > 0) msg += "；未找到: " + string.Join(", ", missing);
                    if (missingIds.Count > 0) msg += "；未找到的 ID: " + string.Join(", ", missingIds);
                    return msg;
                }
                catch (Exception ex) { return "Error: 选中对象失败: " + ex.Message; }
            });
        }

        /// <summary>遍历场景，把匹配 typeKeyword 的对象清单(名称/类型/父级)一步导出为 xlsx。真实数据流。</summary>
        public static string ExportObjectList(string typeKeyword, string folder)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var all = CollectScene(true);
                    if (all.Count == 0) return "未能遍历到场景对象。";

                    var key = (typeKeyword ?? "").Trim();
                    bool wantRobot = key.IndexOf("robot", StringComparison.OrdinalIgnoreCase) >= 0
                                     || key.Contains("机器人");
                    var matched = new List<ITxObject>();
                    foreach (var o in all)
                    {
                        var tn = o.GetType().Name;
                        bool hit = key.Length == 0 || tn.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0
                                   || (wantRobot && o is TxRobot);
                        if (hit) matched.Add(o);
                    }
                    if (matched.Count == 0) return "没有匹配 \"" + key + "\" 的对象，未导出。";

                    var headers = new List<string> { "#", "名称", "类型", "父级" };
                    var rows = new List<IList<string>>();
                    int i = 0;
                    foreach (var o in matched)
                    {
                        i++;
                        rows.Add(new List<string> { i.ToString(), SafeName(o), o.GetType().Name, ParentName(o) });
                    }

                    var fld = string.IsNullOrWhiteSpace(folder)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TxAgentExport")
                        : folder;
                    var tag = SanitizeTag(key.Length > 0 ? key : "objects");
                    var fname = tag + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx";
                    var path = XlsxWriter.Write(Path.Combine(fld, fname), tag, headers, rows);
                    return "已导出 " + matched.Count + " 个对象到: " + path;
                }
                catch (Exception ex) { return "Error: 导出对象清单失败: " + ex.Message; }
            });
        }

        /// <summary>把当前选中操作的焊点/路径点坐标导出为 Excel(复用你的 ExcelExporter，含参考系转换)。</summary>
        public static string ExportPointsExcel(string pointType, bool useMfg, string folder)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var ops = PsReader.GetOperationsFromSelection(Nolog);
                    if (ops == null || ops.Count == 0) return "当前选择里没有可识别的操作。";

                    var pt = ParsePointType(pointType);
                    int total = 0;
                    foreach (var op in ops)
                    {
                        op.Points.Clear();
                        PsReader.FillPoints(op, pt, useMfg, Nolog);
                        total += op.Points.Count;
                    }
                    if (total == 0) return "未找到符合条件的点，未导出。";

                    double[] refMatrix = null;
                    try { var rf = PsReader.GetReferenceFrame(); if (rf != null) refMatrix = rf.Item2; }
                    catch (Exception ex) { return "Error: 读取当前参考系失败，未导出: " + ex.Message; }

                    var path = ExcelExporter.Export(ops, refMatrix, folder, Nolog);
                    return path != null
                        ? "已导出 " + total + " 个点到: " + path
                        : "Error: 导出失败(无数据或写入错误)。";
                }
                catch (Exception ex) { return "Error: 导出焊点 Excel 失败: " + ex.Message; }
            });
        }

        // ───────── 变更操作 (待接入) ─────────

        /// <summary>把当前选中设备最低点对齐到世界 Z=0 (变更，可 Ctrl+Z 撤销)。</summary>
        public static string AlignSelectedDevicesToFloor()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try { return DeviceZAlignService.AlignSelection(); }
                catch (Exception ex) { return "Error: 对齐失败: " + ex.Message; }
            });
        }

        // ───────── API 探查 / 动态代码 ─────────

        /// <summary>探查一个活动对象(按 name 或当前选中第一个)的运行时类型与成员取值。</summary>
        public static string InspectObject(string name, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject target; string rerr;
                    if (!TryResolve(name, objectId, out target, out rerr)) return "Error: " + rerr;
                    if (target == null)
                        return string.IsNullOrWhiteSpace(name) ? "请先选中一个对象，或提供 name。" : ("未找到 " + name);
                    return ApiInspector.InspectObjectLive(target);
                }
                catch (Exception ex) { return "Error: 探查对象失败: " + ex.Message; }
            });
        }

        /// <summary>编译(后台)+执行(主线程, 包 Undo)用户 C# 代码。调用方负责审批与审计。</summary>
        public static string RunCSharp(string code)
        {
            bool ignored;
            return RunCSharp(code, out ignored, null);
        }

        /// <summary>
        /// 带成功标志的重载。
        ///
        /// 【为什么把 success 带出来，而不是让调用方解析返回文本】
        /// "编译没过"和"执行抛异常"这两件事，在这个方法内部是确定已知的；
        /// 一旦出了这个方法就只剩一段给人看的字符串，再判断就成了猜 ——
        /// 而猜错的代价是把失败记成成功，静默地污染片段成功率。
        ///
        /// 注意 success=true 的含义仅限于"编译通过且没有抛出异常"。
        /// 代码自己 return 出来的业务性失败（比如"未找到对象"）它看不出来，
        /// 那需要调用方结合语义判断。
        /// </summary>
        /// <param name="undoLabel">
        /// undo 块的名字，出现在用户的 Ctrl+Z 历史里。留空则用 "run_csharp"。
        /// 【配方执行务必传】配方不走审批，undo 是唯一的兜底手段；
        /// 全都叫 "run_csharp" 的话，连跑三个配方后用户根本认不出该撤到哪一步。
        /// </param>
        public static string RunCSharp(string code, out bool success, string undoLabel = null)
        {
            return RunCSharp(code, out success, undoLabel, null);
        }

        internal static string RunCSharp(string code, out bool success, string undoLabel, Func<string> validateBeforeRun)
        {
            success = false;
            if (string.IsNullOrWhiteSpace(code)) return "未提供代码。";

            // 1) 编译：纯 CPU，不碰 PS —— 在调用线程(后台)进行，不冻结 UI。
            string compileError;
            var assembly = CSharpRunner.Compile(code, out compileError);
            if (assembly == null) return compileError;

            // out 参数不能被匿名方法捕获，用局部变量中转。
            bool ok = false;

            // 2) 执行：碰 PS，必须主线程，包在 Undo 块里(可撤销)。
            var text = PsContext.Current.Run<string>(delegate
            {
                var validation = validateBeforeRun?.Invoke();
                if (validation != null) return validation;
                var log = new StringBuilder();
                Action<string> logfn = delegate (string s) { if (s != null) log.AppendLine(s); };

                TxDocument doc = null;
                try { doc = TxApplication.ActiveDocument; } catch { }
                var label = string.IsNullOrWhiteSpace(undoLabel) ? "run_csharp" : undoLabel;
                var undo = BeginUndo(doc, label);

                string result;
                try { result = CSharpRunner.Invoke(assembly, logfn); ok = true; }
                catch (Exception ex) { result = "执行异常: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                finally { if (undo != null) EndUndo(undo); }

                try { TxApplication.RefreshDisplay(); } catch { }

                var sb = new StringBuilder();
                if (log.Length > 0) sb.Append("日志:\n").Append(log.ToString());
                sb.Append("结果: ").Append(result);
                return sb.ToString();
            });

            // PsContext.Current.Run 是同步封送，回到这里时 ok 已经写好了。
            success = ok;
            return text;
        }

        // ───────── 新增：机器人基座校验 ─────────

        /// <summary>检查 BASE0，或在一个撤销分组中同步偏差项并复查。</summary>
        public static string CheckRobotBase(double posTolMm, double rotTol, string brandMode,
            string name = null, string objectId = null, bool correct = false)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var doc = TxApplication.ActiveDocument;
                    if (doc == null) return "Error: 没有打开的研究文档。";
                    BrandMode mode;
                    if (!Enum.TryParse(brandMode, true, out mode) || !Enum.IsDefined(typeof(BrandMode), mode))
                        return "Error: brand_mode 必须是 Auto/Fanuc/Generic。";
                    TxRobot target = null;
                    if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(objectId))
                    {
                        ITxObject obj; string error;
                        if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                        target = obj as TxRobot;
                        if (target == null) return "Error: 指定对象不是机器人。";
                    }
                    var results = RobotBaseReader.Analyze(posTolMm, rotTol, mode);
                    if (target != null) results = results.Where(r => r.RobotRef != null && r.RobotRef.Id == target.Id).ToList();
                    if (results.Count == 0) return "Error: 未找到可检查的机器人。";
                    int success = 0, skipped = 0;
                    var failures = new JArray();
                    if (correct)
                    {
                        using (var undo = TxTools.Common.SceneUndoScope.Begin(doc, "Agent 校正 BASE0"))
                        foreach (var r in results)
                        {
                            undo.EnsureDocument();
                            try
                            {
                                if (RobotBaseReader.Synchronize(r)) success++; else skipped++;
                            }
                            catch (Exception ex) { failures.Add(new JObject { ["robot"] = Ref(r.RobotRef), ["error"] = ex.Message }); }
                        }
                        var ids = new HashSet<string>(results.Where(r => r.RobotRef != null).Select(r => r.RobotRef.Id));
                        results = RobotBaseReader.Analyze(posTolMm, rotTol, mode).Where(r => r.RobotRef != null && ids.Contains(r.RobotRef.Id)).ToList();
                    }
                    var rows = new JArray();
                    foreach (var r in results)
                        rows.Add(new JObject
                        {
                            ["robot"] = r.RobotName, ["object_id"] = r.RobotRef?.Id, ["brand"] = r.Brand,
                            ["comparable"] = r.Comparable, ["verdict"] = r.Verdict,
                            ["delta_position_mm"] = FiniteValue(r.DeltaPos), ["delta_rotation_native"] = FiniteValue(r.DeltaRot)
                        });
                    return new JObject
                    {
                        ["mode"] = correct ? "correct" : "check", ["total"] = results.Count, ["tolerance_mm"] = posTolMm,
                        ["tolerance_rot_native"] = rotTol,
                        ["consistent"] = results.Count(r => r.Comparable && r.Verdict == "一致"),
                        ["deviation"] = results.Count(r => r.Comparable && r.Verdict != "一致"),
                        ["no_base0"] = results.Count(r => r.Verdict == "无当前BASE0"),
                        ["not_comparable"] = results.Count(r => !r.Comparable && r.Verdict != "无当前BASE0"),
                        ["updated"] = success, ["skipped"] = skipped, ["failures"] = failures, ["robots"] = rows,
                        ["undo"] = correct ? "已完成的场景修改可在当前 PS 工程 Ctrl+Z 撤销；部分失败不会自动回滚。" : ""
                    }.ToString();
                }
                catch (Exception ex) { return "Error: BASE0 操作异常: " + ex.Message; }
            });
        }

        private static JToken FiniteValue(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? (JToken)JValue.CreateNull() : new JValue(value);
        }

        // ───────── 新增：对象位置查询 ─────────

        /// <summary>查询对象的世界坐标系位置和姿态。只读。</summary>
        public static string GetObjectLocation(string name, string format, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                var loc = obj as ITxLocatableObject;
                if (loc == null) return "Error: 对象没有 AbsoluteLocation。";
                var tx = loc.AbsoluteLocation;
                var result = new StringBuilder("对象: " + Ref(obj) + "\n位置 (mm): " + tx.Translation + "\n");
                format = (format ?? "rpy").ToLowerInvariant();
                if (format == "matrix") {
                    result.AppendLine("4×4 变换矩阵（SDK 索引顺序）:");
                    for (int i = 0; i < 4; i++) {
                        for (int j = 0; j < 4; j++) result.Append(tx[i, j].ToString("F6", System.Globalization.CultureInfo.InvariantCulture)).Append(j < 3 ? ", " : "\n");
                    }
                } else {
                    if (format != "rpy" && format != "euler") return "Error: format 只支持 rpy、euler、matrix。";
                    var r = format == "euler" ? tx.RotationEulerZYZ : tx.RotationRPY_ZYX;
                    result.Append(format == "euler" ? "Euler ZYZ (度): " : "RPY ZYX (度): ");
                    result.Append((r.X * 180 / Math.PI).ToString("F4")).Append(", ").Append((r.Y * 180 / Math.PI).ToString("F4")).Append(", ").Append((r.Z * 180 / Math.PI).ToString("F4"));
                }
                return result.ToString().TrimEnd();
            });
        }

        // ───────── 新增：机器人运动学信息 ─────────

        /// <summary>查询机器人关节数、名称、当前角度、TCP 数量。只读。</summary>
        public static string InspectRobotKinematics(string name, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                ITxObject obj; string rerr;
                if (!TryResolve(name, objectId, out obj, out rerr)) return "Error: " + rerr;
                var robot = obj as TxRobot;
                if (robot == null) return string.IsNullOrWhiteSpace(name)
                    ? "当前选中对象不是机器人，无法查询运动学。"
                    : "对象 '" + name + "' 不是机器人，无法查询运动学。";

                var sb = new StringBuilder();
                sb.AppendLine("机器人: " + Ref(robot));

                try
                {
                    dynamic d = robot;
                    int jointCount = 0;
                    try { jointCount = (int)d.JointCount; } catch { }
                    sb.AppendLine("关节数: " + jointCount);

                    // 逐关节信息
                    try
                    {
                        dynamic joints = d.Joints;
                        var en = joints as IEnumerable;
                        if (en != null)
                        {
                            int idx = 0;
                            foreach (var j in en)
                            {
                                try
                                {
                                    dynamic dj = j;
                                    string jName = (string)dj.Name ?? ("J" + idx);
                                    double jVal = 0;
                                    try { jVal = (double)dj.CurrentValue; } catch { }
                                    sb.AppendLine("  J" + idx + ": " + jName + " = " + jVal.ToString("F3") + "°");
                                    idx++;
                                }
                                catch { idx++; }
                            }
                        }
                    }
                    catch { sb.AppendLine("(关节信息不可读)"); }

                    // TCP 数量
                    try
                    {
                        int tcpCount = 0;
                        dynamic tcpData = d.TCPData;
                        if (tcpData != null)
                        {
                            var tcpEn = tcpData as IEnumerable;
                            if (tcpEn != null) foreach (var t in tcpEn) tcpCount++;
                            else tcpCount = 1;
                        }
                        sb.AppendLine("TCP 数量: " + tcpCount);
                    }
                    catch { sb.AppendLine("(TCP 信息不可读)"); }

                    // 品牌（尝试读控制器类型）
                    try
                    {
                        dynamic ctrl = d.ControllerType;
                        if (ctrl != null) sb.AppendLine("控制器: " + ctrl.ToString());
                    }
                    catch { }

                    return sb.ToString();
                }
                catch (Exception ex) { return "Error: 查询运动学异常: " + ex.Message; }
            });
        }

        // ───────── 新增：查找操作绑定机器人 ─────────

        /// <summary>在操作树中查找操作绑定的机器人。只读。</summary>
        public static string FindRobotForOperation(string keyword)
        {
            return PsContext.Current.Run<string>(delegate
            {
                var ops = CollectOperations();
                if (ops.Count == 0) return "操作树中没有找到操作。";

                var sb = new StringBuilder();
                int found = 0;

                foreach (ITxObject op in ops)
                {
                    string opName = SafeName(op);
                    if (!string.IsNullOrWhiteSpace(keyword)
                        && opName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    string robotName = "";
                    try
                    {
                        dynamic dOp = op;
                        dynamic robot = dOp.Robot;
                        if (robot != null) robotName = SafeName(robot);
                    }
                    catch { }
                    // 也尝试用 FindRobotForOps
                    if (robotName.Length == 0)
                    {
                        try
                        {
                            TxRobot r = FindRobotForOps(op, null);
                            if (r != null) robotName = SafeName(r);
                        }
                        catch { }
                    }

                    if (robotName.Length > 0)
                    {
                        sb.AppendLine("• " + opName + " → " + robotName);
                        found++;
                    }
                    else if (string.IsNullOrWhiteSpace(keyword))
                    {
                        sb.AppendLine("• " + opName + " → (无机器人绑定)");
                        found++;
                    }
                }

                if (found == 0) sb.AppendLine("没有找到匹配的操作或绑定关系。");
                else sb.AppendLine().Append("共 " + found + " 条映射");
                return sb.ToString();
            });
        }

        // ───────── 新增：扫描设备 Z 向状态（只读）─────────

        public static string ScanDevicesZ(string[] extraIgnoreKeywords)
        {
            return ScanDevicesZ(new DeviceZOptions { IgnoreKeywords=extraIgnoreKeywords??new string[0] });
        }

        public static string ScanDevicesZ(DeviceZOptions options)
        {
            return PsContext.Current.Run<string>(() => DeviceZAlignService.Execute(options,false));
        }

        public static string AlignDevicesZ(DeviceZOptions options)
        {
            return PsContext.Current.Run<string>(() => DeviceZAlignService.Execute(options,true));
        }

        // ───────── 新增：设置对象位置（变更）─────────

        /// <summary>世界位置使用 mm，姿态使用 RPY_ZYX 度；未指定的姿态分量保留。</summary>
        public static string SetObjectLocation(string name, double x, double y, double z, double? rx, double? ry, double? rz, string objectId = null)
        {
            return PlcRead(() => {
                if (new[] { x, y, z, rx ?? 0, ry ?? 0, rz ?? 0 }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    return "Error: 位置和角度必须为有限数值。";
                ITxObject obj; string error;
                if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                var loc = obj as ITxLocatableObject;
                if (loc == null || !obj.CanBeModified || !loc.CanLocate()) return "Error: 对象不可定位或不可修改。";
                var original = new TxTransformation(loc.AbsoluteLocation);
                var target = new TxTransformation(original);
                if (rx.HasValue || ry.HasValue || rz.HasValue) {
                    if (!original.IsRigidBodyTransformation || original.IsMirrored)
                        return "Error: 对象包含缩放或镜像，不能用 RPY 覆盖姿态。";
                    var old = original.RotationRPY_ZYX;
                    const double radians = Math.PI / 180;
                    target.RotationRPY_ZYX = new TxVector(rx.HasValue ? rx.Value * radians : old.X,
                        ry.HasValue ? ry.Value * radians : old.Y, rz.HasValue ? rz.Value * radians : old.Z);
                }
                target.Translation = new TxVector(x, y, z);
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "设置对象位置")) {
                    loc.AbsoluteLocation = target;
                    var actual = loc.AbsoluteLocation;
                    bool verified = true;
                    for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                        if (Math.Abs(actual[i, j] - target[i, j]) > 1e-6) verified = false;
                    if (!verified) {
                        try { loc.AbsoluteLocation = original; }
                        catch (Exception ex) { return "Error: 位置写入核验失败，恢复原位也失败: " + ex.Message + "。请检查场景并撤销。"; }
                        return "Error: 位置写入核验失败，已恢复原位。";
                    }
                    TxApplication.RefreshDisplay();
                    var p = actual.Translation; var r = actual.RotationRPY_ZYX;
                    return Ref(obj) + " 位置已核验 (mm): " + p + "；RPY_ZYX (度): " +
                        (r.X * 180 / Math.PI).ToString("F4") + ", " + (r.Y * 180 / Math.PI).ToString("F4") + ", " + (r.Z * 180 / Math.PI).ToString("F4");
                }
            });
        }

        /// <summary>操作绑定成功后才控制播放器。reset 停止并回退到起点。</summary>
        public static string SimulateOperation(string operationName, string action, string objectId = null)
        {
            return PlcRead(() => {
                action = (action ?? "").ToLowerInvariant();
                if (!new[] { "play", "pause", "stop", "reset", "rewind" }.Contains(action)) return "Error: 不支持的仿真动作。";
                ITxObject obj; string error;
                if (!TryResolve(operationName, objectId, out obj, out error)) return "Error: " + error;
                var operation = obj as ITxOperation;
                if (operation == null) return "Error: 目标不是操作。";
                var doc = TxApplication.ActiveDocument;
                var generation = TxTools.Common.SceneUndoScope.DocumentGeneration;
                bool reuse = _lastSimPlayer != null && _lastSimGeneration == generation && ReferenceEquals(_lastSimDocument, doc) && _lastSimOperationId == obj.Id;
                if (!reuse && (action == "pause" || action == "stop")) return "Error: 该操作没有由本工具启动的播放器。";
                if (!reuse) {
                    if (_lastSimPlayer != null && _lastSimGeneration == generation && ReferenceEquals(_lastSimDocument, doc) && _lastSimPlayer.IsSimulationRunning())
                        return "Error: 另一个操作的仿真仍在运行，请先停止它。";
                    var player = new TxSimulationPlayer();
                    player.SetOperation(operation);
                    _lastSimPlayer = player;
                    _lastSimDocument = doc;
                    _lastSimGeneration = generation;
                    _lastSimOperationId = obj.Id;
                }
                switch (action) {
                    case "play": _lastSimPlayer.Rewind(); _lastSimPlayer.Play(); break;
                    case "pause": _lastSimPlayer.Pause(); break;
                    case "stop": _lastSimPlayer.Stop(); break;
                    case "reset": _lastSimPlayer.Stop(); _lastSimPlayer.Rewind(); break;
                    case "rewind": _lastSimPlayer.Rewind(); break;
                }
                return "仿真动作已执行: " + action + "，操作: " + Ref(obj);
            });
        }

        private static TxTools.Common.SceneUndoScope BeginUndo(TxDocument doc, string description)
        {
            return TxTools.Common.SceneUndoScope.Begin(doc, description);
        }

        private static void EndUndo(TxTools.Common.SceneUndoScope undo)
        {
            if (undo != null) undo.Dispose();
        }

        // ───────── 内部：遍历辅助 (均在 PsContext.Run 内被调用，不再二次路由) ─────────

        private static List<object> SceneRoots()
        {
            var roots = new List<object>();
            try
            {
                dynamic dDoc = TxApplication.ActiveDocument;
                if (dDoc == null) return roots;
                foreach (var rn in new[] { "PhysicalRoot", "ComponentRoot", "ResourceRoot" })
                {
                    object root = null;
                    try
                    {
                        switch (rn)
                        {
                            case "PhysicalRoot": root = dDoc.PhysicalRoot; break;
                            case "ComponentRoot": root = dDoc.ComponentRoot; break;
                            case "ResourceRoot": root = dDoc.ResourceRoot; break;
                        }
                    }
                    catch { }
                    if (root != null) roots.Add(root);
                }
            }
            catch { }
            return roots;
        }

        private static TxObjectList DescendantsOf(object node, bool recursive)
        {
            var f = new TxTypeFilter(typeof(ITxObject));
            try
            {
                dynamic d = node;
                var r = (recursive ? d.GetAllDescendants(f) : d.GetDirectDescendants(f)) as TxObjectList;
                if (r != null) return r;
            }
            catch { }
            try
            {
                dynamic d = node;
                var r = (recursive ? d.GetAllDescendants() : d.GetDirectDescendants()) as TxObjectList;
                if (r != null) return r;
            }
            catch { }
            return null;
        }

        private static List<ITxObject> CollectScene(bool recursive)
        {
            var list = new List<ITxObject>();
            var seen = new HashSet<int>();
            foreach (var root in SceneRoots())
            {
                var kids = DescendantsOf(root, recursive);
                if (kids == null) continue;
                foreach (ITxObject o in kids)
                {
                    if (o == null) continue;
                    if (seen.Add(RuntimeHelpers.GetHashCode(o))) list.Add(o);
                }
            }
            return list;
        }

        private static Dictionary<string, int> Histogram(List<ITxObject> objs)
        {
            var hist = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var o in objs)
            {
                var tn = o.GetType().Name;
                int c; hist[tn] = hist.TryGetValue(tn, out c) ? c + 1 : 1;
            }
            return hist;
        }

        // ═════════════════════════════════════════════════════════════════
        //  对象定位:名称 + 可选 ID
        //
        //  场景允许多个机器人使用相同名称，定位时必须检查歧义。
        //  旧版 FindByName 精确命中就返回第一个、没精确命中就返回第一个模糊包含的,
        //  于是"操作错对象"不报错不提示,只在用户发现结果不对时才暴露。
        //
        //  ITxObject.Id 是场景内唯一标识(形如 3,57,2,1):
        //    首段=域号 · 中间段=家族号(继承自父设备) · 末段=容器内递增实例号
        //  改名、UI 拖拽改层级都不改变 Id;但 SDK AddObject 是"复制"语义,会生成新 Id 的新实例。
        //  Id 只在单个项目内有效,不要跨项目固化。
        // ═════════════════════════════════════════════════════════════════

        /// <summary>对象的标准短表示:名称 [Id]。凡是列出对象的地方都该用它。</summary>
        internal static string Ref(ITxObject o)
        {
            if (o == null) return "(null)";
            string id = "?";
            try { id = o.Id; } catch { }
            return SafeName(o) + " [" + id + "]";
        }

        /// <summary>候选行:名称[Id] + 类型 + 位置 + 父级,给模型足够信息挑出想要的那个。</summary>
        internal static string Describe(ITxObject o)
        {
            var sb = new StringBuilder();
            sb.Append(Ref(o));
            try { sb.Append("  类型=").Append(o.GetType().Name); } catch { }
            try
            {
                var loc = o as ITxLocatableObject;
                if (loc != null)
                {
                    var t = loc.AbsoluteLocation.Translation;
                    sb.Append("  位置=(").Append(((double)t.X).ToString("F1"))
                      .Append(", ").Append(((double)t.Y).ToString("F1"))
                      .Append(", ").Append(((double)t.Z).ToString("F1")).Append(")");
                }
            }
            catch { }
            var pn = ParentName(o);
            if (!string.IsNullOrEmpty(pn)) sb.Append("  父级=").Append(pn);
            return sb.ToString();
        }

        /// <summary>
        /// 统一定位入口。成功返回 true;失败时 error 是可直接回灌给模型的说明,
        /// 歧义时自带候选表,模型看到就会改用 object_id 重试。
        ///
        /// 优先级:objectId(精确) > name(精确匹配) > name(模糊包含) > 当前选中
        /// </summary>
        internal static bool TryResolve(string name, string objectId, out ITxObject obj, out string error)
        {
            obj = null;
            error = null;

            // ── 1) 有 ID 走精确路径 ──
            if (!string.IsNullOrWhiteSpace(objectId))
            {
                try
                {
                    var doc0 = TxApplication.ActiveDocument;
                    if (doc0 != null) obj = doc0.GetObjectById(objectId.Trim());
                }
                catch { }

                if (obj != null) return true;

                error = "按 ID \"" + objectId + "\" 找不到对象。Id 只在当前项目内有效，"
                      + "且对象被 SDK 复制/重建后会换新 Id。请重新查询以获取当前 Id。";
                return false;
            }

            // ── 2) 无名无 ID → 当前选中 ──
            if (string.IsNullOrWhiteSpace(name))
            {
                var selected = SelectedObjects();
                if (selected.Count == 0)
                {
                    error = "未提供 name/object_id，且当前没有选中任何对象。";
                    return false;
                }
                if (selected.Count > 1)
                {
                    error = "当前选中了 " + selected.Count + " 个对象，无法确定操作哪一个。\n"
                          + Candidates(selected)
                          + "请用 object_id 指定其中一个后重试。";
                    return false;
                }
                obj = selected[0];
                return true;
            }

            // ── 3) 按名字找:精确优先,精确无果再模糊 ──
            var exact = new List<ITxObject>();
            var fuzzy = new List<ITxObject>();

            foreach (var o in CollectScene(true))
            {
                var n = SafeName(o);
                if (n == null) continue;
                if (string.Equals(n, name, StringComparison.Ordinal)) exact.Add(o);
                else if (n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) fuzzy.Add(o);
            }

            var hits = exact.Count > 0 ? exact : fuzzy;
            bool wasFuzzy = exact.Count == 0;

            if (hits.Count == 1) { obj = hits[0]; return true; }

            if (hits.Count == 0)
            {
                error = "找不到名为 \"" + name + "\" 的对象。";
                return false;
            }

            // 命中多个 —— 绝不静默取第一个
            error = (wasFuzzy
                        ? "没有名称完全等于 \"" + name + "\" 的对象，但有 " + hits.Count + " 个名称包含它"
                        : "名称 \"" + name + "\" 在场景中命中 " + hits.Count + " 个对象")
                  + "，无法确定操作哪一个。\n"
                  + Candidates(hits)
                  + "请改用 object_id 指定具体那一个后重试。";
            return false;
        }

        private static string Candidates(List<ITxObject> list)
        {
            var sb = new StringBuilder();
            int max = Math.Min(list.Count, 20);
            for (int i = 0; i < max; i++)
                sb.Append("  ").Append(i + 1).Append(". ").AppendLine(Describe(list[i]));
            if (list.Count > max)
                sb.AppendLine("  …(还有 " + (list.Count - max) + " 个，用更精确的名称缩小范围)");
            return sb.ToString();
        }

        private static List<ITxObject> SelectedObjects()
        {
            var result = new List<ITxObject>();
            try
            {
                dynamic sel = TxApplication.ActiveSelection;
                dynamic items = sel.GetItems();
                var en = items as IEnumerable;
                if (en != null)
                    foreach (var o in en)
                    {
                        var t = o as ITxObject;
                        if (t != null) result.Add(t);
                    }
            }
            catch { }
            return result;
        }


        private static ITxObject FirstSelected()
        {
            try
            {
                dynamic sel = TxApplication.ActiveSelection;
                dynamic items = sel.GetItems();
                var en = items as IEnumerable;
                if (en != null) foreach (var o in en) return o as ITxObject;
            }
            catch { }
            return null;
        }

        private static PointType ParsePointType(string s)
        {
            if (string.Equals(s, "WeldPoint", StringComparison.OrdinalIgnoreCase)) return PointType.WeldPoint;
            if (string.Equals(s, "PathPoint", StringComparison.OrdinalIgnoreCase)) return PointType.PathPoint;
            if (string.Equals(s, "ContinuousPoint", StringComparison.OrdinalIgnoreCase)) return PointType.ContinuousPoint;
            return PointType.All;
        }

        private static string ParentName(object o)
        {
            try { dynamic d = o; dynamic p = d.Collection; if (p != null) return (string)p.Name ?? ""; } catch { }
            return "";
        }

        private static string SanitizeTag(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            var r = sb.ToString().Trim('_');
            return r.Length == 0 ? "objects" : r;
        }

        private static string SafeName(object o)
        {
            try
            {
                dynamic d = o;
                string n = (string)d.Name;
                return string.IsNullOrEmpty(n) ? o.ToString() : n;
            }
            catch { return o == null ? "<null>" : o.ToString(); }
        }

        // ───────── 可复用私有辅助方法 ─────────

        /// <summary>字符串 → BrandMode 枚举转换。可复用工具。</summary>
        private static BrandMode ParseBrandMode(string s)
        {
            if (string.Equals(s, "Fanuc", StringComparison.OrdinalIgnoreCase)) return BrandMode.Fanuc;
            if (string.Equals(s, "Generic", StringComparison.OrdinalIgnoreCase)) return BrandMode.Generic;
            return BrandMode.Auto;
        }

        private static double ExtractZFromTx(TxTransformation tx)
        {
            try { dynamic d = tx; return Convert.ToDouble(d[2, 3]); } catch { }
            try { dynamic d = tx; return Convert.ToDouble(d.Translation.Z); } catch { }
            try { dynamic d = tx; return Convert.ToDouble(d.Z); } catch { }
            try { dynamic d = tx; var v = d.TranslationVector; return Convert.ToDouble(v.Z); } catch { }
            return 0;
        }

        private static double GetDeviceMinZScan(ITxObject obj, TxTransformation absTx, out string method)
        {
            method = "原点";
            try
            {
                if (obj is TxComponent comp)
                {
                    dynamic dComp = comp;
                    try { dynamic pts = dComp.GetLocationAxisIntersectionPoints(2); double m = ExtractMinZFromPtsScan(pts); if (m < double.MaxValue) { method = "轴交点"; return m; } } catch { }
                    try { dynamic pts = dComp.GetLocationAxisIntersectionPoints(); double m = ExtractMinZFromPtsScan(pts); if (m < double.MaxValue) { method = "轴交点"; return m; } } catch { }
                }
            }
            catch { }
            return ExtractZFromTx(absTx);
        }

        private static double ExtractMinZFromPtsScan(object points)
        {
            double minZ = double.MaxValue;
            if (points == null) return minZ;
            try
            {
                var en = points as IEnumerable;
                if (en != null) { foreach (object pt in en) { double z = ExtractZFromPtScan(pt); if (z < minZ) minZ = z; } return minZ; }
            }
            catch { }
            double s = ExtractZFromPtScan(points);
            if (s < minZ) minZ = s;
            return minZ;
        }

        private static double ExtractZFromPtScan(object pt)
        {
            if (pt == null) return double.MaxValue;
            try { dynamic d = pt; return Convert.ToDouble(d.Z); } catch { }
            try { dynamic d = pt; return Convert.ToDouble(d[2, 3]); } catch { }
            try { dynamic d = pt; return Convert.ToDouble(d.Translation.Z); } catch { }
            return double.MaxValue;
        }

        // ───────── 位置辅助（复用 DeviceZAlignService 同款多策略） ─────────

        private static TxTransformation GetAbsoluteLocation(ITxObject obj)
        {
            try { if (obj is ITxLocatableObject loc) { var tx = loc.AbsoluteLocation; if (tx != null) return tx; } } catch { }
            try { dynamic d = obj; var tx = d.AbsoluteLocation as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.Location as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.AbsoluteFrame as TxTransformation; if (tx != null) return tx; } catch { }
            try { dynamic d = obj; var tx = d.LocationInWorld as TxTransformation; if (tx != null) return tx; } catch { }
            return null;
        }

        private static object InvokeOrGet(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var pi = t.GetProperty(name);
            if (pi != null) return pi.GetValue(obj, null);
            var mi = t.GetMethod(name, Type.EmptyTypes);
            if (mi != null) return mi.Invoke(obj, null);
            return null;
        }

        // ───────── CEE 内部逻辑控制（Logic Block / SCL / Modules Viewer）─────────
        //
        // 区分 External PLC 连接 vs CEE 内部逻辑：
        //   External PLC: 信号有 I/O 地址(I1.0/Q1.0), 通过 OPC/ExternalConnection 与外部 PLC 通信
        //   CEE 内部逻辑: 信号无地址需求, 连接 LB Entry/Exit, 由 Modules Viewer 层级调度
        //
        // API 路径:
        //   TxDocument.PlcProgramRoot.CurrentPlcProgram → TxPlcProgram (ITxPlcSignalCreation)
        //   资源 → ITxPlcLogicBehaviorCreation.CreateLogicBehavior() → Smart Component
        //   资源 → ITxPlcSclCreation.CreateSclContainer() → SCL 文本编辑

        // CEE / PLC 使用当前 SDK 的强类型接口；不以反射失败代替“不存在”。
        private static TxPlcProgram GetCurrentPlcProgram()
        {
            var doc = TxApplication.ActiveDocument;
            return doc == null || doc.PlcProgramRoot == null ? null : doc.PlcProgramRoot.CurrentPlcProgram;
        }

        private static IEnumerable<ITxPlcSignal> PlcSignals(TxPlcProgram program)
        {
            return program.GetSignals(new TxTypeFilter(typeof(ITxPlcSignal))).Cast<ITxPlcSignal>();
        }

        private static string PlcRead(Func<string> read)
        {
            return PsContext.Current.Run<string>(() => {
                try { return read(); }
                catch (Exception ex) { return "Error: " + ex.GetType().Name + ": " + ex.Message; }
            });
        }

        public static string GetResourceLogicStatus(string name, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                var result = new StringBuilder("=== 资源 CEE 逻辑状态 ===\n");
                result.AppendLine(Ref(obj) + " [" + obj.GetType().Name + "]");
                var aspect = obj as ITxPlcAspectObject;
                result.AppendLine("PLC 层面: " + (aspect == null ? "不支持" : aspect.HasPlcAspect ? "已加载" : "未加载"));
                var resource = obj as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                var lb = resource == null ? null : resource.LogicBehavior;
                result.AppendLine("逻辑行为: " + (resource == null ? "不支持" : lb == null ? "未创建" : Ref(lb)));
                if (lb != null) result.AppendLine("Entry:" + lb.Entries.Length + " Exit:" + lb.Exits.Length +
                    " Action:" + lb.Actions.Length + " Parameter:" + lb.Parameters.Length + " Constant:" + lb.Constants.Length);
                var scl = obj as Tecnomatix.Engineering.Plc.ITxPlcSclResource;
                var container = scl == null ? null : scl.SclContainer;
                result.AppendLine("SCL 容器: " + (scl == null ? "不支持" : container == null ? "未创建" : Ref(container)));
                var program = GetCurrentPlcProgram();
                if (program == null) result.AppendLine("关联信号: 当前无 PLC 程序");
                else {
                    var signals = PlcSignals(program).Where(s => s.PlcResource != null && s.PlcResource.Id == obj.Id).ToList();
                    result.AppendLine("关联信号: " + signals.Count);
                    foreach (var signal in signals) result.AppendLine("  " + Ref(signal));
                }
                return result.ToString().TrimEnd();
            });
        }

        public static string ListPlcSignals(string nameFilter)
        {
            return PlcRead(() => {
                var program = GetCurrentPlcProgram();
                if (program == null) return "Error: 当前文档没有定义 PLC 程序。";
                var signals = PlcSignals(program).Where(s => string.IsNullOrWhiteSpace(nameFilter) ||
                    s.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                var result = new StringBuilder("=== 信号列表（共 " + signals.Count + " 个）===\n");
                foreach (var signal in signals) result.AppendLine(Ref(signal) + " [" + signal.GetType().Name +
                    "] 类型:" + signal.DataType + " 地址:" + signal.IECFormatAddress + " 注释:" + signal.Comment);
                return result.ToString().TrimEnd();
            });
        }

        public static string ListCeeModules()
        {
            return PlcRead(() => {
                var program = GetCurrentPlcProgram();
                if (program == null) return "Error: 当前文档没有定义 PLC 程序。";
                var result = new StringBuilder();
                foreach (var module in program.PlcModules) {
                    result.AppendLine("模块: " + Ref(module));
                    foreach (var entry in module.PlcEntries) result.AppendLine("  [" + entry.GetType().Name + "] ID:" + entry.UniqueId);
                }
                return result.Length == 0 ? "暂无 CEE 模块。" : result.ToString().TrimEnd();
            });
        }

        // SDK 地址采用字节/位对象。允许 IEC 输入，明确校验方向、宽度及位地址。
        private static TxPlcSignalAddress ParsePlcAddress(string text, string direction, TxPlcSignalDataType type)
        {
            if (string.IsNullOrWhiteSpace(text)) return new TxPlcSignalAddress();
            var m = System.Text.RegularExpressions.Regex.Match(text.Trim(), @"^%?([IQ])([BWD]?)(\d+)(?:\.(\d+))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) throw new ArgumentException("address 应为 IEC 地址，如 I0.0、Q0.0、IW2 或 QD4。");
            if (!string.Equals(m.Groups[1].Value, direction == "input" ? "I" : "Q", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("address 方向与 signal_type 不一致。");
            int b, bit;
            if (!int.TryParse(m.Groups[3].Value, out b)) throw new ArgumentException("地址字节超出范围。");
            var width = m.Groups[2].Value.ToUpperInvariant();
            if (type == TxPlcSignalDataType.Bool) {
                if (width != "" || !m.Groups[4].Success || !int.TryParse(m.Groups[4].Value, out bit) || bit > 7)
                    throw new ArgumentException("BOOL 必须使用 0..7 的位地址，如 Q0.0。");
                return new TxPlcSignalAddress(b, bit);
            }
            var expected = type == TxPlcSignalDataType.Byte || type == TxPlcSignalDataType.SInt ? "B" :
                type == TxPlcSignalDataType.Word || type == TxPlcSignalDataType.Int ? "W" :
                type == TxPlcSignalDataType.LReal ? null : "D";
            if (m.Groups[4].Success || expected == null || width != expected)
                throw new ArgumentException("该数据类型的地址宽度不匹配或不支持；可留空创建内部信号。");
            return new TxPlcSignalAddress(b);
        }

        public static string CreatePlcSignal(string signalType, string name, string address, string dataType, string comment)
        {
            return PlcRead(() => {
                var program = GetCurrentPlcProgram();
                if (program == null) return "Error: 当前文档没有定义 PLC 程序。";
                if (string.IsNullOrWhiteSpace(name)) return "Error: 信号名称不能为空。";
                signalType = (signalType ?? "").ToLowerInvariant();
                TxPlcSignalDataType type;
                if (!Enum.TryParse(dataType ?? "BOOL", true, out type) || !Enum.IsDefined(typeof(TxPlcSignalDataType), type))
                    return "Error: 不支持的数据类型: " + dataType;
                if (PlcSignals(program).Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return "Error: 已存在同名信号。";
                TxPlcSignalCreationData data;
                switch (signalType) {
                    case "input": data = new TxPlcInputSignalCreationData(name); break;
                    case "output": data = new TxPlcOutputSignalCreationData(name); break;
                    case "display": data = new TxPlcDisplaySignalCreationData(name); break;
                    default: return "Error: signal_type 只支持 input、output、display。";
                }
                if (signalType == "display" && !string.IsNullOrWhiteSpace(address)) return "Error: display 信号不接受外部地址。";
                data.DataType = type;
                if (!string.IsNullOrWhiteSpace(address)) data.Address = ParsePlcAddress(address, signalType, type);
                data.Comment = comment ?? "";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "创建 PLC 信号")) {
                    ITxPlcSignal signal;
                    if (signalType == "input") signal = program.CreatePlcInputSignal((TxPlcInputSignalCreationData)data);
                    else if (signalType == "output") signal = program.CreatePlcOutputSignal((TxPlcOutputSignalCreationData)data);
                    else signal = program.CreatePlcDisplaySignal((TxPlcDisplaySignalCreationData)data);
                    if (signal == null) return "Error: SDK 创建信号返回 null。";
                    return "已创建信号: " + Ref(signal) + " 类型:" + signal.DataType + " 地址:" + signal.IECFormatAddress;
                }
            });
        }

        public static string AddLogicToResource(string name, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                var creator = obj as Tecnomatix.Engineering.Plc.ITxPlcLogicBehaviorCreation;
                if (creator == null) return "Error: 资源不支持逻辑行为创建。";
                var resource = obj as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                if (resource != null && resource.LogicBehavior != null) return "Error: 资源已经有 LogicBehavior。";
                var data = new Tecnomatix.Engineering.Plc.TxPlcLogicBehaviorCreationData("LogicBehavior");
                if (!creator.CanCreateLogicBehavior(data)) return "Error: 当前资源状态不允许创建逻辑行为。";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "添加逻辑行为")) {
                    var lb = creator.CreateLogicBehavior(data);
                    return lb == null ? "Error: SDK 创建逻辑行为返回 null。" : "已创建逻辑行为: " + Ref(lb) + "，资源: " + Ref(obj);
                }
            });
        }

        public static string CreateSclContainer(string name, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(name, objectId, out obj, out error)) return "Error: " + error;
                var creator = obj as Tecnomatix.Engineering.Plc.ITxPlcSclCreation;
                if (creator == null) return "Error: 资源不支持 SCL 创建。";
                var resource = obj as Tecnomatix.Engineering.Plc.ITxPlcSclResource;
                if (resource != null && resource.SclContainer != null) return "Error: 资源已经有 SCL 容器。";
                var data = new TxPlcSclContainerCreationData("SCL");
                if (!creator.CanCreateSclContainer(data)) return "Error: 当前资源状态不允许创建 SCL 容器。";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "创建 SCL 容器")) {
                    var scl = creator.CreateSclContainer(data);
                    return scl == null ? "Error: SDK 创建 SCL 容器返回 null。" : "已创建 SCL 容器: " + Ref(scl) + "，资源: " + Ref(obj);
                }
            });
        }

        public static string CopyLogic(string sourceName, string targetName, string sourceId = null, string targetId = null)
        {
            return PlcRead(() => {
                ITxObject src, target; string error;
                if (string.IsNullOrWhiteSpace(sourceName) && string.IsNullOrWhiteSpace(sourceId) ||
                    string.IsNullOrWhiteSpace(targetName) && string.IsNullOrWhiteSpace(targetId)) return "Error: 必须明确指定源与目标资源。";
                if (!TryResolve(sourceName, sourceId, out src, out error)) return "Error: 源资源: " + error;
                if (!TryResolve(targetName, targetId, out target, out error)) return "Error: 目标资源: " + error;
                if (src.Id == target.Id) return "Error: 源与目标资源不能相同。";
                var from = src as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                var to = target as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                if (from == null || to == null || from.LogicBehavior == null) return "Error: 源或目标不支持逻辑资源，或源没有逻辑行为。";
                if (to.LogicBehavior != null) return "Error: 目标已有逻辑行为，不能覆盖。";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "复制逻辑行为")) {
                    from.CopySelfLogicToOtherLogicResource(to);
                    return to.LogicBehavior == null ? "Error: 复制后未找到目标逻辑行为。" : "逻辑行为已复制到 " + Ref(target);
                }
            });
        }

        public static string CreateCeeModule(string name)
        {
            return PlcRead(() => {
                var program = GetCurrentPlcProgram();
                if (program == null) return "Error: 当前文档没有定义 PLC 程序。";
                if (string.IsNullOrWhiteSpace(name)) return "Error: 模块名称不能为空。";
                if (program.PlcModules.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) return "Error: 已存在同名模块。";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "创建 CEE 模块")) {
                    var module = program.CreateModule(new TxPlcModuleCreationData(name));
                    return module == null ? "Error: SDK 创建模块返回 null。" : "已创建 CEE 模块: " + Ref(module);
                }
            });
        }

        public static string CreatePlcSensor(string resourceName, string sensorType, string sensorName, string objectId = null)
        {
            return PlcRead(() => {
                if (!string.Equals(sensorType ?? "light", "light", StringComparison.OrdinalIgnoreCase)) return "Error: 目前只支持 light 光传感器。";
                ITxObject obj; string error;
                if (!TryResolve(resourceName, objectId, out obj, out error)) return "Error: " + error;
                var creator = obj as ITxPlcSensorCreation;
                if (creator == null) return "Error: 资源不支持传感器创建。";
                if (string.IsNullOrWhiteSpace(sensorName)) sensorName = "Sensor_" + obj.Name;
                TxPlcLightSensorCreationData data = new TxTcPlcLightSensorCreationData(sensorName);
                data.MaxRange = 500; data.CurrentRange = 300;
                if (!creator.CanCreatePlcLightSensor(data)) {
                    data = new TxEmsPlcLightSensorCreationData(sensorName);
                    data.MaxRange = 500; data.CurrentRange = 300;
                    if (!creator.CanCreatePlcLightSensor(data)) return "Error: 当前资源或规划环境不允许创建光传感器。";
                }
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "创建光传感器")) {
                    var sensor = creator.CreatePlcLightSensor(data);
                    return sensor == null ? "Error: SDK 创建传感器返回 null。" : "已创建光传感器: " + Ref(sensor) + "，资源: " + Ref(obj);
                }
            });
        }

        public static string ListLogicBehaviorElements(string resourceName, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(resourceName, objectId, out obj, out error)) return "Error: " + error;
                var resource = obj as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                var lb = resource == null ? null : resource.LogicBehavior;
                if (lb == null) return "Error: 资源没有 LogicBehavior。";
                var groups = new[] { lb.Entries.Cast<ITxObject>(), lb.Exits.Cast<ITxObject>(), lb.Actions.Cast<ITxObject>(), lb.Parameters.Cast<ITxObject>(), lb.Constants.Cast<ITxObject>() };
                var result = new StringBuilder("=== LogicBehavior 元素: " + Ref(obj) + " ===\n");
                foreach (var group in groups) foreach (var element in group) {
                    result.AppendLine(Ref(element) + " [" + element.GetType().Name + "]");
                    var entry = element as Tecnomatix.Engineering.Plc.ITxPlcLogicBehaviorEntry;
                    var exit = element as Tecnomatix.Engineering.Plc.ITxPlcLogicBehaviorExit;
                    var signals = entry != null ? entry.ConnectedSignals : exit != null ? exit.ConnectedSignals : null;
                    if (signals != null) foreach (var signal in signals) result.AppendLine("  → " + Ref(signal));
                }
                return result.ToString().TrimEnd();
            });
        }

        public static string ConnectSignalToLB(string resourceName, string signalName, string pinType, string pinName, string objectId = null)
        {
            return PlcRead(() => {
                ITxObject obj; string error;
                if (!TryResolve(resourceName, objectId, out obj, out error)) return "Error: " + error;
                var resource = obj as Tecnomatix.Engineering.Plc.ITxPlcLogicResource;
                var lb = resource == null ? null : resource.LogicBehavior;
                if (lb == null) return "Error: 资源没有 LogicBehavior。";
                var program = GetCurrentPlcProgram();
                if (program == null) return "Error: 当前没有 PLC 程序。";
                var matches = PlcSignals(program).Where(s => string.Equals(s.Name, signalName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1) return "Error: 信号必须精确且唯一匹配，当前命中 " + matches.Count + " 个。";
                var signal = matches[0] as TxPlcSignalBase;
                if (signal == null) return "Error: 该信号不支持 LB 连接。";
                pinType = (pinType ?? "").ToLowerInvariant();
                if (pinType != "entry" && pinType != "exit") return "Error: pin_type 只支持 entry 或 exit。";
                var pins = (pinType == "entry" ? lb.Entries.Cast<ITxObject>() : lb.Exits.Cast<ITxObject>())
                    .Where(p => string.IsNullOrWhiteSpace(pinName) || string.Equals(p.Name, pinName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pins.Count != 1) return "Error: 引脚必须精确且唯一匹配，当前命中 " + pins.Count + " 个。";
                var entry = pins[0] as Tecnomatix.Engineering.Plc.ITxPlcLogicBehaviorEntry;
                var exit = pins[0] as Tecnomatix.Engineering.Plc.ITxPlcLogicBehaviorExit;
                Func<TxPlcSignalBase[]> connected = () => entry != null ? entry.ConnectedSignals : exit.ConnectedSignals;
                if (connected().Any(s => s.Id == signal.Id)) return "信号已经连接到该引脚。";
                using (var undo = TxTools.Common.SceneUndoScope.Begin(TxApplication.ActiveDocument, "连接 LB 信号")) {
                    if (entry != null) entry.ConnectSignal(signal); else exit.ConnectSignal(signal);
                    return connected().Any(s => s.Id == signal.Id) ? "信号 " + Ref(signal) + " 已连接到 " + Ref(pins[0]) : "Error: SDK 连线后核验失败。";
                }
            });
        }
    }
}
