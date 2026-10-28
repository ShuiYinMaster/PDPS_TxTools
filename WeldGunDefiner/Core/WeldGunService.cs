using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Tecnomatix.Engineering;
using TxTools.WeldGunDefiner.Math;

namespace TxTools.WeldGunDefiner.Core
{
    public class WeldGunModel
    {
        public Vec3 PlaneNormal { get; set; }

        // 铰点坐标：由UI层直接写入（Frame取原点，几何体取Picked点击位置）
        public Vec3 WorldO { get; set; }
        public Vec3 WorldA { get; set; }
        public Vec3 WorldC { get; set; }
        public ITxObject HingeO { get; set; }
        public ITxObject HingeA { get; set; }
        public ITxObject HingeC { get; set; }
        public bool HasO { get; set; }
        public bool HasA { get; set; }
        public bool HasC { get; set; }
        public bool AllHingesReady => HasO && HasA && HasC;

        // 偏离平面误差（有参考平面时才有意义）
        public double ErrO { get; set; }
        public double ErrA { get; set; }
        public double ErrC { get; set; }
        public bool ErrorsAcceptable => ErrO < 1.0 && ErrA < 1.0 && ErrC < 1.0;

        // 几何体绑定列表（用于Link创建）
        public List<ITxObject> FixedLinkBodies = new List<ITxObject>();
        public List<ITxObject> InputLinkBodies = new List<ITxObject>();
        public List<ITxObject> CouplerLinkBodies = new List<ITxObject>();
        public List<ITxObject> OutputLinkBodies = new List<ITxObject>();
        public List<ITxObject> Lnk2Bodies = new List<ITxObject>();

        // TCP（电极帽）：用于j1轴方向 + 臂长计算 + 焊钳TCP定义
        public ITxObject ObjStaticTip { get; set; }   // 静电极帽
        public ITxObject ObjMovingTip { get; set; }   // 动电极帽
        public Vec3 WorldStaticTip { get; set; }
        public Vec3 WorldMovingTip { get; set; }
        public Vec3 WorldGunTcp { get; set; }
        public TxFrame TcpFrame { get; set; }   // 焊钳TCP的Frame对象(选已有或新建TCPF)
        public TxTransformation BaseLocation { get; set; } // 可选的世界坐标变换；空时使用组件自身坐标

        // 需求4：OPEN状态适配勾选项
        public bool OpenStateAdapt { get; set; } = false;

        // 计算结果
        public double ArmLength { get; set; }   // 臂长 R = 动TCP投影→O投影
        public double MaxOpenGap { get; set; }   // 最大张开距离（反算）

        public ReferencePlane Plane { get; set; }
        public GunMechanismParams Mechanism { get; set; }
        public bool MechanismReady => Mechanism != null;

        public double S0 { get; set; } = 0.0;
        public double PistonStroke { get; set; } = 100.0;  // 用户手填
        public double OpenGap { get; set; } = 185.0;  // 用户手填
        public double WearAllowance { get; set; } = 10.0;   // 用户手填

        // 关节名称
        public string InputJ1_Name { get; set; } = "input_j1";

        // 生成结果缓存
        public GunFormulas Formulas { get; set; }

        // 目标设备（同时实现 ITxKinematicsModellable 和 ITxDevice）
        public ITxKinematicsModellable TargetKinematics { get; set; }
        public ITxDevice TargetDevice { get; set; }
        public ITxComponent TargetComponent { get; set; }

        public string J1_Name { get; set; } = "J_Piston";
        public string J2_Name { get; set; } = "J_Arm";
        public string J3_Name { get; set; } = "J_Gap";  // 保留备用（可不用）
    }

    public class WeldGunService
    {
        private readonly WeldGunModel _model;

        public WeldGunService(WeldGunModel model) { _model = model; }

        private const string SetupAttribute = "TxTools.WeldGunSetup.v1";

        private static XElement PointElement(string name, Vec3 point)
            => new XElement(name, new XAttribute("x", point.X), new XAttribute("y", point.Y), new XAttribute("z", point.Z));
        private static Vec3 ReadPoint(XElement root, string name)
        {
            var point = root.Element(name) ?? throw new InvalidOperationException("缺少选点 " + name);
            return new Vec3((double)point.Attribute("x"), (double)point.Attribute("y"), (double)point.Attribute("z"));
        }
        private static XElement TransformElement(string name, TxTransformation location)
        {
            var node = new XElement(name);
            if (location != null)
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        node.SetAttributeValue("m" + row + col, location[row, col]);
            return node;
        }
        private static TxTransformation ReadTransform(XElement node)
        {
            if (node == null || node.Attribute("m00") == null) return null;
            var value = new TxTransformation();
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                    value[row, col] = (double)node.Attribute("m" + row + col);
            return value;
        }

