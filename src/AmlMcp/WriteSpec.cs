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

        // Stage one writes CAEX 3.0. A document labelled otherwise would be a lie about itself.
        if (!string.IsNullOrWhiteSpace(spec.SchemaVersion) && spec.SchemaVersion!.Trim() != "3.0")
            faults.Add($"This server writes CAEX 3.0, so schemaVersion '{spec.SchemaVersion}' cannot be written. " +
                       "Leave the field out or set it to 3.0.");

        foreach (var external in spec.ExternalLibraries ?? Array.Empty<ExternalLibrarySpec>())
        {
            if (string.IsNullOrWhiteSpace(external.Alias))
                faults.Add($"An external library (path '{external.Path}') is without an alias.");
            if (string.IsNullOrWhiteSpace(external.Path))
                faults.Add($"External library '{external.Alias}' is without a path.");
        }

        foreach (var library in libraries)
        {
            if (string.IsNullOrWhiteSpace(library.Name)) faults.Add("A class library is without a name.");
            if (!AmlModel.TryClassKind(library.Kind ?? "", out _))
                faults.Add($"Library '{library.Name}' has the unknown kind '{library.Kind}'. " +
                           "Use RoleClass, SystemUnitClass, InterfaceClass or AttributeType.");
            if (library.Classes is null || library.Classes.Count == 0)
                faults.Add($"Library '{library.Name}' has no classes. Name the classes it should carry, " +
                           "or leave the library out.");
            foreach (var cls in library.Classes ?? Array.Empty<ClassSpec>())
            {
                if (string.IsNullOrWhiteSpace(cls.Name))
                    faults.Add($"A class in library '{library.Name}' is without a name.");
                Names(cls.Attributes, $"{library.Name}/{cls.Name}", faults);
                foreach (var @interface in cls.Interfaces ?? Array.Empty<InterfaceSpec>())
                    if (string.IsNullOrWhiteSpace(@interface.Name))
                        faults.Add($"An interface of '{library.Name}/{cls.Name}' is without a name.");
            }
        }

        foreach (var name in Duplicates(hierarchies.Select(h => h.Name)))
            faults.Add($"Two instance hierarchies are called '{name}'.");

        // One walk: it collects every element by its path and reports what it sees on the way.
        // The mirrors are collected as a list, not read back from the index: two elements of
        // the same name share a path, and the index would keep only one of them.
        var elements = new Dictionary<string, ElementSpec>(StringComparer.OrdinalIgnoreCase);
        var mirrors = new List<(string Path, string Master)>();
        var references = new List<(string Path, string Target)>();
        foreach (var hierarchy in hierarchies)
        {
            if (string.IsNullOrWhiteSpace(hierarchy.Name)) faults.Add("An instance hierarchy is without a name.");
            Walk(hierarchy.Elements, hierarchy.Name ?? "", elements, mirrors, references, faults);
        }

        foreach (var hierarchy in hierarchies)
        foreach (var link in hierarchy.Links ?? Array.Empty<LinkSpec>())
        {
            if (string.IsNullOrWhiteSpace(link.Name))
                faults.Add($"A link in '{hierarchy.Name}' is without a name.");
            CheckEndpoint(link, link.From, elements, faults);
            CheckEndpoint(link, link.To, elements, faults);
        }

        foreach (var (path, master) in mirrors)
            if (!elements.ContainsKey(master))
                faults.Add($"'{path}' mirrors '{master}', which is not in this description.");

        // A reference points at an element by path. Said here, the answer names the path the
        // caller wrote; left to the reference check, it would name an ID nobody wrote.
        foreach (var (path, target) in references)
            if (!elements.ContainsKey(target))
                faults.Add($"'{path}' refers to '{target}', which is not in this description.");

        return faults;
    }

    private static void Walk(IReadOnlyList<ElementSpec>? children, string parentPath,
                             Dictionary<string, ElementSpec> elements,
                             List<(string Path, string Master)> mirrors,
                             List<(string Path, string Target)> references, List<string> faults)
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
            Names(child.Attributes, path, faults);
            References(child.Attributes, path, references);
            foreach (var @interface in child.Interfaces ?? Array.Empty<InterfaceSpec>())
                if (string.IsNullOrWhiteSpace(@interface.Name))
                    faults.Add($"An interface of '{path}' is without a name.");
            Walk(child.Children, path, elements, mirrors, references, faults);
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

    /// <summary>An attribute without a name cannot be written, nor can a child of one.</summary>
    private static void Names(IReadOnlyList<AttributeSpec>? attributes, string owner, List<string> faults)
    {
        foreach (var attribute in attributes ?? Array.Empty<AttributeSpec>())
        {
            if (string.IsNullOrWhiteSpace(attribute.Name))
                faults.Add($"An attribute of '{owner}' is without a name.");
            Names(attribute.Attributes, owner, faults);
        }
    }

    /// <summary>Every path an attribute points at, with the attribute it sits on.</summary>
    private static void References(IReadOnlyList<AttributeSpec>? attributes, string owner,
                                   List<(string Path, string Target)> references)
    {
        foreach (var attribute in attributes ?? Array.Empty<AttributeSpec>())
        {
            if (!string.IsNullOrWhiteSpace(attribute.Reference))
                references.Add(($"{owner}.{attribute.Name}", attribute.Reference!));
            References(attribute.Attributes, owner, references);
        }
    }

    private static IEnumerable<string> Duplicates(IEnumerable<string?> names) => names
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .GroupBy(n => n!, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key);
}
