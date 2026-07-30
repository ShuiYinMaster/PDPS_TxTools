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
        private static dynamic _lastSimPlayer;

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
                catch (Exception ex) { return "读取选中对象失败: " + ex.Message; }
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
                catch (Exception ex) { return "读取文档信息失败: " + ex.Message; }
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
                catch (Exception ex) { return "统计场景对象失败: " + ex.Message; }
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
                catch (Exception ex) { return "列出子对象失败: " + ex.Message; }
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
                catch (Exception ex) { return "读取操作失败: " + ex.Message; }
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
                catch (Exception ex) { return "统计点失败: " + ex.Message; }
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
                catch (Exception ex) { return "读取 TCP 选项失败: " + ex.Message; }
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
                catch (Exception ex) { return "读取参考坐标系失败: " + ex.Message; }
            });
        }

        // ───────── 动作：选中 / 真实焊点导出 ─────────

        /// <summary>
        /// 快速可达性摘要：对选中操作的各点位用 robot.GetPoseAtLocation 判定可达(只读，不驱动机器人)。
        /// 不含 RobotReachabilityChecker 的关节余量/奇异/碰撞等完整分析。
        /// </summary>
        /// <summary>
        /// 快速可达性摘要。operationName 非空时在操作树(OperationRoot)里按名查找该操作并检查其下机器人点位；
        /// 为空时回退到当前选中的操作。逐点用 GetPoseAtLocation 判定(只读、不驱动机器人)。
        /// </summary>
        public static string CheckReachability(string operationName)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    var roots = new List<ITxObject>();
                    if (!string.IsNullOrWhiteSpace(operationName))
                    {
                        var key = operationName.Trim();
                        foreach (var o in CollectOperations())
                        {
                            var n = SafeName(o);
                            if (n != null && n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) roots.Add(o);
                        }
                        if (roots.Count == 0) return "在操作树里未找到名称含 \"" + key + "\" 的操作。";
                    }
                    else
                    {
                        var sel = PsReader.GetOperationsFromSelection(Nolog);
                        if (sel == null || sel.Count == 0)
                            return "未提供操作名，且当前没有选中操作。请给出 operation 名(如 OP120)，或先在操作树里选中操作。";
                        foreach (var op in sel) if (op.PsObject != null) roots.Add(op.PsObject);
                    }

                    // 去重并尽量只保留"最外层"匹配(避免父子重复统计)：简单去重即可。
                    var seen = new HashSet<int>();
                    var sb = new StringBuilder();
                    int gReach = 0, gTotal = 0, checkedOps = 0;
                    foreach (var root in roots)
                    {
                        if (!seen.Add(RuntimeHelpers.GetHashCode(root))) continue;
                        var locs = EnumerateLocationOps(root);
                        if (locs.Count == 0) continue;

                        var robot = FindRobotForOps(root, locs);
                        var rootName = SafeName(root);
                        checkedOps++;
                        if (robot == null) { sb.AppendLine("• " + rootName + ": " + locs.Count + " 点，未找到绑定机器人"); continue; }

                        int reach = 0;
                        var bad = new List<string>();
                        foreach (var loc in locs)
                        {
                            bool ok = false;
                            try { ok = robot.GetPoseAtLocation(loc) != null; } catch { ok = false; }
                            if (ok) reach++;
                            else if (bad.Count < 10) bad.Add(SafeName(loc));
                        }
                        gReach += reach; gTotal += locs.Count;
                        sb.Append("• ").Append(rootName).Append(" [机器人 ").Append(SafeName(robot)).Append("]: 可达 ")
                          .Append(reach).Append("/").Append(locs.Count);
                        if (bad.Count > 0) sb.Append("  不可达: ").Append(string.Join(", ", bad));
                        sb.AppendLine();
                    }

                    if (checkedOps == 0) return "匹配到的操作下没有机器人点位可检查(可能不是机器人操作)。";
                    sb.AppendLine("合计: 可达 " + gReach + "/" + gTotal + " 点");
                    sb.Append("(快速摘要：GetPoseAtLocation 判定，不含关节余量/奇异/碰撞分析)");
                    return sb.ToString();
                }
                catch (Exception ex) { return "可达性检查失败: " + ex.Message; }
            });
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
                        sb.AppendLine((i + 1) + ". " + SafeName(matches[i]) + " [" + matches[i].GetType().Name + "]");
                    if (matches.Count > cap) sb.AppendLine("…(其余 " + (matches.Count - cap) + " 个省略)");
                    return sb.ToString();
                }
                catch (Exception ex) { return "查找操作失败: " + ex.Message; }
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
                        catch (Exception ex) { return "设置选中失败: " + ex.Message; }
                    }

                    var msg = "已选中 " + found.Count + " 个对象: " + string.Join(", ", found.Take(20));
                    if (missing.Count > 0) msg += "；未找到: " + string.Join(", ", missing);
                    if (missingIds.Count > 0) msg += "；未找到的 ID: " + string.Join(", ", missingIds);
                    return msg;
                }
                catch (Exception ex) { return "选中对象失败: " + ex.Message; }
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
                catch (Exception ex) { return "导出对象清单失败: " + ex.Message; }
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
                    catch { }

                    var path = ExcelExporter.Export(ops, refMatrix, folder, Nolog);
                    return path != null
                        ? "已导出 " + total + " 个点到: " + path
                        : "导出失败(无数据或写入错误)。";
                }
                catch (Exception ex) { return "导出焊点 Excel 失败: " + ex.Message; }
            });
        }

        // ───────── 变更操作 (待接入) ─────────

        /// <summary>把当前选中设备最低点对齐到世界 Z=0 (变更，可 Ctrl+Z 撤销)。</summary>
        public static string AlignSelectedDevicesToFloor()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try { return DeviceZAlignService.AlignSelection(); }
                catch (Exception ex) { return "对齐失败: " + ex.Message; }
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
                catch (Exception ex) { return "探查对象失败: " + ex.Message; }
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
                var log = new StringBuilder();
                Action<string> logfn = delegate (string s) { if (s != null) log.AppendLine(s); };

                TxDocument doc = null;
                try { doc = TxApplication.ActiveDocument; } catch { }
                var label = string.IsNullOrWhiteSpace(undoLabel) ? "run_csharp" : undoLabel;
                bool undo = doc != null && BeginUndo(doc, label);

                string result;
                try { result = CSharpRunner.Invoke(assembly, logfn); ok = true; }
                catch (Exception ex) { result = "执行异常: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
                finally { if (undo) EndUndo(doc); }

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

        /// <summary>校验场景内所有机器人 BASE0 是否与期望一致。只读。</summary>
        public static string CheckRobotBase(double posTolMm, double rotTol, string brandMode)
        {
            return PsContext.Current.Run<string>(delegate
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return "没有打开的研究文档。";

                // 字符串 → BrandMode 枚举（同 assembly，internal 可直访）
                BrandMode mode;
                if (string.Equals(brandMode, "Fanuc", StringComparison.OrdinalIgnoreCase)) mode = BrandMode.Fanuc;
                else if (string.Equals(brandMode, "Generic", StringComparison.OrdinalIgnoreCase)) mode = BrandMode.Generic;
                else mode = BrandMode.Auto;

                try
                {
                    var results = RobotBaseReader.Analyze(posTolMm, rotTol, mode);
                    if (results == null || results.Count == 0) return "场景中没有找到机器人。";

                    var sb = new StringBuilder();
                    sb.AppendLine("机器人 BASE0 校验结果（容差: 位置 " + posTolMm + "mm / 旋转 " + rotTol + "）：");
                    sb.AppendLine();

                    int pass = 0, fail = 0;
                    foreach (var r in results)
                    {
                        var verdict = r.Verdict ?? "";
                        if (verdict.Contains("一致")) pass++; else fail++;
                        sb.AppendLine("• " + r.RobotName + "  品牌=" + (r.Brand ?? "?")
                            + "  ΔPos=" + (r.DeltaPos >= 0 ? r.DeltaPos.ToString("F3") : "—") + "mm"
                            + "  ΔRot=" + (r.DeltaRot >= 0 ? r.DeltaRot.ToString("F4") : "—")
                            + "  → " + verdict);
                    }
                    sb.AppendLine();
                    sb.Append("汇总: " + pass + " 一致, " + fail + " 存在偏差 / 共 " + results.Count + " 台机器人");
                    return sb.ToString();
                }
                catch (Exception ex) { return "校验异常: " + ex.Message; }
            });
        }

        // ───────── 新增：对象位置查询 ─────────

        /// <summary>查询对象的世界坐标系位置和姿态。只读。</summary>
        public static string GetObjectLocation(string name, string format, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                ITxObject obj; string err;
                if (!TryResolve(name, objectId, out obj, out err)) return "Error: " + err;

                var sb = new StringBuilder();
                sb.AppendLine("对象: " + Ref(obj));
                sb.AppendLine("类型: " + obj.GetType().Name);

                try
                {
                    TxTransformation tx = null;
                    try { if (obj is ITxLocatableObject loc) tx = loc.AbsoluteLocation; } catch { }
                    if (tx == null) try { dynamic d = obj; tx = d.AbsoluteLocation; } catch { }
                    if (tx == null) { sb.AppendLine("无 AbsoluteLocation 信息。"); return sb.ToString(); }

                    // XYZ 平移
                    double x = 0, y = 0, z = 0;
                    try { dynamic t = tx.Translation; x = (double)t.X; y = (double)t.Y; z = (double)t.Z; }
                    catch { try { x = (double)((dynamic)tx)[0, 3]; y = (double)((dynamic)tx)[1, 3]; z = (double)((dynamic)tx)[2, 3]; } catch { } }
                    sb.AppendLine("位置(mm): X=" + x.ToString("F3") + "  Y=" + y.ToString("F3") + "  Z=" + z.ToString("F3"));

                    // 姿态
                    if (string.Equals(format, "matrix", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            // SDK 不同版本旋转矩阵属性名不同，多策略反射
                            dynamic rm = InvokeOrGet(tx, "RotationMatrix") ?? InvokeOrGet(tx, "GetRotationMatrix");
                            sb.AppendLine("旋转矩阵:");
                            for (int i = 0; i < 3; i++)
                            {
                                sb.Append("  [");
                                for (int j = 0; j < 3; j++)
                                    sb.Append(((double)rm[i, j]).ToString("F6") + (j < 2 ? ", " : ""));
                                sb.AppendLine("]");
                            }
                        }
                        catch { sb.AppendLine("(旋转矩阵不可读)"); }
                    }
                    else
                    {
                        // rpy / euler — SDK 不同版本属性名不同，多策略反射
                        string label = string.Equals(format, "euler", StringComparison.OrdinalIgnoreCase) ? "Euler" : "RPY";
                        try
                        {
                            // 依次尝试 RotationRPY_ZYX / RotationRPY / GetRotationRPY（与 RobotBaseReader 同策略）
                            dynamic rp = InvokeOrGet(tx, "RotationRPY_ZYX")
                                         ?? InvokeOrGet(tx, "RotationRPY")
                                         ?? InvokeOrGet(tx, "GetRotationRPY");
                            sb.AppendLine("姿态(" + label + ",度): RX=" + ((double)rp.X).ToString("F4")
                                + "  RY=" + ((double)rp.Y).ToString("F4")
                                + "  RZ=" + ((double)rp.Z).ToString("F4"));
                        }
                        catch { sb.AppendLine("(" + label + " 角不可读)"); }
                    }
                    return sb.ToString();
                }
                catch (Exception ex) { return "读取位置异常: " + ex.Message; }
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
                catch (Exception ex) { return "查询运动学异常: " + ex.Message; }
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

        /// <summary>扫描场景中所有设备的 Z 向落地状态。只读，不做任何修改。</summary>
        public static string ScanDevicesZ(string[] extraIgnoreKeywords)
        {
            return PsContext.Current.Run<string>(delegate
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return "没有打开的研究文档。";

                var skipNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "gun", "robot", "tool", "gripper", "conveyor", "weldgun", "flange", "human" };
                var skipTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "TxWeldGun", "TxGun", "TxGripper", "TxTool", "TxRobot", "TxHumanModel", "TxConveyor", "TxSimulationPlayer" };
                if (extraIgnoreKeywords != null)
                    foreach (var k in extraIgnoreKeywords)
                    { skipNames.Add(k.Trim()); skipTypes.Add(k.Trim()); }

                var sb = new StringBuilder();
                sb.AppendLine("设备 Z 向状态扫描（只读）：");
                sb.AppendLine();

                int checkedCount = 0, needAlign = 0, alreadyOk = 0, skipped = 0;

                foreach (var obj in CollectScene(true))
                {
                    string typeName = obj.GetType().Name;
                    string objName = SafeName(obj);

                    // 跳过已知非落地类型
                    bool shouldSkip = false;
                    foreach (var st in skipTypes)
                        if (typeName.IndexOf(st, StringComparison.OrdinalIgnoreCase) >= 0) { shouldSkip = true; break; }
                    if (!shouldSkip)
                        foreach (var sn in skipNames)
                            if (objName.IndexOf(sn, StringComparison.OrdinalIgnoreCase) >= 0) { shouldSkip = true; break; }

                    // 也检查接口和父级（复刻 DeviceZAlignService.CheckShouldSkip 的策略）
                    if (!shouldSkip)
                    {
                        try
                        {
                            foreach (var iface in obj.GetType().GetInterfaces())
                                if (iface.Name.Contains("Gun") || iface.Name.Contains("Gripper")
                                    || iface.Name.Contains("Robot") || iface.Name.Contains("Tool"))
                                { shouldSkip = true; break; }
                        }
                        catch { }
                    }
                    if (!shouldSkip)
                    {
                        try
                        {
                            dynamic dobj = obj;
                            dynamic parent = dobj.Parent;
                            if (parent != null)
                            {
                                string pt = parent.GetType().Name;
                                if (pt.Contains("Robot") || pt.Contains("Flange")) shouldSkip = true;
                            }
                        }
                        catch { }
                    }

                    if (shouldSkip) { skipped++; continue; }

                    // 获取位置
                    TxTransformation absTx = null;
                    try { if (obj is ITxLocatableObject loc) absTx = loc.AbsoluteLocation; } catch { }
                    if (absTx == null) try { dynamic d = obj; absTx = d.AbsoluteLocation; } catch { }
                    if (absTx == null) continue;

                    // 读取 Z
                    double currentZ = 0;
                    try { dynamic t = absTx.Translation; currentZ = (double)t.Z; }
                    catch { try { currentZ = (double)((dynamic)absTx)[2, 3]; } catch { } }

                    // 尝试读 BoundingBox 获取最低 Z
                    double lowestZ = currentZ; // 回退：原点 Z
                    string zSource = "原点";
                    try
                    {
                        dynamic d = obj;
                        dynamic bbox = d.BoundingBox;
                        if (bbox != null)
                        {
                            try { lowestZ = (double)bbox.MinZ; zSource = "BoundingBox"; } catch { }
                        }
                    }
                    catch { }
                    // 尝试轴交点（同 DeviceZAlignService.GetDeviceMinZ）
                    try
                    {
                        if (obj is TxComponent comp)
                        {
                            dynamic dComp = comp;
                            object pts = null;
                            try { pts = dComp.GetLocationAxisIntersectionPoints(2); } catch { }
                            if (pts == null) try { pts = dComp.GetLocationAxisIntersectionPoints(); } catch { }
                            if (pts != null)
                            {
                                double minZPts = double.MaxValue;
                                var en = pts as IEnumerable;
                                if (en != null) foreach (object pt in en)
                                {
                                    try { dynamic dp = pt; double pz = Convert.ToDouble(dp.Z); if (pz < minZPts) minZPts = pz; } catch { }
                                    try { dynamic dp = pt; double pz = Convert.ToDouble(dp[2, 3]); if (pz < minZPts) minZPts = pz; } catch { }
                                }
                                if (minZPts < double.MaxValue) { lowestZ = minZPts; zSource = "轴交点"; }
                            }
                        }
                    }
                    catch { }

                    checkedCount++;
                    if (Math.Abs(lowestZ) < 1.0) // 1mm 容差认为已落地
                    {
                        alreadyOk++;
                        sb.AppendLine("  ✓ " + objName + " (" + typeName + ")  Z=" + currentZ.ToString("F3")
                            + "  最低Z=" + lowestZ.ToString("F3") + "[" + zSource + "]  已落地");
                    }
                    else
                    {
                        needAlign++;
                        sb.AppendLine("  ✗ " + objName + " (" + typeName + ")  Z=" + currentZ.ToString("F3")
                            + "  最低Z=" + lowestZ.ToString("F3") + "[" + zSource + "]"
                            + "  需偏移 " + (-lowestZ).ToString("F3") + "mm");
                    }
                }

                sb.AppendLine();
                sb.Append("汇总: 检查 " + checkedCount + " 台, " + alreadyOk + " 已落地, "
                    + needAlign + " 需对齐, " + skipped + " 被跳过");
                return sb.ToString();
            });
        }

        // ───────── 新增：设置对象位置（变更）─────────

        /// <summary>设置对象的世界坐标系位置（含可选姿态）。变更操作，包在 Undo 块里可撤销。</summary>
        public static string SetObjectLocation(string name, double x, double y, double z, double? rx, double? ry, double? rz, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                ITxObject obj; string err;
                if (!TryResolve(name, objectId, out obj, out err)) return "Error: " + err;

                var doc = TxApplication.ActiveDocument;
                if (doc == null) return "没有打开的研究文档。";

                bool undo = BeginUndo(doc, "set_object_location(" + name + ")");
                try
                {
                    // 获取当前变换
                    TxTransformation curTx = null;
                    try { if (obj is ITxLocatableObject loc) curTx = loc.AbsoluteLocation; } catch { }
                    if (curTx == null) try { dynamic d = obj; curTx = d.AbsoluteLocation; } catch { }
                    if (curTx == null) return "无法获取 " + SafeName(obj) + " 的 AbsoluteLocation。";

                    // 读取旧位置（供报告）
                    double oldX = 0, oldY = 0, oldZ = 0;
                    try { dynamic t = curTx.Translation; oldX = (double)t.X; oldY = (double)t.Y; oldZ = (double)t.Z; }
                    catch { try { oldX = (double)((dynamic)curTx)[0, 3]; oldY = (double)((dynamic)curTx)[1, 3]; oldZ = (double)((dynamic)curTx)[2, 3]; } catch { } }

                    // 修改平移
                    bool written = false;
                    try { dynamic d = curTx; d.Translation = new TxVector(x, y, z); written = true; } catch { }
                    if (!written) try { dynamic d = curTx; d[0, 3] = x; d[1, 3] = y; d[2, 3] = z; written = true; } catch { }

                    // 修改姿态（仅当指定了 rx/ry/rz）
                    if (rx.HasValue || ry.HasValue || rz.HasValue)
                    {
                        // 读取旧 RPY — 多策略反射（与 RobotBaseReader 同策略）
                        double oldRx = 0, oldRy = 0, oldRz = 0;
                        try
                        {
                            dynamic rp = InvokeOrGet(curTx, "RotationRPY_ZYX")
                                         ?? InvokeOrGet(curTx, "RotationRPY")
                                         ?? InvokeOrGet(curTx, "GetRotationRPY");
                            oldRx = (double)rp.X; oldRy = (double)rp.Y; oldRz = (double)rp.Z;
                        }
                        catch { }

                        double finalRx = rx ?? oldRx;
                        double finalRy = ry ?? oldRy;
                        double finalRz = rz ?? oldRz;

                        // 尝试设置旋转
                        try { dynamic d = curTx; d.SetRotationRPY(finalRx, finalRy, finalRz); written = true; }
                        catch { try { dynamic d = curTx; d.RotationRPY = new TxVector(finalRx, finalRy, finalRz); } catch { } }
                    }

                    // 写回对象
                    bool applied = false;
                    try { if (obj is ITxLocatableObject loc) { loc.AbsoluteLocation = curTx; applied = true; } } catch { }
                    if (!applied) try { dynamic d = obj; d.AbsoluteLocation = curTx; applied = true; } catch { }
                    if (!applied) try { dynamic d = obj; d.Location = curTx; applied = true; } catch { }
                    if (!applied) try { dynamic d = obj; d.SetAbsoluteLocation(curTx); applied = true; } catch { }

                    if (!applied) return "设置位置失败：无法写入 AbsoluteLocation 到 " + SafeName(obj) + "。";

                    try { TxApplication.RefreshDisplay(); } catch { }

                    var sb = new StringBuilder();
                    sb.AppendLine(SafeName(obj) + " 位置已更新:");
                    sb.AppendLine("  旧: X=" + oldX.ToString("F3") + "  Y=" + oldY.ToString("F3") + "  Z=" + oldZ.ToString("F3"));
                    sb.Append("  新: X=" + x.ToString("F3") + "  Y=" + y.ToString("F3") + "  Z=" + z.ToString("F3"));
                    if (rx.HasValue) sb.Append("  RX=" + rx.Value.ToString("F4"));
                    if (ry.HasValue) sb.Append("  RY=" + ry.Value.ToString("F4"));
                    if (rz.HasValue) sb.Append("  RZ=" + rz.Value.ToString("F4"));
                    if (undo) sb.Append("\n可 Ctrl+Z 撤销");
                    return sb.ToString();
                }
                finally { if (undo) EndUndo(doc); }
            });
        }

        // ───────── 新增：播放/重置仿真（变更）─────────

        /// <summary>播放/暂停/停止/倒带操作仿真。变更操作，需审批。</summary>
        public static string SimulateOperation(string operationName, string action)
        {
            return PsContext.Current.Run<string>(delegate
            {
                // 查找操作
                ITxObject opObj = null;
                if (!string.IsNullOrWhiteSpace(operationName))
                {
                    var ops = CollectOperations();
                    // 精确匹配
                    foreach (var o in ops)
                        if (string.Equals(SafeName(o), operationName, StringComparison.Ordinal))
                        { opObj = o; break; }
                    // 模糊匹配
                    if (opObj == null)
                        foreach (var o in ops)
                            if (SafeName(o).IndexOf(operationName, StringComparison.OrdinalIgnoreCase) >= 0)
                            { opObj = o; break; }
                }
                else
                {
                    opObj = FirstSelected();
                    if (opObj != null)
                    {
                        // 确认选中的是操作类型
                        bool isOp = false;
                        try { dynamic d = opObj; isOp = d.GetType().Name.Contains("Operation"); } catch { }
                        if (!isOp) opObj = null;
                    }
                }

                if (opObj == null) return "找不到操作" + (string.IsNullOrWhiteSpace(operationName) ? "（当前没有选中操作）" : " '" + operationName + "'。");

                try
                {
                    dynamic player = _lastSimPlayer;
                    bool reusePlayer = false;

                    // 判断能否复用已有 player（操作相同）
                    if (player != null)
                    {
                        try
                        {
                            dynamic curOp = player.Operation;
                            if (curOp != null && string.Equals(SafeName(curOp), SafeName(opObj), StringComparison.Ordinal))
                                reusePlayer = true;
                        }
                        catch { }
                    }

                    if (!reusePlayer)
                    {
                        player = new TxSimulationPlayer();
                        try { player.SetOperation((dynamic)opObj); } catch { }
                        _lastSimPlayer = player;
                    }

                    string resultMsg;
                    switch (action)
                    {
                        case "play":
                            player.Rewind();
                            player.Play();
                            resultMsg = "仿真已开始播放";
                            break;
                        case "pause":
                            try { player.Pause(); resultMsg = "仿真已暂停"; }
                            catch { resultMsg = "暂停不可用（仿真可能未在播放中）"; }
                            break;
                        case "stop":
                            try { player.Stop(); resultMsg = "仿真已停止"; }
                            catch
                            {
                                try { player.Pause(); resultMsg = "仿真已暂停(stop回退为pause)"; }
                                catch { resultMsg = "停止/暂停均不可用"; }
                            }
                            break;
                        case "rewind":
                            player.Rewind();
                            resultMsg = "仿真已倒带到起点";
                            break;
                        default:
                            player.Rewind();
                            player.Play();
                            resultMsg = "仿真已开始播放(默认)";
                            break;
                    }

                    return resultMsg + ": " + SafeName(opObj) + "  类型: " + opObj.GetType().Name;
                }
                catch (Exception ex) { return "仿真操作异常: " + ex.Message; }
            });
        }

        private static bool BeginUndo(TxDocument doc, string desc)
        {
            try { dynamic d = doc; dynamic ur = d.UndoRedo; if (ur != null) { ur.BeginCommand(desc); return true; } } catch { }
            try { dynamic d = doc; dynamic ctx = d.UndoContext; if (ctx != null) { ctx.Open(desc); return true; } } catch { }
            try { dynamic d = doc; dynamic um = d.UndoManager; if (um != null) { um.BeginUndoStep(desc); return true; } } catch { }
            return false;
        }

        private static void EndUndo(TxDocument doc)
        {
            try { dynamic d = doc; d.UndoRedo.EndCommand(); return; } catch { }
            try { dynamic d = doc; d.UndoContext.Close(); return; } catch { }
            try { dynamic d = doc; d.UndoManager.EndUndoStep(); return; } catch { }
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
        //  场景里允许同名 —— 实测同一 study 有 4 台都叫 kr210r2700extra 的机器人。
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
            try { dynamic d = o; dynamic p = d.Parent; if (p != null) return (string)p.Name ?? ""; } catch { }
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

        /// <summary>获取当前 PLC 程序实例的 dynamic 引用（统一入口）。</summary>
        private static dynamic GetCurrentPlcProgram()
        {
            try
            {
                dynamic doc = TxApplication.ActiveDocument;
                if (doc == null) return null;
                dynamic plcRoot = doc.PlcProgramRoot;
                if (plcRoot == null) return null;
                try { return plcRoot.CurrentPlcProgram; } catch { }
                try { return plcRoot.CurrentPlcProgramOrNull; } catch { }
                return null;
            }
            catch { return null; }
        }

        // ───────── 资源逻辑状态查询（只读）─────────

        /// <summary>
        /// 查询资源的完整 CEE 逻辑状态：HasPlcAspect、LogicBehavior、SclContainer、关联信号。
        /// 只读，不修改场景。
        /// </summary>
        public static string GetResourceLogicStatus(string name, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj = null;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        string rerr;
                        if (!TryResolve(name, objectId, out obj, out rerr)) return "Error: " + rerr;
                    }
                    else
                        obj = FirstSelected();
                    if (obj == null) return "未找到指定资源"
                        + (string.IsNullOrWhiteSpace(name) ? "（当前无选中）。" : " '" + name + "'。");

                    var sb = new StringBuilder();
                    sb.AppendLine("=== 资源 CEE 逻辑状态 ===");
                    sb.AppendLine("名称: " + SafeName(obj));
                    sb.AppendLine("类型: " + obj.GetType().Name);

                    // HasPlcAspect
                    bool hasAspect = false;
                    try { dynamic d = obj; hasAspect = (bool)d.HasPlcAspect; } catch { }
                    sb.AppendLine("PLC 层面: " + (hasAspect ? "已加载" : "✗ 未加载"));

                    // LogicBehavior (ITxPlcLogicResource)
                    try
                    {
                        dynamic d = obj;
                        dynamic lb = d.LogicBehavior;
                        if (lb != null)
                        {
                            sb.AppendLine("逻辑行为(LB): ✓ 已创建");
                            try { int entries = 0, exits = 0, actions = 0, parameters = 0, constants = 0;
                                foreach (dynamic e in lb)
                                {
                                    string tn = e.GetType().Name;
                                    if (tn.Contains("Entry")) entries++;
                                    else if (tn.Contains("Exit")) exits++;
                                    else if (tn.Contains("Action")) actions++;
                                    else if (tn.Contains("Parameter")) parameters++;
                                    else if (tn.Contains("Constant")) constants++;
                                }
                                sb.Append("  Entry(输入):" + entries);
                                if (exits > 0) sb.Append("  Exit(输出):" + exits);
                                if (actions > 0) sb.Append("  Action(动作):" + actions);
                                if (parameters > 0) sb.Append("  Parameter:" + parameters);
                                if (constants > 0) sb.Append("  Constant:" + constants);
                                sb.AppendLine();
                            } catch { }
                        }
                        else sb.AppendLine("逻辑行为(LB): ✗ 未创建");
                    }
                    catch { sb.AppendLine("逻辑行为(LB): 不支持"); }

                    // SclContainer (ITxPlcSclResource)
                    try
                    {
                        dynamic d = obj;
                        dynamic sc = d.SclContainer;
                        if (sc != null)
                        {
                            sb.AppendLine("SCL 容器: ✓ 已创建");
                            try
                            {
                                string prog = sc.MainProgramText as string;
                                int lines = !string.IsNullOrEmpty(prog)
                                    ? prog.Split(new[] { '\n' }, StringSplitOptions.None).Length : 0;
                                sb.AppendLine("  SCL 代码行数: " + lines);
                            }
                            catch { }
                        }
                        else sb.AppendLine("SCL 容器: ✗ 未创建");
                    }
                    catch { sb.AppendLine("SCL 容器: 不支持"); }

                    // 关联信号
                    try
                    {
                        dynamic plcProg = GetCurrentPlcProgram();
                        if (plcProg != null)
                        {
                            dynamic signals = plcProg.GetSignals();
                            int cnt = 0;
                            if (signals != null)
                                foreach (dynamic s in signals)
                                {
                                    try
                                    {
                                        dynamic res = s.PlcResource;
                                        if (res != null && string.Equals(SafeName(res), SafeName(obj),
                                            StringComparison.OrdinalIgnoreCase))
                                        {
                                            cnt++;
                                            if (cnt == 1) sb.AppendLine("关联信号:");
                                            sb.AppendLine("  " + SafeName(s) + " [" + s.GetType().Name + "]");
                                        }
                                    }
                                    catch { }
                                }
                            if (cnt == 0) sb.AppendLine("关联信号: 无");
                        }
                    }
                    catch { sb.AppendLine("关联信号: 查询失败"); }

                    return sb.ToString().TrimEnd();
                }
                catch (Exception ex) { return "查询资源逻辑状态失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 列出 PLC 程序中所有信号（CEE 内外部共用）。支持 nameFilter 过滤。
        /// </summary>
        public static string ListPlcSignals(string nameFilter)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    dynamic plcProg = GetCurrentPlcProgram();
                    if (plcProg == null) return "当前文档没有定义 PLC 程序。";

                    dynamic signals = plcProg.GetSignals();
                    var sb = new StringBuilder();
                    var list = new List<dynamic>();
                    if (signals != null)
                    {
                        foreach (dynamic s in signals)
                        {
                            if (!string.IsNullOrWhiteSpace(nameFilter))
                            {
                                if (SafeName(s).IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            }
                            list.Add(s);
                        }
                    }
                    if (list.Count == 0)
                        return string.IsNullOrWhiteSpace(nameFilter) ? "PLC 程序中暂无信号。" : "未找到 '" + nameFilter + "'。";

                    sb.AppendLine("=== 信号列表（共 " + list.Count + " 个）===");
                    for (int i = 0; i < list.Count; i++)
                    {
                        dynamic s = list[i];
                        string tn = s.GetType().Name;
                        string addr = ""; try { addr = (string)s.Address ?? ""; } catch { }
                        string dt = ""; try { dt = (string)s.DataType ?? ""; } catch { }
                        string cmt = ""; try { cmt = (string)s.Comment ?? ""; } catch { }

                        sb.Append((i + 1) + ". [" + tn + "] " + SafeName(s));
                        if (!string.IsNullOrEmpty(addr)) sb.Append("  地址:" + addr + "  ");
                        if (!string.IsNullOrEmpty(dt)) sb.Append(" 类型:" + dt);
                        sb.AppendLine();
                        if (!string.IsNullOrEmpty(cmt)) sb.AppendLine("    注释: " + cmt);
                        bool hasAddr = !string.IsNullOrEmpty(addr);
                        sb.AppendLine("    用途: " + (hasAddr ? "外部PLC" : "CEE内部"));
                    }
                    return sb.ToString().TrimEnd();
                }
                catch (Exception ex) { return "列出信号失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 列出所有 CEE 模块及其条目（Modules Viewer Hierarchy）。
        /// </summary>
        public static string ListCeeModules()
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    dynamic plcProg = GetCurrentPlcProgram();
                    if (plcProg == null) return "当前文档没有定义 PLC 程序。";

                    dynamic modules = plcProg.PlcModules;
                    var sb = new StringBuilder();
                    int count = 0;
                    if (modules != null)
                    {
                        foreach (dynamic mod in modules)
                        {
                            count++;
                            sb.AppendLine("模块: " + SafeName(mod));
                            try
                            {
                                dynamic entries = mod.PlcEntries;
                                if (entries != null)
                                {
                                    int ei = 0;
                                    foreach (dynamic e in entries)
                                    {
                                        ei++;
                                        sb.Append("  [" + ei + "] ");
                                        try { sb.Append(e.GetType().Name + " "); } catch { }
                                        sb.Append("ID:" + e.UniqueId);
                                        sb.AppendLine();
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    return count == 0 ? "暂无 CEE 模块。" : "=== CEE 模块列表（共 " + count + " 个）===\n" + sb.ToString().TrimEnd();
                }
                catch (Exception ex) { return "列出 CEE 模块失败: " + ex.Message; }
            });
        }

        // ───────── 信号创建（通用，CEE 内外部均可用）─────────

        /// <summary>创建 PLC 信号（input/output）。CEE 内部使用时 address 留空即可。</summary>
        public static string CreatePlcSignal(string signalType, string name, string address,
            string dataType, string comment)
        {
            return PsContext.Current.Run<string>(delegate
                                                         {
                                                             try
                                                             {
                                                                 dynamic plcProg = GetCurrentPlcProgram();
                                                                 if (plcProg == null) return "当前文档没有定义 PLC 程序，无法创建信号。";
                                                                 if (string.IsNullOrWhiteSpace(name)) return "信号名称不能为空。";
                                                                 if (string.IsNullOrWhiteSpace(dataType)) dataType = "BOOL";

                                                                 dynamic doc = TxApplication.ActiveDocument;
                                                                 bool undo = BeginUndo(doc, "create_signal: " + name);
                                                                 try
                                                                 {
                                                                     dynamic sig;
                                                                     switch (signalType.ToLowerInvariant())
                                                                     {
                                                                         case "input":
                                                                             {
                                                                                 var d = new TxPlcInputSignalCreationData(); d.Name = name;
                                                                                 SetPropertyFromString(d, "DataType", dataType);
                                                                                 if (!string.IsNullOrWhiteSpace(address)) SetPropertyFromString(d, "Address", address);
                                                                                 if (!string.IsNullOrWhiteSpace(comment)) d.Comment = comment;
                                                                                 sig = plcProg.CreatePlcInputSignal(d);
                                                                             }
                                                                             break;
                                                                         case "output":
                                                                             {
                                                                                 var d = new TxPlcOutputSignalCreationData(); d.Name = name;
                                                                                 SetPropertyFromString(d, "DataType", dataType);
                                                                                 if (!string.IsNullOrWhiteSpace(address)) SetPropertyFromString(d, "Address", address);
                                                                                 if (!string.IsNullOrWhiteSpace(comment)) d.Comment = comment;
                                                                                 sig = plcProg.CreatePlcOutputSignal(d);
                                                                             }
                                                                             break;
                                                                         case "display":
                                                                             {
                                                                                 var d = new TxPlcDisplaySignalCreationData(); d.Name = name;
                                                                                 SetPropertyFromString(d, "DataType", dataType);
                                                                                 if (!string.IsNullOrWhiteSpace(comment)) d.Comment = comment;
                                                                                 sig = plcProg.CreatePlcDisplaySignal(d);
                                                                             }
                                                                             break;
                                                                         default:
                                                                             return "不支持的信号类型 '" + signalType + "'。支持: input, output, display。";
                                                                     }
                                                                     string result = "已创建" + signalType + "信号: " + SafeName(sig) + "  类型:" + dataType
                                                                         + (string.IsNullOrWhiteSpace(address) ? "" : "  地址:" + address);
                                                                     if (undo) result += "\n可 Ctrl+Z 撤销。";
                                                                     return result;
                                                                 }
                                                                 finally { if (undo) EndUndo(doc); }
                                                             }
                                                             catch (Exception ex) { return "创建信号失败: " + ex.Message; }
                                                         });
        }

        // ───────── CEE 逻辑块操作（变更）─────────

        /// <summary>
        /// 为资源添加 CEE 逻辑行为（创建 Smart Component）。
        /// 资源必须实现 ITxPlcLogicBehaviorCreation 接口。
        /// </summary>
        public static string AddLogicToResource(string name, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj; string rerr;
                    if (!TryResolve(name, objectId, out obj, out rerr)) return "Error: " + rerr;
                    if (obj == null) return "未找到资源'" + (name ?? "(选中)") + "'。";

                    dynamic doc = TxApplication.ActiveDocument;
                    bool undo = BeginUndo(doc, "add_logic: " + SafeName(obj));
                    try
                    {
                        dynamic d = obj;
                        try { bool can = (bool)d.CanCreateLogicBehavior; if (!can) return "该资源不允许创建逻辑行为。"; }
                        catch { return "该资源不支持逻辑行为（未实现 ITxPlcLogicBehaviorCreation）。"; }

                        d.CreateLogicBehavior();
                        string result = "已为资源 '" + Ref(obj) + "' 创建逻辑行为（智能组件）。";
                        if (undo) result += "\n可在 Resource Logic Behavior Editor 中编辑 Entries/Exits/Actions。可 Ctrl+Z 撤销。";
                        return result;
                    }
                    finally { if (undo) EndUndo(doc); }
                }
                catch (Exception ex) { return "添加逻辑行为失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 为资源创建 SCL 容器，用于结构化文本编程。
        /// 资源必须实现 ITxPlcSclCreation 接口。
        /// </summary>
        public static string CreateSclContainer(string name, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj; string rerr;
                    if (!TryResolve(name, objectId, out obj, out rerr)) return "Error: " + rerr;
                    if (obj == null) return "未找到资源'" + (name ?? "(选中)") + "'。";

                    dynamic doc = TxApplication.ActiveDocument;
                    bool undo = BeginUndo(doc, "create_scl: " + SafeName(obj));
                    try
                    {
                        dynamic d = obj;
                        try { bool can = (bool)d.CanCreateSclContainer; if (!can) return "该资源不允许创建 SCL 容器。"; }
                        catch { return "该资源不支持 SCL（未实现 ITxPlcSclCreation）。"; }

                        d.CreateSclContainer();
                        string result = "已为资源 '" + Ref(obj) +"' 创建 SCL 容器。";
                        if (undo) result += "\n可在 SCL Editor 中编写结构化文本逻辑。可 Ctrl+Z 撤销。";
                        return result;
                    }
                    finally { if (undo) EndUndo(doc); }
                }
                catch (Exception ex) { return "创建 SCL 容器失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 将一个资源的逻辑复制到另一个同类资源。
        /// 源必须已有 LogicBehavior，目标必须为空且同类型。
        /// </summary>
        public static string CopyLogic(string sourceName, string targetName, string sourceId = null, string targetId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject src, tgt; string rerr;
                    if (!TryResolve(sourceName, sourceId, out src, out rerr)) return "Error: 源资源 -> " + rerr;
                    if (!TryResolve(targetName, targetId, out tgt, out rerr)) return "Error: 目标资源 -> " + rerr;

                    dynamic doc = TxApplication.ActiveDocument;
                    bool undo = BeginUndo(doc, "copy_logic: " + SafeName(src) + " → " + SafeName(tgt));
                    try
                    {
                        dynamic dSrc = src;
                        try { dSrc.CopySelfLogicToOtherLogicResource((dynamic)tgt); }
                        catch (Exception ex) { return "复制逻辑失败: " + ex.Message + "。目标资源可能已有逻辑或类型不兼容。"; }

                        string result = "已将 '" + Ref(src) + "' 的逻辑行为复制到 '" + Ref(tgt) + "'。";
                        if (undo) result += "\n可 Ctrl+Z 撤销。";
                        return result;
                    }
                    finally { if (undo) EndUndo(doc); }
                }
                catch (Exception ex) { return "复制逻辑失败: " + ex.Message; }
            });
        }

        // ───────── CEE 模块操作（变更）─────────

        /// <summary>
        /// 创建 CEE 模块（Modules Viewer Hierarchy 中的模块）。
        /// 模块用于编写信号表达式：ResultSignal = expression of signals。
        /// </summary>
        public static string CreateCeeModule(string name)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    dynamic plcProg = GetCurrentPlcProgram();
                    if (plcProg == null) return "当前文档没有定义 PLC 程序，无法创建模块。";
                    if (string.IsNullOrWhiteSpace(name)) return "模块名称不能为空。";

                    dynamic doc = TxApplication.ActiveDocument;
                    bool undo = BeginUndo(doc, "create_module: " + name);
                    try
                    {
                        var data = new TxPlcModuleCreationData(name);
                        dynamic mod = plcProg.CreateModule(data);
                        string result = "已创建 CEE 模块: " + name;
                        if (undo) result += "\n可在 Modules Viewer 中编辑信号表达式和 IF/ELSE 条件。可 Ctrl+Z 撤销。";
                        return result;
                    }
                    finally { if (undo) EndUndo(doc); }
                }
                catch (Exception ex) { return "创建 CEE 模块失败: " + ex.Message; }
            });
        }

        // ───────── 传感器创建（变更）─────────

        /// <summary>
        /// 在资源上创建光传感器。资源必须实现 ITxPlcSensorCreation。
        /// 光传感器可检测物体遮挡，发出信号到 LB Entry。
        /// </summary>
        public static string CreatePlcSensor(string resourceName, string sensorType, string sensorName, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj; string rerr;
                    if (!TryResolve(resourceName, objectId, out obj, out rerr)) return "Error: " + rerr;
                    if (obj == null) return "未找到资源'" + (resourceName ?? "(选中)") + "'。";
                    if (string.IsNullOrWhiteSpace(sensorName))
                        sensorName = "Sensor_" + SafeName(obj);

                    dynamic doc = TxApplication.ActiveDocument;
                    bool undo = BeginUndo(doc, "create_sensor: " + sensorName);
                    try
                    {
                        dynamic d = obj;
                        try { bool can = (bool)d.CanCreatePlcLightSensor;
                              if (!can) return "该资源不允许创建光传感器。"; }
                        catch { return "该资源不支持传感器创建（未实现 ITxPlcSensorCreation）。"; }

                        // TxPlcLightSensorCreationData 为 abstract，尝试多种具体子类
                        dynamic sensorData = null;
                        try { sensorData = Activator.CreateInstance(
                            Type.GetType("Tecnomatix.Engineering.TxTcPlcLightSensorCreationData, Tecnomatix.Engineering")); }
                        catch { }
                        if (sensorData == null)
                            try { sensorData = Activator.CreateInstance(
                                Type.GetType("Tecnomatix.Engineering.TxEmsPlcLightSensorCreationData, Tecnomatix.Engineering")); }
                            catch { }

                        if (sensorData == null)
                        {
                            // 最后尝试用动态直接 new — 某些版本自动解析
                            try
                            {
                                dynamic typeToCreate = Type.GetType("Tecnomatix.Engineering.TxTcPlcLightSensorCreationData");
                                if (typeToCreate != null) sensorData = Activator.CreateInstance(typeToCreate);
                            }
                            catch { }
                        }

                        if (sensorData == null)
                            return "无法创建光传感器创建数据：找不到 TxTcPlcLightSensorCreationData 或 TxEmsPlcLightSensorCreationData 类型。";

                        sensorData.Name = sensorName;
                        try { sensorData.CurrentRange = 300.0; } catch { }
                        try { sensorData.MaxRange = 500.0; } catch { }

                        dynamic sensor = d.CreatePlcLightSensor(sensorData);
                        string sensorRef;
                        try { sensorRef = Ref((ITxObject)sensor); } catch { sensorRef = SafeName(sensor); }
                        string result = "已创建光传感器: " + sensorRef + " (资源: " + Ref(obj) + ")";
                        if (undo) result += "\n可 Ctrl+Z 撤销。";
                        return result;
                    }
                    finally { if (undo) EndUndo(doc); }
                }
                catch (Exception ex) { return "创建传感器失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 列出资源的 LogicBehavior 元素：Entries（入口）、Exits（出口）、
        /// Actions（动作）、Parameters（参数）、Constants（常量）。
        /// 只读。用于检查 LB 待连接的状态。
        /// </summary>
        public static string ListLogicBehaviorElements(string resourceName, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj; string rerr;
                    if (!TryResolve(resourceName, objectId, out obj, out rerr)) return "Error: " + rerr;
                    if (obj == null) return "未找到资源'" + (resourceName ?? "(选中)") + "'。";

                    dynamic d = obj;
                    dynamic lb = null;
                    try { lb = d.LogicBehavior; } catch { }
                    if (lb == null) return "该资源没有 LogicBehavior。请先调用 add_logic_to_resource 创建。";

                    var refLabel = Ref(obj);
                    var sb = new StringBuilder();
                    sb.AppendLine("=== LogicBehavior 元素: " + refLabel + " ===");
                    int total = 0;

                    string[] categories = { "Entry", "Exit", "Action", "Parameter", "Constant" };
                    // 尝试多种枚举策略
                    foreach (var cat in categories)
                    {
                        var group = new List<string>();
                        // 策略1: 按接口名筛选
                        try
                        {
                            foreach (dynamic el in lb)
                            {
                                string tn = el.GetType().Name;
                                if (tn.Contains(cat))
                                {
                                    string elName = SafeName(el);
                                    string detail = "";
                                    try { dynamic sig = el.Signal; if (sig != null) detail = " → " + SafeName(sig); } catch { }
                                    try { dynamic sig = el.ConnectedSignal; if (sig != null) detail = " → " + SafeName(sig); } catch { }
                                    group.Add("  " + elName + " [" + tn + "]" + detail);
                                    total++;
                                }
                            }
                        }
                        catch { }

                        if (group.Count > 0)
                        {
                            sb.AppendLine("[" + cat + "] (" + group.Count + "个):");
                            foreach (var g in group) sb.AppendLine(g);
                        }
                    }

                    if (total == 0)
                        sb.AppendLine("LB 中没有元素。请在 Resource Logic Behavior Editor 中创建 Entries/Exits。");
                    else
                        sb.Insert(23 + refLabel.Length, "共 " + total + " 个元素\n");

                    return sb.ToString().TrimEnd();
                }
                catch (Exception ex) { return "列出 LB 元素失败: " + ex.Message; }
            });
        }

        /// <summary>
        /// 将 PLC 信号连接到资源的 LogicBehavior Entry 或 Exit。
        /// 使用多策略 dynamic 调用兜底 SDK 版本差异。
        /// </summary>
        public static string ConnectSignalToLB(string resourceName, string signalName,
            string pinType, string pinName, string objectId = null)
        {
            return PsContext.Current.Run<string>(delegate
            {
                try
                {
                    ITxObject obj; string rerr;
                    if (!TryResolve(resourceName, objectId, out obj, out rerr)) return "Error: " + rerr;
                    if (obj == null) return "未找到资源'" + (resourceName ?? "(选中)") + "'。";

                    // 获取 LB
                    dynamic d = obj;
                    dynamic lb = null;
                    try { lb = d.LogicBehavior; } catch { }
                    if (lb == null) return "该资源没有 LogicBehavior。";

                    // 查找信号
                    dynamic plcProg = GetCurrentPlcProgram();
                    if (plcProg == null) return "PLC 程序未定义。";
                    dynamic sig = null;
                    dynamic signals = plcProg.GetSignals();
                    if (signals != null)
                    {
                        foreach (dynamic s in signals)
                        {
                            if (string.Equals(SafeName(s), signalName, StringComparison.OrdinalIgnoreCase))
                            { sig = s; break; }
                        }
                    }
                    if (sig == null) return "未找到信号 '" + signalName + "'。";

                    // 查找 LB 元素（Entry 或 Exit）
                    dynamic targetEl = null;
                    try
                    {
                        foreach (dynamic el in lb)
                        {
                            string tn = el.GetType().Name;
                            if ((pinType == "entry" && tn.Contains("Entry"))
                                || (pinType == "exit" && tn.Contains("Exit")))
                            {
                                if (string.IsNullOrWhiteSpace(pinName) ||
                                    SafeName(el).IndexOf(pinName, StringComparison.OrdinalIgnoreCase) >= 0)
                                { targetEl = el; break; }
                            }
                        }
                    }
                    catch { }

                    if (targetEl == null)
                        return "未找到 " + pinType + " 引脚'" + (pinName ?? "(任意)") + "'。请先用 PS 编辑器在 LB 中创建 " + pinType + "。";

                    // 连接策略（多种 API 版本）
                    bool connected = false;
                    string errors = "";

                    // 策略1: 设 LB 元素上的 Signal/ConnectedSignal 属性
                    try { targetEl.Signal = sig; connected = true; } catch (Exception ex) { errors += "Signal=" + ex.Message + "; "; }
                    if (!connected) try { targetEl.ConnectedSignal = sig; connected = true; } catch (Exception ex) { errors += "ConnectedSignal=" + ex.Message + "; "; }

                    // 策略2: 设信号上的属性指向 LB 元素
                    if (!connected) try { sig.LBEntry = targetEl; connected = true; } catch { }
                    if (!connected) try { sig.LBExit = targetEl; connected = true; } catch { }

                    // 策略3: LB 上的 Connect/Set/Bind 方法
                    if (!connected) try { lb.ConnectEntryToSignal(targetEl, sig); connected = true; } catch { }
                    if (!connected) try { lb.ConnectExitToSignal(targetEl, sig); connected = true; } catch { }
                    if (!connected) try { lb.SetConnectedSignal(targetEl, sig); connected = true; } catch { }
                    if (!connected) try { targetEl.Connect(sig); connected = true; } catch { }

                    if (!connected)
                        return "无法连接信号到 LB 引脚。失败的尝试: " + errors
                            + "请在 PS Resource Logic Behavior Editor 中手动连接。";

                    return "已将信号 '" + SafeName(sig) + "' 连接到 " + pinType + " '"
                        + SafeName(targetEl) + "' (资源: " + Ref(obj) + ")。";
                }
                catch (Exception ex) { return "连接信号到 LB 失败: " + ex.Message; }
            });
        }
    }
}
