using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using INFITF;
using ProductStructureTypeLib;
using Tecnomatix.Engineering;
using SysFile = System.IO.File;

namespace TxTools.ExportByColor
{
    public sealed class ColorGroup
    {
        public byte R, G, B;
        public int Surface;
        public List<float[]> Tris = new List<float[]>();   // 每条 float[9] = 世界坐标三角形
    }

    public sealed class DeviceData
    {
        public string Name;
        public List<string> Path = new List<string>();   // PS 目录树路径(父级容器名,不含根)
        public List<ColorGroup> Colors = new List<ColorGroup>();
    }

    public class ExportByColorService
    {
        private volatile bool _cancelRequested;
        private int _running;
        public bool IsRunning { get { return Volatile.Read(ref _running) != 0; } }
        public void RequestStop() { _cancelRequested = true; }
        private void CheckCancelled() { if (_cancelRequested) throw new OperationCanceledException("导出已停止"); }
        private readonly SynchronizationContext _psCtx;
        private INFITF.Application _catia;
        private ProductDocument _productDoc;                        // 导入目标文档（RunAsync 开头确定）

        public ExportByColorService(SynchronizationContext psCtx)
        {
            _psCtx = psCtx ?? new SynchronizationContext();
        }

        public void InvokeOnPs(Action action) { OnPs(action); }

