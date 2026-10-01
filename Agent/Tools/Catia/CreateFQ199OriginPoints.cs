// Creates one point at the origin of each matching CATIA Product instance.
// Dry-run by default; --apply creates one new CATPart in the active assembly.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

internal sealed class InstanceOrigin
{
    public string Name;
    public double[] World;
}

internal static class CreateFQ199OriginPoints
{
    private const string SourcePartNumber = "FQ199D0616T25F36N_00";
    private const string NewPartNumber = "FQ199D0616T25F36N_00_OriginPoints";

    private static object Get(object target, string name)
    {
        return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
    }

    private static object Call(object target, string name, params object[] args)
    {
        return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
    }

    private static string Text(object target, string name)
    {
        try { return Convert.ToString(Get(target, name)); }
        catch { return null; }
    }

    private static double[] Components(object product)
    {
        var values = new object[12];
        var args = new object[] { values };
        var byRef = new ParameterModifier(1);
        byRef[0] = true;
        Get(product, "Position").GetType().InvokeMember("GetComponents", BindingFlags.InvokeMethod,
            null, Get(product, "Position"), args, new[] { byRef }, null, null);
        var returned = (Array)args[0];
        var result = new double[12];
        for (var i = 0; i < 12; i++)
            result[i] = Convert.ToDouble(returned.GetValue(i));
        for (var axis = 0; axis < 3; axis++)
        {
            var offset = axis * 3;
            var norm = result[offset] * result[offset] + result[offset + 1] * result[offset + 1] +
                result[offset + 2] * result[offset + 2];
            if (Math.Abs(norm - 1) > 0.001)
                throw new InvalidOperationException("Invalid CATIA position for " + Text(product, "Name"));
        }
        return result;
    }

    private static double[] Compose(double[] parent, double[] child)
    {
        var result = new double[12];
        for (var col = 0; col < 3; col++)
            for (var row = 0; row < 3; row++)
                result[col * 3 + row] = parent[row] * child[col * 3] +
                    parent[3 + row] * child[col * 3 + 1] + parent[6 + row] * child[col * 3 + 2];
        for (var row = 0; row < 3; row++)
            result[9 + row] = parent[9 + row] + parent[row] * child[9] +
                parent[3 + row] * child[10] + parent[6 + row] * child[11];
        return result;
    }

    private static void Collect(object product, double[] parentWorld, List<InstanceOrigin> matches, ref int existing)
    {
        var world = Compose(parentWorld, Components(product));
        var partNumber = Text(product, "PartNumber") ?? "";
        if (string.Equals(partNumber, SourcePartNumber, StringComparison.OrdinalIgnoreCase))
            matches.Add(new InstanceOrigin { Name = Text(product, "Name") ?? partNumber, World = world });
        if (string.Equals(partNumber, NewPartNumber, StringComparison.OrdinalIgnoreCase))
            existing++;
        var products = Get(product, "Products");
        var count = Convert.ToInt32(Get(products, "Count"));
        for (var i = 1; i <= count; i++)
            Collect(Call(products, "Item", i), world, matches, ref existing);
    }

    private static double[] ToLocal(double[] partWorld, double[] worldPoint)
    {
        var dx = worldPoint[9] - partWorld[9];
        var dy = worldPoint[10] - partWorld[10];
        var dz = worldPoint[11] - partWorld[11];
        return new[]
        {
            partWorld[0] * dx + partWorld[1] * dy + partWorld[2] * dz,
            partWorld[3] * dx + partWorld[4] * dy + partWorld[5] * dz,
            partWorld[6] * dx + partWorld[7] * dy + partWorld[8] * dz
        };
    }

    private static int Main(string[] args)
    {
        try
        {
            var apply = args.Length == 1 && string.Equals(args[0], "--apply", StringComparison.OrdinalIgnoreCase);
            if (args.Length > 0 && !apply)
                throw new ArgumentException("Only --apply is supported.");
            var app = Marshal.GetActiveObject("CATIA.Application");
            var document = Get(app, "ActiveDocument");
            var root = Get(document, "Product");
            Console.WriteLine("Active document: {0}", Text(document, "Name"));
            var identity = new double[] { 1,0,0, 0,1,0, 0,0,1, 0,0,0 };
            var matches = new List<InstanceOrigin>();
            var existing = 0;
            Collect(root, identity, matches, ref existing);
            if (matches.Count == 0)
                throw new InvalidOperationException("No " + SourcePartNumber + " instances in the active document.");
            if (existing != 0)
                throw new InvalidOperationException("A points component already exists; no new component was created.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
            {
                if (!names.Add(match.Name))
                    throw new InvalidOperationException("Duplicate instance name: " + match.Name);
                Console.WriteLine("  {0}: ({1:F6}, {2:F6}, {3:F6})",
                    match.Name, match.World[9], match.World[10], match.World[11]);
            }
            Console.WriteLine("Instance count: {0}", matches.Count);
            if (!apply)
            {
                Console.WriteLine("DRY RUN: no CATIA changes made.");
                return 0;
            }

            var rootProducts = Get(root, "Products");
            var newProduct = Call(rootProducts, "AddNewComponent", "Part", NewPartNumber);
            if (newProduct == null)
                throw new InvalidOperationException("CATIA did not return a new component.");
            var referenceProduct = Get(newProduct, "ReferenceProduct");
            var partDocument = Get(referenceProduct, "Parent");
            var part = Get(partDocument, "Part");
            var bodies = Get(part, "HybridBodies");
            var body = Call(bodies, "Add");
            body.GetType().InvokeMember("Name", BindingFlags.SetProperty, null, body,
                new object[] { "FQ199 instance origins" });
            var factory = Get(part, "HybridShapeFactory");
            var newPartWorld = Compose(Components(root), Components(newProduct));
            var created = 0;
            foreach (var match in matches)
            {
                var local = ToLocal(newPartWorld, match.World);
                var point = Call(factory, "AddNewPointCoord", local[0], local[1], local[2]);
                point.GetType().InvokeMember("Name", BindingFlags.SetProperty, null, point,
                    new object[] { match.Name + "_Origin" });
                Call(body, "AppendHybridShape", point);
                created++;
            }
            Call(part, "Update");
            var pointCollection = Get(body, "HybridShapes");
            var verified = Convert.ToInt32(Get(pointCollection, "Count"));
            Console.WriteLine("New CATPart: {0}", Text(newProduct, "PartNumber"));
            Console.WriteLine("Points created: {0}; verified in set: {1}", created, verified);
            return created == matches.Count && verified == matches.Count ? 0 : 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FQ199 point creation failed: " + ex);
            return 1;
        }
    }
}
