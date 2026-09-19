using System.Xml.Linq;
using static AmlMcp.AmlModel;

namespace AmlMcp;

/// <summary>
/// Builds the machine-readable shapes of <see cref="Dto"/> from a model, so that a tool
/// can return both a readable card and data a program can work with.
/// </summary>
internal static class Structured
{
    /// <summary>True when the reported value comes from a DefaultValue rather than a Value element.</summary>
    private static bool IsDefaultValue(XElement attribute) =>
        attribute.Element(C + "Value") is null && attribute.Element(C + "DefaultValue") is not null;

    public static ElementRef Ref(AmlModel m, XElement e) =>
        new(IdOf(e), NameOf(e), PathOf(e), HierarchyNameOf(e), m.ClassOf(e), MirrorReferenceOf(e) is not null);

    public static DocumentInfo Document(AmlModel m)
    {
        var hierarchies = m.Hierarchies.Select(ih =>
        {
            var elements = ih.Descendants(C + "InternalElement").ToList();
            var byClass = elements
                .GroupBy(e => ShortClass(m.ClassOf(e)) is { Length: > 0 } s ? s : "(no class)")
                .OrderByDescending(g => g.Count())
                .Select(g => new ClassCountInfo(g.Key, g.Count()))
                .ToList();
            return new HierarchyInfo(NameOf(ih), IdOf(ih), elements.Count,
                elements.Count(e => MirrorReferenceOf(e) is not null), DescriptionOf(ih), byClass);
        }).ToList();

        var resolvedRefs = m.Refs.Where(r => r.Resolved).ToList();
        var connections = m.Links.Where(l => l.CrossHierarchy)
            .Select(l => (A: l.HierarchyA!, B: l.HierarchyB!, Kind: "link"))
            .Concat(resolvedRefs.Where(r => HierarchyNameOf(r.Source) != HierarchyNameOf(r.Target))
                .Select(r => (A: HierarchyNameOf(r.Source)!, B: HierarchyNameOf(r.Target)!, Kind: "reference")))
            .Concat(m.Root.Descendants(C + "InternalElement")
                .Select(e => (Mirror: e, Master: m.MirrorMasterOf(e)))
                .Where(x => x.Master is not null && HierarchyNameOf(x.Mirror) != HierarchyNameOf(x.Master))
                .Select(x => (A: HierarchyNameOf(x.Mirror)!, B: HierarchyNameOf(x.Master)!, Kind: "mirror")))
            .GroupBy(x => (First: string.CompareOrdinal(x.A, x.B) <= 0 ? x.A : x.B,
                           Second: string.CompareOrdinal(x.A, x.B) <= 0 ? x.B : x.A, x.Kind))
            .Select(g => new HierarchyConnectionInfo(g.Key.First, g.Key.Second, g.Key.Kind, g.Count()))
            .ToList();

        return new DocumentInfo(
            m.FilePath, Path.GetFileName(m.FilePath), m.SchemaVersion, m.FileSize,
            m.IsContainer, m.RootPart,
            m.ContainerParts.Select(p => new ContainerPartInfo(p.Path, p.Size, p.Role)).ToList(),
            m.ExternalRefs.Select(r => new ExternalReferenceInfo(r.Alias, r.Path, r.Loaded, r.ResolvedFile, r.Problem, r.OutsideContainer)).ToList(),
            m.Classes.Values.GroupBy(c => (c.Library, c.External))
                .Select(g => new LibraryInfo(g.Key.Library, g.Count(), g.Key.External)).ToList(),
            hierarchies,
            m.Links.Count, m.Links.Count(l => l.CrossHierarchy), m.Links.Count(l => !l.Resolved),
            resolvedRefs.Count,
            resolvedRefs.Count(r => HierarchyNameOf(r.Source) != HierarchyNameOf(r.Target)),
            m.Refs.Count - resolvedRefs.Count,
            m.DuplicateIds.Distinct().Count(),
            connections);
    }