        private void SaveSetup()
        {
            var links = RprrBuilder.GetOwnedLinks(_model.TargetKinematics);
            var root = new XElement("WeldGunSetup",
                new XAttribute("hingeO", _model.HingeO?.Id ?? ""),
                new XAttribute("hingeA", _model.HingeA?.Id ?? ""),
                new XAttribute("hingeC", _model.HingeC?.Id ?? ""),
                new XAttribute("staticTip", _model.ObjStaticTip?.Id ?? ""),
                new XAttribute("movingTip", _model.ObjMovingTip?.Id ?? ""),
                new XAttribute("tcp", _model.TcpFrame?.Id ?? ""),
                new XAttribute("stroke", _model.PistonStroke), new XAttribute("gap", _model.OpenGap),
                new XAttribute("wear", _model.WearAllowance), new XAttribute("openAdapt", _model.OpenStateAdapt),
                PointElement("O", _model.WorldO), PointElement("A", _model.WorldA),
                PointElement("C", _model.WorldC), PointElement("N", _model.PlaneNormal),
                TransformElement("base", _model.BaseLocation),
                TransformElement("device", (_model.TargetComponent as ITxLocatableObject)?.AbsoluteLocation));
            links["fixed_link"].SetAttribute(new TxStringAttribute(SetupAttribute, root.ToString(SaveOptions.DisableFormatting)));
        }

        public bool TryRestoreSetup(out string status)
        {
            status = null;
            try
            {
                var links = RprrBuilder.GetOwnedLinks(_model.TargetKinematics);
                foreach (var name in new[] { "fixed_link", "input_link", "coupler_link", "output_link", "dummy_link", "lnk1", "lnk2" })
                    if (!links.ContainsKey(name)) { status = "插件 Link 不完整，请完成第二步"; return false; }
                var attribute = links["fixed_link"].GetAttribute(SetupAttribute) as TxStringAttribute;
                if (attribute == null) { status = "已有 Link 缺少选点记录，请完成一次第二步"; return false; }
                var root = XElement.Parse(attribute.Value);
                ITxObject Resolve(string name)
                {
                    var id = (string)root.Attribute(name);
                    var obj = string.IsNullOrEmpty(id) ? null : TxApplication.ActiveDocument.GetObjectById(id);
                    if (obj == null || !obj.IsValid()) throw new InvalidOperationException("已保存资源失效: " + name);
                    return obj;
                }
                var hingeO = Resolve("hingeO"); var hingeA = Resolve("hingeA"); var hingeC = Resolve("hingeC");
                var staticTip = Resolve("staticTip");
                var movingTip = Resolve("movingTip");
                var tcp = Resolve("tcp") as TxFrame;
                if (tcp == null) throw new InvalidOperationException("已保存 TCP 不是 Frame");
                Vec3 o = ReadPoint(root, "O"), a = ReadPoint(root, "A"), c = ReadPoint(root, "C"), n = ReadPoint(root, "N");
                var savedDevice = ReadTransform(root.Element("device"));
                var currentDevice = (_model.TargetComponent as ITxLocatableObject)?.AbsoluteLocation;
                if (savedDevice != null && currentDevice != null)
                {
                    var delta = currentDevice * savedDevice.Inverse;
                    Vec3 Transform(Vec3 v, bool normal) => new Vec3(
                        delta[0,0]*v.X + delta[0,1]*v.Y + delta[0,2]*v.Z + (normal ? 0 : delta[0,3]),
                        delta[1,0]*v.X + delta[1,1]*v.Y + delta[1,2]*v.Z + (normal ? 0 : delta[1,3]),
                        delta[2,0]*v.X + delta[2,1]*v.Y + delta[2,2]*v.Z + (normal ? 0 : delta[2,3]));
                    o = Transform(o, false); a = Transform(a, false); c = Transform(c, false); n = Transform(n, true);
                }
                var bodyLists = new List<ITxObject>[5];
                var bodyNames = new[] { "fixed_link", "input_link", "coupler_link", "output_link", "lnk2" };
                for (int index = 0; index < 5; index++) bodyLists[index] = RprrBuilder.ReadLinkBodies(links[bodyNames[index]]);
                double stroke = (double)root.Attribute("stroke"), gap = (double)root.Attribute("gap"), wear = (double)root.Attribute("wear");
                var baseLocation = ReadTransform(root.Element("base"));
                bool openAdapt = (bool)root.Attribute("openAdapt");
                _model.HingeO = hingeO; _model.HingeA = hingeA; _model.HingeC = hingeC;
                _model.WorldO = o; _model.WorldA = a; _model.WorldC = c; _model.PlaneNormal = n;
                _model.HasO = _model.HasA = _model.HasC = true;
                _model.ObjStaticTip = staticTip; _model.ObjMovingTip = movingTip; _model.TcpFrame = tcp;
                var loc = tcp.AbsoluteLocation;
                _model.WorldGunTcp = new Vec3(loc[0,3], loc[1,3], loc[2,3]);
                _model.BaseLocation = baseLocation;
                _model.PistonStroke = stroke; _model.OpenGap = gap; _model.WearAllowance = wear; _model.OpenStateAdapt = openAdapt;
                var targets = new[] { _model.FixedLinkBodies, _model.InputLinkBodies, _model.CouplerLinkBodies, _model.OutputLinkBodies, _model.Lnk2Bodies };
                for (int index = 0; index < 5; index++) { targets[index].Clear(); targets[index].AddRange(bodyLists[index]); }
                if (!UpdateHingePoints(out status) || !ComputeMechanismFromPoints(out status)) return false;
                status = "已恢复插件 Link、几何绑定与选点，可跳过第二步";
                return true;
            }
            catch (Exception ex) { status = "无法恢复已有定义，请完成第二步: " + ex.Message; return false; }
        }

