using System.Reflection;
using System.Xml.Linq;

namespace AmlMcp;

/// <summary>
/// Turns a description into CAEX. The tree is built, never concatenated, so a name carrying
/// an ampersand or a quote is escaped by the framework rather than by hand. Everything a
/// language model gets wrong about CAEX lives here: the IDs, the namespace, the order the
/// schema prescribes, the form a link partner is written in, and what a mirror looks like.
///
/// <para>
/// <see cref="Element"/> and <see cref="Library"/> render one part on their own, because
/// changing an existing document means rendering a part and not a whole file.
/// </para>
/// </summary>
public static class CaexWriter
{
    private static readonly XNamespace C = AmlModel.C;

    /// <summary>The attribute type an object reference is written with when the description
    /// brought none of its own. See SpecCompletion.</summary>
    private const string GeneratedReferenceType = "AmlMcpReferenceLib/refObj";

    /// <summary>
    /// The order CAEXFile prescribes for the class libraries. The schema puts the
    /// hierarchies first and then the four libraries in exactly this order.
    /// </summary>
    private static readonly string[] LibraryOrder = { "InterfaceClass", "RoleClass", "SystemUnitClass", "AttributeType" };

    public static XElement Document(DocumentSpec spec, string fileName)
    {
        var document = new XElement(C + "CAEXFile",
            new XAttribute("SchemaVersion", string.IsNullOrWhiteSpace(spec.SchemaVersion) ? "3.0" : spec.SchemaVersion!),
            new XAttribute("FileName", fileName));

        // The Header group of CAEXBasicObject comes before everything else.
        if (!string.IsNullOrWhiteSpace(spec.Description))
            document.Add(new XElement(C + "Description", spec.Description));

        document.Add(new XElement(C + "SuperiorStandardVersion", "AutomationML 2.10"));
        document.Add(WriterInformation());

        foreach (var external in spec.ExternalLibraries ?? Array.Empty<ExternalLibrarySpec>())
            document.Add(new XElement(C + "ExternalReference",
                new XAttribute("Path", external.Path),
                new XAttribute("Alias", external.Alias)));

        // The elements before everything that points at one: the IDs are read off the
        // rendered tree afterwards, so a path written in another letter case cannot end up
        // as the hash of a spelling that is nowhere in the document.
        var elements = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(XElement Node, string Path, string Attribute)>();
        var referenceType = ReferenceTypeOf(spec);
        foreach (var hierarchy in spec.Hierarchies ?? Array.Empty<HierarchySpec>())
        {
            var rendered = new XElement(C + "InstanceHierarchy",
                new XAttribute("Name", hierarchy.Name),
                new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf(hierarchy.Name, "hierarchy"))));
            if (!string.IsNullOrWhiteSpace(hierarchy.Description))
                rendered.Add(new XElement(C + "Description", hierarchy.Description));
            foreach (var element in hierarchy.Elements ?? Array.Empty<ElementSpec>())
                rendered.Add(Element(element, hierarchy.Name, elements, pending, referenceType));
            document.Add(rendered);
        }

        // Mirrors and references: the ID of the element they name, as it stands in the tree.
        foreach (var (node, path, attribute) in pending)
        {
            if (!elements.TryGetValue(path, out var target)) continue;
            var id = (string?)target.Attribute("ID") ?? "";
            if (attribute.Length == 0) node.Add(new XElement(C + "Value", id));
            else node.SetAttributeValue(attribute, id);
        }

        foreach (var hierarchy in spec.Hierarchies ?? Array.Empty<HierarchySpec>())
        foreach (var link in hierarchy.Links ?? Array.Empty<LinkSpec>())
            Place(link, hierarchy.Name, elements);

        // The libraries last, and among themselves in the order of the schema.
        foreach (var kind in LibraryOrder)
        foreach (var library in spec.Libraries ?? Array.Empty<LibrarySpec>())
            if (AmlModel.TryClassKind(library.Kind, out var itsKind) && itsKind == kind)
                document.Add(Library(library));

        return document;
    }

    /// <summary>
    /// One element, below the path given. Renders its children with it. The order follows
    /// InternalElementType: what SystemUnitClassType brings first, the RoleRequirements of
    /// the extension last.
    /// </summary>
    public static XElement Element(ElementSpec spec, string parentPath) =>
        Element(spec, parentPath, null, null, GeneratedReferenceType);

    private static XElement Element(ElementSpec spec, string parentPath, Dictionary<string, XElement>? index,
                                    List<(XElement Node, string Path, string Attribute)>? pending, string referenceType)
    {
        var path = parentPath.Length == 0 ? spec.Name : $"{parentPath}/{spec.Name}";
        var element = new XElement(C + "InternalElement",
            new XAttribute("Name", spec.Name),
            new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf(path, "element"))));

        // A mirror carries the master's ID where a class path would otherwise stand. The
        // master may be rendered later, so the ID is filled in afterwards.
        if (!string.IsNullOrWhiteSpace(spec.MirrorOf))
        {
            element.Add(new XAttribute("RefBaseSystemUnitPath", SpecIds.IdFor(SpecIds.KeyOf(spec.MirrorOf!, "element"))));
            pending?.Add((element, spec.MirrorOf!, "RefBaseSystemUnitPath"));
        }
        else if (!string.IsNullOrWhiteSpace(spec.Class))
            element.Add(new XAttribute("RefBaseSystemUnitPath", spec.Class!));

        if (!string.IsNullOrWhiteSpace(spec.Description))
            element.Add(new XElement(C + "Description", spec.Description));

        foreach (var attribute in spec.Attributes ?? Array.Empty<AttributeSpec>())
            element.Add(Attribute(attribute, pending, referenceType));

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
            element.Add(Element(child, path, index, pending, referenceType));

        foreach (var role in spec.SupportedRoles ?? Array.Empty<string>())
            element.Add(new XElement(C + "SupportedRoleClass", new XAttribute("RefRoleClassPath", role)));

        foreach (var role in spec.Roles ?? Array.Empty<string>())
            element.Add(new XElement(C + "RoleRequirements", new XAttribute("RefBaseRoleClassPath", role)));

        index?.Add(path, element);
        return element;
    }

    /// <summary>
    /// Puts a link where the schema allows one: inside an InternalElement, never under an
    /// InstanceHierarchy. The deepest element that holds both sides is the natural place;
    /// two elements with no common one, in one hierarchy or across two, leave the element
    /// the link starts from, which is where the editor puts it when a user draws one.
    /// </summary>
    private static void Place(LinkSpec link, string hierarchy, Dictionary<string, XElement> elements)
    {
        var from = OwnerOf(link.From);
        var to = OwnerOf(link.To);
        var host = CommonElement(from, to, elements) ?? elements.GetValueOrDefault(from);
        if (host is null) return;

        var rendered = new XElement(C + "InternalLink",
            new XAttribute("Name", link.Name),
            new XAttribute("ID", SpecIds.IdFor(SpecIds.KeyOf($"{hierarchy}/{link.Name}", "link"))),
            new XAttribute("RefPartnerSideA", InterfaceId(link.From, elements)),
            new XAttribute("RefPartnerSideB", InterfaceId(link.To, elements)));

        // Before the RoleRequirements: those belong to the extension and come last.
        if (host.Elements(C + "RoleRequirements").FirstOrDefault() is { } roleRequirements)
            roleRequirements.AddBeforeSelf(rendered);
        else
            host.Add(rendered);
    }

    private static XElement? CommonElement(string from, string to, Dictionary<string, XElement> elements)
    {
        var a = from.Split('/');
        var b = to.Split('/');
        var shared = 0;
        while (shared < a.Length && shared < b.Length &&
               string.Equals(a[shared], b[shared], StringComparison.OrdinalIgnoreCase)) shared++;
        // One segment is the hierarchy, which is no element and may hold no link.
        for (var length = shared; length > 1; length--)
            if (elements.TryGetValue(string.Join("/", a[..length]), out var element))
                return element;
        return null;
    }

    private static string OwnerOf(string endpoint)
    {
        var text = endpoint.Trim();
        var cut = text.LastIndexOf('/');
        return cut <= 0 ? text : text[..cut];
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

    /// <summary>One attribute. AttributeType prescribes DefaultValue before Value.</summary>
    private static XElement Attribute(AttributeSpec spec) =>
        Attribute(spec, null, GeneratedReferenceType);

    private static XElement Attribute(AttributeSpec spec,
                                      List<(XElement Node, string Path, string Attribute)>? pending,
                                      string referenceType)
    {
        var attribute = new XElement(C + "Attribute", new XAttribute("Name", spec.Name));

        if (!string.IsNullOrWhiteSpace(spec.Reference))
        {
            attribute.Add(new XAttribute("AttributeDataType", "xs:string"));
            attribute.Add(new XAttribute("RefAttributeType", referenceType));
            // The value is the target's ID, filled in once the whole tree stands.
            if (pending is null)
                attribute.Add(new XElement(C + "Value", SpecIds.IdFor(SpecIds.KeyOf(spec.Reference!, "element"))));
            else
                pending.Add((attribute, spec.Reference!, ""));
            return attribute;
        }

        if (!string.IsNullOrWhiteSpace(spec.Unit)) attribute.Add(new XAttribute("Unit", spec.Unit!));
        if (spec.DefaultValue is not null) attribute.Add(new XElement(C + "DefaultValue", spec.DefaultValue));
        if (spec.Value is not null) attribute.Add(new XElement(C + "Value", spec.Value));
        foreach (var child in spec.Attributes ?? Array.Empty<AttributeSpec>())
            attribute.Add(Attribute(child, pending, referenceType));
        return attribute;
    }

    /// <summary>
    /// The endpoint of a link, as the ID of the interface that is there. Read off the tree,
    /// so an endpoint written in another letter case points at the interface it names and
    /// not at a hash of that spelling.
    /// </summary>
    private static string InterfaceId(string endpoint, Dictionary<string, XElement> elements)
    {
        var text = endpoint.Trim();
        var cut = text.LastIndexOf('/');
        if (cut > 0 && elements.TryGetValue(text[..cut], out var owner))
        {
            var name = text[(cut + 1)..];
            var @interface = owner.Elements(C + "ExternalInterface")
                .FirstOrDefault(i => string.Equals((string?)i.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase));
            if (@interface is not null) return (string?)@interface.Attribute("ID") ?? "";
        }
        return SpecIds.IdFor(SpecIds.KeyOf(text, "interface"));
    }

    /// <summary>
    /// The attribute type an object reference points at: the one the description brought,
    /// if it brought one, otherwise the generated one.
    /// </summary>
    private static string ReferenceTypeOf(DocumentSpec spec)
    {
        foreach (var library in spec.Libraries ?? Array.Empty<LibrarySpec>())
        {
            if (!AmlModel.TryClassKind(library.Kind ?? "", out var kind) || kind != "AttributeType") continue;
            var cls = (library.Classes ?? Array.Empty<ClassSpec>())
                .FirstOrDefault(c => string.Equals(c.Name, "refObj", StringComparison.OrdinalIgnoreCase));
            if (cls is not null) return $"{library.Name}/{cls.Name}";
        }
        return GeneratedReferenceType;
    }

    /// <summary>
    /// Who wrote the document. SourceDocumentInformationType has no Writer attributes: the
    /// Origin family and the timestamp are all it allows, and all four of these are required.
    /// </summary>
    private static XElement WriterInformation() => new(C + "SourceDocumentInformation",
        new XAttribute("OriginName", "aml-mcp"),
        new XAttribute("OriginID", "aml-mcp"),
        new XAttribute("OriginVersion", Version()),
        new XAttribute("OriginVendor", "AutomationML MCP server"),
        new XAttribute("LastWritingDateTime", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")));

    private static string Version() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
