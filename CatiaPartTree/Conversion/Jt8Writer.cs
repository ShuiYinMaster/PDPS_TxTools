using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.CatiaPartTree.Conversion
{
    // JT 8.0 compatibility profile: opaque materials, float32 XYZ and explicit
    // unit face normals at each corner. Geometry is already in the requested mm frame.
    public static class Jt8Writer
    {
        // Corners within this angle of the welded vertex normal are shaded smooth.
        static readonly double CreaseCos=Math.Cos(25*Math.PI/180);
        // Long strips are legal; a generous bound only guards against pathological
        // chains. Reference (DTK) files average well over twenty triangles a strip.
        const int StripLimit=1024;
        sealed class Piece { public MeshShape Mesh;public int[] Faces;public byte[] Color;public Guid Segment;public int ShapeId,MaterialId,LateId,NameId,VertexRecords;public string Name; }
        // Faces[k] = the piece-relative face that introduced Vertices[k]; used only
        // when a corner normal is missing and the face normal is the fallback.
        sealed class Strip { public List<int> Vertices=new List<int>();public List<int> Faces=new List<int>();public int Start,Triangles; }
        static byte[] Build(Action<BinaryWriter> action){using(var s=new MemoryStream()){using(var w=new BinaryWriter(s,Encoding.UTF8,true))action(w);return s.ToArray();}}
        static Guid Type(string prefix){return new Guid(prefix+"-2ac8-11d1-9b6b-0080c7bb5997");}
        static Guid Stable(string key){using(var h=SHA256.Create())return new Guid(h.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(16).ToArray());}
        static string Digest(byte[] value){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(value));}
        static float F(double d){float f=(float)d;if(float.IsNaN(f)||float.IsInfinity(f))throw new InvalidDataException("Float32 overflow");return f;}
        static void String(BinaryWriter w,string s){w.Write(s.Length);w.Write(Encoding.Unicode.GetBytes(s));}
        static byte[] Element(Guid type,byte baseType,byte[] body){return Build(w=>{w.Write(17+body.Length);w.Write(type.ToByteArray());w.Write(baseType);w.Write(body);});}
        static void End(BinaryWriter w){w.Write(16);w.Write(Enumerable.Repeat((byte)255,16).ToArray());}
        static void BaseNode(BinaryWriter w,int id,params int[] attributes){w.Write(id);w.Write(0U);w.Write(attributes.Length);foreach(int a in attributes)w.Write(a);}
        static void Bounds(BinaryWriter w,IEnumerable<double[]> points){var p=points.ToArray();for(int k=0;k<3;k++)w.Write(F(p.Min(v=>v[k])));for(int k=0;k<3;k++)w.Write(F(p.Max(v=>v[k])));}
        static void Range(BinaryWriter w,int n){w.Write(n);w.Write(n);}
        static double Area(Piece p){double total=0;foreach(int index in p.Faces){var ids=p.Mesh.triangles[index];var a=p.Mesh.vertices[ids[0]];var b=p.Mesh.vertices[ids[1]];var c=p.Mesh.vertices[ids[2]];double x=(b[1]-a[1])*(c[2]-a[2])-(b[2]-a[2])*(c[1]-a[1]);double y=(b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]);double z=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);total+=Math.Sqrt(x*x+y*y+z*z)/2;}return total;}
        static double[][] Points(Piece p){return p.Faces.SelectMany(f=>p.Mesh.triangles[f].Select(i=>p.Mesh.vertices[i])).ToArray();}
        static byte[] Shape(Piece p,int payloadBytes)
        {
            var points=Points(p);return Element(Type("10dd1077"),2,Build(w=>{
                BaseNode(w,p.ShapeId,p.MaterialId);Bounds(w,points);Bounds(w,points);w.Write(F(Area(p)));
                Range(w,p.VertexRecords);Range(w,1);Range(w,p.Faces.Length);w.Write(payloadBytes);w.Write(0f);
                w.Write(1);w.Write(0);w.Write(0);w.Write(new byte[4]);}));
        }
        static byte[] Material(Piece p){return Element(Type("10dd1030"),3,Build(w=>{
            w.Write(p.MaterialId);w.Write((byte)0);w.Write(0U);w.Write((ushort)0);
            foreach(float v in new[]{.2f,.2f,.2f,1f})w.Write(v);
            foreach(float v in new[]{p.Color[0]/255f,p.Color[1]/255f,p.Color[2]/255f,1f})w.Write(v);
            foreach(float v in new[]{.2f,.2f,.2f,1f,0f,0f,0f,1f})w.Write(v);w.Write(16f);}));}
        static byte[] PropertyString(int id,string text){return Element(Type("10dd106e"),5,Build(w=>{w.Write(id);w.Write(0x40000000U);String(w,text);}));}
        static byte[] Late(Piece p){return Element(new Guid("e0b05be5-fbbd-11d1-a3a7-00aa00d10954"),8,Build(w=>{w.Write(p.LateId);w.Write(0x40000000U);w.Write(p.Segment.ToByteArray());w.Write(7);}));}
        // Chained strips keep the triangle set, positions and corner normals identical;
        // only the strip boundaries change. Two triangles may share a vertex record only
        // when both of them shade that corner from the same vertex normal.
        static double[] FaceNormal(MeshShape mesh,int[] ids)
        {
            var a=mesh.vertices[ids[0]];var b=mesh.vertices[ids[1]];var c=mesh.vertices[ids[2]];
            double x=(b[1]-a[1])*(c[2]-a[2])-(b[2]-a[2])*(c[1]-a[1]);
            double y=(b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]);
            double z=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
            double length=Math.Sqrt(x*x+y*y+z*z);
            if(length==0||double.IsInfinity(length)||double.IsNaN(length))throw new InvalidDataException("Invalid face normal");
            return new[]{x/length,y/length,z/length};
        }
        // 参考件（DTK 译出的 JT）的做法：法向按顶点索引给，硬边靠"同一位置拆成不同索引"
        // 表现出来，而不是靠逐三角形切换平滑/平面。照抄这套才能既串长条带又不出现色带。
        // 缺法向的角点才用面法向（平铺对照模式或缓存无配对法向时）。
        // Two triangles may share a vertex record when both shade smoothly, or when both
        // are flat on the same plane (identical face normal).
        // 共享角点用的是同一批顶点索引，法向必然一致；只有缺法向（退化成面法向）时才会分叉。
        static bool Shareable(MeshShape mesh,int[] previous,int[] next)
        {
            if(mesh.normals==null)return false;
            return mesh.normals[next[0]]!=null&&mesh.normals[next[1]]!=null;
        }
        // Diagnostics switch: keeps one triangle per strip, matching the earlier output.
        public static bool FlatStrips;
        static List<Strip> Strips(Piece p)
        {
            var mesh=p.Mesh;int count=p.Faces.Length;var strips=new List<Strip>();
            if(FlatStrips)
            {
                for(int position=0;position<count;position++)
                {
                    var flat=mesh.triangles[p.Faces[position]];
                    var one=new Strip{Start=position};one.Vertices.Add(flat[0]);one.Vertices.Add(flat[1]);one.Vertices.Add(flat[2]);
                    one.Faces.Add(position);one.Faces.Add(position);one.Faces.Add(position);
                    one.Triangles=1;strips.Add(one);
                }
                return strips;
            }
            // Directed edges of every triangle (all three cyclic pairs) -> triangle
            // position. Chaining is free to rotate the candidate's indices: a cyclic
            // rotation keeps the winding, so the strip can pick up any adjacent
            // triangle no matter how the reader stored its rotation.
            var leading=new Dictionary<long,int>(count*3);
            for(int position=0;position<count;position++)
            {
                var ids=mesh.triangles[p.Faces[position]];
                for(int k=0;k<3;k++)
                {
                    long key=((long)(uint)ids[k]<<32)|(uint)ids[(k+1)%3];
                    if(!leading.ContainsKey(key))leading.Add(key,position);
                }
            }
            var used=new bool[count];
            for(int position=0;position<count;position++)
            {
                if(used[position])continue;
                var ids=mesh.triangles[p.Faces[position]];
                // The seed rotation decides the walk direction. A strip walk is forced
                // once started, so a bad rotation zig-zags into a dead end while the
                // same seed oriented along the corridor chains dozens of faces: try
                // all three rotations and keep the longest chain.
                Strip best=null;HashSet<int> bestConsumed=null;
                for(int r=0;r<3;r++)
                {
                    var strip=new Strip{Start=position};
                    strip.Vertices.Add(ids[r]);strip.Vertices.Add(ids[(r+1)%3]);strip.Vertices.Add(ids[(r+2)%3]);
                    for(int k=0;k<3;k++)strip.Faces.Add(position);
                    strip.Triangles=1;
                    var consumed=new HashSet<int>{position};
                    ExtendForward(mesh,p,leading,used,strip,consumed);
                    ExtendBackward(mesh,p,leading,used,strip,consumed);
                    if(best==null||strip.Triangles>best.Triangles){best=strip;bestConsumed=consumed;}
                }
                foreach(int f in bestConsumed)used[f]=true;
                strips.Add(best);
            }
            return strips;
        }
        static void ExtendForward(MeshShape mesh,Piece p,Dictionary<long,int> leading,bool[] used,Strip strip,HashSet<int> consumed)
        {
            var previous=new int[3];
            for(int k=0;k<3;k++)previous[k]=strip.Vertices[k];
            while(strip.Triangles<StripLimit)
            {
                int next=strip.Vertices.Count-2;
                int first=next%2==1?strip.Vertices[next+1]:strip.Vertices[next];
                int second=next%2==1?strip.Vertices[next]:strip.Vertices[next+1];
                int candidate;
                if(!leading.TryGetValue(((long)(uint)first<<32)|(uint)second,out candidate))return;
                if(used[candidate]||consumed.Contains(candidate))return;
                var nextIds=mesh.triangles[p.Faces[candidate]];
                int corner;
                if(nextIds[0]==first&&nextIds[1]==second)corner=nextIds[2];
                else if(nextIds[1]==first&&nextIds[2]==second)corner=nextIds[0];
                else if(nextIds[2]==first&&nextIds[0]==second)corner=nextIds[1];
                else return;
                var rotated=new[]{first,second,corner};
                if(!Shareable(mesh,previous,rotated))return;
                consumed.Add(candidate);
                strip.Vertices.Add(corner);strip.Faces.Add(candidate);strip.Triangles++;previous=rotated;
            }
        }
        static void ExtendBackward(MeshShape mesh,Piece p,Dictionary<long,int> leading,bool[] used,Strip strip,HashSet<int> consumed)
        {
            // Backward extension prepends faces in pairs: two vertices shift the
            // existing list by an even offset, so every original triangle keeps its
            // winding, and the two implied new faces keep theirs. Both faces are
            // validated before either is consumed, so a failed step leaks nothing.
            var previous=new[]{strip.Vertices[0],strip.Vertices[1],strip.Vertices[2]};
            while(strip.Triangles+2<=StripLimit)
            {
                int head0=strip.Vertices[0],head1=strip.Vertices[1];
                int faceA;
                if(!leading.TryGetValue(((long)(uint)head1<<32)|(uint)head0,out faceA)||used[faceA]||consumed.Contains(faceA))return;
                var idsA=mesh.triangles[p.Faces[faceA]];
                int z=Third(idsA,head0,head1);
                int faceB;
                if(z<0||!leading.TryGetValue(((long)(uint)z<<32)|(uint)head0,out faceB)||used[faceB]||faceB==faceA||consumed.Contains(faceB))return;
                var idsB=mesh.triangles[p.Faces[faceB]];
                int w=Third(idsB,z,head0);
                if(w<0||!Shareable(mesh,previous,new[]{head0,z,head1})||!Shareable(mesh,previous,new[]{w,z,head0}))return;
                consumed.Add(faceA);consumed.Add(faceB);
                strip.Vertices.Insert(0,z);strip.Vertices.Insert(0,w);
                strip.Faces.Insert(0,faceA);strip.Faces.Insert(0,faceB);
                strip.Triangles+=2;previous=new[]{w,z,head0};
            }
        }
        static int Third(int[] t,int u,int v)
        {
            for(int k=0;k<3;k++)if(t[k]!=u&&t[k]!=v)return t[k];
            return -1;
        }
        static byte[] Lod(Piece p,out int vertexRecords)
        {
            var strips=Strips(p);vertexRecords=0;foreach(var strip in strips)vertexRecords+=strip.Vertices.Count;
            var raw=Build(w=>{foreach(var strip in strips)for(int k=0;k<strip.Vertices.Count;k++)
            {
                int index=strip.Vertices[k];
                // The corner belongs to the triangle that introduced it; shared vertices
                // were only chained when both sides agree on the vertex normal.
                double[] normal=p.Mesh.normals==null?null:p.Mesh.normals[index];
                if(normal==null)
                {
                    // Faces[k] is the face that introduced this vertex record.
                    var ids=p.Mesh.triangles[p.Faces[strip.Faces[k]]];
                    normal=FaceNormal(p.Mesh,ids);
                }
                w.Write(F(normal[0]));w.Write(F(normal[1]));w.Write(F(normal[2]));
                foreach(double v in p.Mesh.vertices[index])w.Write(F(v));
            }});
            var compressed=Zlib(raw);
            var bounds=new List<int>{0};foreach(var strip in strips)bounds.Add(bounds[bounds.Count-1]+strip.Vertices.Count);
            return Element(Type("10dd10ab"),4,Build(w=>{
                w.Write((short)1);w.Write(1);w.Write(new byte[4]); // VertexShapeLOD v1, per-vertex normal binding
                w.Write((short)1);w.Write((short)1);w.Write(new byte[]{1,0,0});w.Write(new byte[4]);
                // Null Int32CDP, PredStride1: first four boundaries absolute, then residuals
                // against the previous stride.
                w.Write((byte)0);w.Write(bounds.Count);
                for(int i=0;i<bounds.Count;i++)w.Write(i<4?bounds[i]:bounds[i]-(bounds[i-1]+(bounds[i-1]-bounds[i-2])));
                w.Write(raw.Length);
                if(compressed.Length<raw.Length){w.Write(compressed.Length);w.Write(compressed);}
                else{w.Write(-raw.Length);w.Write(raw);}
            }));
        }
        static byte[] Zlib(byte[] input)
        {
            byte[] deflate=Build(w=>{using(var z=new DeflateStream(w.BaseStream,CompressionLevel.Optimal,true))z.Write(input,0,input.Length);});
            uint a=1,b=0;foreach(byte value in input){a=(a+value)%65521;b=(b+a)%65521;}uint adler=(b<<16)|a;
            return Build(w=>{w.Write(new byte[]{0x78,0x9c});w.Write(deflate);w.Write(new[]{(byte)(adler>>24),(byte)(adler>>16),(byte)(adler>>8),(byte)adler});});
        }
        static byte[] Segment(Guid id,int type,byte[] body){return Build(w=>{w.Write(id.ToByteArray());w.Write(type);w.Write(body.Length+24);w.Write(body);});}
        public static void Write(VehicleMeshScene scene,string output)
        {
            scene.Validate();WriteCore(scene.vehicleFrameId,"CadDrop.VehicleFrame",scene.shapes,output);
        }
        public static void Write(SourceMeshScene scene,string output)
        {
            scene.Validate();WriteCore(scene.name,"CadDrop.SourceFrame",scene.shapes,output);
        }
        static void WriteCore(string sceneName,string frameProperty,MeshShape[] shapes,string output)
        {
            var pieces=new List<Piece>();
            // float32 取整与校验每个 mesh 只做一次，各颜色分组共享同一份，避免
            // 多色零件按颜色数重复克隆顶点/重复校验全部三角形。
            var roundedCache=new Dictionary<MeshShape,MeshShape>();
            foreach(var mesh in shapes)
            {
                MeshShape rounded;
                if(!roundedCache.TryGetValue(mesh,out rounded))
                {
                    rounded=new MeshShape{name=mesh.name,instanceId=mesh.instanceId,sourceFile=mesh.sourceFile,sourceSha256=mesh.sourceSha256,
                        vertices=mesh.vertices.Select(v=>v.Select(x=>(double)F(x)).ToArray()).ToArray(),triangles=mesh.triangles,rgb=mesh.rgb,normals=mesh.normals};
                    rounded.Validate(); // reject triangles that collapse after float32 storage
                    roundedCache.Add(mesh,rounded);
                }
                foreach(var group in Enumerable.Range(0,mesh.triangles.Length).GroupBy(i=>BitConverter.ToString(mesh.rgb[i])))
                {
                    var p=new Piece{Mesh=rounded,Faces=group.ToArray(),Color=mesh.rgb[group.First()],Name=mesh.name+"/"+group.Key};
                    pieces.Add(p);
                }
            }
            int id=1;foreach(var p in pieces)p.ShapeId=id++;foreach(var p in pieces)p.MaterialId=id++;
            int shapeImplKey=id++,nameKey=id++,frameKey=id++,frameValue=id++,unitKey=id++,unitValue=id++;
            foreach(var p in pieces){p.LateId=id++;p.NameId=id++;}
            var lods=new byte[pieces.Count][];
            for(int i=0;i<pieces.Count;i++){int records;lods[i]=Lod(pieces[i],out records);pieces[i].VertexRecords=records;}
            for(int i=0;i<pieces.Count;i++)pieces[i].Segment=Stable("jt8-normal-v3/"+pieces[i].Mesh.instanceId+"/"+BitConverter.ToString(pieces[i].Color)+"/"+Digest(lods[i]));
            var all=pieces.SelectMany(Points).ToArray();int totalFaces=pieces.Sum(p=>p.Faces.Length);
            var graph=Build(w=>{
                w.Write(Element(Type("10dd103e"),1,Build(b=>{BaseNode(b,0);b.Write(pieces.Count);foreach(var p in pieces)b.Write(p.ShapeId);
                    b.Write(1);String(b,sceneName+".jt");Bounds(b,all);b.Write(F(pieces.Sum(p=>Area(p))));Range(b,all.Length);Range(b,pieces.Count+1);Range(b,totalFaces);Bounds(b,all);})));
                for(int i=0;i<pieces.Count;i++)w.Write(Shape(pieces[i],lods[i].Length));
                foreach(var p in pieces)w.Write(Material(p));
                End(w);w.Write(PropertyString(shapeImplKey,"JT_LLPROP_SHAPEIMPL"));w.Write(PropertyString(nameKey,"JT_PROP_NAME"));
                w.Write(PropertyString(frameKey,frameProperty));w.Write(PropertyString(frameValue,sceneName));
                w.Write(PropertyString(unitKey,"JT_PROP_MEASUREMENT_UNITS"));w.Write(PropertyString(unitValue,"millimeters"));
                foreach(var p in pieces){w.Write(Late(p));w.Write(PropertyString(p.NameId,p.Name));}End(w);
                w.Write((short)1);w.Write(pieces.Count+1);w.Write(0);w.Write(frameKey);w.Write(frameValue);w.Write(nameKey);w.Write(frameValue);w.Write(unitKey);w.Write(unitValue);w.Write(0);
                foreach(var p in pieces){w.Write(p.ShapeId);w.Write(shapeImplKey);w.Write(p.LateId);w.Write(nameKey);w.Write(p.NameId);w.Write(unitKey);w.Write(unitValue);w.Write(0);}
            });
            Guid lsg=Stable("jt8-normal-v3/lsg/"+Digest(graph));
            var compressed=Zlib(graph);var lsgBody=Build(w=>{w.Write(2);w.Write(compressed.Length+1);w.Write((byte)2);w.Write(compressed);});
            var segments=new List<byte[]>{Segment(lsg,1,lsgBody)};segments.AddRange(pieces.Select((p,i)=>Segment(p.Segment,7,lods[i])));
            int toc=105+segments.Sum(s=>s.Length);var result=Build(w=>{
                var header=Encoding.ASCII.GetBytes("Version 8.0 JT            TxTools CATIA mesh writer");w.Write(header);w.Write(Enumerable.Repeat((byte)32,76-header.Length).ToArray());w.Write(new byte[]{10,13,10,32});
                w.Write((byte)0);w.Write(0);w.Write(toc);w.Write(lsg.ToByteArray());foreach(var s in segments)w.Write(s);
                w.Write(segments.Count);int offset=105;for(int i=0;i<segments.Count;i++){var s=segments[i];w.Write(s.Take(16).ToArray());w.Write(offset);w.Write(s.Length);w.Write(new byte[]{0,0,0,(byte)(i==0?1:7)});offset+=s.Length;}
            });
            if(result.Length!=toc+4+28*segments.Count)throw new InvalidDataException("JT size accounting mismatch");
            if(File.Exists(output))throw new IOException("Output already exists");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));string pending=output+".pending";
            using(var s=new FileStream(pending,FileMode.CreateNew,FileAccess.Write))s.Write(result,0,result.Length);
            File.Move(pending,output);
        }
    }
}