        // ── 参考平面 ──

        // ── 铰点投影 ──
        // 参考平面的作用：将三点投影到同一平面，消除轴向建模误差，
        // 保证后续所有2D几何计算在同一平面内进行。
        // 两种情况下都会执行投影，区别只是平面法向量的来源：
        //   有参考平面 → 用侧面法向量，误差=三点偏离该平面的距离（体现建模误差大小）
        //   无参考平面 → 用三点叉积算法向量，三点本身定义平面，投影后误差理论为0
        public bool UpdateHingePoints(out string error)
        {
            error = null;
            if (!_model.HasO) { error = "O点坐标未设置"; return false; }
            if (!_model.HasA) { error = "A点坐标未设置"; return false; }
            if (!_model.HasC) { error = "C点坐标未设置"; return false; }

            Vec3 wO = _model.WorldO, wA = _model.WorldA, wC = _model.WorldC;

            // 三铰点叉积作为"基准法向"：由 O→A→C 顺序唯一确定，与选取哪一侧无关
            Vec3 OA = wA - wO;
            Vec3 OC = wC - wO;
            Vec3 refNormal = Vec3.Cross(OA, OC);
            bool hasRef = refNormal.Length > 1e-6;
            if (hasRef) refNormal = refNormal.Normalized();

            // 法向 N 来自 A 点拾取（Face法向或Frame的Z轴）
            Vec3 normal = _model.PlaneNormal;
            if (normal.Length > 0.5)
            {
                normal = normal.Normalized();
                // 关键：把拾取的面法向对齐到基准方向。
                // 焊钳两侧选面时面法向相反，但叉积基准始终一致，
                // 据此翻转 N，保证 N 方向稳定（解决"另一侧选取轴反掉"）
                if (hasRef && Vec3.Dot(normal, refNormal) < 0)
                    normal = new Vec3(-normal.X, -normal.Y, -normal.Z);

                _model.PlaneNormal = normal;
                _model.Plane = new ReferencePlane(normal, wA);
                _model.ErrO = _model.Plane.DistanceTo(wO);
                _model.ErrA = _model.Plane.DistanceTo(wA);  // A在平面上，应≈0
                _model.ErrC = _model.Plane.DistanceTo(wC);
            }
            else
            {
                // A点未提供法向：直接用三点叉积
                if (!hasRef)
                {
                    error = "A点未确定法向且三点共线，无法确定运动平面";
                    return false;
                }
                _model.PlaneNormal = refNormal;
                _model.Plane = new ReferencePlane(refNormal, wA);
                _model.ErrO = _model.ErrA = _model.ErrC = -1; // 自动
            }
            return true;
        }

