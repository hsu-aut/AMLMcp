namespace AmlMcp;

/// <summary>
/// A document to write, described rather than spelled out as XML. The server owns the IDs,
/// the namespace and the form every reference is written in, so the description says what
/// should be in the document and not how CAEX writes it down.
/// </summary>
public sealed record DocumentSpec(
    string? FileName,
    string? SchemaVersion,
    string? Description,
    IReadOnlyList<ExternalLibrarySpec>? ExternalLibraries,
    IReadOnlyList<LibrarySpec>? Libraries,
    IReadOnlyList<HierarchySpec>? Hierarchies);

/// <summary>A library in another file, referenced under an alias.</summary>
public sealed record ExternalLibrarySpec(string Alias, string Path);

public sealed record LibrarySpec(string Name, string Kind, IReadOnlyList<ClassSpec> Classes);

public sealed record ClassSpec(
    string Name,
    string? BaseClass,
    string? Description,
    IReadOnlyList<AttributeSpec>? Attributes,
    IReadOnlyList<InterfaceSpec>? Interfaces);

public sealed record HierarchySpec(
    string Name,
    string? Description,
    IReadOnlyList<ElementSpec>? Elements,
    IReadOnlyList<LinkSpec>? Links);

/// <summary>
/// One element. <paramref name="Roles"/> become RoleRequirements, <paramref name="SupportedRoles"/>
/// become SupportedRoleClass, and <paramref name="MirrorOf"/> makes the element a mirror of
/// the element at that path.
/// </summary>
public sealed record ElementSpec(
    string Name,
    string? Class,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<string>? SupportedRoles,
    string? Description,
    IReadOnlyList<AttributeSpec>? Attributes,
    IReadOnlyList<InterfaceSpec>? Interfaces,
    IReadOnlyList<ElementSpec>? Children,
    string? MirrorOf);

/// <summary>
/// An attribute. <paramref name="Reference"/> holds the path of an element the attribute
/// points at, which is written as an object reference of the refObj family.
/// </summary>
public sealed record AttributeSpec(
    string Name,
    string? Value,
    string? DefaultValue,
    string? Unit,
    string? Reference,
    IReadOnlyList<AttributeSpec>? Attributes);

public sealed record InterfaceSpec(string Name, string? Class);

/// <summary>A link between two interfaces, each named <c>Hierarchy/Element/Interface</c>.</summary>
public sealed record LinkSpec(string Name, string From, string To);

public static class WriteSpec
{
    /// <summary>
    /// What is wrong with a description, as far as the description alone can tell: empty,
    /// nameless, ambiguous, or pointing at something it does not contain. Whether a class
    /// path resolves depends on the libraries and is decided when the document is written.
    /// Every fault names where it sits, so that one answer is enough to fix them all.
    /// </summary>
    public static IReadOnlyList<string> Validate(DocumentSpec spec)
    {
        var faults = new List<string>();
        var libraries = spec.Libraries ?? Array.Empty<LibrarySpec>();
        var hierarchies = spec.Hierarchies ?? Array.Empty<HierarchySpec>();

        if (libraries.Count == 0 && hierarchies.Count == 0)
        {
            faults.Add("There is nothing to write: name at least one class library or one instance hierarchy.");
            return faults;
        }

        foreach (var library in libraries)
        {
            if (string.IsNullOrWhiteSpace(library.Name)) faults.Add("A class library is without a name.");
            if (!AmlModel.TryClassKind(library.Kind ?? "", out _))
                faults.Add($"Library '{library.Name}' has the unknown kind '{library.Kind}'. " +
                           "Use RoleClass, SystemUnitClass, InterfaceClass or AttributeType.");
            foreach (var cls in library.Classes ?? Array.Empty<ClassSpec>())
                if (string.IsNullOrWhiteSpace(cls.Name))
                    faults.Add($"A class in library '{library.Name}' is without a name.");
        }

        foreach (var name in Duplicates(hierarchies.Select(h => h.Name)))
            faults.Add($"Two instance hierarchies are called '{name}'.");

        // One walk: it collects every element by its path and reports what it sees on the way.
        // The mirrors are collected as a list, not read back from the index: two elements of
        // the same name share a path, and the index would keep only one of them.
        var elements = new Dictionary<string, ElementSpec>(StringComparer.OrdinalIgnoreCase);
        var mirrors = new List<(string Path, string Master)>();
        foreach (var hierarchy in hierarchies)
        {
            if (string.IsNullOrWhiteSpace(hierarchy.Name)) faults.Add("An instance hierarchy is without a name.");
            Walk(hierarchy.Elements, hierarchy.Name ?? "", elements, mirrors, faults);
        }

        foreach (var hierarchy in hierarchies)
        foreach (var link in hierarchy.Links ?? Array.Empty<LinkSpec>())
        {
            CheckEndpoint(link, link.From, elements, faults);
            CheckEndpoint(link, link.To, elements, faults);
        }

        foreach (var (path, master) in mirrors)
            if (!elements.ContainsKey(master))
                faults.Add($"'{path}' mirrors '{master}', which is not in this description.");

        return faults;
    }

    private static void Walk(IReadOnlyList<ElementSpec>? children, string parentPath,
                             Dictionary<string, ElementSpec> elements,
                             List<(string Path, string Master)> mirrors, List<string> faults)
    {
        if (children is null) return;
        foreach (var name in Duplicates(children.Select(c => c.Name)))
            faults.Add($"'{parentPath}' has two children called '{name}'. A path has to name one element.");

        foreach (var child in children)
        {
            if (string.IsNullOrWhiteSpace(child.Name))
            {
                faults.Add($"An element below '{parentPath}' is without a name.");
                continue;
            }
            var path = parentPath.Length == 0 ? child.Name : $"{parentPath}/{child.Name}";
            elements[path] = child;
            if (!string.IsNullOrWhiteSpace(child.MirrorOf)) mirrors.Add((path, child.MirrorOf!));
            Walk(child.Children, path, elements, mirrors, faults);
        }
    }

    private static void CheckEndpoint(LinkSpec link, string endpoint,
                                      Dictionary<string, ElementSpec> elements, List<string> faults)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            faults.Add($"Link '{link.Name}' has an endpoint without a path.");
            return;
        }

        var cut = endpoint.LastIndexOf('/');
        if (cut <= 0)
        {
            faults.Add($"Endpoint '{endpoint}' of link '{link.Name}' has to name an interface, as Hierarchy/Element/Interface.");
            return;
        }

        var owner = endpoint[..cut];
        var interfaceName = endpoint[(cut + 1)..];
        if (!elements.TryGetValue(owner, out var element))
        {
            faults.Add($"Endpoint '{endpoint}' of link '{link.Name}' names no element of this description.");
            return;
        }

        var matches = (element.Interfaces ?? Array.Empty<InterfaceSpec>())
            .Count(i => string.Equals(i.Name, interfaceName, StringComparison.OrdinalIgnoreCase));
        if (matches == 0)
            faults.Add($"Endpoint '{endpoint}' of link '{link.Name}' names no interface of '{owner}'.");
        else if (matches > 1)
            faults.Add($"Endpoint '{endpoint}' of link '{link.Name}' is ambiguous: '{owner}' has that interface twice.");
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string?> names) => names
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key);
}
