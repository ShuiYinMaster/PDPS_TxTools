// Standalone read-only CATIA tree counter.
// The COM calls intentionally mirror Agent/Core/Catia/CatiaBridge.cs.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

internal sealed class CatiaNode
{
    public string Name;
    public string PartNumber;
    public string Revision;
    public string Definition;
    public bool IsAssembly;
    public readonly List<CatiaNode> Children = new List<CatiaNode>();
}

internal static class Program
{
    private const string ProgId = "CATIA.Application";
    private static readonly string[] DefaultTargets = { "FQ114", "Q114", "FQ199" };

    private static object GetProperty(object target, string name)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.GetProperty,
            null,
            target,
            null);
    }

    private static object InvokeMethod(object target, string name, params object[] args)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod,
            null,
            target,
            args);
    }

    private static string TryString(object target, string name)
    {
        try
        {
            var value = GetProperty(target, name);
            return value == null ? null : value.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static int TryInt(object target, string name)
    {
        try
        {
            var value = GetProperty(target, name);
            return value == null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static CatiaNode ReadNode(object product, int depth, int maxDepth)
    {
        var node = new CatiaNode
        {
            Name = TryString(product, "Name"),
            PartNumber = TryString(product, "PartNumber"),
            Revision = TryString(product, "Revision"),
            Definition = TryString(product, "Definition")
        };

        if (depth >= maxDepth)
            return node;

        try
        {
            var products = GetProperty(product, "Products");
            if (products == null)
                return node;

            var count = TryInt(products, "Count");
            node.IsAssembly = count > 0;
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    // CATIA COM collections require Item(i) as InvokeMethod.
                    var child = InvokeMethod(products, "Item", i);
                    if (child != null)
                        node.Children.Add(ReadNode(child, depth + 1, maxDepth));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Skip child {0} at depth {1}: {2}", i, depth, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Read Products failed for {0}: {1}", node.Name ?? "?", ex.Message);
        }

        return node;
    }

    private static string CodeOf(CatiaNode node)
    {
        return string.IsNullOrWhiteSpace(node.PartNumber) ? node.Name : node.PartNumber;
    }

    private static void CountNode(
        CatiaNode node,
        ref int totalNodes,
        ref int assemblyNodes,
        ref int leafParts,
        Dictionary<string, int> targetCounts,
        HashSet<string> uniquePartNumbers)
    {
        totalNodes++;
        if (node.IsAssembly)
            assemblyNodes++;

        if (node.Children.Count == 0)
        {
            leafParts++;
            var code = CodeOf(node) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(code))
                uniquePartNumbers.Add(code);

            foreach (var target in new List<string>(targetCounts.Keys))
            {
                if (code.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                    targetCounts[target]++;
            }
        }

        foreach (var child in node.Children)
            CountNode(child, ref totalNodes, ref assemblyNodes, ref leafParts, targetCounts, uniquePartNumbers);
    }

    private static void PrintTree(CatiaNode node, string prefix, bool isLast)
    {
        var branch = isLast ? "|- " : "|- ";
        var label = string.IsNullOrWhiteSpace(node.Name) ? "?" : node.Name;
        if (!string.IsNullOrWhiteSpace(node.PartNumber) && node.PartNumber != node.Name)
            label += " [" + node.PartNumber + "]";
        if (node.IsAssembly)
            label += " (asm)";
        Console.WriteLine(prefix + branch + label);

        var childPrefix = prefix + (isLast ? "   " : "|  ");
        for (var i = 0; i < node.Children.Count; i++)
            PrintTree(node.Children[i], childPrefix, i == node.Children.Count - 1);
    }

    private static int ReadMaxDepth(string[] args)
    {
        foreach (var arg in args)
        {
            const string prefix = "--max-depth=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int value;
                if (int.TryParse(arg.Substring(prefix.Length), out value) && value > 0)
                    return value;
            }
        }
        return 100;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int Main(string[] args)
    {
        try
        {
            object app;
            try
            {
                app = Marshal.GetActiveObject(ProgId);
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine("Cannot attach to the active CATIA COM instance: {0}", ex.Message);
                Console.Error.WriteLine("Run this utility with the same elevation as CATIA (often Administrator).");
                return 2;
            }

            var document = GetProperty(app, "ActiveDocument");
            if (document == null)
            {
                Console.Error.WriteLine("CATIA has no active document.");
                return 3;
            }

            var documentName = TryString(document, "Name") ?? "?";
            var rootProduct = GetProperty(document, "Product");
            if (rootProduct == null)
            {
                Console.Error.WriteLine("The active document has no Product tree: " + documentName);
                return 4;
            }

            var maxDepth = ReadMaxDepth(args);
            var root = ReadNode(rootProduct, 0, maxDepth);
            var targetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in DefaultTargets)
                targetCounts[target] = 0;

            var totalNodes = 0;
            var assemblyNodes = 0;
            var leafParts = 0;
            var uniquePartNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CountNode(root, ref totalNodes, ref assemblyNodes, ref leafParts, targetCounts, uniquePartNumbers);

            if (HasFlag(args, "--show-tree"))
                PrintTree(root, string.Empty, true);

            Console.WriteLine();
            Console.WriteLine("Active document: " + documentName);
            Console.WriteLine("CATIA Product: " + (CodeOf(root) ?? "?"));
            Console.WriteLine("Total nodes (including root): " + totalNodes);
            Console.WriteLine("Assembly nodes: " + assemblyNodes);
            Console.WriteLine("Leaf part instances: " + leafParts);
            Console.WriteLine("Unique leaf PartNumbers: " + uniquePartNumbers.Count);
            Console.WriteLine("FQ114: " + targetCounts["FQ114"]);
            Console.WriteLine("Q114: " + targetCounts["Q114"]);
            Console.WriteLine("FQ199: " + targetCounts["FQ199"]);
            Console.WriteLine("Target sum: " + (targetCounts["FQ114"] + targetCounts["Q114"] + targetCounts["FQ199"]));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("CATIA tree read failed: " + ex);
            return 1;
        }
    }
}