        // ── 机构参数计算 ──
        public bool ComputeMechanismFromPoints(out string error)
        {
            error = null;
            if (_model.Plane == null) { error = "参考平面未初始化"; return false; }

            if (!PsSdkHelper.TryGetWorldPosition(_model.ObjStaticTip, out Vec3 wStatic))
            { error = "无法获取静电极帽坐标"; return false; }
            if (!PsSdkHelper.TryGetWorldPosition(_model.ObjMovingTip, out Vec3 wMoving))
            { error = "无法获取动电极帽坐标"; return false; }

            _model.WorldStaticTip = wStatic;
            _model.WorldMovingTip = wMoving;

            var plane = _model.Plane;
            Vec2 O2d = plane.To2D(_model.WorldO);
            Vec2 A2d = plane.To2D(_model.WorldA);
            Vec2 C2d = plane.To2D(_model.WorldC);
            Vec2 staticTip2d = plane.To2D(wStatic);
            Vec2 movingTip2d = plane.To2D(wMoving);

            // 活塞方向：从活塞杆Body的Z轴推算，或回退到A→C
            Vec2 pistonDir2d = ComputePistonDir(plane, A2d);

            double R_arm = (movingTip2d - O2d).Length;
            double d_static = (staticTip2d - O2d).Length;
            double theta_static = System.Math.Atan2(staticTip2d.Y - O2d.Y, staticTip2d.X - O2d.X);

            var mech = new GunMechanismParams
            {
                R_arm = R_arm,
                d_static = d_static,
                Theta_static = theta_static,
                S0 = _model.S0,
                PistonStroke = _model.PistonStroke,
                OpenGap = _model.OpenGap,
                WearAllowance = _model.WearAllowance
            };
            mech.ComputeFromPoints(O2d, A2d, C2d, pistonDir2d, _model.S0);
            _model.Mechanism = mech;
            return true;
        }

        private Vec2 ComputePistonDir(ReferencePlane plane, Vec2 A2d)
        {
            // 活塞方向从铰点几何推算：A→C 在平面内的方向
            Vec2 C2d = plane.To2D(_model.WorldC);
            Vec2 d = new Vec2(C2d.X - A2d.X, C2d.Y - A2d.Y);
            double len = d.Length;
            if (len > 1e-6) return new Vec2(d.X / len, d.Y / len);
            return new Vec2(0, 1); // 默认
        }