        private void OnPs(Action action)
        {
            Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { action(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) throw ex;
        }

        private T OnPs<T>(Func<T> func)
        {
            T val = default(T); Exception ex = null;
            _psCtx.Send(delegate(object s)
            {
                try { val = func(); } catch (Exception e) { ex = e; }
            }, null);
            if (ex != null) throw ex;
            return val;
        }

        // ── 枚举场景所有可见设备（保留可见性过滤）───────────────────────
        public List<ITxObject> EnumerateVisibleDevices(Action<string> onLog)
        {
            return OnPs(delegate()
            {
                var list = new List<ITxObject>();
                var doc = TxApplication.ActiveDocument;
                if (doc == null)
                {
                    SafeLog(onLog, "[PS] 无活动文档");
                    return list;
                }
                foreach (var rootName in new[] { "PhysicalRoot", "ResourceRoot", "ComponentRoot" })
                {
                    object root = null;
                    try { root = TryGetProp(doc, rootName); } catch { }
                    if (root == null) continue;
                    var rootObj = root as ITxObject;
                    foreach (var d in ExpandPicked(rootObj, onLog))
                        list.Add(d);
                }
                var visible = new List<ITxObject>();
                foreach (var d in list)
                    if (IsVisible(d)) visible.Add(d);
                SafeLog(onLog, "[PS] 场景资源(展开后) " + list.Count + " 个, 可见 " + visible.Count + " 个");
                return visible;
            });
        }

        private static object TryGetProp(object o, string name)
        {
            if (o == null) return null;
            var pi = o.GetType().GetProperty(name);
            return pi != null ? pi.GetValue(o, null) : null;
        }

        private static bool IsVisible(ITxObject o)
        {
            var d = o as ITxDisplayableObject;
            if (d == null) return true;
            try { return d.Visibility != TxDisplayableObjectVisibility.None; }
            catch { return true; }
        }

        private sealed class EncodedDevice
        {
            public DeviceData Device;
            public string Path;
            public Exception Error;
        }

        // PS reads stay on its synchronization context. Only detached mesh data reaches workers.
        // At most two devices are retained, including the device currently being collected.
        public void RunAsync(List<ITxObject> picked, string originName, string format, string outputRoot, bool mergeMeshes,
                             Action<string> onLog, Action<bool, string> onComplete, bool showEdges = false)
        {
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                throw new InvalidOperationException("上一次导出尚未结束");
            _cancelRequested = false;
            var thread = new Thread(() =>
            {
                var pending = new Queue<Task<EncodedDevice>>();
                int ok = 0, failed = 0;
                string output = null;
                MeshExport.Archive merged = null;
                string selectionName = OnPs(() => picked.Count == 1 ? picked[0].Name :
                    (picked.Count > 0 ? picked[0].Name + "_等" + picked.Count + "个资源" : "合并设备"));
                var combinedCgr = new DeviceData { Name = selectionName };
                try
                {
                    if (format != "CGR" && format != "STL" && format != "OBJ" && format != "PLY" && format != "FBX" && format != "FBX_BINARY" && format != "FBX_ASCII")
                        throw new ArgumentException("不支持的网格格式");
                    if (format == "CGR")
                    {
                        string error;
                        if (!Connect(out error)) throw new InvalidOperationException(error);
                        EnsureProductDocument(onLog);
                    }
                    output = Path.Combine(outputRoot ?? Path.GetTempPath(), "TxTools_Export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(output);
                    var uiLog = onLog;
                    var logGate = new object();
                    string logFile = Path.Combine(output, "export.log");
                    onLog = message =>
                    {
                        lock (logGate)
                            SysFile.AppendAllText(logFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine, Encoding.UTF8);
                        // Keep native-call checkpoints on disk, without flooding the UI.
                        if (message.StartsWith("[几何颜色]") || message.StartsWith("[Detailed 开始]") ||
                            message.StartsWith("[Detailed 完成]") || message.StartsWith("[几何读取开始]") ||
                            message.StartsWith("[几何读取完成]")) return;
                        if (uiLog != null) uiLog(message);
                    };
                    SafeLog(onLog, "[日志] 详细记录：" + logFile);
                    var devices = OnPs(() =>
                    {
                        var result = new List<ITxObject>();
                        var keys = new HashSet<string>();
                        foreach (var item in picked)
                            foreach (var device in ExpandPicked(item, onLog))
                                if (keys.Add(ObjKey(device))) result.Add(device);
                        return result;
                    });
                    int workers = 1;
                    SafeLog(onLog, "[导出] " + devices.Count + " 个设备；逐设备读取、编码与添加，降低峰值内存；目录 " + output);
                    var names = new ExportNames();
                    if (mergeMeshes && format != "CGR") merged = new MeshExport.Archive(output);
                    foreach (var device in devices)
                    {
                        CheckCancelled();
                        if (pending.Count >= workers) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, onLog, ref ok, ref failed);
                        string name = names.Next(OnPs(() => device.Name));
                        try
                        {
                            var data = CollectDeviceGroups(new List<ITxObject> { device }, originName, onLog, true,
                                merged == null ? null : new Action<DeviceData>(batch =>
                                {
                                    long batchTriangles = 0;
                                    foreach (var group in batch.Colors) batchTriangles += group.Tris.Count;
                                    if (batchTriangles == 0) return;
                                    merged.Add(batch, name);
                                    SafeLog(onLog, "[合并缓存] " + name + "：批次 " + batchTriangles + " 面；已落盘");
                                    batch.Colors.Clear();
                                }));
                            foreach (var dd in data)
                            {
                                long triangleCount = 0;
                                foreach (var group in dd.Colors) triangleCount += group.Tris.Count;
                                if (triangleCount == 0) continue;
                                if (mergeMeshes && format == "CGR")
                                {
                                    combinedCgr.Colors.AddRange(dd.Colors);
                                    dd.Colors.Clear();
                                    continue;
                                }
                                if (merged != null)
                                {
                                    try { merged.Add(dd, name); SafeLog(onLog, "[合并缓存] " + name + "：" + triangleCount + " 面"); }
                                    finally { dd.Colors.Clear(); }
                                    continue;
                                }
                                // Large devices run alone; avoid multiplying the topology workspace.
                                if (triangleCount > 500000)
                                    while (pending.Count > 0) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, onLog, ref ok, ref failed);
                                pending.Enqueue(Task.Run(() =>
                                {
                                    var result = new EncodedDevice { Device = dd };
                                    try
                                    {
                                        CheckCancelled();
                                        if (format == "CGR") result.Path = BuildCgr(dd.Colors, name, output, message => SafeLog(onLog, "[" + name + "] " + message), showEdges);
                                        else result.Path = MeshExport.Write(dd, name, output, format, originName);
                                    }
                                    catch (Exception ex) { result.Error = ex; }
                                    finally { dd.Colors.Clear(); }
                                    return result;
                                }));
                                if (triangleCount > 500000) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, onLog, ref ok, ref failed);
                            }
                        }
                        catch (Exception ex) { failed++; SafeLog(onLog, "[读取失败] " + name + ": " + ex.Message); }
                    }
                    while (pending.Count > 0) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, onLog, ref ok, ref failed);
                    if (mergeMeshes && failed > 0)
                        throw new InvalidOperationException("有 " + failed + " 个设备读取失败，停止发布合并文件，避免将不完整布局作为完成结果");
                    CheckCancelled();
                    if (combinedCgr.Colors.Count > 0)
                    {
                        var encoded = new EncodedDevice { Device = combinedCgr };
                        try { encoded.Path = BuildCgr(combinedCgr.Colors, names.Next(combinedCgr.Name), output, onLog, showEdges); }
                        catch (Exception ex) { encoded.Error = ex; }
                        finally { combinedCgr.Colors.Clear(); }
                        FinishExport(encoded, format, onLog, ref ok, ref failed);
                    }
                    CheckCancelled();
                    if (merged != null)
                    {
                        string mergedName = names.Next(selectionName);
                        SafeLog(onLog, "[合并写入] 格式=" + format + "；设备=" + merged.Devices + "；二进制 FBX 使用 7500/64 位偏移");
                        string mergedPath = merged.Finish(output, mergedName, format, originName);
                        ok = merged.Devices;
                        SafeLog(onLog, "[合并完成] " + ok + " 个设备 → " + mergedPath);
                    }
                    CheckCancelled();
                    Interlocked.Exchange(ref _running, 0);
                    SafeComplete(onComplete, ok > 0 && failed == 0, ok + " 个设备完成，" + failed + " 个失败。文件: " + output + (format == "CGR" ? "；CATIA 添加调用已返回，显示仍需验证。" : ""));
                }
                catch (Exception ex)
                {
                    // Observe all workers before reporting completion; no orphan writes after a failed run.
                    while (pending.Count > 0) { try { pending.Dequeue().GetAwaiter().GetResult(); } catch { } }
                    SafeLog(onLog, "[详细错误] " + ex.ToString());
                    if (merged != null) { try { SafeLog(onLog, "[恢复缓存] " + merged.PreserveForRecovery()); } catch (Exception recoveryError) { SafeLog(onLog, "[恢复缓存] " + recoveryError.Message); } }
                    if (output != null) { try { SysFile.WriteAllText(Path.Combine(output, "export-error.txt"), ex.ToString(), Encoding.UTF8); } catch { } }
                    Interlocked.Exchange(ref _running, 0);
                    SafeComplete(onComplete, false, (_cancelRequested ? "已停止；已完成文件保留，当前编码结束后不再导入。" : ex.Message) + "；详细错误及恢复缓存: " + output);
                }
                finally { Interlocked.Exchange(ref _running, 0); if (merged != null) { try { merged.Dispose(); } catch (Exception ex) { SafeLog(onLog, "[缓存清理] " + ex.Message); } } }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void FinishExport(EncodedDevice result, string format, Action<string> onLog, ref int ok, ref int failed)
        {
            CheckCancelled();
            try
            {
                if (result.Error != null) throw result.Error;
                if (format == "CGR")
                {
                    Products current = _productDoc.Product.Products;
                    foreach (string segment in result.Device.Path)
                    {
                        string partName = SafeFileName(segment);
                        int child = FindChildIndex(current, partName);
                        if (child <= 0)
                        {
                            string unique = GetUniqueChildName(current, partName);
                            Product node = current.AddNewProduct("");
                            node.set_PartNumber(unique);
                            child = current.Count;
                        }
                        current = current.Item(child).Products;
                    }
                    int before = current.Count;
                    string deviceName = GetUniqueChildName(current, SafeFileName(result.Device.Name));
                    current.AddComponentsFromFiles(new object[] { result.Path }, "All");
                    if (current.Count != before + 1) throw new InvalidOperationException("CATIA 未添加预期的单个组件");
                    current.Item(before + 1).set_PartNumber(deviceName);
                }
                ok++;
                SafeLog(onLog, "[" + format + "] " + result.Device.Name + " → " + result.Path);
            }
            catch (Exception ex) { failed++; SafeLog(onLog, "[设备失败] " + result.Device.Name + ": " + ex.ToString()); }
        }

        private static string BuildCgr(List<ColorGroup> groups, string name, string tmpDir, Action<string> onLog, bool showEdges = false)
        {
            var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
            // Keep color groups' vertex identity separate: touching independent solids must not
            // become nonmanifold through coordinate welding. All groups still share ONE file.
            int surfaceSequence = 0;
            foreach(var group in groups)
            {
                int surface = ++surfaceSequence;
                var map=new Dictionary<Tuple<float,float,float>,int>();
                foreach(var t in group.Tris)
                {
                    if(t==null||t.Length!=9) throw new ArgumentException("三角形数据必须为 9 个坐标值");
                    var ids=new int[3];
                    for(int k=0;k<3;k++)
                    {
                        var key=Tuple.Create(t[3*k],t[3*k+1],t[3*k+2]);int id;
                        if(!map.TryGetValue(key,out id)) { id=vertices.Count;map.Add(key,id);vertices.Add(new[]{key.Item1,key.Item2,key.Item3}); } ids[k]=id;
                    }
                    faces.Add(new CgrWriter.Face{Idx=ids,R=group.R,G=group.G,B=group.B,Surface=surface});
                }
            }
            if(faces.Count==0) throw new InvalidOperationException("设备无有效三角面");
            string path=Path.Combine(tmpDir,name+".cgr");
            CgrWriter.BuildFile(vertices,faces,path,progress:onLog,writeEdges:showEdges);
            return path;
        }

        /// <summary>
        /// 从 PS 中已加载的单个资源采集几何并生成 CGR。调用方可在其它功能中复用，
        /// 例如焊枪所在 JT 目录尚未提供 CGR 的情况。
        /// </summary>
        public string GenerateCgrForObject(ITxObject source, string outputPath, Action<string> onLog)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (string.IsNullOrEmpty(outputPath)) throw new ArgumentNullException("outputPath");

            string outputDir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDir)) throw new ArgumentException("输出路径必须包含目录", "outputPath");
            Directory.CreateDirectory(outputDir);

