using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static class EncoderCoverageProbe
{
    // Load one pre-change/current encoder per process, so both are exercised by
    // exactly the same caller without recompiling either reference assembly.
    internal static void Run(string assemblyPath, string output, string mode)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        var writer = assembly.GetType("TxTools.ExportByColor.CgrWriter", true);
        var faceType = writer.GetNestedType("Face");
        var faceListType = typeof(List<>).MakeGenericType(faceType);
        var faces = (IList)Activator.CreateInstance(faceListType);
        var vertices = new List<float[]> { new float[] { -7, 11, 23 }, new float[] { 9, 11, 23 },
                                          new float[] { -7, 28, 23 }, new float[] { -7, 11, 42 } };
        int[][] triangles = { new[] { 0, 2, 1 }, new[] { 0, 1, 3 }, new[] { 1, 2, 3 }, new[] { 2, 0, 3 } };
        for (int i = 0; i < triangles.Length; i++)
        {
            object face = Activator.CreateInstance(faceType);
            faceType.GetField("Idx").SetValue(face, triangles[i]);
            faceType.GetField("R").SetValue(face, (byte)(40 + i * 45));
            faceType.GetField("G").SetValue(face, (byte)120);
            faceType.GetField("B").SetValue(face, (byte)180);
            if (mode.StartsWith("rgba-", StringComparison.Ordinal))
                faceType.GetField("Opacity").SetValue(face, (byte)(i * 85));
            faces.Add(face);
        }
        if (mode == "build-api")
        {
            writer.GetMethod("Build", new[] { typeof(List<float[]>), faceListType, typeof(string) })
                  .Invoke(null, new object[] { vertices, faces, output });
            return;
        }
        var backend = assembly.GetType("TxTools.ExportByColor.CgrBackend", true);
        string selected = mode == "legacy" ? "Legacy" : mode == "rgba-compact" ? "Compact" :
                          mode == "rgba-features" ? "LineFace" : "LineFacePlanar";
        writer.GetMethod("BuildFile", new[] { typeof(List<float[]>), faceListType, typeof(string), backend,
                                             typeof(int), typeof(Action<string>), typeof(bool) })
              .Invoke(null, new object[] { vertices, faces, output, Enum.Parse(backend, selected),
                                          20000, null, true });
    }
}
