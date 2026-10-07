using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using TxTools.WeldAnnotator;

internal static class Program
{
    private static int _passed;
    private static void Main()
    {
        Run("Partial weld operation with 63 hidden locations", WeldOperation);
        Run("Repeat isolation retains baseline and hidden whitelist state", RepeatIsolation);
        Run("Whitelist uses scene ID and protects ancestors", WhitelistHierarchy);
        Run("Partial snapshot is rebuilt after parent cascade", PartialSnapshot);
        Run("Failures retain baseline and close undo transaction", FailureRetry);
        Run("Missing record does not show all", NoRecord);
        Run("Scene switch blocks stale snapshot", DocumentSwitch);
        Run("Unreadable visibility cancels before mutation", ReadFailure);
        Run("Invalid whitelist cancels before mutation", InvalidWhitelist);
        Run("Window sessions keep separate baselines", IndependentSessions);
        Run("Incomplete scene traversal cancels mutation", EnumerationFailure);
        Run("Deep trees and repeated roots are traversed once", DeepTree);
        Run("Missing undo manager prevents mutation", MissingUndo);
        Run("Named transaction signature remains supported", NamedTransaction);
        Run("Unsupported transaction signature prevents mutation", UnsupportedTransaction);
        Run("Missing end method is rejected before starting", MissingEndTransaction);
        Run("Transaction start failure prevents scene changes", StartFailure);
        Console.WriteLine("PASS: " + _passed + " regression scenarios");
    }

