// SpotModels.cs  —  C# 7.3
// 焊点分配插件的数据模型：模式枚举 / 焊点·操作快照 / 匹配结果 / 执行计划
//
// 设计要点：
//   · SpotData 同时持有 TxWeldPoint(写位置) 与 它的 TxWeldLocationOperation(排序/反查父操作)，
//     这样写入层不依赖 PsReader.PointInfo（PointInfo 不保留底层 weld point 引用）。
//   · 矩阵一律用 double[16] row-major（与 PsReader.TxToArr 对齐），需要 TxTransformation 时
//     用 PsReader.ArrToTxPublic 还原，避免到处持有可变的 TxTransformation。

using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.ExportGun; // PointInfo / PointType / PsReader 静态工具

namespace TxTools.WeldSpotAllocator
{
    /// <summary>三种工作模式。</summary>
    public enum AllocMode
    {
        UpdatePosition, // A：按名/位置匹配，更新已分配焊点的坐标，保留分配
        NewSpot,        // B：游离新焊点参照最近的已分配焊点，建立分配
        Symmetric       // C：右侧游离焊点参照左侧（镜像后）分配
    }

    /// <summary>点匹配的命中依据。</summary>
    public enum MatchBy { None, Name, Position, NameAndPosition }

    /// <summary>镜像后焊枪工具系手性修正：再绕工具自身某轴翻 180°。</summary>
    public enum FlipAxis { None, X, Y, Z }

    /// <summary>一个焊点或过渡点的快照。</summary>
    public sealed class SpotData
    {
        public string Name;          // 焊点/via 名
        public PointType Kind;       // WeldPoint / PathPoint(=via) / ...
        public double[] Matrix;      // 世界系位姿 double[16] row-major
        internal double[] TrajectoryMatrix; // 焊接位置操作的世界位姿，用于复制旋转
        public double[] Position;    // {m[3], m[7], m[11]}，匹配距离用
        public double[] SymCenter;   // 该点对称中心（绑定零件分身 AbsoluteLocation），可空
        internal string ReferenceFrameSource; // 基准来源或无法确定基准的原因

        public ITxObject Raw;        // 底层：TxWeldPoint（焊点）或 via 操作对象
        public ITxObject LocOp;      // 焊点所属的 TxWeldLocationOperation；via 时与 Raw 同
    }

    /// <summary>一条焊接操作（轨迹）及其有序点列。</summary>
    public sealed class OpData
    {
        public string Name;
        public ITxObject Raw;                          // TxWeldOperation
        public readonly List<SpotData> Spots = new List<SpotData>();   // 仅焊点
        public readonly List<SpotData> Vias  = new List<SpotData>();   // 仅过渡点
        public readonly List<SpotData> Ordered = new List<SpotData>(); // 焊点+via 按轨迹顺序

        public int SpotCount => Spots.Count;

        /// <summary>焊点质心（位置匹配/操作配对用），无焊点返回 null。</summary>
        public double[] Centroid()
        {
            if (Spots.Count == 0) return null;
            double x = 0, y = 0, z = 0;
            foreach (var s in Spots) { x += s.Position[0]; y += s.Position[1]; z += s.Position[2]; }
            int n = Spots.Count;
            return new[] { x / n, y / n, z / n };
        }
    }

    /// <summary>单个点的匹配结果（ref ↔ target）。</summary>
    public sealed class SpotMatch
    {
        public SpotData Ref;       // 参考点（已分配）
        public SpotData Target;    // 目标点（待更新/待分配）
        public double Dist;        // 所用坐标系中的实际距离，无法比较时为 NaN
        public MatchBy By;
        public bool Mirrored;      // 参考点经过车件换系或镜像
        public double[] RefMirror; // 变换后的参考制造焊点位姿，可空
        internal double[] RotationInTargetWorld;
        internal string FrameDetails;
    }

    /// <summary>操作配对（A 模式用；B/C 的 target 为待分配操作时 TargetOp 可空）。</summary>
    public sealed class OpMatch
    {
        public OpData RefOp;
        public OpData TargetOp;
        internal double[] ViaReferenceFrame;
        internal double[] ViaTargetFrame;
        public readonly List<SpotMatch> Matches = new List<SpotMatch>();
    }

    /// <summary>一次执行的完整计划（预览用，确认后交给 SpotWriter）。</summary>
    public sealed class AllocPlan
    {
        internal const string DefaultMappedSuffix = "_Mapped";
        internal string MappedSuffix = DefaultMappedSuffix;

        public AllocMode Mode;
        public bool CopyVias;        // C 模式镜像已随轨迹复制的过渡点
        public bool DiffFrame;       // 两侧按车件局部坐标匹配，写入时转回目标世界系
        public bool CopyRotation;    // 复制（镜像）参考焊点旋转姿态到匹配焊点
        public bool CopyParams;      // 复制参考焊点轨迹参数（速度/zone/duration/OLP）
        public bool ConsumeTargets;  // 将目标位置操作从原轨迹移入，否则复制位置操作
        public FlipAxis Flip;        // 镜像姿态修正（绕工具轴翻 180°，实物试定）
        public readonly List<OpMatch> OpMatches = new List<OpMatch>();
        public readonly List<string> Warnings = new List<string>();
        internal readonly List<string> WriteBlockers = new List<string>();

        internal bool UsesReferenceFrame => Mode == AllocMode.Symmetric || DiffFrame;
        internal string GetMappedName(OpData reference) => reference.Name + MappedSuffix;

        public int TotalMatches
        {
            get { int n = 0; foreach (var o in OpMatches) n += o.Matches.Count; return n; }
        }
    }
}
