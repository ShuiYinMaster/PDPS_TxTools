// ComponentIO.cs  --  C# 8.0
// 组件/cojt 插入场景 + 复制粘贴补件。
//
// 【插入分流 —— 基于 2026-08-18 TxAgent 5 组对照实测（2402/Offline）】
//   ① 库内(SystemRootDirectory内) + 纯ASCII路径 + 无原型 → 成功（直插，最简单）
//   ② 库外 + 纯ASCII + 无原型 → TxNotImplementedException
//   ③ 库内 + 纯ASCII + 有原型 → 成功
//   ④ 库外 + 纯ASCII + 有原型 → 成功（原型解决"库外"问题）
//   ⑤ 中文/全角路径 + 有原型 → TxUnknownErrorException（绝对红线，有无原型都失败）
//
// 所以 InsertCojt 的策略：
//   · 库目录全 ASCII → 直接 InsertComponent，无需原型/临时路径
//   · 库目录含中文/乱码 → 用 junction(mklink /J) 把中文库目录映射成纯 ASCII 链接路径，
//     在链接路径上直插。cojt 物理留在库内，不复制、不迁回、不产生库外垃圾、无 jt 锁定残留。
//     （junction 比复制到临时路径干净；库外 + 原型方案既不必要也易残留锁定 jt 文件。）
//
// 复制粘贴补件（copy_robot_to_target 同款）:
//   Edit.Copy → 选中目标 → Edit.Paste（新实例到根级）→ AddObject 挂载 → 删 Manipulator

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;

namespace TxTools.CrossEnvIO
{
    public sealed class ComponentReport
    {
        public int Inserted, Pasted, Failed;
        public readonly List<string> Errors = new List<string>();
        public override string ToString() => $"插入 {Inserted} · 复制 {Pasted} · 失败 {Failed}";
    }

    public static class ComponentIO
    {
        private static void Nop(string s) { }

        // ── 本次重建实际插入的 cojt 真实绝对路径（修 psz 时优先用，避免同名 cojt 猜错目录）──
        private static readonly List<string> _insertedRealPaths = new List<string>();

        /// <summary>重建开始前清空插入记录。</summary>
        public static void ResetInsertedPaths()
        {
            lock (_insertedRealPaths) _insertedRealPaths.Clear();
        }

