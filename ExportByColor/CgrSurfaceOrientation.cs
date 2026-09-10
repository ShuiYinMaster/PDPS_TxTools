using System;
using System.Collections.Generic;

namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        // Int64's default hash XORs its halves. Packed adjacent vertex IDs then collide
        // heavily (a^b), making large mesh dictionaries approach quadratic time.
        private sealed class EdgeKeyComparer : IEqualityComparer<long>
        {
            public static readonly EdgeKeyComparer Instance=new EdgeKeyComparer();
            public bool Equals(long a,long b) { return a==b; }
            public int GetHashCode(long value)
            {
                unchecked {
                    ulong z=(ulong)value+0x9e3779b97f4a7c15UL;
                    z=(z^(z>>30))*0xbf58476d1ce4e5b9UL;
                    z=(z^(z>>27))*0x94d049bb133111ebUL;
                    return (int)(z^(z>>31));
                }
            }
        }
        private sealed class SurfaceEdge { public List<int> Faces=new List<int>();public List<bool> Directions=new List<bool>(); }
        // PS triangle primitives do not guarantee consistent winding. Solve adjacency first;
        // choose outward winding by signed volume only for closed connected components.
        private static void PrepareSurfaces(ref List<float[]> vertices,ref List<Face> faces,Action<string> progress)
        {
            vertices=new List<float[]>(vertices);faces=new List<Face>(faces);
            var positions=new Dictionary<Tuple<float,float,float>,int>();var canonical=new int[vertices.Count];
            for(int i=0;i<vertices.Count;i++)
            {
                var v=vertices[i];if(v==null||v.Length!=3) throw new ArgumentException("Expected XYZ");
                foreach(float x in v) Finite(x);
                var key=Tuple.Create(v[0],v[1],v[2]);int id;if(!positions.TryGetValue(key,out id)) {id=positions.Count;positions.Add(key,id);}canonical[i]=id;
            }
            var edges=new Dictionary<long,SurfaceEdge>(EdgeKeyComparer.Instance);var faceEdges=new long[faces.Count][];
            var surfaceVertices=new Dictionary<Tuple<int,int>,int>();
            bool preserveSurfaces=Environment.GetEnvironmentVariable("TXTOOLS_CGR_BACKEND")!="legacy";
            for(int i=0;i<faces.Count;i++)
            {
                var ids=faces[i].Idx;if(ids==null||ids.Length!=3) throw new ArgumentException("Expected triangle");
                foreach(int j in ids) if(j<0||j>=vertices.Count) throw new ArgumentException("Invalid index");
                faceEdges[i]=new long[3];
                for(int k=0;k<3;k++)
                {
                    int a=canonical[ids[k]],b=canonical[ids[(k+1)%3]];
                    if(preserveSurfaces)
                    {
                        var ka=Tuple.Create(faces[i].Surface,a);var kb=Tuple.Create(faces[i].Surface,b);
                        if(!surfaceVertices.TryGetValue(ka,out a)){a=surfaceVertices.Count;surfaceVertices.Add(ka,a);}
                        if(!surfaceVertices.TryGetValue(kb,out b)){b=surfaceVertices.Count;surfaceVertices.Add(kb,b);}
                    }
                    if(a==b) throw new ArgumentException("Degenerate triangle");
                    long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);faceEdges[i][k]=key;
                    SurfaceEdge e;if(!edges.TryGetValue(key,out e)) {e=new SurfaceEdge();edges.Add(key,e);}e.Faces.Add(i);e.Directions.Add(a<b);
                }
            }
            int nonmanifold=0;foreach(var e in edges.Values) if(e.Faces.Count>2) nonmanifold++;
            if(progress!=null) progress("[CGR] 拓扑检查："+nonmanifold+" 条多邻面边；保留输入三角面");
            var state=new int[faces.Count];var backFaces=new List<int>();
            var conflicts=new HashSet<long>();var forcedBackFaces=new HashSet<int>();
            for(int seed=0;seed<faces.Count;seed++)
            {
                if(state[seed]!=0) continue;
                var component=new List<int>();var queue=new Queue<int>();queue.Enqueue(seed);state[seed]=1;bool closed=true;bool conflicting=false;
                while(queue.Count>0)
                {
                    int f=queue.Dequeue();component.Add(f);
                    foreach(long key in faceEdges[f])
                    {
                        var e=edges[key];if(e.Faces.Count!=2) {closed=false;continue;}
                        int slot=e.Faces[0]==f?0:1,other=e.Faces[1-slot];
                        int required=e.Directions[0]==e.Directions[1]?-state[f]:state[f];
                        if(state[other]==0) {state[other]=required;queue.Enqueue(other);}
                        else if(state[other]!=required) {closed=false;conflicting=true;conflicts.Add(key);}
                    }
                }
                bool invert=false;
                if(closed)
                {
                    // Translate origin near the component for stable volume at plant coordinates.
                    var origin=vertices[faces[seed].Idx[0]];double volume=0;
                    foreach(int f in component)
                    {
                        var ids=faces[f].Idx;var a=vertices[ids[0]];var b=vertices[ids[1]];var c=vertices[ids[2]];
                        double ax=(double)a[0]-origin[0],ay=(double)a[1]-origin[1],az=(double)a[2]-origin[2];
                        double bx=(double)b[0]-origin[0],by=(double)b[1]-origin[1],bz=(double)b[2]-origin[2];
                        double cx=(double)c[0]-origin[0],cy=(double)c[1]-origin[1],cz=(double)c[2]-origin[2];
                        volume+=state[f]*(ax*(by*cz-bz*cy)+ay*(bz*cx-bx*cz)+az*(bx*cy-by*cx));
                    }
                    invert=volume<0;
                    if(volume==0) closed=false;
                }
                else
                {
                    int sum=0;foreach(int f in component) sum+=state[f];invert=sum<0;
                }
                foreach(int f in component)
                {
                    if((state[f]<0)^invert) {var face=faces[f];face.Idx=new[]{face.Idx[0],face.Idx[2],face.Idx[1]};faces[f]=face;}
                    if(!closed) backFaces.Add(f);
                    if(conflicting) forcedBackFaces.Add(f);
                }
            }
            // Until the native two-sided flag is proven, open sheets have explicit reverse-side
            // triangles with independent vertex identity. Closed solids are NOT duplicated.
            var reverseVertices=new Dictionary<int,int>();
            bool duplicateBackfaces=Environment.GetEnvironmentVariable("TXTOOLS_CGR_DUPLICATE_BACKFACES")!="0";
            if(progress!=null && conflicts.Count>0) progress("[CGR] 绕序冲突："+conflicts.Count+" 条约束边；受影响 "+forcedBackFaces.Count+" 面以正反双面保留");
            foreach(int i in backFaces)
            {
                if(!duplicateBackfaces&&!forcedBackFaces.Contains(i)) continue;
                var face=faces[i];var ids=new int[3];int[] order={0,2,1};
                for(int k=0;k<3;k++)
                {
                    int old=face.Idx[order[k]],id;if(!reverseVertices.TryGetValue(old,out id)) {id=vertices.Count;vertices.Add(vertices[old]);reverseVertices.Add(old,id);}ids[k]=id;
                }
                face.Idx=ids;faces.Add(face);
            }
        }
    }
}