            TxTransformation toolInverse = null;
            var locatableSource = source as ITxLocatableObject;
            if (locatableSource != null)
            {
                try { toolInverse = locatableSource.AbsoluteLocation.Inverse; }
                catch (Exception ex) { SafeLog(onLog, "[CGR] 工具自身坐标读取失败，将使用世界坐标：" + ex.Message); }
            }
            var devices = CollectDeviceGroups(new List<ITxObject> { source }, null, onLog, true, null, toolInverse);
            if (devices == null || devices.Count == 0)
                throw new InvalidOperationException("工具中未读取到可生成 CGR 的几何");

            var groups = new List<ColorGroup>();
            foreach (var device in devices) groups.AddRange(device.Colors);
            if (groups.Count == 0) throw new InvalidOperationException("工具中未读取到有效三角面");

            string name = Path.GetFileNameWithoutExtension(outputPath);
            string generated = BuildCgr(groups, name, outputDir, onLog);
            if (!string.Equals(generated, outputPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("生成的 CGR 路径与请求路径不一致：" + generated);
            return generated;
        }

        // ── 展开：叶子设备直接加入；纯容器递归展开；静态资源兜底整体导出 ──
        // 判定逻辑（实测验证）：
        //   · 叶子设备 = 实现 ITxStorable（有 StorageObject）的对象
        //     (TxComponent/TxServoGun/TxRobot 等)——几何藏在 StorageObject 内部，
        //     需 Reload 解锁后由 EnumDeviceGeometries 枚举；它们大多也是
        //     ITxObjectCollection，若按"容器"递归展开子级，子级几何被 IsDeviceLike
        //     过滤后为空，兜底 HasGeometry(未Reload) 又返回 False → 真设备被丢弃。
        //   · 纯容器 = ITxObjectCollection 且无 ITxStorable
        //     (TxCompoundPart/TxCompoundResource/围栏/底座/柜体/夹具)
        //     → 自身不导出，递归展开子级
        //   · 根对象(TxPhysicalRoot)也实现 ITxStorable，必须排除，只递归不导出
        //   · 几何叶子(ITxGeometry)不单独作为导出单元，由所属对象统一收集
        private static List<ITxObject> ExpandPicked(ITxObject obj, Action<string> onLog)
        {
            var list = new List<ITxObject>();
            if (obj == null) return list;
            if (!IsDeviceLike(obj)) return list;

            // 根对象：只递归展开子级，自身不导出
            if (obj is TxPhysicalRoot)
            {
                foreach (var kid in EnumerateKids(obj))
                    list.AddRange(ExpandPicked(kid, onLog));
                return list;
            }

            // 叶子设备：实现 ITxStorable → 直接加入，不再当容器展开
            if (obj is ITxStorable)
            {
                list.Add(obj);
                return list;
            }

            // 纯容器（无 ITxStorable 的 ITxObjectCollection）→ 递归展开子级
            var coll = obj as ITxObjectCollection;
            if (coll != null)
            {
                foreach (var kid in EnumerateKids(obj))
                    list.AddRange(ExpandPicked(kid, onLog));
                if (list.Count > 0)
                {
                    SafeLog(onLog, "  组合展开: " + obj.Name + " → " + list.Count + " 个单元");
                    return list;
                }

                // 容器无子设备但自身含几何 → 整体导出（静态资源）
                if (HasGeometry(obj))
                {
                    list.Add(obj);
                    SafeLog(onLog, "  静态资源整体导出: " + obj.Name);
                }
                else
                {
                    SafeLog(onLog, "  skip(无几何): " + obj.Name);
                }
                return list;
            }

            // 其他非容器非几何 → 直接加入
            list.Add(obj);
            return list;
        }

        /// <summary>
        /// 枚举对象的设备类直接子级（多路径兜底）。
        /// 路径1：IEnumerable 枚举直接子级；
        /// 路径2：GetAllDescendants(TxNoTypeFilter) 全后代（某些集合不实现 IEnumerable 时兜底，
        ///        例如只有 ITxObjectCollection 的容器）。用 IsDeviceLike 过滤，仅收设备类。
        /// </summary>
        private static List<ITxObject> EnumerateKids(ITxObject obj)
        {
            var kids = new List<ITxObject>();
            if (obj == null) return kids;
            var coll = obj as ITxObjectCollection;
            if (coll == null) return kids;

            // 路径1：IEnumerable 直接子级
            try
            {
                var en = obj as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (object k in en)
                    {
                        var itx = k as ITxObject;
                        if (itx != null && IsDeviceLike(itx)) kids.Add(itx);
                    }
                }
            }
            catch { }

            // 路径2：GetAllDescendants 全后代兜底（防中间层容器不实现 IEnumerable 断链）
            if (kids.Count == 0)
            {
                try
                {
                    var all = coll.GetAllDescendants(new TxNoTypeFilter());
                    if (all != null)
                        foreach (object o in all)
                        {
                            var itx = o as ITxObject;
                            if (itx != null && IsDeviceLike(itx)) kids.Add(itx);
                        }
                }
                catch { }
            }
            return kids;
        }

