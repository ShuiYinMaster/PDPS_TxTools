// Read hidden CATIA Product instances, compare them with HybridBodies in
// DPUB-501038693-XWS_01, and optionally hide matched HybridBodies.
// CATIA COM access follows Agent/Core/Catia/CatiaBridge.cs.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

internal sealed class ProductNode
{
    public object Product;
    public string Name;
    public string PartNumber;
    public readonly List<ProductNode> Children = new List<ProductNode>();
}

internal sealed class HiddenPart
{
    public ProductNode Node;
    public string Name;
    public string PartNumber;
}

internal sealed class GeometrySetInfo
{
    public object HybridBody;
    public string Name;
    public string ParentName;
    public readonly List<string> MatchedKeys = new List<string>();
}

internal static class Program
{
    private const string ProgId = "CATIA.Application";
    private const string TargetPartNumber = "DPUB-501038693-XWS_01";
    private static readonly string[] ProductOnlyHiddenPartNumbers =
    {
        "DPUB-501010748-XAX_01",
        "DPUB-501036211-XAX_01",
        "DPUB-501036201-XAX_01",
        "DPUB-501036202-XAX_01"
    };
    private static object _app;
    private static object _selection;
    private static bool _showProbePrinted;

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

    private static object TryObject(object target, string name)
    {
        try
        {
            return target == null ? null : GetProperty(target, name);
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

    private static ProductNode ReadTree(object product, int depth, int maxDepth)
    {
        var node = new ProductNode
        {
            Product = product,
            Name = TryString(product, "Name"),
            PartNumber = TryString(product, "PartNumber")
        };

        if (depth >= maxDepth)
            return node;

        try
        {
            var products = GetProperty(product, "Products");
            if (products == null)
                return node;

            var count = TryInt(products, "Count");
            for (var i = 1; i <= count; i++)
            {
                try
                {
                    var child = InvokeMethod(products, "Item", i);
                    if (child != null)
                        node.Children.Add(ReadTree(child, depth + 1, maxDepth));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Skip Product child {0} at depth {1}: {2}", i, depth, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Read Products failed for {0}: {1}", node.Name ?? "?", ex.Message);
        }

        return node;
    }

    private static void Flatten(ProductNode node, List<ProductNode> result)
    {
        result.Add(node);
        foreach (var child in node.Children)
            Flatten(child, result);
    }

    private static bool TryGetShow(object target, out int showValue, out string error)
    {
        showValue = -1;
        error = null;
        try
        {
            if (_selection == null)
                throw new InvalidOperationException("ProductDocument.Selection is not initialized.");

            InvokeMethod(_selection, "Clear");
            InvokeMethod(_selection, "Add", target);
            if (showValue == 0)
            {
                // CATIA may not accept SetShow(Show) on a hidden HybridBody
                // when it is only added directly. Re-search the hidden
                // selection first, then apply the show state to that result.
                InvokeMethod(_selection, "Search", "vis:hidden,sel");
                if (TryInt(_selection, "Count2") == 0)
                    throw new InvalidOperationException("Hidden target was not returned by vis:hidden,sel.");
            }
            var visProperties = GetProperty(_selection, "VisProperties");
            var args = new object[] { 0 };
            var returned = InvokeMethod(visProperties, "GetShow", args);
            if (!_showProbePrinted)
            {
                _showProbePrinted = true;
                Console.WriteLine("GetShow probe: return={0}; outArg={1}; outType={2}",
                    returned == null ? "<null>" : returned.ToString(),
                    args[0] == null ? "<null>" : args[0].ToString(),
                    args[0] == null ? "<null>" : args[0].GetType().FullName);
            }
            showValue = Convert.ToInt32(args[0]);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TrySetNoShow(object target, out string error)
    {
        return TrySetShowValue(target, 1, out error);
    }

    private static bool TrySetShowValue(object target, int showValue, out string error)
    {
        error = null;
        try
        {
            if (_selection == null)
                throw new InvalidOperationException("ProductDocument.Selection is not initialized.");

            InvokeMethod(_selection, "Clear");
            InvokeMethod(_selection, "Add", target);
            var visProperties = GetProperty(_selection, "VisProperties");
            // CatVisPropertyShow.catVisPropertyShow = 0; NoShow = 1.
            InvokeMethod(visProperties, "SetShow", showValue);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryIsHiddenBySearch(object target, out string error)
    {
        error = null;
        try
        {
            var name = TryString(target, "Name");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Target has no Name for visibility search.");

            var activeDocument = GetProperty(_app, "ActiveDocument");
            var selection = TryObject(activeDocument, "Selection") ?? _selection;
            if (selection == null)
                throw new InvalidOperationException("No CATIA Selection is available.");

            InvokeMethod(selection, "Clear");
            InvokeMethod(selection, "Search", "Name='" + name + "'&vis:hidden,all");
            var count = TryInt(selection, "Count2");
            InvokeMethod(selection, "Clear");
            return count > 0;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value.Trim();
        var dot = text.LastIndexOf('.');
        if (dot > 0 && dot + 1 < text.Length)
        {
            var suffix = text.Substring(dot + 1);
            int ignored;
            if (int.TryParse(suffix, out ignored))
                text = text.Substring(0, dot);
        }

        if (text.EndsWith(".CATPart", StringComparison.OrdinalIgnoreCase))
            text = text.Substring(0, text.Length - ".CATPart".Length);

        var underscore = text.LastIndexOf('_');
        if (underscore > 0 && underscore + 1 < text.Length)
        {
            var suffix = text.Substring(underscore + 1);
            int ignored;
            if (int.TryParse(suffix, out ignored))
                text = text.Substring(0, underscore);
        }

        return text.Trim().ToUpperInvariant();
    }

    private static bool IsSameKey(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static object ResolvePartDocument(object product)
    {
        try
        {
            var referenceProduct = GetProperty(product, "ReferenceProduct");
            if (referenceProduct == null)
                return null;
            return GetProperty(referenceProduct, "Parent");
        }
        catch
        {
            return null;
        }
    }

    private static List<GeometrySetInfo> ReadGeometrySets(object product)
    {
        var result = new List<GeometrySetInfo>();
        var partDocument = ResolvePartDocument(product);
        if (partDocument == null)
            throw new InvalidOperationException("ReferenceProduct.Parent is not available for the target part.");

        var part = GetProperty(partDocument, "Part");
        var hybridBodies = GetProperty(part, "HybridBodies");
        var count = TryInt(hybridBodies, "Count");
        for (var i = 1; i <= count; i++)
        {
            var body = InvokeMethod(hybridBodies, "Item", i);
            result.Add(new GeometrySetInfo
            {
                HybridBody = body,
                Name = TryString(body, "Name"),
                ParentName = null
            });
        }
        return result;
    }

    private static void ReadChildGeometrySets(GeometrySetInfo parent, List<GeometrySetInfo> result)
    {
        var children = TryObject(parent.HybridBody, "HybridBodies");
        var count = TryInt(children, "Count");
        for (var i = 1; i <= count; i++)
        {
            var body = InvokeMethod(children, "Item", i);
            var child = new GeometrySetInfo
            {
                HybridBody = body,
                Name = TryString(body, "Name"),
                ParentName = parent.Name
            };
            result.Add(child);
            ReadChildGeometrySets(child, result);
        }
    }

    private static List<GeometrySetInfo> FlattenGeometrySets(List<GeometrySetInfo> topLevel)
    {
        var result = new List<GeometrySetInfo>();
        foreach (var parent in topLevel)
        {
            ReadChildGeometrySets(parent, result);
        }
        return result;
    }

    private static List<string> SplitGeometrySetKeys(string name)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
            return result;

        foreach (var token in name.Split('&'))
        {
            var key = Normalize(token);
            if (!string.IsNullOrEmpty(key) && !result.Exists(delegate(string existing)
            {
                return string.Equals(existing, key, StringComparison.OrdinalIgnoreCase);
            }))
                result.Add(key);
        }
        return result;
    }

    private static int ToggleGeometrySets(object partDocument, List<GeometrySetInfo> geometrySets)
    {
        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        var activeDocument = GetProperty(_app, "ActiveDocument");
        var activeSelection = TryObject(activeDocument, "Selection");
        _selection = activeSelection ?? targetSelection;
        Console.WriteLine("Visibility selection source: {0}",
            activeSelection == null ? "Target PartDocument" : "Active ProductDocument");

        var shownCount = 0;
        var hiddenCount = 0;
        var failedCount = 0;
        foreach (var set in geometrySets)
        {
            string error;
            bool wasHidden;
            if (!TryIsHiddenBySearch(set.HybridBody, out error))
            {
                if (!string.IsNullOrEmpty(error))
                {
                    failedCount++;
                    Console.Error.WriteLine("  READ FAIL {0}: {1}", set.Name ?? "?", error);
                    continue;
                }
                wasHidden = false;
            }
            else
            {
                wasHidden = true;
            }

            var desiredShowValue = wasHidden ? 0 : 1;
            if (!TrySetShowValue(set.HybridBody, desiredShowValue, out error))
            {
                failedCount++;
                Console.Error.WriteLine("  TOGGLE FAIL {0}: {1}", set.Name ?? "?", error);
                continue;
            }

            var nowHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            var verified = string.IsNullOrEmpty(error) && nowHidden != wasHidden;
            if (verified)
            {
                if (wasHidden)
                {
                    shownCount++;
                    Console.WriteLine("  SHOW OK   {0}", set.Name ?? "?");
                }
                else
                {
                    hiddenCount++;
                    Console.WriteLine("  HIDE OK   {0}", set.Name ?? "?");
                }
            }
            else
            {
                failedCount++;
                Console.Error.WriteLine("  VERIFY FAIL {0}: {1}",
                    set.Name ?? "?",
                    string.IsNullOrEmpty(error) ? "visibility did not toggle" : error);
            }
        }

        Console.WriteLine();
        Console.WriteLine("Toggled geometry sets: {0}", shownCount + hiddenCount);
        Console.WriteLine("Shown from hidden: {0}", shownCount);
        Console.WriteLine("Hidden from shown: {0}", hiddenCount);
        Console.WriteLine("Toggle failures: {0}", failedCount);
        return failedCount == 0 && shownCount + hiddenCount == geometrySets.Count ? 0 : 5;
    }

    private static int ProbeGeometrySetVisibility(object partDocument, List<GeometrySetInfo> topLevel, List<GeometrySetInfo> geometrySets)
    {
        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        _selection = targetSelection;

        Console.WriteLine("Top-level geometry-set states:");
        foreach (var top in topLevel)
        {
            string topError;
            var topHidden = TryIsHiddenBySearch(top.HybridBody, out topError);
            Console.WriteLine("  TOP {0}: SearchHidden={1} ({2})",
                top.Name ?? "?",
                topHidden ? "true" : "false",
                string.IsNullOrEmpty(topError) ? "ok" : topError);
        }

        var limit = Math.Min(geometrySets.Count, 8);
        for (var i = 0; i < limit; i++)
        {
            var set = geometrySets[i];
            int show;
            string showError;
            string hiddenError;
            var showOk = TryGetShow(set.HybridBody, out show, out showError);
            var hidden = TryIsHiddenBySearch(set.HybridBody, out hiddenError);
            Console.WriteLine("  PROBE {0}: GetShow={1} ({2}); SearchHidden={3} ({4})",
                set.Name ?? "?",
                showOk ? show.ToString() : "FAIL",
                showOk ? "ok" : showError,
                hidden ? "true" : "false",
                string.IsNullOrEmpty(hiddenError) ? "ok" : hiddenError);
        }
        return 0;
    }

    private static int SetRequestedTopToggleResult(object partDocument, List<GeometrySetInfo> topLevel)
    {
        try
        {
            InvokeMethod(partDocument, "Activate");
            Console.WriteLine("Target Part activated: {0}",
                TryString(GetProperty(_app, "ActiveDocument"), "Name") ?? "?");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Target Part activation unavailable: {0}", ex.Message);
        }

        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        var activeDocument = GetProperty(_app, "ActiveDocument");
        var activeSelection = TryObject(activeDocument, "Selection");
        _selection = activeSelection ?? targetSelection;
        Console.WriteLine("Visibility selection source: {0}",
            activeSelection == null ? "Target PartDocument" : "Active ProductDocument");

        var changed = 0;
        var verified = 0;
        foreach (var set in topLevel)
        {
            var shouldShow = string.Equals(set.Name, "Welding Spot", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(set.Name, "Welding CurveBead", StringComparison.OrdinalIgnoreCase);
            string error;
            var isHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine("  READ FAIL {0}: {1}", set.Name ?? "?", error);
                continue;
            }

            var shouldBeHidden = !shouldShow;
            if (isHidden != shouldBeHidden)
            {
                var setOk = shouldShow
                    ? TryShowByGlobalName(set.Name, out error)
                    : TryHideByGlobalName(set.Name, out error);
                if (!setOk)
                {
                    Console.Error.WriteLine("  SET FAIL {0}: {1}", set.Name ?? "?", error);
                    continue;
                }
                changed++;
            }

            var afterHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (string.IsNullOrEmpty(error) && afterHidden == shouldBeHidden)
            {
                verified++;
                Console.WriteLine("  FINAL {0}: {1}", set.Name ?? "?", shouldShow ? "shown" : "hidden");
            }
            else
            {
                Console.Error.WriteLine("  VERIFY FAIL {0}: expected {1}",
                    set.Name ?? "?",
                    shouldShow ? "shown" : "hidden");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Top-level result changes: {0}", changed);
        Console.WriteLine("Top-level result verified: {0}/{1}", verified, topLevel.Count);
        return verified == topLevel.Count ? 0 : 5;
    }

    private static int ShowHiddenTopLevelFromGlobalSearch(object partDocument, List<GeometrySetInfo> topLevel)
    {
        var activeDocument = GetProperty(_app, "ActiveDocument");
        var selection = TryObject(activeDocument, "Selection");
        if (selection == null)
            selection = GetProperty(partDocument, "Selection");
        _selection = selection;

        var shown = 0;
        var attempted = 0;
        foreach (var name in new[] { "Welding Spot", "Welding CurveBead" })
        {
            var found = false;
            foreach (var query in new[]
            {
                "Name='" + name + "',all",
                "Name=" + name + ",all",
                "Name=" + name + "*,all"
            })
            {
                InvokeMethod(selection, "Clear");
                try
                {
                    InvokeMethod(selection, "Search", query);
                    var count = TryInt(selection, "Count2");
                    Console.WriteLine("  Query {0}: {1}", query, count);
                    if (count == 0)
                        continue;

                    attempted++;
                    var visProperties = GetProperty(selection, "VisProperties");
                    InvokeMethod(visProperties, "SetShow", 0);
                    shown++;
                    found = true;
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Query {0}: FAIL {1}", query, ex.Message);
                }
            }

            if (!found)
                Console.Error.WriteLine("  SHOW FAIL {0}: no matching hidden object found", name);
        }

        InvokeMethod(selection, "Clear");
        Console.WriteLine("Named-search show calls: {0}/{1}", shown, attempted);
        return shown == 2 ? 0 : 5;
    }

    private static bool TryShowByGlobalName(string name, out string error)
    {
        error = null;
        try
        {
            var activeDocument = GetProperty(_app, "ActiveDocument");
            var selection = TryObject(activeDocument, "Selection");
            if (selection == null)
                throw new InvalidOperationException("Active ProductDocument.Selection is null.");

            InvokeMethod(selection, "Clear");
            InvokeMethod(selection, "Search", "Name='" + name + "',all");
            if (TryInt(selection, "Count2") == 0)
                throw new InvalidOperationException("Named hidden geometry set was not found.");
            var visProperties = GetProperty(selection, "VisProperties");
            InvokeMethod(visProperties, "SetShow", 0);
            InvokeMethod(selection, "Clear");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryHideByGlobalName(string name, out string error)
    {
        error = null;
        try
        {
            var activeDocument = GetProperty(_app, "ActiveDocument");
            var selection = TryObject(activeDocument, "Selection");
            if (selection == null)
                throw new InvalidOperationException("Active ProductDocument.Selection is null.");

            InvokeMethod(selection, "Clear");
            InvokeMethod(selection, "Search", "Name='" + name + "',all");
            if (TryInt(selection, "Count2") == 0)
                throw new InvalidOperationException("Named geometry set was not found.");
            var visProperties = GetProperty(selection, "VisProperties");
            InvokeMethod(visProperties, "SetShow", 1);
            InvokeMethod(selection, "Clear");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static int InvertNestedGeometrySets(object partDocument, List<GeometrySetInfo> geometrySets)
    {
        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        var activeDocument = GetProperty(_app, "ActiveDocument");
        _selection = TryObject(activeDocument, "Selection") ?? targetSelection;

        var originalStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in geometrySets)
        {
            InvokeMethod(_selection, "Clear");
            InvokeMethod(_selection, "Search", "Name='" + set.Name + "',all");
            var count = TryInt(_selection, "Count2");
            if (count != 1)
                throw new InvalidOperationException("Non-unique CATIA search for " + set.Name + ": " + count);
            var selected = InvokeMethod(_selection, "Item2", 1);
            var value = TryObject(selected, "Value");
            if (!string.Equals(TryString(selected, "Type"), "HybridBody", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(TryString(value, "Name"), set.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Search result is not the expected HybridBody: " + set.Name);
            string readError;
            var hidden = TryIsHiddenBySearch(set.HybridBody, out readError);
            if (!string.IsNullOrEmpty(readError))
                throw new InvalidOperationException("Visibility read failed for " + set.Name + ": " + readError);
            originalStates.Add(set.Name, hidden);
        }
        InvokeMethod(_selection, "Clear");
        Console.WriteLine("Inversion preflight: {0} unique nested sets; hidden={1}; shown={2}.",
            geometrySets.Count,
            new List<bool>(originalStates.Values).FindAll(delegate(bool hidden) { return hidden; }).Count,
            new List<bool>(originalStates.Values).FindAll(delegate(bool hidden) { return !hidden; }).Count);

        var shownCount = 0;
        var hiddenCount = 0;
        var failedCount = 0;
        foreach (var set in geometrySets)
        {
            string error;
            var wasHidden = originalStates[set.Name];

            if (wasHidden)
            {
                if (!TryShowByGlobalName(set.Name, out error))
                {
                    failedCount++;
                    Console.Error.WriteLine("  SHOW FAIL {0}: {1}", set.Name ?? "?", error);
                    continue;
                }
            }
            else if (!TryHideByGlobalName(set.Name, out error))
            {
                failedCount++;
                Console.Error.WriteLine("  HIDE FAIL {0}: {1}", set.Name ?? "?", error);
                continue;
            }

            var nowHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (string.IsNullOrEmpty(error) && nowHidden != wasHidden)
            {
                if (wasHidden)
                {
                    shownCount++;
                    Console.WriteLine("  SHOW OK   {0}", set.Name ?? "?");
                }
                else
                {
                    hiddenCount++;
                    Console.WriteLine("  HIDE OK   {0}", set.Name ?? "?");
                }
            }
            else
            {
                failedCount++;
                Console.Error.WriteLine("  VERIFY FAIL {0}: visibility did not invert", set.Name ?? "?");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Inverted nested geometry sets: {0}", shownCount + hiddenCount);
        Console.WriteLine("Shown from hidden: {0}", shownCount);
        Console.WriteLine("Hidden from shown: {0}", hiddenCount);
        Console.WriteLine("Nested inversion failures: {0}", failedCount);
        return failedCount == 0 && shownCount + hiddenCount == geometrySets.Count ? 0 : 5;
    }

    private static int ProbeNestedStates(object partDocument, List<GeometrySetInfo> geometrySets)
    {
        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        _selection = targetSelection;

        var hidden = 0;
        var shown = 0;
        var failed = 0;
        foreach (var set in geometrySets)
        {
            string error;
            var isHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (!string.IsNullOrEmpty(error))
            {
                failed++;
                continue;
            }
            if (isHidden)
                hidden++;
            else
                shown++;
        }

        Console.WriteLine("Nested visibility summary: total={0}, hidden={1}, shown={2}, failed={3}",
            geometrySets.Count, hidden, shown, failed);
        return failed == 0 ? 0 : 5;
    }

    private static int SetInverseOfMatchedGeometrySets(
        object partDocument,
        List<GeometrySetInfo> allGeometrySets,
        List<GeometrySetInfo> matchedGeometrySets)
    {
        var targetSelection = GetProperty(partDocument, "Selection");
        if (targetSelection == null)
            throw new InvalidOperationException("Target PartDocument.Selection is null.");
        _selection = targetSelection;

        var shown = 0;
        var hidden = 0;
        var failed = 0;
        var expectedShow = 0;
        foreach (var candidate in allGeometrySets)
            if (matchedGeometrySets.Contains(candidate))
                expectedShow++;
        Console.WriteLine("Inverse target counts: show={0}, hide={1}",
            expectedShow,
            allGeometrySets.Count - expectedShow);
        foreach (var set in allGeometrySets)
        {
            var shouldShow = matchedGeometrySets.Contains(set);
            string error;
            var ok = shouldShow
                ? TryShowByGlobalName(set.Name, out error)
                : TryHideByGlobalName(set.Name, out error);
            if (!ok)
            {
                failed++;
                Console.Error.WriteLine("  SET FAIL {0}: {1}", set.Name ?? "?", error);
                continue;
            }

            var isHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (!string.IsNullOrEmpty(error) || isHidden == shouldShow)
            {
                failed++;
                Console.Error.WriteLine("  VERIFY FAIL {0}: expected {1}",
                    set.Name ?? "?",
                    shouldShow ? "shown" : "hidden");
                continue;
            }

            if (shouldShow)
            {
                shown++;
                Console.WriteLine("  SHOW OK   {0}", set.Name ?? "?");
            }
            else
            {
                hidden++;
                Console.WriteLine("  HIDE OK   {0}", set.Name ?? "?");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Inverse matched-set result: shown={0}, hidden={1}, failed={2}",
            shown, hidden, failed);
        return failed == 0 && shown + hidden == allGeometrySets.Count ? 0 : 5;
    }

    private static int TestShowOneNested(object partDocument, GeometrySetInfo set)
    {
        string error;
        var before = TryIsHiddenBySearch(set.HybridBody, out error);
        Console.WriteLine("Single show before: {0} hidden={1} error={2}",
            set.Name ?? "?", before, error ?? "none");

        var activeDocument = GetProperty(_app, "ActiveDocument");
        var selection = TryObject(activeDocument, "Selection");
        _selection = selection ?? GetProperty(partDocument, "Selection");
        InvokeMethod(_selection, "Clear");
        InvokeMethod(_selection, "Search", "Name='" + set.Name + "',all");
        var count = TryInt(_selection, "Count2");
        Console.WriteLine("Single show search count: {0}", count);
        for (var i = 1; i <= Math.Min(count, 5); i++)
        {
            var selected = InvokeMethod(_selection, "Item2", i);
            var value = TryObject(selected, "Value");
            Console.WriteLine("  SINGLE RESULT {0}: type={1}, name={2}",
                i,
                TryString(selected, "Type") ?? "?",
                TryString(value, "Name") ?? "?");
        }
        var visProperties = GetProperty(_selection, "VisProperties");
        InvokeMethod(visProperties, "SetShow", 0);
        InvokeMethod(_selection, "Clear");

        var after = TryIsHiddenBySearch(set.HybridBody, out error);
        Console.WriteLine("Single show after: {0} hidden={1} error={2}",
            set.Name ?? "?", after, error ?? "none");
        return 0;
    }

    private static void ProbeGeometrySetContents(List<GeometrySetInfo> geometrySets)
    {
        Console.WriteLine();
        Console.WriteLine("Geometry-set contents probe:");
        foreach (var set in geometrySets)
        {
            Console.WriteLine("  SET {0}", set.Name ?? "?");
            var body = set.HybridBody;
            var collectionNames = new[] { "HybridShapes", "HybridBodies", "GeometricElements", "Shapes" };
            foreach (var collectionName in collectionNames)
            {
                var collection = TryObject(body, collectionName);
                if (collection == null)
                    continue;

                var count = TryInt(collection, "Count");
                Console.WriteLine("    {0}: {1}", collectionName, count);
                var limit = Math.Min(count, 100);
                for (var i = 1; i <= limit; i++)
                {
                    try
                    {
                        var child = InvokeMethod(collection, "Item", i);
                        Console.WriteLine("      {0}[{1}] {2} | {3}",
                            collectionName,
                            i,
                            TryString(child, "Name") ?? "?",
                            child == null ? "<null>" : child.GetType().FullName);

                        if (collectionName == "HybridBodies" && child != null)
                        {
                            foreach (var nestedCollectionName in new[] { "HybridShapes", "HybridBodies", "GeometricElements", "Shapes" })
                            {
                                var nestedCollection = TryObject(child, nestedCollectionName);
                                if (nestedCollection == null)
                                    continue;
                                var nestedCount = TryInt(nestedCollection, "Count");
                                if (nestedCount == 0)
                                    continue;
                                Console.WriteLine("        {0}: {1}", nestedCollectionName, nestedCount);
                                var nestedLimit = Math.Min(nestedCount, 20);
                                for (var j = 1; j <= nestedLimit; j++)
                                {
                                    var nestedChild = InvokeMethod(nestedCollection, "Item", j);
                                    Console.WriteLine("          {0}[{1}] {2}",
                                        nestedCollectionName,
                                        j,
                                        TryString(nestedChild, "Name") ?? "?");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("      {0}[{1}] read failed: {2}", collectionName, i, ex.Message);
                    }
                }
            }
        }
    }

    private static void ProbePartHiddenSearch(object partDocument)
    {
        try
        {
            var selection = TryObject(partDocument, "Selection");
            if (selection == null)
                return;

            Console.WriteLine();
            Console.WriteLine("Target Part hidden-object search:");
            foreach (var query in new[]
            {
                "CATPrtSearch.HybridBody.Visibility=Hidden,all",
                "CATPrtSearch.HybridShape.Visibility=Hidden,all",
                "CATPrtSearch.HybridBody,all"
            })
            {
                InvokeMethod(selection, "Clear");
                try
                {
                    InvokeMethod(selection, "Search", query);
                    var count = TryInt(selection, "Count2");
                    Console.WriteLine("  CATIA search [{0}]: Count2={1}", query, count);
                    var limit = Math.Min(count, 100);
                    for (var i = 1; i <= limit; i++)
                    {
                        var selected = InvokeMethod(selection, "Item2", i);
                        var value = TryObject(selected, "Value");
                        Console.WriteLine("    RESULT {0}: type={1}; {2} | {3}",
                            i,
                            TryString(selected, "Type") ?? "?",
                            value == null ? "<null>" : (TryString(value, "Name") ?? "?"),
                            value == null ? "?" : value.GetType().FullName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  CATIA search [{0}] failed: {1}", query, ex.Message);
                }
            }
            InvokeMethod(selection, "Clear");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Target Part hidden-object search failed: {0}", ex.Message);
        }
    }

    private static string DisplayName(ProductNode node)
    {
        return (node.PartNumber ?? "?") + " | " + (node.Name ?? "?");
    }

    private static string DisplayName(HiddenPart part)
    {
        return (part.PartNumber ?? "?") + " | " + (part.Name ?? "?");
    }

    private static List<HiddenPart> ReadHiddenProductsFromSearch()
    {
        var result = new List<HiddenPart>();
        InvokeMethod(_selection, "Clear");
        InvokeMethod(_selection, "Search", "CATAsmSearch.Product.Visibility=Hidden,all");
        var count = TryInt(_selection, "Count2");
        for (var i = 1; i <= count; i++)
        {
            var selected = InvokeMethod(_selection, "Item2", i);
            var product = TryObject(selected, "Value");
            if (product == null)
                continue;

            result.Add(new HiddenPart
            {
                Node = new ProductNode
                {
                    Product = product,
                    Name = TryString(product, "Name"),
                    PartNumber = TryString(product, "PartNumber")
                },
                Name = TryString(product, "Name"),
                PartNumber = TryString(product, "PartNumber")
            });
        }
        InvokeMethod(_selection, "Clear");
        return result;
    }

    private static int ReadSearchCount(string query)
    {
        InvokeMethod(_selection, "Clear");
        InvokeMethod(_selection, "Search", query);
        var count = TryInt(_selection, "Count2");
        InvokeMethod(_selection, "Clear");
        return count;
    }

    private static void AddDescendants(ProductNode node, List<ProductNode> result, int maxDepth)
    {
        if (node == null)
            return;

        result.Add(node);
        if (result.Count > 10000)
            throw new InvalidOperationException("Too many hidden-product descendants.");

        if (node.Children.Count == 0)
        {
            var read = ReadTree(node.Product, 0, maxDepth);
            node.Children.AddRange(read.Children);
        }

        foreach (var child in node.Children)
            AddDescendants(child, result, maxDepth);
    }

    private static List<ProductNode> ReadHiddenProductReferences(List<HiddenPart> hiddenRoots, int maxDepth)
    {
        var result = new List<ProductNode>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hiddenRoot in hiddenRoots)
        {
            var descendants = new List<ProductNode>();
            AddDescendants(hiddenRoot.Node, descendants, maxDepth);
            foreach (var node in descendants)
            {
                var key = Normalize(node.PartNumber);
                if (string.IsNullOrEmpty(key))
                    key = Normalize(node.Name);
                if (string.IsNullOrEmpty(key) || !seen.Add(key))
                    continue;
                result.Add(node);
            }
        }
        return result;
    }

    private static void RestoreProductOnlyHiddenNodes(List<ProductNode> allNodes)
    {
        foreach (var partNumber in ProductOnlyHiddenPartNumbers)
        {
            var node = allNodes.Find(delegate(ProductNode candidate)
            {
                return IsSameKey(candidate.PartNumber, partNumber);
            });
            if (node == null)
            {
                Console.Error.WriteLine("  RESTORE MISS {0}", partNumber);
                continue;
            }

            string error;
            if (TrySetNoShow(node.Product, out error))
                Console.WriteLine("  RESTORE HIDDEN {0}", DisplayName(node));
            else
                Console.Error.WriteLine("  RESTORE FAIL {0}: {1}", DisplayName(node), error);
        }
    }

    private static int ShowSpotsForFourAssemblies(List<ProductNode> allNodes, bool apply)
    {
        var referenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var partNumber in ProductOnlyHiddenPartNumbers)
        {
            var assemblies = allNodes.FindAll(delegate(ProductNode node)
            {
                return IsSameKey(node.PartNumber, partNumber);
            });
            if (assemblies.Count != 1)
                throw new InvalidOperationException("Expected one assembly: " + partNumber);

            var descendants = new List<ProductNode>();
            Flatten(assemblies[0], descendants);
            foreach (var node in descendants)
                referenceKeys.Add(Normalize(node.PartNumber));
            Console.WriteLine("Assembly {0}: {1} tree nodes", partNumber, descendants.Count);
        }

        var targets = allNodes.FindAll(delegate(ProductNode node)
        {
            return IsSameKey(node.PartNumber, TargetPartNumber);
        });
        if (targets.Count != 1)
            throw new InvalidOperationException("Expected one " + TargetPartNumber + " part.");

        var topLevel = ReadGeometrySets(targets[0].Product);
        var weldingSpot = topLevel.FindAll(delegate(GeometrySetInfo set)
        {
            return string.Equals(set.Name, "Welding Spot", StringComparison.OrdinalIgnoreCase);
        });
        if (weldingSpot.Count != 1)
            throw new InvalidOperationException("Expected one Welding Spot geometry set.");

        var spotSets = new List<GeometrySetInfo>();
        ReadChildGeometrySets(weldingSpot[0], spotSets);
        var matches = spotSets.FindAll(delegate(GeometrySetInfo set)
        {
            foreach (var key in SplitGeometrySetKeys(set.Name))
                if (referenceKeys.Contains(key))
                    return true;
            return false;
        });
        Console.WriteLine("Welding Spot sub-sets: {0}; matched to four assemblies: {1}",
            spotSets.Count, matches.Count);
        if (spotSets.Count != 37 || matches.Count != 29)
            throw new InvalidOperationException("Unexpected spot-set mapping; no visibility changes made.");

        var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in matches)
        {
            InvokeMethod(_selection, "Clear");
            InvokeMethod(_selection, "Search", "Name='" + set.Name + "',all");
            var count = TryInt(_selection, "Count2");
            if (count != 1)
                throw new InvalidOperationException("Non-unique CATIA search for " + set.Name + ": " + count);
            var selected = InvokeMethod(_selection, "Item2", 1);
            var value = TryObject(selected, "Value");
            if (!string.Equals(TryString(selected, "Type"), "HybridBody", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(TryString(value, "Name"), set.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Search result is not the expected HybridBody: " + set.Name);

            string error;
            var hidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (!string.IsNullOrEmpty(error))
                throw new InvalidOperationException("Visibility read failed for " + set.Name + ": " + error);
            states.Add(set.Name, hidden);
            Console.WriteLine("  {0} {1}", hidden ? "HIDDEN" : "SHOWN ", set.Name);
        }
        InvokeMethod(_selection, "Clear");

        if (!apply)
        {
            Console.WriteLine("DRY RUN: {0} spot sets need to be shown.",
                new List<bool>(states.Values).FindAll(delegate(bool hidden) { return hidden; }).Count);
            return 0;
        }

        var changed = 0;
        var failed = 0;
        foreach (var set in matches)
        {
            if (!states[set.Name])
                continue;
            string error;
            if (!TryShowByGlobalName(set.Name, out error))
            {
                failed++;
                Console.Error.WriteLine("  SHOW FAIL {0}: {1}", set.Name, error);
                continue;
            }
            var stillHidden = TryIsHiddenBySearch(set.HybridBody, out error);
            if (stillHidden || !string.IsNullOrEmpty(error))
            {
                failed++;
                Console.Error.WriteLine("  VERIFY FAIL {0}: {1}", set.Name, error ?? "still hidden");
                continue;
            }
            changed++;
            Console.WriteLine("  SHOW OK {0}", set.Name);
        }
        InvokeMethod(_selection, "Clear");
        Console.WriteLine("Spot sets shown: {0}; already shown: {1}; failures: {2}",
            changed, matches.Count - changed - failed, failed);
        return failed == 0 ? 0 : 5;
    }

    private static void ProbeHiddenSearch()
    {
        try
        {
            var queries = new[]
            {
                "CATAsmSearch.Product.Visibility=Hidden,all",
                "CATAsmSearch.Part.Visibility=Hidden,all",
                "CATAsmSearch.Part.InheritedVisibility=Hidden,all",
                "CATAsmSearch.Product.InheritedVisibility=Hidden,all",
                "CATAsmSearch.Product,vis:hidden,all",
                "CATAsmSearch.Part,vis:hidden,all"
            };

            Console.WriteLine();
            foreach (var query in queries)
            {
                InvokeMethod(_selection, "Clear");
                try
                {
                    InvokeMethod(_selection, "Search", query);
                    Console.WriteLine("CATIA search [{0}]: Count={1}, Count2={2}",
                        query, TryInt(_selection, "Count"), TryInt(_selection, "Count2"));

                    if (query == "CATAsmSearch.Product.Visibility=Hidden,all" ||
                        query == "CATAsmSearch.Part.Visibility=Hidden,all")
                    {
                        var count = TryInt(_selection, "Count2");
                        var limit = Math.Min(count, 100);
                        for (var i = 1; i <= limit; i++)
                        {
                            try
                            {
                                var selected = InvokeMethod(_selection, "Item2", i);
                                var value = TryObject(selected, "Value");
                                var leafProduct = TryObject(selected, "LeafProduct");
                                Console.WriteLine(
                                    "  RESULT {0}: type={1}; value={2} [{3} | {4}]; leaf={5} [{6} | {7}]",
                                    i,
                                    TryString(selected, "Type") ?? "?",
                                    value == null ? "<null>" : value.GetType().FullName,
                                    value == null ? "?" : (TryString(value, "PartNumber") ?? "?"),
                                    value == null ? "?" : (TryString(value, "Name") ?? "?"),
                                    leafProduct == null ? "<null>" : leafProduct.GetType().FullName,
                                    leafProduct == null ? "?" : (TryString(leafProduct, "PartNumber") ?? "?"),
                                    leafProduct == null ? "?" : (TryString(leafProduct, "Name") ?? "?"));
                                if (value != null && i <= 8)
                                {
                                    var referenceProduct = TryObject(value, "ReferenceProduct");
                                    var parentDocument = TryObject(referenceProduct, "Parent");
                                    Console.WriteLine("    REF   {0}: ref=[{1} | {2}]; parent={3}; part={4}",
                                        i,
                                        referenceProduct == null ? "?" : (TryString(referenceProduct, "PartNumber") ?? "?"),
                                        referenceProduct == null ? "?" : (TryString(referenceProduct, "Name") ?? "?"),
                                        parentDocument == null ? "?" : (TryString(parentDocument, "Name") ?? parentDocument.GetType().FullName),
                                        parentDocument == null ? "?" : (TryString(TryObject(parentDocument, "Part"), "Name") ?? "?"));
                                    var children = TryObject(value, "Products");
                                    var childCount = TryInt(children, "Count");
                                    Console.WriteLine("    CHILDREN {0}: {1}", i, childCount);
                                    for (var childIndex = 1; childIndex <= Math.Min(childCount, 20); childIndex++)
                                    {
                                        var child = InvokeMethod(children, "Item", childIndex);
                                        Console.WriteLine("      CHILD {0}.{1}: {2} | {3}",
                                            i,
                                            childIndex,
                                            TryString(child, "PartNumber") ?? "?",
                                            TryString(child, "Name") ?? "?");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine("  RESULT {0}: read failed: {1}", i, ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("CATIA search [{0}] failed: {1}", query, ex.Message);
                }
            }

            InvokeMethod(_selection, "Clear");
        }
        catch (Exception ex)
        {
            Console.WriteLine("CATIA vis:hidden search failed: {0}", ex.Message);
        }
    }

    private static int Main(string[] args)
    {
        var apply = HasFlag(args, "--apply");
        var maxDepth = ReadMaxDepth(args);
        var targetPartNumber = ReadTargetPartNumber(args);

        try
        {
            try
            {
                _app = Marshal.GetActiveObject(ProgId);
            }
            catch (COMException ex)
            {
                Console.Error.WriteLine("Cannot attach to CATIA.Application: {0}", ex.Message);
                Console.Error.WriteLine("Run with the same elevation as CATIA.");
                return 2;
            }

            var activeDocument = GetProperty(_app, "ActiveDocument");
            if (activeDocument == null)
                throw new InvalidOperationException("CATIA has no active document.");

            // CATIA Selection belongs to the active ProductDocument, not to
            // CATIA.Application. This is also the selection used by the
            // existing TxTools CATIA export bridge.
            _selection = GetProperty(activeDocument, "Selection");
            if (_selection == null)
                throw new InvalidOperationException("Active ProductDocument.Selection is null.");

            var rootProduct = GetProperty(activeDocument, "Product");
            if (rootProduct == null)
                throw new InvalidOperationException("The active document has no Product tree.");

            var root = ReadTree(rootProduct, 0, maxDepth);
            var allNodes = new List<ProductNode>();
            Flatten(root, allNodes);

            if (HasFlag(args, "--show-four-assembly-spots"))
                return ShowSpotsForFourAssemblies(allNodes, apply);

            if (HasFlag(args, "--restore-product-only-hidden"))
                RestoreProductOnlyHiddenNodes(allNodes);

            var hiddenParts = ReadHiddenProductsFromSearch();
            Console.WriteLine("Hidden Product instances: {0}", hiddenParts.Count);
            Console.WriteLine("Hidden Part-level instances: {0}",
                ReadSearchCount("CATAsmSearch.Part.Visibility=Hidden,all"));
            foreach (var hidden in hiddenParts)
                Console.WriteLine("  HIDDEN  {0}", DisplayName(hidden));

            var hiddenReferences = ReadHiddenProductReferences(hiddenParts, maxDepth);
            Console.WriteLine("Unique hidden-product references (including descendants): {0}", hiddenReferences.Count);
            foreach (var reference in hiddenReferences)
                Console.WriteLine("  REF     {0}", DisplayName(reference));

            var targetNodes = allNodes.FindAll(delegate(ProductNode node)
            {
                return IsSameKey(node.PartNumber, targetPartNumber) ||
                       IsSameKey(node.Name, targetPartNumber);
            });

            Console.WriteLine();
            Console.WriteLine("Target Product candidates for {0}: {1}", targetPartNumber, targetNodes.Count);
            foreach (var target in targetNodes)
                Console.WriteLine("  TARGET  {0}", DisplayName(target));

            if (targetNodes.Count != 1)
                throw new InvalidOperationException("Expected exactly one target Product candidate.");

            var targetPartDocument = ResolvePartDocument(targetNodes[0].Product);
            if (targetPartDocument == null)
                throw new InvalidOperationException("Target Product has no PartDocument.");

            var geometrySets = ReadGeometrySets(targetNodes[0].Product);
            Console.WriteLine();
            Console.WriteLine("Geometry sets in {0}: {1}", targetPartNumber, geometrySets.Count);
            foreach (var set in geometrySets)
                Console.WriteLine("  SET     {0}", set.Name ?? "?");

            var childGeometrySets = FlattenGeometrySets(geometrySets);
            Console.WriteLine("Nested geometry sets in {0}: {1}", targetPartNumber, childGeometrySets.Count);
            foreach (var set in childGeometrySets)
                Console.WriteLine("  SUBSET  {0} / {1}", set.ParentName ?? "?", set.Name ?? "?");

            if (HasFlag(args, "--toggle-top"))
                return ToggleGeometrySets(targetPartDocument, geometrySets);
            if (HasFlag(args, "--set-requested-toggle-result"))
                return SetRequestedTopToggleResult(targetPartDocument, geometrySets);
            if (HasFlag(args, "--show-hidden-top-global"))
                return ShowHiddenTopLevelFromGlobalSearch(targetPartDocument, geometrySets);
            if (HasFlag(args, "--invert-nested-result"))
                return InvertNestedGeometrySets(targetPartDocument, childGeometrySets);
            if (HasFlag(args, "--probe-nested-states"))
                return ProbeNestedStates(targetPartDocument, childGeometrySets);
            if (HasFlag(args, "--test-show-one"))
                return TestShowOneNested(targetPartDocument, childGeometrySets[0]);
            if (HasFlag(args, "--toggle"))
                return ToggleGeometrySets(targetPartDocument, childGeometrySets);
            if (HasFlag(args, "--probe-toggle"))
                return ProbeGeometrySetVisibility(targetPartDocument, geometrySets, childGeometrySets);

            var hiddenReferenceKeys = new Dictionary<string, ProductNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in hiddenReferences)
            {
                var key = Normalize(reference.PartNumber);
                if (string.IsNullOrEmpty(key))
                    key = Normalize(reference.Name);
                if (!string.IsNullOrEmpty(key))
                    hiddenReferenceKeys[key] = reference;
            }

            var matches = new List<GeometrySetInfo>();
            foreach (var set in childGeometrySets)
            {
                foreach (var setKey in SplitGeometrySetKeys(set.Name))
                {
                    ProductNode reference;
                    if (!hiddenReferenceKeys.TryGetValue(setKey, out reference))
                        continue;
                    set.MatchedKeys.Add(setKey);
                }
                if (set.MatchedKeys.Count > 0)
                    matches.Add(set);
            }

            Console.WriteLine();
            Console.WriteLine("Hidden-part descendant to geometry-set matches: {0}", matches.Count);
            var matchesByParent = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
            {
                var parentName = match.ParentName ?? "?";
                int parentCount;
                matchesByParent.TryGetValue(parentName, out parentCount);
                matchesByParent[parentName] = parentCount + 1;
                Console.WriteLine("  MATCH   {0} / {1}  <-  {2}",
                    match.ParentName ?? "?",
                    match.Name ?? "?",
                    string.Join(", ", match.MatchedKeys.ToArray()));
            }
            foreach (var pair in matchesByParent)
                Console.WriteLine("  MATCH COUNT {0}: {1}", pair.Key, pair.Value);

            if (HasFlag(args, "--invert-matched-result"))
            {
                SetRequestedTopToggleResult(targetPartDocument, geometrySets);
                return SetInverseOfMatchedGeometrySets(targetPartDocument, childGeometrySets, matches);
            }

            if (!apply)
            {
                Console.WriteLine();
                Console.WriteLine("DRY RUN only. Re-run with --apply to hide the matched geometry sets.");
                return 0;
            }

            // Validate all exact-name results before changing visibility.
            var alreadyHidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var match in matches)
            {
                InvokeMethod(_selection, "Clear");
                InvokeMethod(_selection, "Search", "Name='" + match.Name + "',all");
                var count = TryInt(_selection, "Count2");
                if (count != 1)
                    throw new InvalidOperationException("Non-unique CATIA search for " + match.Name + ": " + count);
                var selected = InvokeMethod(_selection, "Item2", 1);
                var value = TryObject(selected, "Value");
                if (!string.Equals(TryString(selected, "Type"), "HybridBody", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(TryString(value, "Name"), match.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Search result is not the expected HybridBody: " + match.Name);
                string readError;
                var hidden = TryIsHiddenBySearch(match.HybridBody, out readError);
                if (!string.IsNullOrEmpty(readError))
                    throw new InvalidOperationException("Visibility read failed for " + match.Name + ": " + readError);
                if (hidden)
                    alreadyHidden.Add(match.Name);
            }
            InvokeMethod(_selection, "Clear");
            Console.WriteLine("Preflight: {0} unique matched sets; {1} already hidden.", matches.Count, alreadyHidden.Count);

            var hiddenSetCount = 0;
            var failures = 0;
            foreach (var match in matches)
            {
                if (alreadyHidden.Contains(match.Name))
                    continue;
                string error;
                if (TryHideByGlobalName(match.Name, out error))
                {
                    hiddenSetCount++;
                    Console.WriteLine("  HIDE OK {0}", match.Name ?? "?");
                }
                else
                {
                    failures++;
                    Console.Error.WriteLine("  HIDE FAIL {0}: {1}", match.Name ?? "?", error);
                }
            }

            var verified = 0;
            foreach (var match in matches)
            {
                string error;
                if (TryIsHiddenBySearch(match.HybridBody, out error))
                    verified++;
                else if (!string.IsNullOrEmpty(error))
                    Console.Error.WriteLine("  VERIFY FAIL {0}: {1}", match.Name ?? "?", error);
            }

            Console.WriteLine();
            Console.WriteLine("Applied hidden geometry sets: {0}", hiddenSetCount);
            Console.WriteLine("Verified hidden geometry sets: {0}", verified);
            return failures == 0 && verified == matches.Count ? 0 : 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Hidden-part comparison failed: " + ex);
            return 1;
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static int ReadMaxDepth(string[] args)
    {
        foreach (var arg in args)
        {
            const string prefix = "--max-depth=";
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            int value;
            if (int.TryParse(arg.Substring(prefix.Length), out value) && value > 0)
                return value;
        }
        return 100;
    }

    private static string ReadTargetPartNumber(string[] args)
    {
        const string prefix = "--target=";
        foreach (var arg in args)
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg.Substring(prefix.Length).Trim();
                if (string.IsNullOrEmpty(value))
                    throw new ArgumentException("--target requires a part number.");
                return value;
            }
        return TargetPartNumber;
    }
}
