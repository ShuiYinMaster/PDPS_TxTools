using System;
using System.Collections.Generic;
using System.IO;

namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        private static byte[] H95(string s) { var b=new byte[s.Length/2];for(int i=0;i<b.Length;i++)b[i]=Convert.ToByte(s.Substring(i*2,2),16);return b; }
        private static readonly byte[] Root95=H95("6200200504fdff00000001006864124500000000fcff4fc19065c9410000044400000000ff03010000");
        private static readonly byte[] Branch95=H95("6200200504fdff00000001006864124500000000fcff4fc19065c9410000044400000000ffff000000");
        private static readonly byte[] Leaf95=H95("95ff6213000000200404ffffffffff01004a168842000000000800c8c2573d45c3562b084400000000");
        private static readonly byte[] Pick95=H95("ff3d000000db0f493fa5d468530000000000ffff0900ecc261a759c3d31003440800a4c24dd330c3d8450d440800c8c2573d45c3562b08444a16884201");
        private static int Find95(int[] p,int x) {while(p[x]!=x){p[x]=p[p[x]];x=p[x];}return x;}
        private static void Join95(int[] p,int a,int b){a=Find95(p,a);b=Find95(p,b);if(a!=b)p[b]=a;}
        private static double Dot95(double[] a,double[] b){return a[0]*b[0]+a[1]*b[1]+a[2]*b[2];}
        internal static bool CurvatureContinues95(double[] previous,double[] current,double[] next)
        {
            // Equal unsigned angles elsewhere on a planar patch do not prove
            // a curved continuation (e.g. the opposite chamfer of a plate).
            // Require consecutive normal rotations about the same signed axis.
            var a=new[]{previous[1]*current[2]-previous[2]*current[1],previous[2]*current[0]-previous[0]*current[2],previous[0]*current[1]-previous[1]*current[0]};
            var b=new[]{current[1]*next[2]-current[2]*next[1],current[2]*next[0]-current[0]*next[2],current[0]*next[1]-current[1]*next[0]};
            double length=Math.Sqrt(Dot95(a,a)*Dot95(b,b));
            return length>1e-20 && Dot95(a,b)/length>=Math.Cos(Math.PI/180*15);
        }
        // Angles are radians. No geometry or normals are changed by classification.
        internal static bool SmoothAdaptive95(double angle,double continuationA,double continuationB)
        {
            // Fine tessellations on cylinders commonly have 5–15 degree
            // normal changes. Always join this low-curvature range; otherwise
            // a cylindrical surface becomes visibly banded.
            if(angle<=Math.PI/180*18)return true;
            // Repeated equal-angle turns also describe a chamfered prism.
            // Neither their magnitude nor their signed rotation proves a
            // smooth CAD surface. Preserve these ambiguous edges instead of
            // bending planar faces. Coarse curves need source normal evidence.
            return false;
        }
        private static double[] Normal95(float[] a,float[] b,float[] c)
        {
            double x=(double)b[0]-a[0],y=(double)b[1]-a[1],z=(double)b[2]-a[2];
            double u=(double)c[0]-a[0],v=(double)c[1]-a[1],w=(double)c[2]-a[2];
            var n=new[]{y*w-z*v,z*u-x*w,x*v-y*u};double length=Math.Sqrt(Dot95(n,n));
            if(length==0)throw new InvalidDataException("Zero area in compact backend");
            for(int k=0;k<3;k++)n[k]/=length;return n;
        }
        private static byte[] Header95(byte[] template,double[] bounds)
        {
            var h=(byte[])template.Clone();Float(h,12,bounds[9]+Math.Max(.2,bounds[9]*1e-6));
            for(int k=0;k<3;k++)Float(h,20+4*k,bounds[k]);return h;
        }
        private static void BuildCompact95(List<float[]> vertices,List<Face> faces,string path,Action<string> progress)
        {
            var groups=new Dictionary<Tuple<int,int>,List<Face>>();var order=new List<List<Face>>();
            foreach(var f in faces)
            {
                var key=Tuple.Create(f.Surface,f.R|(f.G<<8)|(f.B<<16));List<Face> group;
                if(!groups.TryGetValue(key,out group)){group=new List<Face>();groups.Add(key,group);order.Add(group);}group.Add(f);
            }
            int[] all=new int[vertices.Count];for(int i=0;i<all.Length;i++)all[i]=i;var global=Bounds(vertices,all);
            var leaves=new List<byte[]>();long changed=0;int vertexCount=0;
            using(var picks=new MemoryStream())
            {
                foreach(var group in order)
                    CompactGroup95(vertices,group,leaves,picks,ref changed,ref vertexCount);
                using(var scene=new MemoryStream())using(var w=new BinaryWriter(scene))
                {
                    w.Write(Header95(Root95,global));Compact(w,(uint)leaves.Count);
                    foreach(var leaf in leaves)
                    {
                        w.Write((byte)0);
                        // The reference branch shape is required by CATIA for this representation.
                        for(int level=0;level<4;level++){w.Write(Header95(level==0?Branch95:Root95,global));Compact(w,1);w.Write((byte)0);}
                        w.Write(leaf);
                    }
                    w.Write((byte)0);
                    WriteContainer(scene.ToArray(),picks.ToArray(),path,vertices,true);
                }
            }
            if(progress!=null)progress("[CGR95] "+groups.Count+" 个源几何/颜色组，"+leaves.Count+" 块，"+vertexCount+" 顶点；平滑角点 "+changed+"；文件 "+new FileInfo(path).Length+" 字节");
        }
        private static void CompactGroup95(List<float[]> source,List<Face> faces,List<byte[]> leaves,MemoryStream picks,ref long changed,ref int vertexCount)
        {
            int count=faces.Count;var positions=new List<float[]>();var map=new Dictionary<Tuple<float,float,float>,int>();
            var ids=new int[count][];var normals=new double[count][];var angles=new double[count][];
            var areas=new double[count];
            var edges=new Dictionary<long,List<int>>(EdgeKeyComparer.Instance);var parent=new int[checked(count*3)];
            for(int i=0;i<parent.Length;i++)parent[i]=i;
            for(int i=0;i<count;i++)
            {
                ids[i]=new int[3];angles[i]=new double[3];var f=faces[i];
                for(int k=0;k<3;k++)
                {
                    var v=source[f.Idx[k]];var key=Tuple.Create(v[0],v[1],v[2]);int id;
                    if(!map.TryGetValue(key,out id)){id=positions.Count;positions.Add(v);map.Add(key,id);}ids[i][k]=id;
                }
                normals[i]=Normal95(positions[ids[i][0]],positions[ids[i][1]],positions[ids[i][2]]);
                var p0=positions[ids[i][0]];var p1=positions[ids[i][1]];var p2=positions[ids[i][2]];
                double ux=(double)p1[0]-p0[0],uy=(double)p1[1]-p0[1],uz=(double)p1[2]-p0[2];
                double vx=(double)p2[0]-p0[0],vy=(double)p2[1]-p0[1],vz=(double)p2[2]-p0[2];
                double cx=uy*vz-uz*vy,cy=uz*vx-ux*vz,cz=ux*vy-uy*vx;
                areas[i]=Math.Sqrt(cx*cx+cy*cy+cz*cz);
                for(int k=0;k<3;k++)
                {
                    var a=positions[ids[i][k]];var b=positions[ids[i][(k+1)%3]];var c=positions[ids[i][(k+2)%3]];
                    double ab=0,ac=0,dot=0;
                    for(int j=0;j<3;j++){double u=(double)b[j]-a[j],v=(double)c[j]-a[j];ab+=u*u;ac+=v*v;dot+=u*v;}
                    angles[i][k]=Math.Acos(Math.Max(-1,Math.Min(1,dot/Math.Sqrt(ab*ac))));
                    long key=EdgeKey(ids[i][k],ids[i][(k+1)%3]);List<int> edge;
                    if(!edges.TryGetValue(key,out edge)){edge=new List<int>();edges.Add(key,edge);}edge.Add(i*3+k);
                }
            }
            // Collapse coplanar triangles before estimating curvature, so an
            // arbitrary triangulation diagonal does not count as a continuation.
            var patches=new int[count];for(int f=0;f<count;f++)patches[f]=f;
            foreach(var edge in edges.Values)for(int a=0;a<edge.Count;a++)for(int b=a+1;b<edge.Count;b++)
            {
                int fa=edge[a]/3,fb=edge[b]/3;
                if(Dot95(normals[fa],normals[fb])>1-1e-8)Join95(patches,fa,fb);
            }
            var neighbors=new Dictionary<int,Dictionary<int,double>>();
            foreach(var edge in edges.Values)
            {
                if(edge.Count!=2&&edge.Count!=4)continue;
                for(int a=0;a<edge.Count;a++)for(int b=a+1;b<edge.Count;b++)
                {
                    int fa=edge[a]/3,fb=edge[b]/3,pa=Find95(patches,fa),pb=Find95(patches,fb);
                    if(pa==pb)continue;
                    double angle=Math.Acos(Math.Max(-1,Math.Min(1,Dot95(normals[fa],normals[fb]))));
                    if(angle>=Math.PI/3)continue;
                    Dictionary<int,double> na,nb;
                    if(!neighbors.TryGetValue(pa,out na)){na=new Dictionary<int,double>();neighbors.Add(pa,na);}
                    if(!neighbors.TryGetValue(pb,out nb)){nb=new Dictionary<int,double>();neighbors.Add(pb,nb);}
                    na[pb]=angle;nb[pa]=angle;
                }
            }
            foreach(var edge in edges.Values)
            {
                if(edge.Count!=2&&edge.Count!=4)continue;
                var matches=new int[edge.Count];
                for(int i=0;i<edge.Count;i++)
                {
                    matches[i]=-1;int a=edge[i],af=a/3,ak=a%3;bool direction=ids[af][ak]<ids[af][(ak+1)%3];
                    for(int j=0;j<edge.Count;j++)
                    {
                        int b=edge[j],bf=b/3,bk=b%3;if(af==bf||direction==(ids[bf][bk]<ids[bf][(bk+1)%3]))continue;
                        double angle=Math.Acos(Math.Max(-1,Math.Min(1,Dot95(normals[af],normals[bf]))));
                        int pa=Find95(patches,af),pb=Find95(patches,bf);
                        double ca=double.NaN,cb=double.NaN;Dictionary<int,double> adj;
                        if(neighbors.TryGetValue(pa,out adj))foreach(var pair in adj)
                            if(pair.Key!=pb&&CurvatureContinues95(normals[pair.Key],normals[pa],normals[pb])&&(double.IsNaN(ca)||Math.Abs(pair.Value-angle)<Math.Abs(ca-angle)))ca=pair.Value;
                        if(neighbors.TryGetValue(pb,out adj))foreach(var pair in adj)
                            if(pair.Key!=pa&&CurvatureContinues95(normals[pa],normals[pb],normals[pair.Key])&&(double.IsNaN(cb)||Math.Abs(pair.Value-angle)<Math.Abs(cb-angle)))cb=pair.Value;
                        if(!SmoothAdaptive95(angle,ca,cb))continue;
                        if(matches[i]!=-1){matches[i]=-2;break;}matches[i]=j;
                    }
                }
                for(int i=0;i<edge.Count;i++)
                {
                    int j=matches[i];if(j<=i||matches[j]!=i)continue;int a=edge[i],b=edge[j];
                    Join95(parent,a,b/3*3+(b%3+1)%3);Join95(parent,a/3*3+(a%3+1)%3,b);
                }
            }
            var sums=new Dictionary<int,double[]>();
            for(int i=0;i<count;i++)for(int k=0;k<3;k++)
            {
                int root=Find95(parent,3*i+k);double[] sum;if(!sums.TryGetValue(root,out sum)){sum=new double[3];sums.Add(root,sum);}
                for(int j=0;j<3;j++)sum[j]+=normals[i][j]*angles[i][k]*areas[i];
            }
            foreach(var sum in sums.Values){double length=Math.Sqrt(Dot95(sum,sum));if(length>0)for(int j=0;j<3;j++)sum[j]/=length;}
            var vs=new List<float[]>();var ns=new List<float[]>();var triangles=new List<int[]>();var local=new Dictionary<Tuple<int,int>,int>();
            for(int i=0;i<count;i++)
            {
                if(vs.Count+3>MaxVertsPerChunk){WriteLeaf95(vs,ns,triangles,faces[0],leaves,picks);vertexCount+=vs.Count;vs.Clear();ns.Clear();triangles.Clear();local.Clear();}
                var tri=new int[3];
                for(int k=0;k<3;k++)
                {
                    int root=Find95(parent,3*i+k);var key=Tuple.Create(ids[i][k],root);int index;var normal=sums[root];
                    if(Dot95(normal,normals[i])<.999999)changed++;
                    if(!local.TryGetValue(key,out index))
                    {
                        index=vs.Count;local.Add(key,index);vs.Add(positions[ids[i][k]]);
                        ns.Add(new[]{Finite(normal[0]),Finite(normal[1]),Finite(normal[2])});
                    }
                    tri[k]=index;
                }
                triangles.Add(tri);
            }
            if(triangles.Count>0){WriteLeaf95(vs,ns,triangles,faces[0],leaves,picks);vertexCount+=vs.Count;}
        }
        private static void WriteLeaf95(List<float[]> vs,List<float[]> ns,List<int[]> faces,Face color,List<byte[]> leaves,MemoryStream picks)
        {
            var order=new List<int>();for(int i=0;i<vs.Count;i++)order.Add(i);
            order.Sort((a,b)=>{for(int k=0;k<3;k++){int c=vs[a][k].CompareTo(vs[b][k]);if(c!=0)return c;}return a.CompareTo(b);});
            var remap=new int[vs.Count];for(int i=0;i<order.Count;i++)remap[order[i]]=i;
            byte[] payload;
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms))
            {
                w.Write(new byte[]{255,255,0,0,1});Compact(w,(uint)vs.Count);Compact(w,(uint)vs.Count);w.Write(0);
                var bitmap=new byte[(vs.Count+3)/4];var values=new List<float>();float[] previous=null;
                for(int i=0;i<order.Count;i++)
                {
                    var v=vs[order[i]];int code;
                    if(previous!=null&&v[0]==previous[0]&&v[1]==previous[1]&&v[2]==previous[2])code=1;
                    else if(previous!=null&&v[0]==previous[0]&&v[1]==previous[1]){code=2;values.Add(v[2]);}
                    else if(previous!=null&&v[0]==previous[0]){code=3;values.Add(v[1]);values.Add(v[2]);}
                    else{code=0;values.AddRange(v);}bitmap[i/4]|=(byte)(code<<(2*(i%4)));previous=v;
                }
                w.Write(bitmap);Compact(w,(uint)values.Count);foreach(float x in values)w.Write(x);
                Compact(w,(uint)checked(vs.Count*2));var normalMap=new byte[(vs.Count+1)/2];
                for(int i=0;i<order.Count;i++)
                {
                    var n=ns[order[i]];
                    // Store the two minor unit components. A fixed Z omission
                    // amplifies quantization near horizontal normals and can
                    // even make the reconstructed squared Z negative.
                    int axis=0;
                    for(int k=1;k<3;k++)if(Math.Abs(n[k])>Math.Abs(n[axis]))axis=k;
                    double length=Math.Sqrt((double)n[0]*n[0]+(double)n[1]*n[1]+(double)n[2]*n[2]);
                    if(length==0||double.IsNaN(length)||double.IsInfinity(length))
                        throw new InvalidDataException("Invalid compact normal");
                    for(int k=0;k<3;k++)if(k!=axis)
                        w.Write((short)Math.Round(Math.Max(-1,Math.Min(1,n[k]/length))*32767));
                    normalMap[i/2]|=(byte)((axis*2+(n[axis]<0?1:0))<<(4*(i%2)));
                }
                w.Write(normalMap);w.Write(.2f);w.Write(new byte[]{0,1,2,10});Compact(w,1);w.Write(new byte[]{1,65});
                Compact(w,(uint)faces.Count);Compact(w,(uint)checked(faces.Count*3));
                foreach(var tri in faces)foreach(int j in tri)Index(w,remap[j],vs.Count>255);
                w.Write(new byte[]{32,4,4,255,255,color.B,color.G,color.R});payload=ms.ToArray();
            }
            var bounds=Bounds(vs,order.ToArray());var header=(byte[])Leaf95.Clone();
            Float(header,17,bounds[9]+Math.Max(.2,bounds[9]*1e-6));for(int k=0;k<3;k++)Float(header,25+4*k,bounds[k]);LE(header,2,checked(header.Length+payload.Length-1));
            using(var ms=new MemoryStream())using(var w=new BinaryWriter(ms)){w.Write(header);w.Write(payload);Compact(w,checked((uint)picks.Position));leaves.Add(ms.ToArray());}
            var pick=(byte[])Pick95.Clone();
            for(int k=0;k<3;k++){Float(pick,20+4*k,bounds[k]-bounds[k+3]);Float(pick,32+4*k,bounds[k]+bounds[k+3]);Float(pick,44+4*k,bounds[k]);}
            Float(pick,56,bounds[9]);picks.Write(pick,0,pick.Length);
        }
    }
}
