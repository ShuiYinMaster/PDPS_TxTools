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
using TxTools.Agent.Core;
using SysFile = System.IO.File;

namespace TxTools.ExportByColor
{
    public sealed class ColorGroup
    {
        public byte R, G, B;
        // Surface identity prevents separately-instanced geometry with the same
        // RGB value from being welded into one smoothing island downstream.
        public int Surface;
        public List<float[]> Tris = new List<float[]>();   // 每条 float[9] = 世界坐标三角形
    }

    public sealed class DeviceData
    {
        public string Name;
        public List<string> Path = new List<string>();   // PS 目录树路径(父级容器名,不含根)
        public List<ColorGroup> Colors = new List<ColorGroup>();
    }

    public sealed class ExportProgressInfo
    {
        public int Total;
        public int Collected;
        public int Completed;
        public int Failed;
        public string Stage;
        public string DeviceName;

        // Geometry collection determines the workload, while encoding/publishing
        // confirms it is usable.  Weight both phases so a full bar means done.
        public int Percent
        {
            get
            {
                if (Total <= 0) return 0;
                int scan = Math.Min(Total, Math.Max(0, Collected));
                int done = Math.Min(Total, Math.Max(0, Completed));
                return Math.Min(99, (scan * 65 + done * 35) / Total);
            }
        }
    }

    public class ExportByColorService
    {
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
            public string ExportName;
            public string Path;
            public string SourcePath;
            public ThreeDXmlWriter.PartTicket PackagePart;
            public Cfv3EncodingStats Cfv3Stats;
            public Exception Error;
        }

        private sealed class ExportProgressState
        {
            internal readonly int Total;
            internal int Collected;
            internal int Completed;
            internal int Failed;

            internal ExportProgressState(int total) { Total = total; }

            internal void Report(Action<ExportProgressInfo> callback, string stage, string deviceName)
            {
                if (callback == null) return;
                try
                {
                    callback(new ExportProgressInfo
                    {
                        Total = Total,
                        Collected = Collected,
                        Completed = Completed,
                        Failed = Failed,
                        Stage = stage,
                        DeviceName = deviceName
                    });
                }
                catch { }
            }
        }

