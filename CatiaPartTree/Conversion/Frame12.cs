using System;
using System.IO;
using System.Linq;

namespace TxTools.CatiaPartTree.Conversion
{
    // CATIA: three basis columns (U,V,W), then origin, all in parent coordinates.
    public sealed class Frame12
    {
        private readonly double[] c;
        public double[] Components { get { return (double[])c.Clone(); } }
        public static Frame12 Identity { get { return new Frame12(new double[]{1,0,0,0,1,0,0,0,1,0,0,0}); } }
        public Frame12(double[] values)
        {
            if(values == null || values.Length != 12 || values.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                throw new InvalidDataException("CATIA 变换必须包含 12 个有效数值。");
            // Preserve CATIA's basis as supplied, including numerical drift,
            // scaling, shear and reflection. Composition does not require a rigid frame.
            c=(double[])values.Clone();
        }
        public double Determinant { get { return c[0]*(c[4]*c[8]-c[7]*c[5])-c[3]*(c[1]*c[8]-c[7]*c[2])+c[6]*(c[1]*c[5]-c[4]*c[2]); } }
        public double[] Point(double[] p)
        {
            var q=Direction(p);for(int k=0;k<3;k++)q[k]+=c[9+k];return q;
        }
        public double[] Direction(double[] p)
        {
            if(p==null || p.Length!=3 || p.Any(x=>double.IsNaN(x)||double.IsInfinity(x)))throw new InvalidDataException("Invalid XYZ");
            return new[]{c[0]*p[0]+c[3]*p[1]+c[6]*p[2],c[1]*p[0]+c[4]*p[1]+c[7]*p[2],c[2]*p[0]+c[5]*p[1]+c[8]*p[2]};
        }
        // parent * child. Do not reverse multiplication or add parent origins twice.
        public Frame12 ThenChild(Frame12 child)
        {
            var v=new double[12];var b=child.c;
            for(int col=0;col<3;col++){var q=Direction(new[]{b[col*3],b[col*3+1],b[col*3+2]});Array.Copy(q,0,v,col*3,3);}
            Array.Copy(Point(new[]{b[9],b[10],b[11]}),0,v,9,3);return new Frame12(v);
        }
        public Frame12 Inverse()
        {
            // A transpose is only the inverse of an orthonormal basis.
            double determinant=Determinant;
            if(determinant==0)throw new InvalidOperationException("该 CATIA 变换不可逆。");
            var v=new double[12];
            v[0]=(c[4]*c[8]-c[7]*c[5])/determinant;
            v[1]=(c[7]*c[2]-c[1]*c[8])/determinant;
            v[2]=(c[1]*c[5]-c[4]*c[2])/determinant;
            v[3]=(c[6]*c[5]-c[3]*c[8])/determinant;
            v[4]=(c[0]*c[8]-c[6]*c[2])/determinant;
            v[5]=(c[3]*c[2]-c[0]*c[5])/determinant;
            v[6]=(c[3]*c[7]-c[6]*c[4])/determinant;
            v[7]=(c[6]*c[1]-c[0]*c[7])/determinant;
            v[8]=(c[0]*c[4]-c[3]*c[1])/determinant;
            for(int row=0;row<3;row++)v[9+row]=-(v[row]*c[9]+v[3+row]*c[10]+v[6+row]*c[11]);return new Frame12(v);
        }
    }
}
