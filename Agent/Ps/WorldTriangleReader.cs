using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.ExportByColor;

namespace TxTools.Agent.Ps
{
    /// <summary>Double-precision detached world triangles; SDK objects never escape the read scope.</summary>
    internal static class WorldTriangleReader
    {
        internal static List<DeviceZMath.Triangle> Read(TxDocument document, string rootId, int maximumTriangles, Action<string> log)
        {
            if (!Equals(document, TxApplication.ActiveDocument)) throw new InvalidOperationException("工程已切换。");
            if (TxTools.Common.SceneUndoScope.HasActiveTransaction)
                throw new InvalidOperationException("网格实体加载必须在场景撤销事务之外执行。");
            var generation = TxTools.Common.SceneUndoScope.DocumentGeneration;
            Action ensure = () => {
                if (generation != TxTools.Common.SceneUndoScope.DocumentGeneration || !Equals(document, TxApplication.ActiveDocument))
                    throw new InvalidOperationException("工程已重载，已停止几何读取或状态恢复。");
            };
            var target = document.GetObjectById(rootId);
            if (target == null) throw new InvalidOperationException("资源已不存在：" + rootId);
            var result = new List<DeviceZMath.Triangle>();
            using (var scope = new AppearanceGeometrySource.ReadScope(log, true, ensure))
            {
                var instances = scope.CollectResource(target, () => document.GetObjectById(rootId));
                var seen = new HashSet<string>();
                foreach (var instance in instances)
                {
                    ensure();
                    if (!seen.Add(instance.InstanceKey)) continue;
                    var approximation = instance.Geometry.Approximation;
                    if (approximation == null) throw new InvalidOperationException("几何未提供网格：" + instance.Geometry.Name);
                    var points = approximation.Points;
                    if (points == null || points.Length == 0) throw new InvalidOperationException("几何网格顶点为空：" + instance.Geometry.Name);
                    var primitives = approximation.Primitives;
                    if (primitives == null) throw new InvalidOperationException("几何图元为空：" + instance.Geometry.Name);
                    var world = instance.WorldLocation;
                    foreach (var primitive in primitives)
                        foreach (var indices in ApproximationTriangles.Indices(primitive, points.Length))
                        {
                            var a = world.Transform(points[indices[0]]);
                            var b = world.Transform(points[indices[1]]);
                            var c = world.Transform(points[indices[2]]);
                            var triangle = new DeviceZMath.Triangle(
                                new DeviceZMath.V3(a.X, a.Y, a.Z), new DeviceZMath.V3(b.X, b.Y, b.Z),
                                new DeviceZMath.V3(c.X, c.Y, c.Z), instance.Geometry.Id);
                            if (!triangle.Finite) throw new InvalidOperationException("网格包含非有限坐标。");
                            if (triangle.Area <= 1e-12) continue;
                            if (result.Count >= maximumTriangles) throw new InvalidOperationException("三角面数量超出上限，已停止读取，未使用不完整几何。");
                            result.Add(triangle);
                        }
                }
            }
            ensure();
            if (result.Count == 0) throw new InvalidOperationException("未读取到有效三角面；不会退回到资源原点。");
            return result;
        }
    }
}
