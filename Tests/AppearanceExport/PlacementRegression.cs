using System;
using Tecnomatix.Engineering;
using TxTools.ExportByColor;

internal sealed class PlacedGeometry : ITxGeometry, ITxLocatableObject
{
    public string Id { get { return "mesh"; } }
    public string Name { get { return "mesh"; } }
    public TxTransformation AbsoluteLocation { get; set; }
}

internal static class PlacementRegression
{
    private static int checks;
    private static void Equal(TxTransformation matrix, double x, double y, double z, string message)
    {
        var actual = matrix.Apply(1, 2, 3);
        if (Math.Abs(actual[0] - x) > 1e-9 || Math.Abs(actual[1] - y) > 1e-9 || Math.Abs(actual[2] - z) > 1e-9)
            throw new Exception(message + ": " + string.Join(",", actual));
        checks++;
    }

    private static void Main()
    {
        var source = new TxTransformation(0, -1, 0, 100, 1, 0, 0, 200, 0, 0, 1, 30);
        var local = new TxTransformation(1, 0, 0, 5, 0, 0, -1, 7, 0, 1, 0, 11);
        var geometryWorld = TxTransformation.Multiply(source, local);
        var appearance = new TxTransformation(-1, 0, 0, -10, 0, -1, 0, 40, 0, 0, 1, 8);
        var mapped = AppearanceGeometrySource.MapToAppearance(appearance, source, geometryWorld);
        Equal(mapped, -16, 36, 21, "Remove source rotation/translation; retain geometry-to-part offset");
        var secondAppearance = new TxTransformation(0, 1, 0, 300, -1, 0, 0, -50, 0, 0, 1, 8);
        Equal(AppearanceGeometrySource.MapToAppearance(secondAppearance, source, geometryWorld), 304, -56, 21,
            "Shared source must use each appearance's own world pose");

        var frame = new TxTransformation(0, -1, 0, 10, 1, 0, 0, 20, 0, 0, 1, 30);
        var relative = TxTransformation.Multiply(frame.Inverse, mapped);
        Equal(relative, 16, 26, -9, "Apply rotated output origin after appearance mapping");
        var extraInverse = new TxTransformation(1, 0, 0, -5, 0, 1, 0, -6, 0, 0, 1, -7);
        Equal(TxTransformation.Multiply(extraInverse, relative), 11, 20, -16, "Apply tool inverse last");
        Equal(TxTransformation.Multiply(appearance.Inverse, mapped), 6, 4, 13,
            "Export in the appearance's own frame must retain only geometry-to-part placement");

        var mesh = new PlacedGeometry { AbsoluteLocation = geometryWorld };
        var part = new PlacedGeometry { AbsoluteLocation = source };
        var placed = new PlacedGeometry { AbsoluteLocation = appearance };
        var instance = new AppearanceGeometrySource.GeometryInstance(mesh, "appearance/mesh", placed, part);
        Equal(instance.WorldLocation, -16, 36, 21, "Production geometry-instance getter must apply the mapping");
        var ordinary = new AppearanceGeometrySource.GeometryInstance(mesh, "mesh");
        Equal(ordinary.WorldLocation, 96, 206, 43, "Ordinary geometry must retain its original world pose");
        try { AppearanceGeometrySource.MapToAppearance(appearance, null, geometryWorld); throw new Exception("Missing pose accepted"); }
        catch (InvalidOperationException) { checks++; }
        Console.WriteLine("PASS: " + checks + " placement checks (managed SDK matrix double)");
    }
}
