using System;
using System.IO;

// Experimental CGR type-13 canonical cylinder attribute, validated against
// CATIA exports. It describes a graphic face; it does not replace display mesh.
public static class CgrCanonicalCylinderCodec
{
 public sealed class Cylinder { public double[] Center,Axis; public double Radius; }
 static void BE(BinaryWriter w,float value){var bytes=BitConverter.GetBytes(value);if(BitConverter.IsLittleEndian)Array.Reverse(bytes);w.Write(bytes);}
 static float ReadBE(byte[] b,int p){var bytes=new[]{b[p+3],b[p+2],b[p+1],b[p]};if(!BitConverter.IsLittleEndian)Array.Reverse(bytes);return BitConverter.ToSingle(bytes,0);}
 static bool Finite(double d){return !double.IsNaN(d)&&!double.IsInfinity(d);}
 public static byte[] Encode(Cylinder value){
  if(value==null||value.Center==null||value.Axis==null||value.Center.Length!=3||value.Axis.Length!=3||!Finite(value.Radius)||value.Radius<=0||value.Radius>float.MaxValue)throw new ArgumentException("Invalid cylinder");
  double norm=0;for(int k=0;k<3;k++){if(!Finite(value.Center[k])||Math.Abs(value.Center[k])>float.MaxValue||!Finite(value.Axis[k]))throw new ArgumentException("Invalid cylinder coordinates");norm+=value.Axis[k]*value.Axis[k];}
  norm=Math.Sqrt(norm);if(!Finite(norm)||norm==0)throw new ArgumentException("Invalid axis");
  using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream)){
   w.Write((ushort)26);w.Write(new byte[]{0x33,0x33});
   for(int k=0;k<3;k++)BE(w,(float)value.Center[k]);
   BE(w,(float)(value.Axis[0]/norm));BE(w,(float)(value.Axis[1]/norm));
   BE(w,(float)(value.Axis[2]<0?-value.Radius:value.Radius));return stream.ToArray();
  }
 }
 public static Cylinder Decode(byte[] bytes,int offset){
  if(bytes==null||offset<0||offset>bytes.Length-28||bytes[offset]!=26||bytes[offset+1]!=0||bytes[offset+2]!=0x33||bytes[offset+3]!=0x33)throw new InvalidDataException("Not a supported canonical cylinder attribute");
  var center=new double[3];for(int k=0;k<3;k++)center[k]=ReadBE(bytes,offset+4+k*4);
  double x=ReadBE(bytes,offset+16),y=ReadBE(bytes,offset+20),signed=ReadBE(bytes,offset+24),remainder=1-x*x-y*y;
  if(remainder< -2e-7||signed==0||!Finite(signed)||!Finite(x)||!Finite(y))throw new InvalidDataException("Invalid canonical cylinder axis/radius");
  foreach(double c in center)if(!Finite(c))throw new InvalidDataException("Invalid canonical center");
  return new Cylinder{Center=center,Axis=new[]{x,y,(signed<0?-1:1)*Math.Sqrt(Math.Max(0,remainder))},Radius=Math.Abs(signed)};
 }
}
