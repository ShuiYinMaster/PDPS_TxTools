using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.ExportByColor
{
    public static partial class RoundHoleCgrMesh
    {
        sealed class RefFacet
        {
            internal CgrWriter.Face Face;internal int[] Ids;internal int Original=-1;
            internal Cylinder Cylinder;internal double[] Normal,Origin;
        }
        sealed class RefEdge
        {
            internal int A,B,Middle=-1;internal readonly List<RefFacet> Faces=new List<RefFacet>();
        }
        sealed class RefWorkBudget
        {
            // Retain difficult source geometry rather than letting quadratic
            // patch searches monopolize a parallel resource conversion worker.
            internal long Remaining=4000000;
            internal bool Spend(long count){Remaining-=count;return Remaining>=0;}
        }
        static double RefRadius(double[] point,Cylinder c,out double[] gradient)
        {
            var d=Sub(point,c.Center);double height=Dot(d,c.Axis);for(int k=0;k<3;k++)d[k]-=height*c.Axis[k];
            double radius=Math.Sqrt(Dot(d,d));gradient=radius==0?new double[3]:d.Select(v=>v/radius).ToArray();return radius;
        }
        static double RefAlignment(RefFacet t,List<float[]> points)
        {
            var n=Normal(points,t.Ids);if(t.Cylinder==null)return Dot(n,t.Normal);
            var center=Enumerable.Range(0,3).Select(k=>t.Ids.Average(v=>(double)points[v][k])).ToArray();double[] g;RefRadius(center,t.Cylinder,out g);return Dot(n,g)*(t.Cylinder.ExternalCylinder?1:-1);
        }
        static bool RefProject(double[] point,List<Cylinder> cylinders,List<RefFacet> planes,out float[] projected)
        {
            projected=null;var p=(double[])point.Clone();double initial=0;
            foreach(var c in cylinders){double[] g;initial=Math.Max(initial,Math.Abs(RefRadius(p,c,out g)-c.Radius));}
            // The correction is bounded by the chord/fit error at the source
            // point. Newton cannot jump to another intersection branch.
            double budget=Math.Max(.01,5*initial+.001);
            if(initial<.0002&&planes.All(plane=>Math.Abs(Dot(Sub(p,plane.Origin),plane.Normal))<.0002)){projected=p.Select(v=>(float)v).ToArray();return true;}
            for(int iteration=0;iteration<32;iteration++)
            {
                var a=new double[3,4];double error=0;
                Action<double[],double> row=(g,r)=>{error=Math.Max(error,Math.Abs(r));for(int j=0;j<3;j++){a[j,3]-=g[j]*r;for(int k=0;k<3;k++)a[j,k]+=g[j]*g[k];}};
                foreach(var c in cylinders){double[] g;double r=RefRadius(p,c,out g)-c.Radius;if(g.All(v=>v==0))return false;row(g,r);}
                foreach(var plane in planes)row(plane.Normal,Dot(Sub(p,plane.Origin),plane.Normal));
                if(error<.0001)break;
                for(int j=0;j<3;j++)a[j,j]+=1e-10;
                for(int j=0;j<3;j++)
                {
                    int pivot=j;for(int k=j+1;k<3;k++)if(Math.Abs(a[k,j])>Math.Abs(a[pivot,j]))pivot=k;
                    if(Math.Abs(a[pivot,j])<1e-14)return false;
                    for(int k=j;k<4;k++){double swap=a[j,k];a[j,k]=a[pivot,k];a[pivot,k]=swap;}
                    double divisor=a[j,j];for(int k=j;k<4;k++)a[j,k]/=divisor;
                    for(int i=0;i<3;i++)if(i!=j){double factor=a[i,j];for(int k=j;k<4;k++)a[i,k]-=factor*a[j,k];}
                }
                for(int j=0;j<3;j++)p[j]+=a[j,3];
                if(p.Any(v=>double.IsNaN(v)||double.IsInfinity(v))||Math.Sqrt(Dot(Sub(p,point),Sub(p,point)))>budget)return false;
            }
            projected=p.Select(v=>(float)v).ToArray();var actual=D(projected);
            foreach(var c in cylinders){double[] g;if(Math.Abs(RefRadius(actual,c,out g)-c.Radius)>.001)return false;}
            return planes.All(plane=>Math.Abs(Dot(Sub(actual,plane.Origin),plane.Normal))<=.001);
        }
        static bool RefineSourceShape(Result result,List<float[]> originalVertices,List<CgrWriter.Face> originalFaces,int shape,List<Cylinder> selected,out string failure)
        {
            failure="source-region";var source=result.Vertices;var faces=result.Faces;var targets=new HashSet<Cylinder>(selected);
            var shapes=new HashSet<int>(result.Cylinders.Values.Where(c=>c.SourceSurface==shape).Select(c=>c.Surface)){shape};
            Func<float[],Tuple<float,float,float>> key=v=>Tuple.Create(v[0],v[1],v[2]);
            // Coincident contact faces of separate JT Shapes are separate
            // parts, not shared topology. Do not weld them through proximity.
            var region=new HashSet<int>();for(int fi=0;fi<faces.Count;fi++)if(shapes.Contains(faces[fi].Surface))region.Add(fi);
            if(region.Count>150000){failure="source-region-budget";return false;}
            var coordinates=new Dictionary<Tuple<float,float,float>,int>();var points=new List<float[]>();
            Func<int,int> local=v=>{int id;var value=key(source[v]);if(!coordinates.TryGetValue(value,out id)){id=points.Count;coordinates.Add(value,id);points.Add(source[v]);}return id;};
            var triangles=new List<RefFacet>();
            foreach(int fi in region)
            {
                var f=faces[fi];var n=Normal(source,f.Idx);double length=Math.Sqrt(Dot(n,n));if(length==0){failure="source-degenerate";return false;}
                Cylinder c;result.Cylinders.TryGetValue(f.Surface,out c);int original;bool known=result.SourceFaceIndices.TryGetValue(f.Idx,out original);
                var origin=D(source[f.Idx[0]]);if(known&&c!=null){var indices=originalFaces[original].Idx;n=Normal(originalVertices,indices);length=Math.Sqrt(Dot(n,n));origin=D(originalVertices[indices[0]]);}
                triangles.Add(new RefFacet{Face=f,Ids=f.Idx.Select(local).ToArray(),Cylinder=c,Normal=n.Select(v=>v/length).ToArray(),Origin=origin,Original=known?original:-1});
            }
            var incident=new Dictionary<int,List<RefFacet>>();foreach(var t in triangles)foreach(int id in t.Ids){List<RefFacet> list;if(!incident.TryGetValue(id,out list))incident.Add(id,list=new List<RefFacet>());list.Add(t);}
            Func<List<RefFacet>,List<Cylinder>> cylinders=list=>list.Where(t=>t.Cylinder!=null).Select(t=>t.Cylinder).Distinct().ToList();
            Func<List<RefFacet>,List<RefFacet>> planes=list=>list.Where(t=>t.Cylinder==null).ToList();
            foreach(var pair in incident)
            {
                if(!pair.Value.Any(t=>t.Cylinder!=null&&targets.Contains(t.Cylinder)))continue;
                float[] projected;if(!RefProject(D(points[pair.Key]),cylinders(pair.Value),planes(pair.Value),out projected)){failure="vertex-constraints point="+string.Join(",",points[pair.Key])+" cylinders="+string.Join("|",cylinders(pair.Value).Select(c=>c.Radius+":"+string.Join(",",c.Center)+":"+string.Join(",",c.Axis)))+" planes="+planes(pair.Value).Count;return false;}points[pair.Key]=projected;
            }
            int before=triangles.Count;var work=new RefWorkBudget();
            if(!RefRepairFlippedEdges(triangles,points,work)){failure="refinement-work-budget";return false;}
            if(!RefRebuildPlanarCaps(triangles,points,work,out triangles)){failure=work.Remaining<0?"refinement-work-budget":"planar-cap-boundaries";return false;}
            bool complete=false;string pendingEdge="";
            for(int pass=0;pass<24;pass++)
            {
                var edges=new Dictionary<long,RefEdge>();foreach(var t in triangles)for(int k=0;k<3;k++){int a=t.Ids[k],b=t.Ids[(k+1)%3];long edge=Edge(a,b);RefEdge e;if(!edges.TryGetValue(edge,out e))edges.Add(edge,e=new RefEdge{A=a,B=b});e.Faces.Add(t);}
                if(edges.Values.Any(e=>e.Faces.Count>2)){failure="source-nonmanifold pass="+pass;return false;}
                int added=0;
                foreach(var e in edges.Values)
                {
                    if(!e.Faces.Any(t=>t.Cylinder!=null&&targets.Contains(t.Cylinder)))continue;
                    var cs=cylinders(e.Faces);var midpoint=Enumerable.Range(0,3).Select(k=>((double)points[e.A][k]+points[e.B][k])/2).ToArray();
                    if(!cs.Any(c=>{double[] g;return Math.Abs(RefRadius(midpoint,c,out g)-c.Radius)>.00200001;}))continue;
                    float[] projected;if(!RefProject(midpoint,cs,planes(e.Faces),out projected)){failure="edge-constraints";return false;}
                    if(key(projected).Equals(key(points[e.A]))||key(projected).Equals(key(points[e.B]))){failure="float-resolution";return false;}
                    pendingEdge=" edge="+string.Join(",",points[e.A])+"|"+string.Join(",",points[e.B])+" midpoint="+string.Join(",",projected)+" radii="+string.Join(",",cs.Select(c=>c.Radius));
                    e.Middle=points.Count;points.Add(projected);added++;
                }
                if(added==0){complete=true;break;}
                var next=new List<RefFacet>();
                foreach(var t in triangles)
                {
                    var v=t.Ids;var m=Enumerable.Range(0,3).Select(k=>edges[Edge(v[k],v[(k+1)%3])].Middle).ToArray();int count=m.Count(id=>id>=0);var split=new List<int[]>();
                    if(count==0){next.Add(t);continue;}
                    if(count==1){int k=Array.FindIndex(m,id=>id>=0);split.Add(new[]{v[k],m[k],v[(k+2)%3]});split.Add(new[]{m[k],v[(k+1)%3],v[(k+2)%3]});}
                    else if(count==2){int k=Array.FindIndex(m,id=>id<0),a=v[k],b=v[(k+1)%3],c=v[(k+2)%3],mb=m[(k+1)%3],mc=m[(k+2)%3];split.Add(new[]{c,mc,mb});split.Add(new[]{mc,a,b});split.Add(new[]{mc,b,mb});}
                    else{split.Add(new[]{v[0],m[0],m[2]});split.Add(new[]{m[0],v[1],m[1]});split.Add(new[]{m[2],m[1],v[2]});split.Add(new[]{m[0],m[1],m[2]});}
                    foreach(var ids in split)next.Add(new RefFacet{Face=t.Face,Ids=ids,Cylinder=t.Cylinder,Normal=t.Normal,Origin=t.Origin,Original=t.Original});
                }
                if(next.Count>150000||points.Count>100000){failure="refinement-budget";return false;}
                if(!RefRepairFlippedEdges(next,points,work)){failure="refinement-work-budget";return false;}
                if(!RefRebuildPlanarCaps(next,points,work,out triangles)){failure=work.Remaining<0?"refinement-work-budget":"refined-facet-boundaries";return false;}
                if(triangles.Count>300000||points.Count>300000){failure="refinement-budget";return false;}
            }
            if(!complete){failure="refinement-depth triangles="+triangles.Count+" vertices="+points.Count+pendingEdge;return false;}
            foreach(var t in triangles)
            {
                double alignment=RefAlignment(t,points);
                if(alignment<=0){failure="triangle-winding surface="+t.Face.Surface+" radius="+(t.Cylinder==null?0:t.Cylinder.Radius)+" dot="+alignment+" points="+string.Join("|",t.Ids.Select(v=>string.Join(",",points[v])));return false;}
                if(t.Cylinder==null&&t.Ids.Any(v=>Math.Abs(Dot(Sub(D(points[v]),t.Origin),t.Normal))>.001)){failure="cap-plane";return false;}
                if(t.Cylinder!=null&&targets.Contains(t.Cylinder)&&t.Ids.Any(v=>{double[] g;return Math.Abs(RefRadius(D(points[v]),t.Cylinder,out g)-t.Cylinder.Radius)>.001;})){failure="cylinder-radius";return false;}
            }
            var beforeEdges=TrimEdgeCounts(region.Select(fi=>faces[fi]).ToList(),source);
            var afterEdges=TrimEdgeCounts(triangles.Select(t=>Triangle(t.Face,t.Ids[0],t.Ids[1],t.Ids[2])).ToList(),points);
            if(afterEdges.Values.Any(n=>n>2)||(beforeEdges.Values.All(n=>n==2)&&afterEdges.Values.Any(n=>n!=2))){failure="refined-shape-topology";return false;}
            // Positional aliases within the source Shape share one new vertex;
            // coincident contact geometry in other Shapes stays independent.
            var output=new List<float[]>(source);int offset=output.Count;output.AddRange(points);
            var rebuilt=new List<CgrWriter.Face>();for(int fi=0;fi<faces.Count;fi++)if(!region.Contains(fi))rebuilt.Add(faces[fi]);
            foreach(var t in triangles){var f=t.Face;f.Idx=t.Ids.Select(v=>offset+v).ToArray();var n=Unit(Normal(points,t.Ids));f.Nx=(float)n[0];f.Ny=(float)n[1];f.Nz=(float)n[2];rebuilt.Add(f);if(t.Original>=0)result.SourceFaceIndices[f.Idx]=t.Original;}
            result.RefinementAddedTriangles+=triangles.Count-before;result.Vertices=output;result.Faces=rebuilt;
            foreach(var c in selected){c.PreservedWallTessellation=false;c.RefinedSourceWall=true;}
            result.Diagnostics.Add("shape="+shape+" source-wall-refinement=accepted cylinders="+selected.Count+" triangles="+before+"->"+triangles.Count+"; joint cylinder/plane constraints, shared edges and source material retained");return true;
        }
        static bool RefRepairFlippedEdges(List<RefFacet> triangles,List<float[]> points,RefWorkBudget work)
        {
            // Curvature can turn an almost collinear parameter-domain triangle
            // inside out in 3D. Flip its internal diagonal, never its winding or
            // a material/cylinder boundary. Both replacement faces must face the
            // analytical surface correctly before the flip is accepted.
            for(int pass=0;pass<32;pass++)
            {
                if(!work.Spend(triangles.Count))return false;
                var bad=triangles.FirstOrDefault(t=>RefAlignment(t,points)<=0);if(bad==null)return true;
                bool fixedEdge=false;
                foreach(int k in Enumerable.Range(0,3).OrderByDescending(k=>Dot(Sub(D(points[bad.Ids[k]]),D(points[bad.Ids[(k+1)%3]])),Sub(D(points[bad.Ids[k]]),D(points[bad.Ids[(k+1)%3]])))))
                {
                    if(!work.Spend(2L*triangles.Count))return false;
                    int a=bad.Ids[k],b=bad.Ids[(k+1)%3],c=bad.Ids[(k+2)%3];var neighbors=triangles.Where(t=>t!=bad&&t.Ids.Contains(a)&&t.Ids.Contains(b)).ToList();if(neighbors.Count!=1)continue;var other=neighbors[0];
                    if(other.Cylinder!=bad.Cylinder||other.Face.Surface!=bad.Face.Surface||other.Face.R!=bad.Face.R||other.Face.G!=bad.Face.G||other.Face.B!=bad.Face.B||other.Face.Opacity!=bad.Face.Opacity)continue;
                    if(bad.Cylinder==null&&(Dot(bad.Normal,other.Normal)<.999999||Math.Abs(Dot(Sub(bad.Origin,other.Origin),bad.Normal))>.001))continue;
                    int d=other.Ids.First(id=>id!=a&&id!=b);if(d==c||triangles.Any(t=>t!=bad&&t!=other&&t.Ids.Contains(c)&&t.Ids.Contains(d)))continue;
                    var first=new RefFacet{Face=bad.Face,Ids=new[]{c,a,d},Cylinder=bad.Cylinder,Normal=bad.Normal,Origin=bad.Origin,Original=bad.Original};
                    var last=new RefFacet{Face=other.Face,Ids=new[]{c,d,b},Cylinder=other.Cylinder,Normal=other.Normal,Origin=other.Origin,Original=other.Original};
                    if(RefAlignment(first,points)<=0||RefAlignment(last,points)<=0)continue;
                    bad.Ids=first.Ids;other.Ids=last.Ids;fixedEdge=true;break;
                }
                if(!fixedEdge)return true;
            }
            return true;
        }
        static bool RefRebuildPlanarCaps(List<RefFacet> triangles,List<float[]> points,RefWorkBudget work,out List<RefFacet> rebuilt)
        {
            rebuilt=new List<RefFacet>();var caps=triangles;
            if(!work.Spend(2L*triangles.Count))return false;
            var parents=Enumerable.Range(0,caps.Count).ToArray();var edges=new Dictionary<long,List<int>>();
            for(int i=0;i<caps.Count;i++)for(int k=0;k<3;k++){long edge=Edge(caps[i].Ids[k],caps[i].Ids[(k+1)%3]);List<int> list;if(!edges.TryGetValue(edge,out list))edges.Add(edge,list=new List<int>());list.Add(i);}
            foreach(var list in edges.Values.Where(l=>l.Count==2))
            {
                var a=caps[list[0]];var b=caps[list[1]];
                if(a.Face.Surface==b.Face.Surface&&a.Face.R==b.Face.R&&a.Face.G==b.Face.G&&a.Face.B==b.Face.B&&a.Face.Opacity==b.Face.Opacity&&Dot(a.Normal,b.Normal)>.999999&&Math.Abs(Dot(Sub(a.Origin,b.Origin),a.Normal))<.001){int first=Root(parents,list[0]),last=Root(parents,list[1]);parents[last]=first;}
            }
            foreach(var indices in Enumerable.Range(0,caps.Count).GroupBy(i=>Root(parents,i)))
            {
                var group=indices.Select(i=>caps[i]).ToList();var first=group[0];
                // Keep valid subdivision connectivity and its Steiner points.
                // Only a patch whose old diagonal flipped needs retriangulation.
                if(group.All(t=>RefAlignment(t,points)>0)){rebuilt.AddRange(group);continue;}
                if(group.Count==1){rebuilt.Add(first);continue;}
                var boundary=new Dictionary<long,List<int[]>>();foreach(var t in group)for(int k=0;k<3;k++){int[] edge={t.Ids[k],t.Ids[(k+1)%3]};List<int[]> list;long id=Edge(edge[0],edge[1]);if(!boundary.TryGetValue(id,out list))boundary.Add(id,list=new List<int[]>());list.Add(edge);}
                if(boundary.Values.Any(l=>l.Count>2))return false;
                var n=first.Normal;var u=Unit(Cross(n,Math.Abs(n[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));var v=Cross(n,u);
                Func<double[],double[]> xy=p=>{var d=Sub(p,first.Origin);return new[]{Dot(d,u),Dot(d,v)};};
                List<int[]> mesh;var outline=boundary.Values.Where(l=>l.Count==1).Select(l=>l[0]).ToList();
                if(first.Cylinder==null){if(!TriangulateTrimBoundary(outline,points,n,xy,1,out mesh))return false;if(!RefInsertInteriorPoints(mesh,group.SelectMany(t=>t.Ids).Distinct().ToList(),id=>xy(D(points[id])),work))return false;}
                else
                {
                    var c=first.Cylinder;u=Unit(Cross(c.Axis,Math.Abs(c.Axis[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));v=Cross(c.Axis,u);
                    Func<int,double> angle=id=>{var d=Sub(D(points[id]),c.Center);return Math.Atan2(Dot(d,v),Dot(d,u));};double reference=angle(first.Ids[0]);
                    var ids=group.SelectMany(t=>t.Ids).Distinct().ToList();var mapping=ids.Select((id,i)=>new{id,i}).ToDictionary(p=>p.id,p=>p.i);
                    var uv=ids.Select(id=>new[]{(float)(c.Radius*Turn(reference,angle(id))),(float)Dot(Sub(D(points[id]),c.Center),c.Axis),0f}).ToList();
                    // Work within one original cylindrical facet, never across
                    // the periodic seam or a half-turn of the cylinder.
                    if(uv.Max(q=>q[0])-uv.Min(q=>q[0])>c.Radius*Math.PI/2)return false;
                    var uvBoundary=outline.Select(e=>new[]{mapping[e[0]],mapping[e[1]]}).ToList();double sign=c.ExternalCylinder?1:-1;
                    List<int[]> parameterMesh;if(!TriangulateTrimBoundary(uvBoundary,uv,new[]{0d,0d,sign},p=>new[]{p[0],p[1]},sign,out parameterMesh))return false;
                    if(!RefInsertInteriorPoints(parameterMesh,Enumerable.Range(0,uv.Count).ToList(),id=>new[]{(double)uv[id][0],uv[id][1]},work))return false;
                    mesh=parameterMesh.Select(t=>t.Select(id=>ids[id]).ToArray()).ToList();
                }
                foreach(var ids in mesh)rebuilt.Add(new RefFacet{Face=first.Face,Ids=ids,Cylinder=first.Cylinder,Normal=n,Origin=first.Origin,Original=first.Original});
            }
            return true;
        }
        static bool RefInsertInteriorPoints(List<int[]> mesh,List<int> points,Func<int,double[]> xy,RefWorkBudget work)
        {
            var used=new HashSet<int>(mesh.SelectMany(t=>t));
            foreach(int id in points.Where(id=>!used.Contains(id)))
            {
                var p=xy(id);int found=-1;long edge=-1;
                for(int i=0;i<mesh.Count;i++)
                {
                    if(!work.Spend(1))return false;
                    var t=mesh[i];var q=t.Select(xy).ToArray();double sign=Cross2(q[0],q[1],q[2])>=0?1:-1;
                    // A projected midpoint on a diagonal can round to either
                    // side in Float32 UV. Insert it into both incident faces;
                    // testing triangle containment first can lose that point.
                    for(int k=0;k<3;k++){var a=q[k];var b=q[(k+1)%3];double dx=b[0]-a[0],dy=b[1]-a[1],length=dx*dx+dy*dy;if(length==0)continue;double fraction=((p[0]-a[0])*dx+(p[1]-a[1])*dy)/length;if(fraction>1e-7&&fraction<1-1e-7&&Math.Abs(Cross2(a,b,p))/Math.Sqrt(length)<=.00001){found=i;edge=Edge(t[k],t[(k+1)%3]);break;}}
                    if(found>=0)break;
                    bool inside=true;for(int k=0;k<3;k++){var a=q[k];var b=q[(k+1)%3];if(sign*Cross2(a,b,p)<-1e-9){inside=false;break;}}
                    if(!inside)continue;found=i;
                    for(int k=0;k<3;k++){var a=q[k];var b=q[(k+1)%3];double dx=b[0]-a[0],dy=b[1]-a[1],length=dx*dx+dy*dy;if(length==0)continue;double fraction=((p[0]-a[0])*dx+(p[1]-a[1])*dy)/length;if(fraction>1e-7&&fraction<1-1e-7&&Math.Abs(Cross2(a,b,p))/Math.Sqrt(length)<1e-7){edge=Edge(t[k],t[(k+1)%3]);break;}}
                    break;
                }
                if(found<0)continue;
                if(edge<0){var t=mesh[found];mesh.RemoveAt(found);mesh.Add(new[]{t[0],t[1],id});mesh.Add(new[]{t[1],t[2],id});mesh.Add(new[]{t[2],t[0],id});}
                else for(int i=mesh.Count-1;i>=0;i--){if(!work.Spend(1))return false;var t=mesh[i];for(int k=0;k<3;k++)if(Edge(t[k],t[(k+1)%3])==edge){mesh.RemoveAt(i);mesh.Add(new[]{t[k],id,t[(k+2)%3]});mesh.Add(new[]{id,t[(k+1)%3],t[(k+2)%3]});break;}}
                used.Add(id);
            }
            return true;
        }
        static void RefineRegisteredSourceWalls(Result result,List<float[]> originalVertices,List<CgrWriter.Face> originalFaces,Action<string> log)
        {
            foreach(var group in result.Cylinders.Values.Where(c=>c.PreservedWallTessellation).GroupBy(c=>c.SourceSurface).ToList())
            {
                string failure;if(!RefineSourceShape(result,originalVertices,originalFaces,group.Key,group.ToList(),out failure)){result.Diagnostics.Add("shape="+group.Key+" source-wall-refinement=retained reason="+failure);if(log!=null)log("[CGR source wall refinement guard] shape="+group.Key+" reason="+failure);}
            }
            if(log!=null)log("[CGR source wall refinement] refined="+result.Cylinders.Values.Count(c=>c.RefinedSourceWall)+" retained="+result.Cylinders.Values.Count(c=>c.PreservedWallTessellation)+" addedTriangles="+result.RefinementAddedTriangles+"; joint constraints; displaySag<=0.002mm for refined walls");
        }
        static void RefineRemainingSourceWalls(Result result,List<float[]> vertices,List<CgrWriter.Face> faces,List<RoundHoleReconstruction.Hole> holes,Action<string> log)
        {
            var accepted=new HashSet<double[]>(result.Cylinders.Values.Select(c=>c.Center));
            var remaining=holes.Where(h=>h.PairedRims&&h.WallTriangles!=null&&h.RimPointsA!=null&&h.RimPointsB!=null&&h.Depth>.02&&!accepted.Contains(h.Center)).ToList();
            if(remaining.Count==0)return;
            var origins=new Dictionary<int[],int>();for(int fi=0;fi<faces.Count;fi++)if(!origins.ContainsKey(faces[fi].Idx))origins.Add(faces[fi].Idx,fi);int recovered=0;
            foreach(var h in remaining)
            {
                if(h.WallTriangles.All(fi=>(faces[fi].Opacity??255)==0))continue;
                var walls=new HashSet<int>(h.WallTriangles);var seen=new HashSet<int>();var current=new List<int>();
                for(int fi=0;fi<result.Faces.Count;fi++)
                {
                    var f=result.Faces[fi];if(result.Cylinders.ContainsKey(f.Surface))continue;int original;
                    if((result.SourceFaceIndices.TryGetValue(f.Idx,out original)||origins.TryGetValue(f.Idx,out original))&&walls.Contains(original)){current.Add(fi);seen.Add(original);}
                }
                // All proven wall facets must still be present; a subset is
                // insufficient evidence to claim a reconstructed cylinder.
                if(seen.Count!=walls.Count)continue;
                int surface=checked(result.Faces.Max(f=>f.Surface)+1);var old=new Dictionary<int,int>();
                foreach(int fi in current){var f=result.Faces[fi];old.Add(fi,f.Surface);int original;if(!result.SourceFaceIndices.ContainsKey(f.Idx)&&origins.TryGetValue(f.Idx,out original))result.SourceFaceIndices[f.Idx]=original;f.Surface=surface;result.Faces[fi]=f;}
                var cylinder=new Cylinder{Surface=surface,SourceSurface=h.Surface,Center=h.Center,Axis=h.Axis,RimA=h.RimA,RimB=h.RimB,Radius=h.Radius,Depth=h.Depth,ExternalCylinder=h.ExternalCylinder,TrimmedRimA=h.TrimmedRimA,TrimmedRimB=h.TrimmedRimB,PreservedWallTessellation=true};
                result.Cylinders.Add(surface,cylinder);string failure;
                if(RefineSourceShape(result,vertices,faces,h.Surface,new List<Cylinder>{cylinder},out failure)){recovered++;if(log!=null)log("[CGR source wall recovered] shape="+h.Surface+" radius="+h.Radius+"; source topology and per-face RGBA retained");}
                else{result.Cylinders.Remove(surface);foreach(var pair in old){var f=result.Faces[pair.Key];f.Surface=pair.Value;result.Faces[pair.Key]=f;}result.Diagnostics.Add("shape="+h.Surface+" radius="+h.Radius+" source-wall-fallback=retained reason="+failure);}
            }
            if(log!=null)log("[CGR source wall fallback] recovered="+recovered+"; failed candidates keep original geometry; no partial native group");
        }
    }
}
