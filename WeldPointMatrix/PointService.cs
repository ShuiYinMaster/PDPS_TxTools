using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using Tecnomatix.Engineering.DataTypes;
using TxTools.RobotReachabilityChecker.Services;
using TxTools.WeldSpotAllocator;
using TxTools.ExportGun;

namespace TxTools.WeldPointMatrix
{
    internal static class PointService
    {
        internal static bool IsWeldOperation(ITxObject obj)
        {
            if (obj == null) return false;
            string name = obj.GetType().Name;
            return name == "TxWeldOperation" || name == "TxBaseWeldOperation"
                || (name.Contains("Weld") && name.Contains("Operation")
                    && !name.Contains("Location"));
        }

        internal static bool IsSupportedOperation(ITxObject obj)
        {
            if (IsWeldOperation(obj)) return true;
            if (obj == null || !(obj is ITxObjectCollection)) return false;
            if (obj is TxContinuousRoboticOperation) return true;
            string name = obj.GetType().Name;
            return name.Contains("Operation") && !name.Contains("Location")
                && (name.Contains("Continuous") || name.Contains("Seam"));
        }

        private static bool HasContinuousPointType(ITxObject obj)
        {
            string name = obj.GetType().Name;
            return name.Contains("Continuous") || name.Contains("Seam");
        }

        internal static OpData Read(ITxObject operation, Action<string> log)
        {
            if (IsWeldOperation(operation) && !HasContinuousPointType(operation))
                return SpotReader.ReadOneWeldOp(operation, log);
            var data = new OpData { Name = operation.Name, Raw = operation };
            ReadContinuousChildren(operation, data, log ?? (s => { }), 0);
            return data;
        }

        private static void ReadContinuousChildren(ITxObject parent, OpData data,
            Action<string> log, int depth)
        {
            if (parent == null || depth > 20) return;
            foreach (ITxObject child in DirectChildren(parent))
            {
                if (child == null) continue;
                string typeName = child.GetType().Name;
                bool seam = typeName.Contains("SeamLocation");
                bool weld = child is TxWeldLocationOperation;
                bool robotic = child is ITxRoboticLocationOperation;
                if (seam || weld || robotic)
                {
                    try
                    {
                        TxWeldPoint weldPoint = weld
                            ? ((TxWeldLocationOperation)child).WeldPoint : null;
                        TxTransformation tx = weldPoint != null
                            ? weldPoint.AbsoluteLocation
                            : (child as ITxLocatableObject)?.AbsoluteLocation;
                        if (tx == null)
                        {
                            try { tx = ((dynamic)child).AbsoluteLocation as TxTransformation; }
                            catch { }
                        }
                        if (tx == null)
                        {
                            log("[连续点] 无法读取点位坐标：" + child.Name);
                            continue;
                        }
                        double[] matrix = PsReader.TxToArr(tx);
                        PointType kind = weldPoint != null ? PointType.WeldPoint
                            : seam ? PointType.ContinuousPoint : PointType.PathPoint;
                        var point = new SpotData
                        {
                            Name = weldPoint != null ? weldPoint.Name : child.Name,
                            Kind = kind,
                            Matrix = matrix,
                            Position = new[] { matrix[3], matrix[7], matrix[11] },
                            Raw = weldPoint != null ? (ITxObject)weldPoint : child,
                            LocOp = child
                        };
                        data.Ordered.Add(point);
                        if (kind == PointType.WeldPoint) data.Spots.Add(point);
                        else if (kind == PointType.PathPoint) data.Vias.Add(point);
                    }
                    catch (Exception ex)
                    { log("[连续点] 读取 " + child.Name + " 失败：" + ex.Message); }
                }
                else if (child is ITxObjectCollection)
                    ReadContinuousChildren(child, data, log, depth + 1);
            }
        }

