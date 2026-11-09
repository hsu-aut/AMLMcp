using System.Reflection;
using System.Xml.Linq;

namespace AmlMcp;

/// <summary>
/// Turns a description into CAEX. The tree is built, never concatenated, so a name carrying
/// an ampersand or a quote is escaped by the framework rather than by hand. Everything a
/// language model gets wrong about CAEX lives here: the IDs, the namespace, the form a link
/// partner is written in, and what a mirror reference looks like.
///
/// <para>
/// <see cref="Element"/> and <see cref="Library"/> render one part on their own, because
/// changing an existing document means rendering a part and not a whole file.
/// </para>
/// </summary>
public static class CaexWriter
{
    private static readonly XNamespace C = AmlModel.C;

    /// <summary>The attribute type an object reference is written with. See SpecCompletion.</summary>
    private const string ReferenceType = "AmlMcpReferenceLib/refObj";

    public static XElement Document(DocumentSpec spec, string fileName)
    {
        var document = new XElement(C + "CAEXFile",
            new XAttribute("SchemaVersion", string.IsNullOrWhiteSpace(spec.SchemaVersion) ? "3.0" : spec.SchemaVersion!),
            new XAttribute("FileName", fileName),
            new XElement(C + "SuperiorStandardVersion", "AutomationML 2.10"),
            WriterInformation());

        if (!string.IsNullOrWhiteSpace(spec.Description))
            document.Add(new XElement(C + "Description", spec.Description));

        foreach (var external in spec.ExternalLibraries ?? Array.Empty<ExternalLibrarySpec>())
            document.Add(new XElement(C + "ExternalReference",
                new XAttribute("Path", external.Path),
                new XAttribute("Alias", external.Alias)));

        foreach (var library in spec.Libraries ?? Array.Empty<LibrarySpec>())
            document.Add(Library(library));

        // The IDs of the interfaces have to be known before the links are written, and a
        // link may reach into another hierarchy, so the elements come first, links after.
        var hierarchies = new List<(HierarchySpec Spec, XElement Element)>();
        foreach (var hierarchy in spec.Hierarchies ?? Array.Empty<HierarchySpec>())
        {
            var rendered = new XElement(C + "InstanceHierarchy",
                new XAttribute("Name", hierarchy.Name),
                new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf(hierarchy.Name, "hierarchy"))));
            if (!string.IsNullOrWhiteSpace(hierarchy.Description))
                rendered.Add(new XElement(C + "Description", hierarchy.Description));
            foreach (var element in hierarchy.Elements ?? Array.Empty<ElementSpec>())
                rendered.Add(Element(element, hierarchy.Name));
            hierarchies.Add((hierarchy, rendered));
            document.Add(rendered);
        }

        foreach (var (hierarchy, rendered) in hierarchies)
        foreach (var link in hierarchy.Links ?? Array.Empty<LinkSpec>())
            rendered.Add(new XElement(C + "InternalLink",
                new XAttribute("Name", link.Name),
                new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf($"{hierarchy.Name}/{link.Name}", "link"))),
                new XAttribute("RefPartnerSideA", InterfaceId(link.From)),
                new XAttribute("RefPartnerSideB", InterfaceId(link.To))));

        return document;
    }

    /// <summary>One element, below the path given. Renders its children with it.</summary>
    public static XElement Element(ElementSpec spec, string parentPath)
    {
        var path = parentPath.Length == 0 ? spec.Name : $"{parentPath}/{spec.Name}";
        var element = new XElement(C + "InternalElement",
            new XAttribute("Name", spec.Name),
            new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf(path, "element"))));

        // A mirror carries the master's ID where a class path would otherwise stand.
        if (!string.IsNullOrWhiteSpace(spec.MirrorOf))
            element.Add(new XAttribute("RefBaseSystemUnitPath", SpecIds.IdFor(SpecIds.KeyOf(spec.MirrorOf!, "element"))));
        else if (!string.IsNullOrWhiteSpace(spec.Class))
            element.Add(new XAttribute("RefBaseSystemUnitPath", spec.Class!));

        if (!string.IsNullOrWhiteSpace(spec.Description))
            element.Add(new XElement(C + "Description", spec.Description));

        foreach (var attribute in spec.Attributes ?? Array.Empty<AttributeSpec>())
            element.Add(Attribute(attribute));

        foreach (var @interface in spec.Interfaces ?? Array.Empty<InterfaceSpec>())
        {
            var rendered = new XElement(C + "ExternalInterface",
                new XAttribute("Name", @interface.Name),
                new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf($"{path}/{@interface.Name}", "interface"))));
            if (!string.IsNullOrWhiteSpace(@interface.Class))
                rendered.Add(new XAttribute("RefBaseClassPath", @interface.Class!));
            element.Add(rendered);
        }

        foreach (var child in spec.Children ?? Array.Empty<ElementSpec>())
            element.Add(Element(child, path));

        foreach (var role in spec.Roles ?? Array.Empty<string>())
            element.Add(new XElement(C + "RoleRequirements", new XAttribute("RefBaseRoleClassPath", role)));

        foreach (var role in spec.SupportedRoles ?? Array.Empty<string>())
            element.Add(new XElement(C + "SupportedRoleClass", new XAttribute("RefRoleClassPath", role)));

        return element;
    }

    /// <summary>One class library, of whichever of the four kinds it declares.</summary>
    public static XElement Library(LibrarySpec spec)
    {
        if (!AmlModel.TryClassKind(spec.Kind, out var kind))
            throw new ArgumentException($"Unknown library kind '{spec.Kind}'.", nameof(spec));

        var library = new XElement(C + kind + "Lib", new XAttribute("Name", spec.Name));
        foreach (var cls in spec.Classes ?? Array.Empty<ClassSpec>())
            library.Add(Class(cls, kind));
        return library;
    }

    private static XElement Class(ClassSpec spec, string kind)
    {
        var cls = new XElement(C + kind, new XAttribute("Name", spec.Name));
        // An attribute type has to say what it holds; the others carry no data type.
        if (kind == "AttributeType") cls.Add(new XAttribute("AttributeDataType", "xs:string"));
        if (!string.IsNullOrWhiteSpace(spec.BaseClass))
            cls.Add(new XAttribute("RefBaseClassPath", spec.BaseClass!));
        if (!string.IsNullOrWhiteSpace(spec.Description))
            cls.Add(new XElement(C + "Description", spec.Description));
        foreach (var attribute in spec.Attributes ?? Array.Empty<AttributeSpec>())
            cls.Add(Attribute(attribute));
        foreach (var @interface in spec.Interfaces ?? Array.Empty<InterfaceSpec>())
        {
            var rendered = new XElement(C + "ExternalInterface", new XAttribute("Name", @interface.Name));
            if (!string.IsNullOrWhiteSpace(@interface.Class))
                rendered.Add(new XAttribute("RefBaseClassPath", @interface.Class!));
            cls.Add(rendered);
        }
        return cls;
    }

    private static XElement Attribute(AttributeSpec spec)
    {
        var attribute = new XElement(C + "Attribute", new XAttribute("Name", spec.Name));

        if (!string.IsNullOrWhiteSpace(spec.Reference))
        {
            attribute.Add(new XAttribute("AttributeDataType", "xs:string"));
            attribute.Add(new XAttribute("RefAttributeType", ReferenceType));
            attribute.Add(new XElement(C + "Value", SpecIds.IdFor(SpecIds.KeyOf(spec.Reference!, "element"))));
            return attribute;
        }

        if (!string.IsNullOrWhiteSpace(spec.Unit)) attribute.Add(new XAttribute("Unit", spec.Unit!));
        if (spec.Value is not null) attribute.Add(new XElement(C + "Value", spec.Value));
        if (spec.DefaultValue is not null) attribute.Add(new XElement(C + "DefaultValue", spec.DefaultValue));
        foreach (var child in spec.Attributes ?? Array.Empty<AttributeSpec>())
            attribute.Add(Attribute(child));
        return attribute;
    }

    /// <summary>The endpoint of a link, as the ID of the interface it names.</summary>
    private static string InterfaceId(string endpoint) =>
        SpecIds.IdFor(SpecIds.KeyOf(endpoint.Trim(), "interface"));

    private static XElement WriterInformation() => new(C + "SourceDocumentInformation",
        new XAttribute("OriginName", "aml-mcp"),
        new XAttribute("OriginID", "aml-mcp"),
        new XAttribute("OriginVersion", Version()),
        new XAttribute("WriterName", "aml-mcp"),
        new XAttribute("WriterID", "aml-mcp"),
        new XAttribute("WriterVersion", Version()),
        new XAttribute("LastWritingDateTime", DateTime.UtcNow.ToString("o")));

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