        // PS reads stay on its synchronization context. Only detached mesh data reaches workers.
        // At most two devices are retained, including the device currently being collected.
        public void RunAsync(List<ITxObject> picked, string originName, string format, string outputRoot, bool mergeMeshes,
                             Action<string> onLog, Action<ExportProgressInfo> onProgress, Action<bool, string> onComplete)
        {
            RunAsync(picked,originName,format,outputRoot,mergeMeshes,onLog,onProgress,onComplete,CgrBackend.Configured);
        }
        public void RunAsync(List<ITxObject> picked, string originName, string format, string outputRoot, bool mergeMeshes,
                             Action<string> onLog, Action<ExportProgressInfo> onProgress, Action<bool, string> onComplete,CgrBackend backend)
        {
            // Capture the choice per export; never change a process-wide environment variable
            // while the two device workers are encoding.  R38 feature CGR is bridged to
            // compact geometry only at the CFV3 boundary; compact capacity limits may split it.
            CgrBackend runBackend=CgrWriter.ResolveBackend(backend);
            var thread = new Thread(() =>
            {
                var pending = new Queue<Task<EncodedDevice>>();
                int ok = 0, failed = 0;
                string output = null;
                string workDir = null;
                MeshExport.Archive merged = null;
                ThreeDXmlWriter package = null;
                try
                {
                    if(format=="3DXML"&&runBackend==CgrBackend.LineFacePlanar)
                        throw new ArgumentException("逐平面兼容方案尚未接入 3DXML，请选择默认直出或 R38 线面几何试用方案");
                    if (format != "3DXML" && format != "CGR" && format != "STL" && format != "OBJ" && format != "PLY" && format != "FBX" && format != "FBX_BINARY" && format != "FBX_ASCII")
                        throw new ArgumentException("不支持的网格格式");
                    if (format == "CGR")
                    {
                        string error;
                        if (!Connect(out error)) throw new InvalidOperationException(error);
                        EnsureProductDocument(onLog);
                    }
                    if (format == "3DXML")
                    {
                        if (string.IsNullOrWhiteSpace(outputRoot)) throw new ArgumentException("未指定 3DXML 输出文件");
                        output = Path.GetFullPath(outputRoot);
                        if (!string.Equals(Path.GetExtension(output), ".3dxml", StringComparison.OrdinalIgnoreCase)) output += ".3dxml";
                        workDir = TxToolsTemp.SessionDirectory("ExportByColor", "3DXML");
                        package = new ThreeDXmlWriter(output, Path.GetFileNameWithoutExtension(output));
                    }
                    else
                    {
                        var exportRoot = outputRoot ?? TxToolsTemp.DirectoryFor("ExportByColor");
                        output = Path.Combine(exportRoot, "TxTools_Export_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        workDir = output;
                        Directory.CreateDirectory(workDir);
                    }
                    var devices = OnPs(() =>
                    {
                        var result = new List<ITxObject>();
                        var keys = new HashSet<string>();
                        foreach (var item in picked)
                            foreach (var device in ExpandPicked(item, onLog))
                                if (keys.Add(ObjKey(device))) result.Add(device);
                        return result;
                    });
                    int workers = Environment.Is64BitProcess ? Math.Min(2, Math.Max(1, Environment.ProcessorCount / 2)) : 1;
                    var progress = new ExportProgressState(devices.Count);
                    progress.Report(onProgress, "准备导出", null);
                    SafeLog(onLog, "[导出] " + devices.Count + " 个设备；" + (format == "3DXML" ? "直出装配 3DXML" : format) + "；编码并发 " + workers + " 路");
                    if(format=="CGR")SafeLog(onLog,runBackend==CgrBackend.LineFace?"[CGR] 线面压缩：连续曲面合域，保留网格测量；可切回逐平面兼容或原方案":runBackend==CgrBackend.LineFacePlanar?"[CGR] 逐平面兼容：保留细分面域及边关联":"[CGR] 原有生成方案");
                    if(format=="3DXML"&&runBackend==CgrBackend.LineFace)SafeLog(onLog,"[3DXML] R38 线面几何试用：保留几何和源组后桥接 CFV3；3DXML 不携带 CGR 线/面选择记录");
                    var names = new ExportNames();
                    if (mergeMeshes && format != "CGR" && format != "3DXML") merged = new MeshExport.Archive(output);
                    foreach (var device in devices)
                    {
                        if (pending.Count >= workers) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, package, onLog, progress, onProgress, ref ok, ref failed);
                        string name = names.Next(OnPs(() => device.Name));
                        progress.Report(onProgress, "采集几何", name);
                        try
                        {
                            bool submitted = false;
                            var data = CollectDeviceGroups(new List<ITxObject> { device }, originName, onLog, true,
                                merged == null ? null : new Action<DeviceData>(batch =>
                                {
                                    long batchTriangles = 0;
                                    foreach (var group in batch.Colors) batchTriangles += group.Tris.Count;
                                    if (batchTriangles == 0) return;
                                    merged.Add(batch, name);
                                    DetailLog(onLog, "[合并缓存] " + name + "：批次 " + batchTriangles + " 面；已落盘");
                                    batch.Colors.Clear();
                                }));
                            foreach (var dd in data)
                            {
                                long triangleCount = 0;
                                foreach (var group in dd.Colors) triangleCount += group.Tris.Count;
                                if (triangleCount == 0) continue;
                                if (merged != null)
                                {
                                    try { merged.Add(dd, name); DetailLog(onLog, "[合并缓存] " + name + "：" + triangleCount + " 面"); submitted = true; }
                                    finally { dd.Colors.Clear(); }
                                    continue;
                                }
                                // Large devices run alone; avoid multiplying the topology workspace.
                                if (triangleCount > 500000)
                                    while (pending.Count > 0) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, package, onLog, progress, onProgress, ref ok, ref failed);
                                ThreeDXmlWriter.PartTicket part = format == "3DXML" ? package.PreparePart(name, dd.Path) : null;
                                pending.Enqueue(Task.Run(() =>
                                {
                                    var result = new EncodedDevice { Device = dd, ExportName = name, PackagePart = part };
                                    try
                                    {
                                        if (format == "CGR" || format == "3DXML")
                                        {
                                            string cgr = BuildCgr(dd.Colors, name, workDir, message => DetailLog(onLog, "[" + name + "] " + message),runBackend);
                                            if (format == "3DXML")
                                            {
                                                result.SourcePath = cgr;
                                                result.Path = Path.Combine(workDir, name + ".3DRep");
                                                result.Cfv3Stats = Cfv3Encoder.ConvertFile(cgr, result.Path, part.Identity);
                                            }
                                            else result.Path = cgr;
                                        }
                                        else result.Path = MeshExport.Write(dd, name, output, format, originName);
                                    }
                                    catch (Exception ex) { result.Error = ex; }
                                    finally { dd.Colors.Clear(); }
                                    return result;
                                }));
                                submitted = true;
                                if (triangleCount > 500000) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, package, onLog, progress, onProgress, ref ok, ref failed);
                            }
                            progress.Collected++;
                            if (merged != null || !submitted) progress.Completed++;
                            progress.Report(onProgress, submitted ? "编码/写入" : "无有效三角面，已跳过", name);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            progress.Collected++;
                            progress.Completed++;
                            progress.Failed++;
                            progress.Report(onProgress, "读取失败", name);
                            SafeLog(onLog, "[读取失败] " + name + ": " + ex.Message);
                        }
                    }
                    while (pending.Count > 0) FinishExport(pending.Dequeue().GetAwaiter().GetResult(), format, package, onLog, progress, onProgress, ref ok, ref failed);
                    if (merged != null)
                    {
                        string mergedName = names.Next("合并设备");
                        SafeLog(onLog, "[合并写入] 格式=" + format + "；设备=" + merged.Devices + "；二进制 FBX 使用 7500/64 位偏移");
                        string mergedPath = merged.Finish(output, mergedName, format, originName);
                        ok = merged.Devices;
                        SafeLog(onLog, "[合并完成] " + ok + " 个设备 → " + mergedPath);
                    }
                    if (format == "3DXML")
                    {
                        if (ok == 0 || failed != 0)
                        {
                            progress.Collected = progress.Total;
                            progress.Completed = progress.Total;
                            progress.Failed = failed;
                            progress.Report(onProgress, "导出结束，未发布不完整文件", null);
                            SafeComplete(onComplete, false, ok + " 个设备完成，" + failed + " 个失败；未生成不完整的 3DXML。恢复目录: " + workDir);
                            return;
                        }
                        package.Complete();
                        SafeLog(onLog, "[3DXML 完成] " + package.PartCount + " 个 3DRep，保留 PS 装配层级 → " + output);
                        try { Directory.Delete(workDir, false); } catch (Exception cleanupError) { SafeLog(onLog, "[临时目录] " + cleanupError.Message); }
                    }
                    progress.Collected = progress.Total;
                    progress.Completed = progress.Total;
                    progress.Failed = failed;
                    progress.Report(onProgress, failed == 0 ? "导出完成" : "导出结束，存在失败", null);
                    SafeComplete(onComplete, ok > 0 && failed == 0, ok + " 个设备完成，" + failed + " 个失败。文件: " + output + (format == "CGR" ? "；CATIA 添加调用已返回，显示仍需验证。" : ""));
                }
                catch (Exception ex)
                {
                    // Observe all workers before reporting completion; no orphan writes after a failed run.
                    while (pending.Count > 0) { try { pending.Dequeue().GetAwaiter().GetResult(); } catch { } }
                    SafeLog(onLog, "[详细错误] " + ex.ToString());
                    if (merged != null) { try { SafeLog(onLog, "[恢复缓存] " + merged.PreserveForRecovery()); } catch (Exception recoveryError) { SafeLog(onLog, "[恢复缓存] " + recoveryError.Message); } }
                    if (workDir != null) { try { SysFile.WriteAllText(Path.Combine(workDir, "export-error.txt"), ex.ToString(), Encoding.UTF8); } catch { } }
                    SafeComplete(onComplete, false, ex.Message + "；详细错误及恢复缓存: " + workDir);
                }
                finally
                {
                    if (package != null) { try { package.Dispose(); } catch (Exception ex) { SafeLog(onLog, "[3DXML 清理] " + ex.Message); } }
                    if (merged != null) { try { merged.Dispose(); } catch (Exception ex) { SafeLog(onLog, "[缓存清理] " + ex.Message); } }
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void FinishExport(EncodedDevice result, string format, ThreeDXmlWriter package, Action<string> onLog,
            ExportProgressState progress, Action<ExportProgressInfo> onProgress, ref int ok, ref int failed)
        {
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
                else if (format == "3DXML")
                {
                    if (package == null || result.PackagePart == null) throw new InvalidOperationException("3DXML 写入器未初始化");
                    package.AddEncodedPart(result.PackagePart, result.Path);
                    if (result.Cfv3Stats != null)
                        DetailLog(onLog, "[CFV3] " + result.ExportName + "：" + result.Cfv3Stats.Leaves + " 叶，Skeleton=" + result.Cfv3Stats.SkeletonBytes + "，SurfacicReps=" + result.Cfv3Stats.SurfacicRepBytes + (result.Cfv3Stats.FeatureGeometryBridge ? "；R38 线面几何桥接，未写入 CGR 线/面选择记录" : ""));
                    TryDeleteTemporary(result.Path, onLog);
                    TryDeleteTemporary(result.SourcePath, onLog);
                }
                ok++;
                progress.Completed++;
                progress.Report(onProgress, "已完成", result.Device.Name);
                SafeLog(onLog, "[完成] " + result.Device.Name + "（" + format + "）");
            }
            catch (Exception ex)
            {
                failed++;
                progress.Completed++;
                progress.Failed++;
                progress.Report(onProgress, "编码失败", result.Device == null ? null : result.Device.Name);
                SafeLog(onLog, "[设备失败] " + result.Device.Name + ": " + ex.Message);
                DetailLog(onLog, "[设备失败详情] " + ex);
            }
        }

        private static void TryDeleteTemporary(string path, Action<string> onLog)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (SysFile.Exists(path)) SysFile.Delete(path); }
            catch (Exception ex) { DetailLog(onLog, "[临时文件] " + path + "：" + ex.Message); }
        }

        private static string BuildCgr(List<ColorGroup> groups, string name, string tmpDir, Action<string> onLog,CgrBackend backend=CgrBackend.Configured)
        {
            var vertices=new List<float[]>();var faces=new List<CgrWriter.Face>();
            // Keep color groups' vertex identity separate: touching independent solids must not
            // become nonmanifold through coordinate welding. All groups still share ONE file.
            int surface=0;
            foreach(var group in groups)
            {
                surface++;
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
            CgrWriter.BuildFile(vertices,faces,path,backend,progress:onLog);
            return path;
        }

        /// <summary>
        /// 从 PS 中已加载的单个资源采集几何并生成 CGR。供 ExportGun 等旧调用方复用。
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

        /// <summary>
        /// Production JT route: resolve the selected Process Simulate resource's
        /// backing JT file, encode the native loaded representation as CGR, and
        /// insert that CGR into the active CATIA Product document.
        /// </summary>
        public void RunJtToCgrAsync(List<ITxObject> picked,
                                    Action<string> onLog,
                                    Action<ExportProgressInfo> onProgress,
                                    Action<bool, string> onComplete)
        {
            var thread = new Thread(() =>
            {
                int ok = 0, failed = 0;
                string workDir = Path.Combine(Path.GetTempPath(),
                    "TxTools_JT_CGR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
                    Guid.NewGuid().ToString("N").Substring(0, 8));
                try
                {
                    string error;
                    if (!Connect(out error)) throw new InvalidOperationException(error);
                    EnsureProductDocument(onLog);
                    Directory.CreateDirectory(workDir);
                    var devices = OnPs(() =>
                    {
                        var result = new List<ITxObject>();
                        var keys = new HashSet<string>();
                        foreach (var item in picked)
                            foreach (var device in ExpandPicked(item, onLog))
                                if (keys.Add(ObjKey(device))) result.Add(device);
                        return result;
                    });
                    if (devices.Count == 0) throw new InvalidOperationException("没有可转换的设备");
                    var progress = new ExportProgressState(devices.Count);
                    progress.Report(onProgress, "JT 准备", null);
                    SafeLog(onLog, "[JT→CGR] " + devices.Count + " 个资源；输出目录：" + workDir);
                    var names = new ExportNames();
                    foreach (var device in devices)
                    {
                        string name = names.Next(OnPs(() => device.Name));
                        progress.Report(onProgress, "解析 JT", name);
                        try
                        {
                            string jtPath = null, trace = null;
                            bool resolved = OnPs(() => JtResourceResolver.TryResolve(device, out jtPath, out trace));
                            if (!resolved || string.IsNullOrWhiteSpace(jtPath))
                                throw new FileNotFoundException("未能从资源解析 JT 文件或 COJT 目录（" + trace + "）", jtPath);
                            if (!SysFile.Exists(jtPath) && !Directory.Exists(jtPath))
                                throw new FileNotFoundException("JT 文件或 COJT 资源目录不存在", jtPath);
                            SafeLog(onLog, "[JT] " + name + " ← " + jtPath + "（" + trace + "）");

                            {
                                var placement = OnPs(() =>
                                {
                                    var located = device as ITxLocatableObject;
                                    if (located == null) throw new InvalidOperationException("资源没有可读取的放置坐标");
                                    var loc = located.AbsoluteLocation;
                                    var o = loc.Transform(new TxVector(0, 0, 0));
                                    var x = loc.Transform(new TxVector(1, 0, 0));
                                    var y = loc.Transform(new TxVector(0, 1, 0));
                                    var z = loc.Transform(new TxVector(0, 0, 1));
                                    return new double[] { x.X-o.X,x.Y-o.Y,x.Z-o.Z,0,
                                        y.X-o.X,y.Y-o.Y,y.Z-o.Z,0,z.X-o.X,z.Y-o.Y,z.Z-o.Z,0,o.X,o.Y,o.Z,1 };
                                });
                                string decoder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ExportByColorService).Assembly.Location),
                                    "JtDirectCs", "TxTools.JtDecoder.exe");
                                SafeLog(onLog, "[JT 直接保色] 使用 JT 文件内姿态和颜色，应用资源整体放置；未采集 PS 运动部件当前姿态");
                                progress.Report(onProgress, "JT 直接解码", name);
                                var directTreePath = OnPs(() => GetTreePath(device, onLog));
                                JtDirectBridge.Mesh mesh;
                                try
                                {
                                    mesh = JtDirectBridge.Convert(jtPath, workDir, decoder, 300000);
                                }
                                catch (NotSupportedException unsupported)
                                {
                                    SafeLog(onLog, "[JT compatibility] " + unsupported.Message);
                                    SafeLog(onLog, "[JT compatibility] Using loaded PS geometry/design colors and current PS pose; direct JT face-color decoder is not used.");
                                    progress.Report(onProgress, "PS compatibility collection", name);
                                    var nativeData = CollectDeviceGroups(new List<ITxObject> { device }, null, onLog, true);
                                    if (nativeData == null || nativeData.Count == 0)
                                        throw new InvalidOperationException("Native JT compatibility found no loaded PS geometry; " + unsupported.Message);
                                    var nativeGroups = new List<ColorGroup>();
                                    try
                                    {
                                        foreach (var data in nativeData) nativeGroups.AddRange(data.Colors);
                                        if (nativeGroups.Count == 0)
                                            throw new InvalidOperationException("Native PS compatibility found no triangles");
                                        string nativeCgr = BuildCgr(nativeGroups, name, workDir,
                                            message => DetailLog(onLog, "[" + name + "] " + message), CgrBackend.Compact);
                                        progress.Collected++;
                                        FinishExport(new EncodedDevice { Device = new DeviceData { Name = name, Path = directTreePath },
                                            ExportName = name, Path = nativeCgr, SourcePath = jtPath },
                                            "CGR", null, onLog, progress, onProgress, ref ok, ref failed);
                                    }
                                    finally
                                    {
                                        nativeGroups.Clear();
                                        foreach (var data in nativeData) data.Colors.Clear();
                                    }
                                    continue;
                                }
                                JtDirectBridge.Place(mesh, placement);
                                var directFaces = new List<CgrWriter.Face>();
                                foreach (var face in mesh.Faces)
                                    directFaces.Add(new CgrWriter.Face { Idx=face.Indices,R=face.R,G=face.G,B=face.B,Surface=face.Surface,
                                        Nx=face.Normal[0],Ny=face.Normal[1],Nz=face.Normal[2] });
                                string directCgr = System.IO.Path.Combine(workDir, name + ".cgr");
                                CgrWriter.BuildFile(mesh.Vertices,directFaces,directCgr,CgrBackend.Compact,20000,
                                    message => DetailLog(onLog,"["+name+"] "+message));
                                progress.Collected++;
                                FinishExport(new EncodedDevice { Device=new DeviceData { Name=name,Path=directTreePath },ExportName=name,
                                    Path=directCgr,SourcePath=jtPath },"CGR",null,onLog,progress,onProgress,ref ok,ref failed);
                                continue;
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            progress.Collected++;
                            progress.Completed++;
                            progress.Failed++;
                            progress.Report(onProgress, "JT 失败", name);
                            SafeLog(onLog, "[JT 失败] " + name + ": " + ex.Message);
                            DetailLog(onLog, "[JT 失败详情] " + ex);
                        }
                    }
                    progress.Collected = progress.Total;
                    progress.Completed = progress.Total;
                    progress.Failed = failed;
                    progress.Report(onProgress, failed == 0 ? "JT→CGR 完成" : "JT→CGR 结束，存在失败", null);
                    SafeComplete(onComplete, ok > 0 && failed == 0,
                        ok + " 个 JT 资源已转换并插入 CATIA，" + failed + " 个失败；CGR 保留在 " + workDir);
                }
                catch (Exception ex)
                {
                    SafeLog(onLog, "[JT→CGR 详细错误] " + ex.ToString());
                    SafeComplete(onComplete, false, ex.Message + "；恢复目录: " + workDir);
                }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private static bool TryPrimitiveColor(object primitive, out byte r, out byte g, out byte b)
        {
            r=g=b=0;if(primitive==null)return false;
            try
            {
                object value=null;var type=primitive.GetType();
                foreach(string property in new[]{"Color","TxColor","Appearance"})
                { var p=type.GetProperty(property,BindingFlags.Instance|BindingFlags.Public);if(p!=null){value=p.GetValue(primitive,null);if(value!=null)break;} }
                if(value!=null && value.GetType().GetProperty("Color")!=null) value=value.GetType().GetProperty("Color").GetValue(value,null);
                var c=value as TxColor;if(c!=null){r=c.Red;g=c.Green;b=c.Blue;return true;}
                var method=type.GetMethod("GetColor",Type.EmptyTypes);if(method!=null){c=method.Invoke(primitive,null) as TxColor;if(c!=null){r=c.Red;g=c.Green;b=c.Blue;return true;}}
            }catch{}
            return false;
        }

        private static void StableGeometryColor(HashSet<TxColor> colors, ref byte r, ref byte g, ref byte b)
        {
            if(colors==null||colors.Count==0)return;
            TxColor best=null;
            foreach(var c in colors)
            {
                if(c==null)continue;
                if(best==null || (best.Red==0&&best.Green==0&&best.Blue==0) ||
                   c.Red<best.Red || (c.Red==best.Red && (c.Green<best.Green || (c.Green==best.Green&&c.Blue<best.Blue)))) best=c;
            }
            if(best!=null){r=best.Red;g=best.Green;b=best.Blue;}
            // Prefer the non-black color deterministically when a black placeholder exists.
            foreach(var c in colors) if(c!=null && (c.Red!=0||c.Green!=0||c.Blue!=0))
            {
                if(best==null || best.Red==0&&best.Green==0&&best.Blue==0 ||
                   c.Red<best.Red || (c.Red==best.Red&&(c.Green<best.Green || c.Green==best.Green&&c.Blue<best.Blue))) best=c;
            }
            if(best!=null){r=best.Red;g=best.Green;b=best.Blue;}
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

        /// <summary>唯一性键：优先 ITxObject.Id；无 Id 时必须按 COM 包装对象身份区分，名称可重复。</summary>
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

                // 2) 输出坐标系：不能只减平移，原点对象可能包含旋转。
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
                    long totalTriangles = 0;
                    int colorFallbacks = 0;
                    int surfaceId = 0;
                    foreach (var g in geoms)
                    {
                        surfaceId++;
                        byte r = 160, gg2 = 160, bb2 = 160;
                        bool actualColor = false;
                        try
                        {
                            // GetColors is the SDK's real design-color API.  Casting only to
                            // TxGeometry/TxSolid drops proxy and prototype representations.
                            HashSet<TxColor> colors = null;
                            var displayable = g as ITxDisplayableObject;
                            if (displayable != null) colors = displayable.GetColors();
                            if (colors != null && colors.Count > 0)
                            {
                                StableGeometryColor(colors, ref r, ref gg2, ref bb2);
                                actualColor = true;
                            }
                            else if (displayable != null)
                            {
                                var fallback = displayable.Color;
                                if (fallback != null && (fallback.Red != 0 || fallback.Green != 0 || fallback.Blue != 0))
                                {
                                    r = fallback.Red; gg2 = fallback.Green; bb2 = fallback.Blue;
                                    actualColor = true;
                                }
                            }
                            if (!actualColor)
                            {
                                colorFallbacks++;
                                DetailLog(onLog, "[颜色缺失] 类型=" + g.GetType().FullName + "；对象=" + ObjKey(g) + "；使用默认灰色 RGB=160,160,160");
                            }
                        }
                        catch (Exception ex)
                        {
                            colorFallbacks++;
                            DetailLog(onLog, "[颜色读取失败] " + surfaceId + "：" + ex.Message + "；使用默认灰色");
                        }
                        DetailLog(onLog, "[几何颜色] " + dev.Name + "/" + surfaceId + " 类型=" + g.GetType().Name + " 来源=" + (actualColor ? "设计色" : "默认灰色") + " RGB=" + r + "," + gg2 + "," + bb2);
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
                            DetailLog(onLog, "[几何读取开始] " + dev.Name + "/" + surfaceId + "；" + ObjKey(g) + "；" + g.GetType().Name);
                            var a = g.Approximation;
                            DetailLog(onLog, "[几何读取完成] " + dev.Name + "/" + surfaceId + "；" + ObjKey(g));
                            if (a == null || a.Points == null || a.Points.Length < 3) continue;
                            var pts = a.Points;
                            var outputTransform = ((ITxLocatableObject)g).AbsoluteLocation;
                            if (originInverse != null) outputTransform = TxTransformation.Multiply(originInverse, outputTransform);
                            if (extraInverse != null) outputTransform = TxTransformation.Multiply(extraInverse, outputTransform);
                            foreach (var prim in a.Primitives)
                            {
                                byte pr = r, pg = gg2, pb = bb2;
                                // Primitive properties are renderer-dependent; they are opt-in
                                // so they cannot overwrite the verified geometry design color.
                                if (Environment.GetEnvironmentVariable("TXTOOLS_CGR_PRIMITIVE_COLORS") == "1")
                                { byte tr, tg2, tb; if (TryPrimitiveColor(prim, out tr, out tg2, out tb)) { pr = tr; pg = tg2; pb = tb; } }
                                string primitiveRgb = surfaceId + ":" + pr + "," + pg + "," + pb;
                                ColorGroup primitiveGroup;
                                if (!colorIndex.TryGetValue(primitiveRgb, out primitiveGroup))
                                {
                                    primitiveGroup = new ColorGroup { R = pr, G = pg, B = pb, Surface = surfaceId };
                                    colorIndex[primitiveRgb] = primitiveGroup;
                                    dd.Colors.Add(primitiveGroup);
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
                                totalTriangles++;
                                if (streamSink != null && bufferedTriangles >= streamBatchTriangles)
                                {
                                    streamSink(dd);
                                    dd = new DeviceData { Name = dev.Name, Path = dd.Path };
                                    colorIndex.Clear();
                                    bufferedTriangles = 0;
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
                    }
                    SafeLog(onLog, "[采集] " + dev.Name + "：" + geoms.Count + " 个几何，" + totalTriangles + " 个三角面" +
                        (colorFallbacks == 0 ? "" : "，" + colorFallbacks + " 个使用默认灰色"));
                    if (streamSink != null && bufferedTriangles > 0)
                    {
                        streamSink(dd);
                        dd = new DeviceData { Name = dev.Name, Path = dd.Path };
                        colorIndex.Clear();
                    }
                    try { if (st != null) st.Reload(TxRepresentationLevel.United); } catch { }
                    if (streamSink == null && dd.Colors.Count > 0) result.Add(dd);
                }
                return result;
            });
        }

        private static List<ITxGeometry> EnumDeviceGeometries(ITxObject dev, ref TxLibraryStorage st, Action<string> onLog)
        {
            var list = new List<ITxGeometry>();
            var rootStorable = dev as ITxStorable;
            if (rootStorable != null)
            {
                try { st = rootStorable.StorageObject as TxLibraryStorage; }
                catch (Exception ex) { SafeLog(onLog, "[Storage 读取失败] " + dev.Name + "：" + ex.Message); }
            }

            // A prototype frequently consists of nested storable subsets.  A single
            // GetAllDescendants call made before their Detailed reload sees only its
            // coarse proxy (or no child at all), which was the EquipmentPrototype loss.
            CollectGeometryChildren(dev, list, new HashSet<string>(), new HashSet<TxLibraryStorage>(), onLog);
            DetailLog(onLog, "  " + dev.Name + ": 几何 " + list.Count + " 个（Detailed，去重后）");
            return list;
        }

        private static void CollectGeometryChildren(ITxObject node, List<ITxGeometry> output,
            HashSet<string> seen, HashSet<TxLibraryStorage> loaded, Action<string> onLog)
        {
            if (node == null || !seen.Add(ObjKey(node))) return;
            var geometry = node as ITxGeometry;
            // Kinematic links publish an aggregate proxy alongside their children;
            // preserve the children rather than exporting that uncoloured proxy twice.
            if (geometry != null && !(node is TxKinematicLink))
            {
                output.Add(geometry);
                return;
            }

            var stored = node as ITxStorable;
            if (stored != null)
            {
                try
                {
                    var storage = stored.StorageObject as TxLibraryStorage;
                    if (storage != null && loaded.Add(storage))
                    {
                        DetailLog(onLog, "[Detailed 开始] " + node.Name + "；" + ObjKey(node));
                        storage.Reload(TxRepresentationLevel.Detailed);
                        DetailLog(onLog, "[Detailed 完成] " + node.Name);
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Detailed 加载失败：" + node.Name + "；" + ObjKey(node), ex);
                }
            }

            var children = new List<ITxObject>();
            var childKeys = new HashSet<string>();
            Action<ITxObject> add = child =>
            {
                if (child != null && childKeys.Add(ObjKey(child))) children.Add(child);
            };
            try
            {
                var enumerable = node as System.Collections.IEnumerable;
                if (enumerable != null)
                    foreach (object child in enumerable) add(child as ITxObject);
            }
            catch (Exception ex)
            {
                DetailLog(onLog, "[子项枚举回退] " + node.Name + "：" + ex.Message);
            }

            // EquipmentPrototype implementations differ between PS releases.  Some
            // expose subsets through a property instead of ITxObjectCollection.  Probe
            // only conventional read-only subset names and only accept ITxObject values.
            AddPrototypeSubsetChildren(node, add, onLog);

            if (children.Count == 0)
            {
                var coll = node as ITxObjectCollection;
                if (coll != null)
                {
                    try
                    {
                        var all = coll.GetAllDescendants(new TxNoTypeFilter());
                        if (all != null) foreach (object child in all) add(child as ITxObject);
                    }
                    catch (Exception ex) { DetailLog(onLog, "[后代枚举失败] " + node.Name + "：" + ex.Message); }
                }
            }

            foreach (var child in children) CollectGeometryChildren(child, output, seen, loaded, onLog);
            if (node is TxKinematicLink && children.Count == 0 && geometry != null)
            {
                DetailLog(onLog, "[连杆回退] " + node.Name + " 无可枚举子项，保留整体网格");
                output.Add(geometry);
            }
        }

        private static void AddPrototypeSubsetChildren(ITxObject node, Action<ITxObject> add, Action<string> onLog)
        {
            foreach (string member in new[] { "Subsets", "SubSets", "Subset", "EquipmentPrototype", "Prototype", "Components" })
            {
                try
                {
                    var property = node.GetType().GetProperty(member, BindingFlags.Instance | BindingFlags.Public);
                    if (property == null || property.GetIndexParameters().Length != 0) continue;
                    AddPrototypeSubsetValue(property.GetValue(node, null), add);
                }
                catch (Exception ex) { DetailLog(onLog, "[原型子集探测] " + node.Name + "." + member + "：" + ex.Message); }
            }
        }

        private static void AddPrototypeSubsetValue(object value, Action<ITxObject> add)
        {
            var objectValue = value as ITxObject;
            if (objectValue != null) { add(objectValue); return; }
            var enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null) return;
            foreach (object child in enumerable) add(child as ITxObject);
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

        // Set TXTOOLS_EXPORT_VERBOSE=1 only while diagnosing a problematic PS object.
        // Normal export logs stay device-level so the UI remains responsive.
        private static void DetailLog(Action<string> cb, string msg)
        {
            if (Environment.GetEnvironmentVariable("TXTOOLS_EXPORT_VERBOSE") == "1") SafeLog(cb, msg);
        }

        private static void SafeLog(Action<string> cb, string msg)
        { if (cb != null) try { cb(msg); } catch { } }
        private static void SafeComplete(Action<bool, string> cb, bool ok, string msg)
        { if (cb != null) try { cb(ok, msg); } catch { } }
    }
}
