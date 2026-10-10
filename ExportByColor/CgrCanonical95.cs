using System;
using System.Collections.Generic;
using System.IO;
namespace TxTools.ExportByColor
{
    public static partial class CgrWriter
    {
        public static void BuildFileWithCylinders(List<float[]> vertices,List<Face> faces,string output,
            Dictionary<int,RoundHoleCgrMesh.Cylinder> cylinders,Action<string> log=null)
        {
            if(vertices==null||faces==null||cylinders==null)throw new ArgumentNullException("mesh/cylinders");
            faces=FilterZeroArea(vertices,faces,log);
            // Pure mesh/parameter data stays local to the conversion invocation.
            BuildLineFace95(vertices,faces,output,int.MaxValue,log,true,cylinders);
            if(log!=null)log("[CGR canonical] cylindrical source groups="+cylinders.Count+"; direct CGR only; no reference axes or CATPart companions");
        }
        private static void CanonicalFloat95(BinaryWriter w,double value)
        {var bytes=BitConverter.GetBytes(Finite(value));if(BitConverter.IsLittleEndian)Array.Reverse(bytes);w.Write(bytes);}
        private static void WriteCanonicalCylinder95(BinaryWriter w,RoundHoleCgrMesh.Cylinder cylinder)
        {
            double n=0;foreach(double value in cylinder.Axis)n+=value*value;n=Math.Sqrt(n);
            if(n<=0||double.IsNaN(n)||double.IsInfinity(n)||cylinder.Radius<=0)throw new InvalidDataException("Invalid analytic cylinder");
            // Native CATIA type-13 attribute: 26-byte value includes tag 33 33.
            // Z's sign is stored in the signed radius; its magnitude follows from unit X/Y.
            w.Write((ushort)26);w.Write(new byte[]{0x33,0x33});
            foreach(double value in cylinder.Center)CanonicalFloat95(w,value);
            CanonicalFloat95(w,cylinder.Axis[0]/n);CanonicalFloat95(w,cylinder.Axis[1]/n);
            CanonicalFloat95(w,cylinder.Axis[2]<0?-cylinder.Radius:cylinder.Radius);
        }
        private static bool WriteCanonicalCircle95(BinaryWriter w,RoundHoleCgrMesh.Cylinder cylinder,FeatureBoundary95 edge,List<float[]> vertices)
        {
            if(edge.Polyline==null||edge.Polyline.Count<4||edge.Polyline[0]!=edge.Polyline[edge.Polyline.Count-1])return false;
            double[] center=null;
            foreach(var rim in new[]{cylinder.RimA,cylinder.RimB})
            {
                bool valid=true;foreach(int id in edge.Polyline)
                {
                    var p=vertices[id];double dx=p[0]-rim[0],dy=p[1]-rim[1],dz=p[2]-rim[2];
                    if(Math.Abs(dx*cylinder.Axis[0]+dy*cylinder.Axis[1]+dz*cylinder.Axis[2])>.01||Math.Abs(Math.Sqrt(dx*dx+dy*dy+dz*dz)-cylinder.Radius)>.01){valid=false;break;}
                }
                if(valid){center=rim;break;}
            }
            if(center==null)return false;
            // Circle center/radius attribute: native exports infer its plane from edge geometry.
            w.Write((ushort)18);w.Write(new byte[]{0x33,0x37});foreach(double value in center)CanonicalFloat95(w,value);CanonicalFloat95(w,cylinder.Radius);return true;
        }
    }
}