        private static IEnumerable<ITxObject> DirectChildren(ITxObject parent)
        {
            int count = -1;
            try { count = (int)((dynamic)parent).GetChildCount(); } catch { }
            if (count > 0)
            {
                var ordered = new List<ITxObject>();
                for (int i = 0; i < count; i++)
                {
                    ITxObject child = null;
                    try { child = ((dynamic)parent).GetChildAt(i) as ITxObject; }
                    catch { }
                    if (child != null) ordered.Add(child);
                }
                if (ordered.Count == count)
                {
                    foreach (ITxObject child in ordered) yield return child;
                    yield break;
                }
            }
            var collection = parent as ITxObjectCollection;
            if (collection == null) yield break;
            TxObjectList children = null;
            try { children = collection.GetDirectDescendants(new TxTypeFilter(typeof(ITxObject))); }
            catch { }
            if (children != null)
                foreach (ITxObject child in children) yield return child;
        }

        internal static void DriveRobot(ITxObject operation, SpotData point)
        {
            if (point == null)
                throw new InvalidOperationException("请选择焊点或过渡点。");
            var location = point.LocOp as ITxRoboticLocationOperation;
            if (location == null)
                throw new InvalidOperationException("点位没有机器人位置操作。");
            TxRobot robot = RobotFinder.FindAssociatedRobotSilent(operation)
                ?? RobotFinder.FindAssociatedRobotSilent(point.LocOp);
            if (robot == null)
                throw new InvalidOperationException("所选操作没有关联机器人。");

            Action restoreTool = null;
            try
            {
                TxFrame frame = ToolFrameReader.ReadLocationToolFrame(location);
                if (frame != null)
                {
                    string error;
                    restoreTool = ToolFrameSwitcher.SwitchToFrame(robot, frame, null, out error);
                    if (restoreTool == null)
                        throw new InvalidOperationException("切换点位工具坐标失败：" + error);
                }
                TxPoseData pose = robot.GetPoseAtLocation(location);
                if (pose == null)
                    throw new InvalidOperationException("机器人无法计算该点位的姿态。");
                robot.CurrentPose = pose;
            }
            finally
            {
                try { if (restoreTool != null) restoreTool(); }
                finally { TxApplication.RefreshDisplay(); }
            }
        }

        internal static void MovePoint(ITxObject sourceOperation, ITxObject targetOperation,
            ITxObject location, ITxObject predecessor)
        {
            if (sourceOperation == null || targetOperation == null || location == null)
                throw new ArgumentException("拖放的操作或点位无效。");
            if (!(location is ITxOperation))
                throw new InvalidOperationException("所选点位不是可移动的操作节点。");
            if (predecessor != null && !(predecessor is ITxOperation))
                throw new InvalidOperationException("目标位置不是操作节点。");
            if (Same(location, predecessor)) return;

            bool sameOperation = Same(sourceOperation, targetOperation);
            var sourceBefore = Read(sourceOperation, s => { }).Ordered;
            if (IndexOf(sourceBefore, location) < 0)
                throw new InvalidOperationException("原操作已不包含该点位，请刷新列表后重试。");
            var targetBefore = sameOperation ? sourceBefore : Read(targetOperation, s => { }).Ordered;
            if (predecessor != null && IndexOf(targetBefore, predecessor) < 0)
                throw new InvalidOperationException("目标操作中找不到放置位置，请刷新列表后重试。");

            var ordered = targetOperation as ITxOrderedObjectCollection;
            if (ordered == null)
                throw new InvalidOperationException("目标操作不支持点位排序。");
            try
            {
                if (sameOperation)
                {
                    try { ordered.MoveChildAfter(location, predecessor); }
                    catch { ordered.AddObjectAfter(location, predecessor as ITxOperation); }
                }
                else
                {
                    // AddObjectAfter 会将原始 location 节点移到目标操作，而非复制焊点。
                    ordered.AddObjectAfter(location, predecessor as ITxOperation);
                }

                var targetAfter = Read(targetOperation, s => { }).Ordered;
                int expected = predecessor == null ? 0 : IndexOf(targetAfter, predecessor) + 1;
                int actual = IndexOf(targetAfter, location);
                if (actual < 0 || actual != expected)
                    throw new InvalidOperationException("PS 未按目标顺序放置点位，请检查操作树。");
                if (!sameOperation && IndexOf(Read(sourceOperation, s => { }).Ordered, location) >= 0)
                    throw new InvalidOperationException("点位仍保留在原操作中，请检查操作树。");
            }
            finally { TxApplication.RefreshDisplay(); }
        }

