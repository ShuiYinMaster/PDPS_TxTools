using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
namespace TxTools.ExportByColor
{
 public sealed class ExportNames
 {
  readonly HashSet<string> used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  public string Next(string name)
  {
   var b=new StringBuilder(); foreach(char c in name??"") b.Append(c<32||Array.IndexOf(Path.GetInvalidFileNameChars(),c)>=0||char.IsWhiteSpace(c)?'_':c);
   string stem=b.ToString().Trim(' ','.');if(stem.Length==0)stem="Unnamed";if(stem.Length>80)stem=stem.Substring(0,80);
   string first=stem.Split('.')[0].ToUpperInvariant();if(first=="CON"||first=="PRN"||first=="AUX"||first=="NUL"||System.Text.RegularExpressions.Regex.IsMatch(first,"^(COM|LPT)[0-9]$"))stem="_"+stem;
   string result=stem;int i=2;while(!used.Add(result))result=stem+"_"+(i++).ToString("D3");return result;
  }
 }
 public static class MeshExport
 {
  public sealed class Part { public string Device;public byte R,G,B;public int Count;public Func<IEnumerable<float[]>> Read;public Func<int,int,IEnumerable<float[]>> ReadRange;public long Offset; }
  public sealed class Archive:IDisposable
  {
   readonly string spool;readonly string checkpoint;int checkpointWrites;bool keep;readonly List<Part> parts=new List<Part>();public int Devices{get;private set;}
   public Archive(string directory){spool=Path.Combine(directory,".mesh_"+Guid.NewGuid().ToString("N")+".partial");checkpoint=spool+".jsonl";using(File.Create(spool)){} }
   public void Add(DeviceData device,string name)
   {
    var added=new List<Part>();
    using(var w=new BinaryWriter(new FileStream(spool,FileMode.Append,FileAccess.Write,FileShare.Read,1024*1024,FileOptions.SequentialScan)))foreach(var g in device.Colors)
    {
     if(g.Tris.Count==0)continue;long offset=w.BaseStream.Position;int count=g.Tris.Count;
     foreach(var t in g.Tris){Validate(t);foreach(float v in t)w.Write(v);}
     added.Add(new Part{Device=name,R=g.R,G=g.G,B=g.B,Count=count,Offset=offset,Read=()=>ReadSpool(spool,offset,count),ReadRange=(first,n)=>ReadSpool(spool,offset+36L*first,n)});
    }
    parts.AddRange(added);if(added.Count>0){Devices++;AppendCheckpoint(name,added);}
   }
   void AppendCheckpoint(string device,List<Part> added)
   {
    using(var s=new FileStream(checkpoint,FileMode.Append,FileAccess.Write,FileShare.Read))
    using(var w=new StreamWriter(s,new UTF8Encoding(false)))
    { foreach(var p in added) w.WriteLine("{\"device\":"+J(device)+",\"offset\":"+p.Offset+",\"triangles\":"+p.Count+",\"rgb\":["+p.R+","+p.G+","+p.B+"]}"); w.Flush(); checkpointWrites++; if((checkpointWrites&3)==0)s.Flush(true); }
   }
   public string Finish(string dir,string name,string format,string origin){return WriteParts(parts,name,dir,format,origin);}
   public string PreserveForRecovery()
   {
    keep=true;
    using(var w=new StreamWriter(spool+".json",false,new UTF8Encoding(false)))
    {
     w.Write("{\"schema\":\"TxTools.MeshSpool\",\"version\":1,\"spool\":"+J(Path.GetFileName(spool))+",\"parts\":[");bool first=true;
     foreach(var p in parts){if(!first)w.Write(',');first=false;w.Write("{\"device\":"+J(p.Device)+",\"offset\":"+p.Offset+",\"triangles\":"+p.Count+",\"rgb\":["+p.R+","+p.G+","+p.B+"]}");}w.Write("]}");
    }
    return spool+".json";
   }
   public void Dispose(){if(!keep&&File.Exists(spool))File.Delete(spool);if(!keep&&File.Exists(checkpoint))File.Delete(checkpoint);}
  }
  static IEnumerable<float[]> ReadList(List<float[]> source,int offset,int count)
  {for(int i=0;i<count;i++)yield return source[offset+i];}
  static IEnumerable<float[]> ReadSpool(string path,long offset,int count)
  {using(var r=new BinaryReader(File.OpenRead(path))){r.BaseStream.Position=offset;for(int i=0;i<count;i++){var t=new float[9];for(int k=0;k<9;k++)t[k]=r.ReadSingle();yield return t;}}}
  static void Validate(float[] t){if(t==null||t.Length!=9)throw new ArgumentException("三角面必须有 9 个坐标值");foreach(float v in t)if(float.IsNaN(v)||float.IsInfinity(v))throw new ArgumentException("非有限网格坐标");}
  internal static string F(double v){return v.ToString("R",CultureInfo.InvariantCulture);}
  internal static IEnumerable<float[]> Checked(Part p){int n=0;foreach(var t in p.Read()){Validate(t);n++;yield return t;}if(n!=p.Count)throw new InvalidDataException("面数与声明不一致");}
  internal static long Total(List<Part> parts){long n=0;foreach(var p in parts)n+=p.Count;return n;}
  public static string Write(DeviceData device,string name,string directory,string format,string origin)
  {
   var parts=new List<Part>();foreach(var g in device.Colors)if(g.Tris.Count>0)parts.Add(new Part{Device=name,R=g.R,G=g.G,B=g.B,Count=g.Tris.Count,Read=()=>g.Tris,ReadRange=(first,n)=>ReadList(g.Tris,first,n)});return WriteParts(parts,name,directory,format,origin);
  }
  static string WriteParts(List<Part> parts,string name,string directory,string format,string origin)
  {
   if(Total(parts)==0)throw new InvalidDataException("无有效三角面");Directory.CreateDirectory(directory);
   string ext=format.StartsWith("FBX",StringComparison.Ordinal)?"fbx":format.ToLowerInvariant(),path=Path.Combine(directory,name+"."+ext),temp=path+".partial";
   if(File.Exists(path)||File.Exists(temp))throw new IOException("文件已存在: "+path);
   try
   {
    if(format=="STL")WriteStl(parts,temp);else if(format=="OBJ")WriteObj(parts,name,directory,temp);else if(format=="PLY")WritePly(parts,temp);
    else if(format=="FBX"||format=="FBX_BINARY"||format=="FBX_ASCII")FbxExport.Write(parts,temp,format!="FBX_ASCII");else throw new ArgumentException("不支持的网格格式");
    if(format=="STL")WriteRanges(parts,path+".rgb.json",origin);File.Move(temp,path);return path;
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
  static string J(string s){var b=new StringBuilder("\"");foreach(char c in s??""){if(c=='"'||c=='\\')b.Append('\\').Append(c);else if(c<32)b.Append("\\u").Append(((int)c).ToString("x4"));else b.Append(c);}return b.Append('"').ToString();}
  static void WriteRanges(List<Part> parts,string path,string origin)
  {
   using(var w=new StreamWriter(new FileStream(path,FileMode.CreateNew),new UTF8Encoding(false)))
   {
    w.Write("{\"schema\":\"TxTools.MeshColors\",\"version\":1,\"units\":\"mm\",\"origin\":"+J(origin)+",\"triangleCount\":"+Total(parts)+",\"ranges\":[");long offset=0;bool first=true;
    foreach(var p in parts){if(!first)w.Write(',');first=false;w.Write("{\"device\":"+J(p.Device)+",\"firstTriangle\":"+offset+",\"triangleCount\":"+p.Count+",\"rgb\":["+p.R+","+p.G+","+p.B+"]}");offset+=p.Count;}w.Write("]}");
   }
  }
  static void WriteObj(List<Part> parts,string name,string directory,string path)
  {
   using(var w=new StreamWriter(path,false,new UTF8Encoding(false)))using(var m=new StreamWriter(new FileStream(Path.Combine(directory,name+".mtl"),FileMode.CreateNew),new UTF8Encoding(false)))
   {
    w.WriteLine("# PS coordinates in millimetres\nmtllib "+name+".mtl");long vertex=1;int index=0;string current=null;
    foreach(var p in parts)
    {
     string mat="RGB_"+index++;m.WriteLine("newmtl "+mat+"\nKd "+F(p.R/255.0)+" "+F(p.G/255.0)+" "+F(p.B/255.0)+"\nd 1\nillum 1");
     if(current!=p.Device){current=p.Device;w.WriteLine("o "+current);}w.WriteLine("g "+p.Device+"_"+mat+"\nusemtl "+mat);
     foreach(var t in Checked(p)){for(int k=0;k<9;k+=3)w.WriteLine("v "+F(t[k])+" "+F(t[k+1])+" "+F(t[k+2]));w.WriteLine("f "+vertex+" "+(vertex+1)+" "+(vertex+2));vertex+=3;}
    }
   }
  }
  static void WritePly(List<Part> parts,string path)
  {
   long count=Total(parts);if(count*3>int.MaxValue)throw new ArgumentException("PLY 索引超出范围");
   using(var w=new StreamWriter(path,false,Encoding.ASCII))
   {
    w.WriteLine("ply\nformat ascii 1.0\ncomment PS coordinates in millimetres\nelement vertex "+count*3+"\nproperty float x\nproperty float y\nproperty float z\nelement face "+count+"\nproperty list uchar int vertex_indices\nproperty uchar red\nproperty uchar green\nproperty uchar blue\nend_header");
    foreach(var p in parts)foreach(var t in Checked(p))for(int k=0;k<9;k+=3)w.WriteLine(F(t[k])+" "+F(t[k+1])+" "+F(t[k+2]));
    long vertex=0;foreach(var p in parts)for(int i=0;i<p.Count;i++){w.WriteLine("3 "+vertex+" "+(vertex+1)+" "+(vertex+2)+" "+p.R+" "+p.G+" "+p.B);vertex+=3;}
   }
  }
  static void WriteStl(List<Part> parts,string path)
  {
   long count=Total(parts);if(count>uint.MaxValue)throw new ArgumentException("STL 面数超出范围");
   using(var w=new BinaryWriter(File.Create(path)))
   {
    w.Write(new byte[80]);w.Write((uint)count);foreach(var p in parts)foreach(var t in Checked(p))
    {
     double ax=(double)t[3]-t[0],ay=(double)t[4]-t[1],az=(double)t[5]-t[2],bx=(double)t[6]-t[0],by=(double)t[7]-t[1],bz=(double)t[8]-t[2];double x=ay*bz-az*by,y=az*bx-ax*bz,z=ax*by-ay*bx,len=Math.Sqrt(x*x+y*y+z*z);
     w.Write(len>0?(float)(x/len):0f);w.Write(len>0?(float)(y/len):0f);w.Write(len>0?(float)(z/len):0f);foreach(float v in t)w.Write(v);w.Write((ushort)0);
    }
   }
  }
 }
}
