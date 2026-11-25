using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TxTools.CatiaPartTree.Conversion
{
    public static class NativeCadReader
    {
        sealed class Record { public int Id,Kind,Parent;public string Name;public byte[] Data; }
        static int BE(byte[] d,int p){Range(d,p,4);uint v=(uint)d[p]<<24|(uint)d[p+1]<<16|(uint)d[p+2]<<8|d[p+3];return unchecked((int)v);}
        static void Range(byte[] d,int p,int n){if(p<0||n<0||p>d.Length-n)throw new InvalidDataException("Native data truncated");}
        static int Find(byte[] d,byte[] value,int start,int end){for(int p=start;p<=end-value.Length;p++)if(value.Where((v,i)=>d[p+i]!=v).Count()==0)return p;return -1;}
        static byte[] Slice(byte[] d,int p,int n){Range(d,p,n);var result=new byte[n];Buffer.BlockCopy(d,p,result,0,n);return result;}
        static readonly byte[] V5Magic=Encoding.ASCII.GetBytes("V5_CFV2\0");
        static string Hex(byte[] d,int p,int n)
        {
            if(p<0||p>d.Length-n)return "-";
            var text=new StringBuilder(n*2);for(int i=0;i<n;i++)text.Append(d[p+i].ToString("x2"));return text.ToString();
        }
        static bool IsDirectory(byte[] d,int p)
        {
            // Container revisions beyond CB0001 keep the directory framing; the
            // version itself is reported only when the table cannot be parsed.
            return p>=64&&p+16<=d.Length&&Encoding.ASCII.GetString(d,p,11)=="CATIA_V5 CB";
        }
        static void Locate(byte[] d,out int directory,out int length)
        {
            int headerDirectory=BE(d,8),headerLength=BE(d,12);
            bool footer=d.Length>=12&&Encoding.ASCII.GetString(d,d.Length-8,8)=="CB__END\0";
            int footerLength=footer?BE(d,d.Length-12):-1;
            // The trailing record repeats the directory length, so the block can be
            // located from the file tail even when the header words are stale.
            if(footer&&footerLength>=64&&footerLength<=d.Length-64&&IsDirectory(d,d.Length-footerLength))
            {directory=d.Length-footerLength;length=footerLength;return;}
            // Header candidate also accepts a directory followed by extra bytes.
            if(headerLength>=64&&headerDirectory>=64&&(long)headerDirectory+headerLength<=d.Length&&IsDirectory(d,headerDirectory))
            {directory=headerDirectory;length=headerLength;return;}
            int nested=0;for(int p=Find(d,V5Magic,8,d.Length);p>=0&&nested<16;p=Find(d,V5Magic,p+1,d.Length))nested++;
            throw new InvalidDataException("CFV final directory mismatch（文件 "+d.Length+" 字节，头部目录 "+headerDirectory+"+"+headerLength+"，尾部 "+(footer?"CB__END 长度 "+footerLength:"缺少 CB__END")+
                "，头部目录处 "+Hex(d,headerDirectory,16)+"，嵌套 V5_CFV2 "+nested+" 处）");
        }
        static List<Record> Container(byte[] d)
        {
            if(d.Length<80||Encoding.ASCII.GetString(d,0,8)!="V5_CFV2\0")
                throw new NotSupportedException("Expected native CFV2（文件 "+d.Length+" 字节，首部 "+Hex(d,0,8)+"）");
            int directory,length;Locate(d,out directory,out length);int end=directory+length-12;
            var records=new List<Record>();int previousEnd=-1;
            for(int p=directory+64;p+86<=end;)
            {
                int n=BE(d,p);if(n<86||(n-86)%20!=0||n>end-p){p++;continue;}
                int id=BE(d,p+4),kind=(d[p+8]<<8)|d[p+9],count=BE(d,p+82);
                if(id<1||kind>2||count!=(n-86)/20){p++;continue;}
                string name=null;int parent=0;
                if(previousEnd>=0)
                {
                    parent=BE(d,previousEnd);int s=previousEnd+80;int bytes=BE(d,previousEnd+76);
                    if(bytes>=2&&bytes%2==0&&s<=p&&bytes==p-s)
                    {
                        if(d[s]==0&&(d[s+1]==3||d[s+1]==5))s+=2;
                    }
                    else if(parent==0)s=previousEnd+86;else s=p;
                    if(s+2<=p&&d[p-2]==0&&d[p-1]==0){name=Encoding.BigEndianUnicode.GetString(d,s,p-s-2);if(name.Any(char.IsControl))name=null;}
                }
                var stream=new MemoryStream();int logical=0;
                for(int i=0;i<count;i++)
                {
                    int e=p+86+i*20,physical=BE(d,e),stored=BE(d,e+4),repeated=BE(d,e+8),offset=BE(d,e+12);
                    // Real parts keep a named-stream segment after the directory block,
                    // so extent data may live on either side of it. Only the file
                    // bounds themselves constrain the physical range.
                    if(stored<0||stored!=repeated||physical<64||(long)physical+stored>d.Length||(kind==2&&offset!=logical))
                        throw new InvalidDataException("Invalid CFV extent（记录 "+id+" 类型 "+kind+" 名称 "+(name??"?")+" extent "+i+"：物理 "+physical+"，长度 "+stored+"，重复 "+repeated+"，逻辑偏移 "+offset+"/"+logical+"，文件 "+d.Length+"）");
                    stream.Write(d,physical,stored);logical=checked(logical+stored);
                }
                if(kind==2&&logical!=BE(d,p+14))throw new InvalidDataException("CFV stream length mismatch");
                records.Add(new Record{Id=id,Kind=kind,Parent=parent,Name=name,Data=stream.ToArray()});previousEnd=p+n;p+=n;
            }
            if(records.Count==0||records.Select(r=>r.Id).Distinct().Count()!=records.Count||records.Where((r,i)=>r.Id!=i+1).Any())
                throw new InvalidDataException("Incomplete CFV allocation table（目录 "+directory+"+"+length+"，标签 "+Hex(d,directory,16)+"，记录 "+records.Count+"，名称 "+string.Join(",",records.Take(6).Select(r=>r.Name??"?"))+"）");
            return records;
        }
        public static byte[] EmbeddedCgr(byte[] part)
        {
            var records=Container(part);
            var stream=records.SingleOrDefault(r=>r.Kind==2&&r.Name=="Data");
            if(stream==null)throw new NotSupportedException("CATPart 未保存几何缓存（缺少 Data 流），请在 CATIA 中打开并保存该零件后再转换："+string.Join(",",records.Select(r=>r.Name??"?").Take(8)));
            byte[] data=stream.Data;
            byte[] names=Encoding.ASCII.GetBytes("CATCGRCont\0CATContainer\0");int p=Find(data,names,0,data.Length);
            if(p<0)throw new NotSupportedException("CATPart 未保存 CGR 几何缓存（缺少 CATCGRCont/CATContainer），请在 CATIA 中打开并保存该零件后再转换");
            if(Find(data,names,p+names.Length,data.Length)>=0)throw new NotSupportedException("CATCGRCont ambiguous");p+=names.Length;
            byte[] tag={3,0,247,0,3,0,0,0};int uuid=-1;
            for(int pad=0;pad<8;pad++)
            {
                int q=p+pad;if(q+24<=data.Length&&Slice(data,q,8).SequenceEqual(tag)){uuid=q+8;break;}
                if(q>=data.Length||data[q]!=0)break;
            }
            if(uuid<0)throw new NotSupportedException("Unknown CATCGRCont identity layout");
            // IDs are unsigned; reconstruct words without treating them as offsets.
            string identity=string.Join("_",Enumerable.Range(0,4).Select(k=>((uint)data[uuid+k*4]<<24|(uint)data[uuid+k*4+1]<<16|(uint)data[uuid+k*4+2]<<8|data[uuid+k*4+3]).ToString("x")));
            var caches=records.Where(r=>r.Kind==2&&r.Name=="Cont_"+identity).ToArray();
            if(caches.Length!=1)throw new InvalidDataException("CATCGRCont stream identity mismatch（"+identity+"，匹配 "+caches.Length+" 个，流 "+string.Join(",",records.Select(r=>r.Name??"?").Take(10))+"）");Container(caches[0].Data);return caches[0].Data;
        }
        sealed class Cursor
        {
            public byte[] D;public int P,End;
            public Cursor(byte[] d,int p,int end){D=d;P=p;End=end;}
            public void Need(int n){Range(D,P,n);if(P>End-n)throw new InvalidDataException("Native leaf truncated");}
            public int Byte(){Need(1);return D[P++];}
            public int Count(){int n=Byte();if(n!=255)return n;Need(4);uint value=BitConverter.ToUInt32(D,P);P+=4;return checked((int)value);}
            public float Float(){Need(4);float v=BitConverter.ToSingle(D,P);P+=4;if(float.IsNaN(v)||float.IsInfinity(v))throw new InvalidDataException("Nonfinite native coordinate");return v;}
            public int Index(int width)
            {
                // Index width follows the addressable vertex count: 1/2/3 bytes.
                int b=Byte();
                if(width==1)return b;
                if(width==2)return (b<<8)|Byte();
                int v=(b<<16)|(Byte()<<8)|Byte();if(v<0)throw new InvalidDataException("Native index sign");return v;
            }
        }
        // Native tessellation can produce a zero-thickness two-face flap at a
        // sub-precision tip. Remove only a reversed coincident pair attached by
        // one edge; never weld nearby positions or delete long thin source faces.
        public static MeshShape RemoveCollapsedFlaps(MeshShape mesh)
        {
            var vertices = new Dictionary<Tuple<double, double, double>, int>();
            var ids = new int[mesh.vertices.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var p = mesh.vertices[i]; var key = Tuple.Create(p[0], p[1], p[2]); int id;
                if (!vertices.TryGetValue(key, out id)) { id = vertices.Count; vertices.Add(key, id); }
                ids[i] = id;
            }
            var faces = mesh.triangles.Select(t => t.Select(i => ids[i]).ToArray()).ToArray();
            var coincident = new Dictionary<Tuple<int, int, int>, List<int>>();
            var edges = new Dictionary<long, int>(MeshEdgeComparer.Instance);
            long Edge(int a, int b) { return ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b); }
            for (int f = 0; f < faces.Length; f++)
            {
                var sorted = faces[f].OrderBy(i => i).ToArray(); var key = Tuple.Create(sorted[0], sorted[1], sorted[2]);
                List<int> list; if (!coincident.TryGetValue(key, out list)) coincident.Add(key, list = new List<int>()); list.Add(f);
                for (int k = 0; k < 3; k++) { long edge = Edge(faces[f][k], faces[f][(k + 1) % 3]); int n; edges.TryGetValue(edge, out n); edges[edge] = n + 1; }
            }
            var remove = new HashSet<int>();
            foreach (var pair in coincident.Values)
            {
                if (pair.Count != 2) continue;
                var a = faces[pair[0]]; var b = faces[pair[1]];
                int offset = Array.IndexOf(b, a[0]);
                if (b[(offset + 1) % 3] != a[2] || b[(offset + 2) % 3] != a[1]) continue;
                var uses = Enumerable.Range(0, 3).Select(k => edges[Edge(a[k], a[(k + 1) % 3])]).OrderBy(n => n).ToArray();
                if (uses[0] != 2 || uses[1] != 2 || uses[2] != 4) continue;
                remove.Add(pair[0]); remove.Add(pair[1]);
            }
            if (remove.Count == 0) return mesh;
            var keep = Enumerable.Range(0, faces.Length).Where(i => !remove.Contains(i)).ToArray();
            return new MeshShape { name = mesh.name, instanceId = mesh.instanceId, sourceFile = mesh.sourceFile, sourceSha256 = mesh.sourceSha256,
                vertices = mesh.vertices, normals = mesh.normals, nativeNormalBindings = mesh.nativeNormalBindings, triangles = keep.Select(i => mesh.triangles[i]).ToArray(), rgb = keep.Select(i => mesh.rgb[i]).ToArray() };
        }
        public static MeshShape Mesh(byte[] cgr,string name,string instance,string source,string hash)
        {
            return MeshFromGraph(CgrSceneGraph(cgr),name,instance,source,hash);
        }
        internal static byte[] CgrSceneGraph(byte[] cgr)
        {
            var records=Container(cgr);var owners=records.Where(r=>r.Kind==1&&r.Name=="SceneGraph").Select(r=>r.Id).ToArray();
            return records.Single(r=>r.Kind==2&&r.Name=="MainDataStream"&&owners.Contains(r.Parent)).Data;
        }
        internal static MeshShape MeshFromGraph(byte[] graph,string name,string instance,string source,string hash)
        {
            // Initial Part cache adapter accepts an A3 root only. Scene branches need
            // explicit matrix/reference expansion; never silently flatten them.
            if(graph.Length<6||graph[0]!=0xa3)throw new NotSupportedException("Native cache scene branches require matrix expansion");
            var env=new Cursor(graph,1,graph.Length);int envelope=env.Count();if(envelope+2!=graph.Length)throw new InvalidDataException("Incomplete native A3 cache");
            var vs=new List<double[]>();var ts=new List<int[]>();var colors=new List<byte[]>();var normals=new List<double[]>();double[][] leafNormals=null;byte[] signature={255,255,2,0,1};int search=0,leaves=0;
            while(true)
            {
                int start=Find(graph,signature,search,graph.Length);if(start<0)break;int header=start-41;
                // The five-byte marker can also occur inside packed coordinate
                // words; only an envelope header confirms a leaf. Stray markers are
                // data noise and must not invalidate the leaves that did parse.
                if(header<0||graph[header]!=149||graph[header+1]!=255){search=start+1;continue;}
                long endLong=(long)header+1+BitConverter.ToUInt32(graph,header+2);if(endLong>graph.Length||endLong<=start)throw new InvalidDataException("Invalid mesh range");
                int end=(int)endLong;
                var cur=new Cursor(graph,start+5,end);int nv=cur.Count(),secondary=cur.Count();
                // Single-leaf caches carry the whole part: index width follows the
                // vertex count (1/2/3 bytes), it is not a per-part constant.
                if(nv<3||nv>4000000)throw new NotSupportedException("Native coordinate counts/layout（顶点 "+nv+"，第二组 "+secondary+"）");
                for(int i=0;i<4;i++)if(cur.Byte()!=0)throw new NotSupportedException("Native coordinate prefix");
                int bitmapBytes=(nv+3)/4;cur.Need(bitmapBytes);var bitmap=Slice(graph,cur.P,bitmapBytes);cur.P+=bitmapBytes;
                int valueCount=cur.Count();int valuesEnd=checked(cur.P+4*valueCount);cur.Need(4*valueCount);var local=new List<double[]>();
                for(int i=0;i<nv;i++)
                {
                    int code=(bitmap[i/4]>>(2*(i%4)))&3;var p=i==0?new double[3]:(double[])local[i-1].Clone();
                    // Codes 1/2/3 reuse the previous vertex. The first vertex starts
                    // from the zero point, so a cache may legitimately begin with
                    // code 2/3 (X stays 0) or code 1 (origin); the value count below
                    // verifies that the bitmap and payload agree.
                    if(code==0){p[0]=cur.Float();p[1]=cur.Float();p[2]=cur.Float();}
                    else if(code==2)p[2]=cur.Float();
                    else if(code==3){p[1]=cur.Float();p[2]=cur.Float();}
                    local.Add(p);
                }
                if(cur.P!=valuesEnd)throw new InvalidDataException("Coordinate bitmap value mismatch");
                if(secondary!=0)
                {
                    // 缓存自带的法向：每顶点 4 bit 主轴码 + 两个 int16 分量，
                    // 与 ExportByColor/Cfv3Encoder 的 V3 编码一致（6..11 为轴向法向）。
                    int words=cur.Count(),bitmapLength=(secondary+1)/2;cur.Need(checked(words*2+bitmapLength));
                    int shorts=cur.P,codes=cur.P+words*2,word=0;
                    leafNormals=new double[secondary][];
                    for(int i=0;i<secondary;i++)
                    {
                        int code=(graph[codes+i/2]>>(4*(i%2)))&15;
                        if(code>11)throw new NotSupportedException("Native normal nibble");
                        double nx,ny,nz;
                        if(code>=6)
                        {
                            nx=code==6?1:(code==7?-1:0);
                            ny=code==8?1:(code==9?-1:0);
                            nz=code==10?1:(code==11?-1:0);
                        }
                        else
                        {
                            if(word+2>words)throw new InvalidDataException("Native normal word accounting");
                            double a=BitConverter.ToInt16(graph,shorts+word*2)/32767.0;
                            double b=BitConverter.ToInt16(graph,shorts+(word+1)*2)/32767.0;
                            word+=2;
                            double major=Math.Sqrt(Math.Max(0.0,1.0-a*a-b*b));
                            if((code&1)!=0)major=-major;
                            int axis=code>>1;
                            if(axis==0){nx=major;ny=a;nz=b;}
                            else if(axis==1){ny=major;nx=a;nz=b;}
                            else{nz=major;nx=a;ny=b;}
                        }
                        double length=Math.Sqrt(nx*nx+ny*ny+nz*nz);
                        leafNormals[i]=length>0?new[]{nx/length,ny/length,nz/length}:null;
                    }
                    if(word!=words)throw new InvalidDataException("Native normal word accounting");
                    cur.P=codes+bitmapLength;
                }
                else leafNormals=null;
                // A zero normal count omits the word-count field and nibble map.
                if(cur.Float()<0)throw new InvalidDataException("Negative native tessellation tolerance");
                // Transition kinds 2..5 share one primitive layout; 5 only changes
                // the tessellation variant, not the record framing.
                if(cur.Byte()!=0||!new[]{2,3,4,5}.Contains(cur.Byte())||cur.Byte()!=2||cur.Byte()!=10)throw new NotSupportedException("Native primitive transition");
                int domains=cur.Count();if(domains<1||domains>cur.End-cur.P)throw new InvalidDataException("Native primitive domains exceed remaining data");
                var groups=new List<List<int[]>>();var domainNormals=new List<double[]>();int width=nv>65535?3:nv>255?2:1;
                for(int domain=0;domain<domains;domain++)
                {
                    if(cur.Byte()!=1)throw new NotSupportedException("Native primitive opcode");int flags=cur.Byte();if(flags<65||flags>79)throw new NotSupportedException("Native primitive descriptor");
                    int singles=(flags&1)!=0?cur.Count():0,strips=(flags&2)!=0?cur.Count():0,fans=(flags&4)!=0?cur.Count():0,total=cur.Count();
                    // Bit 3 binds one normal to the entire primitive domain. The
                    // vertex stream may then contain normals belonging to adjacent
                    // curved domains; it must not override this planar normal.
                    double[] domainNormal=null;
                    if((flags&8)!=0)
                    {
                        double x=cur.Float(),y=cur.Float(),z=cur.Float(),length=Math.Sqrt(x*x+y*y+z*z);
                        if(length>0)domainNormal=new[]{x/length,y/length,z/length};
                    }
                    int[] lengths=Enumerable.Range(0,strips+fans).Select(i=>cur.Count()).ToArray();if(singles*3+lengths.Sum()!=total)throw new InvalidDataException("Native index count mismatch");
                    int[] indices=Enumerable.Range(0,total).Select(i=>cur.Index(width)).ToArray();if(indices.Any(i=>i>=nv))throw new InvalidDataException("Native index range（顶点 "+nv+"，宽度 "+width+"）");
                    var faces=new List<int[]>();int offset=0;for(int i=0;i<singles;i++){faces.Add(new[]{indices[offset],indices[offset+1],indices[offset+2]});offset+=3;}
                    for(int g=0;g<lengths.Length;g++)
                    {
                        int n=lengths[g];if(n<3)throw new InvalidDataException("Short native strip/fan");
                        for(int i=2;i<n;i++)faces.Add(g<strips?((i&1)==0?new[]{indices[offset+i-2],indices[offset+i-1],indices[offset+i]}:new[]{indices[offset+i-1],indices[offset+i-2],indices[offset+i]}):new[]{indices[offset],indices[offset+i-1],indices[offset+i]});offset+=n;
                    }
                    groups.Add(faces);domainNormals.Add(domainNormal);
                }
                int vertexBase=vs.Count;vs.AddRange(local);
                if(leafNormals!=null&&leafNormals.Length==local.Count)normals.AddRange(leafNormals);
                else normals.AddRange(Enumerable.Repeat<double[]>(null,local.Count));
                var boundVertices=new Dictionary<Tuple<int,double,double,double>,int>();
                for(int domain=0;domain<groups.Count;domain++)
                {
                    var group=groups[domain];var domainNormal=domainNormals[domain];
                    // The leading style byte differs between caches (0x30/0x20); the
                    // RGB record that follows is identical, so both share one layout.
                    int style=cur.Byte();
                    if((style!=0x30&&style!=0x20)||cur.Byte()!=4||cur.Byte()!=4||cur.Byte()!=255||cur.Byte()!=255)
                        throw new NotSupportedException("Native RGB/alpha layout（样式 "+style+"）");
                    byte b=(byte)cur.Byte(),g=(byte)cur.Byte(),r=(byte)cur.Byte();foreach(var t in group)
                    {
                        var triangle=new int[3];
                        for(int k=0;k<3;k++)
                        {
                            int index=t[k];
                            if(domainNormal==null)triangle[k]=vertexBase+index;
                            else
                            {
                                var key=Tuple.Create(index,domainNormal[0],domainNormal[1],domainNormal[2]);int bound;
                                if(!boundVertices.TryGetValue(key,out bound))
                                {bound=vs.Count;boundVertices.Add(key,bound);vs.Add(local[index]);normals.Add(domainNormal);}
                                triangle[k]=bound;
                            }
                        }
                        ts.Add(triangle);colors.Add(new[]{r,g,b});
                    }
                }
                leaves++;search=end;
            }
            if(leaves==0)throw new NotSupportedException("未从该零件的 CATIA 可视化缓存读到可转换的三角面");
            // Discard only unreferenced records superseded by domain binding.
            var used=ts.SelectMany(t=>t).Distinct().OrderBy(i=>i).ToArray();var remap=new int[vs.Count];
            for(int i=0;i<used.Length;i++)remap[used[i]]=i;
            var mesh=new MeshShape{name=name,instanceId=instance,sourceFile=source,sourceSha256=hash,vertices=used.Select(i=>vs[i]).ToArray(),triangles=ts.Select(t=>t.Select(i=>remap[i]).ToArray()).ToArray(),rgb=colors.ToArray(),
                normals=used.Any(i=>normals[i]!=null)?used.Select(i=>normals[i]).ToArray():null,nativeNormalBindings=used.All(i=>normals[i]!=null)};
            mesh.Validate();
            if(mesh.normals==null)mesh.Smooth();   // 缓存缺法向时才退回几何近似
            else mesh.FillMissingNormals();
            return mesh;
        }
    }
}