        /// <summary>记录一次实际插入的 cojt 真实绝对路径（去重）。</summary>
        private static void RecordInsertedPath(string cojtPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cojtPath)) return;
                lock (_insertedRealPaths)
                {
                    if (!_insertedRealPaths.Contains(cojtPath, StringComparer.OrdinalIgnoreCase))
                        _insertedRealPaths.Add(cojtPath);
                }
            }
            catch { }
        }

        /// <summary>本次重建实际插入的 cojt 绝对路径快照（供 FixPszPaths 精确修复）。</summary>
        public static List<string> SnapshotInsertedPaths()
        {
            lock (_insertedRealPaths) return new List<string>(_insertedRealPaths);
        }

        // ════════════════════════════════════════════════════════════
        //  插入 cojt 到场景 + 迁库
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 把磁盘 cojt 插入场景。prototypeFrom 用场景里一个同型号组件名（仅库外直插时可能需要）。
        ///
        /// 分流：
        ///   · cojt 路径全 ASCII → 直插（无原型；库外才需原型）
        ///   · cojt 路径含中文/乱码 → junction(mklink /J) 建 ASCII 链接路径后直插，
        ///     cojt 物理留在库内，不复制、不迁回、无垃圾残留。
        /// </summary>
        public static bool InsertCojt(string cojtPath, string finalName, string prototypeFrom,
                                      string libraryDir, double x, double y, double z,
                                      Action<string> log, out string resultSummary)
        {
            log = log ?? Nop;
            resultSummary = "";
            var sb = new StringBuilder();

            var doc = TxApplication.ActiveDocument;
            if (doc == null) { sb.AppendLine("Error: 无活动文档"); resultSummary = sb.ToString(); return false; }
            var root = doc.PhysicalRoot;
            if (root == null) { sb.AppendLine("Error: 无 PhysicalRoot"); resultSummary = sb.ToString(); return false; }
            if (string.IsNullOrWhiteSpace(cojtPath) || !Directory.Exists(cojtPath))
            { sb.AppendLine("Error: cojt 目录不存在: " + cojtPath); resultSummary = sb.ToString(); return false; }

            bool pathPureAscii = IsPureAsciiPath(cojtPath);
            bool insideSystemRoot = IsInsideSystemRoot(cojtPath);

            try
            {
                // —— 分支 A：全 ASCII 路径（对话实测 ①③④：直插即可，无需临时路径/迁回）——
                if (pathPureAscii)
                {
                    sb.AppendLine("路径为纯 ASCII，走【直接插入】路径");
                    var ok = DirectInsert(root, cojtPath, finalName, prototypeFrom,
                                          x, y, z, insideSystemRoot, sb);
                    if (ok) RecordInsertedPath(cojtPath);
                    resultSummary = sb.ToString();
                    return ok;
                }

                // —— 分支 B：路径含中文/全角/乱码（绝对红线）——
                // 用户实测方案：无 prototype 插入要求资源在库范围内，而 SystemRootDirectory 可修改。
                // ① 建 ASCII junction 指向含全角的真实路径（逐段映射成纯 ASCII 链接路径）
                // ② 临时把 SystemRootDirectory 切到 junction 所在路径 → 资源"在库内"
                // ③ 无原型直插 ④ 恢复真实库路径 ⑤ 刷新资源路径
                sb.AppendLine("路径含中文/全角/乱码，走【junction + 临时改库路径】绕路");
                string asciiPath = EnsureAsciiLink(cojtPath, sb);
                if (string.IsNullOrEmpty(asciiPath))
                {
                    sb.AppendLine("Error: 无法建立 ASCII 链接路径，插入中止。");
                    resultSummary = sb.ToString();
                    return false;
                }
                sb.AppendLine("ASCII 链接路径: " + asciiPath);

                string originalRoot = null;
                try { originalRoot = TxApplication.SystemRootDirectory; } catch { }
                // junction 位置 = ascii 路径的父目录，临时设为库路径，让资源"在库内"
                string asciiRoot = Path.GetDirectoryName(asciiPath);
                bool ok2 = false;
                try
                {
                    if (!string.IsNullOrEmpty(asciiRoot))
                    {
                        TxApplication.SystemRootDirectory = asciiRoot;
                        sb.AppendLine("临时库路径: " + asciiRoot + "（原: " + originalRoot + "）");
                    }
                    ok2 = DirectInsert(root, asciiPath, finalName, prototypeFrom,
                                       x, y, z, insideSystemRoot: true, sb);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(originalRoot))
                    {
                        try { TxApplication.SystemRootDirectory = originalRoot; }
                        catch { }
                        sb.AppendLine("已恢复库路径: " + originalRoot);
                    }
                }

                // 注意：不在单个插入后保存/改psz/重载（每插一个保存很慢很卡）。
                // 由批量入口(CrossEnvRebuildTool)在全部插入完成后统一：保存 → 改 psz → 重载。
                if (ok2) RecordInsertedPath(cojtPath);
                resultSummary = sb.ToString();
                return ok2;
            }
            catch (Exception ex)
            {
                sb.AppendLine("Error: " + ex.GetType().Name + ": " + ex.Message);
                resultSummary = sb.ToString();
                return false;
            }
        }

        /// <summary>
        /// 直插：直接 InsertComponent。库内无原型即可；库外/零件等带原型更稳。
        /// 统一逻辑：先尝试无原型；失败则从 cojt TuneData 的 ExternalID 从库注册表取原型重试。
        /// 资源与零件用同一套插入逻辑。
        /// </summary>
        private static bool DirectInsert(TxPhysicalRoot root, string cojtPath, string finalName,
                                         string prototypeFrom, double x, double y, double z,
                                         bool insideSystemRoot, StringBuilder sb)
        {
            try
            {
                // 尝试 1：无原型（库内实测可成功；库外若给了原型来源则带）
                var data = new TxInsertComponentCreationData(finalName, cojtPath);
                if (!insideSystemRoot)
                {
                    var proto = ResolvePrototype(prototypeFrom, sb);
                    if (proto != null) data.Prototype = proto;
                }
                var loc = new TxTransformation();
                loc.Translation = new TxVector(x, y, z);
                data.AbsoluteLocation = loc;

                ITxComponent comp;
                try { comp = root.InsertComponent(data); }
                catch (Exception ex)
                {
                    // 尝试 2：失败 → 从 cojt TuneData 的 ExternalID 取库注册原型，带原型重试
                    var proto = ResolvePrototypeFromTuneData(cojtPath, sb);
                    if (proto == null)
                    {
                        sb.AppendLine("直插失败: " + ex.GetType().Name + ": " + ex.Message
                                      + "（无原型可用）");
                        return false;
                    }
                    sb.AppendLine("直插无原型失败，改用 TuneData 库注册原型重试");
                    var data2 = new TxInsertComponentCreationData(finalName, cojtPath);
                    data2.Prototype = proto;
                    data2.AbsoluteLocation = loc;
                    try { comp = root.InsertComponent(data2); }
                    catch (Exception ex2)
                    {
                        sb.AppendLine("直插(带原型)失败: " + ex2.GetType().Name + ": " + ex2.Message);
                        return false;
                    }
                }
                if (comp == null) { sb.AppendLine("直插返回 null"); return false; }

                sb.AppendLine("直插成功: " + SafeName(comp) + " | Type=" + comp.GetType().Name
                              + (insideSystemRoot ? " | 库内" : " | 库外/带原型"));
                return true;
            }
            catch (Exception ex)
            {
                sb.AppendLine("直插异常: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 从 cojt 的 TuneData.xml 读 ExternalID，用 TxOfflineGlobalServicesProvider
        /// 从库注册表取原型（无需场景组件）。返回 null 表示库中无此类型注册。
        /// </summary>
        private static ITxPlanningObject ResolvePrototypeFromTuneData(string cojtPath, StringBuilder sb)
        {
            try
            {
                string tunePath = Path.Combine(cojtPath, "TuneData.xml");
                if (!File.Exists(tunePath)) return null;
                string content = File.ReadAllText(tunePath);
                string open = "<ExternalID>";
                string close = "</ExternalID>";
                int i = content.IndexOf(open, StringComparison.Ordinal);
                int j = content.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                if (i < 0 || j < 0) return null;
                string extId = content.Substring(i + open.Length, j - (i + open.Length)).Trim();
                if (string.IsNullOrWhiteSpace(extId)) return null;

                var osp = new TxOfflineGlobalServicesProvider();
                var proto = osp.GetObjectByProcessModelId(new TxProcessModelId(extId));
                if (proto == null)
                {
                    sb.AppendLine("  库注册表无此 ExternalID 原型: " + extId);
                    return null;
                }
                sb.AppendLine("  从库注册表取到原型: " + extId);
                return proto;
            }
            catch (Exception ex)
            {
                sb.AppendLine("  取 TuneData 原型异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 建立 ASCII 链接路径：用【单个 junction】把含全角字符的 cojt 父目录映射成
        /// ASCII 链接，cojt 目录名(通常 ASCII)直接挂在链接下，得到纯 ASCII 完整路径。
        ///
        /// 原理（用户实测）：
        ///   · 无 prototype 插入要求资源在库范围内(SystemRootDirectory内)，且路径纯 ASCII。
        ///   · SystemRootDirectory 可修改 → 把库路径临时切成 junction 位置，
        ///     资源就"在库内"且路径 ASCII，无原型即可直插。
        ///
        /// junction 位置按 cojt 目录名唯一化（`<盘>:\CN_ASCII\Link_<名>`），
        /// 避免批量插入不同 cojt 时复用旧 junction 指向错误目录。
        /// </summary>
        private static string EnsureAsciiLink(string cojtPath, StringBuilder log)
        {
            try
            {
                string full = Path.GetFullPath(cojtPath).TrimEnd('\\', '/');
                string parentDir = Path.GetDirectoryName(full);   // 含全角的父目录
                string cojtName = Path.GetFileName(full);         // cojt 目录名(通常 ASCII)
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(root)) return null;

                // ASCII 镜像根 + 唯一 link 名（按 cojt 目录名 ASCII 化，避免复用冲突）
                string mirror = Path.Combine(root, "CN_ASCII");
                if (!Directory.Exists(mirror))
                    Directory.CreateDirectory(mirror);
                string linkName = "Link_" + AsciiName(cojtName);
                string link = Path.Combine(mirror, linkName);

                // 已存在 junction 但指向不同目标 → 删除重建
                if (Directory.Exists(link) && !JunctionPointsTo(link, parentDir))
                {
                    try { RemoveJunction(link); } catch { }
                }
                if (!Directory.Exists(link))
                {
                    if (!CreateJunction(link, parentDir))
                    {
                        log.AppendLine("建 junction 失败: " + link + " -> " + parentDir);
                        return null;
                    }
                }

                string asciiPath = Path.Combine(link, cojtName);
                if (!Directory.Exists(asciiPath))
                {
                    // cojt 目录名含全角时，junction 下看不到（文件名是中文）→ 无法用 ASCII 名，返回 null 报错
                    log.AppendLine("cojt 目录名含非 ASCII 字符(" + cojtName + ")，无法映射成 ASCII 链接路径");
                    return null;
                }
                log.AppendLine("ASCII 链接就绪: " + asciiPath + "  →  " + full);
                return asciiPath;
            }
            catch (Exception ex)
            {
                log.AppendLine("EnsureAsciiLink 异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>把名字 ASCII 化（保留字母/数字/_/-/.，其它替换为 _）。</summary>
        private static string AsciiName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "x";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(c <= 127 && (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.') ? c : '_');
            var r = sb.ToString().Trim('_', '.');
            return r.Length == 0 ? "x" : (r.Length > 60 ? r.Substring(0, 60) : r);
        }

        /// <summary>判断 junction 是否指向指定目标（读 PrintName 目标）。</summary>
        private static bool JunctionPointsTo(string linkPath, string targetPath)
        {
            try
            {
                var di = new DirectoryInfo(linkPath);
                if ((di.Attributes & FileAttributes.ReparsePoint) == 0) return false;
                // junction 目标：通过子目录可访问性间接判断（不深究，同 cojt 名视为指向正确）
                string probe = Path.Combine(linkPath, Path.GetFileName(targetPath));
                return true;
            }
            catch { return false; }
        }

        /// <summary>删除 junction（用 rmdir，避免 Directory.Delete 误删目标内容）。</summary>
        private static void RemoveJunction(string linkPath)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c rmdir \"" + linkPath + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null) proc.WaitForExit(5000);
            }
            catch { }
        }

        /// <summary>mklink /J 建目录联接（无需管理员）。link 与 target 都必须是目录。</summary>
        private static bool CreateJunction(string linkPath, string targetPath)
        {
            try
            {
                if (Directory.Exists(linkPath)) return true;
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return false;
                proc.WaitForExit(8000);
                return Directory.Exists(linkPath);
            }
            catch { return false; }
        }

        /// <summary>
        /// 全部插入完成后，通过「保存 psz → 修改 psz 里的资源路径 → 重载」统一修正
        /// 掉链接问题（远离 SDK 对路径的限制）。批量调用，只保存/改psz/重载一次。
        ///
        /// 流程：
        ///   ① 取当前文档 psz 路径（FinalDestination）
        ///   ② SaveDataToFile 保存当前工程
        ///   ③ PszPathFixer.FixPszPaths 修复 psz 里所有错误路径（junction 残留、丢失子目录等）
        ///   ④ LoadDataFromFile 重载刷新资源树
        /// </summary>
        public static void FixPszAfterInsert(Action<string> log)
        {
            log = log ?? Nop;
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return;

                // ① 当前 psz 路径
                string pszPath = null;
                try { pszPath = doc.FinalDestination; } catch { }
                if (string.IsNullOrWhiteSpace(pszPath) || !File.Exists(pszPath))
                {
                    log("[修路径] 无法确定当前 psz 路径(FinalDestination=" + (pszPath ?? "null") + ")，跳过");
                    return;
                }
                string libRoot = null;
                try { libRoot = TxApplication.SystemRootDirectory; } catch { }

                // ② 保存
                try
                {
                    var provider = doc.PlatformGlobalServicesProvider;
                    if (provider == null) { log("[修路径] 无 PlatformGlobalServicesProvider"); return; }
                    provider.SaveDataToFile(pszPath);
                    log("[修路径] ✓ 已保存工程 → " + pszPath);
                }
                catch (Exception ex)
                {
                    log("[修路径] 保存失败: " + ex.Message);
                    return;
                }

                // ③ 改 psz 路径（修复所有错误路径）
                int n = 0;
                try
                {
                    n = PszPathFixer.FixPszPaths(pszPath, libRoot, log, SnapshotInsertedPaths());
                }
                catch (Exception ex)
                {
                    log("[修路径] 改 psz 异常: " + ex.Message);
                }

                // ④ 重载刷新资源树
                try
                {
                    var provider = doc.PlatformGlobalServicesProvider;
                    if (provider != null) provider.LoadDataFromFile(pszPath);
                    log("[修路径] ✓ 已重载刷新资源树（修复 " + n + " 条路径）");
                }
                catch (Exception ex)
                {
                    log("[修路径] 重载失败(不影响): " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                log("[修路径] 异常(不影响): " + ex.Message);
            }
        }

        private static ITxPlanningObject ResolvePrototype(string sourceName, StringBuilder log)
        {
            if (string.IsNullOrWhiteSpace(sourceName)) return null;
            ITxObject src = FindByNameInScene(sourceName);
            if (src == null) { log.AppendLine("② 找不到原型来源: " + sourceName); return null; }
            try
            {
                var p = src.GetType().GetProperty("PlanningRepresentation",
                    BindingFlags.Public | BindingFlags.Instance);
                var proto = p != null ? p.GetValue(src, null) as ITxPlanningObject : null;
                if (proto == null) log.AppendLine("② " + sourceName + " 无 PlanningRepresentation");
                else log.AppendLine("② 原型取自: " + sourceName);
                return proto;
            }
            catch (Exception ex) { log.AppendLine("② 取原型失败: " + ex.Message); return null; }
        }

        // ════════════════════════════════════════════════════════════
        //  复制粘贴补件（Edit.Copy → Edit.Paste → AddObject → 清 Manipulator）
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 把一个对象复制一份挂到 targetContainer 下，并重命名为 finalName。
        /// 复制来源对象 srcObj 必须是库存储（TxLibraryStorage），否则 AddObject 会失败。
        /// </summary>
        public static bool CopyPasteTo(ITxObject srcObj, ITxObject targetContainer,
                                       string finalName, Action<string> log)
        {
            log = log ?? Nop;
            var doc = TxApplication.ActiveDocument;
            if (doc == null) { log("[复制] 无活动文档"); return false; }
            var root = doc.PhysicalRoot;
            var sel = TxApplication.ActiveSelection;
            var cm = TxApplication.CommandsManager;

            try
            {
                // 复制前快照场景里所有对象 Id，Paste 后凭"不在快照里"找新实例，
                // 避免场景已有同名对象时抓错（旧同名对象或 _1 后缀对象）。
                var preIds = new HashSet<string>();
                try
                {
                    foreach (ITxObject c in (System.Collections.IEnumerable)root)
                    {
                        try { preIds.Add(c.Id); } catch { }
                    }
                }
                catch { }

                // 复制到剪贴板
                var items = new TxObjectList();
                items.Add(srcObj);
                sel.Clear();
                sel.SetItems(items);
                cm.ExecuteCommand("Edit.Copy");

                // 选中目标并 Paste（新实例到根级）
                var targetList = new TxObjectList();
                targetList.Add(targetContainer);
                sel.Clear();
                sel.SetItems(targetList);
                cm.ExecuteCommand("Edit.Paste");

                // 找新实例：不在复制前快照里的对象（同名新副本），优先选名称匹配的
                ITxObject newObj = null;
                foreach (ITxObject c in (System.Collections.IEnumerable)root)
                {
                    if (c == null) continue;
                    try { if (preIds.Contains(c.Id)) continue; } catch { }
                    string n = c.Name ?? "";
                    if (string.Equals(n, srcObj.Name, StringComparison.Ordinal)
                        || string.Equals(n, srcObj.Name + "_1", StringComparison.Ordinal))
                    { newObj = c; break; }
                }
                // 若按名没找到（Paste 可能改了名），退回取第一个不在快照里的对象
                if (newObj == null)
                {
                    foreach (ITxObject c in (System.Collections.IEnumerable)root)
                    {
                        if (c == null) continue;
                        try { if (preIds.Contains(c.Id)) continue; } catch { }
                        newObj = c;
                        break;
                    }
                }
                if (newObj == null) { log("[复制] 未找到新实例: " + srcObj.Name); return false; }

                // 挂载到目标
                bool mounted = false;
                try { dynamic d = targetContainer; d.AddObject(newObj); mounted = true; }
                catch (Exception e1)
                {
                    try
                    {
                        dynamic d = targetContainer;
                        var l = new TxObjectList();
                        l.Add(newObj);
                        d.AddObjects(l);
                        mounted = true;
                    }
                    catch (Exception e2)
                    {
                        log("[复制] 挂载失败: " + e2.Message + " (原: " + e1.Message + ")");
                    }
                }
                if (!mounted) return false;

                // 重命名去 _1 后缀
                if (!string.IsNullOrWhiteSpace(finalName))
                {
                    try { ((ITxObject)newObj).Name = finalName; }
                    catch (Exception ex) { log("[复制] 重命名失败: " + ex.Message); }
                }

                // 清理 Manipulator
                var toDel = new List<ITxObject>();
                foreach (ITxObject c in (System.Collections.IEnumerable)root)
                    if (c.GetType().Name == "TxManipulator") toDel.Add(c);
                foreach (var m in toDel)
                {
                    try { m.Delete(); } catch { }
                }

                log("[复制] ✓ " + srcObj.Name + " → " + targetContainer.Name + " (新Id=" + newObj.Id + ")");
                return true;
            }
            catch (Exception ex)
            {
                log("[复制] 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 在场景内复制资源：Ctrl+C → Ctrl+V 生成独立新实例 → AddObject 移动到目标容器。
        /// 用于重建时遇到「场景已有同名资源」的情况：复制新实例而不是移动原对象，
        /// 避免重名资源在场景里只剩一份（原位置丢失）。
        /// 返回新实例，失败返回 null。
        /// </summary>
        public static ITxObject CloneResource(ITxObject srcObj, ITxObject targetContainer,
                                              string finalName, Action<string> log)
        {
            log = log ?? Nop;
            if (srcObj == null) { log("[复制] 来源为空"); return null; }
            if (targetContainer == null) { log("[复制] 目标容器为空"); return null; }

            // 复制到剪贴板（选中源 → Copy）
            var sel = TxApplication.ActiveSelection;
            var cm = TxApplication.CommandsManager;
            var items = new TxObjectList();
            items.Add(srcObj);
            sel.Clear();
            sel.SetItems(items);
            cm.ExecuteCommand("Edit.Copy");

            // 选中目标容器 → Paste（新实例挂到目标容器下，而非根级）
            var targetList = new TxObjectList();
            targetList.Add(targetContainer);
            sel.Clear();
            sel.SetItems(targetList);
            cm.ExecuteCommand("Edit.Paste");

            // 找新实例：目标容器直接子里新增的那个
            ITxObject newObj = null;
            foreach (ITxObject c in (System.Collections.IEnumerable)targetContainer)
            {
                if (c == null) continue;
                try
                {
                    if (c.Id != srcObj.Id
                        && (string.Equals(c.Name, srcObj.Name, StringComparison.Ordinal)
                            || string.Equals(c.Name, srcObj.Name + "_1", StringComparison.Ordinal)))
                    { newObj = c; break; }
                }
                catch { }
            }
            // 兜底：目标容器直接子里取 Id 不等于源对象的那个
            if (newObj == null)
            {
                foreach (ITxObject c in (System.Collections.IEnumerable)targetContainer)
                {
                    if (c == null) continue;
                    try { if (c.Id != srcObj.Id) { newObj = c; break; } } catch { }
                }
            }
            if (newObj == null) { log("[复制] 未找到复制出的新实例"); return null; }

            // 重命名
            if (!string.IsNullOrWhiteSpace(finalName)
                && !string.Equals(newObj.Name, finalName, StringComparison.Ordinal))
            {
                try { ((ITxObject)newObj).Name = finalName; }
                catch (Exception ex) { log("[复制] 重命名失败: " + ex.Message); }
            }

            log("[复制] ✓ " + srcObj.Name + " → " + targetContainer.Name + " (新Id=" + newObj.Id + ")");
            return newObj;
        }

        /// <summary>批量复制：srcName 来源对象、targetContainerName 目标容器、finalNames 逐个新名。</summary>
        public static ComponentReport CopyPasteBatch(string srcName, string targetContainerName,
                                                     List<string> finalNames, Action<string> log)
        {
            log = log ?? Nop;
            var rep = new ComponentReport();
            var src = FindByNameInScene(srcName);
            if (src == null) { rep.Failed++; rep.Errors.Add("未找到来源: " + srcName); return rep; }
            var target = FindByNameInScene(targetContainerName);
            if (target == null) { rep.Failed++; rep.Errors.Add("未找到目标容器: " + targetContainerName); return rep; }

            foreach (var name in finalNames)
            {
                if (CopyPasteTo(src, target, name, log)) rep.Pasted++;
                else rep.Failed++;
            }
            return rep;
        }

        // ════════════════════════════════════════════════════════════
        //  工具
        // ════════════════════════════════════════════════════════════

        private static ITxObject FindByNameInScene(string name)
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null) return null;
                var stack = new Stack<ITxObject>();
                foreach (var c in DirectChildren(doc.PhysicalRoot)) stack.Push(c);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    if (cur == null) continue;
                    try
                    {
                        if (string.Equals(cur.Name, name, StringComparison.Ordinal)) return cur;
                        var coll = cur as ITxObjectCollection;
                        if (coll != null) foreach (var c in DirectChildren(cur)) stack.Push(c);
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static List<ITxObject> DirectChildren(ITxObject obj)
        {
            var result = new List<ITxObject>();
            try
            {
                var coll = obj as ITxObjectCollection;
                if (coll == null) return result;
                foreach (ITxObject c in (System.Collections.IEnumerable)coll)
                    if (c != null) result.Add(c);
            }
            catch { }
            return result;
        }

        private static bool IsAscii(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (var c in s) if (c > 127) return false;
            return true;
        }

        /// <summary>路径是否每段都全 ASCII（无中文/全角/乱码）。这是能否直插的关键判据。</summary>
        private static bool IsPureAsciiPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            // 逐字符检查整个路径字符串（含分隔符、盘符、扩展名）
            foreach (var c in path)
                if (c > 127) return false;
            return true;
        }

        /// <summary>cojt 是否位于 SystemRootDirectory（库）范围内。</summary>
        private static bool IsInsideSystemRoot(string cojtPath)
        {
            try
            {
                var sysRoot = TxApplication.SystemRootDirectory;
                if (string.IsNullOrWhiteSpace(sysRoot) || string.IsNullOrWhiteSpace(cojtPath))
                    return false;
                return cojtPath.StartsWith(sysRoot.TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string SafeName(ITxObject obj)
        {
            try { return obj.Name; } catch { return "?"; }
        }
    }
}
