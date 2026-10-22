using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Tecnomatix.Engineering;

namespace TxTools.ExportByColor
{
    internal static class ApproximationTriangles
    {
        // 2402 exposes PrimitiveType as an internal, read-only enum property.
        // Without that metadata only an exact triangle is unambiguous.
        private static readonly PropertyInfo PrimitiveType = typeof(TxPrimitiveData).GetProperty(
            "PrimitiveType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static IEnumerable<int[]> Indices(TxPrimitiveData primitive, int pointCount)
        {
            if (primitive == null) throw new InvalidDataException("网格包含空图元");
            string kind = PrimitiveType == null ? null : Convert.ToString(PrimitiveType.GetValue(primitive, null));
            if (kind == "Line") yield break;
            int[] indices = primitive.Indices;
            if (indices == null || indices.Length < 3) yield break;
            foreach (int index in indices)
                if (index < 0 || index >= pointCount) throw new InvalidDataException("三角面索引超出顶点范围：" + index);
            if (kind == "Strip")
            {
                for (int i = 2; i < indices.Length; i++)
                {
                    int a = indices[i - 2], b = indices[i - 1], c = indices[i];
                    if (a == b || b == c || a == c) continue; // strip connectors
                    yield return (i & 1) == 0 ? new[] { a, b, c } : new[] { b, a, c };
                }
                yield break;
            }
            if (indices.Length != 3)
                throw new InvalidDataException("不支持的面图元 " + (kind ?? "未知类型") + "（" + indices.Length +
                    " 个索引）；停止该设备以避免截断几何");
            yield return indices;
        }
    }
}
