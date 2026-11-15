using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.CommandParameters;
using TxTools.Common;

namespace TxTools.FootingPlanExport
{
    internal static class PsSectionService
    {
        internal const string SaveContourCommand = "DnSectionCommands.CUiSecSaveContourAsComponentCmd";
        // 场景对象与导出文件的名称一律使用纯 ASCII：原生截面命令与 JT 存储
        // 在中文字符下会走 Unicode→ANSI 转换，名称会退化成问号。
        private const string CutNamePrefix = "FootingPlan_";
        private const string SectionNamePrefix = "FootingPlane_";
        private static bool _sceneUncertain;

        internal static string Export(TxDocument document, double height, CacheSession cache,
                                      CancellationToken token, Action<int, string> progress)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("截面 SDK 必须在 PS 主线程调用。");
            if (_sceneUncertain) throw new InvalidOperationException("上次临时对象清理未完成，请检查工程并重启 PS 后重试。");
            if (document == null || document != TxApplication.ActiveDocument) throw new InvalidOperationException("当前工程已改变。");
            if (double.IsNaN(height) || double.IsInfinity(height)) throw new ArgumentException("Z 距离必须是有限数值。");
            var viewer = TxApplication.ViewersManager.GraphicViewer;
            if (viewer == null) throw new InvalidOperationException("没有活动的图形视图。");
            var commands = TxApplication.CommandsManager;
            if (!commands.IsCommandConnected(SaveContourCommand))
                throw new InvalidOperationException("当前 PS 未加载 Save Section Contour as Component 命令。");
            token.ThrowIfCancellationRequested();
            string cutName = AsciiName(CutNamePrefix, cache.RunId);
            string sectionName = AsciiName(SectionNamePrefix, cache.RunId);
            TxBoundingBox sceneBox = RequirePlaneCrossesScene(document, height);
            string cutId = null, sectionId = null, jt = null;
            TxObjectList previousSelection = null;
            string phase = "创建截平面";
            var oldSections = new List<ITxSection>();
            foreach (ITxObject obj in document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxSection))))
            {
                var existing = obj as ITxSection;
                if (existing != null && existing.Active) oldSections.Add(existing);
            }
            bool oldContours = viewer.ShowContours, oldClip = viewer.Clip, oldCut = viewer.Cut;
            bool viewerTouched = false;
            ITxComponent previousScope = document.CurrentModelingWorkingSpace;
            var cleanupErrors = new List<string>();
            Exception failure = null;
            var undo = SceneUndoScope.Begin(document, "导出地脚图");
            try
            {
                token.ThrowIfCancellationRequested(); undo.EnsureDocument();
                progress(8, phase);
                // Only this temporary plane contributes contours. Existing
                // sections and viewer options are restored in the finally block.
                foreach (var existing in oldSections) existing.Active = false;
                var location = new TxTransformation();
                // 截平面必须覆盖模型：固定停在原点、10 m 见方时，离原点很远的
                // 工程不会被平面覆盖，轮廓也不会产生。
                double centerX = 0, centerY = 0, sizeX = 10000, sizeY = 10000;
                if (sceneBox != null)
                {
                    centerX = (sceneBox.Lower.X + sceneBox.Higher.X) / 2;
                    centerY = (sceneBox.Lower.Y + sceneBox.Higher.Y) / 2;
                    sizeX = PlaneSize(sceneBox.Higher.X - sceneBox.Lower.X);
                    sizeY = PlaneSize(sceneBox.Higher.Y - sceneBox.Lower.Y);
                }
                location.Translation = new TxVector(centerX, centerY, height);
                var section = document.PhysicalRoot.CreateSection(new TxSectionCreationData(sectionName, location, new TxVector(sizeX, sizeY, 1)));
                sectionId = section.Id;
                section.Active = true;
                viewerTouched = true;
                previousSelection = SelectSection(section);
                phase = "Show Section Contours";
                progress(18, phase);
                token.ThrowIfCancellationRequested(); undo.EnsureDocument();
                phase = "Save Section Contour as Component";
                progress(26, phase);
                // The native command only reads contours already generated inside
                // the graphic viewer, and its enabler is refreshed by PS itself.
                // That refresh never happens inside this synchronous SDK chain, so
                // wait for the contours to become available instead of failing at
                // the first enabler query.
                if (!PrepareContours(viewer, commands, token))
                    throw new InvalidOperationException(ContourUnavailableMessage(document, height));
                var before = ComponentIds(document);
                bool executed = false;
                Exception commandFailure = null;
                // 该命令是交互式的：它弹出 "Save Component As" 对话框等待用户
                // 挑选 .cojt 路径，不会在场景里留下组件。对话框不接受带路径的
                // 文件名，所以只写纯文件名，落盘目录由对话框自身给出。
                string sectionCojt = cache.SectionCojtPath;
                string verifiedCojt = null;
                var cojtSnapshot = cache.SnapshotCojts();
                var saveDialog = SaveComponentDialog.Watch(sectionCojt, TimeSpan.FromSeconds(30));
                using (saveDialog)
                {
                    try { executed = commands.ExecuteCommand(SaveContourCommand); }
                    catch (Exception error) { commandFailure = error; }
                    undo.EnsureDocument();
                    progress(30, "等待截面 cojt");
                    WaitForContourResult(cache, document, before, saveDialog, cojtSnapshot, token);
                    verifiedCojt = saveDialog.VerifiedPath;
                }
                undo.EnsureDocument();
                var created = NewTopLevelComponents(document, before);
                if (created.Count > 1) _sceneUncertain = true;
                ITxComponent contour = created.Count == 1 ? created[0] : null;
                if (contour != null)
                {
                    cutId = contour.Id;
                    ((ITxObject)contour).Name = cutName;
                }
                if (commandFailure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(commandFailure).Throw();
                token.ThrowIfCancellationRequested();
                phase = "保存本地截面 JT";
                progress(32, phase);
                string cojtDirectory = ResolveSectionCojt(cache, saveDialog, sectionCojt, cojtSnapshot);
                if (cojtDirectory != null)
                {
                    jt = cache.StageStoredJt(cojtDirectory);
                    RemoveForeignCojt(cojtDirectory, cache);
                }
                else if (contour != null)
                {
                    // Fallback for builds where the command creates a component in
                    // the scene instead of asking for a file.
                    var storable = contour as ITxStorable;
                    if (!executed) throw new InvalidOperationException("原生截面保存命令未成功执行。");
                    if (storable == null) throw new IOException("原生截面组件不支持存储。");
                    var local = storable.StorageObject as TxLocalStorage;
                    if (local != null)
                        jt = cache.StageLocalJt(local.SaveComponent());
                    else
                    {
                        var library = storable.StorageObject as TxLibraryStorage;
                        if (library == null) throw new IOException("无法读取原生截面组件的 JT 存储。");
                        jt = cache.StageStoredJt(library.FullPath);
                    }
                }
                else
                {
                    throw new InvalidOperationException("截面轮廓没有保存成 cojt，也没有生成场景组件。" +
                        (executed ? "保存对话框可能被取消。" : "原生命令未成功执行。") +
                        "\n目标文件名：" + Path.GetFileName(sectionCojt) +
                        "\n对话框目录：" + (saveDialog.LastFolder ?? "未读到") +
                        "\n已点保存：" + (saveDialog.Clicked ? "是" : "否") +
                        "\n确认路径：" + (verifiedCojt ?? "未确认"));
                }
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException error) { failure = error; }
            catch (Exception error) { failure = new IOException(phase + "失败：" + error.Message, error); }
            finally
            {
                try
                {
                    undo.EnsureDocument();
                    var current = document.CurrentModelingWorkingSpace;
                    if (current != null && (current.Id == cutId || current.Name == cutName))
                    {
                        if (previousScope != null && document.DoesObjectExist(previousScope.Id))
                            ((ITxComponent)document.GetObjectById(previousScope.Id)).SetModelingScope();
                        else
                            commands.ExecuteCommand("Modeling.SetModelingWorkingSpace", new TxSingleObjectParameters(document.PhysicalRoot));
                    }
                }
                catch (Exception error) { cleanupErrors.Add("恢复建模工作空间：" + error.Message); }
                try { undo.EnsureDocument(); DeleteOwned(document, cutId, cutName); }
                catch (Exception error) { cleanupErrors.Add("移除临时截面：" + error.Message); }
                try { undo.EnsureDocument(); DeleteOwned(document, sectionId, sectionName); }
                catch (Exception error) { cleanupErrors.Add("移除临时截平面：" + error.Message); }
                foreach (var existing in oldSections)
                    try { undo.EnsureDocument(); existing.Active = true; }
                    catch (Exception error) { cleanupErrors.Add("恢复原截平面：" + error.Message); }
                if (viewerTouched)
                {
                    try { viewer.Clip = oldClip; viewer.Cut = oldCut; viewer.ShowContours = oldContours; viewer.UpdateContours(); }
                    catch (Exception error) { cleanupErrors.Add("恢复截面显示：" + error.Message); }
                }
                RestoreSelection(previousSelection, cleanupErrors);
                try { undo.Dispose(); }
                catch (Exception error) { cleanupErrors.Add("结束撤销分组：" + error.Message); }
                cache.SceneDetached = cleanupErrors.Count == 0 && !_sceneUncertain;
                _sceneUncertain |= cleanupErrors.Count != 0;
            }
            if (cleanupErrors.Count > 0)
                throw new IOException((failure == null ? "截面缓存已生成。" : failure.Message + "\n") +
                    "临时对象清理未完成，缓存已保留：\n" + string.Join("\n", cleanupErrors), failure);
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return jt;
        }

        private static string AsciiName(string prefix, string runId)
        {
            string name = prefix + runId;
            foreach (char c in name)
                if (c > 0x7F) throw new InvalidOperationException("导出对象名称必须是 ASCII：" + name);
            return name;
        }

        private static string Fmt(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        // The plane must pass through visible geometry, otherwise the native
        // command never produces contours and would fail much later.
        private static TxBoundingBox RequirePlaneCrossesScene(TxDocument document, double height)
        {
            var box = TryVisibleBox(document);
            if (box == null) return null;
            double low = box.Lower.Z, high = box.Higher.Z;
            if (height >= low && height <= high) return box;
            throw new InvalidOperationException("Z " + Fmt(height) + " mm 不在场景可见范围 " + Fmt(low) + " ~ " + Fmt(high) +
                " mm 内，截平面不会切到任何零件。请调整距离后重试。");
        }

        // 截平面需要覆盖整个模型，取 1.5 倍余量并限制在 1 m ~ 1000 m 之间。
        private static double PlaneSize(double extent)
        {
            if (double.IsNaN(extent) || double.IsInfinity(extent)) return 10000;
            return Math.Min(1000000, Math.Max(1000, extent * 1.5 + 1000));
        }

        private static TxBoundingBox TryVisibleBox(TxDocument document)
        {
            try
            {
                var calculator = new TxBoundingBoxCalculator();
                calculator.AddObject(document.PhysicalRoot);
                var box = calculator.CalculateVisibleObjectsBoundingBox();
                if (box == null || box.IsEmpty) return null;
                if (!Finite(box.Lower) || !Finite(box.Higher)) return null;
                return box;
            }
            catch (Exception) { return null; }
        }

        private static bool Finite(TxVector value)
            => value != null && !double.IsNaN(value.X) && !double.IsNaN(value.Y) && !double.IsNaN(value.Z) &&
               !double.IsInfinity(value.X) && !double.IsInfinity(value.Y) && !double.IsInfinity(value.Z);

        private static int CountCrossingComponents(TxDocument document, double height)
        {
            try
            {
                int count = 0, budget = 800;
                foreach (ITxObject obj in document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxComponent))))
                {
                    if (--budget < 0) break;
                    var calculator = new TxBoundingBoxCalculator();
                    calculator.AddObject(obj);
                    var box = calculator.CalculateBoundingBox();
                    if (box == null || box.IsEmpty) continue;
                    if (box.Lower.Z <= height && box.Higher.Z >= height) count++;
                }
                return count;
            }
            catch (Exception) { return -1; }
        }

        private static string ContourUnavailableMessage(TxDocument document, double height)
        {
            string message = "Z " + Fmt(height) + " mm 处没有可用的截面轮廓，原生 Save Section Contour as Component 未启用。";
            var box = TryVisibleBox(document);
            if (box != null)
                message += "\n场景可见 Z 范围：" + Fmt(box.Lower.Z) + " ~ " + Fmt(box.Higher.Z) + " mm。";
            int crossing = CountCrossingComponents(document, height);
            if (crossing < 0) message += "\n无法统计穿过该高度的组件。";
            else if (crossing == 0) message += "\n穿过该高度的组件：0 个。请把 Z 距离改到零件的实际高度上。";
            else message += "\n穿过该高度的组件：" + crossing + " 个；轮廓没有在图形视图里生成，请取消隐藏模型后重试。";
            return message;
        }

        // The native enabler may require the active section inside the current
        // selection; the previous selection is restored by the caller.
        private static TxObjectList SelectSection(ITxSection section)
        {
            TxObjectList previous = null;
            try
            {
                previous = TxApplication.ActiveSelection.GetItems();
                var only = new TxObjectList();
                only.Add(section);
                TxApplication.ActiveSelection.SetItems(only);
            }
            catch (Exception) { }
            return previous;
        }

        private static void RestoreSelection(TxObjectList previous, List<string> errors)
        {
            if (previous == null) return;
            try { TxApplication.ActiveSelection.SetItems(previous); }
            catch (Exception)
            {
                try { TxApplication.ActiveSelection.Clear(); }
                catch (Exception error) { errors.Add("恢复选择集：" + error.Message); }
            }
        }

        // Contours are generated by the viewer, but PS only publishes them to the
        // native command through its own refresh cycle. Pump the message loop and
        // poll the enabler instead of trusting the first query.
        private static bool PrepareContours(TxGraphicViewer viewer, TxCommandsManager commands, CancellationToken token)
        {
            bool[] clipModes = { true, false };
            for (int index = 0; index < clipModes.Length; index++)
            {
                viewer.Clip = clipModes[index];
                viewer.Cut = false;
                viewer.ShowContours = true;
                try { viewer.UpdateContours(); }
                catch (Exception) { }
                try { TxApplication.RefreshDisplay(); }
                catch (Exception) { }
                if (WaitForContourCommand(commands, viewer, token, index == 0 ? 3000 : 1500)) return true;
            }
            return false;
        }

        private static bool WaitForContourCommand(TxCommandsManager commands, TxGraphicViewer viewer,
                                                 CancellationToken token, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            int round = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (commands.IsCommandEnabled(SaveContourCommand, true)) return true;
                if (watch.ElapsedMilliseconds >= timeoutMs) return false;
                if (round % 8 == 0)
                {
                    try { viewer.UpdateContours(); TxApplication.RefreshDisplay(); }
                    catch (Exception) { }
                }
                round++;
                Application.DoEvents();
                Thread.Sleep(25);
            }
        }

        // 命令可能是异步启动的半模态命令：对话框稍后才出现，cojt 也在随后才
        // 落盘。这里泵消息等它就绪；正常路径在命令返回时文件已经存在。
        private static void WaitForContourResult(CacheSession cache, TxDocument document, HashSet<string> before,
                                                SaveDialogWatch watch, HashSet<string> cojtSnapshot,
                                                CancellationToken token)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 12000)
            {
                token.ThrowIfCancellationRequested();
                string directory;
                if (watch.VerifiedPath != null && CacheSession.IsCojtReady(watch.VerifiedPath))
                {
                    // 文件可能在写入完成前就被看见，留一次刷新周期。
                    Thread.Sleep(200);
                    return;
                }
                if (cache.FindNewCojt(cojtSnapshot) != null)
                {
                    Thread.Sleep(200);
                    return;
                }
                if (cache.TryGetSectionCojt(out directory))
                {
                    Thread.Sleep(200);
                    return;
                }
                if (HasNewComponent(document, before)) return;
                Application.DoEvents();
                Thread.Sleep(50);
            }
        }

        // 依次尝试：对话框确认过的真实路径 → 缓存目录里本次新出现的 cojt →
        // 本次运行目录 → 按唯一文件名在常见根目录里兜底搜索。
        private static string ResolveSectionCojt(CacheSession cache, SaveDialogWatch dialog, string expected,
                                                 HashSet<string> cojtSnapshot)
        {
            if (dialog.VerifiedPath != null && CacheSession.IsCojtReady(dialog.VerifiedPath)) return dialog.VerifiedPath;
            string created = cache.FindNewCojt(cojtSnapshot);
            if (created != null) return created;
            string directory;
            if (cache.TryGetSectionCojt(out directory)) return directory;
            string found = CacheSession.FindCojtByName(Path.GetFileName(expected));
            if (found != null && CacheSession.IsCojtReady(found)) return found;
            return null;
        }

        // 对话框若把 cojt 存到了本次缓存目录以外（通常是 PS 记住的上次目录），
        // JT 已经抽取完成，随后把这次生成的临时目录清掉，避免污染用户目录。
        private static void RemoveForeignCojt(string cojt, CacheSession cache)
        {
            if (cojt == null) return;
            if (cojt.StartsWith(cache.RunDirectory, StringComparison.OrdinalIgnoreCase)) return;
            try { Directory.Delete(cojt, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool HasNewComponent(TxDocument document, HashSet<string> before)
        {
            foreach (ITxObject obj in document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxComponent))))
                if (!before.Contains(obj.Id)) return true;
            return false;
        }

        private static HashSet<string> ComponentIds(TxDocument document)
        {
            var result = new HashSet<string>();
            foreach (ITxObject obj in document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxComponent)))) result.Add(obj.Id);
            return result;
        }

        private static List<ITxComponent> NewTopLevelComponents(TxDocument document, HashSet<string> before)
        {
            var created = new List<ITxComponent>();
            var ids = new HashSet<string>();
            foreach (ITxObject obj in document.PhysicalRoot.GetAllDescendants(new TxTypeFilter(typeof(ITxComponent))))
                if (!before.Contains(obj.Id)) { created.Add((ITxComponent)obj); ids.Add(obj.Id); }
            var result = new List<ITxComponent>();
            foreach (var obj in created)
            {
                ITxObject parent = obj.Collection;
                bool nested = false;
                var seen = new HashSet<string>();
                while (parent != null)
                {
                    if (!seen.Add(parent.Id)) throw new IOException("场景模型结构存在循环。");
                    if (ids.Contains(parent.Id)) { nested = true; break; }
                    parent = parent.Collection;
                }
                if (!nested) result.Add(obj);
            }
            return result;
        }

        private static void DeleteOwned(TxDocument document, string id, string uniqueName)
        {
            if (id != null && document.DoesObjectExist(id)) document.GetObjectById(id).Delete();
            foreach (ITxObject item in document.GetObjectsByName(uniqueName)) if (item.Name == uniqueName) item.Delete();
            if ((id != null && document.DoesObjectExist(id)) || document.GetObjectsByName(uniqueName).Count != 0)
                throw new IOException("临时对象仍在场景中：" + uniqueName);
        }
    }
}
