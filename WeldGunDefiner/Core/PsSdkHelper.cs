using System;
using System.Collections.Generic;
using System.Reflection;
using Tecnomatix.Engineering;

namespace TxTools.WeldGunDefiner.Core
{
    using Math;

    /// <summary>
    /// PS SDK 接口封装层（基于 tecnomatix_api_docs_combined.md 验证）
    ///
    /// 已确认的关键API：
    ///   TxApplication.ActiveSelection → TxSelection
    ///   TxSelection.GetItems() → TxObjectList（有GetEnumerator，支持foreach）
    ///   TxSelection.Count（属性）
    ///   TxObjectList : Collection<ITxObject>（有索引器、foreach、Count）
    ///
    ///   ITxKinematicsModellable.CreateJoint(creationData) → TxJoint
    ///   ITxKinematicsModellable.CreateLink(creationData) → 链接
    ///   TxJoint.KinematicsFunction（get/set，字符串公式）
    ///   TxJoint.Type（TxJointType枚举）
    ///   TxJoint.LowerSoftLimit / UpperSoftLimit
    ///
    ///   ITxDevice.CreatePose(TxPoseCreationData) → pose
    ///   TxPoseCreationData.Name / PoseData
    ///   TxPoseData.JointValues
    /// </summary>
    public static class PsSdkHelper
    {
        // ─────────────────────────────────────────────
        // 1. 从选中对象获取世界坐标点
        // ─────────────────────────────────────────────