    public static ElementInfo Element(AmlModel m, XElement e, bool includeLayout)
    {
        var attributes = new List<AttributeInfo>();
        var hidden = 0;
        Collect(e, "");

        void Collect(XElement owner, string prefix)
        {
            foreach (var attribute in owner.Elements(C + "Attribute"))
            {
                if (!includeLayout && m.IsPresentationAttribute(attribute)) { hidden++; continue; }
                var path = prefix.Length == 0 ? NameOf(attribute) : prefix + "/" + NameOf(attribute);
                var value = (string?)attribute.Element(C + "Value");
                var fallback = (string?)attribute.Element(C + "DefaultValue");
                if (value is not null || fallback is not null || !attribute.Elements(C + "Attribute").Any())
                    attributes.Add(new AttributeInfo(path, value ?? fallback, (string?)attribute.Attribute("Unit"),
                        value is null && fallback is not null, null));
                Collect(attribute, path);
            }
        }

        foreach (var inherited in m.InheritedAttributes(e, includeLayout))
        {
            if (ValueOf(inherited.Attribute) is null && inherited.Attribute.Elements(C + "Attribute").Any()) continue;
            attributes.Add(new AttributeInfo(inherited.Path, ValueOf(inherited.Attribute),
                (string?)inherited.Attribute.Attribute("Unit"), IsDefaultValue(inherited.Attribute), inherited.ClassKey));
        }

        var master = m.MirrorMasterOf(e);
        var classPath = m.ClassOf(e);
        var classInfo = m.ResolveClass(classPath);

        return new ElementInfo(
            IdOf(e), NameOf(e), e.Name.LocalName, PathOf(e), HierarchyNameOf(e),
            master is null ? null : Ref(m, master),
            classPath, classInfo is not null,
            classInfo is null ? null : DescriptionOf(classInfo.Element),
            RolesOf(e).ToList(), DescriptionOf(e), attributes,
            e.Elements(C + "ExternalInterface").Select(i => new InterfaceInfo(
                IdOf(i), NameOf(i), (string?)i.Attribute("RefBaseClassPath"),
                m.MirrorMasterOf(i) is { } im ? PathOf(im) : null)).ToList(),
            e.Elements(C + "InternalElement").Select(c => Ref(m, c)).ToList(),
            m.EdgesOf(e, includeContainment: false).Select(edge => new ConnectionInfo(
                edge.Kind, edge.Detail, IdOf(edge.Target), PathOf(edge.Target),
                HierarchyNameOf(edge.Target), HierarchyNameOf(edge.Target) != HierarchyNameOf(e))).ToList(),
            m.Refs.Where(r => !r.Resolved && AnchorOf(r.Source) == e)
                .Select(r => $"{r.AttributePath} = {r.Value}").ToList(),
            hidden);
    }

    public static ClassDetailInfo Class(AmlModel m, ClassInfo info)
    {
        var cls = info.Element;
        var attributes = new List<AttributeInfo>();
        foreach (var (attribute, path) in OwnAttributes(cls))
        {
            if (m.IsInsidePresentation(attribute)) continue;
            // A group that only holds other attributes carries no value of its own.
            if (ValueOf(attribute) is null && attribute.Elements(C + "Attribute").Any()) continue;
            attributes.Add(new AttributeInfo(path, ValueOf(attribute), (string?)attribute.Attribute("Unit"),
                IsDefaultValue(attribute), null));
        }
        foreach (var inherited in m.InheritedAttributes(cls))
        {
            if (ValueOf(inherited.Attribute) is null && inherited.Attribute.Elements(C + "Attribute").Any()) continue;
            attributes.Add(new AttributeInfo(inherited.Path, ValueOf(inherited.Attribute),
                (string?)inherited.Attribute.Attribute("Unit"), IsDefaultValue(inherited.Attribute), inherited.ClassKey));
        }

        return new ClassDetailInfo(
            info.Key, info.Kind, info.Library, info.External, info.SourceFile, DescriptionOf(cls),
            m.ClassChain((string?)cls.Attribute("RefBaseClassPath")).Select(c => c.Key).ToList(),
            attributes,
            cls.Elements(C + "ExternalInterface").Select(i => $"{NameOf(i)} [{(string?)i.Attribute("RefBaseClassPath")}]").ToList(),
            cls.Elements(C + "SupportedRoleClass").Select(r => (string?)r.Attribute("RefRoleClassPath") ?? "").ToList(),
            m.Classes.Values.Where(c => m.ResolveClass((string?)c.Element.Attribute("RefBaseClassPath"))?.Element == cls)
                .Select(c => c.PlainPath).Distinct().ToList(),
            m.Root.Descendants().Count(x => ClassReferenceAttributes.Any(a => m.ResolveClass((string?)x.Attribute(a))?.Element == cls)));
    }

