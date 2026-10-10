using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TxTools.ExportByColor
{
    // No PS/COM objects cross the process boundary. No shell or PATH lookup.
    public static class JtDirectBridge
    {
        public sealed class Triangle
        {
            public int[] Indices;
            public byte R,G,B; public byte Alpha = 255;
            public int Surface;
            public float[] Normal;
        }
        public sealed class Mesh
        {
            public readonly List<float[]> Vertices = new List<float[]>();
            public readonly List<Triangle> Faces = new List<Triangle>();
        }
        // Each call owns its decoder process, mesh, output file and log callback.
        // Callers must provide detached placement data and a unique output path.
        public static void ConvertToCgr(string source, string directory, string worker,
            string output, double[] placement, Action<string> log)
        {
            ConvertToCgr(source, directory, worker, output, placement, CgrBackend.Compact, log);
        }

        public static void ConvertToCgr(string source, string directory, string worker,
            string output, double[] placement, CgrBackend backend, Action<string> log)
        {
            if (backend != CgrBackend.Compact && backend != CgrBackend.LineFace && backend != CgrBackend.LineFacePlanar)
                throw new ArgumentException("JT output requires an explicit compact or line/face backend", "backend");
            var mesh = Convert(source, directory, worker, 300000);
            Place(mesh, placement);
            var faces = new List<CgrWriter.Face>(mesh.Faces.Count);
            foreach (var face in mesh.Faces)
                faces.Add(new CgrWriter.Face { Idx=face.Indices, R=face.R, G=face.G, B=face.B,
                    Surface=face.Surface, Opacity=face.Alpha, Nx=face.Normal[0], Ny=face.Normal[1], Nz=face.Normal[2] });
            CgrWriter.BuildFile(mesh.Vertices, faces, output, backend, 20000, log, true);
        }

        public static int ConversionWorkers
        {
            get
            {
                // A 32-bit PS host has limited address space; default to two on 64-bit.
                int limit = Environment.Is64BitProcess ? Math.Min(4, Environment.ProcessorCount) : 1;
                int requested;
                if (!int.TryParse(Environment.GetEnvironmentVariable("TXTOOLS_JT_WORKERS"), out requested)
                    || requested < 1) requested = 2;
                return Math.Max(1, Math.Min(limit, requested));
            }
        }
        public static string ResolveFile(string source)
        {
            source=Path.GetFullPath(source);
            if(File.Exists(source) && string.Equals(Path.GetExtension(source),".jt",StringComparison.OrdinalIgnoreCase))return source;
            if(!Directory.Exists(source))throw new InvalidDataException("直接解码需要 JT 文件或 COJT 目录");
            string[] files=Directory.GetFiles(source,"*.jt",SearchOption.TopDirectoryOnly);
            if(files.Length!=1)throw new InvalidDataException("COJT 目录需要唯一的顶层 JT 文件，实际="+files.Length);
            return files[0];
        }
        private static string Quote(string value)
        {
            var b=new StringBuilder("\"");int slashes=0;
            foreach(char c in value) {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"')b.Append('\\',slashes*2+1);else b.Append('\\',slashes);
                b.Append(c);slashes=0;
            }
            b.Append('\\',slashes*2);return b.Append('"').ToString();
        }
        public static Mesh Convert(string source,string directory,string worker,int timeoutMs)
        {
            source=ResolveFile(source);
            directory=Path.GetFullPath(directory);
            if(!Path.IsPathRooted(worker)||!File.Exists(worker))
                throw new FileNotFoundException("随插件部署的 C# JT 解码器不存在: "+worker);
            Directory.CreateDirectory(directory);
            string output=Path.Combine(directory,Guid.NewGuid().ToString("N")+".jtmesh");
            string log=output+".log";
            string headerInfo=null;
            using(var writer=new StreamWriter(log,false,new UTF8Encoding(false)))
            using(var process=new Process()) {
                object gate=new object();
                process.StartInfo=new ProcessStartInfo(worker,Quote(source)+" "+Quote(output)) {
                    UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                    WorkingDirectory=Path.GetDirectoryName(worker)
                };
                process.OutputDataReceived+=(s,e)=>{if(e.Data!=null)lock(gate){writer.WriteLine(e.Data);if(e.Data.StartsWith("JT_HEADER=",StringComparison.Ordinal))headerInfo=e.Data;}};
                process.ErrorDataReceived+=(s,e)=>{if(e.Data!=null)lock(gate)writer.WriteLine("ERROR "+e.Data);};
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                if(!process.WaitForExit(timeoutMs)) {
                    process.Kill();process.WaitForExit();throw new TimeoutException("JT 解码超时；日志="+log);
                }
                process.WaitForExit();
                if(process.ExitCode==2)throw new NotSupportedException("JT 文件需要 PS 原生兼容转换；"+(headerInfo??"版本未提供")+"；日志="+log);
                if(process.ExitCode!=0)throw new InvalidDataException("JT 解码失败，退出码="+process.ExitCode+"；日志="+log);
            }
            return Read(output,source);
        }
        private static float Finite(float v)
        {
            if(float.IsNaN(v)||float.IsInfinity(v))throw new InvalidDataException("非有限网格数据");return v;
        }
        public static Mesh Read(string path,string source)
        {
            using(var r=new BinaryReader(File.OpenRead(path))) {
                string magic=Encoding.ASCII.GetString(r.ReadBytes(8)); uint version=r.ReadUInt32();
                bool rgba=magic=="JTMESH02"&&version==2;
                if(!rgba && !(magic=="JTMESH01"&&version==1))throw new InvalidDataException("JT mesh 协议不支持");
                byte[] expected=r.ReadBytes(32),actual;
                using(var h=SHA256.Create())using(var s=File.OpenRead(source))actual=h.ComputeHash(s);
                if(expected.Length!=32)throw new InvalidDataException("摘要截断");
                for(int i=0;i<32;i++)if(expected[i]!=actual[i])throw new InvalidDataException("JT 文件已变化或网格来源不匹配");
                uint nv=r.ReadUInt32(),nf=r.ReadUInt32();
                if(nv==0||nf==0||nv>5000000||nf>5000000||r.BaseStream.Length!=52L+nv*12L+nf*(rgba?32L:31L))
                    throw new InvalidDataException("网格数量、长度或 500 万限制不符合要求");
                var mesh=new Mesh();
                for(uint i=0;i<nv;i++)mesh.Vertices.Add(new[]{Finite(r.ReadSingle()),Finite(r.ReadSingle()),Finite(r.ReadSingle())});
                for(uint i=0;i<nf;i++) {
                    int[] ids={r.ReadInt32(),r.ReadInt32(),r.ReadInt32()};
                    foreach(int id in ids)if(id<0||id>=nv)throw new InvalidDataException("网格索引越界");
                    var face=new Triangle{Indices=ids,R=r.ReadByte(),G=r.ReadByte(),B=r.ReadByte(),Alpha=rgba?r.ReadByte():(byte)255,Surface=r.ReadInt32(),
                        Normal=new[]{Finite(r.ReadSingle()),Finite(r.ReadSingle()),Finite(r.ReadSingle())}};
                    if(face.Surface<=0)throw new InvalidDataException("缺少 Shape 面域");mesh.Faces.Add(face);
                }
                return mesh;
            }
        }
        public static void Place(Mesh mesh,double[] m)
        {
            if(m==null||m.Length!=16)throw new ArgumentException("放置矩阵需 16 项");
            foreach(double v in m)if(double.IsNaN(v)||double.IsInfinity(v))throw new ArgumentException("放置矩阵非有限");
            if(Math.Abs(m[3])+Math.Abs(m[7])+Math.Abs(m[11])+Math.Abs(m[15]-1)>1e-8)throw new ArgumentException("非仿射放置矩阵");
            for(int i=0;i<3;i++)for(int j=0;j<3;j++) {
                double dot=0;for(int k=0;k<3;k++)dot+=m[i*4+k]*m[j*4+k];
                if(Math.Abs(dot-(i==j?1:0))>1e-5)throw new ArgumentException("放置矩阵暂仅支持刚体变换");
            }
            double det=m[0]*(m[5]*m[10]-m[6]*m[9])-m[1]*(m[4]*m[10]-m[6]*m[8])+m[2]*(m[4]*m[9]-m[5]*m[8]);
            if(det<0)throw new ArgumentException("暂不支持镜像资源放置");
            foreach(var p in mesh.Vertices) {
                double x=p[0],y=p[1],z=p[2];
                for(int k=0;k<3;k++)p[k]=Finite((float)(x*m[k]+y*m[4+k]+z*m[8+k]+m[12+k]));
            }
            foreach(var f in mesh.Faces) {
                double x=f.Normal[0],y=f.Normal[1],z=f.Normal[2];
                for(int k=0;k<3;k++)f.Normal[k]=Finite((float)(x*m[k]+y*m[4+k]+z*m[8+k]));
            }
        }
    }
}