        /// <summary>
        /// 判断对象是否为可导出的设备/资源（黑名单式）：
        /// 排除操作类(ITxOperation)、焊点、坐标系、相机、注释、标注、零件外观、扫掠体、纯几何叶子。
        /// 注意：不用白名单(ITxDevice 等)，因为围栏/底座/柜体/夹具是 TxCompoundResource，
        /// 不实现 ITxDevice 但含几何、应整体导出——黑名单才能保住它们。
        /// </summary>
        private static bool IsDeviceLike(ITxObject o)
        {
            if (o == null) return false;
            // 几何一律排除：用 ITxGeometry 接口判断，
            // 因为 TxSolid 基类是 TxObjectBase 只实现 ITxGeometry（不是 TxGeometry 子类），
            // 单查 TxGeometry 会漏掉它。
            if (o is ITxGeometry) return false;
            if (o is ITxOperation) return false;
            if (o is TxWeldPoint) return false;
            if (o is TxFrame) return false;
            if (o is TxViewCamera) return false;
            if (o is TxNote) return false;
            if (o is TxLinearDimension) return false;
            if (o is TxPartAppearance) return false;
            if (o is TxSweptVolume) return false;
            return true;
        }

        /// <summary>唯一性键：优先 ITxObject.Id（场景内唯一，形如 3,57,2,1），读取失败回退名称。</summary>
        private static string ObjKey(ITxObject o)
        {
            if (o == null) return "";
            try { var id = o.Id; if (!string.IsNullOrEmpty(id)) return "id:" + id; } catch { }
            return "ref:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>
        /// 沿父容器链向上收集设备所在目录树路径（父级容器名数组，根级容器止步）。
        /// 用于在 CATIA 中按 PS 目录树归类导入的资源。
        /// 需在 PS 主线程调用。
        ///
        /// 关键：ITxObject 没有 Parent 属性（v6.5 文档确认），正确取父是
        ///   o.Collection（返回 ITxObjectCollection，即父容器）；多属性兜底
        ///   OwningObject/Owner/Container 兼容不同 SDK 版本。
        /// </summary>
        private static List<string> GetTreePath(ITxObject dev, Action<string> onLog)
        {
            var path = new List<string>();
            if (dev == null) return path;
            try
            {
                object cur = dev;
                var guard = new HashSet<string>();
                while (cur != null)
                {
                    ITxObject curObj = cur as ITxObject;
                    if (curObj == null) break;
                    string key = ObjKey(curObj);
                    if (!guard.Add(key)) break;               // 防环
                    object parent = GetParentObject(curObj);
                    if (parent == null) break;
                    ITxObject pObj = parent as ITxObject;
                    if (pObj == null) break;
                    string pName = null;
                    try { pName = pObj.Name; } catch { }
                    if (string.IsNullOrEmpty(pName)) break;
                    // 根级容器止步（PhysicalRoot/ResourceRoot/ComponentRoot 等顶层）
                    string pType = null;
                    try { pType = pObj.GetType().Name; } catch { }
                    if (pType != null &&
                        (pType.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0
                         || pName == "PhysicalRoot" || pName == "ResourceRoot" || pName == "ComponentRoot"))
                        break;
                    path.Insert(0, pName);
                    cur = parent;
                }
            }
            catch { }
            if (path.Count > 0)
                SafeLog(onLog, "  目录树: " + dev.Name + " → " + string.Join("/", path));
            return path;
        }

        /// <summary>
        /// 取 PS 对象的父容器。优先强类型 ITxObject.Collection；
        /// 再反射尝试 Parent/OwningObject/Owner/Container 多属性兜底。
        /// </summary>
        private static object GetParentObject(ITxObject o)
        {
            if (o == null) return null;
            // 1) 强类型 ITxObject.Collection → 父容器
            try
            {
                var coll = o.Collection;
                if (coll != null && !ReferenceEquals(coll, o)) return coll;
            }
            catch { }
            // 2) 反射多属性兜底（不同 SDK 版本命名不同）
            try
            {
                var t = o.GetType();
                foreach (var name in new[] { "Parent", "OwningObject", "Owner", "Container" })
                {
                    var p = t.GetProperty(name);
                    if (p != null)
                    {
                        var v = p.GetValue(o, null);
                        if (v is ITxObject && !ReferenceEquals(v, o)) return v;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>对象自身或其子树是否含几何（用于静态资源整体导出判断）。</summary>
        private static bool HasGeometry(ITxObject o)
        {
            var coll = o as ITxObjectCollection;
            if (coll == null) return false;
            try
            {
                // 注意：TxSolid 不是 TxGeometry 子类，需两个类型一起过滤
                var geomFilter = new TxTypeFilter(new Type[] { typeof(TxGeometry), typeof(TxSolid) });
                var geoms = coll.GetAllDescendants(geomFilter);
                return geoms != null && geoms.Count > 0;
            }
            catch { return false; }
        }

        // ── 分组：PS 主线程枚举几何，按(设备×颜色)收集世界三角形 ──────────
        private List<DeviceData> CollectDeviceGroups(List<ITxObject> picked, string originName, Action<string> onLog, bool expandedAlready = false, Action<DeviceData> streamSink = null, TxTransformation extraInverse = null)
        {
            return OnPs(delegate()
            {
                // 1) 展开设备（无去重）
                var devices = new List<ITxObject>();
                foreach (var o in picked)
                {
                    var expanded = expandedAlready ? new List<ITxObject> { o } : ExpandPicked(o, onLog);
                    if (expanded.Count == 0)
                        SafeLog(onLog, "  skip(无可导出): " + (o != null ? o.Name : "null"));
                    devices.AddRange(expanded);
                }
                SafeLog(onLog, "[PS] 展开资源数: " + devices.Count);
                if (devices.Count == 0) return new List<DeviceData>();

                // 2) 原点偏移
                TxTransformation originInverse = null;
                if (!string.IsNullOrEmpty(originName))
                {
                    var lst = TxApplication.ActiveDocument.GetObjectsByName(originName);
                    if (lst != null && lst.Count > 0)
                    {
                        var lo = lst[0] as ITxLocatableObject;
                        if (lo != null)
                        {
                            originInverse = lo.AbsoluteLocation.Inverse;
                            SafeLog(onLog, "[PS] 原点: " + originName);
                        }
                    }
                }

                // 3) 每个设备按颜色分组收集三角形
                var result = new List<DeviceData>();
                foreach (var dev in devices)
                {
                    var dd = new DeviceData { Name = dev.Name };
                    dd.Path = GetTreePath(dev, onLog);

                    TxLibraryStorage st = null;
                    List<ITxGeometry> geoms = EnumDeviceGeometries(dev, ref st, onLog);
                    if (geoms == null || geoms.Count == 0)
                    {
                        // 区分：无几何（对象本身就不含几何） vs 枚举失败（有 StorageObject 但拿不到）
                        bool hasAny = HasGeometry(dev);
                        if (hasAny)
                            SafeLog(onLog, "  " + dev.Name + ": 枚举几何失败(含几何但未解锁)，已跳过");
                        else
                            SafeLog(onLog, "  " + dev.Name + ": 无几何，已跳过");
                        continue;
                    }

                    var colorIndex = new Dictionary<string, ColorGroup>();
                    const int streamBatchTriangles = 500000;
                    int bufferedTriangles = 0;
                    int surfaceId = 0;
                    foreach (var g in geoms)
                    {
                        CheckCancelled();
                        surfaceId++;
                        byte r = 160, gg2 = 160, bb2 = 160;
                        bool actualColor = false;
                        try
                        {
                            // All displayable geometry types expose GetColors through the
                            // public interface, including mesh geometry faces and proxies.
                            HashSet<TxColor> cs = null;
                            var displayable = g as ITxDisplayableObject;
                            if (displayable != null) cs = displayable.GetColors();
                            if (cs == null || cs.Count == 0)
                                SafeLog(onLog, "[颜色缺失] 类型=" + g.GetType().FullName + "；对象=" + ObjKey(g) + "；GetColors 未返回颜色，使用默认灰色 RGB=160,160,160");
                            if (cs != null && cs.Count > 0)
                            {
                                // 取色：优先取第一个【非纯黑】颜色。
                                // 原因：ReferenceRep（引用表示）几何的 GetColors() 常把占位黑
                                // (0,0,0) 排在前面，真实设计色在集合里；直接 break 取第一个
                                // 会让主体导出成黑色。仅当颜色全是黑时才回退用黑。
                                bool colorPicked = false;
                                foreach (object c in cs)
                                {
                                    var col = c as TxColor;
                                    if (col != null)
                                    {
                                        if (col.Red == 0 && col.Green == 0 && col.Blue == 0) continue;
                                        r = col.Red; gg2 = col.Green; bb2 = col.Blue;
                                        colorPicked = true;
                                        actualColor = true;
                                        break;
                                    }
                                }
                                if (!colorPicked)
                                {
                                    foreach (object c in cs)
                                    {
                                        var col = c as TxColor;
                                        if (col != null) { r = col.Red; gg2 = col.Green; bb2 = col.Blue; actualColor = true; break; }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { SafeLog(onLog, "[颜色读取失败] " + surfaceId + "：" + ex.Message); }
                        SafeLog(onLog, "[几何颜色] " + dev.Name + "/" + surfaceId + " 类型=" + g.GetType().Name + " 来源=" + (actualColor ? "GetColors" : "默认灰色") + " RGB=" + r + "," + gg2 + "," + bb2);
                        string rgb = surfaceId + ":" + r + "," + gg2 + "," + bb2;
                        ColorGroup cg;
                        if (!colorIndex.TryGetValue(rgb, out cg))
                        {
                            cg = new ColorGroup { R = r, G = gg2, B = bb2, Surface = surfaceId };
                            colorIndex[rgb] = cg;
                            dd.Colors.Add(cg);
                        }
                        try
                        {
                            SafeLog(onLog, "[几何读取开始] " + dev.Name + "/" + surfaceId + "；" + ObjKey(g) + "；" + g.GetType().Name);
                            var a = g.Approximation;
                            SafeLog(onLog, "[几何读取完成] " + dev.Name + "/" + surfaceId + "；" + ObjKey(g));
                            if (a == null || a.Points == null || a.Points.Length < 3) continue;
                            var pts = a.Points;
                            var loc = ((ITxLocatableObject)g).AbsoluteLocation;
                            var outputTransform = loc;
                            if (originInverse != null) outputTransform = TxTransformation.Multiply(originInverse, outputTransform);
                            if (extraInverse != null) outputTransform = TxTransformation.Multiply(extraInverse, outputTransform);
                            foreach (var prim in a.Primitives)
                            {
                                CheckCancelled();
                                byte pr=r, pg=gg2, pb=bb2;
                                string primitiveRgb=surfaceId+":"+pr+","+pg+","+pb;
                                ColorGroup primitiveGroup;
                                if(!colorIndex.TryGetValue(primitiveRgb,out primitiveGroup))
                                {
                                    primitiveGroup=new ColorGroup{R=pr,G=pg,B=pb,Surface=surfaceId};colorIndex[primitiveRgb]=primitiveGroup;dd.Colors.Add(primitiveGroup);
                                }
                                var idx = prim.Indices;
                                if (idx == null || idx.Length < 3) continue;
                                if (idx.Length != 3) throw new InvalidDataException("不支持的面索引数量 " + idx.Length + "，停止该设备以避免截断几何");
                                var p0 = outputTransform.Transform(pts[idx[0]]);
                                var p1 = outputTransform.Transform(pts[idx[1]]);
                                var p2 = outputTransform.Transform(pts[idx[2]]);
                                primitiveGroup.Tris.Add(new float[]
                                {
                                    (float)p0.X, (float)p0.Y, (float)p0.Z,
                                    (float)p1.X, (float)p1.Y, (float)p1.Z,
                                    (float)p2.X, (float)p2.Y, (float)p2.Z
                                });
                                bufferedTriangles++;
                                if (streamSink != null && bufferedTriangles >= streamBatchTriangles)
                                {
                                    streamSink(dd);
                                    dd = new DeviceData { Name = dev.Name, Path = dd.Path };
                                    colorIndex.Clear();
                                    bufferedTriangles = 0;
                                    // 后续三角面重新按颜色建立组，避免继续引用已刷出的列表。
                                    if (!colorIndex.TryGetValue(rgb, out cg))
                                    {
                                        cg = new ColorGroup { R = r, G = gg2, B = bb2, Surface = surfaceId };
                                        colorIndex[rgb] = cg;
                                        dd.Colors.Add(cg);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException("几何读取失败：" + dev.Name + "/" + surfaceId + "；" + ObjKey(g) + "。该设备未通过完整性检查", ex);
                        }

                        // 合并网格采用有界流式写入，避免大设备在采集阶段占满内存。
                    }
                    if (streamSink != null && bufferedTriangles > 0)
                    {
                        streamSink(dd);
                        dd = new DeviceData { Name = dev.Name, Path = dd.Path };
                        colorIndex.Clear();
                    }
                    if (streamSink == null)
                    {
                    }
                    try { if (st != null) st.Reload(TxRepresentationLevel.United); } catch { }
                    if (streamSink == null && dd.Colors.Count > 0) result.Add(dd);
                }
                return result;
            });
        }

        private List<ITxGeometry> EnumDeviceGeometries(ITxObject dev, ref TxLibraryStorage st, Action<string> onLog)
        {
            var list = new List<ITxGeometry>();

            CollectGeometryChildren(dev, list, new HashSet<string>(), new HashSet<TxLibraryStorage>(), onLog);
            SafeLog(onLog, "  " + dev.Name + ": 几何 " + list.Count + " 个（Detailed，去重后）");
            return list;
        }

        private void CollectGeometryChildren(ITxObject node, List<ITxGeometry> output,
            HashSet<string> seen, HashSet<TxLibraryStorage> loaded, Action<string> onLog)
        {
            CheckCancelled();
            if (node == null || !seen.Add(ObjKey(node))) return;
            var g = node as ITxGeometry;
            // Kinematic links expose both an aggregate mesh and child geometry.
            // Traverse their children instead of painting the aggregate one color.
            if (g != null && !(node is TxKinematicLink)) { output.Add(g); return; }
            // Read detailed design colors and children, even when the current
            // representation already exposes a coarse black mesh.
            var stored = node as ITxStorable;
            if (stored != null)
            {
                try
                {
                    var storage = stored.StorageObject as TxLibraryStorage;
                    if (storage != null && loaded.Add(storage))
                    {
                        SafeLog(onLog, "[Detailed 开始] " + node.Name + "；" + ObjKey(node));
                        CheckCancelled();
                        storage.Reload(TxRepresentationLevel.Detailed);
                        SafeLog(onLog, "[Detailed 完成] " + node.Name);
                    }
                }
                catch (Exception ex) { throw new InvalidOperationException("Detailed 加载失败：" + node.Name + "；" + ObjKey(node), ex); }
            }
            var coll = node as ITxObjectCollection;
            if (coll == null) return;
            var children = new List<ITxObject>();
            try {
                var enumerable = node as System.Collections.IEnumerable;
                if (enumerable != null) foreach (object child in enumerable) {
                    var obj = child as ITxObject; if (obj != null) children.Add(obj);
                }
            } catch (Exception ex) { children.Clear(); SafeLog(onLog, "[子项枚举回退] " + node.Name + "：" + ex.Message); }
            if (children.Count == 0) {
                var all = coll.GetAllDescendants(new TxNoTypeFilter());
                if (all != null) foreach (object child in all) { var obj = child as ITxObject; if (obj != null) children.Add(obj); }
            }
            foreach (var child in children) CollectGeometryChildren(child, output, seen, loaded, onLog);
            if (node is TxKinematicLink && children.Count == 0)
            {
                SafeLog(onLog, "[连杆回退] " + node.Name + " 无可枚举子项，保留整体网格");
                output.Add(g);
            }
        }

        /// <summary>递归遍历设备所有后代组件，无条件对每个对象 StorageObject.Reload(Detailed)。</summary>
        private static void ReloadRecursive(ITxObject node, Action<string> onLog)
        {
            if (node == null) return;

            // 当前节点 Reload(Detailed)
            try
            {
                object so = null;
                try { so = ((ITxStorable)node).StorageObject; } catch { }
                var nodeSt = so as TxLibraryStorage;
                if (nodeSt != null)
                    try { nodeSt.Reload(TxRepresentationLevel.Detailed); } catch { }
            }
            catch { }

            // 递归所有直接子组件（多路径：IEnumerable 或 GetAllDescendants 兜底）
            try
            {
                var kids = new List<ITxObject>();
                try
                {
                    foreach (object k in (System.Collections.IEnumerable)node)
                    {
                        var itx = k as ITxObject;
                        if (itx != null) kids.Add(itx);
                    }
                }
                catch { }
                if (kids.Count == 0)
                {
                    var coll = node as ITxObjectCollection;
                    if (coll != null)
                    {
                        try
                        {
                            var all = coll.GetAllDescendants(new TxNoTypeFilter());
                            if (all != null)
                                foreach (object o in all)
                                {
                                    var itx = o as ITxObject;
                                    if (itx != null) kids.Add(itx);
                                }
                        }
                        catch { }
                    }
                }
                foreach (var kid in kids)
                    if (kid != null)
                        ReloadRecursive(kid, onLog);
            }
            catch { }
        }

        // ── 写二进制 STL ────────────────────────────────────────────────
        private bool Connect(out string error)
        {
            error = null;
            try
            {
                _catia = (INFITF.Application)Marshal.GetActiveObject("CATIA.Application");
                try { _catia.Visible = true; } catch { }
                SafeLog(null, "✓ 已连接 CATIA");
                return true;
            }
            catch
            {
                _catia = null;
                error = "未检测到运行中的 CATIA V5，请先打开 CATIA";
                return false;
            }
        }

        // ── 前置确定导入目标文档：无活动文档/非 Product 文档时新建 ──────
        private void EnsureProductDocument(Action<string> onLog)
        {
            _productDoc = null;
            Document activeDoc = null;
            try { activeDoc = _catia.ActiveDocument; } catch { }
            if (activeDoc is ProductDocument existing)
            {
                _productDoc = existing;
                SafeLog(onLog, "[Catia] 使用当前 Product 文档");
            }
            else
            {
                try
                {
                    _productDoc = (ProductDocument)_catia.Documents.Add("Product");
                    SafeLog(onLog, "[Catia] 新建 Product 文档");
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[Catia] 新建 Product 文档失败: " + ex.Message);
                    throw;
                }
            }
        }

        /// <summary>在 Products 集合里按名字找子 Product 的索引(1-based)；不存在返回 0。</summary>
        private static int FindChildIndex(Products prods, string name)
        {
            try
            {
                for (int i = 1; i <= prods.Count; i++)
                {
                    Product p = prods.Item(i);
                    string pn = null, pn2 = null;
                    try { pn = p.get_PartNumber(); } catch { }
                    try { pn2 = p.get_Name(); } catch { }
                    if ((!string.IsNullOrEmpty(pn) && string.Equals(pn, SafeFileName(name), StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrEmpty(pn2) && string.Equals(pn2, SafeFileName(name), StringComparison.OrdinalIgnoreCase)))
                        return i;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// 生成父 Products 下的唯一名：若 baseName 已被占用，追加 _2/_3/... 区分标记，
        /// 避免 CATIA 同父级 Product 重名被自动改成 Product.1 之类的默认命名。
        /// </summary>
        private static string GetUniqueChildName(Products parent, string baseName)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int cnt = 0;
            try { cnt = parent.Count; } catch { }
            for (int i = 1; i <= cnt; i++)
            {
                try
                {
                    Product ch = parent.Item(i);
                    try { existing.Add(ch.get_Name()); } catch { }
                    try { existing.Add(ch.get_PartNumber()); } catch { }
                }
                catch { }
            }
            if (!existing.Contains(baseName)) return baseName;
            for (int i = 2; i <= 999; i++)
            {
                string tryName = baseName + "_" + i;
                if (!existing.Contains(tryName)) return tryName;
            }
            return baseName + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        private static string SafeFileName(string name)
        {
            var inv = Path.GetInvalidFileNameChars();
            foreach (char c in inv) name = name.Replace(c.ToString(), "_");
            return name.Replace("(", "").Replace(")", "").Replace(" ", "_");
        }

        private static void SafeLog(Action<string> cb, string msg)
        { if (cb != null) try { cb(msg); } catch { } }
        private static void SafeComplete(Action<bool, string> cb, bool ok, string msg)
        { if (cb != null) try { cb(ok, msg); } catch { } }
    }
}
