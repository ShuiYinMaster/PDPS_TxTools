using System;
using System.Collections.Generic;
using System.Globalization;
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
        // Long strips are legal; a generous bound only guards against pathological
        // chains. Reference (DTK) files average well over twenty triangles a strip.
        const int StripLimit=1024;
        sealed class Piece { public MeshShape Mesh;public int[] Faces;public byte[] Color;public Guid Segment;public int ShapeId,MaterialId,LateId,VertexRecords; }
        sealed class Part
        {
            public readonly List<Piece> Pieces = new List<Piece>();
            public string Name;
            public int EntityId, RootId, NodeId, LodId, GroupId;
            public int EntityNameId, RootNameId, NameId;
        }
        // Faces[k] = the piece-relative face that introduced Vertices[k]; used only
        // when a corner normal is missing and the face normal is the fallback.
        sealed class Strip { public List<int> Vertices=new List<int>();public List<int> Faces=new List<int>();public int Start,Triangles; }
        // Assembly tree node. Part nodes own one Part; assembly nodes only group
        // children so the source product structure reaches the JT scene graph.
        sealed class SceneNode
        {
            public string Name;
            public Part Part;
            public readonly List<SceneNode> Children=new List<SceneNode>();
            public int Id,NameId;
        }
        static byte[] Build(Action<BinaryWriter> action){using(var s=new MemoryStream()){using(var w=new BinaryWriter(s,Encoding.UTF8,true))action(w);return s.ToArray();}}
        static Guid Type(string prefix){return new Guid(prefix+"-2ac8-11d1-9b6b-0080c7bb5997");}
        static Guid Stable(string key){using(var h=SHA256.Create())return new Guid(h.ComputeHash(Encoding.UTF8.GetBytes(key)).Take(16).ToArray());}
        static string Digest(byte[] value){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(value));}
        static float F(double d){float f=(float)d;if(float.IsNaN(f)||float.IsInfinity(f))throw new InvalidDataException("Float32 overflow");return f;}
        static void String(BinaryWriter w,string s){w.Write(s.Length);w.Write(Encoding.Unicode.GetBytes(s));}
        static byte[] Element(Guid type,byte baseType,byte[] body){return Build(w=>{w.Write(17+body.Length);w.Write(type.ToByteArray());w.Write(baseType);w.Write(body);});}
        static void End(BinaryWriter w){w.Write(16);w.Write(Enumerable.Repeat((byte)255,16).ToArray());}
        static void BaseNode(BinaryWriter w,int id,params int[] attributes){w.Write(id);w.Write(0U);w.Write(attributes.Length);foreach(int a in attributes)w.Write(a);}
        static void Bounds(BinaryWriter w,double[] lo,double[] hi){for(int k=0;k<3;k++)w.Write(F(lo[k]));for(int k=0;k<3;k++)w.Write(F(hi[k]));}
        static void Extents(Piece p,double[] lo,double[] hi)
        {
            foreach(int index in p.Faces)
            {
                var ids=p.Mesh.triangles[index];
                for(int corner=0;corner<3;corner++)
                {
                    var point=p.Mesh.vertices[ids[corner]];
                    for(int k=0;k<3;k++)
                    {
                        if(point[k]<lo[k])lo[k]=point[k];
                        if(point[k]>hi[k])hi[k]=point[k];
                    }
                }
            }
        }
        static void Bounds(BinaryWriter w,Piece p)
        {
            var lo=new[]{double.MaxValue,double.MaxValue,double.MaxValue};
            var hi=new[]{double.MinValue,double.MinValue,double.MinValue};
            Extents(p,lo,hi);
            Bounds(w,lo,hi);
        }
        static void Range(BinaryWriter w,int n){w.Write(n);w.Write(n);}
        static double Area(Piece p){double total=0;foreach(int index in p.Faces){var ids=p.Mesh.triangles[index];var a=p.Mesh.vertices[ids[0]];var b=p.Mesh.vertices[ids[1]];var c=p.Mesh.vertices[ids[2]];double x=(b[1]-a[1])*(c[2]-a[2])-(b[2]-a[2])*(c[1]-a[1]);double y=(b[2]-a[2])*(c[0]-a[0])-(b[0]-a[0])*(c[2]-a[2]);double z=(b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);total+=Math.Sqrt(x*x+y*y+z*z)/2;}return total;}
        static byte[] Shape(Piece p,int payloadBytes)
        {
            return Element(Type("10dd1077"),2,Build(w=>{
                BaseNode(w,p.ShapeId,p.MaterialId);Bounds(w,p);Bounds(w,p);w.Write(F(Area(p)));
                Range(w,p.VertexRecords);Range(w,1);Range(w,p.Faces.Length);w.Write(payloadBytes);w.Write(0f);
                w.Write(1);w.Write(0);w.Write(0);w.Write(new byte[4]);}));
        }
        // Use the legacy header found in the PS library JT 8.0. The GUID and
        // payload identify this element as a material.
        static byte[] Material(Piece p){return Element(Type("10dd1030"),1,Build(w=>{
            w.Write(p.MaterialId);w.Write((byte)0);w.Write(0U);w.Write((ushort)0);
            foreach(float v in new[]{.3f,.3f,.3f,1f})w.Write(v);
            foreach(float v in new[]{p.Color[0]/255f,p.Color[1]/255f,p.Color[2]/255f,1f})w.Write(v);
            foreach(float v in new[]{.8f,.8f,.8f,1f,0f,0f,0f,1f})w.Write(v);w.Write(15f);}));}
        static byte[] PropertyString(int id,string text){return Element(Type("10dd106e"),5,Build(w=>{w.Write(id);w.Write(0x40000000U);String(w,text);}));}
        // JT_PROP_NAME encodes assembly and part names as name.type;version;instance:.
        // A semicolon in a source file name cannot be part of the encoded name.
        static string ProductNodeName(string name, string type, int instance)
        {
            return name.Replace(';', '_') + "." + type + ";1;" +
                instance.ToString(CultureInfo.InvariantCulture) + ":";
        }
        static void NodeProperties(BinaryWriter writer, int nodeId, params int[] propertyPairs)
        {
            writer.Write(nodeId);
            foreach (int id in propertyPairs) writer.Write(id);
            writer.Write(0);
        }
        // 延迟装载属性：把属性/形状绑定到另一个段。末尾的整型是目标段的段类型（1=LSG，4=元数据，7=LOD0）。
        static byte[] LateRef(int id,Guid segment,int type){return Element(new Guid("e0b05be5-fbbd-11d1-a3a7-00aa00d10954"),8,Build(w=>{
            w.Write(id);w.Write(0x40000000U);w.Write(segment.ToByteArray());w.Write(type);}));}
        // 元数据段元素（ce357247 Property Proxy）：键长 + UTF-16 键 + 类型字节 + 值，0 结尾。
        // 类型 1 的值为 字符数 + UTF-16。PS 与 DTK 的件都带一个 `JT_PROP_ORIGINATING_BREPTYPE=None`，
        // 用于声明该零件没有 B-Rep、几何只有网格。
        static void MetaString(BinaryWriter w,string key,string value)
        {
            w.Write(key.Length);w.Write(Encoding.Unicode.GetBytes(key));w.Write((byte)1);
            w.Write(value.Length);w.Write(Encoding.Unicode.GetBytes(value));
        }
        static byte[] MetaDataElement(string sceneName){return Element(new Guid("ce357247-38fb-11d1-a506-006097bdc6e1"),9,Build(w=>{
            MetaString(w,"Name::",sceneName);
            MetaString(w,"JT_PROP_ORIGINATING_BREPTYPE","None");
            MetaString(w,"AdvCompressLODLevel::","0.000000 0.000000 0.000000");
            w.Write(0);}));}
        static byte[] PartNode(int id,int lod){return Element(new Guid("ce357244-38fb-11d1-a506-006097bdc6e1"),1,Build(w=>{
            BaseNode(w,id);w.Write(1);w.Write(lod);w.Write((ushort)1);w.Write((ushort)1);
            w.Write(99); // Part empty field as written by the native JT reference.
        }));}
        static byte[] RangeLodNode(int id,int child){return Element(Type("10dd104c"),1,Build(w=>{
            BaseNode(w,id);w.Write(1);w.Write(child);
            w.Write(0); // Reserved float vector size.
            w.Write(0); // Reserved integer.
            w.Write(0); // Zero range limits for one representation.
            w.Write(0f);w.Write(0f);w.Write(0f);
        }));}
        static byte[] GroupNode(int id,int[] children){return Element(Type("10dd101b"),1,Build(w=>{
            BaseNode(w,id);w.Write(children.Length);foreach(int c in children)w.Write(c);}));}
        // The document metadata node groups the parts and references file metadata.
        static byte[] MetaDataNode(int id,int[] children,params int[] attributes){return Element(new Guid("ce357245-38fb-11d1-a506-006097bdc6e1"),1,Build(w=>{
            BaseNode(w,id,attributes);w.Write(children.Length);foreach(int c in children)w.Write(c);w.Write((short)1);}));}
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
            var leading=new Dictionary<long,int>(count*3,MeshEdgeComparer.Instance);
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
            if(!FlatStrips&&strips.Count>1)
            {
                // DEFLATE can reuse nearby repeated position/normal records only
                // within its sliding window. Spatially order whole strips; never
                // change a strip's vertices, winding or corner attributes.
                var centers=strips.Select(s=>Enumerable.Range(0,3).Select(axis=>s.Vertices.Average(v=>p.Mesh.vertices[v][axis])).ToArray()).ToArray();
                var lo=Enumerable.Range(0,3).Select(axis=>centers.Min(c=>c[axis])).ToArray();
                var span=Enumerable.Range(0,3).Select(axis=>centers.Max(c=>c[axis])-lo[axis]).ToArray();
                var order=Enumerable.Range(0,strips.Count).OrderBy(i=>SpatialKey(centers[i],lo,span)).ToArray();
                var offsets=new int[strips.Count];int offset=0;
                for(int i=0;i<strips.Count;i++){offsets[i]=offset;offset+=strips[i].Vertices.Count*24;}
                var spatial=new byte[raw.Length];offset=0;
                foreach(int i in order)
                {
                    int length=strips[i].Vertices.Count*24;
                    Buffer.BlockCopy(raw,offsets[i],spatial,offset,length);offset+=length;
                }
                var candidate=Zlib(spatial);
                if(candidate.Length<compressed.Length)
                {
                    compressed=candidate;raw=spatial;
                    strips=order.Select(i=>strips[i]).ToList();
                }
            }
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
        static uint SpatialKey(double[] point,double[] lo,double[] span)
        {
            uint key=0;
            for(int axis=0;axis<3;axis++)
            {
                uint cell=span[axis]>0?(uint)Math.Max(0,Math.Min(1023,(point[axis]-lo[axis])/span[axis]*1023)):0;
                for(int bit=0;bit<10;bit++)key|=((cell>>bit)&1U)<<(bit*3+axis);
            }
            return key;
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
            scene.Validate();WriteCore(scene.vehicleFrameId,"CadDrop.VehicleFrame",scene.shapes,null,null,output);
        }
        public static void Write(SourceMeshScene scene,string output)
        {
            scene.Validate();
            if(scene.instances!=null)WriteInstanced(scene,output);
            else WriteCore(scene.name,"CadDrop.SourceFrame",scene.shapes,scene.structure,scene.partShapes,output);
        }
        // Instanced assemblies write each source definition once in local
        // coordinates. Occurrence nodes carry only the placement transform and
        // reference the shared RangeLOD child, so repeated parts do not copy
        // vertex records or shape segments.
        sealed class Definition
        {
            public List<Piece> Pieces;
            public int GroupId,SharedChild;
        }
        sealed class InstancedNode
        {
            public string Name;
            public bool Part;
            public int Definition=-1;
            public double[] Frame;
            public readonly List<InstancedNode> Children=new List<InstancedNode>();
            public int Id,NameId,EntityId,RootId,NodeId,LodId,TransformId,EntityNameId,RootNameId,PartNameId;
        }
        static void WriteInstanced(SourceMeshScene scene,string output)
        {
            var pieces=new List<Piece>();
            var roundedCache=new Dictionary<MeshShape,MeshShape>();
            var definitions=new Definition[scene.definitions.Length];
            for(int i=0;i<definitions.Length;i++)
            {
                if(scene.definitions[i]==null||scene.definitions[i].Length==0)continue;
                var part=CreatePart(scene.definitions[i],"",pieces,roundedCache);
                if(part!=null)definitions[i]=new Definition{Pieces=part.Pieces};
            }
            var root=new InstancedNode{Name=scene.name};
            foreach(var child in scene.structure.Children)AddInstanced(root,child,definitions,scene.instances);
            if(root.Children.Count==0||pieces.Count==0)throw new InvalidDataException("Assembly has no writable geometry");
            var assemblies=new List<InstancedNode>();
            var occurrences=new List<InstancedNode>();
            CollectInstanced(root,root,assemblies,occurrences);
            int id=1,metaId=id++;
            foreach(var assembly in assemblies)
            {
                assembly.Id=id++;
                assembly.NameId=id++;
            }
            foreach(var occurrence in occurrences)
            {
                occurrence.EntityId=id++;
                occurrence.RootId=id++;
                occurrence.NodeId=id++;
                occurrence.LodId=id++;
                occurrence.TransformId=id++;
            }
            foreach(var definition in definitions)
                if(definition!=null&&definition.Pieces.Count>1)definition.GroupId=id++;
            foreach(var piece in pieces)piece.ShapeId=id++;
            foreach(var piece in pieces)piece.MaterialId=id++;
            int shapeImplKey=id++,nameKey=id++,assemblyName=id++,frameKey=id++,frameValue=id++,unitKey=id++,unitValue=id++;
            int metaKey=id++,partTypeKey=id++,partTypeValue=id++,metaLate=id++;
            int simplifierKey=id++,simplifierValue=id++,simplifyParamsKey=id++,simplifyParamsValue=id++;
            foreach(var occurrence in occurrences)
            {
                occurrence.EntityNameId=id++;
                occurrence.RootNameId=id++;
                occurrence.PartNameId=id++;
            }
            foreach(var piece in pieces)piece.LateId=id++;
            foreach(var definition in definitions)
            {
                if(definition==null)continue;
                definition.SharedChild=definition.GroupId==0?definition.Pieces[0].ShapeId:definition.GroupId;
            }
            var lods=new byte[pieces.Count][];
            for(int i=0;i<pieces.Count;i++){int records;lods[i]=Lod(pieces[i],out records);pieces[i].VertexRecords=records;}
            for(int i=0;i<pieces.Count;i++)pieces[i].Segment=Stable("jt8-normal-v3/"+pieces[i].Mesh.instanceId+"/"+BitConverter.ToString(pieces[i].Color)+"/"+Digest(lods[i]));
            var allLo=new[]{double.MaxValue,double.MaxValue,double.MaxValue};
            var allHi=new[]{double.MinValue,double.MinValue,double.MinValue};
            var localBounds=new double[definitions.Length][];
            for(int i=0;i<definitions.Length;i++)
            {
                if(definitions[i]==null)continue;
                var lo=new[]{double.MaxValue,double.MaxValue,double.MaxValue};
                var hi=new[]{double.MinValue,double.MinValue,double.MinValue};
                foreach(var piece in definitions[i].Pieces)Extents(piece,lo,hi);
                localBounds[i]=new[]{lo[0],lo[1],lo[2],hi[0],hi[1],hi[2]};
            }
            foreach(var occurrence in occurrences)
            {
                var bounds=localBounds[occurrence.Definition];
                if(bounds==null)continue;
                var frame=new Frame12(occurrence.Frame);
                for(int corner=0;corner<8;corner++)
                {
                    var point=frame.Point(new[]{
                        (corner&1)==0?bounds[0]:bounds[3],
                        (corner&2)==0?bounds[1]:bounds[4],
                        (corner&4)==0?bounds[2]:bounds[5]});
                    for(int k=0;k<3;k++)
                    {
                        if(point[k]<allLo[k])allLo[k]=point[k];
                        if(point[k]>allHi[k])allHi[k]=point[k];
                    }
                }
            }
            int totalFaces=pieces.Sum(p=>p.Faces.Length);
            var metaStream=MetaDataElement(scene.name);var metaSegment=Stable("jt8-metadata-v1/"+Digest(metaStream));
            int descendantCount=1+assemblies.Count+occurrences.Count*4+definitions.Count(item=>item!=null&&item.GroupId!=0)+pieces.Count;
            var graph=Build(w=>{
                w.Write(Element(Type("10dd103e"),1,Build(b=>{BaseNode(b,0);b.Write(1);b.Write(metaId);
                    b.Write(1);String(b,scene.name+".jt");Bounds(b,allLo,allHi);b.Write(F(pieces.Sum(p=>Area(p))));
                    Range(b,pieces.Sum(p=>p.VertexRecords));Range(b,descendantCount);
                    Range(b,totalFaces);Bounds(b,allLo,allHi);})));
                w.Write(MetaDataNode(metaId,root.Children.Select(EntryId).ToArray()));
                WriteInstancedNodes(w,root,definitions);
                foreach(var definition in definitions)
                    if(definition!=null&&definition.GroupId!=0)
                        w.Write(GroupNode(definition.GroupId,definition.Pieces.Select(piece=>piece.ShapeId).ToArray()));
                for(int i=0;i<pieces.Count;i++)w.Write(Shape(pieces[i],lods[i].Length));
                foreach(var p in pieces)w.Write(Material(p));
                foreach(var occurrence in occurrences)w.Write(TransformAttribute(occurrence.TransformId,occurrence.Frame));
                End(w);w.Write(PropertyString(shapeImplKey,"JT_LLPROP_SHAPEIMPL"));w.Write(PropertyString(nameKey,"JT_PROP_NAME"));
                w.Write(PropertyString(assemblyName,ProductNodeName(scene.name,"asm",0)));
                w.Write(PropertyString(frameKey,"CadDrop.SourceFrame"));w.Write(PropertyString(frameValue,scene.name));
                w.Write(PropertyString(unitKey,"JT_PROP_MEASUREMENT_UNITS"));w.Write(PropertyString(unitValue,"millimeters"));
                w.Write(PropertyString(metaKey,"JT_LLPROP_METADATA"));
                w.Write(PropertyString(partTypeKey,"PartitionType"));w.Write(PropertyString(partTypeValue,"Assembly"));
                w.Write(PropertyString(simplifierKey,"JT_PROP_SIMP_NAME"));
                w.Write(PropertyString(simplifierValue,"TxToolsTriStripper"));
                w.Write(PropertyString(simplifyParamsKey,"JT_PROP_SIMP_PARAMS"));
                w.Write(PropertyString(simplifyParamsValue,":LOD selection ratio=1.000000"));
                w.Write(LateRef(metaLate,metaSegment,4));
                for(int i=0;i<assemblies.Count;i++)
                    w.Write(PropertyString(assemblies[i].NameId,ProductNodeName(assemblies[i].Name,"asm",i+1)));
                for(int i=0;i<occurrences.Count;i++)
                {
                    var occurrence=occurrences[i];
                    w.Write(PropertyString(occurrence.EntityNameId,ProductNodeName(occurrence.Name+"\\\\"+occurrence.Name,"asm",i)));
                    w.Write(PropertyString(occurrence.RootNameId,ProductNodeName("Root","asm",i)));
                    w.Write(PropertyString(occurrence.PartNameId,ProductNodeName(occurrence.Name,"part",i)));
                }
                foreach(var piece in pieces)w.Write(LateRef(piece.LateId,piece.Segment,7));
                End(w);
                w.Write((short)1);
                w.Write(2+assemblies.Count+occurrences.Count*3+pieces.Count);
                NodeProperties(w,0,partTypeKey,partTypeValue,nameKey,assemblyName,frameKey,frameValue);
                NodeProperties(w,metaId,unitKey,unitValue,metaKey,metaLate);
                foreach(var assembly in assemblies)
                    NodeProperties(w,assembly.Id,nameKey,assembly.NameId,partTypeKey,partTypeValue,unitKey,unitValue);
                foreach(var occurrence in occurrences)
                {
                    NodeProperties(w,occurrence.EntityId,nameKey,occurrence.EntityNameId,unitKey,unitValue);
                    NodeProperties(w,occurrence.RootId,nameKey,occurrence.RootNameId,unitKey,unitValue);
                    NodeProperties(w,occurrence.NodeId,nameKey,occurrence.PartNameId,unitKey,unitValue);
                }
                foreach(var piece in pieces)
                    NodeProperties(w,piece.ShapeId,simplifyParamsKey,simplifyParamsValue,simplifierKey,simplifierValue,shapeImplKey,piece.LateId);
            });
            WriteFile(graph,pieces,lods,metaStream,output);
        }
        static void AddInstanced(InstancedNode parent,AssemblyStructureNode source,Definition[] definitions,AssemblyInstance[] instances)
        {
            if(source.IsPart)
            {
                if(source.PartIndex<0||source.PartIndex>=instances.Length)throw new InvalidDataException("Assembly references an unknown placement");
                var instance=instances[source.PartIndex];
                if(instance==null||instance.Definition<0||instance.Definition>=definitions.Length||definitions[instance.Definition]==null)return;
                parent.Children.Add(new InstancedNode{Name=instance.Name,Part=true,Definition=instance.Definition,Frame=instance.Frame});
                return;
            }
            var node=new InstancedNode{Name=source.Name};
            foreach(var child in source.Children)AddInstanced(node,child,definitions,instances);
            if(node.Children.Count!=0)parent.Children.Add(node);
        }
        static void CollectInstanced(InstancedNode node,InstancedNode root,List<InstancedNode> assemblies,List<InstancedNode> occurrences)
        {
            if(node.Part){occurrences.Add(node);return;}
            if(!ReferenceEquals(node,root))assemblies.Add(node);
            foreach(var child in node.Children)CollectInstanced(child,root,assemblies,occurrences);
        }
        static int EntryId(InstancedNode node){return node.Part?node.EntityId:node.Id;}
        static void WriteInstancedNodes(BinaryWriter w,InstancedNode node,Definition[] definitions)
        {
            if(node.Part)
            {
                w.Write(MetaDataNode(node.EntityId,new[]{node.RootId},node.TransformId));
                w.Write(MetaDataNode(node.RootId,new[]{node.NodeId}));
                w.Write(PartNode(node.NodeId,node.LodId));
                w.Write(RangeLodNode(node.LodId,definitions[node.Definition].SharedChild));
                return;
            }
            if(node.Id!=0)w.Write(MetaDataNode(node.Id,node.Children.Select(EntryId).ToArray()));
            foreach(var child in node.Children)WriteInstancedNodes(w,child,definitions);
        }
        static byte[] TransformAttribute(int id,double[] frame)
        {
            return Element(Type("10dd1083"),3,Build(w=>{
                w.Write(id);w.Write((byte)0);w.Write(0U);w.Write((ushort)0xFFFF);
                foreach(double value in new[]{frame[0],frame[1],frame[2],0d,frame[3],frame[4],frame[5],0d,
                    frame[6],frame[7],frame[8],0d,frame[9],frame[10],frame[11],1d})w.Write(F(value));
            }));
        }
        // Share only records that are identical at JT's actual storage precision.
        // CAD patch IDs and sub-float differences must not break otherwise valid
        // strips. Distinct hard-edge normals remain distinct vertex records.
        static MeshShape CompactRecords(MeshShape mesh)
        {
            if(mesh.normals==null)return mesh;
            var lookup=new Dictionary<Tuple<float,float,float,float,float,float>,int>();
            var vertices=new List<double[]>();var normals=new List<double[]>();
            var map=new int[mesh.vertices.Length];
            for(int i=0;i<map.Length;i++)
            {
                var p=mesh.vertices[i];var n=mesh.normals[i];int index;
                if(n==null)
                {
                    // A missing normal belongs to its introducing face, so it
                    // cannot be welded safely without splitting every corner.
                    map[i]=vertices.Count;vertices.Add(p);normals.Add(null);continue;
                }
                var key=Tuple.Create(F(p[0]),F(p[1]),F(p[2]),F(n[0]),F(n[1]),F(n[2]));
                if(!lookup.TryGetValue(key,out index))
                {
                    index=vertices.Count;lookup.Add(key,index);vertices.Add(p);
                    normals.Add(new[]{(double)key.Item4,(double)key.Item5,(double)key.Item6});
                }
                map[i]=index;
            }
            return new MeshShape{name=mesh.name,instanceId=mesh.instanceId,sourceFile=mesh.sourceFile,sourceSha256=mesh.sourceSha256,
                vertices=vertices.ToArray(),normals=normals.ToArray(),rgb=mesh.rgb,
                triangles=mesh.triangles.Select(t=>new[]{map[t[0]],map[t[1]],map[t[2]]}).ToArray()};
        }
        // One source occurrence becomes one Part node. Its color groups stay
        // together under the part's single representation.
        static Part CreatePart(MeshShape[] meshes,string name,List<Piece> pieces,Dictionary<MeshShape,MeshShape> roundedCache)
        {
            var part=new Part{Name=name};
            foreach(var mesh in meshes)
            {
                MeshShape rounded;
                if(!roundedCache.TryGetValue(mesh,out rounded))
                {
                    rounded=new MeshShape{name=mesh.name,instanceId=mesh.instanceId,sourceFile=mesh.sourceFile,sourceSha256=mesh.sourceSha256,
                        vertices=mesh.vertices.Select(v=>v.Select(x=>(double)F(x)).ToArray()).ToArray(),triangles=mesh.triangles,rgb=mesh.rgb,normals=mesh.normals};
                    rounded=CompactRecords(rounded);
                    rounded.Validate(); // reject triangles that collapse after float32 storage
                    roundedCache.Add(mesh,rounded);
                }
                foreach(var group in Enumerable.Range(0,mesh.triangles.Length).GroupBy(i=>BitConverter.ToString(mesh.rgb[i])))
                {
                    var p=new Piece{Mesh=rounded,Faces=group.ToArray(),Color=mesh.rgb[group.First()]};
                    pieces.Add(p);part.Pieces.Add(p);
                }
            }
            return part.Pieces.Count==0?null:part;
        }
        // Prune branches whose geometry was skipped during conversion so an empty
        // source assembly never becomes an empty JT node.
        static void BuildScene(AssemblyStructureNode source,MeshShape[][] partShapes,List<Piece> pieces,
            Dictionary<MeshShape,MeshShape> roundedCache,SceneNode parent,string fallbackName)
        {
            string name=string.IsNullOrWhiteSpace(source.Name)?fallbackName:source.Name;
            if(source.IsPart)
            {
                if(source.PartIndex<0||source.PartIndex>=partShapes.Length)throw new InvalidDataException("Assembly references an unknown part");
                var meshes=partShapes[source.PartIndex];
                if(meshes==null||meshes.Length==0)return;
                var part=CreatePart(meshes,name,pieces,roundedCache);
                if(part!=null)parent.Children.Add(new SceneNode{Name=part.Name,Part=part});
                return;
            }
            var node=new SceneNode{Name=name};
            foreach(var child in source.Children)BuildScene(child,partShapes,pieces,roundedCache,node,fallbackName);
            if(node.Children.Count!=0)parent.Children.Add(node);
        }
        static void Collect(SceneNode node,SceneNode root,List<Part> parts,List<SceneNode> assemblies)
        {
            if(node.Part!=null){parts.Add(node.Part);return;}
            if(!ReferenceEquals(node,root))assemblies.Add(node);
            foreach(var child in node.Children)Collect(child,root,parts,assemblies);
        }
        static int EntryId(SceneNode node){return node.Part==null?node.Id:node.Part.EntityId;}
        static void WriteNodes(BinaryWriter w,SceneNode node)
        {
            if(node.Part!=null)
            {
                var part=node.Part;
                // Match the entity and Root assembly nodes of the PS library.
                w.Write(MetaDataNode(part.EntityId, new[] { part.RootId }));
                w.Write(MetaDataNode(part.RootId, new[] { part.NodeId }));
                w.Write(PartNode(part.NodeId, part.LodId));
                int lodChild = part.GroupId == 0 ? part.Pieces[0].ShapeId : part.GroupId;
                w.Write(RangeLodNode(part.LodId, lodChild));
                // A RangeLOD's children are alternatives. Keep all colors
                // together in a single representation when a group is needed.
                if (part.GroupId != 0)
                    w.Write(GroupNode(part.GroupId, part.Pieces.Select(piece => piece.ShapeId).ToArray()));
                return;
            }
            if(node.Id!=0)w.Write(MetaDataNode(node.Id, node.Children.Select(EntryId).ToArray()));
            foreach(var child in node.Children)WriteNodes(w,child);
        }
        // Build the output tree. A source assembly manifest keeps its containers;
        // a single part or a legacy flat manifest becomes one level under the root.
        static void WriteCore(string sceneName,string frameProperty,MeshShape[] shapes,AssemblyStructureNode structure,
            MeshShape[][] partShapes,string output)
        {
            var pieces=new List<Piece>();
            // float32 取整与校验每个 mesh 只做一次，各颜色分组共享同一份，避免
            // 多色零件按颜色数重复克隆顶点/重复校验全部三角形。
            var roundedCache=new Dictionary<MeshShape,MeshShape>();
            var root=new SceneNode{Name=sceneName};
            if(structure==null)
            {
                foreach(var mesh in shapes)
                {
                    var part=CreatePart(new[]{mesh},string.IsNullOrWhiteSpace(mesh.name)?sceneName:mesh.name,pieces,roundedCache);
                    if(part!=null)root.Children.Add(new SceneNode{Name=part.Name,Part=part});
                }
            }
            else
            {
                if(partShapes==null)throw new InvalidDataException("Missing assembly geometry records");
                foreach(var child in structure.Children)BuildScene(child,partShapes,pieces,roundedCache,root,sceneName);
            }
            var parts=new List<Part>();
            var assemblies=new List<SceneNode>();
            Collect(root,root,parts,assemblies);
            if(parts.Count==0)throw new InvalidDataException("Assembly has no writable geometry");
            int id = 1, metaId = id++;
            foreach (var assembly in assemblies)
            {
                assembly.Id = id++;
                assembly.NameId = id++;
            }
            foreach (var part in parts)
            {
                part.EntityId = id++;
                part.RootId = id++;
                part.NodeId = id++;
                part.LodId = id++;
                if (part.Pieces.Count > 1) part.GroupId = id++;
            }
            foreach(var p in pieces)p.ShapeId=id++;foreach(var p in pieces)p.MaterialId=id++;
            int shapeImplKey=id++,nameKey=id++,assemblyName=id++,frameKey=id++,frameValue=id++,unitKey=id++,unitValue=id++;
            int metaKey=id++,partTypeKey=id++,partTypeValue=id++,metaLate=id++;
            int simplifierKey = id++, simplifierValue = id++, simplifyParamsKey = id++, simplifyParamsValue = id++;
            foreach (var part in parts)
            {
                part.EntityNameId = id++;
                part.RootNameId = id++;
                part.NameId = id++;
            }
            foreach (var piece in pieces) piece.LateId = id++;
            var lods=new byte[pieces.Count][];
            for(int i=0;i<pieces.Count;i++){int records;lods[i]=Lod(pieces[i],out records);pieces[i].VertexRecords=records;}
            for(int i=0;i<pieces.Count;i++)pieces[i].Segment=Stable("jt8-normal-v3/"+pieces[i].Mesh.instanceId+"/"+BitConverter.ToString(pieces[i].Color)+"/"+Digest(lods[i]));
            int totalFaces=pieces.Sum(p=>p.Faces.Length);
            var allLo=new[]{double.MaxValue,double.MaxValue,double.MaxValue};
            var allHi=new[]{double.MinValue,double.MinValue,double.MinValue};
            foreach(var piece in pieces)Extents(piece,allLo,allHi);
            var metaStream=MetaDataElement(sceneName);var metaSegment=Stable("jt8-metadata-v1/"+Digest(metaStream));
            int descendantCount = 1 + assemblies.Count + parts.Count * 4 + parts.Count(part => part.GroupId != 0) + pieces.Count;
            var graph=Build(w=>{
                w.Write(Element(Type("10dd103e"),1,Build(b=>{BaseNode(b,0);b.Write(1);b.Write(metaId);
                    b.Write(1);String(b,sceneName+".jt");Bounds(b,allLo,allHi);b.Write(F(pieces.Sum(p=>Area(p))));
                    Range(b,pieces.Sum(p=>p.VertexRecords));Range(b,descendantCount);
                    Range(b,totalFaces);Bounds(b,allLo,allHi);})));
                w.Write(MetaDataNode(metaId, root.Children.Select(EntryId).ToArray()));
                WriteNodes(w,root);
                for(int i=0;i<pieces.Count;i++)w.Write(Shape(pieces[i],lods[i].Length));
                foreach(var p in pieces)w.Write(Material(p));
                End(w);w.Write(PropertyString(shapeImplKey,"JT_LLPROP_SHAPEIMPL"));w.Write(PropertyString(nameKey,"JT_PROP_NAME"));
                w.Write(PropertyString(assemblyName,ProductNodeName(sceneName,"asm",0)));
                w.Write(PropertyString(frameKey,frameProperty));w.Write(PropertyString(frameValue,sceneName));
                w.Write(PropertyString(unitKey,"JT_PROP_MEASUREMENT_UNITS"));w.Write(PropertyString(unitValue,"millimeters"));
                w.Write(PropertyString(metaKey,"JT_LLPROP_METADATA"));
                w.Write(PropertyString(partTypeKey,"PartitionType"));w.Write(PropertyString(partTypeValue,"Assembly"));
                w.Write(PropertyString(simplifierKey, "JT_PROP_SIMP_NAME"));
                w.Write(PropertyString(simplifierValue, "TxToolsTriStripper"));
                w.Write(PropertyString(simplifyParamsKey, "JT_PROP_SIMP_PARAMS"));
                w.Write(PropertyString(simplifyParamsValue, ":LOD selection ratio=1.000000"));
                w.Write(LateRef(metaLate,metaSegment,4));
                for(int i=0;i<assemblies.Count;i++)
                    w.Write(PropertyString(assemblies[i].NameId, ProductNodeName(assemblies[i].Name, "asm", i + 1)));
                for(int i=0;i<parts.Count;i++)
                {
                    var part=parts[i];
                    string entityPath = part.Name + "\\\\" + part.Name;
                    w.Write(PropertyString(part.EntityNameId, ProductNodeName(entityPath, "asm", i)));
                    w.Write(PropertyString(part.RootNameId, ProductNodeName("Root", "asm", i)));
                    w.Write(PropertyString(part.NameId, ProductNodeName(part.Name, "part", i)));
                }
                foreach (var piece in pieces) w.Write(LateRef(piece.LateId, piece.Segment, 7));
                End(w);
                w.Write((short)1);
                w.Write(2 + assemblies.Count + parts.Count * 3 + pieces.Count);
                NodeProperties(w, 0, partTypeKey, partTypeValue, nameKey, assemblyName, frameKey, frameValue);
                NodeProperties(w, metaId, unitKey, unitValue, metaKey, metaLate);
                foreach (var assembly in assemblies)
                    NodeProperties(w, assembly.Id, nameKey, assembly.NameId, partTypeKey, partTypeValue, unitKey, unitValue);
                foreach (var part in parts)
                {
                    NodeProperties(w, part.EntityId, nameKey, part.EntityNameId, unitKey, unitValue);
                    NodeProperties(w, part.RootId, nameKey, part.RootNameId, unitKey, unitValue);
                    NodeProperties(w, part.NodeId, nameKey, part.NameId, unitKey, unitValue);
                }
                foreach (var piece in pieces)
                    NodeProperties(w, piece.ShapeId, simplifyParamsKey, simplifyParamsValue, simplifierKey, simplifierValue, shapeImplKey, piece.LateId);
            });
            WriteFile(graph,pieces,lods,metaStream,output);
        }
        static void WriteFile(byte[] graph,List<Piece> pieces,byte[][] lods,byte[] metaStream,string output)
        {
            Guid lsg=Stable("jt8-normal-v3/lsg/"+Digest(graph));
            var compressed=Zlib(graph);var lsgBody=Build(w=>{w.Write(2);w.Write(compressed.Length+1);w.Write((byte)2);w.Write(compressed);});
            var metaCompressed=Zlib(metaStream);
            var metaBody=Build(w=>{w.Write(2);w.Write(metaCompressed.Length+1);w.Write((byte)2);w.Write(metaCompressed);});
            var metaSegment=Stable("jt8-metadata-v1/"+Digest(metaStream));
            var segments=new List<byte[]>{Segment(lsg,1,lsgBody)};segments.AddRange(pieces.Select((p,i)=>Segment(p.Segment,7,lods[i])));
            segments.Add(Segment(metaSegment,4,metaBody));
            var segmentTypes=new List<int>{1};segmentTypes.AddRange(pieces.Select(_=>7));segmentTypes.Add(4);
            int toc=105+segments.Sum(s=>s.Length);var result=Build(w=>{
                var header=Encoding.ASCII.GetBytes("Version 8.0 JT            TxTools CATIA mesh writer");w.Write(header);w.Write(Enumerable.Repeat((byte)32,76-header.Length).ToArray());w.Write(new byte[]{10,13,10,32});
                w.Write((byte)0);w.Write(0);w.Write(toc);w.Write(lsg.ToByteArray());foreach(var s in segments)w.Write(s);
                w.Write(segments.Count);int offset=105;for(int i=0;i<segments.Count;i++){var s=segments[i];w.Write(s.Take(16).ToArray());w.Write(offset);w.Write(s.Length);w.Write(new byte[]{0,0,0,(byte)segmentTypes[i]});offset+=s.Length;}
            });
            if(result.Length!=toc+4+28*segments.Count)throw new InvalidDataException("JT size accounting mismatch");
            if(File.Exists(output))throw new IOException("Output already exists");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));string pending=output+".pending";
            using(var s=new FileStream(pending,FileMode.CreateNew,FileAccess.Write))s.Write(result,0,result.Length);
            File.Move(pending,output);
        }
    }
}