    private static DisplaySession NewSession()
    {
        TxApplication.ActiveDocument = new TxDocument();
        TxApplication.ActiveUndoManager = new UndoManager();
        return new DisplaySession(Console.WriteLine);
    }
    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine("PASS: " + name);
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected InvalidOperationException");
    }
    private static DisplayNode Physical(string id, bool visible = true)
    {
        var node = new DisplayNode(id, visible);
        TxApplication.ActiveDocument.PhysicalRoot.Children.Add(node);
        return node;
    }
    private static void WeldOperation()
    {
        var session = NewSession();
        var part = Physical("part");
        var op = new DisplayNode("1,1990") { Name = "R1(13)" };
        TxApplication.ActiveDocument.OperationRoot.Children.Add(op);
        for (int i = 0; i < 63; i++) op.Children.Add(new DisplayNode("location-" + i, false));
        Assert(op.Visibility == TxDisplayableObjectVisibility.Partial, "fixture must start Partial");
        Assert(session.ShowOnly(new[] { part }), "isolation must succeed");
        Assert(op.BlankCalls == 1 && op.Visibility == TxDisplayableObjectVisibility.None,
            "must explicitly blank weld operation itself");
        foreach (DisplayNode child in op.Children)
            Assert(child.Visibility == TxDisplayableObjectVisibility.None, "all 63 locations hidden");
        Assert(TxApplication.ActiveUndoManager.Starts == 1 && TxApplication.ActiveUndoManager.Ends == 1,
            "one undo transaction for whole change");
        Assert(session.Restore() && op.Visibility == TxDisplayableObjectVisibility.Partial,
            "restore original residual Partial");
        foreach (DisplayNode child in op.Children) Assert(!child.OwnVisible, "restore must keep points hidden");
    }
    private static void RepeatIsolation()
    {
        var session = NewSession();
        DisplayNode a = Physical("a"), b = Physical("b", false), c = Physical("c");
        Assert(session.ShowOnly(new[] { b }), "first isolation");
        Assert(session.ShowOnly(new[] { a }), "second isolation");
        Assert(session.Restore(), "restore repeated isolation");
        Assert(a.OwnVisible && !b.OwnVisible && c.OwnVisible,
            "restore first baseline including originally hidden whitelist object");
        Assert(!session.HasPendingChanges && !session.CanRestore, "automatic history cleared after success");
    }
    private static void WhitelistHierarchy()
    {
        var session = NewSession();
        var parent = Physical("parent");
        DisplayNode kept = new DisplayNode("keep"), sibling = new DisplayNode("sibling");
        parent.Children.Add(kept);
        parent.Children.Add(sibling);
        // 不同 SDK 包装实例，同一真实 ID；名字内的括号不参与查找。
        Assert(session.ShowOnly(new[] { new DisplayNode("keep") { Name = "keep(13)" } }), "identity by ID");
        Assert(kept.OwnVisible && !sibling.OwnVisible && parent.BlankCalls == 0, "protect ancestors");
        Assert(session.ShowOnly(new[] { parent }), "compound whitelist");
        Assert(kept.OwnVisible && sibling.OwnVisible, "retain whitelist descendants");
    }
    private static void PartialSnapshot()
    {
        var session = NewSession();
        var parent = Physical("parent");
        DisplayNode shown = new DisplayNode("shown"), hidden = new DisplayNode("hidden", false);
        parent.Children.Add(shown);
        parent.Children.Add(hidden);
        session.CaptureSnapshot();
        Assert(!session.HasPendingChanges, "taking snapshot alone has no display changes");
        Assert(session.ShowOnly(new[] { Physical("other") }), "hide partial component");
        Assert(session.Restore(), "restore partial component");
        Assert(parent.Visibility == TxDisplayableObjectVisibility.Partial && shown.OwnVisible && !hidden.OwnVisible,
            "hidden descendants must not be displayed by restoring parent");
        Assert(session.HasSnapshot && session.CanRestore && !session.HasPendingChanges, "manual snapshot reusable");
        parent.Display();
        Assert(session.Restore() && !hidden.OwnVisible, "repeat manual snapshot restoration");
    }
    private static void FailureRetry()
    {
        var session = NewSession();
        DisplayNode keep = Physical("keep"), other = Physical("other");
        other.FailBlank = true;
        Assert(!session.ShowOnly(new[] { keep }), "must report blank failure");
        Assert(session.HasPendingChanges && session.CanRestore, "failure preserves recovery record");
        Assert(!TxApplication.ActiveUndoManager.Open, "failed mutation must end transaction");
        other.FailBlank = false;
        Assert(session.ShowOnly(new[] { keep }), "retry isolation");
        other.FailDisplay = true;
        Assert(!session.Restore() && session.CanRestore && session.HasPendingChanges, "failed restore is retryable");
        other.FailDisplay = false;
        Assert(session.Restore() && other.OwnVisible && !session.HasPendingChanges, "restore retry");
        Assert(TxApplication.ActiveUndoManager.Starts == TxApplication.ActiveUndoManager.Ends, "undo balanced");
    }
    private static void NoRecord()
    {
        var session = NewSession();
        var hidden = Physical("hidden", false);
        Assert(session.Restore() && !hidden.OwnVisible, "no-record restore must not display hidden objects");
        Assert(TxApplication.ActiveUndoManager.Starts == 0, "no undo for no-op");
    }
    private static void DocumentSwitch()
    {
        var session = NewSession();
        var old = Physical("old");
        session.CaptureSnapshot();
        TxApplication.ActiveDocument = new TxDocument();
        Throws(delegate { session.Restore(); });
        Assert(old.OwnVisible && TxApplication.ActiveUndoManager.Starts == 0, "stale snapshot cannot mutate");
    }
    private static void ReadFailure()
    {
        var session = NewSession();
        var node = Physical("node");
        session.CaptureSnapshot();
        node.FailRead = true;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Throws(delegate { session.CaptureSnapshot(); });
        Assert(session.HasSnapshot && !session.HasPendingChanges && TxApplication.ActiveUndoManager.Starts == 0,
            "read failure preserves snapshot and prevents mutation");
    }
    private static void InvalidWhitelist()
    {
        var session = NewSession();
        var node = Physical("node");
        Throws(delegate { session.ShowOnly(new ITxObject[0]); });
        Throws(delegate { session.ShowOnly(new[] { new DisplayNode("missing") }); });
        Assert(node.OwnVisible && !session.HasPendingChanges && TxApplication.ActiveUndoManager.Starts == 0,
            "invalid whitelist must not blank scene");
    }
    private static void IndependentSessions()
    {
        var first = NewSession();
        DisplayNode a = Physical("a"), b = Physical("b");
        first.ShowOnly(new[] { a });
        var second = new DisplaySession(Console.WriteLine);
        second.ShowOnly(new[] { b });
        Assert(second.Restore() && a.OwnVisible && !b.OwnVisible, "second baseline");
        Assert(first.Restore() && a.OwnVisible && b.OwnVisible, "first baseline unaffected");
    }

    private static void EnumerationFailure()
    {
        var session = NewSession();
        var node = Physical("node");
        TxApplication.ActiveDocument.OperationRoot.FailEnumeration = true;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Assert(node.OwnVisible && TxApplication.ActiveUndoManager.Starts == 0, "incomplete tree cannot be modified");
    }
    private static void DeepTree()
    {
        var session = NewSession();
        var doc = TxApplication.ActiveDocument;
        doc.ComponentRoot = doc.PhysicalRoot;
        SceneNode parent = doc.OperationRoot;
        for (int i = 0; i < 40; i++)
        {
            var next = new SceneNode("branch-" + i);
            parent.Children.Add(next);
            parent = next;
        }
        var op = new DisplayNode("deep-op");
        parent.Children.Add(op);
        var part = Physical("part");
        Assert(session.CaptureSnapshot() == 2, "no depth truncation or duplicate roots");
        Assert(session.ShowOnly(new[] { part }) && !op.OwnVisible && op.BlankCalls == 1, "deep operation hidden once");
    }
    private static void MissingUndo()
    {
        var session = NewSession();
        var node = Physical("node", false);
        TxApplication.ActiveUndoManager = null;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Assert(!node.OwnVisible && !session.HasPendingChanges, "no changes without undo transaction");
    }

    private static void NamedTransaction()
    {
        var session = NewSession();
        var node = Physical("node", false);
        var undo = new NamedUndoManager();
        TxApplication.ActiveUndoManager = undo;
        Assert(session.ShowOnly(new[] { node }) && node.OwnVisible, "string signature must work");
        Assert(undo.Starts == 1 && undo.Ends == 1 && !string.IsNullOrEmpty(undo.LastName), "named transaction balanced");
        Assert(session.Restore() && !node.OwnVisible && undo.Starts == 2 && undo.Ends == 2, "named restore transaction");
    }
    private static void UnsupportedTransaction()
    {
        var session = NewSession();
        var node = Physical("node", false);
        var undo = new UnsupportedUndoManager();
        TxApplication.ActiveUndoManager = undo;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Assert(!node.OwnVisible && !session.HasPendingChanges && undo.Starts == 0 && undo.Ends == 0,
            "unsupported signature cannot start or modify scene");
    }
    private static void MissingEndTransaction()
    {
        var session = NewSession();
        var node = Physical("node", false);
        var undo = new MissingEndUndoManager();
        TxApplication.ActiveUndoManager = undo;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Assert(!node.OwnVisible && !session.HasPendingChanges && undo.Starts == 0 && !undo.Open,
            "end must be verified before start");
    }
    private static void StartFailure()
    {
        var session = NewSession();
        var node = Physical("node", false);
        var undo = TxApplication.ActiveUndoManager;
        undo.FailStart = true;
        Throws(delegate { session.ShowOnly(new[] { node }); });
        Assert(!node.OwnVisible && !session.HasPendingChanges && undo.Starts == 0 && undo.Ends == 0,
            "failed start cannot modify scene or close an unopened transaction");
    }
}