        // ── 生成运动学：完整复刻RPRR向导 ──
        public GenerationResult GenerateAll()
        {
            using (var undoScope = TxTools.Common.SceneUndoScope.Begin("焊钳定义"))
            {
                var result = new GenerationResult();

                if (_model.TargetKinematics == null)
                { result.AddError("未指定目标设备"); return result; }
                if (_model.Plane == null)
                { result.AddError("参考平面未初始化，请先完成铰点选取"); return result; }

                // ── 6. 建模状态检测（在UI层已做，这里再兜底）──
                if (!_model.TargetKinematics.IsOpenForKinematicsModeling)
                {
                    result.AddError("设备未开启运动学建模状态，请在向导中启用");
                    return result;
                }

                // ── TCP 坐标（从选取的 ITxObject 读取世界坐标）──
                Vec3 worldStaticTip = _model.WorldStaticTip;
                Vec3 worldMovingTip = _model.WorldMovingTip;
                if (_model.ObjStaticTip != null)
                    PsSdkHelper.TryGetWorldPosition(_model.ObjStaticTip, out worldStaticTip);
                if (_model.ObjMovingTip != null)
                    PsSdkHelper.TryGetWorldPosition(_model.ObjMovingTip, out worldMovingTip);

                // ── 臂长计算：动TCP投影到参考平面的点 → O投影点的距离 ──
                double R_arm = 0;
                if (_model.Plane != null && _model.ObjMovingTip != null)
                {
                    Vec3 projMovTcp = _model.Plane.Project(worldMovingTip);
                    Vec3 projO = _model.Plane.Project(_model.WorldO);
                    R_arm = (projMovTcp - projO).Length;
                    _model.ArmLength = R_arm;
                }

                // ── 最大张开距离反算（4. 注入开口值）──
                // 活塞最大行程对应的动臂最大转角，用转角×臂长算焊钳开口
                // OpenGap 用户填的值作为j1 Low的绝对值，同时用于公式换算
                // MaxOpenGap 用臂长×活塞行程对应角度几何算（更精确）
                // 当前先用用户输入值作为 j1 Low limit，后续可用臂长反校
                _model.MaxOpenGap = _model.OpenGap;  // 先等于用户输入，后续精化

                // ── 活塞方向：从铰点几何推算（不依赖活塞杆几何体）──
                Vec3 pistonDir = ComputePistonDirFromHinges();

                // 构建RprrParams
                var p = new RprrParams
                {
                    WorldO = _model.WorldO,
                    WorldA = _model.WorldA,
                    WorldC = _model.WorldC,
                    N = _model.PlaneNormal,
                    PistonDir = pistonDir,
                    Plane = _model.Plane,
                    FixedLinkBodies = _model.FixedLinkBodies,
                    InputLinkBodies = _model.InputLinkBodies,
                    CouplerLinkBodies = _model.CouplerLinkBodies,
                    OutputLinkBodies = _model.OutputLinkBodies,
                    Lnk2Bodies = _model.Lnk2Bodies,
                    WorldStaticTip = worldStaticTip,
                    WorldMovingTip = worldMovingTip,
                    R_arm = R_arm,
                    S0 = _model.S0,
                    PistonStroke = _model.PistonStroke,
                    OpenGap = _model.MaxOpenGap,
                    WearAllowance = _model.WearAllowance,
                    OpenStateAdapt = _model.OpenStateAdapt,
                };

                // 执行构建
                var builder = new RprrBuilder(_model.TargetKinematics, p);
                builder.LinksCreated = () =>
                {
                    try { SaveSetup(); }
                    catch (Exception ex) { result.AddInfo("[!] 无法保存下次复用的选点记录: " + ex.Message); }
                };
                builder.OpenAdaptationStarting = () =>
                {
                    var links = RprrBuilder.GetOwnedLinks(_model.TargetKinematics);
                    links["fixed_link"].RemoveAttribute(SetupAttribute);
                };
                string buildErr;
                if (!builder.Build(out buildErr))
                {
                    result.AddError($"RPRR构建失败: {buildErr}。如有部分变更，请在当前 PS 工程按 Ctrl+Z 撤销本批次；未自动回滚。");
                    result.AddInfo(builder.Log);
                    return result;
                }
                if (!builder.VerifyKinematics(out buildErr))
                {
                    result.AddError("机构验证失败: " + buildErr);
                    result.AddInfo(builder.Log);
                    return result;
                }
                _model.TargetDevice = _model.TargetKinematics as ITxDevice;
                result.AddSuccess("RPRR运动学结构创建完成（7 Link + 6 Joint）");
                result.AddInfo(builder.Log);

                // 公式常数（用于日志）
                var formulas = builder.BuildFormulas();
                result.AddInfo("");
                result.AddInfo("=== 几何常数 ===");
                result.AddInfo($"a=|OA|={formulas.Const_a:F2}  b=连杆={formulas.Const_b:F2}  r=|OC|={formulas.Const_r:F2}  s0={formulas.Const_s0:F2}");

                // 自动姿态是可选步骤，失败仅提示手动补建，不阻止后续焊钳定义。
                bool poseReady = builder.VerifyReadyForPoses(out string poseReadyError);
                if (poseReady && _model.TargetDevice != null)
                {
                    var closeVals = new System.Collections.Generic.Dictionary<TxJoint, double>();
                    var openVals = new System.Collections.Generic.Dictionary<TxJoint, double>();
                    if (builder.J1 != null)
                    {
                        // CLOSE = 零位，OPEN = 负开口；正磨量不属于闭合姿态。
                        closeVals[builder.J1] = 0;
                        openVals[builder.J1] = formulas.J1_Low;
                    }
                    string pcErr, poErr;
                    bool pc = PsSdkHelper.CreatePose(_model.TargetDevice, "CLOSE", closeVals, out pcErr);
                    result.AddInfo(pc ? "[OK] Pose: CLOSE" : "[!] CLOSE 自动创建失败，可手动创建（j1=0）: " + pcErr);
                    bool po = PsSdkHelper.CreatePose(_model.TargetDevice, "OPEN", openVals, out poErr);
                    result.AddInfo(po ? "[OK] Pose: OPEN" : $"[!] OPEN 自动创建失败，可手动创建（j1={formulas.J1_Low:F2}）: " + poErr);

                    if (pc && po)
                    {
                        bool lb = PsSdkHelper.CreateSimpleLogicBlock(_model.TargetDevice, "GUN_OPEN", "OPEN", "CLOSE");
                        result.AddInfo(lb ? "[OK] Logic Block" : "[!] Logic Block 需手动配置");
                    }
                    else result.AddInfo("[!] 姿态未全部就绪，请手动补建后配置 Logic Block");
                }
                else
                {
                    result.AddInfo("[!] 自动姿态前置检查未通过，已跳过 CLOSE/OPEN: " + poseReadyError);
                    result.AddInfo($"[!] 可手动创建 CLOSE（j1=0）、OPEN（j1={formulas.J1_Low:F2}），再配置 Logic Block");
                }

                // ── 焊钳定义（Servo Gun）：写完Pose后添加 ──
                // Tool=Servo Gun, TCP=选取的TcpFrame, Base=焊钳自身坐标,
                // 不检测干涉=静/动电极帽（可留空）
                if (_model.TargetKinematics != null)
                {
                    // Base = 焊钳组件自身坐标系
                    TxTransformation baseLoc = _model.BaseLocation;
                    if (baseLoc == null)
                    {
                        try { baseLoc = (_model.TargetComponent as ITxLocatableObject)?.AbsoluteLocation; }
                        catch { baseLoc = null; }
                    }

                    // 不检测干涉的实体：两个电极帽
                    var nonColliding = new System.Collections.Generic.List<ITxObject>();
                    if (_model.ObjStaticTip != null) nonColliding.Add(_model.ObjStaticTip);
                    if (_model.ObjMovingTip != null) nonColliding.Add(_model.ObjMovingTip);

                    string gunErr;
                    bool gun = PsSdkHelper.DefineAsServoGun(
                        _model.TargetKinematics, _model.TcpFrame, baseLoc, nonColliding, out gunErr);
                    if (!gun) result.AddError(gunErr);
                    result.AddInfo(gun
                        ? $"[OK] 焊钳定义: Servo Gun (TCP={(_model.TcpFrame != null ? _model.TcpFrame.Name : "未设")}, 不检测干涉×{nonColliding.Count})"
                        : "[!] 焊钳定义部分失败 - " + gunErr);
                }

                result.Success = result.Errors.Count == 0;
                return result;

            }
        }

