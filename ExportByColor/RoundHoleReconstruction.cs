using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TxTools.ExportByColor
{
    // Reconstructs geometric parameters from tessellation, not original CAD feature history.
    // Exact-position welding is analysis-only, scoped to a JT Shape; it never changes output mesh indices.
    public static class RoundHoleReconstruction
    {
        public sealed class Options
        {
            // Coarse JT circles can have only eight samples. Full rings, radial
            // wall evidence and paired boundaries are still required to replace them.
            public int MinimumSegments = 8;
            public double AbsoluteTolerance = 0.01;
            public double RelativeTolerance = 0.001;
            public bool IncludeExternalCylinders;
            public bool IncludeTransitionRims;
        }
        public sealed class Hole
        {
            public int Surface, Segments, WallTriangleCount;
            public double[] Center, Axis, RimA, RimB;
            public double Radius, Depth, MaximumError;
            public bool PairedRims;
            public bool ExternalCylinder;
            public int[] WallTriangles;
            public double[][] RimPointsA, RimPointsB;
        }
        private sealed class Edge
        {
            internal int A, B;
            internal readonly List<int> Faces = new List<int>(2);
        }
        private sealed class Rim
        {
            internal double[] Center, Axis;
            internal double Radius, Error;
            internal int Segments;
            internal int WallComponent, WallTriangleCount;
            internal List<double[]> Points;
            internal bool ExternalCylinder;
            internal List<int> CapFaces;
        }
        public static List<Hole> Find(List<float[]> vertices, List<CgrWriter.Face> faces, Options options = null)
        {
            if (vertices == null || faces == null) throw new ArgumentNullException("mesh");
            options = options ?? new Options();
            if (options.MinimumSegments < 8 || !Positive(options.AbsoluteTolerance) || !Positive(options.RelativeTolerance)
                || options.RelativeTolerance > 0.05) throw new ArgumentException("Invalid circle fitting options");
            var groups = new Dictionary<int, List<int>>();
            for(int faceIndex=0;faceIndex<faces.Count;faceIndex++)
            {
                var face=faces[faceIndex];List<int> group;
                if (!groups.TryGetValue(face.Surface, out group)) groups.Add(face.Surface, group = new List<int>());
                group.Add(faceIndex);
            }
            var result = new List<Hole>();
            foreach (var group in groups)
            {
                var candidates=new List<Hole>();AnalyzeShape(vertices,faces,group.Value,group.Key,options,candidates);
                if(options.IncludeTransitionRims)
                {
                    // Separate chamfers from fine cylindrical walls at stricter
                    // crease angles. Coarse walls still use the original pass.
                    foreach(double crease in new[]{Math.PI/6,Math.PI/12})
                    {
                        var extra=new List<Hole>();AnalyzeShape(vertices,faces,group.Value,group.Key,options,extra,crease);
                        var occupied=new HashSet<int>();foreach(var h in candidates)if(h.PairedRims)foreach(int fi in h.WallTriangles)occupied.Add(fi);
                        foreach(var h in extra)if(h.PairedRims&&!Array.Exists(h.WallTriangles,fi=>occupied.Contains(fi))){candidates.RemoveAll(c=>!c.PairedRims&&Array.Exists(c.WallTriangles,fi=>Array.IndexOf(h.WallTriangles,fi)>=0));candidates.Add(h);foreach(int fi in h.WallTriangles)occupied.Add(fi);}
                    }
                }
                result.AddRange(candidates);
            }
            return result;
        }
        private static bool Positive(double v) { return v > 0 && !double.IsNaN(v) && !double.IsInfinity(v); }
        private static long Key(int a, int b) { return ((long)Math.Min(a,b) << 32) | (uint)Math.Max(a,b); }
        private static double[] Sub(double[] a, double[] b) { return new[] { a[0]-b[0],a[1]-b[1],a[2]-b[2] }; }
        private static double Dot(double[] a,double[] b) { return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]; }
        private static double[] Cross(double[] a,double[] b) { return new[] {a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]}; }
        private static double Length(double[] a) { return Math.Sqrt(Dot(a,a)); }
        private static double[] Unit(double[] a) { double n=Length(a);if(!Positive(n))return null;return new[]{a[0]/n,a[1]/n,a[2]/n}; }
        private static void Canonical(double[] axis) { int k=0;for(int j=1;j<3;j++)if(Math.Abs(axis[j])>Math.Abs(axis[k]))k=j;if(axis[k]<0)for(int j=0;j<3;j++)axis[j]=-axis[j]; }
        private static int Root(int[] parent,int i) { while(parent[i]!=i){parent[i]=parent[parent[i]];i=parent[i];}return i; }

        private static void AnalyzeShape(List<float[]> source,List<CgrWriter.Face> faces,List<int> sourceIndices,int surface,Options options,List<Hole> result,double creaseAngle=Math.PI/3)
        {
            var positions=new Dictionary<Tuple<float,float,float>,int>();var vertices=new List<double[]>();
            var edges=new Dictionary<long,Edge>();var normals=new List<double[]>();var triangles=new List<int[]>();var areas=new List<double>();
            var originalIndices=new List<int>();
            foreach(int sourceIndex in sourceIndices)
            {
                var face=faces[sourceIndex];
                if(face.Idx==null||face.Idx.Length!=3)throw new ArgumentException("Expected triangle");
                int[] ids=new int[3];
                for(int k=0;k<3;k++)
                {
                    int id=face.Idx[k];if(id<0||id>=source.Count)throw new ArgumentException("Triangle index out of range");
                    var p=source[id];if(p==null||p.Length!=3)throw new ArgumentException("Expected XYZ");
                    foreach(float v in p)if(float.IsNaN(v)||float.IsInfinity(v))throw new ArgumentException("Non-finite point");
                    var key=Tuple.Create(p[0],p[1],p[2]);int local;
                    if(!positions.TryGetValue(key,out local)){local=vertices.Count;positions.Add(key,local);vertices.Add(new[]{(double)p[0],p[1],p[2]});}
                    ids[k]=local;
                }
                var rawNormal=Cross(Sub(vertices[ids[1]],vertices[ids[0]]),Sub(vertices[ids[2]],vertices[ids[0]]));var normal=Unit(rawNormal);
                if(normal==null)continue;
                int f=normals.Count;normals.Add(normal);areas.Add(Length(rawNormal));triangles.Add(ids);originalIndices.Add(sourceIndex);
                for(int k=0;k<3;k++){int a=ids[k],b=ids[(k+1)%3];long key=Key(a,b);Edge edge;if(!edges.TryGetValue(key,out edge))edges.Add(key,edge=new Edge{A=a,B=b});edge.Faces.Add(f);}
            }
            // Only manifold sharp joins may form rims. Open seams and ambiguous joins are excluded.
            var sharp=new List<Edge>();
            var parent=new int[normals.Count];for(int i=0;i<parent.Length;i++)parent[i]=i;
            foreach(var edge in edges.Values)if(edge.Faces.Count==2&&Dot(normals[edge.Faces[0]],normals[edge.Faces[1]])>(Math.Cos(creaseAngle)+1e-7))parent[Root(parent,edge.Faces[0])]=Root(parent,edge.Faces[1]);
            var components=new Dictionary<int,List<int>>();for(int i=0;i<parent.Length;i++){int root=Root(parent,i);List<int> component;if(!components.TryGetValue(root,out component))components.Add(root,component=new List<int>());component.Add(i);}
            foreach(var edge in edges.Values)
            {
                if(edge.Faces.Count!=2||Dot(normals[edge.Faces[0]],normals[edge.Faces[1]])>(Math.Cos(creaseAngle)+1e-7))continue;
                sharp.Add(edge);
            }
            // Follow each surface component's boundary separately. A cap can
            // have a seam meeting a circular rim at a vertex; the global sharp
            // edge graph then branches even though the cylinder itself is closed.
            var boundaries=new Dictionary<int,List<Edge>>();foreach(var edge in sharp){int first=Root(parent,edge.Faces[0]),second=Root(parent,edge.Faces[1]);if(first==second)continue;foreach(int root in new[]{first,second}){List<Edge> boundary;if(!boundaries.TryGetValue(root,out boundary))boundaries.Add(root,boundary=new List<Edge>());boundary.Add(edge);}}
            var rims=new List<Rim>();foreach(var boundary in boundaries)
            {
              var componentSharp=boundary.Value;var componentLinks=new Dictionary<int,List<int>>();for(int i=0;i<componentSharp.Count;i++)foreach(int v in new[]{componentSharp[i].A,componentSharp[i].B}){List<int> incident;if(!componentLinks.TryGetValue(v,out incident))componentLinks.Add(v,incident=new List<int>());incident.Add(i);}
              var used=new bool[componentSharp.Count];for(int seed=0;seed<componentSharp.Count;seed++)
              {
                if(used[seed])continue;
                int start=componentSharp[seed].A,current=start,edgeId=seed;var points=new List<double[]>();var ring=new List<Edge>();bool closed=false;
                while(!used[edgeId])
                {
                    used[edgeId]=true;var edge=componentSharp[edgeId];points.Add(vertices[current]);ring.Add(edge);
                    current=edge.A==current?edge.B:edge.A;
                    if(componentLinks[current].Count!=2)break;
                    if(current==start){closed=true;break;}
                    var next=componentLinks[current];edgeId=next[0]==edgeId?next[1]:next[0];
                }
                if(!closed||points.Count<options.MinimumSegments)continue;
                Rim rim;if(!Fit(points,options,out rim))continue;
                int inward=0,outward=0;var wallComponents=new HashSet<int>();var capNormal=new double[3];var capFaces=new HashSet<int>();
                foreach(var edge in ring)
                {
                    var mid=new[]{(vertices[edge.A][0]+vertices[edge.B][0])/2,(vertices[edge.A][1]+vertices[edge.B][1])/2,(vertices[edge.A][2]+vertices[edge.B][2])/2};
                    var radial=Unit(Sub(mid,rim.Center));if(radial==null)continue;
                    var a=normals[edge.Faces[0]];var b=normals[edge.Faces[1]];
                    double direction=0;int wall=-1,cap=-1;
                    double transition=options.IncludeTransitionRims?.15:.95;
                    if(Math.Abs(Dot(a,rim.Axis))>transition&&Math.Abs(Dot(b,rim.Axis))<0.15){direction=Dot(b,radial);wall=edge.Faces[1];cap=edge.Faces[0];}
                    else if(Math.Abs(Dot(b,rim.Axis))>transition&&Math.Abs(Dot(a,rim.Axis))<0.15){direction=Dot(a,radial);wall=edge.Faces[0];cap=edge.Faces[1];}
                    if(cap>=0&&Math.Abs(Dot(normals[cap],rim.Axis))>.9999){capFaces.Add(cap);for(int k=0;k<3;k++)capNormal[k]+=normals[cap][k]*areas[cap]*(Dot(normals[cap],rim.Axis)<0?-1:1);}
                    if(direction<-.90){inward++;wallComponents.Add(Root(parent,wall));}
                    else if(options.IncludeExternalCylinders&&direction>.90){outward++;wallComponents.Add(Root(parent,wall));}
                }
                int required=(int)Math.Ceiling(ring.Count*.90);
                if(inward<required&&outward<required)continue;
                // A small ring's float32 plane fit can tilt by enough to reject a
                // large adjacent cap. Area-weighted planar faces provide stronger
                // axial evidence; refit and still check every original rim point.
                var stableAxis=Unit(capNormal);Rim refined;
                if(stableAxis!=null&&Fit(points,options,out refined,stableAxis))rim=refined;
                rim.ExternalCylinder=outward>=required;
                if(wallComponents.Count!=1)continue;
                foreach(int component in wallComponents)rim.WallComponent=component;
                if(rim.WallComponent!=boundary.Key)continue;
                bool cylinder=true;double tolerance=Math.Max(options.AbsoluteTolerance,rim.Radius*options.RelativeTolerance);
                foreach(int f in components[rim.WallComponent])
                {
                    if(Math.Abs(Dot(normals[f],rim.Axis))>0.15){cylinder=false;break;}
                    foreach(int id in triangles[f]){var delta=Sub(vertices[id],rim.Center);double error=Math.Abs(Length(Cross(delta,rim.Axis))-rim.Radius);rim.Error=Math.Max(rim.Error,error);if(error>tolerance){cylinder=false;break;}}
                    if(!cylinder)break;
                }
                if(!cylinder)continue;rim.WallTriangleCount=components[rim.WallComponent].Count;rim.CapFaces=new List<int>(capFaces);
                rims.Add(rim);
              }
            }
            var paired=new bool[rims.Count];
            for(int i=0;i<rims.Count;i++)
            {
                if(paired[i])continue;var a=rims[i];int match=-1;double depth=double.PositiveInfinity;
                for(int j=i+1;j<rims.Count;j++)
                {
                    if(paired[j])continue;var b=rims[j];if(a.WallComponent!=b.WallComponent||a.ExternalCylinder!=b.ExternalCylinder)continue;double tolerance=Math.Max(options.AbsoluteTolerance,Math.Max(a.Radius,b.Radius)*options.RelativeTolerance);
                    if(Math.Abs(a.Radius-b.Radius)>tolerance||Math.Abs(Dot(a.Axis,b.Axis))<0.9999)continue;
                    var delta=Sub(b.Center,a.Center);double axial=Math.Abs(Dot(delta,a.Axis));double offset=Length(Cross(delta,a.Axis));
                    if(offset>tolerance||axial<=tolerance||axial>=depth)continue;
                    match=j;depth=axial;
                }
                // A common axis from both rim centers is more reliable than one
                // small ring's plane. Choose it only when it fits both rings and
                // improves agreement with the original adjacent planar faces.
                if(match>=0)
                {
                    var b=rims[match];Func<Rim,double> score=r=>{double error=0;foreach(int fi in r.CapFaces)foreach(int v in triangles[fi])error=Math.Max(error,Math.Abs(Dot(Sub(vertices[v],r.Center),r.Axis)));return Math.Max(error,r.Error);};
                    double best=Math.Max(score(a),score(b));
                    foreach(var proposed in new[]{Unit(Sub(b.Center,a.Center)),Unit(new[]{a.Axis[0]+b.Axis[0],a.Axis[1]+b.Axis[1],a.Axis[2]+b.Axis[2]})})
                    {
                        if(proposed==null||Math.Abs(Dot(proposed,a.Axis))<.9999)continue;Rim ra,rb;
                        if(!Fit(a.Points,options,out ra,proposed)||!Fit(b.Points,options,out rb,proposed))continue;
                        ra.CapFaces=a.CapFaces;rb.CapFaces=b.CapFaces;double candidate=Math.Max(score(ra),score(rb));
                        // Avoid changing a good axis for microscopic score gains:
                        // extra angular phase jitter can collapse float32 slivers.
                        if(best<=.001||candidate>=best*.8)continue;best=candidate;ra.WallComponent=a.WallComponent;ra.WallTriangleCount=a.WallTriangleCount;ra.ExternalCylinder=a.ExternalCylinder;rb.WallComponent=b.WallComponent;rb.WallTriangleCount=b.WallTriangleCount;rb.ExternalCylinder=b.ExternalCylinder;a=ra;rims[match]=rb;depth=Math.Abs(Dot(Sub(rb.Center,ra.Center),ra.Axis));
                    }
                }
                // Pairing alone does not prove a through hole: a blind-hole bottom can also be circular.
                paired[i]=true;var h=new Hole{Surface=surface,Center=(double[])a.Center.Clone(),Axis=(double[])a.Axis.Clone(),RimA=(double[])a.Center.Clone(),Radius=a.Radius,MaximumError=a.Error,Segments=a.Segments,WallTriangleCount=a.WallTriangleCount,ExternalCylinder=a.ExternalCylinder};
                if(match>=0){var b=rims[match];paired[match]=true;h.PairedRims=true;h.RimB=(double[])b.Center.Clone();h.Depth=depth;h.MaximumError=Math.Max(h.MaximumError,b.Error);h.Segments+=b.Segments;h.Radius=(a.Radius+b.Radius)/2;for(int k=0;k<3;k++)h.Center[k]=(a.Center[k]+b.Center[k])/2;}
                // Validate the final common axis/average radius, not just each independent rim fit.
                foreach(int f in components[a.WallComponent])foreach(int id in triangles[f])h.MaximumError=Math.Max(h.MaximumError,Math.Abs(Length(Cross(Sub(vertices[id],h.Center),h.Axis))-h.Radius));
                foreach(var rim in match>=0?new[]{a,rims[match]}:new[]{a})foreach(var point in rim.Points)h.MaximumError=Math.Max(h.MaximumError,Math.Abs(Dot(Sub(point,rim.Center),h.Axis)));
                if(h.MaximumError>Math.Max(options.AbsoluteTolerance,h.Radius*options.RelativeTolerance))continue;
                h.WallTriangles=components[a.WallComponent].ConvertAll(f=>originalIndices[f]).ToArray();
                h.RimPointsA=a.Points.ToArray();if(match>=0)h.RimPointsB=rims[match].Points.ToArray();
                result.Add(h);
            }
        }
        private static bool Fit(List<double[]> points,Options options,out Rim rim,double[] axisHint=null)
        {
            rim=null;var origin=new double[3];foreach(var p in points)for(int k=0;k<3;k++)origin[k]+=p[k]/points.Count;
            var normal=new double[3];for(int i=0;i<points.Count;i++){var c=Cross(Sub(points[i],origin),Sub(points[(i+1)%points.Count],origin));for(int k=0;k<3;k++)normal[k]+=c[k];}
            var axis=axisHint??Unit(normal);if(axis==null)return false;Canonical(axis);
            var u=Unit(Sub(points[0],origin));if(u==null)return false;u=Unit(Cross(axis,u));if(u==null)return false;var v=Cross(axis,u);
            double scale=0;foreach(var p in points)scale=Math.Max(scale,Length(Sub(p,origin)));if(!Positive(scale))return false;
            var matrix=new double[3,4];
            foreach(var p in points){var delta=Sub(p,origin);double x=Dot(delta,u)/scale,y=Dot(delta,v)/scale,q=x*x+y*y;double[] row={x,y,1};for(int a=0;a<3;a++){for(int b=0;b<3;b++)matrix[a,b]+=row[a]*row[b];matrix[a,3]+=row[a]*q;}}
            for(int k=0;k<3;k++){int pivot=k;for(int a=k+1;a<3;a++)if(Math.Abs(matrix[a,k])>Math.Abs(matrix[pivot,k]))pivot=a;if(Math.Abs(matrix[pivot,k])<1e-12)return false;for(int b=k;b<4;b++){double tmp=matrix[k,b];matrix[k,b]=matrix[pivot,b];matrix[pivot,b]=tmp;}double d=matrix[k,k];for(int b=k;b<4;b++)matrix[k,b]/=d;for(int a=0;a<3;a++)if(a!=k){d=matrix[a,k];for(int b=k;b<4;b++)matrix[a,b]-=d*matrix[k,b];}}
            double cx=matrix[0,3]/2,cy=matrix[1,3]/2,r2=matrix[2,3]+cx*cx+cy*cy;if(!Positive(r2))return false;
            double radius=Math.Sqrt(r2)*scale,tolerance=Math.Max(options.AbsoluteTolerance,radius*options.RelativeTolerance);var center=new double[3];for(int k=0;k<3;k++)center[k]=origin[k]+scale*(cx*u[k]+cy*v[k]);
            double error=0;var angles=new List<double>();
            foreach(var p in points){var delta=Sub(p,center);double plane=Math.Abs(Dot(delta,axis)),x=Dot(delta,u),y=Dot(delta,v);double radial=Math.Abs(Math.Sqrt(x*x+y*y)-radius);error=Math.Max(error,Math.Max(plane,radial));angles.Add(Math.Atan2(y,x));}
            if(error>tolerance)return false;
            double winding=0;int sign=0;for(int i=0;i<angles.Count;i++){double turn=angles[(i+1)%angles.Count]-angles[i];while(turn<=-Math.PI)turn+=2*Math.PI;while(turn>Math.PI)turn-=2*Math.PI;if(Math.Abs(turn)<1e-6||Math.Abs(turn)>Math.PI/4+Math.Max(1e-6,2*tolerance/radius))return false;int s=turn>0?1:-1;if(sign!=0&&sign!=s)return false;sign=s;winding+=turn;}if(Math.Abs(Math.Abs(winding)-2*Math.PI)>1e-5)return false;
            angles.Sort();double maxGap=0;for(int i=0;i<angles.Count;i++){double gap=(i+1<angles.Count?angles[i+1]:angles[0]+2*Math.PI)-angles[i];if(gap<1e-6)return false;maxGap=Math.Max(maxGap,gap);}if(maxGap>Math.PI/4+Math.Max(1e-6,2*tolerance/radius))return false;
            rim=new Rim{Center=center,Axis=axis,Radius=radius,Error=error,Segments=points.Count,Points=points};return true;
        }
        public static void WriteCsv(string path,List<Hole> holes)
        {
            using(var w=new StreamWriter(path,false,new UTF8Encoding(true)))
            {
                w.WriteLine("shape,kind,cx,cy,cz,ax,ay,az,radius,diameter,depth,max_fit_error,rim_segments,rim_a_x,rim_a_y,rim_a_z,rim_b_x,rim_b_y,rim_b_z");
                foreach(var h in holes){var row=new List<string>{h.Surface.ToString(CultureInfo.InvariantCulture),h.ExternalCylinder?(h.PairedRims?"paired_external_cylinder":"single_external_rim"):(h.PairedRims?"paired_circular_rims":"single_circular_rim")};foreach(double d in h.Center)row.Add(d.ToString("R",CultureInfo.InvariantCulture));foreach(double d in h.Axis)row.Add(d.ToString("R",CultureInfo.InvariantCulture));foreach(double d in new[]{h.Radius,h.Radius*2,h.Depth,h.MaximumError,(double)h.Segments})row.Add(d.ToString("R",CultureInfo.InvariantCulture));foreach(double d in h.RimA)row.Add(d.ToString("R",CultureInfo.InvariantCulture));for(int k=0;k<3;k++)row.Add(h.RimB==null?"":h.RimB[k].ToString("R",CultureInfo.InvariantCulture));w.WriteLine(string.Join(",",row));}
            }
        }
    }
}
