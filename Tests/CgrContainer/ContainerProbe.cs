using System;
using System.IO;
using System.Globalization;
using TxTools.ExportByColor;

// Standalone structural probe: no PS, CATIA, geometry encoder or template source.
internal static class ContainerProbe
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 4 && args[0] == "--encode")
            {
                EncoderCoverageProbe.Run(args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length < 5 || args.Length > 9)
                throw new ArgumentException("scene.bin picks.bin output.cgr sceneFragments pickFragments [identity[@unixSeconds] [minX,minY,minZ,maxX,maxY,maxZ [compact [fixed3D,accuracy3D,curveAccuracy3D,fixed2D,accuracy2D]]]]");
            byte[] scene = File.ReadAllBytes(args[0]), picks = File.ReadAllBytes(args[1]);
            CgrStorageIdentity? identity = null;
            if (args.Length > 5)
            {
                string[] parts = args[5].Split('@');
                if (parts.Length > 2) throw new ArgumentException("Invalid identity timestamp context");
                identity = CgrStorageIdentity.Parse(parts[0], parts.Length == 2 ? (uint?)uint.Parse(parts[1], CultureInfo.InvariantCulture) : null);
            }
            double[] bounds = { -10, -20, -30, 40, 50, 60 };
            if (args.Length > 6)
                bounds = Array.ConvertAll(args[6].Split(','), value => double.Parse(value, CultureInfo.InvariantCulture));
            CgrVisualizationAccuracy? accuracy = null;
            if (args.Length > 8)
            {
                string[] values = args[8].Split(',');
                if (values.Length != 5) throw new ArgumentException("Expected five visualization accuracy fields");
                accuracy = new CgrVisualizationAccuracy(bool.Parse(values[0]),
                    float.Parse(values[1], CultureInfo.InvariantCulture), float.Parse(values[2], CultureInfo.InvariantCulture),
                    bool.Parse(values[3]), float.Parse(values[4], CultureInfo.InvariantCulture));
            }
            using (var output = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write))
                CgrContainer.Write(output, scene, picks, bounds,
                                   args.Length > 7 && bool.Parse(args[7]), int.Parse(args[3]), int.Parse(args[4]), identity, accuracy);
            Console.WriteLine("Container written without CgrTemplate.cs");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
