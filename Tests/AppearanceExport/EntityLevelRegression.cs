using System;
using System.Collections.Generic;
using Tecnomatix.Engineering;
using Tecnomatix.Planning;
using TxTools.ExportByColor;

internal abstract class EntityNode : ITxDisplayableObject, ITxLocatableObject
{
    public string Id { get; set; }
    public string Name { get { return Id; } }
    public TxTransformation AbsoluteLocation { get; set; }
    internal TxDisplayableObjectVisibility State = TxDisplayableObjectVisibility.None;
    internal bool Expired;
    public virtual TxDisplayableObjectVisibility Visibility { get { Valid(); return State; } }
    internal void Valid() { if (Expired) throw new Exception("Stale entity wrapper used: " + Id); }
    public virtual void Display() { Valid(); State = TxDisplayableObjectVisibility.All; }
    public virtual void Blank() { Valid(); State = TxDisplayableObjectVisibility.None; }
}

internal sealed class EntityMesh : EntityNode, ITxGeometry
{
    internal EntityPart Owner;
    internal void ReadTriangles()
    {
        Valid();
        if (Owner.RepresentationType != TxRepresentationType.Detailed || Visibility != TxDisplayableObjectVisibility.All)
            throw new Exception("Triangles read before entity-level load/display");
    }
}

internal sealed class EntityPart : EntityNode, ITxObjectCollection, ITxStorable, ITxComponent
{
    internal readonly List<string> Events;
    internal readonly TxLibraryStorage Storage = new TxLibraryStorage();
    internal readonly TxObjectList Coarse = new TxObjectList();
    internal readonly TxObjectList Detailed = new TxObjectList();
    internal Func<TxObjectList> NewCoarse;
    internal Func<TxObjectList> NewDetailed;
    internal bool RequireDetailedEnumeration, MixedVisibility, FailLoad;
    internal int Loads, Unloads;
    private TxObjectList current;
    public TxRepresentationType RepresentationType { get; private set; }
    public TxStorage StorageObject { get { return Storage; } }

    internal EntityPart(string id, List<string> events, TxRepresentationType initial = TxRepresentationType.United)
    {
        Id = id; Events = events; RepresentationType = initial;
        current = initial == TxRepresentationType.United ? Coarse : Detailed;
        Storage.ReloadAction = level =>
        {
            Events.Add(Id + ":reload:" + level);
            if (level == TxRepresentationLevel.Detailed)
            {
                if (Visibility != TxDisplayableObjectVisibility.All) throw new Exception("Load before showing source");
                Loads++;
                RepresentationType = TxRepresentationType.Detailed;
                current = NewDetailed == null ? Detailed : NewDetailed();
                State = TxDisplayableObjectVisibility.None; // loading can reset visibility
                if (FailLoad) throw new InvalidOperationException("fixture load failure");
            }
            else
            {
                Unloads++;
                RepresentationType = TxRepresentationType.United;
                current = NewCoarse == null ? Coarse : NewCoarse();
                State = TxDisplayableObjectVisibility.All;
            }
        };
    }

    public override TxDisplayableObjectVisibility Visibility
    {
        get
        {
            if (!MixedVisibility || current.Count == 0) return base.Visibility;
            bool all = true, none = true;
            foreach (ITxObject child in current)
            {
                var state = ((ITxDisplayableObject)child).Visibility;
                all &= state == TxDisplayableObjectVisibility.All;
                none &= state == TxDisplayableObjectVisibility.None;
            }
            return all ? TxDisplayableObjectVisibility.All : none ? TxDisplayableObjectVisibility.None : TxDisplayableObjectVisibility.Partial;
        }
    }
    public override void Display()
    {
        base.Display();
        foreach (ITxObject child in current) ((ITxDisplayableObject)child).Display();
    }
    public override void Blank()
    {
        base.Blank();
        foreach (ITxObject child in current) ((ITxDisplayableObject)child).Blank();
    }
    public TxObjectList GetDirectDescendants(TxNoTypeFilter filter) { return ReadChildren(); }
    public TxObjectList GetAllDescendants(TxNoTypeFilter filter) { return ReadChildren(); }
    private TxObjectList ReadChildren()
    {
        Events.Add(Id + ":enumerate:" + RepresentationType);
        if (RequireDetailedEnumeration && RepresentationType != TxRepresentationType.Detailed)
            throw new Exception("Enumerated entities before entity-level load");
        return current;
    }
}

internal sealed class EntityPlanning : ITxPlanningObject
{
    public ITxObject EngineeringRepresentation { get; set; }
}

internal sealed class EntityAppearance : EntityNode, ITxPartAppearance
{
    public ITxPlanningObject OriginPart { get; set; }
}

