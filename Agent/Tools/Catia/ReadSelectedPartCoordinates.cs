// Read-only probe of the selected CATIA Product's local and world frames.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class ReadSelectedPartCoordinates
{
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

    private static object Optional(object target, string name)
    {
        try { return target == null ? null : Get(target, name); }
        catch { return null; }
    }

    private static bool SameComObject(object left, object right)
    {
        if (left == null || right == null) return false;
        var a = Marshal.GetIUnknownForObject(left);
        var b = Marshal.GetIUnknownForObject(right);
        try { return a == b; }
        finally { Marshal.Release(a); Marshal.Release(b); }
    }

    private static bool FindPath(object product, object selected, List<object> path)
    {
        path.Add(product);
        if (SameComObject(product, selected)) return true;
        var products = Optional(product, "Products");
        var count = products == null ? 0 : Convert.ToInt32(Get(products, "Count"));
        for (var i = 1; i <= count; i++)
            if (FindPath(Call(products, "Item", i), selected, path)) return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static double[] Components(object product)
    {
        var position = Get(product, "Position");
        var values = new object[12];
        var args = new object[] { values };
        var byRef = new ParameterModifier(1);
        byRef[0] = true;
        position.GetType().InvokeMember("GetComponents", BindingFlags.InvokeMethod,
            null, position, args, new[] { byRef }, null, null);
        var returned = args[0] as Array ?? values;
        var result = new double[12];
        for (var i = 0; i < result.Length; i++)
            result[i] = Convert.ToDouble(returned.GetValue(i));
        for (var axis = 0; axis < 3; axis++)
        {
            var offset = axis * 3;
            var squaredLength = result[offset] * result[offset] +
                result[offset + 1] * result[offset + 1] +
                result[offset + 2] * result[offset + 2];
            if (Math.Abs(squaredLength - 1) > 0.001)
                throw new InvalidOperationException("CATIA returned an invalid Position axis.");
        }
        return result;
    }

    // CATIA components are X/Y/Z basis columns followed by origin.
    private static double[] Compose(double[] parent, double[] child)
    {
        var result = new double[12];
        for (var col = 0; col < 3; col++)
            for (var row = 0; row < 3; row++)
                result[col * 3 + row] =
                    parent[row] * child[col * 3] +
                    parent[3 + row] * child[col * 3 + 1] +
                    parent[6 + row] * child[col * 3 + 2];
        for (var row = 0; row < 3; row++)
            result[9 + row] = parent[9 + row] +
                parent[row] * child[9] +
                parent[3 + row] * child[10] +
                parent[6 + row] * child[11];
        return result;
    }

    private static void Print(string label, double[] c)
    {
        Console.WriteLine(label);
        Console.WriteLine("  Origin: ({0:F6}, {1:F6}, {2:F6})", c[9], c[10], c[11]);
        Console.WriteLine("  X axis: ({0:F9}, {1:F9}, {2:F9})", c[0], c[1], c[2]);
        Console.WriteLine("  Y axis: ({0:F9}, {1:F9}, {2:F9})", c[3], c[4], c[5]);
        Console.WriteLine("  Z axis: ({0:F9}, {1:F9}, {2:F9})", c[6], c[7], c[8]);
    }

    private static int Main()
    {
        try
        {
            var app = Marshal.GetActiveObject("CATIA.Application");
            var document = Get(app, "ActiveDocument");
            var selection = Get(document, "Selection");
            var count = Convert.ToInt32(Get(selection, "Count2"));
            Console.WriteLine("Active document: {0}", Text(document, "Name"));
            Console.WriteLine("Selected items: {0}", count);
            if (count != 1)
                throw new InvalidOperationException("Select exactly one part/product node in the CATIA tree.");

            var selectedItem = Call(selection, "Item2", 1);
            var product = Optional(selectedItem, "Value");
            Console.WriteLine("Selection type: {0}", Text(selectedItem, "Type") ?? "?");
            Console.WriteLine("Selection value: {0} | {1}", Text(product, "Name") ?? "?", Text(product, "PartNumber") ?? "?");
            if (product == null || Optional(product, "Position") == null)
            {
                product = Optional(selectedItem, "LeafProduct");
                Console.WriteLine("LeafProduct fallback: {0} | {1}", Text(product, "Name") ?? "?", Text(product, "PartNumber") ?? "?");
            }
            if (product == null || Optional(product, "Position") == null)
                throw new InvalidOperationException("Selected item does not resolve to a Product with Position.");

            var root = Get(document, "Product");
            var path = new List<object>();
            if (!FindPath(root, product, path))
                throw new InvalidOperationException("Selected Product was not found in the active document's Product tree.");

            Console.WriteLine("Product path:");
            foreach (var node in path)
                Console.WriteLine("  {0} | {1}", Text(node, "Name") ?? "?", Text(node, "PartNumber") ?? "?");

            var local = Components(product);
            Print("Selected product relative to its parent:", local);

            var world = new double[] { 1,0,0, 0,1,0, 0,0,1, 0,0,0 };
            foreach (var node in path)
                world = Compose(world, Components(node));
            Print("Selected product relative to root/world:", world);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CATIA coordinate read failed: " + ex);
            return 1;
        }
    }
}
