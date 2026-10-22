using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    internal static class AppearanceGeometrySource
    {
        internal static bool IsAppearance(ITxObject value)
        {
            return value is ITxPartAppearance || value is ITxSkidAppearance;
        }

        internal static string Key(ITxObject value)
        {
            try { if (!string.IsNullOrEmpty(value.Id)) return "id:" + value.Id; } catch { }
            return "ref:" + RuntimeHelpers.GetHashCode(value);
        }

        internal sealed class GeometryInstance
        {
            internal readonly ITxGeometry Geometry;
            internal readonly string InstanceKey;
            private readonly ITxLocatableObject appearance;
            private readonly ITxLocatableObject source;

            internal GeometryInstance(ITxGeometry geometry, string instanceKey,
                ITxLocatableObject appearance = null, ITxLocatableObject source = null)
            {
                Geometry = geometry;
                InstanceKey = instanceKey;
                this.appearance = appearance;
                this.source = source;
            }

            internal TxTransformation WorldLocation
            {
                get
                {
                    var located = Geometry as ITxLocatableObject;
                    if (located == null) throw new InvalidOperationException("三角面几何没有可读取的绝对位置");
                    var world = located.AbsoluteLocation;
                    if (appearance == null) return world;
                    // Remove the source root's WORLD pose, retaining geometry-to-part offsets.
                    return MapToAppearance(appearance.AbsoluteLocation, source.AbsoluteLocation, world);
                }
            }
        }

        internal static TxTransformation MapToAppearance(TxTransformation appearanceWorld,
            TxTransformation sourceWorld, TxTransformation geometryWorld)
        {
            if (appearanceWorld == null || sourceWorld == null || geometryWorld == null)
                throw new InvalidOperationException("外观、关联零件或子几何的世界矩阵为空");
            return TxTransformation.Multiply(appearanceWorld,
                TxTransformation.Multiply(sourceWorld.Inverse, geometryWorld));
        }

        // Keep this scope alive through Approximation/Points/Primitives reads.
        // All calls, including Dispose, run on the PS synchronization context.
        internal sealed class ReadScope : IDisposable
        {
            private sealed class VisibilityState
            {
                internal string Key;
                internal TxDisplayableObjectVisibility State;
            }

            private sealed class VisibilityChange
            {
                internal Func<ITxObject> ResolveRoot;
                internal TxDisplayableObjectVisibility State;
                internal List<VisibilityState> Descendants;
            }

            private sealed class LevelChange
            {
                internal TxLibraryStorage Storage;
                internal TxRepresentationLevel Level;
            }

            private readonly List<VisibilityChange> changes = new List<VisibilityChange>();
            private readonly HashSet<string> captured = new HashSet<string>();
            private readonly HashSet<TxLibraryStorage> loaded = new HashSet<TxLibraryStorage>();
            private readonly List<LevelChange> levelChanges = new List<LevelChange>();
            private readonly Action<string> log;
            private bool disposed;

            internal ReadScope(Action<string> log) { this.log = log; }

            internal List<GeometryInstance> Collect(ITxObject appearanceObject)
            {
                if (disposed) throw new ObjectDisposedException("ReadScope");
                if (!IsAppearance(appearanceObject)) throw new ArgumentException("对象不是零件或滑橇外观");
                var partAppearance = appearanceObject as ITxPartAppearance;
                var origin = partAppearance != null ? partAppearance.OriginPart : ((ITxSkidAppearance)appearanceObject).OriginSkid;
                if (origin == null) throw new InvalidOperationException("外观没有关联零件：" + appearanceObject.Name);
                var sourceObject = origin.EngineeringRepresentation;
                if (sourceObject == null || IsAppearance(sourceObject))
                    throw new InvalidOperationException("外观关联零件没有有效的实体表示：" + appearanceObject.Name);
                var appearance = appearanceObject as ITxLocatableObject;
                var source = sourceObject as ITxLocatableObject;
                if (appearance == null || source == null)
                    throw new InvalidOperationException("外观或关联零件没有可读取的世界位置：" + appearanceObject.Name);

                CaptureSource(sourceObject, () => origin.EngineeringRepresentation);
                var geometry = new List<ITxGeometry>();
                CollectNode(sourceObject, geometry, new HashSet<string>());
                if (geometry.Count == 0)
                    throw new InvalidOperationException("关联零件显示并加载实体级后仍未提供三角面几何：" + appearanceObject.Name);
                var result = new List<GeometryInstance>();
                foreach (var item in geometry)
                    result.Add(new GeometryInstance(item, "appearance:" + Key(appearanceObject) + "/" + Key(item), appearance, source));
                if (log != null) log("[外观关联] " + appearanceObject.Name + " → " + sourceObject.Name +
                    "；应用 外观世界矩阵 × 零件世界矩阵逆 × 子几何世界矩阵");
                return result;
            }

            private void CaptureSource(ITxObject node, Func<ITxObject> resolveRoot)
            {
                var displayable = node as ITxDisplayableObject;
                if (displayable == null || captured.Contains(Key(node))) return;
                var state = displayable.Visibility;
                var change = new VisibilityChange { ResolveRoot = resolveRoot, State = state };
                if (state == TxDisplayableObjectVisibility.Partial)
                {
                    // Display() affects descendants: preserve the mixed state before changing it.
                    change.Descendants = new List<VisibilityState>();
                    SnapshotChildren(node, change.Descendants, new HashSet<string>());
                    if (change.Descendants.Count == 0)
                        throw new InvalidOperationException("无法保存关联零件的部分显示状态：" + node.Name);
                }
                changes.Add(change); // Save before Display() or Reload() can change the scene.
                captured.Add(Key(node));
            }

            private void EnsureVisible(ITxObject node)
            {
                var displayable = node as ITxDisplayableObject;
                if (displayable == null) return;
                var state = displayable.Visibility;
                if (state == TxDisplayableObjectVisibility.All || state == TxDisplayableObjectVisibility.CannotBeDisplayed) return;
                displayable.Display();
                if (log != null) log("[临时显示关联零件] " + node.Name + "；原状态=" + state);
            }

            private void BeforeReload(ITxObject node, TxLibraryStorage storage)
            {
                var component = node as ITxComponent;
                if (component != null && component.RepresentationType == TxRepresentationType.United)
                    levelChanges.Add(new LevelChange { Storage = storage, Level = TxRepresentationLevel.United });
            }

            private void SnapshotChildren(ITxObject node, List<VisibilityState> result, HashSet<string> seen)
            {
                if (node == null || !seen.Add(Key(node))) return;
                foreach (ITxObject child in Children(node))
                {
                    if (child == null || seen.Contains(Key(child))) continue;
                    var displayable = child as ITxDisplayableObject;
                    if (displayable != null) result.Add(new VisibilityState { Key = Key(child), State = displayable.Visibility });
                    SnapshotChildren(child, result, seen);
                }
            }

            private void CollectNode(ITxObject node, List<ITxGeometry> result, HashSet<string> seen)
            {
                if (node == null || !seen.Add(Key(node))) return;
                EnsureVisible(node);
                GeometryEntityLoader.Load(node, loaded, log, BeforeReload);
                // Reload can reset visibility and replace children. Recheck visibility
                // and enumerate fresh entity objects only after loading has completed.
                EnsureVisible(node);
                var geometry = node as ITxGeometry;
                if (geometry != null && !(node is TxKinematicLink))
                {
                    result.Add(geometry);
                    return;
                }
                var children = Children(node);
                foreach (ITxObject child in children) CollectNode(child, result, seen);
                if (geometry != null && children.Count == 0) result.Add(geometry);
            }

            private TxObjectList Children(ITxObject node)
            {
                var collection = node as ITxObjectCollection;
                if (collection == null) return new TxObjectList();
                TxObjectList children = null;
                Exception directError = null;
                try { children = collection.GetDirectDescendants(new TxNoTypeFilter()); }
                catch (Exception ex) { directError = ex; }
                if (children == null || children.Count == 0)
                {
                    try { children = collection.GetAllDescendants(new TxNoTypeFilter()); }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("关联零件子树读取失败：" + node.Name,
                            directError == null ? ex : new AggregateException(directError, ex));
                    }
                }
                return children ?? new TxObjectList();
            }

            private void IndexDisplayableChildren(ITxObject node, Dictionary<string, ITxDisplayableObject> result,
                HashSet<string> seen)
            {
                if (node == null || !seen.Add(Key(node))) return;
                foreach (ITxObject child in Children(node))
                {
                    if (child == null) continue;
                    var displayable = child as ITxDisplayableObject;
                    if (displayable != null) result[Key(child)] = displayable;
                    IndexDisplayableChildren(child, result, seen);
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                var failures = new List<Exception>();
                Action<Action> restore = action => { try { action(); } catch (Exception ex) { failures.Add(ex); } };
                // Restore nested representation levels before parents, then reacquire
                // visibility objects by identity. Reload invalidates old geometry wrappers.
                for (int i = levelChanges.Count - 1; i >= 0; i--)
                {
                    var level = levelChanges[i];
                    restore(() => level.Storage.Reload(level.Level));
                }
                for (int i = changes.Count - 1; i >= 0; i--)
                {
                    var change = changes[i];
                    restore(() =>
                    {
                        var node = change.ResolveRoot();
                        var root = node as ITxDisplayableObject;
                        if (root == null) throw new InvalidOperationException("无法重新获取关联零件的显示对象");
                        if (change.State == TxDisplayableObjectVisibility.All)
                        {
                            if (root.Visibility != TxDisplayableObjectVisibility.All) root.Display();
                        }
                        else if (change.State == TxDisplayableObjectVisibility.None)
                        {
                            if (root.Visibility != TxDisplayableObjectVisibility.None) root.Blank();
                        }
                        else if (change.Descendants != null)
                        {
                            var current = new Dictionary<string, ITxDisplayableObject>();
                            restore(() => IndexDisplayableChildren(node, current, new HashSet<string>()));
                            restore(() => root.Blank());
                            foreach (var item in change.Descendants)
                            {
                                ITxDisplayableObject target;
                                if (!current.TryGetValue(item.Key, out target))
                                {
                                    if (item.State == TxDisplayableObjectVisibility.All)
                                        failures.Add(new InvalidOperationException("重载后未找到原可见子对象：" + item.Key));
                                    continue;
                                }
                                if (item.State == TxDisplayableObjectVisibility.All) restore(() => target.Display());
                                else if (item.State == TxDisplayableObjectVisibility.None) restore(() => target.Blank());
                            }
                        }
                    });
                }
                if (failures.Count > 0)
                    throw new InvalidOperationException("导出后未能完全恢复关联零件的加载级别或显示状态", new AggregateException(failures));
            }
        }
    }
}