        private static int IndexOf(System.Collections.Generic.IList<SpotData> points, ITxObject location)
        {
            for (int i = 0; i < points.Count; i++)
                if (Same(points[i].LocOp, location)) return i;
            return -1;
        }

        private static bool Same(ITxObject a, ITxObject b)
        {
            if (ReferenceEquals(a, b)) return true;
            try { return a != null && b != null && a.Equals(b); }
            catch { return false; }
        }

        internal static ITxObject CreatePoint(ITxObject operation, ITxObject predecessor,
            bool weld, string name, double[] matrix)
        {
            if (operation == null || matrix == null || matrix.Length != 16)
                throw new ArgumentException("操作或点位姿态无效。");
            if (weld && !IsWeldOperation(operation))
                throw new InvalidOperationException("连续点操作不支持直接创建焊点，请选择焊接操作。");
            TxTransformation transform = PsReader.ArrToTxPublic(matrix);
            if (!weld)
            {
                var data = new TxRoboticViaLocationOperationCreationData(name);
                TxRoboticViaLocationOperation via = null;
                var continuous = operation as TxContinuousRoboticOperation;
                try
                {
                    if (continuous != null)
                        via = continuous.CreateRoboticViaLocationOperationAfter(data, predecessor as ITxOperation);
                    else
                        via = ((dynamic)operation).CreateRoboticViaLocationOperationAfter(data, predecessor);
                }
                catch
                {
                    // 旧版容器没有 After 接口时，在下方用追加+重排。
                }
                if (via == null)
                {
                    via = continuous != null
                        ? continuous.CreateRoboticViaLocationOperation(data)
                        : ((dynamic)operation).CreateRoboticViaLocationOperation(data);
                    if (via != null)
                    {
                        try
                        {
                            var ordered = operation as ITxOrderedObjectCollection;
                            if (ordered == null)
                                throw new InvalidOperationException("操作不支持点位排序。");
                            ordered.AddObjectAfter(via, predecessor as ITxOperation);
                        }
                        catch
                        {
                            try { ((dynamic)via).Delete(); } catch { }
                            TxApplication.RefreshDisplay();
                            throw;
                        }
                    }
                }
                if (via == null) throw new InvalidOperationException("创建过渡点返回空对象。");
                try
                {
                    ((ITxLocatableObject)via).AbsoluteLocation = transform;
                    TxApplication.RefreshDisplay();
                    return via;
                }
                catch
                {
                    try { ((dynamic)via).Delete(); } catch { }
                    TxApplication.RefreshDisplay();
                    throw;
                }
            }

            var position = transform.Translation;
            var wpData = TxMfgCreationDataFactory.CreateWeldPointCreationData(name,
                new TxVector(position.X, position.Y, position.Z));
            var weldData = new TxWeldLocationOperationCreationData
            {
                Name = name,
                WeldPointCreationData = wpData,
                ProjectedLocation = transform
            };
            TxWeldLocationOperation weldLocation =
                ((dynamic)operation).CreateWeldLocationOperation(weldData) as TxWeldLocationOperation;
            if (weldLocation == null)
                throw new InvalidOperationException("创建焊点返回空对象。");
            try
            {
                weldLocation.WeldPoint.AbsoluteLocation = transform;
                var ordered = operation as ITxOrderedObjectCollection;
                if (ordered == null)
                    throw new InvalidOperationException("操作不支持点位排序。");
                ordered.AddObjectAfter(weldLocation, predecessor as ITxOperation);
                TxApplication.RefreshDisplay();
                return weldLocation;
            }
            catch
            {
                // 创建已成功但后续设置失败时，避免留下位置错误或顺序错误的焊点。
                try { ((dynamic)weldLocation).Delete(); } catch { }
                TxApplication.RefreshDisplay();
                throw;
            }
        }
    }
}
