// TxMath.cs  --  C# 8.0
// CrossEnvIO 内部坐标系工具：与 SRC/PsReader.MatrixToEulerDeg 的 RPY_ZYX 约定完全一致。
//
// 抽取（读）:
//   sinY = -m[8]        (m[8] = 矩阵 [2,0])
//   ry   = asin(sinY)
//   rx   = atan2(m[9], m[10])
//   rz   = atan2(m[4], m[0])
//
// 重建（写）对应矩阵（m00..m22）:
//   m00 = cz*cy, m01 = cz*sy*sx - sz*cx, m02 = cz*sy*cx + sz*sx
//   m10 = sz*cy, m11 = sz*sy*sx + cz*cx, m12 = sz*sy*cx - cz*sx
//   m20 = -sy,   m21 = cy*sx,            m22 = cy*cx
// （与对话验证过的 CreateWeldLocationOperation 重建代码逐项一致）

using System;
using Tecnomatix.Engineering;

namespace TxTools.CrossEnvIO
{
    public static class TxMath
    {
        private const double Rad2Deg = 180.0 / Math.PI;
        private const double Deg2Rad = Math.PI / 180.0;

        /// <summary>从 4x4 行主序矩阵提取欧拉角（度，RPY_ZYX）。null/长度不足返回 0。</summary>
        public static void MatrixToEulerDeg(double[] m, out double rx, out double ry, out double rz)
        {
            rx = ry = rz = 0.0;
            if (m == null || m.Length < 12) return;
            double sinY = -m[8];
            if (sinY > 1.0) sinY = 1.0;
            if (sinY < -1.0) sinY = -1.0;
            ry = Math.Asin(sinY);
            const double GimbalLock = 1.0 - 1e-6;
            if (Math.Abs(sinY) < GimbalLock)
            {
                rx = Math.Atan2(m[9], m[10]);
                rz = Math.Atan2(m[4], m[0]);
            }
            else
            {
                rx = 0.0;
                rz = sinY > 0 ? Math.Atan2(-m[1], m[5]) : Math.Atan2(m[1], -m[5]);
            }
            rx *= Rad2Deg; ry *= Rad2Deg; rz *= Rad2Deg;
        }

        /// <summary>从 X/Y/Z/RX/RY/RZ（度，RPY_ZYX）重建 TxTransformation。</summary>
        public static TxTransformation BuildTransform(double x, double y, double z,
                                                      double rxd, double ryd, double rzd)
        {
            double rx = rxd * Deg2Rad, ry = ryd * Deg2Rad, rz = rzd * Deg2Rad;
            double cx = Math.Cos(rx), sx = Math.Sin(rx);
            double cy = Math.Cos(ry), sy = Math.Sin(ry);
            double cz = Math.Cos(rz), sz = Math.Sin(rz);

            double m00 = cz * cy, m01 = cz * sy * sx - sz * cx, m02 = cz * sy * cx + sz * sx;
            double m10 = sz * cy, m11 = sz * sy * sx + cz * cx, m12 = sz * sy * cx - cz * sx;
            double m20 = -sy,     m21 = cy * sx,                m22 = cy * cx;

            var loc = new TxTransformation();
            loc.Translation = new TxVector(x, y, z);
            loc[0, 0] = m00; loc[0, 1] = m01; loc[0, 2] = m02;
            loc[1, 0] = m10; loc[1, 1] = m11; loc[1, 2] = m12;
            loc[2, 0] = m20; loc[2, 1] = m21; loc[2, 2] = m22;
            return loc;
        }

        /// <summary>把 TxTransformation 转成 4x4 行主序 double[16]。</summary>
        public static double[] TxToArr(TxTransformation tx)
        {
            if (tx == null) return new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            return new double[] {
                tx[0,0], tx[0,1], tx[0,2], tx[0,3],
                tx[1,0], tx[1,1], tx[1,2], tx[1,3],
                tx[2,0], tx[2,1], tx[2,2], tx[2,3],
                tx[3,0], tx[3,1], tx[3,2], tx[3,3]
            };
        }
    }
}
