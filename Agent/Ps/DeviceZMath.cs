using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.Agent.Ps
{
    public sealed class DeviceZOptions
    {
        public string Scope = "scene";
        public string[] ObjectIds = new string[0];
        public string[] IgnoreKeywords = new string[0];
        public string[] SupportGeometryIds = new string[0];
        public double TargetZ = 0;
        public double PositionTolerance = 0.1;
        public double AngleToleranceDeg = 0.05;
        public double PlaneTolerance = 0.5;
        public double MaximumTiltDeg = 3;
        public int MaximumTriangles = 1000000;
        public bool Level = true;
    }

    /// <summary>Pure mesh analysis. No SDK calls, floats, or implicit origin fallback.</summary>
    internal static class DeviceZMath
    {
        internal struct V3
        {
            internal double X, Y, Z;
            internal V3(double x, double y, double z) { X=x; Y=y; Z=z; }
            internal bool Finite { get { return IsFinite(X)&&IsFinite(Y)&&IsFinite(Z); } }
            internal double Length { get { return Math.Sqrt(Dot(this,this)); } }
            public static V3 operator +(V3 a,V3 b) { return new V3(a.X+b.X,a.Y+b.Y,a.Z+b.Z); }
            public static V3 operator -(V3 a,V3 b) { return new V3(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
            public static V3 operator *(V3 a,double s) { return new V3(a.X*s,a.Y*s,a.Z*s); }
        }
        internal sealed class Triangle
        {
            internal readonly V3 A,B,C,Normal,Center;
            internal readonly double Area;
            internal readonly string GeometryId;
            internal Triangle(V3 a,V3 b,V3 c,string id)
            {
                A=a;B=b;C=c;GeometryId=id;
                var cross=Cross(b-a,c-a); double length=cross.Length;
                Area=length/2;Normal=length>0?cross*(1/length):new V3();
                Center=a+((b-a)+(c-a))*(1.0/3);
            }
            internal bool Finite { get { return A.Finite&&B.Finite&&C.Finite&&Normal.Finite&&IsFinite(Area); } }
            internal IEnumerable<V3> Points { get { yield return A;yield return B;yield return C; } }
            internal double LowZ { get { return Math.Min(A.Z,Math.Min(B.Z,C.Z)); } }
        }
        internal sealed class Measurement
        {
            internal double MinZ, MaxZ, TiltDeg, ResidualMax, ResidualRms, SupportHeightRange, SupportArea, NormalSpreadDeg;
            internal string LowestGeometryId;
            internal V3 LowestPoint, Normal, Pivot;
            internal double SpanX,SpanY;
            internal int TriangleCount,SupportTriangles;
            internal string SupportStatus="unknown",Reason="未找到可信支撑面。";
            internal string[] SupportGeometryIds=new string[0];
            internal bool Reliable { get { return SupportStatus=="reliable"; } }
        }
        internal sealed class Correction
        {
            internal V3 Pivot;
            internal double[] Rotation;
            internal double ShiftZ, AngleDeg;
            internal V3 Transform(V3 p) { var q=Pivot+Rotate(Rotation,p-Pivot);q.Z+=ShiftZ;return q; }
        }
        internal static bool IsFinite(double value) { return !double.IsNaN(value)&&!double.IsInfinity(value); }
        internal static double Dot(V3 a,V3 b) { return a.X*b.X+a.Y*b.Y+a.Z*b.Z; }
        internal static V3 Cross(V3 a,V3 b) { return new V3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X); }
        private static double Degrees(double value) { return value*180/Math.PI; }
        private static double Clamp(double value) { return Math.Max(-1,Math.Min(1,value)); }

        internal static Measurement Analyze(List<Triangle> mesh,DeviceZOptions options)
        {
            if(mesh==null||mesh.Count==0)throw new InvalidOperationException("有效三角面为空。");
            var result=new Measurement {MinZ=double.PositiveInfinity,MaxZ=double.NegativeInfinity,TriangleCount=mesh.Count};
            double minX=double.PositiveInfinity,minY=double.PositiveInfinity,maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
            foreach(var t in mesh)
            {
                if(!t.Finite||t.Area<=1e-12)throw new InvalidOperationException("网格包含无效三角面。");
                foreach(var p in t.Points)
                {
                    if(p.Z<result.MinZ){result.MinZ=p.Z;result.LowestPoint=p;result.LowestGeometryId=t.GeometryId;}
                    result.MaxZ=Math.Max(result.MaxZ,p.Z);
                    minX=Math.Min(minX,p.X);maxX=Math.Max(maxX,p.X);minY=Math.Min(minY,p.Y);maxY=Math.Max(maxY,p.Y);
                }
            }
            result.SpanX=maxX-minX;result.SpanY=maxY-minY;
            if(result.SpanX<=1e-6||result.SpanY<=1e-6){result.Reason="几何没有足够的二维支撑范围。";return result;}
            double radians=options.MaximumTiltDeg*Math.PI/180;
            double band=Math.Tan(radians)*Math.Sqrt(result.SpanX*result.SpanX+result.SpanY*result.SpanY)+2*options.PlaneTolerance;
            var selection=new HashSet<string>(options.SupportGeometryIds??new string[0],StringComparer.Ordinal);
            var candidates=mesh.Where(t=>Math.Abs(t.Normal.Z)>=Math.Cos(radians)
                &&(selection.Count==0||selection.Contains(t.GeometryId))
                &&t.Center.Z<=result.MinZ+band).ToList();
            if(candidates.Count==0){result.Reason="底部未找到允许倾角范围内的支撑面；可使用 support_geometry_ids 指定候选几何。";return result;}
            var index=new SurfaceIndex(mesh);
            var support=new List<Triangle>();
            foreach(var t in candidates)
            {
                // Query the complete mesh below each face, not a ray through the resource origin.
                double lower=index.MinimumZ(t.Center.X,t.Center.Y);
                if(IsFinite(lower)&&t.Center.Z-lower<=options.PlaneTolerance) support.Add(t);
            }
            if(support.Count==0){result.Reason="底部候选面不属于几何下包络。";return result;}
            result.SupportTriangles=support.Count;
            result.SupportGeometryIds=support.Select(t=>t.GeometryId).Distinct().ToArray();
            double totalArea=support.Sum(t=>t.Area);
            result.SupportArea=totalArea;
            var reference=support[0].Center;
            V3 mean=new V3(),normal=new V3();
            foreach(var t in support)
            {
                double w=t.Area/totalArea;
                mean=mean+(t.Center-reference)*w;
                normal=normal+t.Normal*(t.Normal.Z<0?-w:w); // Winding is not an outward-normal guarantee.
            }
            result.Pivot=reference+mean;
            double length=normal.Length;
            if(length<1e-9){result.Reason="底部面片法向无法形成共同方向。";return result;}
            normal=normal*(1/length);result.Normal=normal;
            result.TiltDeg=Degrees(Math.Acos(Clamp(normal.Z)));
            double sx0=double.PositiveInfinity,sy0=double.PositiveInfinity,sx1=double.NegativeInfinity,sy1=double.NegativeInfinity;
            double xx=0,yy=0,xy=0,sumError=0,low=double.PositiveInfinity,high=double.NegativeInfinity;
            double maxNormalError=0;
            foreach(var t in support)
            {
                var n=t.Normal.Z<0?t.Normal*(-1):t.Normal;
                maxNormalError=Math.Max(maxNormalError,Degrees(Math.Acos(Clamp(Dot(n,normal)))));
                foreach(var p in t.Points)
                {
                    var d=p-result.Pivot;double w=t.Area/(3*totalArea),distance=Dot(normal,d);
                    xx+=w*d.X*d.X;yy+=w*d.Y*d.Y;xy+=w*d.X*d.Y;
                    sumError+=w*distance*distance;result.ResidualMax=Math.Max(result.ResidualMax,Math.Abs(distance));
                    low=Math.Min(low,distance);high=Math.Max(high,distance);
                    sx0=Math.Min(sx0,p.X);sx1=Math.Max(sx1,p.X);sy0=Math.Min(sy0,p.Y);sy1=Math.Max(sy1,p.Y);
                }
            }
            result.ResidualRms=Math.Sqrt(sumError);result.SupportHeightRange=high-low;result.NormalSpreadDeg=maxNormalError;
            if(sx1-sx0<result.SpanX*0.25||sy1-sy0<result.SpanY*0.25||xx*yy-xy*xy<=Math.Max(1e-12,xx*yy*1e-5))
            {result.SupportStatus="insufficient_coverage";result.Reason="支撑候选集中于局部或近似共线，无法判断整台资源的左右/前后倾斜。";return result;}
            if(result.ResidualMax>options.PlaneTolerance||maxNormalError>options.AngleToleranceDeg)
            {result.SupportStatus="non_coplanar";result.Reason="底部支撑面不共面或法向不一致；整体旋转无法可靠调平全部支脚。";return result;}
            if(result.TiltDeg>options.MaximumTiltDeg+1e-8)
            {result.SupportStatus="excessive_tilt";result.Reason="检测倾角超出接近摆正的允许范围。";return result;}
            foreach(var t in mesh)foreach(var p in t.Points)
                if(Dot(normal,p-result.Pivot)<-options.PlaneTolerance)
                {result.SupportStatus="geometry_below_support";result.Reason="存在低于候选支撑平面的几何，无法自动确定安装基准。";return result;}
            result.SupportStatus="reliable";result.Reason="底部支撑面分布充分，法向一致且共面误差在容差内。";
            return result;
        }

        internal static Correction Plan(List<Triangle> mesh,Measurement measurement,DeviceZOptions options,V3 resourceX)
        {
            if(options.Level&&!measurement.Reliable)throw new InvalidOperationException(measurement.Reason);
            var rotation=new double[]{1,0,0,0,1,0,0,0,1};
            var pivot=measurement.Reliable?measurement.Pivot:measurement.LowestPoint;
            bool level=options.Level&&measurement.TiltDeg>options.AngleToleranceDeg;
            if(level)
            {
                var n=measurement.Normal;var v=Cross(n,new V3(0,0,1));double c=n.Z;
                var basis=new[]{new V3(1,0,0),new V3(0,1,0),new V3(0,0,1)};
                for(int j=0;j<3;j++)
                {
                    var p=basis[j];var q=p+Cross(v,p)+Cross(v,Cross(v,p))*(1/(1+c));
                    rotation[j]=q.X;rotation[3+j]=q.Y;rotation[6+j]=q.Z;
                }
                var correctedX=Rotate(rotation,resourceX);
                if(resourceX.X*resourceX.X+resourceX.Y*resourceX.Y<1e-12||correctedX.X*correctedX.X+correctedX.Y*correctedX.Y<1e-12)
                    throw new InvalidOperationException("资源 X 轴没有可保持的水平朝向。");
                double yaw=Math.Atan2(resourceX.Y,resourceX.X)-Math.Atan2(correctedX.Y,correctedX.X);
                double cy=Math.Cos(yaw),sy=Math.Sin(yaw);
                for(int j=0;j<3;j++){double x=rotation[j],y=rotation[3+j];rotation[j]=cy*x-sy*y;rotation[3+j]=sy*x+cy*y;}
            }
            var plan=new Correction {Rotation=rotation,Pivot=pivot,AngleDeg=level?measurement.TiltDeg:0};
            double minimum=double.PositiveInfinity;
            foreach(var triangle in mesh)foreach(var p in triangle.Points)minimum=Math.Min(minimum,plan.Transform(p).Z);
            if(level&&minimum<pivot.Z-options.PlaneTolerance)
                throw new InvalidOperationException("调平后仍有几何低于支撑平面，支撑基准存在歧义。");
            plan.ShiftZ=options.TargetZ-minimum;
            return plan;
        }
        internal static V3 Rotate(double[] m,V3 p) { return new V3(m[0]*p.X+m[1]*p.Y+m[2]*p.Z,m[3]*p.X+m[4]*p.Y+m[5]*p.Z,m[6]*p.X+m[7]*p.Y+m[8]*p.Z); }

        // 2D BVH accelerates lower-envelope tests without changing SDK visibility or casting native rays.
        private sealed class SurfaceIndex
        {
            private sealed class Node {internal double X0,Y0,X1,Y1,Z;internal Node Left,Right;internal int Start,Count;}
            private readonly Triangle[] triangles;
            private readonly Node root;
            private long queries;
            internal SurfaceIndex(List<Triangle> source){triangles=source.ToArray();root=Build(0,triangles.Length);}
            private Node Build(int start,int count)
            {
                var n=new Node {X0=double.PositiveInfinity,Y0=double.PositiveInfinity,X1=double.NegativeInfinity,Y1=double.NegativeInfinity,Z=double.PositiveInfinity,Start=start,Count=count};
                for(int i=start;i<start+count;i++)foreach(var p in triangles[i].Points)
                {n.X0=Math.Min(n.X0,p.X);n.X1=Math.Max(n.X1,p.X);n.Y0=Math.Min(n.Y0,p.Y);n.Y1=Math.Max(n.Y1,p.Y);n.Z=Math.Min(n.Z,p.Z);}
                if(count<=8)return n;
                bool x=n.X1-n.X0>=n.Y1-n.Y0;
                Array.Sort(triangles,start,count,Comparer<Triangle>.Create((a,b)=>(x?a.Center.X:a.Center.Y).CompareTo(x?b.Center.X:b.Center.Y)));
                int half=count/2;n.Left=Build(start,half);n.Right=Build(start+half,count-half);return n;
            }
            internal double MinimumZ(double x,double y){double z=double.PositiveInfinity;Query(root,x,y,ref z);return z;}
            private void Query(Node n,double x,double y,ref double minimum)
            {
                if(n==null||x<n.X0-1e-8||x>n.X1+1e-8||y<n.Y0-1e-8||y>n.Y1+1e-8||n.Z>minimum+1e-8)return;
                if(n.Left!=null)
                {
                    var a=n.Left.Z<=n.Right.Z?n.Left:n.Right;var b=ReferenceEquals(a,n.Left)?n.Right:n.Left;
                    Query(a,x,y,ref minimum);Query(b,x,y,ref minimum);return;
                }
                for(int i=n.Start;i<n.Start+n.Count;i++)
                {
                    if(++queries>50000000)throw new InvalidOperationException("网格下包络计算超出复杂度上限，未输出不完整测量。");
                    var t=triangles[i];double ax=t.A.X-x,ay=t.A.Y-y,bx=t.B.X-x,by=t.B.Y-y,cx=t.C.X-x,cy=t.C.Y-y;
                    double denominator=(t.B.X-t.A.X)*(t.C.Y-t.A.Y)-(t.B.Y-t.A.Y)*(t.C.X-t.A.X);
                    if(Math.Abs(denominator)<1e-18)continue;
                    double u=(bx*cy-by*cx)/denominator,w=(cx*ay-cy*ax)/denominator,q=1-u-w;
                    if(u>=-1e-9&&w>=-1e-9&&q>=-1e-9)minimum=Math.Min(minimum,u*t.A.Z+w*t.B.Z+q*t.C.Z);
                }
            }
        }
    }
}
