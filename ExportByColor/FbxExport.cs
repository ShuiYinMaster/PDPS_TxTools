using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace TxTools.ExportByColor
{
 // Binary FBX 7500 (64-bit offsets), ASCII FBX 7400 static meshes. Arrays are streamed uncompressed; no Blender/SDK runtime dependency.
 public static class FbxExport
 {
  sealed class Name {public string Text,Kind;public Name(string text,string kind){Text=text;Kind=kind;}}
  sealed class ArrayValue {public char Type;public int Count;public Func<IEnumerable<double>> Values;}
  internal sealed class Writer:IDisposable
  {
   readonly BinaryWriter binary;readonly StreamWriter ascii;int depth;
   public Writer(string path,bool bin):this(File.Create(path),bin) {}
   internal Writer(Stream stream,bool bin)
   {
    if(bin){binary=new BinaryWriter(stream);binary.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0"));binary.Write(7500);}
    else{ascii=new StreamWriter(stream,new UTF8Encoding(false));ascii.WriteLine("; FBX 7.4.0 project file");}
   }
   public void N(string name,params object[] props){B(name,null,props);}
   public void B(string name,Action children,params object[] props)
   {
    if(binary!=null)
    {
     long start=binary.BaseStream.Position;binary.Write(new byte[24]);byte[] key=Encoding.UTF8.GetBytes(name);binary.Write(checked((byte)key.Length));binary.Write(key);long p=binary.BaseStream.Position;
     try { foreach(var value in props)Property(value); } catch(Exception ex) { throw new InvalidDataException("FBX 节点 "+name+"，文件偏移 "+start+": "+ex.Message,ex); } long length=binary.BaseStream.Position-p;
     if(children!=null){children();binary.Write(new byte[25]);}
     long end=binary.BaseStream.Position;binary.BaseStream.Position=start;
     binary.Write(checked((ulong)end));binary.Write((ulong)props.Length);binary.Write(checked((ulong)length));binary.BaseStream.Position=end;
    }
    else
    {
     ascii.Write(new string(' ',depth));ascii.Write(name+": ");
     for(int i=0;i<props.Length;i++){if(i>0)ascii.Write(", ");TextProperty(props[i]);}
     if(children!=null){ascii.WriteLine(" {");depth++;children();depth--;ascii.Write(new string(' ',depth));ascii.WriteLine("}");}else ascii.WriteLine();
    }
   }
   void Property(object value)
   {
    var a=value as ArrayValue;
    if(a!=null)
    {
     binary.Write((byte)a.Type);binary.Write(a.Count);binary.Write(0);binary.Write(checked((uint)((long)a.Count*(a.Type=='d'?8:4))));int count=0;
     foreach(double x in a.Values()){if(a.Type=='d')binary.Write(x);else binary.Write(checked((int)x));count++;}if(count!=a.Count)throw new InvalidDataException("FBX 数组计数不一致");return;
    }
    var n=value as Name;if(n!=null)value=n.Text+"\0\x01"+n.Kind;
    if(value is string){binary.Write((byte)'S');var b=Encoding.UTF8.GetBytes((string)value);binary.Write(b.Length);binary.Write(b);}
    else if(value is long){binary.Write((byte)'L');binary.Write((long)value);}
    else if(value is int){binary.Write((byte)'I');binary.Write((int)value);}
    else if(value is bool){binary.Write((byte)'C');binary.Write((byte)((bool)value?1:0));}
    else{binary.Write((byte)'D');binary.Write(Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture));}
   }
   void TextProperty(object value)
   {
    var a=value as ArrayValue;if(a!=null){ascii.Write("*"+a.Count+" { a: ");bool first=true;int count=0;foreach(double x in a.Values()){if(!first)ascii.Write(',');first=false;ascii.Write(MeshExport.F(x));count++;}if(count!=a.Count)throw new InvalidDataException("FBX 数组计数不一致");ascii.Write(" }");return;}
    var n=value as Name;if(n!=null)value=n.Kind+"::"+n.Text;
    if(value is string)ascii.Write("\""+((string)value).Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n"," ").Replace("\r"," ")+"\"");
    else if(value is bool)ascii.Write((bool)value?"T":"F");else ascii.Write(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture));
   }
   public void Finish()
   {
    if(binary==null)return;binary.Write(new byte[25]);binary.Write(new byte[]{0xfa,0xbc,0xab,0x09,0xd0,0xc8,0xd4,0x66,0xb1,0x76,0xfb,0x83,0x1c,0xf7,0x26,0x7e});binary.Write(0);
    int padding=16-(int)(binary.BaseStream.Position%16);binary.Write(new byte[padding]);binary.Write(7500);binary.Write(new byte[120]);binary.Write(new byte[]{0xf8,0x5a,0x8c,0x6a,0xde,0xf5,0xd9,0x7e,0xec,0xe9,0x0c,0xe3,0x75,0x8f,0x29,0x0b});
   }
   public void Dispose(){if(binary!=null)binary.Dispose();if(ascii!=null)ascii.Dispose();}
  }
  static IEnumerable<double> Coordinates(MeshExport.Part p){foreach(var t in MeshExport.Checked(p))foreach(float v in t)yield return v;}
  static IEnumerable<double> Indices(MeshExport.Part p){for(int i=0;i<p.Count;i++){yield return i*3;yield return i*3+1;yield return -(i*3+3);}}
  static IEnumerable<double> Zero(){yield return 0;}
  static void Transform(Writer w)
  {
   w.B("Properties70",()=>{w.N("P","Lcl Translation","Lcl Translation","","A",0.0,0.0,0.0);w.N("P","Lcl Rotation","Lcl Rotation","","A",0.0,0.0,0.0);w.N("P","Lcl Scaling","Lcl Scaling","","A",1.0,1.0,1.0);});
  }
  internal static List<MeshExport.Part> SplitParts(List<MeshExport.Part> input,int limit=1000000)
  {
   if(limit<1)throw new ArgumentOutOfRangeException("limit");
   var result=new List<MeshExport.Part>();
   foreach(var part in input)
   {
    if(part.Count<=limit){result.Add(part);continue;}
    if(part.ReadRange==null)throw new InvalidDataException("大网格缺少分段读取器: "+part.Device);
    for(int offset=0;offset<part.Count;)
    {
     int first=offset,count=Math.Min(limit,part.Count-offset);
     result.Add(new MeshExport.Part{Device=part.Device,R=part.R,G=part.G,B=part.B,Count=count,Read=()=>part.ReadRange(first,count)});
     offset+=count;
    }
   }
   return result;
  }
  public static void Write(List<MeshExport.Part> parts,string path,bool binary)
  {
   parts=SplitParts(parts);
   var devices=new Dictionary<string,long>(StringComparer.Ordinal);long next=1000;foreach(var p in parts)if(!devices.ContainsKey(p.Device))devices.Add(p.Device,next++);
   using(var w=new Writer(path,binary))
   {
    w.B("FBXHeaderExtension",()=>{w.N("FBXHeaderVersion",1003);w.N("FBXVersion",binary?7500:7400);w.N("Creator","TxTools R30");});
    w.B("GlobalSettings",()=>{w.N("Version",1000);w.B("Properties70",()=>{
     w.N("P","UpAxis","int","Integer","",2);w.N("P","UpAxisSign","int","Integer","",1);w.N("P","FrontAxis","int","Integer","",1);w.N("P","FrontAxisSign","int","Integer","",-1);w.N("P","CoordAxis","int","Integer","",0);w.N("P","CoordAxisSign","int","Integer","",1);w.N("P","UnitScaleFactor","double","Number","",0.1);w.N("P","OriginalUnitScaleFactor","double","Number","",0.1);
    });});
    w.B("Documents",()=>{w.N("Count",1);w.B("Document",()=>{w.B("Properties70",()=>{});w.N("RootNode",0L);},1L,"Scene","Scene");});
    w.B("Definitions",()=>{w.N("Version",100);w.N("Count",devices.Count+parts.Count*3);w.B("ObjectType",()=>w.N("Count",devices.Count+parts.Count),"Model");w.B("ObjectType",()=>w.N("Count",parts.Count),"Geometry");w.B("ObjectType",()=>w.N("Count",parts.Count),"Material");});
    long objectStart=next;
    w.B("Objects",()=>{
     foreach(var d in devices)w.B("Model",()=>{w.N("Version",232);Transform(w);},d.Value,new Name(d.Key,"Model"),"Null");
     for(int i=0;i<parts.Count;i++)
     {
      var p=parts[i];long geometry=objectStart+i*3L,model=geometry+1,material=geometry+2;string name=p.Device+"_RGB_"+i;
      w.B("Geometry",()=>{
       w.N("GeometryVersion",124);w.N("Vertices",new ArrayValue{Type='d',Count=checked(p.Count*9),Values=()=>Coordinates(p)});w.N("PolygonVertexIndex",new ArrayValue{Type='i',Count=checked(p.Count*3),Values=()=>Indices(p)});
       w.B("LayerElementMaterial",()=>{w.N("Version",101);w.N("Name","");w.N("MappingInformationType","AllSame");w.N("ReferenceInformationType","IndexToDirect");w.N("Materials",new ArrayValue{Type='i',Count=1,Values=Zero});},0);
       w.B("Layer",()=>{w.N("Version",100);w.B("LayerElement",()=>{w.N("Type","LayerElementMaterial");w.N("TypedIndex",0);});},0);
      },geometry,new Name(name,"Geometry"),"Mesh");
      w.B("Model",()=>{w.N("Version",232);Transform(w);w.N("Shading",true);w.N("Culling","CullingOff");},model,new Name(name,"Model"),"Mesh");
      w.B("Material",()=>{w.N("Version",102);w.N("ShadingModel","lambert");w.N("MultiLayer",0);w.B("Properties70",()=>{w.N("P","DiffuseColor","Color","","A",p.R/255.0,p.G/255.0,p.B/255.0);w.N("P","DiffuseFactor","Number","","A",1.0);});},material,new Name(name,"Material"),"");
     }
    });
    w.B("Connections",()=>{foreach(var d in devices)w.N("C","OO",d.Value,0L);for(int i=0;i<parts.Count;i++){long g=objectStart+i*3L;w.N("C","OO",g+1,devices[parts[i].Device]);w.N("C","OO",g,g+1);w.N("C","OO",g+2,g+1);}});
    w.Finish();
   }
  }
 }
}
