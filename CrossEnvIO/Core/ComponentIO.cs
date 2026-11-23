// ComponentIO.cs  --  C# 8.0
// 从最终库位置插入；已验证 AppFunc 版本的中文路径使用调用范围内的兼容处理。

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
        //  从最终接收库文件夹插入 cojt
        // ════════════════════════════════════════════════════════════

        /// <summary>从最终库位置插入模型，保留 SDK 名称；中文路径仅使用已验证的兼容配置。</summary>
        public static bool InsertCojt(string cojtPath, string finalName, string prototypeFrom,
                                      string libraryDir, double x, double y, double z,
                                      Action<string> log, out string resultSummary)
        {
            ITxObject inserted;
            return InsertCojt(cojtPath, finalName, prototypeFrom, libraryDir, x, y, z,
                              log, out resultSummary, out inserted);
        }

        public static bool InsertCojt(string cojtPath, string finalName, string prototypeFrom,
                                      string libraryDir, double x, double y, double z,
                                      Action<string> log, out string resultSummary, out ITxObject insertedObject)
        {
            return InsertCojt(cojtPath, finalName, prototypeFrom, libraryDir, x, y, z, log, out resultSummary, out insertedObject, false);
        }

        // allowRoot: 允许 COJT 直接放在库根（配方快速验证用），默认沿用"必须放在库内子文件夹"的规则。
        public static bool InsertCojt(string cojtPath, string finalName, string prototypeFrom,
                                      string libraryDir, double x, double y, double z,
                                      Action<string> log, out string resultSummary, out ITxObject insertedObject,
                                      bool allowRoot)
        {
            insertedObject = null;
            using (var undoScope = TxTools.Common.SceneUndoScope.Begin("插入 cojt 组件"))
            {
                log = log ?? Nop;
                resultSummary = "";
                var sb = new StringBuilder();

                var doc = TxApplication.ActiveDocument;
                if (doc == null) { sb.AppendLine("Error: 无活动文档"); resultSummary = sb.ToString(); return false; }
                var root = doc.PhysicalRoot;
                if (root == null) { sb.AppendLine("Error: 无 PhysicalRoot"); resultSummary = sb.ToString(); return false; }
                try { cojtPath=NormalizeStoragePath(cojtPath); }
                catch(Exception ex) { resultSummary="无法解析组件真实路径: "+ex.Message; return false; }
                if (string.IsNullOrWhiteSpace(cojtPath) || !Directory.Exists(cojtPath))
                { sb.AppendLine("Error: cojt 目录不存在: " + cojtPath); resultSummary = sb.ToString(); return false; }

                ITxObject candidate=null;
                try
                {
                    string libraryRoot=TxApplication.SystemRootDirectory;
                    bool ok=LibraryInsertion.AtDestination(cojtPath,libraryRoot,
                        sdkPath=>DirectInsert(root,sdkPath,cojtPath,prototypeFrom,x,y,z,true,sb,out candidate),
                        message=>sb.AppendLine(message),allowRoot);
                    insertedObject=candidate;
                    if(ok)
                    {
                        // The compatibility scope is already closed before final
                        // reference verification, hierarchy updates or scene refresh.
                        LibraryPersistence.Ensure(candidate,cojtPath,cojtPath,message=>sb.AppendLine(message));
                        RecordInsertedPath(cojtPath);
                    }
                    resultSummary = sb.ToString();
                    return ok;
                }
                catch (AppFuncCompatibilityException ex)
                {
                    insertedObject=candidate;
                    throw new NativeLibraryException("中文路径兼容插入未完成："+ex.Message+"\n模型路径："+cojtPath,ex,candidate==null?0:1);
                }
                catch (Exception ex)
                {
                    insertedObject=candidate;
                    sb.AppendLine("Error: " + ex.GetType().Name + ": " + ex.Message);
                    resultSummary = sb.ToString();
                    return false;
                }

            }
        }

        internal sealed class CojtLoadResult
        {
            internal ITxObject Instance;
            internal Exception Error;
            internal List<ITxObject> CreatedBeforeError;
        }

        // PS 2402 exposes single-component loading. Submit the whole batch on
        // the document thread before checking any of the returned instances.
        internal static CojtLoadResult[] LoadCojts(IReadOnlyList<string> paths, string libraryRoot, Action<int> beforeLoad,
            bool allowRoot = false)
        {
            var root = TxApplication.ActiveDocument?.PhysicalRoot;
            if (root == null) throw new InvalidOperationException("无活动的 PhysicalRoot。");

            var results = new CojtLoadResult[paths.Count];
            var created = new List<ITxObject>();
            TxObjectRoot_ItemCreatedEventHandler onCreated = (sender, args) =>
            {
                if (args.Item is ITxComponent component) created.Add(component);
            };
            root.ItemCreated += onCreated;
            try
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    beforeLoad(i);
                    created.Clear();
                    var result = results[i] = new CojtLoadResult();
                    string path = paths[i];
                    try
                    {
                        LibraryInsertion.AtDestination(path, libraryRoot, sdkPath =>
                        {
                            var data = new TxInsertComponentCreationData(string.Empty, sdkPath)
                            {
                                AbsoluteLocation = new TxTransformation()
                            };
                            return result.Instance = root.InsertComponent(data);
                        }, null, allowRoot);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (AppFuncCompatibilityException error)
                    {
                        throw new NativeLibraryException("插入环境不可用：" + error.Message + "\n模型路径：" + path, error);
                    }
                    catch (Exception error) { result.Error = error; }
                    if (result.Instance == null && created.Count != 0)
                        result.CreatedBeforeError = new List<ITxObject>(created);
                }
            }
            finally { root.ItemCreated -= onCreated; }
            return results;
        }

        internal static void VerifyLoadedCojt(CojtLoadResult result, string path, string libraryRoot)
        {
            if (result.Instance == null)
            {
                // Recover an unambiguous partial insertion from the SDK event.
                // An ambiguous or failed call is never resubmitted here.
                string detail = result.Error?.GetBaseException().Message ?? "插入后未取得实例";
                if (result.CreatedBeforeError != null)
                {
                    var matches = result.CreatedBeforeError
                        .Where(item => item.IsValid() && LibraryPersistence.ReferenceIssue(item, path, libraryRoot) == null)
                        .GroupBy(item => item.Id)
                        .Select(items => items.First())
                        .Take(2)
                        .ToArray();
                    if (matches.Length == 1) result.Instance = matches[0];
                    else detail += "；PS 报告有新增组件，库引用无法唯一匹配，请核对场景。";
                }
                throw new IOException(detail);
            }
            if (result.Error != null) throw new IOException(result.Error.GetBaseException().Message);
            LibraryPersistence.Ensure(result.Instance, path, path, null);
            RecordInsertedPath(path);
        }

        // Paste 会复制实例（新 Id）。副本是否仍持有正式库引用不由 Paste 保证，
        // 重建原生树后必须重新核验，避免树里层级正确但模型引用已丢。
        internal static void VerifyPastedCojt(ITxObject instance, string path, string libraryRoot)
        {
            if (instance == null || !instance.IsValid()) throw new IOException("Paste 后未取得有效实例");
            LibraryPersistence.Ensure(instance, path, path, null);
        }

        /// <summary>
        /// 直插：直接 InsertComponent。库内无原型即可；库外/零件等带原型更稳。
        /// 统一逻辑：先尝试无原型；失败则从 cojt TuneData 的 ExternalID 从库注册表取原型重试。
        /// 资源与零件用同一套插入逻辑。
        /// </summary>
        private static bool DirectInsert(TxPhysicalRoot root, string cojtPath, string documentPath,
                                         string prototypeFrom, double x, double y, double z,
                                         bool insideSystemRoot, StringBuilder sb, out ITxObject insertedObject)
        {
            insertedObject = null;
            try
            {
                // 尝试 1：无原型（库内实测可成功；库外若给了原型来源则带）
                // An empty SDK name preserves the name embedded in the model document.
                // Scene display names and cojt filenames are independent identities.
                var data = new TxInsertComponentCreationData(string.Empty, cojtPath);
                if (!insideSystemRoot)
                {
                    var proto = ResolvePrototype(prototypeFrom, sb);
                    if (proto != null) data.Prototype = proto;
                }
                var loc = new TxTransformation();
                loc.Translation = new TxVector(x, y, z);
                data.AbsoluteLocation = loc;

                var beforeIds=new HashSet<string>(root.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>().Select(obj=>obj.Id));
                ITxComponent comp;
                try { comp = root.InsertComponent(data); }
                catch (Exception ex)
                {
                    // 尝试 2：失败 → 从 cojt TuneData 的 ExternalID 取库注册原型，带原型重试
                    if(root.GetAllDescendants(new TxTypeFilter(typeof(ITxObject))).Cast<ITxObject>().Any(obj=>!beforeIds.Contains(obj.Id)))
                    {sb.AppendLine("SDK 插入抛出异常但场景已有新增对象，已停止重试；请核对该实例。原因："+ex.Message);return false;}
                    var proto = ResolvePrototypeFromTuneData(documentPath, sb);
                    if (proto == null)
                    {
                        sb.AppendLine("直插失败: " + ex.GetType().Name + ": " + ex.Message
                                      + "（无原型可用）");
                        return false;
                    }
                    sb.AppendLine("直插无原型失败，改用 TuneData 库注册原型重试");
                    var data2 = new TxInsertComponentCreationData(string.Empty, cojtPath);
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
                insertedObject = comp as ITxObject;

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
                // A TuneData file can contain several projects. Read only the active project's entry.
                string extId = new Tecnomatix.Engineering.Utilities.TxTunePmDocumentManager()
                    .LoadPrototypeDataFromDocument(cojtPath)?.ExternalId;
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


        internal static string NormalizeStoragePath(string path)
        {
            return StoragePaths.Normalize(path,TxApplication.SystemRootDirectory,SnapshotInsertedPaths(),
                Path.Combine(TxTools.Agent.Core.TxToolsTemp.RootPath,"CrossEnvIO","Links"));
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
        public static bool FixPszAfterInsert(Action<string> log)
        {
            try { SavePatchReload(SnapshotInsertedPaths(),log); return true; }
            catch(Exception ex) { (log??Nop)(ex.ToString()); return false; }
        }
        internal static PszSaveReloadResult SavePatchReload(IEnumerable<string> paths,Action<string> log,Action<string> beforeReload=null)
        {
            if(TxTools.Common.SceneUndoScope.HasActiveTransaction)
                throw new InvalidOperationException("场景事务尚未结束，不能保存/重载工程。");
            var doc=TxApplication.ActiveDocument??throw new InvalidOperationException("没有活动工程");
            var provider=doc.PlatformGlobalServicesProvider??throw new InvalidOperationException("没有工程保存/重载服务");
            string file=doc.FinalDestination,root=TxApplication.SystemRootDirectory;
            return PszSaveReload.Run(file,root,paths,()=>provider.SaveDataToFile(file),()=>provider.LoadDataFromFile(file),log,beforeReload);
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
            using (var scope = TxTools.Common.SceneUndoScope.Begin("复制组件"))
            {
                try { return CloneResource(srcObj, targetContainer, finalName, log) != null; }
                catch (Exception ex) { (log ?? Nop)("[复制] 失败: " + ex.Message); return false; }
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
            var previousSelection = sel.GetItems();
            var root = TxApplication.ActiveDocument.PhysicalRoot;
            // Paste can use the selected container or the physical root. Snapshot
            // these two direct child sets instead of scanning the entire scene per copy.
            Func<List<ITxObject>> candidatesInPasteScope=()=>SceneAccess.Children(targetContainer)
                .Concat(targetContainer.Id==root.Id?Enumerable.Empty<ITxObject>():SceneAccess.Children(root))
                .Where(obj=>obj!=null&&obj.GetType()==srcObj.GetType()).GroupBy(obj=>obj.Id).Select(g=>g.First()).ToList();
            var beforeIds = new HashSet<string>(candidatesInPasteScope().Select(obj=>obj.Id));
            try
            {
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
                var candidates = candidatesInPasteScope().Where(c=>!beforeIds.Contains(c.Id)).ToList();
                // 只接受一个新增、同类型、与源同名或带 PS 自动后缀的实例。
                newObj = CopyIdentity.FindNew(srcObj, beforeIds, candidates,
                    obj => obj.Id, obj => obj.Name, obj => obj.GetType());
                if (newObj == null)
                {
                    throw new NativeLibraryException("复制粘贴后无法唯一识别新增实例，已停止；请核对或撤销本批次。",null,candidates.Count);
                }
                if (!DirectChildIds(targetContainer).Contains(newObj.Id))
                {
                    try
                    {
                        var container=targetContainer as ITxObjectCollection;
                        if(container==null||!container.CanBeAdded(newObj))throw new InvalidOperationException("目标不支持挂载此组件");
                        container.AddObject(newObj);
                    }
                    catch (Exception ex) { throw new NativeLibraryException("复制已生成实例，但挂载失败："+newObj.Name+" / "+ex.Message,ex,1); }
                }

                // 重命名
                if (!string.IsNullOrWhiteSpace(finalName)
                    && !string.Equals(newObj.Name, finalName, StringComparison.Ordinal))
                {
                    try { ((ITxObject)newObj).Name = finalName; }
                    catch (Exception ex) { throw new NativeLibraryException("复制已生成实例，但命名失败："+newObj.Name+" / "+ex.Message,ex,1); }
                }

                log("[复制] ✓ " + srcObj.Name + " → " + targetContainer.Name + " (新Id=" + newObj.Id + ")");
                return newObj;
            }
            catch(NativeLibraryException){throw;}
            catch(Exception ex)
            {
                int added=candidatesInPasteScope().Count(obj=>!beforeIds.Contains(obj.Id));
                throw new NativeLibraryException("复制粘贴未完成，已停止后续实例；新增组件 "+added+" 个。原因："+ex.Message,ex,added);
            }
            finally { try { sel.Clear(); sel.SetItems(previousSelection); } catch { } }
        }

        private static HashSet<string> DirectChildIds(ITxObject container)
        {
            var result = new HashSet<string>();
            foreach (ITxObject obj in SceneAccess.Children(container))
                if (obj != null) result.Add(obj.Id);
            return result;
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

        /// <summary>检查活动工程的库存储路径；无法完整检查时保守地视为已引用。</summary>
        public static bool IsPathReferencedByScene(string directory)
        {
            try
            {
                var doc = TxApplication.ActiveDocument;
                if (doc == null || doc.PhysicalRoot == null) return true;
                string root = Path.GetFullPath(directory).TrimEnd('\\', '/');
                var all = doc.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxObject)));
                foreach (ITxObject obj in all)
                {
                    var storable = obj as ITxStorable;
                    if (storable == null) continue;
                    var storage = storable.StorageObject as TxLibraryStorage;
                    if (storage == null) continue;
                    string full = storage.FullPath;
                    if (string.IsNullOrWhiteSpace(full)) continue;
                    full = NormalizeStoragePath(full);
                    if (string.Equals(root, full, StringComparison.OrdinalIgnoreCase)
                        || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return false;
            }
            catch { return true; }
        }

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
                foreach (ITxObject c in SceneAccess.Children(coll))
                    if (c != null) result.Add(c);
            }
            catch { }
            return result;
        }


        private static string SafeName(ITxObject obj)
        {
            try { return obj.Name; } catch { return "?"; }
        }
    }
}
