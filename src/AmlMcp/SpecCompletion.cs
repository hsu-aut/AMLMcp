namespace AmlMcp;

/// <summary>
/// Makes a description self-contained. Every class, role and interface class it uses has to
/// be somewhere, or the written document would not resolve and the reference check would
/// refuse it. A reference an external library carries is left to that library; everything
/// else becomes a class library of the document itself, and a bare name is pointed at the
/// library that now carries it.
/// </summary>
public static class SpecCompletion
{
    private const string UnitLib = "AmlMcpUnitLib";
    private const string RoleLib = "AmlMcpRoleLib";
    private const string InterfaceLib = "AmlMcpInterfaceLib";
    private const string ReferenceLib = "AmlMcpReferenceLib";

    public static DocumentSpec Complete(DocumentSpec spec)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in spec.Libraries ?? Array.Empty<LibrarySpec>())
        foreach (var cls in library.Classes ?? Array.Empty<ClassSpec>())
            known.Add($"{library.Name}/{cls.Name}");

        // An attribute type called refObj that the description brings itself is the one to
        // use; only without one does the server add its own.
        var declaredReference = (spec.Libraries ?? Array.Empty<LibrarySpec>())
            .Any(l => AmlModel.TryClassKind(l.Kind ?? "", out var k) && k == "AttributeType"
                      && (l.Classes ?? Array.Empty<ClassSpec>()).Any(c => string.Equals(c.Name, "refObj", StringComparison.OrdinalIgnoreCase)));

        var added = new List<(string Library, string Kind, string Class)>();
        var libraryClasses = (spec.Libraries ?? Array.Empty<LibrarySpec>())
            .Select(l => Complete(l, known, added))
            .ToList();
        var hierarchies = (spec.Hierarchies ?? Array.Empty<HierarchySpec>())
            .Select(h => h with { Elements = Rewrite(h.Elements, known, added, declaredReference) })
            .ToList();

        // Nothing was added, so nothing was rewritten either: a path is only rewritten when
        // the class behind it had to be created. The description then stands as it came.
        if (added.Count == 0) return spec;

        var libraries = libraryClasses.ToList();
        foreach (var group in added.GroupBy(a => (a.Library, a.Kind)))
        {
            var classes = group.Select(a => a.Class).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new ClassSpec(name, null, null, null, null)).ToList();
            var existing = libraries.FindIndex(l => string.Equals(l.Name, group.Key.Library, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                libraries[existing] = libraries[existing] with { Classes = libraries[existing].Classes.Concat(classes).ToList() };
            else
                libraries.Add(new LibrarySpec(group.Key.Library, group.Key.Kind, classes));
        }

        return spec with
        {
            Libraries = libraries,
            Hierarchies = spec.Hierarchies is null ? null : hierarchies,
        };
    }

    /// <summary>
    /// A class of a library names a base class and interface classes of its own, and those
    /// have to resolve as well.
    /// </summary>
    private static LibrarySpec Complete(LibrarySpec library, HashSet<string> known,
                                        List<(string Library, string Kind, string Class)> added)
    {
        if (library.Classes is null || library.Classes.Count == 0) return library;
        if (!AmlModel.TryClassKind(library.Kind ?? "", out var kind)) return library;

        var generated = kind switch
        {
            "RoleClass" => RoleLib,
            "InterfaceClass" => InterfaceLib,
            "AttributeType" => ReferenceLib,
            _ => UnitLib,
        };
        return library with
        {
            Classes = library.Classes.Select(c => c with
            {
                BaseClass = Resolve(c.BaseClass, generated, kind, known, added),
                Interfaces = c.Interfaces?.Select(i => i with
                {
                    Class = Resolve(i.Class, InterfaceLib, "InterfaceClass", known, added),
                }).ToList(),
            }).ToList(),
        };
    }

    private static IReadOnlyList<ElementSpec>? Rewrite(IReadOnlyList<ElementSpec>? elements,
        HashSet<string> known, List<(string Library, string Kind, string Class)> added, bool declaredReference)
    {
        if (elements is null) return null;
        return elements.Select(element => element with
        {
            Class = Resolve(element.Class, UnitLib, "SystemUnitClass", known, added),
            Roles = element.Roles?.Select(r => Resolve(r, RoleLib, "RoleClass", known, added)!).ToList(),
            SupportedRoles = element.SupportedRoles?.Select(r => Resolve(r, RoleLib, "RoleClass", known, added)!).ToList(),
            Interfaces = element.Interfaces?.Select(i => i with
            {
                Class = Resolve(i.Class, InterfaceLib, "InterfaceClass", known, added),
            }).ToList(),
            Attributes = Attributes(element.Attributes, known, added, declaredReference),
            Children = Rewrite(element.Children, known, added, declaredReference),
        }).ToList();
    }

    private static IReadOnlyList<AttributeSpec>? Attributes(IReadOnlyList<AttributeSpec>? attributes,
        HashSet<string> known, List<(string Library, string Kind, string Class)> added, bool declaredReference)
    {
        if (attributes is null) return null;
        foreach (var attribute in attributes)
        {
            // An object reference is written with an attribute type, which has to exist too,
            // unless the description already brought one.
            if (!declaredReference && !string.IsNullOrWhiteSpace(attribute.Reference))
                Resolve("refObj", ReferenceLib, "AttributeType", known, added);
        }
        return attributes
            .Select(a => a with { Attributes = Attributes(a.Attributes, known, added, declaredReference) })
            .ToList();
    }

    /// <summary>
    /// The path a reference should carry. A reference with an alias belongs to an external
    /// library and is left as it is. A path whose library the document does not have gets
    /// that library; a bare name gets the generated one and is rewritten to point at it.
    /// </summary>
    private static string? Resolve(string? reference, string generatedLibrary, string kind,
        HashSet<string> known, List<(string Library, string Kind, string Class)> added)
    {
        if (string.IsNullOrWhiteSpace(reference)) return reference;
        var text = reference.Trim();
        if (text.Contains('@', StringComparison.Ordinal)) return text;
        if (known.Contains(text)) return text;

        var cut = text.LastIndexOf('/');
        var library = cut > 0 ? text[..cut] : generatedLibrary;
        var name = cut > 0 ? text[(cut + 1)..] : text;
        var path = $"{library}/{name}";
        if (known.Add(path)) added.Add((library, kind, name));
        return path;
    }
}
