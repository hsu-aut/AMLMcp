using System.ComponentModel;
using System.Text;
using System.Xml.Linq;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static AmlMcp.AmlModel;

namespace AmlMcp;

/// <summary>
/// Read-only MCP tools over AutomationML (CAEX 3.0 and 2.15) documents. Every
/// answer names element IDs and paths so a client can verify each statement in
/// the model itself.
/// </summary>
[McpServerToolType]
public static class AmlTools
{
    private const string DocumentParameter =
        "Optional path of an .aml file. Omit to use the most recently opened document.";

    private static CallToolResult Answer(string text, object? data) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        StructuredContent = data is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(data, Json.Options),
    };

    [McpServerTool(Name = "open_aml_document", ReadOnly = true, Idempotent = true)]
    [Description("Opens an AutomationML document (.aml with CAEX 3.0 or 2.15, or an .amlx container) and returns an overview: " +
                 "for containers the root document and all packaged parts with their roles, external library references " +
                 "and whether they resolve, class libraries, instance hierarchies with element counts by class, and " +
                 "how the hierarchies are connected (InternalLinks, references and mirror objects across hierarchies). " +
                 "Call this first. The document stays open for the other tools and is reloaded automatically when the file changes.")]
    public static CallToolResult OpenAmlDocument(

        DocumentStore store,
        [Description("Absolute or relative path to the .aml file.")] string path)
    {
        // One model for both halves; the store may have moved on under a concurrent call.
        var m = store.Open(path);
        return Answer(OpenDocumentText(m), Structured.Document(m));
    }

    [McpServerTool(Name = "get_tree", ReadOnly = true, Idempotent = true)]
    [Description("Shows the containment tree below an instance hierarchy or element, with class, ID, mirror objects and a " +
                 "marker for elements that are connected to other hierarchies. Use it to orient yourself in a hierarchy.")]
    public static CallToolResult GetTree(
        DocumentStore store,
        [Description("Hierarchy name, element ID, path (Hierarchy/Parent/Child) or unique element name. Omit for all hierarchies.")] string? element = null,
        [Description("How many levels to show (1 to 6).")] int depth = 3,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        var nodes = new List<TreeNodeInfo>();
        var clamped = Math.Clamp(depth, 1, 6);
        var truncated = false;
        var text = TreeText(m, element, clamped, nodes, out truncated);
        return Answer(text, new TreeInfo(string.IsNullOrWhiteSpace(element) ? null : element.Trim(), clamped, truncated, nodes));
    }

    public static string TreeText(DocumentStore store, string? element = null, int depth = 3, string? document = null) =>
        TreeText(store.Get(document), element, Math.Clamp(depth, 1, 6), null, out _);

    /// <summary>One walk that renders the tree and, when asked, records it for the structured half.</summary>
    public static string TreeText(AmlModel m, string? element, int depth, List<TreeNodeInfo>? collect, out bool truncated)
    {
        var roots = string.IsNullOrWhiteSpace(element) ? m.Hierarchies : new List<XElement> { m.ResolveElement(element) };
        var sb = new StringBuilder();
        var budget = 400;

        foreach (var root in roots)
        {
            var node = Write(root, 0);
            if (node is not null) collect?.Add(node);
        }
        truncated = budget <= 0;
        if (truncated) sb.AppendLine("... output truncated; narrow down with a smaller depth or a sub-element.");
        return sb.ToString();

        TreeNodeInfo? Write(XElement e, int level)
        {
            if (budget <= 0) return null;
            budget--;
            var cross = m.EdgesOf(e, includeContainment: false)
                .Count(x => HierarchyNameOf(x.Target) != HierarchyNameOf(e));
            var cls = ShortClass(m.ClassOf(e));
            var master = m.MirrorMasterOf(e);
            sb.Append(new string(' ', level * 2))
              .Append(NameOf(e))
              .Append(cls.Length > 0 ? $"  [{cls}]" : e.Name.LocalName == "InstanceHierarchy" ? "  [InstanceHierarchy]" : "")
              .Append(master is not null ? $"  (mirror of {PathOf(master)})" : "")
              .Append(cross > 0 ? $"  <{cross} cross-hierarchy connection(s)>" : "")
              .AppendLine($"  id={IdOf(e)}");

            var children = e.Elements(C + "InternalElement").ToList();
            var childNodes = new List<TreeNodeInfo>();
            var hidden = 0;
            if (level + 1 >= depth)
            {
                hidden = children.Count;
                if (children.Count > 0) sb.AppendLine($"{new string(' ', (level + 1) * 2)}... {children.Count} child element(s)");
            }
            else
            {
                foreach (var child in children)
                {
                    var node = Write(child, level + 1);
                    if (node is null) hidden++;
                    else childNodes.Add(node);
                }
            }

            return new TreeNodeInfo(IdOf(e), NameOf(e), PathOf(e), m.ClassOf(e), master is null ? null : PathOf(master),
                cross, hidden, childNodes);
        }
    }

    // ------------------------------------------------------------------ search

    [McpServerTool(Name = "find_elements", ReadOnly = true, Idempotent = true)]
    [Description("Searches internal elements by text in name, description, ID, class or role, optionally restricted " +
                 "to a class, a role or one instance hierarchy. Class and role filters follow inheritance: filtering " +
                 "for a base class also finds instances of its subclasses. Returns path, class and ID per hit.")]
    public static CallToolResult FindElements(

        DocumentStore store,
        [Description("Text to search for (case-insensitive). May be empty when filtering by class or role only.")] string? query = null,
        [Description("Only elements whose system unit class or one of its base classes contains this text, e.g. FPD_ProcessOperator or PT_Transition.")] string? classContains = null,
        [Description("Only elements with a role requirement whose role class or one of its base classes contains this text.")] string? roleContains = null,
        [Description("Only elements in this instance hierarchy.")] string? hierarchy = null,
        [Description("Maximum number of results.")] int limit = 30,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        return Answer(FindElementsText(m, query, classContains, roleContains, hierarchy, limit),
            Structured.Search(m, query, classContains, roleContains, hierarchy, Math.Clamp(limit, 1, 200)));
    }

    [McpServerTool(Name = "get_element", ReadOnly = true, Idempotent = true)]
    [Description("Returns a compact card for one element: path, hierarchy, class (resolved against the libraries), roles, " +
                 "description, own attributes, attributes inherited from the class chain, interfaces, children, and all " +
                 "connections: InternalLinks, references and mirror relations in both directions, each with the partner's " +
                 "path, hierarchy and ID. Diagram layout attributes are hidden unless requested.")]
    public static CallToolResult GetElement(

        DocumentStore store,
        [Description("Element ID (braces optional), path (Hierarchy/Parent/Child) or unique element name.")] string element,
        [Description("Include diagram layout attributes (positions, bounds, waypoints).")] bool includeLayout = false,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        return Answer(ElementCard(m, element, includeLayout), Structured.Element(m, m.ResolveElement(element), includeLayout));
    }

    [McpServerTool(Name = "get_neighbors", ReadOnly = true, Idempotent = true)]
    [Description("Follows connections from an element up to a given depth: InternalLinks, references (refObj family " +
                 "and similar) and mirror relations in both directions, and optionally parent/child containment. Use " +
                 "crossHierarchyOnly to see how e.g. a process view, a behaviour view and a plant structure are tied together.")]
    public static CallToolResult GetNeighbors(
        DocumentStore store,
        [Description("Element ID, path or unique name.")] string element,
        [Description("How many hops to follow (1 to 4).")] int depth = 1,
        [Description("Also follow parent/child containment.")] bool includeContainment = false,
        [Description("Only report connections that end in a different instance hierarchy.")] bool crossHierarchyOnly = false,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        var found = new List<NeighborInfo>();
        var clamped = Math.Clamp(depth, 1, 4);
        var start = m.ResolveElement(element);
        var text = NeighborsText(m, start, clamped, includeContainment, crossHierarchyOnly, found);
        return Answer(text, new NeighborsInfo(Structured.Ref(m, start), clamped, found.Count, found));
    }

    public static string NeighborsText(DocumentStore store, string element, int depth = 1,
        bool includeContainment = false, bool crossHierarchyOnly = false, string? document = null)
    {
        var m = store.Get(document);
        return NeighborsText(m, m.ResolveElement(element), Math.Clamp(depth, 1, 4), includeContainment, crossHierarchyOnly, null);
    }

    /// <summary>One traversal that renders the neighbours and, when asked, records them.</summary>
    public static string NeighborsText(AmlModel m, XElement start, int depth, bool includeContainment,
        bool crossHierarchyOnly, List<NeighborInfo>? collect)
    {
        var sb = new StringBuilder($"Neighbours of {PathOf(start)} (id={IdOf(start)}), depth {depth}:\n");
        var seen = new HashSet<XElement> { start };
        var frontier = new List<XElement> { start };
        var reported = 0;

        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<XElement>();
            foreach (var node in frontier)
            {
                foreach (var edge in m.EdgesOf(node, includeContainment))
                {
                    if (!seen.Add(edge.Target)) continue;
                    next.Add(edge.Target);
                    var cross = HierarchyNameOf(edge.Target) != HierarchyNameOf(node);
                    if (crossHierarchyOnly && !cross) continue;
                    collect?.Add(new NeighborInfo(level, edge.Kind, edge.Detail,
                        Structured.Ref(m, AnchorOf(node)), Structured.Ref(m, AnchorOf(edge.Target)), cross));
                    if (reported++ >= 150) continue;
                    sb.AppendLine($"{new string(' ', level * 2)}hop {level} from {NameOf(node)}: {FormatEdge(m, node, edge)}");
                }
            }
            frontier = next;
        }

        if (reported == 0) return sb.Append("  none").ToString();
        if (reported > 150) sb.AppendLine($"... {reported - 150} more");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ path

    [McpServerTool(Name = "find_path", ReadOnly = true, Idempotent = true)]
    [Description("Finds the shortest chain of connections between two elements (links, references, mirror relations and, " +
                 "unless disabled, containment). Answers questions like 'how is this Petri net transition related to that process operator'.")]
    public static CallToolResult FindPath(

        DocumentStore store,
        [Description("Start element: ID, path or unique name.")] string from,
        [Description("Target element: ID, path or unique name.")] string to,
        [Description("Allow parent/child steps. Disable to see only semantic connections.")] bool includeContainment = true,
        [Description("Maximum number of steps (1 to 20).")] int maxSteps = 12,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        return Answer(FindPathText(m, from, to, includeContainment, maxSteps),
            Structured.PathBetween(m, m.ResolveElement(from), m.ResolveElement(to), includeContainment, Math.Clamp(maxSteps, 1, 20)));
    }

    [McpServerTool(Name = "get_class", ReadOnly = true, Idempotent = true)]
    [Description("Looks up a class from the role, system unit, interface or attribute type libraries (embedded or " +
                 "referenced externally): description, inheritance chain, own and inherited attributes with defaults, " +
                 "interfaces, supported roles, subclasses, and how many elements in the document use it. Accepts a full " +
                 "path or a class name; a name shared by a role and a system unit class returns both.")]
    public static CallToolResult GetClass(

        DocumentStore store,
        [Description("Class path (e.g. VDI_FPD_DomainLibrary@VDI_FPD_SystemUnitClassLib/FPD_ProcessOperator) or just the class name.")] string classPath,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        var matches = Structured.Classes(m, classPath);
        // Wrapped in an object: MCP structured content is not an array.
        return Answer(ClassCard(m, classPath),
            new ClassMatchesInfo(matches.Count, matches.Select(c => Structured.Class(m, c)).ToList()));
    }

    [McpServerTool(Name = "list_classes", ReadOnly = true, Idempotent = true)]
    [Description("Lists classes from all libraries available to the document, optionally filtered by library, kind " +
                 "(RoleClass, SystemUnitClass, InterfaceClass, AttributeType) or text.")]
    public static CallToolResult ListClasses(
        DocumentStore store,
        [Description("Only classes whose library name contains this text.")] string? library = null,
        [Description("RoleClass, SystemUnitClass, InterfaceClass or AttributeType.")] string? kind = null,
        [Description("Only classes whose path or description contains this text.")] string? query = null,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        var list = MatchingClasses(m, library, kind, query);
        return Answer(ListClassesText(m, library, kind, query),
            new ClassListInfo(list.Count, Math.Min(list.Count, 150), list.Take(150)
                .Select(c => new ClassSummaryInfo(c.Key, c.Kind, c.Library, c.External, DescriptionOf(c.Element)))
                .ToList()));
    }

    public static string ListClassesText(DocumentStore store, string? library = null, string? kind = null,
        string? query = null, string? document = null) => ListClassesText(store.Get(document), library, kind, query);

    public static string ListClassesText(AmlModel m, string? library, string? kind, string? query)
    {
        var list = MatchingClasses(m, library, kind, query);
        if (list.Count == 0) return "No matching classes.";
        var sb = new StringBuilder($"{list.Count} class(es){(list.Count > 150 ? ", showing 150" : "")}:\n");
        foreach (var c in list.Take(150))
            sb.AppendLine($"  {c.Key}  ({c.Kind}){(DescriptionOf(c.Element) is { } d ? "  " + Shorten(d, 90) : "")}");
        return sb.ToString();
    }

    private static List<ClassInfo> MatchingClasses(AmlModel m, string? library, string? kind, string? query)
    {
        return m.Classes.Values
            .GroupBy(c => c.PlainPath).Select(g => g.OrderBy(c => c.External).First())
            .Where(c => string.IsNullOrWhiteSpace(library) || c.Library.Contains(library, StringComparison.OrdinalIgnoreCase))
            .Where(c => string.IsNullOrWhiteSpace(kind) || c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Where(c => string.IsNullOrWhiteSpace(query)
                        || c.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (DescriptionOf(c.Element)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(c => c.Library).ThenBy(c => c.PlainPath)
            .ToList();
    }

    // ------------------------------------------------------------------ checks

    [McpServerTool(Name = "check_references", ReadOnly = true, Idempotent = true)]
    [Description("Checks the document's referential integrity: class paths that do not resolve against embedded or " +
                 "external libraries, InternalLinks with missing partners, dangling references, mirror objects whose " +
                 "master is missing, duplicate IDs, external libraries that could not be loaded, and interfaces shared by several links.")]
    public static CallToolResult CheckReferences(

        DocumentStore store,
        [Description(DocumentParameter)] string? document = null)
    {
        var m = store.Get(document);
        return Answer(CheckText(m), Structured.Check(m));
    }


    // ------------------------------------------------------------------ open

    public static string OpenDocumentText(DocumentStore store, string path) => OpenDocumentText(store.Open(path));

    public static string OpenDocumentText(AmlModel m)
    {
        var sb = new StringBuilder();

        sb.AppendLine(m.IsContainer
            ? $"Document: {Path.GetFileName(m.FilePath)}  (AMLX container, root document {m.RootPart}, CAEX {m.SchemaVersion}, {FormatSize(m.FileSize)})"
            : $"Document: {Path.GetFileName(m.FilePath)}  (CAEX {m.SchemaVersion}, {FormatSize(m.FileSize)})");
        sb.AppendLine($"Path: {m.FilePath}");

        if (m.IsContainer)
        {
            sb.AppendLine($"\nContainer parts ({m.ContainerParts.Count}):");
            foreach (var part in m.ContainerParts.Take(40))
                sb.AppendLine($"  {part.Path}  ({part.Role ?? "no declared role"}, {FormatSize(part.Size)})");
            if (m.ContainerParts.Count > 40) sb.AppendLine($"  ... {m.ContainerParts.Count - 40} more");
            var other = m.ContainerParts.Count(p => !p.Path.EndsWith(".aml", StringComparison.OrdinalIgnoreCase));
            if (other > 0)
                sb.AppendLine($"  {other} part(s) are not AutomationML (e.g. geometry, logic, documents); their content is not read.");
        }

        if (m.ExternalRefs.Count > 0)
        {
            sb.AppendLine($"\nExternal references ({m.ExternalRefs.Count}):");
            foreach (var r in m.ExternalRefs)
            {
                var state = !r.Loaded ? "NOT loaded: " + r.Problem
                    : r.OutsideContainer ? "loaded from disk, NOT from the container"
                    : m.IsContainer ? "loaded from the container"
                    : "loaded";
                sb.AppendLine($"  {r.Alias} -> {r.Path}  [{state}]");
            }
        }

        var local = m.Classes.Values.Where(c => !c.External).GroupBy(c => c.Library).ToList();
        var external = m.Classes.Values.Where(c => c.External).GroupBy(c => c.Library).ToList();
        sb.AppendLine($"\nClass libraries: {local.Count} embedded, {external.Count} from external files");
        foreach (var g in local.Concat(external))
            sb.AppendLine($"  {g.Key}: {g.Count()} classes{(g.First().External ? " (external)" : "")}");

        sb.AppendLine($"\nInstance hierarchies ({m.Hierarchies.Count}):");
        foreach (var ih in m.Hierarchies)
        {
            var elements = ih.Descendants(C + "InternalElement").ToList();
            var mirrors = elements.Count(e => MirrorReferenceOf(e) is not null);
            var byClass = elements.GroupBy(e => ShortClass(m.ClassOf(e)) is { Length: > 0 } s ? s : "(no class)")
                .OrderByDescending(g => g.Count()).Take(6)
                .Select(g => $"{g.Key} {g.Count()}");
            sb.AppendLine($"  {NameOf(ih)}: {elements.Count} elements{(mirrors > 0 ? $", {mirrors} of them mirrors" : "")}  [{string.Join(", ", byClass)}]");
            if (DescriptionOf(ih) is { } d) sb.AppendLine($"    {Shorten(d, 160)}");
        }

        var crossLinks = m.Links.Count(l => l.CrossHierarchy);
        var unresolvedLinks = m.Links.Count(l => !l.Resolved);
        var resolvedRefs = m.Refs.Where(r => r.Resolved).ToList();
        var crossRefs = resolvedRefs.Count(r => HierarchyNameOf(r.Source) != HierarchyNameOf(r.Target));
        sb.AppendLine($"\nInternalLinks: {m.Links.Count} total, {crossLinks} across hierarchies, {unresolvedLinks} unresolved");
        sb.AppendLine($"References (refObj family and similar): {resolvedRefs.Count} resolved ({crossRefs} across hierarchies), {m.Refs.Count - resolvedRefs.Count} dangling");

        var crossPairs = m.Links.Where(l => l.CrossHierarchy).Select(l => (l.HierarchyA!, l.HierarchyB!, "link"))
            .Concat(resolvedRefs.Where(r => HierarchyNameOf(r.Source) != HierarchyNameOf(r.Target))
                .Select(r => (HierarchyNameOf(r.Source)!, HierarchyNameOf(r.Target)!, "reference")))
            .Concat(m.Root.Descendants(C + "InternalElement")
                .Select(e => (Mirror: e, Master: m.MirrorMasterOf(e)))
                .Where(x => x.Master is not null && HierarchyNameOf(x.Mirror) != HierarchyNameOf(x.Master))
                .Select(x => (HierarchyNameOf(x.Mirror)!, HierarchyNameOf(x.Master)!, "mirror")))
            .GroupBy(p => (Order(p.Item1, p.Item2), p.Item3))
            .ToList();
        if (crossPairs.Count > 0)
        {
            sb.AppendLine("\nHow the hierarchies connect:");
            foreach (var g in crossPairs)
                sb.AppendLine($"  {g.Key.Item1.Item1} <-> {g.Key.Item1.Item2}: {g.Count()} {g.Key.Item2}(s)");
        }

        if (m.DuplicateIds.Count > 0)
            sb.AppendLine($"\nWarning: {m.DuplicateIds.Count} duplicate IDs in the document.");

        sb.AppendLine("\nNext: get_tree for structure, find_elements to search, get_element for details, get_neighbors / find_path to follow connections.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ tree

    public static string FindElementsText(DocumentStore store, string? query = null, string? classContains = null,
        string? roleContains = null, string? hierarchy = null, int limit = 30, string? document = null) =>
        FindElementsText(store.Get(document), query, classContains, roleContains, hierarchy, limit);

    public static string FindElementsText(AmlModel m, string? query = null, string? classContains = null,
        string? roleContains = null, string? hierarchy = null, int limit = 30)
    {
        limit = Math.Clamp(limit, 1, 200);
        var q = query?.Trim() ?? "";

        var hits = m.Root.Descendants(C + "InternalElement").Where(e =>
        {
            if (!string.IsNullOrWhiteSpace(hierarchy) && !string.Equals(HierarchyNameOf(e), hierarchy, StringComparison.OrdinalIgnoreCase)) return false;
            var cls = m.ClassOf(e) ?? "";
            if (!string.IsNullOrWhiteSpace(classContains) && !MatchesWithInheritance(m, cls, classContains)) return false;
            var roles = RolesOf(e).ToList();
            if (!string.IsNullOrWhiteSpace(roleContains) && !roles.Any(r => MatchesWithInheritance(m, r, roleContains))) return false;
            if (q.Length == 0) return true;
            return NameOf(e).Contains(q, StringComparison.OrdinalIgnoreCase)
                   || (DescriptionOf(e)?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                   || IdOf(e).Contains(q, StringComparison.OrdinalIgnoreCase)
                   || cls.Contains(q, StringComparison.OrdinalIgnoreCase)
                   || roles.Any(r => r.Contains(q, StringComparison.OrdinalIgnoreCase));
        }).ToList();

        if (hits.Count == 0) return "No matching elements.";
        var sb = new StringBuilder($"{hits.Count} match(es){(hits.Count > limit ? $", showing {limit}" : "")}:\n");
        foreach (var e in hits.Take(limit))
            sb.AppendLine($"  {PathOf(e)}  [{ShortClass(m.ClassOf(e))}]{(MirrorReferenceOf(e) is not null ? "  (mirror)" : "")}  id={IdOf(e)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ element

    public static string ElementCard(DocumentStore store, string element, bool includeLayout = false, string? document = null) =>
        ElementCard(store.Get(document), element, includeLayout);

    public static string ElementCard(AmlModel m, string element, bool includeLayout = false)
    {
        var e = m.ResolveElement(element);
        var sb = new StringBuilder();

        sb.AppendLine($"{NameOf(e)}  ({e.Name.LocalName})");
        sb.AppendLine($"  id: {IdOf(e)}");
        sb.AppendLine($"  path: {PathOf(e)}");
        sb.AppendLine($"  hierarchy: {HierarchyNameOf(e)}");

        if (MirrorReferenceOf(e) is { } masterId)
        {
            var master = m.MirrorMasterOf(e);
            sb.AppendLine(master is null
                ? $"  mirror of: {masterId}  (master NOT found in the document)"
                : $"  mirror of: {PathOf(master)}  id={IdOf(master)}  [{HierarchyNameOf(master)}]  (a mirror represents the master object; ask get_element for the master's full data)");
        }

        if (m.ClassOf(e) is { } suc)
        {
            var info = m.ResolveClass(suc);
            sb.AppendLine($"  class: {suc}{(info is null ? "  (NOT resolved)" : "")}");
            if (info is not null && DescriptionOf(info.Element) is { } cd) sb.AppendLine($"    class meaning: {Shorten(cd, 220)}");
        }
        foreach (var role in RolesOf(e))
            sb.AppendLine($"  role: {role}{(m.ResolveClass(role) is null ? "  (NOT resolved)" : "")}");
        if (DescriptionOf(e) is { } d) sb.AppendLine($"  description: {Shorten(d, 400)}");

        var lines = new List<string>();
        var hiddenLayout = 0;
        CollectAttributes(m, e, "", includeLayout, lines, ref hiddenLayout);
        if (lines.Count > 0)
        {
            sb.AppendLine("  attributes:");
            foreach (var line in lines.Take(60)) sb.AppendLine("    " + line);
            if (lines.Count > 60) sb.AppendLine($"    ... {lines.Count - 60} more");
        }
        if (hiddenLayout > 0) sb.AppendLine($"  ({hiddenLayout} layout attribute group(s) hidden, use includeLayout=true)");

        AppendInherited(sb, m, e, includeLayout);

        var interfaces = e.Elements(C + "ExternalInterface").ToList();
        if (interfaces.Count > 0)
        {
            sb.AppendLine("  interfaces:");
            foreach (var i in interfaces.Take(40))
                sb.AppendLine($"    {NameOf(i)}  [{InterfaceClassLabel(m, i)}]  id={IdOf(i)}");
            if (interfaces.Count > 40) sb.AppendLine($"    ... {interfaces.Count - 40} more");
        }

        var children = e.Elements(C + "InternalElement").ToList();
        if (children.Count > 0)
        {
            sb.AppendLine($"  children ({children.Count}):");
            foreach (var c in children.Take(30)) sb.AppendLine($"    {NameOf(c)}  [{ShortClass(m.ClassOf(c))}]  id={IdOf(c)}");
            if (children.Count > 30) sb.AppendLine($"    ... {children.Count - 30} more");
        }

        var connections = m.EdgesOf(e, includeContainment: false).ToList();
        if (connections.Count > 0)
        {
            sb.AppendLine($"  connections ({connections.Count}):");
            foreach (var c in connections.Take(60)) sb.AppendLine("    " + FormatEdge(m, e, c));
            if (connections.Count > 60) sb.AppendLine($"    ... {connections.Count - 60} more");
        }
        else
        {
            sb.AppendLine("  connections: none");
        }

        foreach (var r in m.Refs.Where(r => !r.Resolved && AnchorOf(r.Source) == e))
            sb.AppendLine($"  dangling reference: {r.AttributePath} = {r.Value}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ neighbours

    public static string FindPathText(DocumentStore store, string from, string to,
        bool includeContainment = true, int maxSteps = 12, string? document = null) =>
        FindPathText(store.Get(document), from, to, includeContainment, maxSteps);

    public static string FindPathText(AmlModel m, string from, string to, bool includeContainment = true, int maxSteps = 12)
    {
        var source = m.ResolveElement(from);
        var target = m.ResolveElement(to);
        maxSteps = Math.Clamp(maxSteps, 1, 20);
        if (source == target) return "Start and target are the same element.";

        var previous = new Dictionary<XElement, (XElement From, Edge Edge)>();
        var queue = new Queue<(XElement Node, int Steps)>();
        queue.Enqueue((source, 0));
        var visited = new HashSet<XElement> { source };

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
            return $"No connection within {maxSteps} steps between {PathOf(source)} and {PathOf(target)}" +
                   (includeContainment ? "." : " without containment steps. Try includeContainment=true.");

        var chain = new List<(XElement From, Edge Edge)>();
        for (var cur = target; cur != source; cur = previous[cur].From) chain.Add(previous[cur]);
        chain.Reverse();

        var sb = new StringBuilder($"Path with {chain.Count} step(s):\n");
        sb.AppendLine($"  {PathOf(source)}  [{HierarchyNameOf(source)}]");
        foreach (var (_, edge) in chain)
            sb.AppendLine($"    {EdgeVerb(edge)}  {PathOf(edge.Target)}  [{HierarchyNameOf(edge.Target)}]  id={IdOf(edge.Target)}");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ classes

    public static string ClassCard(DocumentStore store, string classPath, string? document = null) =>
        ClassCard(store.Get(document), classPath);

    public static string ClassCard(AmlModel m, string classPath)
    {
        var exact = m.ResolveClass(classPath);
        var matches = exact is not null
            ? new List<ClassInfo> { exact }
            : m.Classes.Values
                .Where(c => c.PlainPath.EndsWith("/" + classPath.Trim(), StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.PlainPath).Select(g => g.OrderBy(c => c.External).First()).ToList();

        if (matches.Count == 0)
            throw new McpException($"Class '{classPath}' not found. Use list_classes to browse.");
        if (matches.Count > 4)
            throw new McpException($"'{classPath}' matches {matches.Count} classes, give a full path:" + Environment.NewLine +
                                   string.Join(Environment.NewLine, matches.Select(c => "  " + c.Key)));

        var sb = new StringBuilder();
        if (matches.Count > 1)
        {
            sb.AppendLine($"'{classPath}' names {matches.Count} classes (e.g. a role and a system unit class):");
            sb.AppendLine();
        }
        foreach (var info in matches) AppendClassCard(sb, m, info);
        return sb.ToString();
    }

    public static string CheckText(DocumentStore store, string? document = null) => CheckText(store.Get(document));

    public static string CheckText(AmlModel m)
    {
        var sb = new StringBuilder($"Reference check for {Path.GetFileName(m.FilePath)}\n");
        var problems = 0;

        // Mirror objects carry an ID instead of a class path and are checked separately.
        var unresolvedClasses = m.Root.Descendants()
            .SelectMany(e => ClassReferenceAttributes.Select(a => (Element: e, Attribute: a, Value: (string?)e.Attribute(a))))
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Where(x => !IsMirrorSlot(x.Element, x.Attribute))
            .Where(x => m.ResolveClass(x.Value, x.Element) is null)
            .GroupBy(x => x.Value!).ToList();

        // Class paths that fail only because their library file is missing are
        // reported under that library, not as separate problems.
        var missingAliases = m.ExternalRefs.Where(r => !r.Loaded).Select(r => r.Alias).ToHashSet();
        foreach (var r in m.ExternalRefs.Where(r => !r.Loaded))
        {
            var affected = unresolvedClasses.Where(g => g.Key.StartsWith(r.Alias + "@", StringComparison.Ordinal)).ToList();
            if (r.Remote)
            {
                sb.AppendLine($"  note: remote library not downloaded: {r.Alias} -> {r.Path}");
                if (affected.Count > 0)
                    sb.AppendLine($"    {affected.Count} class path(s) used {affected.Sum(g => g.Count())}x from it are not checked; put a copy next to the document to check them");
                continue;
            }
            problems++;
            sb.AppendLine($"  external library not loaded: {r.Alias} -> {r.Path} ({r.Problem})");
            if (affected.Count > 0)
                sb.AppendLine($"    consequence: {affected.Count} class path(s) used {affected.Sum(g => g.Count())}x cannot be resolved, e.g. {affected[0].Key}");
        }

        foreach (var r in m.ExternalRefs.Where(r => r.Loaded && r.Remote))
            sb.AppendLine($"  note: remote library {r.Alias} -> {r.Path} was read from the local copy {r.ResolvedFile}");

        foreach (var r in m.NestedLibraryProblems)
        {
            if (r.Remote)
            {
                sb.AppendLine($"  note: an external library refers to a remote library that was not downloaded: {r.Alias} -> {r.Path}");
                continue;
            }
            problems++;
            sb.AppendLine($"  library of an external library not loaded: {r.Alias} -> {r.Path} ({r.Problem})");
        }

        foreach (var r in m.ExternalRefs.Where(r => r.OutsideContainer))
            sb.AppendLine($"  note: {r.Alias} -> {r.Path} is not inside the container and was loaded from disk next to it; " +
                          "the container is not self-contained and will break when passed on alone");

        foreach (var g in unresolvedClasses.Where(g => !missingAliases.Any(a => g.Key.StartsWith(a + "@", StringComparison.Ordinal))))
        {
            problems++;
            sb.AppendLine($"  unresolved class path: {g.Key}  (used {g.Count()}x, e.g. by {DisplayPathOf(g.First().Element)})");
        }

        foreach (var dm in m.DanglingMirrors)
        {
            problems++;
            sb.AppendLine(m.ClassByName(dm.MasterId) is { } likely
                ? $"  class path without library: {PathOf(dm.Mirror)} refers to {dm.MasterId}, which is neither an ID in this " +
                  $"document nor a full class path. Did you mean {likely.Key}?"
                : $"  mirror without master: {PathOf(dm.Mirror)} refers to {dm.MasterId}, which is not in the document");
        }

        foreach (var l in m.Links.Where(l => !l.Resolved))
        {
            problems++;
            sb.AppendLine($"  link without partner: {l.Name} A={(l.InterfaceA is null ? "missing " + l.SideA : "ok")} B={(l.InterfaceB is null ? "missing " + l.SideB : "ok")}");
        }

        foreach (var r in m.Refs.Where(r => !r.Resolved))
        {
            problems++;
            sb.AppendLine($"  dangling reference: {PathOf(r.Source)}.{r.AttributePath} = {r.Value}");
        }

        foreach (var id in m.DuplicateIds.Distinct())
        {
            problems++;
            sb.AppendLine($"  duplicate ID: {id}");
        }

        foreach (var id in m.CaseVariantIds.Distinct())
            sb.AppendLine($"  note: {id} appears with different letter case; both are treated as the same object");

        var shared = m.Links.Where(l => l.Resolved)
            .SelectMany(l => new[] { l.InterfaceA!, l.InterfaceB! })
            .GroupBy(i => i).Where(g => g.Count() > 1).ToList();
        foreach (var g in shared)
            sb.AppendLine($"  note: interface {PathOf(g.Key)} is used by {g.Count()} links");

        sb.AppendLine(problems == 0 ? "  no problems found" : $"  {problems} problem(s)");
        sb.AppendLine($"  checked: {m.Classes.Count} library classes, {m.Links.Count} links, {m.Refs.Count} references, {m.ById.Count} IDs");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ formatting helpers

    private static bool IsMirrorSlot(XElement element, string attribute) =>
        MirrorReferenceOf(element) is not null &&
        ((attribute == "RefBaseSystemUnitPath" && element.Name.LocalName == "InternalElement") ||
         (attribute == "RefBaseClassPath" && element.Name.LocalName == "ExternalInterface"));

    private static void AppendClassCard(StringBuilder sb, AmlModel m, ClassInfo info)
    {
        var cls = info.Element;
        sb.AppendLine($"{info.Key}  ({info.Kind})");
        sb.AppendLine($"  library: {info.Library}{(info.External ? $"  (external file {Path.GetFileName(info.SourceFile)})" : "")}");
        if (DescriptionOf(cls) is { } d) sb.AppendLine($"  description: {Shorten(d, 600)}");

        var baseRef = (string?)cls.Attribute("RefBaseClassPath");
        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            var chain = m.ClassChain(baseRef).ToList();
            var labels = chain.Select(c => c.Key).ToList();
            var unresolvedTail = chain.Count == 0 ? baseRef : (string?)chain[^1].Element.Attribute("RefBaseClassPath");
            if (!string.IsNullOrWhiteSpace(unresolvedTail)) labels.Add(unresolvedTail + " (NOT resolved)");
            sb.AppendLine($"  inherits from: {string.Join("  ->  ", labels)}");
        }

        var attributeLines = new List<string>();
        var hidden = 0;
        CollectAttributes(m, cls, "", includeLayout: false, attributeLines, ref hidden);
        if (attributeLines.Count > 0)
        {
            sb.AppendLine("  attributes:");
            foreach (var line in attributeLines.Take(50)) sb.AppendLine("    " + line);
        }
        if (hidden > 0) sb.AppendLine($"  ({hidden} layout attribute group(s) not shown)");
        AppendInherited(sb, m, cls, includeLayout: false);

        foreach (var i in cls.Elements(C + "ExternalInterface"))
            sb.AppendLine($"  interface: {NameOf(i)} [{(string?)i.Attribute("RefBaseClassPath")}]");
        foreach (var r in cls.Elements(C + "SupportedRoleClass"))
            sb.AppendLine($"  supported role: {(string?)r.Attribute("RefRoleClassPath")}");

        var subclasses = m.Classes.Values
            .Where(c => m.ResolveClass((string?)c.Element.Attribute("RefBaseClassPath"))?.Element == cls)
            .Select(c => c.PlainPath).Distinct().ToList();
        if (subclasses.Count > 0) sb.AppendLine($"  subclasses: {string.Join(", ", subclasses.Take(20))}");

        var uses = m.Root.Descendants()
            .Count(e => ClassReferenceAttributes.Any(a => m.ResolveClass((string?)e.Attribute(a))?.Element == cls));
        sb.AppendLine($"  used by {uses} element(s) in this document");
        sb.AppendLine();
    }

    private static void AppendInherited(StringBuilder sb, AmlModel m, XElement element, bool includeLayout)
    {
        var inherited = m.InheritedAttributes(element, includeLayout)
            .Where(a => ValueOf(a.Attribute) is not null || !a.Attribute.Elements(C + "Attribute").Any())
            .ToList();
        if (inherited.Count == 0) return;

        sb.AppendLine("  inherited from the class (not set on the element itself):");
        foreach (var a in inherited.Take(40))
        {
            var value = ValueOf(a.Attribute);
            var unit = (string?)a.Attribute.Attribute("Unit");
            var shown = value is null ? "(no value)" : $"= {Shorten(value, 120)}{(string.IsNullOrEmpty(unit) ? "" : " " + unit)}";
            sb.AppendLine($"    {a.Path} {shown}  (from {a.ClassKey})");
        }
        if (inherited.Count > 40) sb.AppendLine($"    ... {inherited.Count - 40} more");
    }

    private static void CollectAttributes(AmlModel model, XElement owner, string prefix, bool includeLayout, List<string> lines, ref int hiddenLayout)
    {
        foreach (var attribute in owner.Elements(C + "Attribute"))
        {
            if (!includeLayout && model.IsPresentationAttribute(attribute))
            {
                hiddenLayout++;
                continue;
            }
            var path = prefix.Length == 0 ? NameOf(attribute) : prefix + "/" + NameOf(attribute);
            var value = (string?)attribute.Element(C + "Value");
            var defaultValue = (string?)attribute.Element(C + "DefaultValue");
            var unit = (string?)attribute.Attribute("Unit");
            if (value is not null)
                lines.Add($"{path} = {Shorten(value, 160)}{(string.IsNullOrEmpty(unit) ? "" : " " + unit)}");
            else if (defaultValue is not null)
                lines.Add($"{path} = {Shorten(defaultValue, 160)}{(string.IsNullOrEmpty(unit) ? "" : " " + unit)} (default)");
            else if (!attribute.Elements(C + "Attribute").Any())
                lines.Add($"{path} (no value)");
            CollectAttributes(model, attribute, path, includeLayout, lines, ref hiddenLayout);
        }
    }

    private static bool MatchesWithInheritance(AmlModel m, string classReference, string text)
    {
        if (classReference.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        return m.ClassChain(classReference).Any(c => c.Key.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                  || c.PlainPath.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static string InterfaceClassLabel(AmlModel m, XElement @interface)
    {
        var master = m.MirrorMasterOf(@interface);
        if (master is not null) return $"mirror of {PathOf(master)}";
        return ShortClass((string?)@interface.Attribute("RefBaseClassPath"));
    }

    private static string FormatEdge(AmlModel m, XElement from, Edge edge)
    {
        var otherHierarchy = HierarchyNameOf(edge.Target);
        var crossMarker = otherHierarchy != HierarchyNameOf(from) ? $"  [other hierarchy: {otherHierarchy}]" : "";
        return $"{EdgeVerb(edge)}  {PathOf(edge.Target)}  [{ShortClass(m.ClassOf(edge.Target))}]  id={IdOf(edge.Target)}{crossMarker}";
    }

    private static string EdgeVerb(Edge edge) => edge.Kind switch
    {
        "link" => $"--link {edge.Detail}-->",
        "reference" => $"--{edge.Detail}-->",
        "referenced-by" => $"<--{edge.Detail}--",
        "mirror-of" => edge.Detail.Length > 0 ? $"--mirror of (interface {edge.Detail})-->" : "--mirror of-->",
        "mirrored-by" => edge.Detail.Length > 0 ? $"<--mirrored by (interface {edge.Detail})--" : "<--mirrored by--",
        "parent" => "--contained in-->",
        "child" => "--contains-->",
        _ => $"--{edge.Kind}-->"
    };

    private static (string, string) Order(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} KB",
        _ => $"{bytes / (1024 * 1024.0):0.0} MB"
    };

    private static string Shorten(string text, int max)
    {
        var flat = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..max] + "...";
    }
}
