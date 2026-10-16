using System;
using System.Globalization;
using System.IO;

namespace TxTools.ExportByColor
{
    // Minimal runtime probe; no geometry encoder, template or CATIA dependency.
    public static partial class CgrWriter
    {
        private static float Finite(double value)
        {
            float result = (float)value;
            if (float.IsNaN(result) || float.IsInfinity(result)) throw new ArgumentException("Non-finite float");
            return result;
        }
        private static void Compact(BinaryWriter w, uint value)
        { if (value < 255) w.Write((byte)value); else CompactExtended(w, value); }
        private static void CompactExtended(BinaryWriter w, uint value)
        { w.Write((byte)255); w.Write(value); }

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 5) throw new ArgumentException("mode payload out fields_csv pickOffset");
                byte[] payload = File.ReadAllBytes(args[1]);
                double[] values = Array.ConvertAll(args[3].Split(','), value => double.Parse(value, CultureInfo.InvariantCulture));
                uint pickOffset = uint.Parse(args[4], CultureInfo.InvariantCulture);
                byte[] result;
                if (args[0] == "common")
                {
                    if (values.Length != 14) throw new ArgumentException("Expected inheritance,flags,graphicsLow,graphicsHigh,colorSource,opacity,colorWord,field11,radius,scalar16,cx,cy,cz,objectId");
                    using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
                    {
                        WriteSceneCommon(w, checked((byte)values[1]), checked((byte)values[2]), checked((byte)values[7]),
                            Finite(values[8]), Finite(values[9]), new[] { Finite(values[10]), Finite(values[11]), Finite(values[12]) },
                            checked((uint)values[13]), checked((uint)values[6]), checked((byte)values[4]),
                            checked((uint)values[0]), checked((byte)values[3]), checked((byte)values[5]));
                        result = ms.ToArray();
                    }
                }
                else if (args[0] == "attributes")
                {
                    if (values.Length != 2) throw new ArgumentException("Expected layerCode,nodeOptions");
                    using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
                    { WriteSceneNodeAttributes(w, checked((ushort)values[0]), checked((ushort)values[1])); result = ms.ToArray(); }
                }
                else if (args[0] == "feature" || args[0] == "legacy")
                {
                    if (values.Length != 4) throw new ArgumentException("Expected padded radius,cx,cy,cz");
                    var center = new[] { Finite(values[1]), Finite(values[2]), Finite(values[3]) };
                    result = args[0] == "feature" ? WriteAcceptedFeatureScene(payload, Finite(values[0]), center, pickOffset)
                                                   : WriteAcceptedLegacyScene(payload, Finite(values[0]), center);
                }
                else
                {
                    if (values.Length != 6) throw new ArgumentException("Expected minXYZ,maxXYZ");
                    var bounds = new double[10];
                    double r2 = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        bounds[k] = (values[k] + values[k + 3]) / 2;
                        bounds[k + 3] = (values[k + 3] - values[k]) / 2;
                        r2 += bounds[k + 3] * bounds[k + 3];
                    }
                    bounds[9] = Math.Sqrt(r2);
                    if (args[0] == "leaf") result = WriteCompactLeaf(payload, bounds, pickOffset);
                    else if (args[0] == "pick")
                    {
                        using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
                        { WriteCompactPick(w, bounds); result = ms.ToArray(); }
                    }
                    else throw new ArgumentException("Unknown mode");
                }
                using (var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write))
                    output.Write(result, 0, result.Length);
                Console.WriteLine(SceneRecordVersion);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
