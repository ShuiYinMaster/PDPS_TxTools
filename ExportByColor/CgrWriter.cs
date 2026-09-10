using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace TxTools.ExportByColor
{
    // Experimental R7-R12 standalone encoding. Opaque metadata/ID allocation remain experimental.
    public static partial class CgrWriter
    {
        public const string Version="CGR-20260909-R33-compact-smooth";
        public const int MaxVertsPerChunk=40960;
        private const int MaxFacesPerChunk=20000;
        public struct Face { public float Nx,Ny,Nz; public int[] Idx; public byte R,G,B; public int Surface; }
        private sealed class Entry
        {
            public int Type,Offset,Count; public byte[] Data;
            public Entry(int t,int o,int n,byte[] d) { Type=t;Offset=o;Count=n;Data=d; }
        }
        private sealed class Edge { public int A,B; public List<uint> Faces=new List<uint>(); }
        private sealed class Primitive { public int[] Indices; public bool Strip; public bool List; public Face Source; public int FaceIndex; public double[] Bounds; }
        private static void Compact(BinaryWriter w,uint n) { if(n<255) w.Write((byte)n); else { w.Write((byte)255);w.Write(n); } }
        private static void CompactExtended(BinaryWriter w,uint n) { w.Write((byte)255);w.Write(n); }
        private static void Index(BinaryWriter w,int i,bool wide) { if(wide) w.Write((byte)(i>>8)); w.Write((byte)i); }
        private static void BE(byte[] b,int p,int v) { for(int i=0;i<4;i++) b[p+i]=(byte)((uint)v>>(24-8*i)); }
        private static void LE(byte[] b,int p,int v) { Array.Copy(BitConverter.GetBytes(v),0,b,p,4); }
        private static float Finite(double x)
        {
            float f=(float)x;
            if(float.IsNaN(f)||float.IsInfinity(f)) throw new ArgumentException("Non-finite coordinate or bounds");
            return f;
        }
        private static void Float(byte[] b,int p,double v) { Array.Copy(BitConverter.GetBytes(Finite(v)),0,b,p,4); }
        private static double[] Bounds(List<float[]> vertices,int[] indices)
        {
            double[] lo={double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity};
            double[] hi={double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity};
            foreach(int i in indices) for(int k=0;k<3;k++) { lo[k]=Math.Min(lo[k],vertices[i][k]);hi[k]=Math.Max(hi[k],vertices[i][k]); }
            double[] b=new double[10];double r2=0;
            for(int k=0;k<3;k++) { b[k]=b[k+6]=(lo[k]+hi[k])/2;b[k+3]=(hi[k]-lo[k])/2;r2+=b[k+3]*b[k+3]; }
            b[9]=Math.Sqrt(r2);return b;
        }
        private static long EdgeKey(int a,int b) { int x=Math.Min(a,b),y=Math.Max(a,b); return ((long)x<<32)|(uint)y; }
        private static List<Face> ReorderForStrips(List<Face> input)
        {
            var result=new List<Face>(input.Count);var byColor=new Dictionary<int,List<int>>();
            for(int i=0;i<input.Count;i++) { int c=input[i].R|(input[i].G<<8)|(input[i].B<<16); if(!byColor.TryGetValue(c,out var list)){list=new List<int>();byColor.Add(c,list);} list.Add(i); }
            foreach(var group in byColor.Values)
            {
                var edgeMap=new Dictionary<long,List<int>>();
                foreach(int fi in group) { long key=((long)input[fi].Idx[0]<<32)|(uint)input[fi].Idx[1]; if(!edgeMap.TryGetValue(key,out var l)){l=new List<int>();edgeMap.Add(key,l);} l.Add(fi); }
                var used=new HashSet<int>();
                foreach(int seed in group)
                {
                    if(used.Contains(seed)) continue;
                    var f=input[seed];var seq=new List<int>{f.Idx[0],f.Idx[1],f.Idx[2]};used.Add(seed);result.Add(f);
                    while(true)
                    {
                        int j=seq.Count;int a=seq[j-2],b=seq[j-1];long key=(j%2==1)?((long)a<<32)|(uint)b:((long)b<<32)|(uint)a;if(!edgeMap.TryGetValue(key,out var candidates)) break;
                        int found=-1;
                        foreach(int ci in candidates) if(!used.Contains(ci)) { var n=input[ci]; bool ok=(j%2==1&&n.Idx[0]==a&&n.Idx[1]==b)||(j%2==0&&n.Idx[0]==b&&n.Idx[1]==a); if(ok){found=ci;break;} }
                        if(found<0) break; var nf=input[found];seq.Add(nf.Idx[2]);used.Add(found);result.Add(nf);
                    }
                }
            }
            return result;
        }
        // Multiple mesh blocks remain inside ONE CGR container.
        public static void BuildFile(List<float[]> vertices,List<Face> faces,string outPath,int maxFacesPerBlock=MaxFacesPerChunk,Action<string> progress=null,bool writeEdges=false)
        {
            if(vertices==null||faces==null||faces.Count==0||maxFacesPerBlock<1) throw new ArgumentException("Empty mesh or invalid block size");
            var watch=Stopwatch.StartNew();int inputFaces=faces.Count;
            if(progress!=null) progress("[CGR] 编码器 "+Version+"；DLL="+typeof(CgrWriter).Assembly.Location+"；批量="+(Environment.GetEnvironmentVariable("TXTOOLS_CGR_BATCH_LISTS")!="0")+"；边="+writeEdges+"；背面="+(Environment.GetEnvironmentVariable("TXTOOLS_CGR_DUPLICATE_BACKFACES")!="0"));
            faces=FilterZeroArea(vertices,faces,progress);
            PrepareSurfaces(ref vertices,ref faces,progress);
            if(!writeEdges && Environment.GetEnvironmentVariable("TXTOOLS_CGR_BACKEND")!="legacy")
            {
                BuildCompact95(vertices,faces,outPath,progress);return;
            }
            if(writeEdges && progress!=null) progress("[CGR] 边线使用兼容编码路径，包含三角网格边；文件会较大，当前路径不输出平滑法线");
            if(Environment.GetEnvironmentVariable("TXTOOLS_CGR_LOCALIZE")=="1")
            {
                double[] lo={double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity},hi={double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity};
                foreach(var v in vertices) for(int k=0;k<3;k++){lo[k]=Math.Min(lo[k],v[k]);hi[k]=Math.Max(hi[k],v[k]);}
                float[] shift={(float)((lo[0]+hi[0])/2),(float)((lo[1]+hi[1])/2),(float)((lo[2]+hi[2])/2)};
                foreach(var v in vertices) for(int k=0;k<3;k++) v[k]-=shift[k];
                if(progress!=null) progress("[CGR] 局部坐标：平移 " + shift[0]+","+shift[1]+","+shift[2]);
            }
            // Keep source face order until primitive/type13 IDs are fully
            // remapped; stripification remains an experimental helper.
            if(progress!=null) progress("[CGR] 绕序/背面准备："+watch.ElapsedMilliseconds+" ms，输入 "+inputFaces+" 面，编码 "+faces.Count+" 面");
            var scenes=new List<byte[]>();var packets=new List<byte[]>();int i=0;uint nextId=FirstId;
            while(i<faces.Count)
            {
                var verts=new List<float[]>();var local=new List<Face>();var map=new Dictionary<int,int>();
                while(i<faces.Count&&local.Count<maxFacesPerBlock)
                {
                    Face f=faces[i];
                    if(f.Idx==null||f.Idx.Length!=3) throw new ArgumentException("Only triangles are supported");
                    int add=0;foreach(int j in f.Idx) if(!map.ContainsKey(j)) add++;
                    if(map.Count+add>MaxVertsPerChunk&&local.Count>0) break;
                    int[] ids=new int[3];
                    for(int k=0;k<3;k++)
                    {
                        int j=f.Idx[k];if(j<0||j>=vertices.Count) throw new ArgumentException("Invalid triangle index");
                        if(!map.ContainsKey(j)) { map.Add(j,verts.Count);verts.Add(vertices[j]); } ids[k]=map[j];
                    }
                    f.Idx=ids;local.Add(f);i++;
                }
                byte[] scene,packet;BuildStreams(verts,local,nextId,out scene,out packet,out nextId,writeEdges);
                scenes.Add(scene);packets.Add(packet);
            }
            byte[] combined,topology;
            using(var ms=new MemoryStream()) using(var w=new BinaryWriter(ms))
            {
                w.Write(ScenePrefix,0,41);Compact(w,(uint)scenes.Count);
                foreach(var scene in scenes) { w.Write((byte)0);w.Write(scene,43,scene.Length-43); }
                combined=ms.ToArray();
            }
            // Global root bounds cover all devices, while each child retains its local bounds.
            SetRootSphere(combined,vertices);
            using(var ms=new MemoryStream()) { foreach(var packet in packets) ms.Write(packet,0,packet.Length);topology=ms.ToArray(); }
            WriteContainer(combined,topology,outPath,vertices);
            if(progress!=null) progress("[CGR] 编码完成："+scenes.Count+" 个内部块，1 个文件，"+new FileInfo(outPath).Length+" 字节，总耗时 "+watch.ElapsedMilliseconds+" ms");
        }
        public static List<string> BuildChunked(List<float[]> vertices,List<Face> faces,string outDir,string baseName)
        {
            Directory.CreateDirectory(outDir);string path=Path.Combine(outDir,baseName+".cgr");
            BuildFile(vertices,faces,path);return new List<string>{path};
        }
        private static void SetRootSphere(byte[] scene,List<float[]> vertices)
        {
            int[] all=new int[vertices.Count];for(int i=0;i<all.Length;i++) all[i]=i;
            var b=Bounds(vertices,all);double r2=0;
            for(int k=0;k<3;k++) { b[k]=Finite(b[k]);double h=0;foreach(var v in vertices) h=Math.Max(h,Math.Abs(v[k]-b[k]));r2+=h*h;Float(scene,20+4*k,b[k]); }
            double radius=Math.Sqrt(r2);Float(scene,12,radius+Math.Max(.2,radius*1e-6));
        }
        public static void Build(List<float[]> vertices,List<Face> faces,string outPath)
        {
            byte[] scene,packet;uint nextId;
            faces=FilterZeroArea(vertices,faces,null);
            BuildStreams(vertices,faces,FirstId,out scene,out packet,out nextId);WriteContainer(scene,packet,outPath,vertices);
        }
        private static List<Face> FilterZeroArea(List<float[]> vertices,List<Face> faces,Action<string> progress)
        {
            var kept=new List<Face>(faces.Count);int removed=0;
            foreach(var f in faces)
            {
                if(f.Idx==null||f.Idx.Length!=3) throw new ArgumentException("Expected triangle");
                foreach(int j in f.Idx)
                {
                    if(j<0||j>=vertices.Count) throw new ArgumentException("Invalid triangle index");
                    var point=vertices[j];if(point==null||point.Length!=3) throw new ArgumentException("Expected XYZ");
                    foreach(float value in point) Finite(value);
                }
                var a=vertices[f.Idx[0]];var b=vertices[f.Idx[1]];var c=vertices[f.Idx[2]];
                double ux=(double)b[0]-a[0],uy=(double)b[1]-a[1],uz=(double)b[2]-a[2];
                double vx=(double)c[0]-a[0],vy=(double)c[1]-a[1],vz=(double)c[2]-a[2];
                // Exact zero only: retain arbitrarily small nonzero triangles.
                if(uy*vz-uz*vy==0 && uz*vx-ux*vz==0 && ux*vy-uy*vx==0){removed++;continue;}
                kept.Add(f);
            }
            if(progress!=null) progress("[CGR] 零面积检查：输入 "+faces.Count+"，保留 "+kept.Count+"，排除 "+removed+" 个重合/共线退化面（不设面积阈值）");
            if(kept.Count==0) throw new ArgumentException("No nonzero-area triangles remain");
            return kept;
        }
        private static void BuildStreams(List<float[]> vertices,List<Face> faces,uint firstId,out byte[] scene,out byte[] packet,out uint nextId,bool writeEdges=false)
        {
            if(vertices==null||vertices.Count<3||vertices.Count>MaxVertsPerChunk||faces==null||faces.Count==0)
                throw new ArgumentException("Expected 3..65536 vertices and nonempty triangles");
            foreach(var v in vertices) { if(v==null||v.Length!=3) throw new ArgumentException("Expected XYZ");foreach(float x in v) Finite(x); }
            // Sort vertex references only: improve native prefix reuse without
            // welding identities, changing winding, colors, or world positions.
            var order=new List<int>();for(int vi=0;vi<vertices.Count;vi++) order.Add(vi);
            order.Sort((a,b)=>{for(int k=0;k<3;k++){int c=vertices[a][k].CompareTo(vertices[b][k]);if(c!=0)return c;}return a.CompareTo(b);});
            var sorted=new List<float[]>(vertices.Count);var remap=new int[vertices.Count];
            foreach(int old in order){remap[old]=sorted.Count;sorted.Add(vertices[old]);}
            var remapped=new List<Face>(faces.Count);
            foreach(var source in faces){var f=source;f.Idx=new[]{remap[f.Idx[0]],remap[f.Idx[1]],remap[f.Idx[2]]};remapped.Add(f);}
            vertices=sorted;faces=remapped;
            var normals=new List<float[]>();var bounds=new List<double[]>();
            var edges=new List<Edge>();var edgeMap=new Dictionary<long,Edge>(EdgeKeyComparer.Instance);
            for(int fi=0;fi<faces.Count;fi++)
            {
                int[] ids=faces[fi].Idx;
                if(ids==null||ids.Length!=3) throw new ArgumentException("Expected triangle");
                foreach(int j in ids) if(j<0||j>=vertices.Count) throw new ArgumentException("Invalid index");
                var a=vertices[ids[0]];var b=vertices[ids[1]];var c=vertices[ids[2]];
                double ux=(double)b[0]-a[0],uy=(double)b[1]-a[1],uz=(double)b[2]-a[2];
                double vx=(double)c[0]-a[0],vy=(double)c[1]-a[1],vz=(double)c[2]-a[2];
                double nx=uy*vz-uz*vy,ny=uz*vx-ux*vz,nz=ux*vy-uy*vx;
                double len=Math.Sqrt(nx*nx+ny*ny+nz*nz);
                if(len==0) throw new ArgumentException("Degenerate triangle at index "+fi);
                normals.Add(new[]{Finite(nx/len),Finite(ny/len),Finite(nz/len)});bounds.Add(Bounds(vertices,ids));
                if(writeEdges)
                    for(int k=0;k<3;k++)
                    {
                        int x=Math.Min(ids[k],ids[(k+1)%3]),y=Math.Max(ids[k],ids[(k+1)%3]);long key=((long)x<<32)|(uint)y;
                        Edge e;if(!edgeMap.TryGetValue(key,out e)) { e=new Edge{A=x,B=y};edgeMap.Add(key,e);edges.Add(e); }
                        e.Faces.Add((uint)fi);
                    }
            }
            // type13 has two adjacency slots. For ambiguous joins encode one boundary
            // edge per incident face; never invent a two-face pairing or delete geometry.
            var encodedEdges=new List<Edge>();
            foreach(var edge in edges)
            {
                if(edge.Faces.Count<=2) encodedEdges.Add(edge);
                else foreach(uint face in edge.Faces)
                { var boundary=new Edge{A=edge.A,B=edge.B};boundary.Faces.Add(face);encodedEdges.Add(boundary); }
            }
            edges=encodedEdges;
            if(!writeEdges) edges=new List<Edge>();
            bool wide=vertices.Count>255;byte[] payload;
            var primitives=new List<Primitive>();
            var faceToPrimitive=new int[faces.Count];
            // Greedily join consecutive same-color triangles into strips when
            // their winding matches the native alternating strip convention.
            for(int fi=0;fi<faces.Count;fi++)
            {
                Face f=faces[fi]; var seq=new List<int>{f.Idx[0],f.Idx[1],f.Idx[2]}; int used=1;
                while(Environment.GetEnvironmentVariable("TXTOOLS_CGR_STRIPS")=="1" && fi+used<faces.Count && faces[fi+used].R==f.R && faces[fi+used].G==f.G && faces[fi+used].B==f.B)
                {
                    Face n=faces[fi+used]; int j=seq.Count; int a=seq[j-2],b=seq[j-1];
                    bool ok=(j%2==1 && n.Idx[0]==b && n.Idx[1]==a) || (j%2==0 && n.Idx[0]==a && n.Idx[1]==b);
                    if(!ok) break; seq.Add(n.Idx[2]); used++;
                }
                int primitiveIndex=primitives.Count;
                if(used>=2) primitives.Add(new Primitive{Indices=seq.ToArray(),Strip=true,Source=f,FaceIndex=fi,Bounds=Bounds(vertices,seq.ToArray())});
                else
                {
                    int take=1;bool batchLists=Environment.GetEnvironmentVariable("TXTOOLS_CGR_BATCH_LISTS")!="0";
                    while(batchLists&&fi+take<faces.Count&&take<32&&faces[fi+take].R==f.R&&faces[fi+take].G==f.G&&faces[fi+take].B==f.B)
                    {
                        double dot=normals[fi][0]*normals[fi+take][0]+normals[fi][1]*normals[fi+take][1]+normals[fi][2]*normals[fi+take][2];
                        if(dot<0.9995) break; take++;
                    }
                    var packed=new int[take*3];for(int q=0;q<take;q++) Array.Copy(faces[fi+q].Idx,0,packed,q*3,3);
                    primitives.Add(new Primitive{Indices=packed,Strip=false,List=take>1,Source=f,FaceIndex=fi,Bounds=Bounds(vertices,packed)});used=take;
                }
                for(int k=0;k<used;k++) faceToPrimitive[fi+k]=primitiveIndex;
                fi+=used-1;
            }
            using(var ms=new MemoryStream()) using(var w=new BinaryWriter(ms))
            {
                w.Write(new byte[]{255,255,2,0,1});Compact(w,(uint)vertices.Count);Compact(w,0);w.Write(0);
                // Native CGR stores a 2-bit update mode per vertex.  The mode
                // reuses the unchanged prefix of the previous XYZ tuple:
                // 0=XYZ, 1=none, 2=Z, 3=YZ.  Keep float32 comparisons exact.
                var bitmap=new byte[(vertices.Count+3)/4];
                var values=new List<float>(vertices.Count*2);
                float[] previous=null;
                for(int vi=0;vi<vertices.Count;vi++)
                {
                    float[] v=vertices[vi];int code;
                    if(previous!=null && v[0]==previous[0] && v[1]==previous[1] && v[2]==previous[2]) code=1;
                    else if(previous!=null && v[0]==previous[0] && v[1]==previous[1]) { code=2;values.Add(v[2]); }
                    else if(previous!=null && v[0]==previous[0]) { code=3;values.Add(v[1]);values.Add(v[2]); }
                    else { code=0;values.Add(v[0]);values.Add(v[1]);values.Add(v[2]); }
                    bitmap[vi/4]|=(byte)(code << (2*(vi%4)));
                    previous=v;
                }
                w.Write(bitmap);Compact(w,(uint)values.Count);
                foreach(float x in values) w.Write(x);
                w.Write(.2f);w.Write(new byte[]{0,2,2,10});Compact(w,(uint)primitives.Count);
                foreach(var pr in primitives)
                {
                    if(pr.Strip) { w.Write(new byte[]{1,66,1});Compact(w,(uint)pr.Indices.Length);Compact(w,(uint)pr.Indices.Length);foreach(int j in pr.Indices) Index(w,j,wide); }
                    else if(pr.List) { w.Write(new byte[]{1,73});Compact(w,(uint)(pr.Indices.Length/3));CompactExtended(w,(uint)pr.Indices.Length);foreach(float x in normals[pr.FaceIndex]) w.Write(x);foreach(int j in pr.Indices) Index(w,j,wide); }
                    else { w.Write(new byte[]{1,73,1,3});foreach(float x in normals[pr.FaceIndex]) w.Write(x);foreach(int j in pr.Indices) Index(w,j,wide); }
                }
                foreach(var pr in primitives) { var f=pr.Source; w.Write(new byte[]{48,4,4,255,255,f.B,f.G,f.R}); }
                w.Write(new byte[]{1,1});Compact(w,(uint)edges.Count);
                foreach(var e in edges) { w.Write(new byte[]{2,2});Index(w,e.A,wide);Index(w,e.B,wide); }
                w.Write(new byte[]{16,36,4,255,255,0,0,0});payload=ms.ToArray();
            }
            scene=new byte[ScenePrefix.Length+payload.Length+SceneSuffix.Length];
            Array.Copy(ScenePrefix,scene,ScenePrefix.Length);Array.Copy(payload,0,scene,ScenePrefix.Length,payload.Length);
            Array.Copy(SceneSuffix,0,scene,ScenePrefix.Length+payload.Length,SceneSuffix.Length);
            LE(scene,45,scene.Length-45);LE(scene,191,payload.Length+40);
            int[] all=new int[vertices.Count];for(int i=0;i<all.Length;i++) all[i]=i;
            var sphere=Bounds(vertices,all);double sum=0;
            for(int k=0;k<3;k++) { sphere[k]=Finite(sphere[k]);double half=0;foreach(var v in vertices) half=Math.Max(half,Math.Abs(v[k]-sphere[k]));sum+=half*half; }
            double radius=Math.Sqrt(sum);radius=Finite(radius+Math.Max(.2,radius*1e-6));
            int[] radiusOffsets={12,60,107,154,206},centerOffsets={20,68,115,162,214};
            for(int i=0;i<5;i++) { Float(scene,radiusOffsets[i],radius);for(int k=0;k<3;k++) Float(scene,centerOffsets[i]+4*k,sphere[k]); }
            using(var ms=new MemoryStream()) using(var w=new BinaryWriter(ms))
            {
                w.Write((byte)255);w.Write(0);w.Write(PacketPrefix);
                for(int i=0;i<primitives.Count;i++) { w.Write(checked(firstId+(uint)i)); if(primitives[i].List) w.Write(new byte[]{0x1a,0x00}); else w.Write(FaceReserved); foreach(double x in primitives[i].Bounds) w.Write(Finite(x)); w.Write(primitives[i].List?(byte)0x20:FaceSuffix); }
                for(int i=0;i<edges.Count;i++)
                {
                    w.Write((byte)96);w.Write(checked(firstId+(uint)primitives.Count+(uint)i));w.Write(EdgeReserved);
                    Compact(w,(uint)faceToPrimitive[checked((int)edges[i].Faces[0])]);
                    Compact(w,edges[i].Faces.Count==2?(uint)faceToPrimitive[checked((int)edges[i].Faces[1])]:uint.MaxValue);
                }
                packet=ms.ToArray();LE(packet,1,packet.Length);
            }
            nextId=checked(firstId+(uint)primitives.Count+(uint)edges.Count);
        }
        private static void WriteContainer(byte[] scene,byte[] packet,string outPath,List<float[]> geometry=null,bool compact95=false)
        {
            byte[] directory=(byte[])DirectoryTemplate.Clone(),header=(byte[])Header.Clone();
            string temporary=outPath+"."+Guid.NewGuid().ToString("N")+".partial";
            try
            {
            using(var ms=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write))
            {
                ms.Write(header,0,header.Length);int encodedBytes=0;
                foreach(var e in TemplateEntries)
                {
                    byte[] blob=e.Type==12?scene:e.Type==13?packet:e.Data;
                    if(e.Type==6&&compact95){blob=(byte[])blob.Clone();blob[4]=22;}
                    if(e.Type==9 && geometry!=null)
                    {
                        blob=(byte[])blob.Clone();double[] lo={double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity},hi={double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity};
                        foreach(var v in geometry) for(int k=0;k<3;k++){lo[k]=Math.Min(lo[k],v[k]);hi[k]=Math.Max(hi[k],v[k]);}
                        for(int k=0;k<3;k++){Float(blob,0x1b+4*k,lo[k]);Float(blob,0x27+4*k,hi[k]);}
                    }
                    int encoding=(directory[e.Offset+8]<<8)|directory[e.Offset+9];
                    if(encoding==2) encodedBytes=checked(encodedBytes+blob.Length);
                    if(blob.Length<e.Count) throw new InvalidDataException("Stream shorter than fragment count");
                    if(e.Type>=12) { BE(directory,e.Offset-(e.Type==12?102:98),blob.Length);BE(directory,e.Offset+14,blob.Length); }
                    int logical=0;
                    for(int i=0;i<e.Count;i++)
                    {
                        int n=blob.Length/e.Count+(i<blob.Length%e.Count?1:0),p=e.Offset+86+20*i;
                        BE(directory,p,checked((int)ms.Position));BE(directory,p+4,n);BE(directory,p+8,n);BE(directory,p+12,logical);BE(directory,p+16,0);
                        ms.Write(blob,logical,n);logical+=n;
                    }
                }
                BE(header,8,checked((int)ms.Position));
                // Native directory header summarizes type-2 bytes and total physical file size.
                BE(directory,27,encodedBytes);BE(directory,31,checked((int)ms.Position+directory.Length));
                ms.Write(directory,0,directory.Length);ms.Position=0;ms.Write(header,0,header.Length);

            }
            File.Move(temporary,outPath);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