    public static ElementListInfo Search(AmlModel m, string? query, string? classContains,
                                         string? roleContains, string? hierarchy, int limit)
    {
        var q = query?.Trim() ?? "";
        var hits = m.Root.Descendants(C + "InternalElement").Where(e =>
        {
            if (!string.IsNullOrWhiteSpace(hierarchy) && !string.Equals(HierarchyNameOf(e), hierarchy, StringComparison.OrdinalIgnoreCase)) return false;
            var cls = m.ClassOf(e) ?? "";
            if (!string.IsNullOrWhiteSpace(classContains) && !Matches(m, cls, classContains)) return false;
            var roles = RolesOf(e).ToList();
            if (!string.IsNullOrWhiteSpace(roleContains) && !roles.Any(r => Matches(m, r, roleContains))) return false;
            if (q.Length == 0) return true;
            return NameOf(e).Contains(q, StringComparison.OrdinalIgnoreCase)
                   || (DescriptionOf(e)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                   || IdOf(e).Contains(q, StringComparison.OrdinalIgnoreCase)
                   || cls.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || roles.Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase));
        }).ToList();

        return new ElementListInfo(hits.Count, Math.Min(hits.Count, limit),
            hits.Take(limit).Select(e => Ref(m, e)).ToList());
    }

    public static bool Matches(AmlModel m, string classReference, string text) =>
        classReference.Contains(text, StringComparison.OrdinalIgnoreCase)
        || m.ClassChain(classReference).Any(c => c.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                                              || c.PlainPath.Contains(text, StringComparison.OrdinalIgnoreCase));

    public static CheckInfo Check(AmlModel m)
    {
        var problems = new List<ProblemInfo>();
        var notes = new List<ProblemInfo>();

        var unresolved = m.Root.Descendants()
            .SelectMany(e => ClassReferenceAttributes.Select(a => (Element: e, Attribute: a, Value: (string?)e.Attribute(a))))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Where(x => !(MirrorReferenceOf(x.Element) is not null &&
                          ((x.Attribute == "RefBaseSystemUnitPath" && x.Element.Name.LocalName == "InternalElement") ||
                           (x.Attribute == "RefBaseClassPath" && x.Element.Name.LocalName == "ExternalInterface"))))
            .Where(x => m.ResolveClass(x.Value, x.Element) is null)
            .GroupBy(x => x.Value!).ToList();

        var missingAliases = m.ExternalRefs.Where(r => !r.Loaded).Select(r => r.Alias).ToHashSet();
        foreach (var r in m.ExternalRefs.Where(r => !r.Loaded))
            (r.Remote ? notes : problems).Add(new ProblemInfo(r.Remote ? "remoteLibraryNotDownloaded" : "externalLibraryNotLoaded",
                $"{r.Alias} -> {r.Path} ({r.Problem})"));
        foreach (var r in m.ExternalRefs.Where(r => r.Loaded && r.Remote))
            notes.Add(new ProblemInfo("remoteLibraryLocalCopy", $"{r.Alias} -> {r.Path} read from {r.ResolvedFile}"));
        foreach (var r in m.NestedLibraryProblems)
            (r.Remote ? notes : problems).Add(new ProblemInfo(r.Remote ? "remoteLibraryNotDownloaded" : "nestedLibraryNotLoaded",
                $"{r.Alias} -> {r.Path} ({r.Problem})"));
        foreach (var g in unresolved.Where(g => !missingAliases.Any(a => g.Key.StartsWith(a + "@", StringComparison.Ordinal))))
            problems.Add(new ProblemInfo("unresolvedClassPath", $"{g.Key} (used {g.Count()}x)"));
        foreach (var dm in m.DanglingMirrors)
            problems.Add(m.ClassByName(dm.MasterId) is { } likely
                ? new ProblemInfo("classPathWithoutLibrary", $"{PathOf(dm.Mirror)} refers to {dm.MasterId}, probably {likely.Key}")
                : new ProblemInfo("mirrorWithoutMaster", $"{PathOf(dm.Mirror)} refers to {dm.MasterId}"));
        foreach (var l in m.Links.Where(l => !l.Resolved))
            problems.Add(new ProblemInfo("linkWithoutPartner", l.Name));
        foreach (var r in m.Refs.Where(r => !r.Resolved))
            problems.Add(new ProblemInfo("danglingReference", $"{PathOf(r.Source)}.{r.AttributePath} = {r.Value}"));
        foreach (var id in m.DuplicateIds.Distinct())
            problems.Add(new ProblemInfo("duplicateId", id));
        foreach (var id in m.CaseVariantIds.Distinct())
            notes.Add(new ProblemInfo("idCaseVariant", $"{id} appears with different letter case; treated as one object"));

        foreach (var r in m.ExternalRefs.Where(r => r.OutsideContainer))
            notes.Add(new ProblemInfo("libraryOutsideContainer", $"{r.Alias} -> {r.Path}"));
        foreach (var g in m.Links.Where(l => l.Resolved).SelectMany(l => new[] { l.InterfaceA!, l.InterfaceB! })
                     .GroupBy(i => i).Where(g => g.Count() > 1))
            notes.Add(new ProblemInfo("sharedInterface", $"{PathOf(g.Key)} is used by {g.Count()} links"));

        return new CheckInfo(Path.GetFileName(m.FilePath), problems.Count == 0, problems, notes,
            m.Classes.Count, m.Links.Count, m.Refs.Count, m.ById.Count);
    }

    public static PathInfo PathBetween(AmlModel m, XElement source, XElement target, bool includeContainment, int maxSteps)
    {
        if (source == target)
            return new PathInfo(true, Ref(m, source), Ref(m, target), 0, Array.Empty<PathStepInfo>(), "same element");

        var previous = new Dictionary<XElement, (XElement From, Edge Edge)>();
        var queue = new Queue<(XElement Node, int Steps)>();
        var visited = new HashSet<XElement> { source };
        queue.Enqueue((source, 0));

        while (queue.Count > 0)
        {
            var (node, steps) = queue.Dequeue();
            if (node == target) break;
            if (steps >= maxSteps) continue;
            foreach (var edge in m.EdgesOf(node, includeContainment))
            {
                if (!visited.Add(edge.Target)) continue;
                previous[edge.Target] = (node, edge);
                queue.Enqueue((edge.Target, steps + 1));
            }
        }

        if (!previous.ContainsKey(target))
            return new PathInfo(false, Ref(m, source), Ref(m, target), 0, Array.Empty<PathStepInfo>(),
                $"no connection within {maxSteps} steps");

        var chain = new List<PathStepInfo>();
        for (var current = target; current != source; current = previous[current].From)
        {
            var (_, edge) = previous[current];
            chain.Add(new PathStepInfo(edge.Kind, edge.Detail, Ref(m, edge.Target)));
        }
        chain.Reverse();
        return new PathInfo(true, Ref(m, source), Ref(m, target), chain.Count, chain, null);
    }

    public static List<ClassInfo> Classes(AmlModel m, string classPath)
    {
        var exact = m.ResolveClass(classPath);
        if (exact is not null) return new List<ClassInfo> { exact };
        return m.Classes.Values
            .Where(c => c.PlainPath.EndsWith("/" + classPath.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.PlainPath).Select(g => g.OrderBy(c => c.External).First()).ToList();
    }
}
