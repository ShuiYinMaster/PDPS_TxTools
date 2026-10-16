using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.ExportByColor
{
    // Each accepted wall and its two cap rims are replaced atomically. Ambiguous
    // cap triangulations stay unchanged; input lists and coordinate arrays are never edited.
    public static partial class RoundHoleCgrMesh
    {
        public sealed class DisplayOptions
        {
            public double ChordToleranceMm=.02;
            public int MinimumCircleSegments=24;
            public static DisplayOptions Fine(){return new DisplayOptions{ChordToleranceMm=.002,MinimumCircleSegments=96};}
        }
        public sealed class Cylinder
        {
            public int Surface,SourceSurface;
            public double[] Center,Axis,RimA,RimB;
            public double Radius,Depth;
            public bool ExternalCylinder;
            public bool TrimmedRimA,TrimmedRimB;
            public bool PreservedWallTessellation;
            public bool RefinedSourceWall;
        }
        public sealed class Result
        {
            public List<float[]> Vertices;public List<CgrWriter.Face> Faces;
            public readonly Dictionary<int,Cylinder> Cylinders=new Dictionary<int,Cylinder>();
            public int RemovedWallTriangles,ReplacedCapTriangles,AddedWallTriangles,AddedCapTriangles;
            public int RefinementAddedTriangles;
            public readonly HashSet<int> ChangedSourceTriangles=new HashSet<int>();
            public readonly List<string> Diagnostics=new List<string>();
            public readonly Dictionary<int[],int> SourceFaceIndices=new Dictionary<int[],int>();
        }
        sealed class Plan
        {
            internal RoundHoleReconstruction.Hole Hole;internal double[] U,V;
            internal bool Rejected;internal readonly List<RimEdge> Edges=new List<RimEdge>();
            internal string Failure;
            internal List<double> Angles;internal int[][] Rings;internal Cylinder Cylinder;
        }
        sealed class RimEdge
        {
            internal Plan Plan;internal int Ring,A,B,Cap=-1;internal int[] Arc;
            internal bool Transition;
            internal double[] CapNormal,CapOrigin;
            internal double AngleA,AngleB;
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
        // Keep the source trim height along each projected chord. The radial
        // coordinate is made circular; the axial coordinate is never flattened.
        static double RimHeight(Plan p,int ring,double angle)
        {
            var h=p.Hole;var rim=ring==0?h.RimPointsA:h.RimPointsB;
            if(!(ring==0?h.TrimmedRimA:h.TrimmedRimB))return Dot(Sub(ring==0?h.RimA:h.RimB,h.Center),h.Axis);
            foreach(var point in rim)if(Math.Abs(Turn(Angle(p,point),angle))<1e-8)return Dot(Sub(point,h.Center),h.Axis);
            for(int i=0;i<rim.Length;i++)
            {
                var a=rim[i];var b=rim[(i+1)%rim.Length];double start=Angle(p,a),turn=Turn(start,Angle(p,b)),part=Turn(start,angle);
                if(part*turn<0||Math.Abs(part)>Math.Abs(turn)+1e-8)continue;
                // Intersect the circular wall with the matched source cap plane.
                // Linear chord height alone can lift a new arc off a curved cap.
                double aa=Angle(p,a),ab=Angle(p,b);
                var matched=p.Edges.FirstOrDefault(e=>e.Ring==ring&&e.CapNormal!=null&&((Math.Abs(Turn(aa,e.AngleA))<1e-6&&Math.Abs(Turn(ab,e.AngleB))<1e-6)||(Math.Abs(Turn(aa,e.AngleB))<1e-6&&Math.Abs(Turn(ab,e.AngleA))<1e-6)));
                if(matched!=null)
                {
                    var radial=h.Center.Select((c,k)=>c+h.Radius*(Math.Cos(angle)*p.U[k]+Math.Sin(angle)*p.V[k])).ToArray();
                    return -Dot(Sub(radial,matched.CapOrigin),matched.CapNormal)/Dot(h.Axis,matched.CapNormal);
                }
                var da=Sub(a,h.Center);var db=Sub(b,h.Center);
                double ca=Dot(da,p.U)*Math.Sin(angle)-Dot(da,p.V)*Math.Cos(angle);
                double cb=Dot(db,p.U)*Math.Sin(angle)-Dot(db,p.V)*Math.Cos(angle);
                double t=ca/(ca-cb);t=Math.Max(0,Math.Min(1,t));
                return (1-t)*Dot(da,h.Axis)+t*Dot(db,h.Axis);
            }
            throw new InvalidOperationException("Trim angle outside the complete source rim");
        }
        static CgrWriter.Face Triangle(CgrWriter.Face source,int a,int b,int c){source.Idx=new[]{a,b,c};return source;}
        // Some JT exporters put a cylinder shell and its planar cap in separate
        // Shapes. Join only an entire rim with one exact edge match per segment;
        // proximity alone cannot prove that two resources share a cap.
        static void LinkSplitShapeCaps(List<float[]> source,List<CgrWriter.Face> faces,List<Plan> plans,
            Dictionary<int,Plan> walls,Dictionary<long,RimEdge> edges,Func<int,double[],int> id,List<double[]> points,Action<string> log)
        {
            var coordinates=new Dictionary<Tuple<float,float,float>,int>();
            Func<double[],Tuple<float,float,float>> point=p=>Tuple.Create((float)p[0],(float)p[1],(float)p[2]);
            Func<double[],int> coordinate=p=>{int v;var key=point(p);if(!coordinates.TryGetValue(key,out v)){v=coordinates.Count;coordinates.Add(key,v);}return v;};
            var candidates=new Dictionary<long,List<RimEdge>>();
            foreach(var p in plans.Where(p=>!p.Rejected))foreach(var e in p.Edges.Where(e=>e.Ring==0?p.Hole.OpenRimA:p.Hole.OpenRimB))
            {long key=Edge(coordinate(points[e.A]),coordinate(points[e.B]));List<RimEdge> list;if(!candidates.TryGetValue(key,out list))candidates.Add(key,list=new List<RimEdge>());list.Add(e);}
            if(candidates.Count==0)return;
            var matches=new Dictionary<RimEdge,List<int>>();
            for(int fi=0;fi<faces.Count;fi++)
            {
                if(walls.ContainsKey(fi))continue;var f=faces[fi];var local=new int[3];for(int k=0;k<3;k++){int v;local[k]=coordinates.TryGetValue(point(D(source[f.Idx[k]])),out v)?v:-1;}
                for(int k=0;k<3;k++)
                {
                    List<RimEdge> list;if(local[k]<0||local[(k+1)%3]<0||!candidates.TryGetValue(Edge(local[k],local[(k+1)%3]),out list))continue;
                    foreach(var e in list)
                    {
                        if(f.Surface==e.Plan.Hole.Surface)continue;var plane=e.Ring==0?e.Plan.Hole.RimA:e.Plan.Hole.RimB;var n=Normal(source,f.Idx);double length=Math.Sqrt(Dot(n,n));
                        bool trimmed=e.Ring==0?e.Plan.Hole.TrimmedRimA:e.Plan.Hole.TrimmedRimB;
                        if(length==0||Math.Abs(Dot(n,e.Plan.Hole.Axis))/length<(trimmed?.15:.5)||(!trimmed&&f.Idx.Any(v=>Math.Abs(Dot(Sub(D(source[v]),plane),e.Plan.Hole.Axis))>.01)))continue;
                        List<int> hits;if(!matches.TryGetValue(e,out hits))matches.Add(e,hits=new List<int>());hits.Add(fi);
                    }
                }
            }
            int linked=0;
            foreach(var p in plans.Where(p=>!p.Rejected))for(int ring=0;ring<2;ring++)
            {
                if(!(ring==0?p.Hole.OpenRimA:p.Hole.OpenRimB))continue;var rim=p.Edges.Where(e=>e.Ring==ring).ToList();
                if(rim.Any(e=>!matches.ContainsKey(e)||matches[e].Count!=1))continue;
                int surface=faces[matches[rim[0]][0]].Surface;if(rim.Any(e=>faces[matches[e][0]].Surface!=surface))continue;
                var aliases=rim.Select(e=>new RimEdge{Plan=p,Ring=ring,A=id(surface,points[e.A]),B=id(surface,points[e.B])}).ToList();
                if(aliases.Any(e=>edges.ContainsKey(Edge(e.A,e.B))))continue;
                foreach(var e in aliases){edges.Add(Edge(e.A,e.B),e);p.Edges.Add(e);}linked++;
            }
            if(log!=null)log("[CGR split Shape rims] linked="+linked+"; complete exact rim matches only; cap material retained");
        }
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
        {return Replace(source,faces,holes,log,new DisplayOptions());}
        public static Result Replace(List<float[]> source,List<CgrWriter.Face> faces,List<RoundHoleReconstruction.Hole> holes,Action<string> log,DisplayOptions display)
        {
            if(display==null||double.IsNaN(display.ChordToleranceMm)||double.IsInfinity(display.ChordToleranceMm)||display.ChordToleranceMm<=0||display.ChordToleranceMm>.2||display.MinimumCircleSegments<8||display.MinimumCircleSegments>2048)throw new ArgumentOutOfRangeException("display");
            var density=new DisplayOptions{ChordToleranceMm=display.ChordToleranceMm,MinimumCircleSegments=display.MinimumCircleSegments};
            if(log!=null)log("[CGR display density] regular sag<="+density.ChordToleranceMm+"mm minimumSegments="+density.MinimumCircleSegments+"; original rim samples retained; clipped/intersecting walls keep 0.002mm guards; analytical parameters unchanged");
            // A cylindrical wall can have additional boundary loops where a
            // transverse bore cuts it. Bridging only its two axial rims would
            // fill those openings. Preserve that wall's source tessellation;
            // its proven cylinder still receives native measurement attributes.
            var punctured=holes.Where(h=>h.PairedRims&&h.WallTriangles!=null&&h.RimPointsA!=null&&h.RimPointsB!=null&&h.Depth>.02&&HasAdditionalWallBoundaries(source,faces,h)).ToList();
            var excluded=new HashSet<RoundHoleReconstruction.Hole>(punctured);
            var result=ReplaceCore(source,faces,holes.Where(h=>!excluded.Contains(h)).ToList(),log,density);
            foreach(var h in holes.Where(h=>h.PairedRims&&!h.ExternalCylinder&&(h.TrimmedRimA||h.TrimmedRimB)))
            {
                if(result.Cylinders.Values.Any(c=>c.SourceSurface==h.Surface&&Math.Abs(c.Radius-h.Radius)<.01&&Dot(Sub(c.Center,h.Center),Sub(c.Center,h.Center))<.0001))continue;
                string failure;
                if(!TryClipTrimmedHole(source,faces,result,h,out failure)){result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=trim-clip-"+failure);if(log!=null)log("[CGR trim clip guard] shape="+h.Surface+" reason="+failure);}
                else if(log!=null)log("[CGR trim clip accepted] shape="+h.Surface+" radius="+h.Radius+" center="+string.Join(",",h.Center)+"; source cap facets and seam topology retained");
            }
            var origins=new Dictionary<int[],int>();if(punctured.Count>0)for(int i=0;i<faces.Count;i++)if(!origins.ContainsKey(faces[i].Idx))origins.Add(faces[i].Idx,i);int preserved=0;
            foreach(var h in punctured)
            {
                var material=faces[h.WallTriangles[0]];
                if((material.Opacity??255)==0||h.WallTriangles.Any(fi=>faces[fi].R!=material.R||faces[fi].G!=material.G||faces[fi].B!=material.B||(faces[fi].Opacity??255)!=(material.Opacity??255)))
                {result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=punctured-wall-material; source preserved");continue;}
                var wallIds=new HashSet<int>(h.WallTriangles);var current=new List<int>();
                for(int i=0;i<result.Faces.Count;i++)
                {
                    var f=result.Faces[i];if(result.Cylinders.ContainsKey(f.Surface))continue;int original;
                    if((result.SourceFaceIndices.TryGetValue(f.Idx,out original)||origins.TryGetValue(f.Idx,out original))&&wallIds.Contains(original))current.Add(i);
                }
                if(current.Count==0){result.Diagnostics.Add("shape="+h.Surface+" reason=punctured-wall-missing; source preserved");continue;}
                int surface=checked(result.Faces.Max(f=>f.Surface)+1);
                foreach(int i in current){var f=result.Faces[i];int original;if(!result.SourceFaceIndices.ContainsKey(f.Idx)&&origins.TryGetValue(f.Idx,out original))result.SourceFaceIndices[f.Idx]=original;f.Surface=surface;result.Faces[i]=f;}
                result.Cylinders.Add(surface,new Cylinder{Surface=surface,SourceSurface=h.Surface,Center=h.Center,Axis=h.Axis,RimA=h.RimA,RimB=h.RimB,Radius=h.Radius,Depth=h.Depth,ExternalCylinder=h.ExternalCylinder,TrimmedRimA=h.TrimmedRimA,TrimmedRimB=h.TrimmedRimB,PreservedWallTessellation=true});
                preserved++;result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" punctured-wall=preserved native cylinder attributes; no filled side openings");
            }
            if(log!=null)log("[CGR punctured cylinders] preserved="+preserved+"; source wall tessellation and transverse openings retained");
            RefineRegisteredSourceWalls(result,source,faces,log);
            RefineRemainingSourceWalls(result,source,faces,holes,log);
            if(log!=null)log("[CGR final cylinders] accepted="+result.Cylinders.Count+" inner="+result.Cylinders.Values.Count(c=>!c.ExternalCylinder)+" outer="+result.Cylinders.Values.Count(c=>c.ExternalCylinder));
            if(log!=null)log("[CGR final source trim] cylinders="+result.Cylinders.Values.Count(c=>c.TrimmedRimA||c.TrimmedRimB));
            return result;
        }
        static bool HasAdditionalWallBoundaries(List<float[]> vertices,List<CgrWriter.Face> faces,RoundHoleReconstruction.Hole h)
        {
            var ids=new Dictionary<Tuple<float,float,float>,int>();Func<double[],int> id=p=>{var key=Tuple.Create((float)p[0],(float)p[1],(float)p[2]);int value;if(!ids.TryGetValue(key,out value)){value=ids.Count;ids.Add(key,value);}return value;};
            var counts=new Dictionary<long,int>();foreach(int fi in h.WallTriangles)for(int k=0;k<3;k++){long edge=Edge(id(D(vertices[faces[fi].Idx[k]])),id(D(vertices[faces[fi].Idx[(k+1)%3]])));int count;counts.TryGetValue(edge,out count);counts[edge]=count+1;}
            var rims=new HashSet<long>();foreach(var ring in new[]{h.RimPointsA,h.RimPointsB})for(int i=0;i<ring.Length;i++)rims.Add(Edge(id(ring[i]),id(ring[(i+1)%ring.Length])));
            return counts.Any(e=>e.Value!=2&&!rims.Contains(e.Key));
        }
        static Result ReplaceCore(List<float[]> source,List<CgrWriter.Face> faces,List<RoundHoleReconstruction.Hole> holes,Action<string> log,DisplayOptions density)
        {
            var result=new Result{Vertices=new List<float[]>(source),Faces=new List<CgrWriter.Face>()};
            var ids=new Dictionary<Tuple<int,float,float,float>,int>();var points=new List<double[]>();
            Func<int,double[],int> id=(surface,point)=>{var key=Key(surface,point);int value;if(!ids.TryGetValue(key,out value)){value=points.Count;ids.Add(key,value);points.Add(point);}return value;};
            var plans=new List<Plan>();var edges=new Dictionary<long,RimEdge>();var walls=new Dictionary<int,Plan>();
            foreach(var h in holes)
            {
                if(!h.PairedRims||h.WallTriangles==null||h.RimPointsA==null||h.RimPointsB==null||h.Depth<=.02)continue;
                var color=faces[h.WallTriangles[0]];
                if((color.Opacity??255)==0){result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=transparent-wall");continue;}
                if(h.WallTriangles.Any(f=>faces[f].R!=color.R||faces[f].G!=color.G||faces[f].B!=color.B||(faces[f].Opacity??255)!=(color.Opacity??255))){result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=mixed-wall-material");continue;}
                var p=new Plan{Hole=h};p.U=Unit(Cross(h.Axis,Math.Abs(h.Axis[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));p.V=Cross(h.Axis,p.U);
                if(h.RimPointsA.Concat(h.RimPointsB).Any(v=>{double a=Angle(p,v);return (RimHeight(p,1,a)-RimHeight(p,0,a))*Dot(Sub(h.RimB,h.RimA),h.Axis)<=.02*h.Depth;}))
                {result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=crossing-trim-rims");continue;}
                if(h.WallTriangles.Any(f=>faces[f].Idx.Any(v=>{var point=D(source[v]);double a=Angle(p,point),first=RimHeight(p,0,a),second=RimHeight(p,1,a),axial=Dot(Sub(point,h.Center),h.Axis);return axial<Math.Min(first,second)-.01||axial>Math.Max(first,second)+.01;}))){result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" reason=wall-outside-rims");continue;}
                plans.Add(p);
                foreach(int f in h.WallTriangles){Plan other;if(walls.TryGetValue(f,out other)){other.Rejected=true;p.Rejected=true;}else walls.Add(f,p);}
                int ring=0;foreach(var rim in new[]{h.RimPointsA,h.RimPointsB}){
                    for(int i=0;i<rim.Length;i++){int a=id(h.Surface,rim[i]),b=id(h.Surface,rim[(i+1)%rim.Length]);var e=new RimEdge{Plan=p,Ring=ring,A=a,B=b};long key=Edge(a,b);RimEdge other;if(edges.TryGetValue(key,out other)){other.Plan.Rejected=true;p.Rejected=true;}else edges.Add(key,e);p.Edges.Add(e);}ring++;
                }
            }
            LinkSplitShapeCaps(source,faces,plans,walls,edges,id,points,log);
            var capEdges=new Dictionary<int,List<RimEdge>>();var joinEdges=new Dictionary<int,List<RimEdge>>();
            for(int fi=0;fi<faces.Count;fi++)
            {
                if(walls.ContainsKey(fi))continue;var f=faces[fi];var local=new int[3];for(int k=0;k<3;k++){int value;local[k]=ids.TryGetValue(Key(f.Surface,D(source[f.Idx[k]])),out value)?value:-1;}
                var hits=new List<RimEdge>();for(int k=0;k<3;k++){RimEdge e;if(local[k]>=0&&local[(k+1)%3]>=0&&edges.TryGetValue(Edge(local[k],local[(k+1)%3]),out e))hits.Add(e);}
                if(hits.Count==0)continue;
                var normal=Unit(Normal(source,f.Idx));
                // Float32 sliver triangles can have unstable geometric normals.
                // Test their points against the fitted rim plane as well as orientation.
                foreach(var hit in hits){if(hit.Cap>=0){hit.Plan.Rejected=true;continue;}hit.Cap=fi;var plane=hit.Ring==0?hit.Plan.Hole.RimA:hit.Plan.Hole.RimB;double distance=f.Idx.Max(v=>Math.Abs(Dot(Sub(D(source[v]),plane),hit.Plan.Hole.Axis)));double alignment=Math.Abs(Dot(normal,hit.Plan.Hole.Axis));bool trimmed=hit.Ring==0?hit.Plan.Hole.TrimmedRimA:hit.Plan.Hole.TrimmedRimB;hit.Transition=!trimmed&&alignment>.15&&alignment<.9999&&distance>.01;if(trimmed?alignment<=.15:!hit.Transition&&(alignment<.5||distance>.01)){hit.Plan.Rejected=true;hit.Plan.Failure="cap-not-planar";result.Diagnostics.Add("shape="+f.Surface+" cap-face="+fi+" radius="+hit.Plan.Hole.Radius+" plane-distance="+distance+" normal-dot="+Dot(normal,hit.Plan.Hole.Axis));}}
                foreach(var hit in hits){hit.CapNormal=normal;hit.CapOrigin=D(source[f.Idx[0]]);hit.AngleA=Angle(hit.Plan,points[hit.A]);hit.AngleB=Angle(hit.Plan,points[hit.B]);}
                if(hits.Any(e=>e.Transition))joinEdges.Add(fi,hits);else capEdges.Add(fi,hits);
            }
            foreach(var p in plans)if(p.Edges.Any(e=>e.Cap<0&&!(e.Ring==0?p.Hole.OpenRimA:p.Hole.OpenRimB)))p.Rejected=true;
            if(log!=null)log("[CGR hole coverage] pairedWalls="+plans.Count+" capTopologyAccepted="+plans.Count(p=>!p.Rejected)+" missingCapJoins="+plans.Count(p=>p.Edges.Any(e=>e.Cap<0&&!(e.Ring==0?p.Hole.OpenRimA:p.Hole.OpenRimB)))+" sourceOpenEnds="+plans.Sum(p=>(p.Hole.OpenRimA?1:0)+(p.Hole.OpenRimB?1:0)));
            int nextSurface=faces.Max(f=>f.Surface);
            foreach(var p in plans.Where(p=>!p.Rejected))
            {
                // Two independently fitted rims can differ by a few microns in angular
                // phase. Merge those near-identical samples before float32 tessellation;
                // otherwise they create zero-width slivers at the shared cap boundary.
                double angularTolerance=Math.Max(1e-6,.001/p.Hole.Radius);
                var all=p.Hole.RimPointsA.Concat(p.Hole.RimPointsB).Select(v=>Angle(p,v)).OrderBy(a=>a).ToList();var unique=new List<double>();foreach(double a in all)if(unique.Count==0||a-unique[unique.Count-1]>angularTolerance)unique.Add(a);if(unique.Count>1&&2*Math.PI+unique[0]-unique[unique.Count-1]<=angularTolerance)unique.RemoveAt(unique.Count-1);
                // Clipped endpoints retain the tight source-boundary tolerance.
                double tolerance=(p.Hole.TrimmedRimA||p.Hole.TrimmedRimB) ? .002 : density.ChordToleranceMm;
                int minimum=(p.Hole.TrimmedRimA||p.Hole.TrimmedRimB) ? 96 : density.MinimumCircleSegments;
                double step=Math.Min(2*Math.PI/minimum,2*Math.Acos(Math.Max(-1,1-tolerance/p.Hole.Radius)));
                if(step<2*Math.PI/2048){p.Rejected=true;p.Failure="display-resolution";continue;}
                p.Angles=new List<double>();for(int i=0;i<unique.Count;i++){double a=unique[i],b=i+1<unique.Count?unique[i+1]:unique[0]+2*Math.PI;int count=(int)Math.Ceiling((b-a)/step);for(int k=0;k<count;k++)p.Angles.Add((a+(b-a)*k/count)%(2*Math.PI));}p.Angles.Sort();
                p.Cylinder=new Cylinder{SourceSurface=p.Hole.Surface,Surface=checked(++nextSurface),Center=p.Hole.Center,Axis=p.Hole.Axis,Radius=p.Hole.Radius,Depth=p.Hole.Depth,ExternalCylinder=p.Hole.ExternalCylinder,TrimmedRimA=p.Hole.TrimmedRimA,TrimmedRimB=p.Hole.TrimmedRimB};
                var centerA=p.Hole.Center.Select((c,k)=>c+Dot(Sub(p.Hole.RimA,p.Hole.Center),p.Hole.Axis)*p.Hole.Axis[k]).ToArray();var centerB=p.Hole.Center.Select((c,k)=>c+Dot(Sub(p.Hole.RimB,p.Hole.Center),p.Hole.Axis)*p.Hole.Axis[k]).ToArray();p.Cylinder.RimA=centerA;p.Cylinder.RimB=centerB;
                p.Rings=new int[2][];for(int ring=0;ring<2;ring++){p.Rings[ring]=new int[p.Angles.Count];for(int i=0;i<p.Angles.Count;i++){double a=p.Angles[i],height=RimHeight(p,ring,a);var point=new float[3];for(int k=0;k<3;k++)point[k]=(float)(p.Hole.Center[k]+height*p.Hole.Axis[k]+p.Hole.Radius*(Math.Cos(a)*p.U[k]+Math.Sin(a)*p.V[k]));p.Rings[ring][i]=result.Vertices.Count;result.Vertices.Add(point);}}
                foreach(var e in p.Edges)e.Arc=Arc(p,e.Ring,Angle(p,points[e.A]),Angle(p,points[e.B]));
            }
            var snaps=new Dictionary<Tuple<int,float,float,float>,int>();
            var patches=BuildCapPatches(source,faces,walls,capEdges,id,points,new HashSet<int>(joinEdges.Keys));
            var capTriangles=new Dictionary<int,List<int[]>>();bool retry;
            do
            {
                retry=false;snaps.Clear();capTriangles.Clear();
                foreach(var p in plans.Where(p=>!p.Rejected)){int ring=0;foreach(var rim in new[]{p.Hole.RimPointsA,p.Hole.RimPointsB}){foreach(var point in rim)snaps[Key(p.Hole.Surface,point)]=p.Rings[ring][Closest(p.Angles,Angle(p,point))];ring++;}}
                foreach(var p in plans.Where(p=>!p.Rejected))foreach(var e in p.Edges.Where(e=>e.Cap>=0))foreach(int endpoint in new[]{e.A,e.B})
                    snaps[Key(faces[e.Cap].Surface,points[endpoint])]=p.Rings[e.Ring][Closest(p.Angles,Angle(p,points[endpoint]))];
                foreach(var join in joinEdges)
                {
                    var active=join.Value.Where(e=>!e.Plan.Rejected).Select(e=>e.Plan).Distinct().ToList();if(active.Count==0)continue;
                    var f=faces[join.Key];var polygon=new List<int>();for(int k=0;k<3;k++)
                    {
                        int a=id(f.Surface,D(source[f.Idx[k]])),b=id(f.Surface,D(source[f.Idx[(k+1)%3]]));RimEdge e;
                        if(edges.TryGetValue(Edge(a,b),out e)&&!e.Plan.Rejected){bool forward=a==e.A;for(int v=0;v<e.Arc.Length-1;v++)polygon.Add(e.Arc[forward?v:e.Arc.Length-1-v]);}
                        else {int mapped;polygon.Add(snaps.TryGetValue(Key(f.Surface,D(source[f.Idx[k]])),out mapped)?mapped:f.Idx[k]);}
                    }
                    var triangles=Triangulate(polygon,result.Vertices,Normal(source,f.Idx));
                    if(triangles==null){foreach(var p in active){p.Rejected=true;p.Failure="transition-triangulation";}retry=true;}else capTriangles.Add(join.Key,triangles);
                }
                foreach(var patch in patches)
                {
                    var active=patch.Plans.Where(p=>!p.Rejected).ToList();if(active.Count==0)continue;
                    string failure;var triangulated=TriangulateCapPatch(patch,source,result.Vertices,faces,edges,points,snaps,out failure);
                    if(triangulated==null){var trimmed=active.Where(p=>p.Hole.TrimmedRimA||p.Hole.TrimmedRimB).ToList();foreach(var p in trimmed.Count>0?trimmed:active){p.Rejected=true;p.Failure=failure;result.Diagnostics.Add("shape="+p.Hole.Surface+" radius="+p.Hole.Radius+" direction="+(p.Hole.ExternalCylinder?"outer":"inner")+" reason="+failure);}retry=true;continue;}
                    foreach(int fi in patch.Faces)capTriangles.Add(fi,new List<int[]>());
                    capTriangles[patch.Faces[0]]=triangulated;
                }
            }while(retry);
            foreach(var p in plans.Where(p=>!p.Rejected))result.Cylinders.Add(p.Cylinder.Surface,p.Cylinder);
            for(int fi=0;fi<faces.Count;fi++)
            {
                Plan wall;if(walls.TryGetValue(fi,out wall)&&!wall.Rejected){result.RemovedWallTriangles++;result.ChangedSourceTriangles.Add(fi);continue;}
                var f=faces[fi];var mapped=(int[])f.Idx.Clone();bool changed=false;for(int k=0;k<3;k++){int replacement;if(snaps.TryGetValue(Key(f.Surface,D(source[f.Idx[k]])),out replacement)){mapped[k]=replacement;changed=true;}}
                List<int[]> cap;if(capTriangles.TryGetValue(fi,out cap)){foreach(var triangle in cap){var replacement=Triangle(f,triangle[0],triangle[1],triangle[2]);var n=Unit(Normal(result.Vertices,triangle));replacement.Nx=(float)n[0];replacement.Ny=(float)n[1];replacement.Nz=(float)n[2];result.Faces.Add(replacement);result.SourceFaceIndices[replacement.Idx]=fi;result.AddedCapTriangles++;}result.ReplacedCapTriangles++;result.ChangedSourceTriangles.Add(fi);continue;}
                if(changed){f.Idx=mapped;result.SourceFaceIndices[mapped]=fi;result.ChangedSourceTriangles.Add(fi);}result.Faces.Add(f);
            }
            foreach(var p in plans.Where(p=>!p.Rejected))
            {
                var color=faces[p.Hole.WallTriangles[0]];color.Surface=p.Cylinder.Surface;
                for(int i=0;i<p.Angles.Count;i++){int j=(i+1)%p.Angles.Count;foreach(var t in new[]{new[]{p.Rings[0][i],p.Rings[0][j],p.Rings[1][j]},new[]{p.Rings[0][i],p.Rings[1][j],p.Rings[1][i]}}){var middle=D(result.Vertices[t[0]]);var radial=Sub(middle,p.Hole.Center);double axial=Dot(radial,p.Hole.Axis);for(int k=0;k<3;k++)radial[k]-=axial*p.Hole.Axis[k];var n=Normal(result.Vertices,t);if(Dot(n,radial)*(p.Hole.ExternalCylinder?-1:1)>0){int swap=t[1];t[1]=t[2];t[2]=swap;}n=Unit(Normal(result.Vertices,t));color.Nx=(float)n[0];color.Ny=(float)n[1];color.Nz=(float)n[2];result.Faces.Add(Triangle(color,t[0],t[1],t[2]));result.AddedWallTriangles++;}}
            }
            if(log!=null)log("[CGR cylindrical replacement] accepted="+result.Cylinders.Count+" eligible="+plans.Count+" removedWalls="+result.RemovedWallTriangles+" replacedCaps="+result.ReplacedCapTriangles+" addedWalls="+result.AddedWallTriangles+" addedCaps="+result.AddedCapTriangles+" regularDisplaySag<="+density.ChordToleranceMm+"mm; clipped ends<=0.002mm; ambiguous caps retained");
            if(log!=null)log("[CGR source trim] cylinders="+result.Cylinders.Values.Count(c=>c.TrimmedRimA||c.TrimmedRimB)+"; source axial profile retained; trimmed ends have no circle attribute");
            if(log!=null){foreach(var g in plans.Where(p=>p.Rejected).GroupBy(p=>(p.Failure??"rim-topology").Split(' ')[0]))log("[CGR cylinder guard] reason="+g.Key+" count="+g.Count()+" exampleShapes="+string.Join(",",g.Take(8).Select(p=>p.Hole.Surface)));log("[CGR cylinder directions] inner="+result.Cylinders.Values.Count(c=>!c.ExternalCylinder)+" outer="+result.Cylinders.Values.Count(c=>c.ExternalCylinder));}
            return result;
        }
    }
}
