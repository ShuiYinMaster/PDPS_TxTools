using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.ExportByColor
{
    // Each accepted wall and its two cap rims are replaced atomically. Ambiguous
    // cap triangulations stay unchanged; input lists and coordinate arrays are never edited.
    public static partial class RoundHoleCgrMesh
    {
        public sealed class Cylinder
        {
            public int Surface,SourceSurface;
            public double[] Center,Axis,RimA,RimB;
            public double Radius,Depth;
        }
        public sealed class Result
        {
            public List<float[]> Vertices;public List<CgrWriter.Face> Faces;
            public readonly Dictionary<int,Cylinder> Cylinders=new Dictionary<int,Cylinder>();
            public int RemovedWallTriangles,ReplacedCapTriangles,AddedWallTriangles,AddedCapTriangles;
            public readonly HashSet<int> ChangedSourceTriangles=new HashSet<int>();
        }
        sealed class Plan
        {
            internal RoundHoleReconstruction.Hole Hole;internal double[] U,V;
            internal bool Rejected;internal readonly List<RimEdge> Edges=new List<RimEdge>();
            internal List<double> Angles;internal int[][] Rings;internal Cylinder Cylinder;
        }
        sealed class RimEdge
        {
            internal Plan Plan;internal int Ring,A,B,Cap=-1;internal int[] Arc;
        }
        static double Dot(double[] a,double[] b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
        static double[] Sub(double[] a,double[] b){return new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};}
        static double[] Cross(double[] a,double[] b){return new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}
        static double[] Unit(double[] a){double n=Math.Sqrt(Dot(a,a));return a.Select(x=>x/n).ToArray();}
        static double[] D(float[] p){return new[]{(double)p[0],p[1],p[2]};}
        static long Edge(int a,int b){return ((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);}
        static Tuple<int,float,float,float> Key(int s,double[] p){return Tuple.Create(s,(float)p[0],(float)p[1],(float)p[2]);}
        static double Angle(Plan p,double[] point){var d=Sub(point,p.Hole.Center);double a=Math.Atan2(Dot(d,p.V),Dot(d,p.U));return a<0?a+2*Math.PI:a;}
        static double Turn(double a,double b){double d=b-a;while(d<=-Math.PI)d+=2*Math.PI;while(d>Math.PI)d-=2*Math.PI;return d;}
        static int Closest(List<double> angles,double angle){int found=-1;double best=double.PositiveInfinity;for(int i=0;i<angles.Count;i++){double d=Math.Abs(Turn(angle,angles[i]));if(d<best){best=d;found=i;}}if(found<0)throw new InvalidOperationException("Rim angle was lost");return found;}
        static int[] Arc(Plan p,int ring,double a,double b){int first=Closest(p.Angles,a),last=Closest(p.Angles,b),step=Turn(a,b)>0?1:-1;var arc=new List<int>();for(int i=first;;i=(i+step+p.Angles.Count)%p.Angles.Count){arc.Add(p.Rings[ring][i]);if(i==last)break;if(arc.Count>p.Angles.Count)throw new InvalidOperationException("Invalid rim arc");}return arc.ToArray();}
        static CgrWriter.Face Triangle(CgrWriter.Face source,int a,int b,int c){source.Idx=new[]{a,b,c};return source;}
        static double[] Normal(List<float[]> vertices,int[] ids){return Cross(Sub(D(vertices[ids[1]]),D(vertices[ids[0]])),Sub(D(vertices[ids[2]]),D(vertices[ids[0]])));}
        static List<int[]> Triangulate(List<int> polygon,List<float[]> vertices,double[] sourceNormal)
        {
            var normal=Unit(sourceNormal);var left=new List<int>(polygon);var result=new List<int[]>();
            while(left.Count>3)
            {
                bool found=false;
                for(int i=0;i<left.Count;i++)
                {
                    int a=left[(i+left.Count-1)%left.Count],b=left[i],c=left[(i+1)%left.Count];
                    if(Dot(normal,Normal(vertices,new[]{a,b,c}))<=1e-12)continue;
                    bool blocked=false;
                    foreach(int point in left)
                    {
                        if(point==a||point==b||point==c)continue;var p=D(vertices[point]);
                        double ab=Dot(normal,Cross(Sub(D(vertices[b]),D(vertices[a])),Sub(p,D(vertices[a]))));
                        double bc=Dot(normal,Cross(Sub(D(vertices[c]),D(vertices[b])),Sub(p,D(vertices[b]))));
                        double ca=Dot(normal,Cross(Sub(D(vertices[a]),D(vertices[c])),Sub(p,D(vertices[c]))));
                        if(ab>=-1e-10&&bc>=-1e-10&&ca>=-1e-10){blocked=true;break;}
                    }
                    if(blocked)continue;result.Add(new[]{a,b,c});left.RemoveAt(i);found=true;break;
                }
                if(!found)return null;
            }
            if(left.Count!=3||Dot(normal,Normal(vertices,left.ToArray()))<=1e-12)return null;result.Add(left.ToArray());return result;
        }

        public static Result Replace(List<float[]> source,List<CgrWriter.Face> faces,List<RoundHoleReconstruction.Hole> holes,Action<string> log=null)
        {
            var result=new Result{Vertices=new List<float[]>(source),Faces=new List<CgrWriter.Face>()};
            var ids=new Dictionary<Tuple<int,float,float,float>,int>();var points=new List<double[]>();
            Func<int,double[],int> id=(surface,point)=>{var key=Key(surface,point);int value;if(!ids.TryGetValue(key,out value)){value=points.Count;ids.Add(key,value);points.Add(point);}return value;};
            var plans=new List<Plan>();var edges=new Dictionary<long,RimEdge>();var walls=new Dictionary<int,Plan>();
            foreach(var h in holes)
            {
                if(!h.PairedRims||h.WallTriangles==null||h.RimPointsA==null||h.RimPointsB==null||h.Depth<=.02)continue;
                var color=faces[h.WallTriangles[0]];
                if((color.Opacity??255)==0||h.WallTriangles.Any(f=>faces[f].R!=color.R||faces[f].G!=color.G||faces[f].B!=color.B||faces[f].Opacity!=color.Opacity))continue;
                double lo=Math.Min(Dot(Sub(h.RimA,h.Center),h.Axis),Dot(Sub(h.RimB,h.Center),h.Axis)),hi=Math.Max(Dot(Sub(h.RimA,h.Center),h.Axis),Dot(Sub(h.RimB,h.Center),h.Axis));
                if(h.WallTriangles.Any(f=>faces[f].Idx.Any(v=>{double axial=Dot(Sub(D(source[v]),h.Center),h.Axis);return axial<lo-.01||axial>hi+.01;})))continue;
                var p=new Plan{Hole=h};p.U=Unit(Cross(h.Axis,Math.Abs(h.Axis[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));p.V=Cross(h.Axis,p.U);plans.Add(p);
                foreach(int f in h.WallTriangles){Plan other;if(walls.TryGetValue(f,out other)){other.Rejected=true;p.Rejected=true;}else walls.Add(f,p);}
                int ring=0;foreach(var rim in new[]{h.RimPointsA,h.RimPointsB}){
                    for(int i=0;i<rim.Length;i++){int a=id(h.Surface,rim[i]),b=id(h.Surface,rim[(i+1)%rim.Length]);var e=new RimEdge{Plan=p,Ring=ring,A=a,B=b};long key=Edge(a,b);RimEdge other;if(edges.TryGetValue(key,out other)){other.Plan.Rejected=true;p.Rejected=true;}else edges.Add(key,e);p.Edges.Add(e);}ring++;
                }
            }
            var capEdges=new Dictionary<int,List<RimEdge>>();
            for(int fi=0;fi<faces.Count;fi++)
            {
                if(walls.ContainsKey(fi))continue;var f=faces[fi];var local=new int[3];for(int k=0;k<3;k++){int value;local[k]=ids.TryGetValue(Key(f.Surface,D(source[f.Idx[k]])),out value)?value:-1;}
                var hits=new List<RimEdge>();for(int k=0;k<3;k++){RimEdge e;if(local[k]>=0&&local[(k+1)%3]>=0&&edges.TryGetValue(Edge(local[k],local[(k+1)%3]),out e))hits.Add(e);}
                if(hits.Count==0)continue;
                var normal=Unit(Normal(source,f.Idx));
                foreach(var hit in hits){if(hit.Cap>=0){hit.Plan.Rejected=true;continue;}hit.Cap=fi;if(Math.Abs(Dot(normal,hit.Plan.Hole.Axis))<.99999)hit.Plan.Rejected=true;}
                capEdges.Add(fi,hits);
            }
            foreach(var p in plans)if(p.Edges.Any(e=>e.Cap<0))p.Rejected=true;
            if(log!=null)log("[CGR hole coverage] pairedWalls="+plans.Count+" capTopologyAccepted="+plans.Count(p=>!p.Rejected)+" missingCapJoins="+plans.Count(p=>p.Edges.Any(e=>e.Cap<0)));
            int nextSurface=faces.Max(f=>f.Surface);
            foreach(var p in plans.Where(p=>!p.Rejected))
            {
                // Two independently fitted rims can differ by a few microns in angular
                // phase. Merge those near-identical samples before float32 tessellation;
                // otherwise they create zero-width slivers at the shared cap boundary.
                double angularTolerance=Math.Max(1e-6,.001/p.Hole.Radius);
                var all=p.Hole.RimPointsA.Concat(p.Hole.RimPointsB).Select(v=>Angle(p,v)).OrderBy(a=>a).ToList();var unique=new List<double>();foreach(double a in all)if(unique.Count==0||a-unique[unique.Count-1]>angularTolerance)unique.Add(a);if(unique.Count>1&&2*Math.PI+unique[0]-unique[unique.Count-1]<=angularTolerance)unique.RemoveAt(unique.Count-1);
                double step=Math.Min(2*Math.PI/96,2*Math.Acos(Math.Max(-1,1-.002/p.Hole.Radius)));
                if(step<2*Math.PI/2048){p.Rejected=true;continue;}
                p.Angles=new List<double>();for(int i=0;i<unique.Count;i++){double a=unique[i],b=i+1<unique.Count?unique[i+1]:unique[0]+2*Math.PI;int count=(int)Math.Ceiling((b-a)/step);for(int k=0;k<count;k++)p.Angles.Add((a+(b-a)*k/count)%(2*Math.PI));}p.Angles.Sort();
                p.Cylinder=new Cylinder{SourceSurface=p.Hole.Surface,Surface=checked(++nextSurface),Center=p.Hole.Center,Axis=p.Hole.Axis,Radius=p.Hole.Radius,Depth=p.Hole.Depth};
                var centerA=p.Hole.Center.Select((c,k)=>c+Dot(Sub(p.Hole.RimA,p.Hole.Center),p.Hole.Axis)*p.Hole.Axis[k]).ToArray();var centerB=p.Hole.Center.Select((c,k)=>c+Dot(Sub(p.Hole.RimB,p.Hole.Center),p.Hole.Axis)*p.Hole.Axis[k]).ToArray();p.Cylinder.RimA=centerA;p.Cylinder.RimB=centerB;
                p.Rings=new int[2][];int ring=0;foreach(var center in new[]{centerA,centerB}){p.Rings[ring]=new int[p.Angles.Count];for(int i=0;i<p.Angles.Count;i++){double a=p.Angles[i];var point=new float[3];for(int k=0;k<3;k++)point[k]=(float)(center[k]+p.Hole.Radius*(Math.Cos(a)*p.U[k]+Math.Sin(a)*p.V[k]));p.Rings[ring][i]=result.Vertices.Count;result.Vertices.Add(point);}ring++;}
                foreach(var e in p.Edges)e.Arc=Arc(p,e.Ring,Angle(p,points[e.A]),Angle(p,points[e.B]));
            }
            var snaps=new Dictionary<Tuple<int,float,float,float>,int>();
            var patches=BuildCapPatches(source,faces,walls,capEdges,id,points);
            var capTriangles=new Dictionary<int,List<int[]>>();bool retry;
            do
            {
                retry=false;snaps.Clear();capTriangles.Clear();
                foreach(var p in plans.Where(p=>!p.Rejected)){int ring=0;foreach(var rim in new[]{p.Hole.RimPointsA,p.Hole.RimPointsB}){foreach(var point in rim)snaps[Key(p.Hole.Surface,point)]=p.Rings[ring][Closest(p.Angles,Angle(p,point))];ring++;}}
                foreach(var patch in patches)
                {
                    var active=patch.Plans.Where(p=>!p.Rejected).ToList();if(active.Count==0)continue;
                    var triangulated=TriangulateCapPatch(patch,source,result.Vertices,faces,edges,points,snaps);
                    if(triangulated==null){foreach(var p in active)p.Rejected=true;retry=true;continue;}
                    foreach(int fi in patch.Faces)capTriangles.Add(fi,new List<int[]>());
                    capTriangles[patch.Faces[0]]=triangulated;
                }
            }while(retry);
            foreach(var p in plans.Where(p=>!p.Rejected))result.Cylinders.Add(p.Cylinder.Surface,p.Cylinder);
            for(int fi=0;fi<faces.Count;fi++)
            {
                Plan wall;if(walls.TryGetValue(fi,out wall)&&!wall.Rejected){result.RemovedWallTriangles++;result.ChangedSourceTriangles.Add(fi);continue;}
                var f=faces[fi];var mapped=(int[])f.Idx.Clone();bool changed=false;for(int k=0;k<3;k++){int replacement;if(snaps.TryGetValue(Key(f.Surface,D(source[f.Idx[k]])),out replacement)){mapped[k]=replacement;changed=true;}}
                List<int[]> cap;if(capTriangles.TryGetValue(fi,out cap)){foreach(var triangle in cap){result.Faces.Add(Triangle(f,triangle[0],triangle[1],triangle[2]));result.AddedCapTriangles++;}result.ReplacedCapTriangles++;result.ChangedSourceTriangles.Add(fi);continue;}
                if(changed){f.Idx=mapped;result.ChangedSourceTriangles.Add(fi);}result.Faces.Add(f);
            }
            foreach(var p in plans.Where(p=>!p.Rejected))
            {
                var color=faces[p.Hole.WallTriangles[0]];color.Surface=p.Cylinder.Surface;
                for(int i=0;i<p.Angles.Count;i++){int j=(i+1)%p.Angles.Count;foreach(var t in new[]{new[]{p.Rings[0][i],p.Rings[0][j],p.Rings[1][j]},new[]{p.Rings[0][i],p.Rings[1][j],p.Rings[1][i]}}){var middle=D(result.Vertices[t[0]]);var radial=Sub(middle,p.Hole.Center);double axial=Dot(radial,p.Hole.Axis);for(int k=0;k<3;k++)radial[k]-=axial*p.Hole.Axis[k];var n=Normal(result.Vertices,t);if(Dot(n,radial)>0){int swap=t[1];t[1]=t[2];t[2]=swap;}n=Unit(Normal(result.Vertices,t));color.Nx=(float)n[0];color.Ny=(float)n[1];color.Nz=(float)n[2];result.Faces.Add(Triangle(color,t[0],t[1],t[2]));result.AddedWallTriangles++;}}
            }
            if(log!=null)log("[CGR cylindrical replacement] accepted="+result.Cylinders.Count+" eligible="+plans.Count+" removedWalls="+result.RemovedWallTriangles+" replacedCaps="+result.ReplacedCapTriangles+" addedWalls="+result.AddedWallTriangles+" addedCaps="+result.AddedCapTriangles+" displaySag<=0.002mm; ambiguous caps retained");
            return result;
        }
    }
}