        public static bool TryGetWorldPosition(object txObj, out Vec3 position)
        {
            position = default;
            if (txObj == null) return false;

            try
            {
                if (txObj is ITxLocatableObject locatable)
                {
                    var loc = locatable.AbsoluteLocation;
                    position = ExtractTranslation(loc);
                    return true;
                }

                // 反射回退
                var prop = txObj.GetType().GetProperty("AbsoluteLocation");
                if (prop?.GetValue(txObj, null) is TxTransformation tx)
                {
                    position = ExtractTranslation(tx);
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static Vec3 ExtractTranslation(TxTransformation tx)
            => new Vec3(tx[0, 3], tx[1, 3], tx[2, 3]);

        // ─────────────────────────────────────────────
        // 2. 从选中的 Face 对象获取法向量（反射多路探测）
        // ─────────────────────────────────────────────

        public static bool TryGetFaceNormal(object faceObj, out Vec3 normal)
        {
            normal = default;
            if (faceObj == null) return false;

            try
            {
                foreach (var name in new[] { "Normal", "NormalVector" })
                {
                    var p = faceObj.GetType().GetProperty(name);
                    if (p?.GetValue(faceObj, null) is TxVector v)
                    {
                        normal = new Vec3(v).Normalized();
                        return true;
                    }
                }
                var mi = faceObj.GetType().GetMethod("GetNormal", Type.EmptyTypes);
                if (mi?.Invoke(faceObj, null) is TxVector v2)
                {
                    normal = new Vec3(v2).Normalized();
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string DiagnoseFaceObject(object faceObj)
        {
            if (faceObj == null) return "null";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"类型: {faceObj.GetType().FullName}");
            sb.AppendLine("属性:");
            foreach (var p in faceObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                sb.AppendLine($"  {p.PropertyType.Name} {p.Name}");
            sb.AppendLine("方法:");
            foreach (var m in faceObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (!m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                    sb.AppendLine($"  {m.Name}");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────
        // 3. 运动学写入（基于文档确认的API）
        // ─────────────────────────────────────────────

        /// <summary>
        /// 在 ITxKinematicsModellable 上创建 Joint。
        /// CreateJoint 接受一个 CreationData 对象（反射构造，类名待运行时确认）。
        /// 返回 TxJoint，失败返回 null。
        /// </summary>
        public static TxJoint CreateJoint(ITxKinematicsModellable device, string name,
            string jointTypeName, double lowerLimit, double upperLimit,
            TxVector axisFrom, TxVector axisTo)
        {
            try
            {
                // TxJointCreationData 位于 Tecnomatix.Engineering.DataTypes 命名空间
                // CreateJoint 参数类型为 Tecnomatix.Engineering.DataTypes.TxJointCreationData
                var asmNames = new[]
                {
                    "Tecnomatix.Engineering.DataTypes, Tecnomatix.Engineering",
                    "Tecnomatix.Engineering.DataTypes, Tecnomatix.Engineering.Base",
                    "Tecnomatix.Engineering"
                };

                Type dataType = null;
                foreach (var asm in asmNames)
                {
                    dataType = Type.GetType($"Tecnomatix.Engineering.DataTypes.TxJointCreationData, {asm}");
                    if (dataType != null) break;
                }
                // 回退：直接搜所有已加载程序集
                if (dataType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        dataType = asm.GetType("Tecnomatix.Engineering.DataTypes.TxJointCreationData");
                        if (dataType != null) break;
                    }
                }
                if (dataType == null) return null;

                var data = Activator.CreateInstance(dataType);

                TrySet(data, "Name", name);
                TrySetEnum(data, "JointType", jointTypeName);
                TrySet(data, "LowerLimit", lowerLimit);
                TrySet(data, "UpperLimit", upperLimit);
                TrySet(data, "LowerSoftLimit", lowerLimit);
                TrySet(data, "UpperSoftLimit", upperLimit);

                // TxJointAxis 构造函数需要两个 TxVector 参数（无无参构造）
                if (axisFrom != null && axisTo != null)
                {
                    var axis = new TxJointAxis(axisFrom, axisTo);
                    TrySet(data, "Axis", axis);
                }

                // CreateJoint 参数类型为 DataTypes.TxJointCreationData，用反射调用避免类型不匹配
                var mi = device.GetType().GetMethod("CreateJoint");
                if (mi == null) return null;
                var result = mi.Invoke(device, new[] { data });
                return result as TxJoint;
            }
            catch { return null; }
        }

        /// <summary>
        /// 通过 TxJoint.KinematicsFunction 属性写入公式字符串
        /// （文档确认：TxJoint.KinematicsFunction get/set）
        /// </summary>
        public static bool SetJointFormula(TxJoint joint, string formula)
        {
            if (joint == null || string.IsNullOrEmpty(formula)) return false;
            try
            {
                joint.KinematicsFunction = formula;
                return true;
            }
            catch
            {
                // 回退反射
                try
                {
                    var p = joint.GetType().GetProperty("KinematicsFunction");
                    if (p != null && p.CanWrite) { p.SetValue(joint, formula, null); return true; }
                }
                catch { }
                return false;
            }
        }

        // ─────────────────────────────────────────────
        // 4. Pose 创建（基于文档确认的API）
        // 文档：ITxDevice.CreatePose(TxPoseCreationData)
        //       TxPoseCreationData.Name / PoseData
        //       TxPoseData.JointValues（类型待确认，反射探测）
        // ─────────────────────────────────────────────

        // 姿态仅包含编译后的主动关节；每次都在机构/公式验证完成后读取当前模板。
        public static bool CreatePose(ITxDevice device, string poseName,
            Dictionary<TxJoint, double> jointValues, out string err, Action<string> log = null)
        {
            err = null;
            string stage = "检查";
            void Trace(string message) => log?.Invoke($"[POSE {poseName}] {DateTime.Now:HH:mm:ss.fff} {message}");
            try
            {
                if (device == null) throw new InvalidOperationException("device为空");
                if (jointValues == null || jointValues.Count == 0) throw new InvalidOperationException("缺少关节目标值");
                stage = "读取编译后的默认姿态";
                Trace(stage);
                using (var template = device.CurrentPose)
                {
                    if (template == null) throw new InvalidOperationException("CurrentPose为空");
                    using (var defaults = new TxDevicePoseData(device, template))
                    {
                        foreach (var kv in jointValues)
                        {
                            if (kv.Key == null || !defaults.DoesHaveJoint(kv.Key))
                                throw new InvalidOperationException("默认姿态不包含目标主动轴: " + kv.Key?.Name);
                            if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value))
                                throw new InvalidOperationException("目标值无效: " + kv.Key.Name);
                            defaults.SetJointValue(kv.Key, 0);
                            Trace("默认值 " + kv.Key.Name + "=0；目标值=" + kv.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        // 某些 PS 资源对不存在的名称调用 GetPoseByName 会抛 TxUnknownErrorException。
                        // 枚举完整列表区分“没有该姿态”和“读取列表失败”，不将未知错误当作不存在。
                        stage = "读取 PoseList";
                        Trace(stage + " 开始（不调用 GetPoseByName）");
                        var poses = device.PoseList;
                        if (poses == null) throw new InvalidOperationException("PoseList返回空列表对象，无法确认已有姿态");
                        Trace("PoseList 数量=" + poses.Count);
                        TxPose pose = null;
                        foreach (ITxObject item in poses)
                        {
                            var candidate = item as TxPose;
                            if (candidate == null) continue;
                            string candidateName = candidate.Name;
                            Trace("已有姿态: " + candidateName);
                            if (!string.Equals(candidateName, poseName, StringComparison.OrdinalIgnoreCase)) continue;
                            if (pose != null && pose.Id != candidate.Id)
                                throw new InvalidOperationException("PoseList含有多个同名姿态，未自动覆盖: " + poseName);
                            pose = candidate;
                        }
                        Trace(pose == null ? "列表中不存在 " + poseName + "，进入创建阶段" :
                            "列表中找到 " + poseName + "，进入更新阶段");
                        if (pose == null)
                        {
                            stage = "CreatePose（默认值，实例级）";
                            Trace(stage + " 开始");
                            var creation = new TxPoseCreationData(poseName, defaults.PoseData) { CreateAtInstance = true };
                            pose = device.CreatePose(creation);
                            if (pose == null) throw new InvalidOperationException("CreatePose返回空对象");
                            Trace(stage + " 成功，ID=" + pose.Id);
                        }
                        else Trace("复用已有姿态，ID=" + pose.Id + "，实例级=" + pose.IsAtInstance);

                        // 新建对象成功后，单独调用 PoseData setter 写目标值，不在创建参数中注入目标值。
                        stage = "读取已创建姿态";
                        using (var actual = new TxDevicePoseData(device, pose.PoseData))
                        {
                            foreach (var kv in jointValues)
                            {
                                if (!actual.DoesHaveJoint(kv.Key)) throw new InvalidOperationException("新姿态缺少主动轴: " + kv.Key.Name);
                                actual.SetJointValue(kv.Key, kv.Value);
                            }
                            stage = "写入目标 PoseData";
                            Trace(stage + " 开始");
                            pose.PoseData = actual.PoseData;
                            Trace(stage + " 成功");
                        }
                        stage = "保存值回读";
                        using (var saved = new TxDevicePoseData(device, pose.PoseData))
                            foreach (var kv in jointValues)
                            {
                                if (!saved.DoesHaveJoint(kv.Key)) throw new InvalidOperationException("保存后缺少主动轴: " + kv.Key.Name);
                                double value = saved.GetJointValue(kv.Key);
                                Trace("回读 " + kv.Key.Name + "=" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                                if (double.IsNaN(value) || double.IsInfinity(value) || System.Math.Abs(value - kv.Value) > 1e-6)
                                    throw new InvalidOperationException("保存值不匹配: " + kv.Key.Name);
                            }
                        Trace("创建/赋值完成");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                err = poseName + " [" + stage + "] " + ex;
                Trace("失败: " + err);
                return false;
            }
        }

        public static bool CreatePose(ITxDevice device, string poseName,
            Dictionary<TxJoint, double> jointValues)
        {
            string _; return CreatePose(device, poseName, jointValues, out _);
        }

        // ── DefineZeroPosition：把当前位姿定为零位（需求4，对应"Define as Zero Position"）──
        public static bool DefineZeroPosition(ITxKinematicsModellable device, out string err)
        {
            err = null;
            try
            {
                if (device == null) { err = "device为null"; return false; }
                bool can = true;
                try { can = device.CanDefineZeroPosition(); } catch { can = true; }
                if (!can) { err = "CanDefineZeroPosition=false"; return false; }
                device.DefineZeroPosition();
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        // ── 驱动单个关节到指定值（需求4：把焊钳驱动回CLOSE）──
        public static bool SetJointCurrentValue(TxJoint joint, double value, out string err)
        {
            err = null;
            try
            {
                if (joint == null) { err = "joint为null"; return false; }
                joint.CurrentValue = value;
                return true;
            }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        // ── 定义为 Servo Gun 工具（需求3焊钳定义）──
        // API确认：DefineAsTool(ITxToolDefinitionData) 需参数。
        // 用 TxServoGunDefinitionData 设 TCPF(TxFrame) / BaseframeAbsoluteLocation /
        // NonCollidingEntities(TxObjectList)，再传入 DefineAsTool。
        public static bool DefineAsServoGun(ITxKinematicsModellable device,
            TxFrame tcpFrame, TxTransformation baseLoc,
            List<ITxObject> nonCollidingEntities, out string err, Action<string> log = null)
        {
            err = null;
            try
            {
                if (device == null) { err = "device为null"; return false; }

                if (tcpFrame == null || baseLoc == null) { err = "Servo Gun 定义需要有效 TCP Frame 和基坐标"; return false; }

                // 不检测干涉列表（构造参数，可空列表）
                var ncList = new TxObjectList();
                if (nonCollidingEntities != null)
                    foreach (var o in nonCollidingEntities)
                        if (o != null) ncList.Add(o);

                // 构造确认：TxServoGunDefinitionData(TxFrame TCPF,
                //   TxTransformation baseframeAbsoluteLocation, TxObjectList nonCollidingEntities)
                var def = new Tecnomatix.Engineering.DataTypes.TxServoGunDefinitionData(
                    tcpFrame, baseLoc, ncList);

                // 对已定义 Servo Gun，先核对配置；一致时不重复执行类型转换。
                var existing = device as TxServoGun;
                if (existing != null)
                {
                    bool same = existing.TCPF != null && existing.TCPF.Id == tcpFrame.Id;
                    var existingBase = existing.BaseframeAbsoluteLocation;
                    for (int row = 0; row < 4; row++)
                        for (int col = 0; col < 4; col++)
                            same &= existingBase != null && System.Math.Abs(existingBase[row, col] - baseLoc[row, col]) < 1e-6;
                    var expectedIds = new HashSet<string>();
                    foreach (ITxObject item in ncList) expectedIds.Add(item.Id);
                    var actualIds = new HashSet<string>();
                    foreach (ITxObject item in existing.NonCollidingEntities) actualIds.Add(item.Id);
                    same &= actualIds.SetEquals(expectedIds);
                    log?.Invoke("[工具定义] 已有 Servo Gun，TCP/BASE/不检测干涉配置一致=" + same);
                    if (same) return true;
                }
                bool can = device.CanDefineAsTool(def);
                log?.Invoke("[工具定义] CanDefineAsTool=" + can + ", 已有ServoGun=" + (existing != null));
                if (!can) { err = "SDK拒绝工具定义；已有定义的TCP、BASE或不检测干涉列表与请求不同"; return false; }
                device.DefineAsTool(def);
                log?.Invoke("[工具定义] DefineAsTool 完成");
                return true;
            }
            catch (Exception ex) { err = (err ?? "") + " | " + ex.Message; return false; }
        }

        // ─────────────────────────────────────────────
        // 5. Logic Block（反射，接口未在文档中明确）
        // ─────────────────────────────────────────────

        public static bool CreateSimpleLogicBlock(ITxDevice device,
            string signalName, string poseOn, string poseOff)
        {
            try
            {
                // 尝试通过 ITxComponent 接口找 CreateLogicBlock
                var comp = device as ITxComponent;
                if (comp == null) return false;

                var mi = comp.GetType().GetMethod("CreateLogicBlock");
                if (mi == null) return false;

                var lbType = Type.GetType("Tecnomatix.Engineering.TxLogicBlockCreationData, Tecnomatix.Engineering");
                if (lbType == null) return false;

                var lbData = Activator.CreateInstance(lbType);
                TrySet(lbData, "Name", "GunControl");
                var lb = mi.Invoke(comp, new[] { lbData });
                if (lb == null) return false;

                TryCall(lb, "AddInputSignal", signalName, "Boolean");
                TryCall(lb, "AddPoseTransition", signalName, true, poseOn);
                TryCall(lb, "AddPoseTransition", signalName, false, poseOff);
                return true;
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────
        // 内部工具
        // ─────────────────────────────────────────────

        private static void TrySet(object obj, string prop, object value)
        {
            try { obj.GetType().GetProperty(prop)?.SetValue(obj, value, null); } catch { }
        }

        private static void TrySetEnum(object obj, string prop, string enumName)
        {
            try
            {
                var p = obj.GetType().GetProperty(prop);
                if (p == null) return;
                if (p.PropertyType.IsEnum)
                {
                    var val = Enum.Parse(p.PropertyType, enumName, true);
                    p.SetValue(obj, val, null);
                }
                else
                {
                    p.SetValue(obj, enumName, null);
                }
            }
            catch { }
        }

        private static void TryCall(object obj, string method, params object[] args)
        {
            try { obj.GetType().GetMethod(method)?.Invoke(obj, args); } catch { }
        }

        // ─────────────────────────────────────────────
        // 高亮显示：选中对象变色，取消时恢复
        // ─────────────────────────────────────────────
        // ITxDisplayableObject.Color 是属性（TxColor类型），不是SetColor方法
        // 用预定义颜色常量（静态属性，避免构造函数参数类型不确定）

        /// <summary>不同 Link 的高亮配色（与图例对应）</summary>
        public static TxColor ColorFixed => TxColor.TxColorOrange; // 固定枪体
        public static TxColor ColorInput => TxColor.TxColorGreen;  // 活塞缸体
        public static TxColor ColorCoupler => TxColor.TxColorYellow; // 活塞杆
        public static TxColor ColorOutput => TxColor.TxColorBlue;  // 动臂
        public static TxColor ColorLnk2 => TxColor.TxColorMagenta;  // 静臂（磨量补偿）

        public static void RefreshView()
        {
            try { TxApplication.RefreshDisplay(); }
            catch { } // 工程关闭或视图不可用时不打断向导。
        }

        /// <summary>高亮一个对象为指定颜色（ITxDisplayableObject.Color 属性）</summary>
        public static void Highlight(ITxObject obj, TxColor color)
        {
            if (obj == null) return;
            try
            {
                var disp = obj as ITxDisplayableObject;
                if (disp != null) disp.Color = color;
            }
            catch { }
        }

        /// <summary>高亮一个对象（默认黄色）</summary>
        public static void Highlight(ITxObject obj)
        {
            Highlight(obj, TxColor.TxColorYellow);
        }

        /// <summary>取消高亮，恢复原色（ITxDisplayableObject.RestoreColor）</summary>
        public static void Unhighlight(ITxObject obj)
        {
            if (obj == null) return;
            try
            {
                var disp = obj as ITxDisplayableObject;
                if (disp != null) disp.RestoreColor();
            }
            catch { }
        }

        /// <summary>批量高亮为指定颜色</summary>
        public static void HighlightAll(System.Collections.Generic.IEnumerable<ITxObject> objs, TxColor color)
        {
            if (objs == null) return;
            foreach (var o in objs) Highlight(o, color);
        }

        /// <summary>批量高亮</summary>
        public static void HighlightAll(System.Collections.Generic.IEnumerable<ITxObject> objs)
        {
            if (objs == null) return;
            foreach (var o in objs) Highlight(o);
        }

        /// <summary>批量取消高亮</summary>
        public static void UnhighlightAll(System.Collections.Generic.IEnumerable<ITxObject> objs)
        {
            if (objs == null) return;
            foreach (var o in objs) Unhighlight(o);
        }

        // ── 在 Component 内创建 Frame（需求3：TCP点选位置时建TCPF）──
        public static TxFrame CreateFrame(ITxComponent comp, string name,
            TxTransformation absLoc, out string err)
        {
            err = null;
            try
            {
                var creator = comp as ITxFrameCreation;
                if (creator == null) { err = "组件不支持ITxFrameCreation"; return null; }
                var cd = new TxFrameCreationData();
                cd.Name = name;
                if (absLoc != null) cd.AbsoluteLocation = absLoc;
                var frame = creator.CreateFrame(cd);
                return frame;
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        public static bool IsDescendantOf(ITxObject obj, ITxObject root)
        {
            if (obj == null || root == null) return false;
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                ITxObject current = obj;
                for (int depth = 0; depth < 256 && current != null; depth++)
                {
                    if (current.Id == root.Id) return true;
                    if (!seen.Add(current.Id)) return false;
                    // Collection 本身就是父对象，不能再猜它的 OwningObject。
                    current = current.Collection as ITxObject;
                }
            }
            catch { return false; }
            return false;
        }

        public static bool ValidateLinkBody(ITxObject obj, ITxObject root, out string error)
        {
            error = null;
            try
            {
                if (obj == null || root == null) { error = "对象或目标焊钳为空"; return false; }
                string description = obj.Name + " [ID=" + obj.Id + ", CLR=" + ((object)obj).GetType().FullName + "]";
                if (obj.Id == root.Id || obj is TxFrame || obj is ITxFace || obj is TxJoint || obj is TxKinematicLink || obj is ITxDevice ||
                    (!(obj is ITxGeometry) && !(obj is ITxComponent)))
                { error = "请选择焊钳内实体或零件，不能选择工序、坐标系、设备根节点或运动学节点: " + description; return false; }
                if (!obj.IsValid()) { error = "资源已失效: " + description; return false; }
                if (!IsDescendantOf(obj, root))
                { error = "资源不属于目标焊钳，或无法确认归属: " + description; return false; }
                return true;
            }
            catch (Exception ex) { error = "无法验证几何资源: " + ex.GetType().Name + " - " + ex.Message; return false; }
        }

        // ── 收集 Component 下所有后代（保留兼容，但归属判断改用 IsDescendantOf）──
        public static System.Collections.Generic.HashSet<ITxObject> CollectDescendants(ITxComponent comp)
        {
            var set = new System.Collections.Generic.HashSet<ITxObject>();
            if (comp == null) return set;
            try
            {
                var coll = comp as ITxObjectCollection;
                if (coll != null)
                {
                    var filter = new TxTypeFilter(typeof(ITxObject));
                    var all = coll.GetAllDescendants(filter);
                    if (all != null)
                        foreach (ITxObject o in all) if (o != null) set.Add(o);
                }
            }
            catch { }
            set.Add(comp);   // 自身也算
            return set;
        }
    }
}
