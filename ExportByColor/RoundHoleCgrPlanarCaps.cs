using System;
using System.Collections.Generic;
using System.Linq;

namespace TxTools.ExportByColor
{
    public static partial class RoundHoleCgrMesh
    {
        sealed class CapPatch
        {
            internal readonly List<int> Faces=new List<int>();
            internal readonly HashSet<Plan> Plans=new HashSet<Plan>();
            internal readonly List<int[]> Boundary=new List<int[]>();
            internal readonly Dictionary<int,int> OriginalVertices=new Dictionary<int,int>();
            internal double[] Normal;internal bool Valid=true;
        }
        static int Root(int[] parents,int x){while(parents[x]!=x){parents[x]=parents[parents[x]];x=parents[x];}return x;}
        static List<CapPatch> BuildCapPatches(List<float[]> vertices,List<CgrWriter.Face> faces,
            Dictionary<int,Plan> walls,Dictionary<int,List<RimEdge>> caps,Func<int,double[],int> id,List<double[]> points)
        {
            var result=new List<CapPatch>();var eligible=new HashSet<int>(caps.Keys.Select(i=>faces[i].Surface));
            var groups=Enumerable.Range(0,faces.Count).Where(i=>eligible.Contains(faces[i].Surface)&&!walls.ContainsKey(i))
                .GroupBy(i=>Tuple.Create(faces[i].Surface,faces[i].R,faces[i].G,faces[i].B,faces[i].Opacity??255));
            foreach(var group in groups)
            {
                var indices=group.ToArray();var parents=Enumerable.Range(0,indices.Length).ToArray();
                var local=new int[indices.Length][];var normals=new double[indices.Length][];
                var adjacent=new Dictionary<long,List<int>>();
                for(int i=0;i<indices.Length;i++)
                {
                    var f=faces[indices[i]];local[i]=f.Idx.Select(v=>id(f.Surface,D(vertices[v]))).ToArray();
                    var n=Normal(vertices,f.Idx);normals[i]=Dot(n,n)>1e-20?Unit(n):null;
                    for(int k=0;k<3;k++){long e=Edge(local[i][k],local[i][(k+1)%3]);List<int> a;if(!adjacent.TryGetValue(e,out a))adjacent.Add(e,a=new List<int>());a.Add(i);}
                }
                foreach(var pair in adjacent)
                {
                    var a=pair.Value;if(a.Count!=2||normals[a[0]]==null||normals[a[1]]==null)continue;
                    if(Dot(normals[a[0]],normals[a[1]])<.99999999)continue;
                    var origin=points[local[a[0]][0]];
                    if(local[a[1]].Any(v=>Math.Abs(Dot(Sub(points[v],origin),normals[a[0]]))>.001))continue;
                    parents[Root(parents,a[1])]=Root(parents,a[0]);
                }
                var patches=new Dictionary<int,CapPatch>();
                for(int i=0;i<indices.Length;i++)
                {
                    int root=Root(parents,i);CapPatch p;if(!patches.TryGetValue(root,out p))patches.Add(root,p=new CapPatch{Normal=normals[i]});
                    p.Faces.Add(indices[i]);List<RimEdge> hits;if(caps.TryGetValue(indices[i],out hits))foreach(var e in hits)p.Plans.Add(e.Plan);
                    var face=faces[indices[i]];for(int k=0;k<3;k++)if(!p.OriginalVertices.ContainsKey(local[i][k]))p.OriginalVertices.Add(local[i][k],face.Idx[k]);
                }
                foreach(var pair in adjacent)
                {
                    var a=pair.Value;
                    foreach(int i in a)
                    {
                        var p=patches[Root(parents,i)];if(p.Plans.Count==0)continue;
                        if(a.Count>2){p.Valid=false;continue;}
                        if(a.Count==2&&Root(parents,a[0])==Root(parents,a[1]))continue;
                        for(int k=0;k<3;k++)if(Edge(local[i][k],local[i][(k+1)%3])==pair.Key)p.Boundary.Add(new[]{local[i][k],local[i][(k+1)%3]});
                    }
                }
                foreach(var p in patches.Values.Where(p=>p.Plans.Count>0))
                {
                    if(p.Normal==null)p.Valid=false;
                    else {var origin=D(vertices[faces[p.Faces[0]].Idx[0]]);if(p.OriginalVertices.Values.Any(v=>Math.Abs(Dot(Sub(D(vertices[v]),origin),p.Normal))>.01))p.Valid=false;}
                    result.Add(p);
                }
            }
            return result;
        }
        static List<int[]> TriangulateCapPatch(CapPatch patch,List<float[]> source,List<float[]> vertices,List<CgrWriter.Face> faces,
            Dictionary<long,RimEdge> rimEdges,List<double[]> points,Dictionary<Tuple<int,float,float,float>,int> snaps)
        {
            if(!patch.Valid||patch.Boundary.Count<3)return null;
            var next=new Dictionary<int,int>();var incoming=new HashSet<int>();
            foreach(var e in patch.Boundary){if(next.ContainsKey(e[0])||!incoming.Add(e[1]))return null;next.Add(e[0],e[1]);}
            if(next.Keys.Any(v=>!incoming.Contains(v)))return null;
            var remaining=new HashSet<int>(next.Keys);var loops=new List<List<int>>();int surface=faces[patch.Faces[0]].Surface;
            while(remaining.Count>0)
            {
                int start=remaining.First(),v=start;var loop=new List<int>();
                do
                {
                    if(!remaining.Remove(v))return null;int end=next[v];RimEdge e;
                    if(rimEdges.TryGetValue(Edge(v,end),out e)&&!e.Plan.Rejected)
                    {
                        bool forward=v==e.A;for(int k=0;k<e.Arc.Length-1;k++)loop.Add(e.Arc[forward?k:e.Arc.Length-1-k]);
                    }
                    else {int mapped;loop.Add(snaps.TryGetValue(Key(surface,points[v]),out mapped)?mapped:patch.OriginalVertices[v]);}
                    v=end;
                }while(v!=start);
                for(int i=loop.Count-1;i>=0;i--)if(loop.Count>1&&loop[i]==loop[(i+1)%loop.Count])loop.RemoveAt(i);
                if(loop.Count<3)return null;loops.Add(loop);
            }
            var origin=D(vertices[loops[0][0]]);var u=Unit(Cross(patch.Normal,Math.Abs(patch.Normal[0])<.8?new[]{1d,0d,0d}:new[]{0d,1d,0d}));var w=Cross(patch.Normal,u);
            Func<int,double[]> xy=v=>{var d=Sub(D(vertices[v]),origin);return new[]{Dot(d,u),Dot(d,w)};};
            Func<List<int>,double> area=loop=>{double a=0;for(int i=0;i<loop.Count;i++){var x=xy(loop[i]);var y=xy(loop[(i+1)%loop.Count]);a+=x[0]*y[1]-y[0]*x[1];}return a/2;};
            loops=loops.OrderByDescending(l=>Math.Abs(area(l))).ToList();
            double expectedArea=Math.Abs(area(loops[0]))-loops.Skip(1).Sum(l=>Math.Abs(area(l)));if(expectedArea<=1e-10)return null;
            var flattened=new List<int>();var holes=new List<int>();var coordinates=new List<double>();
            foreach(var loop in loops){if(flattened.Count>0)holes.Add(flattened.Count);foreach(int v in loop){flattened.Add(v);coordinates.AddRange(xy(v));}}
            var decoded=Vendor.EarCut.EarCut.Calculate(coordinates.ToArray(),holes.ToArray());
            if(decoded.Count==0||decoded.Count%3!=0)return null;var triangles=new List<int[]>();
            for(int i=0;i<decoded.Count;i+=3){var t=new[]{flattened[decoded[i]],flattened[decoded[i+1]],flattened[decoded[i+2]]};if(Dot(Normal(vertices,t),patch.Normal)<0){int s=t[1];t[1]=t[2];t[2]=s;}triangles.Add(t);}
            // EarCut can omit collinear boundary vertices. Reinsert them so adjacent
            // untouched surfaces and the new cylinder retain exactly the same edges.
            if(!RestoreCapBoundary(triangles,loops,vertices,patch.Normal))return null;
            double actualArea=0;foreach(var t in triangles){double a=Dot(Normal(vertices,t),patch.Normal)/2;if(a<=1e-12)return null;actualArea+=a;}
            if(Math.Abs(actualArea-expectedArea)>Math.Max(1e-6,expectedArea*1e-7))return null;
            return triangles;
        }
        static bool RestoreCapBoundary(List<int[]> triangles,List<List<int>> loops,List<float[]> vertices,double[] normal)
        {
            var expected=new HashSet<long>();foreach(var loop in loops)for(int i=0;i<loop.Count;i++)if(!expected.Add(Edge(loop[i],loop[(i+1)%loop.Count])))return false;
            for(int pass=0;pass<=expected.Count;pass++)
            {
                var actual=new Dictionary<long,List<int>>();for(int i=0;i<triangles.Count;i++)for(int k=0;k<3;k++){long e=Edge(triangles[i][k],triangles[i][(k+1)%3]);List<int> a;if(!actual.TryGetValue(e,out a))actual.Add(e,a=new List<int>());a.Add(i);}
                if(actual.Values.Any(a=>a.Count>2))return false;
                var missing=actual.FirstOrDefault(e=>e.Value.Count==1&&!expected.Contains(e.Key));
                if(missing.Value==null)return expected.All(e=>actual.ContainsKey(e)&&actual[e].Count==1);
                int first=(int)(missing.Key>>32),last=(int)missing.Key;List<int> chain=null;
                foreach(var loop in loops)
                {
                    int begin=loop.IndexOf(first);if(begin<0)continue;
                    foreach(int step in new[]{1,-1})
                    {
                        var path=new List<int>{first};for(int k=1;k<loop.Count;k++){int v=loop[(begin+step*k+loop.Count)%loop.Count];path.Add(v);if(v==last)break;}
                        if(path[path.Count-1]!=last||path.Count<3)continue;
                        var a=D(vertices[first]);var d=Sub(D(vertices[last]),a);double dd=Dot(d,d);if(dd<1e-20)continue;
                        if(path.All(v=>{var q=Sub(D(vertices[v]),a);double t=Dot(q,d)/dd;return t>=-1e-9&&t<=1+1e-9&&Dot(Cross(q,d),Cross(q,d))/dd<1e-12;})){chain=path;break;}
                    }
                    if(chain!=null)break;
                }
                if(chain==null)return false;int index=missing.Value[0];int opposite=triangles[index].First(v=>v!=first&&v!=last);triangles.RemoveAt(index);
                for(int i=0;i<chain.Count-1;i++){var t=new[]{chain[i],chain[i+1],opposite};if(Dot(Normal(vertices,t),normal)<0){int s=t[1];t[1]=t[2];t[2]=s;}triangles.Add(t);}
            }
            return false;
        }
    }
}