        // 活塞方向从铰点几何推算：A→C 方向投影到参考平面内
        private Vec3 ComputePistonDirFromHinges()
        {
            Vec3 AC = _model.WorldC - _model.WorldA;
            double len = AC.Length;
            if (len < 1e-6) return new Vec3(0, 1, 0);
            Vec3 dir = new Vec3(AC.X / len, AC.Y / len, AC.Z / len);
            // 投影到参考平面内（去除轴向分量）
            if (_model.Plane != null)
            {
                Vec3 n = _model.PlaneNormal;
                double dot = Vec3.Dot(dir, n);
                dir = new Vec3(dir.X - n.X * dot, dir.Y - n.Y * dot, dir.Z - n.Z * dot).Normalized();
            }
            return dir;
        }
        private static TxJoint FindJointByName(ITxKinematicsModellable device, string name)
        {
            if (device == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var joints = device.Joints;
                if (joints == null) return null;
                foreach (ITxObject obj in joints)
                {
                    var j = obj as TxJoint;
                    if (j != null && j.Name == name) return j;
                }
            }
            catch { }
            return null;
        }
    }
    public class GenerationResult
    {
        public bool Success { get; set; }
        public List<string> Successes { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Infos { get; } = new List<string>();
        public GunFormulas Formulas { get; set; }

        public void AddSuccess(string msg) => Successes.Add("[OK] " + msg);
        public void AddError(string msg) => Errors.Add("[X] " + msg);
        public void AddInfo(string msg) => Infos.Add("[i] " + msg);

        public string Summary()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in Successes) sb.AppendLine(s);
            foreach (var i in Infos) sb.AppendLine(i);
            foreach (var e in Errors) sb.AppendLine(e);
            return sb.ToString();
        }
    }
}