internal static class EntityLevelRegression
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
    private static EntityAppearance Appearance(string id, EntityPart source)
    {
        return new EntityAppearance { Id = id, OriginPart = new EntityPlanning { EngineeringRepresentation = source } };
    }
    private static EntityMesh Mesh(string id, EntityPart owner, TxDisplayableObjectVisibility state = TxDisplayableObjectVisibility.None)
    {
        return new EntityMesh { Id = id, Owner = owner, State = state };
    }
    private static void Main()
    {
        var events = new List<string>();
        var source = new EntityPart("source", events) { RequireDetailedEnumeration = true };
        var subset = new EntityPart("subset", events) { RequireDetailedEnumeration = true };
        source.Coarse.Add(subset); source.Detailed.Add(subset);
        var mesh = Mesh("mesh", subset); subset.Detailed.Add(mesh);
        using (var scope = new AppearanceGeometrySource.ReadScope(null))
        {
            var first = scope.Collect(Appearance("A", source));
            Check(first.Count == 1 && ReferenceEquals(first[0].Geometry, mesh), "Nested entities not loaded");
            mesh.ReadTriangles();
            Check(source.Loads == 1 && subset.Loads == 1, "Each nested storage must load once");
            var second = scope.Collect(Appearance("B", source));
            Check(source.Loads == 1 && subset.Loads == 1, "Shared source was reloaded repeatedly");
            Check(first[0].InstanceKey != second[0].InstanceKey, "Loaded source lost appearance identity");
            Check(source.RepresentationType == TxRepresentationType.Detailed && subset.RepresentationType == TxRepresentationType.Detailed,
                "Entity level was released before triangle reading");
        }
        Check(source.RepresentationType == TxRepresentationType.United && subset.RepresentationType == TxRepresentationType.United,
            "Original load levels were not restored");
        Check(source.Visibility == TxDisplayableObjectVisibility.None, "Hidden source not restored after unloading");
        Check(events.IndexOf("source:reload:Detailed") < events.IndexOf("source:enumerate:Detailed") &&
            events.IndexOf("subset:reload:Detailed") < events.IndexOf("subset:enumerate:Detailed"), "Load/enumeration ordering");
        Check(events.IndexOf("subset:reload:United") < events.IndexOf("source:reload:United"), "Unload children before parent");

        // Reload replaces coarse wrappers. Restoration must bind fresh objects by ID.
        var partial = new EntityPart("partial", events) { MixedVisibility = true };
        var oldShown = Mesh("shown", partial, TxDisplayableObjectVisibility.All);
        var oldHidden = Mesh("hidden", partial);
        partial.Coarse.Add(oldShown); partial.Coarse.Add(oldHidden);
        EntityMesh freshShown = null, freshHidden = null;
        partial.NewDetailed = () =>
        {
            oldShown.Expired = oldHidden.Expired = true;
            var list = new TxObjectList(); list.Add(Mesh("detailed", partial)); return list;
        };
        partial.NewCoarse = () =>
        {
            freshShown = Mesh("shown", partial, TxDisplayableObjectVisibility.All);
            freshHidden = Mesh("hidden", partial, TxDisplayableObjectVisibility.All);
            var list = new TxObjectList(); list.Add(freshShown); list.Add(freshHidden); return list;
        };
        using (var scope = new AppearanceGeometrySource.ReadScope(null))
            Check(scope.Collect(Appearance("partial-appearance", partial)).Count == 1, "Partial source load failed");
        Check(freshShown.Visibility == TxDisplayableObjectVisibility.All && freshHidden.Visibility == TxDisplayableObjectVisibility.None,
            "Mixed visibility was not restored using fresh wrappers");
        Check(partial.Visibility == TxDisplayableObjectVisibility.Partial, "Original partial state lost");

        var alreadyDetailed = new EntityPart("already-detailed", events, TxRepresentationType.Detailed);
        alreadyDetailed.Detailed.Add(Mesh("detailed-mesh", alreadyDetailed));
        using (var scope = new AppearanceGeometrySource.ReadScope(null)) scope.Collect(Appearance("C", alreadyDetailed));
        Check(alreadyDetailed.Loads == 1 && alreadyDetailed.Unloads == 0 &&
            alreadyDetailed.RepresentationType == TxRepresentationType.Detailed, "Original Detailed level must remain Detailed");

        var failure = new EntityPart("failed-source", events) { FailLoad = true, RequireDetailedEnumeration = true };
        bool failed = false;
        try { using (var scope = new AppearanceGeometrySource.ReadScope(null)) scope.Collect(Appearance("failed-appearance", failure)); }
        catch (InvalidOperationException) { failed = true; }
        Check(failed, "Load failure was hidden");
        Check(failure.RepresentationType == TxRepresentationType.United && failure.Visibility == TxDisplayableObjectVisibility.None,
            "Partial load failure did not restore level/visibility");
        Check(!events.Contains("failed-source:enumerate:Detailed"), "Enumerated after failed load");
        Console.WriteLine("PASS: " + checks + " entity-level load/lifetime/restore checks (SDK storage doubles)");
    }
}
