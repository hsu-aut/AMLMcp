using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ModelContextProtocol;

namespace AmlMcp;

public sealed record ExternalRef(string Alias, string Path, string? ResolvedFile, bool Loaded, string? Problem, bool OutsideContainer = false, bool Remote = false);

public sealed record ClassInfo(string Key, string PlainPath, string Kind, string Library, XElement Element, string SourceFile, bool External);

public sealed record LinkInfo(
    XElement Link, string Name, string SideA, string SideB,
    XElement? InterfaceA, XElement? InterfaceB, XElement? OwnerA, XElement? OwnerB,
    string? HierarchyA, string? HierarchyB)
{
    public bool Resolved => InterfaceA is not null && InterfaceB is not null;
    public bool CrossHierarchy => HierarchyA is not null && HierarchyB is not null && HierarchyA != HierarchyB;
}

public sealed record RefInfo(XElement Source, string AttributePath, string Value, XElement? Target)
{
    public bool Resolved => Target is not null;
}

/// <summary>A mirror object whose master ID does not exist in the document.</summary>
public sealed record DanglingMirror(XElement Mirror, string MasterId);

/// <summary>An attribute an element does not define itself but inherits from its class chain.</summary>
public sealed record InheritedAttribute(string Path, XElement Attribute, string ClassKey);

/// <summary>A file inside an AMLX container, with the role its package relationships give it.</summary>
public sealed record ContainerPart(string Path, long Size, string? Role);

/// <summary>A navigable connection between two model elements.</summary>
public sealed record Edge(string Kind, XElement Target, string Detail);

/// <summary>
/// Read-only, graph-shaped view of one AutomationML document: a plain .aml file
/// (CAEX 3.0, or 2.15 normalised into the 3.0 namespace) or an AMLX container,
/// including the class libraries it references.
/// </summary>
public sealed class AmlModel
{
    public static readonly XNamespace C = "http://www.dke.de/CAEX";

    private const string ContainerPrefix = "amlx:/";

    private static readonly HashSet<string> ClassKinds = new() { "InterfaceClass", "RoleClass", "SystemUnitClass", "AttributeType" };
    private static readonly HashSet<string> LibraryKinds = new() { "InterfaceClassLib", "RoleClassLib", "SystemUnitClassLib", "AttributeTypeLib" };
    private static readonly HashSet<string> NodeKinds = new() { "InstanceHierarchy", "InternalElement" };

    public static readonly string[] ClassReferenceAttributes =
        { "RefBaseSystemUnitPath", "RefBaseClassPath", "RefBaseRoleClassPath", "RefRoleClassPath", "RefAttributeType" };

    public ServerOptions Options { get; }
    public string FilePath { get; }
    public DateTime LoadedWriteTimeUtc { get; }
    public XElement Root { get; }
    public long FileSize { get; }
    public string SchemaVersion { get; }

    /// <summary>True when the file is an AMLX container (a ZIP package).</summary>
    public bool IsContainer { get; }

    /// <summary>Path of the root document inside the container, or null for a plain file.</summary>
    public string? RootPart { get; }

    public List<ContainerPart> ContainerParts { get; } = new();

    public Dictionary<string, XElement> ById { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DuplicateIds { get; } = new();

    /// <summary>IDs that appear twice with different letter case. Treated as one object.</summary>
    public List<string> CaseVariantIds { get; } = new();

    private readonly Dictionary<string, string> _rawIds = new(StringComparer.OrdinalIgnoreCase);
    public List<ExternalRef> ExternalRefs { get; } = new();

    /// <summary>Libraries referenced by an external library that could not be loaded.
    /// Separate because a CAEX alias belongs to the document that declares it.</summary>
    public List<ExternalRef> NestedLibraryProblems { get; } = new();
    public Dictionary<string, ClassInfo> Classes { get; } = new(StringComparer.Ordinal);
    public List<LinkInfo> Links { get; } = new();
    public List<RefInfo> Refs { get; } = new();
    public List<DanglingMirror> DanglingMirrors { get; } = new();
    public List<XElement> Hierarchies { get; }

    private readonly Dictionary<string, ClassInfo> _plainIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ClassInfo>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<XElement, List<Edge>> _edges = new();
    private readonly Dictionary<string, XElement> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _indexedAliases = new(StringComparer.OrdinalIgnoreCase);

    // Only set while the constructor reads a container.
    private Dictionary<string, ZipArchiveEntry>? _entries;

    private AmlModel(string path, ServerOptions options)
    {
        Options = options;
        FilePath = path;
        LoadedWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        FileSize = new FileInfo(path).Length;
        IsContainer = LooksLikeContainer(path);

        FileStream? stream = null;
        ZipArchive? archive = null;
        try
        {
            string baseLocation;
            if (IsContainer)
            {
                stream = File.OpenRead(path);
                try
                {
                    archive = new ZipArchive(stream, ZipArchiveMode.Read);
                }
                catch (InvalidDataException ex)
                {
                    throw new McpException($"'{Path.GetFileName(path)}' looks like an AMLX container (ZIP) but cannot be read: {ex.Message}");
                }

                _entries = archive.Entries
                    .Where(e => !e.FullName.EndsWith('/'))
                    .GroupBy(e => ZipKey(e.FullName))
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

                var roles = ReadRelationships();
                RootPart = FindRootPart(roles, path);
                Root = LoadContainerEntry(RootPart);
                baseLocation = ZipDirectoryOf(RootPart);

                foreach (var entry in _entries.Values.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    var partPath = NormalizeZipPath(entry.FullName);
                    if (IsPackageBookkeeping(partPath)) continue;
                    roles.TryGetValue(ZipKey(partPath), out var role);
                    if (role is null && ZipKey(partPath) == ZipKey(RootPart)) role = "RootDocument";
                    ContainerParts.Add(new ContainerPart(partPath, entry.Length, role));
                }
            }
            else
            {
                Root = SafeLoad(path);
                baseLocation = Path.GetDirectoryName(path)!;
            }

            SchemaVersion = (string?)Root.Attribute("SchemaVersion") ?? "?";

            foreach (var el in Root.DescendantsAndSelf())
            {
                var id = (string?)el.Attribute("ID");
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (ById.TryAdd(NormId(id), el)) { _rawIds[NormId(id)] = id.Trim(); continue; }
                if (string.Equals(_rawIds.GetValueOrDefault(NormId(id)), id.Trim(), StringComparison.Ordinal))
                    DuplicateIds.Add(id);
                else
                    CaseVariantIds.Add(id);
            }

            _fileCache[IsContainer ? ContainerPrefix + RootPart : path] = Root;

            Hierarchies = Root.Elements(C + "InstanceHierarchy").ToList();
            IndexClasses(Root, IsContainer ? ContainerPrefix + RootPart : path, alias: null, external: false);
            LoadExternalReferences(Root, baseLocation, inContainer: IsContainer, depth: 0, topLevel: true);
            var missing = ExternalRefs.Where(r => !r.Loaded).Select(r => Path.GetFileName(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            NestedLibraryProblems.RemoveAll(r => missing.Contains(Path.GetFileName(r.Path)));
            IndexLinks();
            IndexReferences();
            IndexMirrors();
        }
        finally
        {
            _entries = null;
            archive?.Dispose();
            stream?.Dispose();
        }
    }

    public static AmlModel Load(string path, ServerOptions? options = null) =>
        new(Path.GetFullPath(path), options ?? ServerOptions.Default);

    // ------------------------------------------------------------------ loading

    private static XElement SafeLoad(string file)
    {
        using var stream = File.OpenRead(file);
        return SafeLoad(stream, Path.GetFileName(file));
    }

    /// <summary>
    /// Loads a CAEX document without DTD processing. CAEX 2.15 documents have no
    /// XML namespace; their element names are moved into the CAEX 3.0 namespace
    /// so the rest of the model handles both versions identically.
    /// </summary>
    private static XElement SafeLoad(Stream stream, string displayName)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        XElement root;
        using (var reader = XmlReader.Create(stream, settings))
            root = XDocument.Load(reader).Root ?? throw new McpException($"'{displayName}' is empty.");

        if (root.Name.LocalName != "CAEXFile")
            throw new McpException(
                $"'{displayName}' is not an AutomationML/CAEX document: the root element is <{root.Name.LocalName}>, expected <CAEXFile>.");

        var foreign = root.Name.Namespace;
        if (foreign != C)
        {
            foreach (var el in root.DescendantsAndSelf())
                if (el.Name.Namespace == foreign || el.Name.Namespace == XNamespace.None)
                    el.Name = C + el.Name.LocalName;
        }
        return root;
    }

    private void IndexClasses(XElement root, string source, string? alias, bool external)
    {
        if (!_indexedAliases.Add(source + "|" + (alias ?? ""))) return;

        foreach (var lib in root.Elements().Where(e => LibraryKinds.Contains(e.Name.LocalName)))
        {
            var libName = NameOf(lib);
            Walk(lib, libName);

            void Walk(XElement parent, string prefix)
            {
                foreach (var cls in parent.Elements().Where(e => ClassKinds.Contains(e.Name.LocalName)))
                {
                    var plain = prefix + "/" + NameOf(cls);
                    var key = alias is null ? plain : alias + "@" + plain;
                    var info = new ClassInfo(key, plain, cls.Name.LocalName, libName, cls, source, external);
                    Classes.TryAdd(key, info);
                    _plainIndex.TryAdd(plain, info);
                    if (!_byName.TryGetValue(NameOf(cls), out var sameName))
                        _byName[NameOf(cls)] = sameName = new List<ClassInfo>();
                    if (!sameName.Any(c => c.Element == cls)) sameName.Add(info);
                    Walk(cls, plain);
                }
            }
        }
    }

    /// <summary>
    /// Resolves ExternalReference elements. Inside a container a reference is
    /// looked up relative to the referencing part first, then by file name
    /// anywhere in the container, and only then on disk next to the container.
    /// </summary>
    private void LoadExternalReferences(XElement root, string baseLocation, bool inContainer, int depth, bool topLevel)
    {
        foreach (var er in root.Elements(C + "ExternalReference"))
        {
            var alias = (string?)er.Attribute("Alias") ?? "";
            var relative = (string?)er.Attribute("Path") ?? "";
            string? resolved = null, problem = null;
            var loaded = false;
            var outside = false;

            // A remote library is not downloaded. A local copy under the same file name is
            // used instead, and the address is shown without any credentials in it.
            var remote = Uri.TryCreate(relative, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
            var shown = relative;
            if (remote)
            {
                shown = uri!.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
                relative = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "");
            }
            try
            {
                if (relative.Length == 0)
                {
                    problem = "empty path";
                }
                else if (inContainer && FindContainerEntry(relative, baseLocation) is { } partPath)
                {
                    resolved = ContainerPrefix + partPath;
                    var isNew = !_fileCache.TryGetValue(resolved, out var libRoot);
                    if (isNew)
                    {
                        libRoot = LoadContainerEntry(partPath);
                        _fileCache[resolved] = libRoot;
                    }
                    IndexClasses(libRoot!, resolved, alias, external: true);
                    if (isNew && depth < 4)
                        LoadExternalReferences(libRoot!, ZipDirectoryOf(partPath), inContainer: true, depth + 1, topLevel: false);
                    loaded = true;
                }
                else
                {
                    var directory = inContainer ? Path.GetDirectoryName(FilePath)! : baseLocation;
                    var candidate = FindOnDisk(relative, directory);
                    if (candidate is not null && !Options.Allows(candidate))
                    {
                        problem = $"outside the directories this server may read ({Options.RootsDescription})";
                    }
                    else if (candidate is not null)
                    {
                        resolved = candidate;
                        outside = inContainer;
                        var isNew = !_fileCache.TryGetValue(candidate, out var libRoot);
                        if (isNew)
                        {
                            libRoot = SafeLoad(candidate);
                            _fileCache[candidate] = libRoot;
                        }
                        IndexClasses(libRoot!, candidate, alias, external: true);
                        if (isNew && depth < 4)
                            LoadExternalReferences(libRoot!, Path.GetDirectoryName(candidate)!, inContainer: false, depth + 1, topLevel: false);
                        loaded = true;
                    }
                    else
                    {
                        problem = remote
                            ? "remote library, not downloaded, and no local copy next to the document"
                            : inContainer ? "not in the container and not next to it on disk" : "file not found next to the document";
                    }
                }
            }
            catch (Exception ex)
            {
                problem = ex.Message;
            }

            if (topLevel)
                ExternalRefs.Add(new ExternalRef(alias, shown, resolved, loaded, problem, outside, remote));
            else if (!loaded && NestedLibraryProblems.All(r => r.Path != shown))
                NestedLibraryProblems.Add(new ExternalRef(alias, shown, resolved, loaded, problem, outside, remote));
        }
    }

    private static string? FindOnDisk(string relative, string directory)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var unescaped = Uri.UnescapeDataString(relative);
        foreach (var candidate in new[] { relative, unescaped }.Distinct())
        {
            var full = Path.IsPathRooted(candidate) ? candidate : Path.GetFullPath(Path.Combine(directory, candidate));
            if (File.Exists(full)) return full;
            var sameFolder = Path.Combine(directory, Path.GetFileName(candidate));
            if (File.Exists(sameFolder)) return sameFolder;
        }
        return null;
    }

    // ------------------------------------------------------------------ AMLX container

    /// <summary>A file is treated as a container when it starts with a ZIP signature or is named .amlx.</summary>
    private static bool LooksLikeContainer(string path)
    {
        if (path.EndsWith(".amlx", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return s.Read(head) == 4 && head[0] == 0x50 && head[1] == 0x4B && (head[2] == 0x03 || head[2] == 0x05);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every package relationship file and maps each target part to the
    /// last segment of its relationship type (RootDocument, Library, AnyContent, ...).
    /// </summary>
    private Dictionary<string, string> ReadRelationships()
    {
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, entry) in _entries!)
        {
            if (!key.EndsWith(".rels", StringComparison.Ordinal)) continue;

            var relsPath = NormalizeZipPath(entry.FullName);
            var relsFolder = ZipDirectoryOf(relsPath);                  // ".../_rels"
            var sourceFolder = relsFolder.EndsWith("_rels", StringComparison.OrdinalIgnoreCase)
                ? ZipDirectoryOf(relsFolder)
                : relsFolder;

            XElement rels;
            try
            {
                using var s = entry.Open();
                using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                rels = XDocument.Load(reader).Root!;
            }
            catch (XmlException)
            {
                continue;
            }

            foreach (var rel in rels.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                if (string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                var target = (string?)rel.Attribute("Target");
                var type = (string?)rel.Attribute("Type");
                if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(type)) continue;

                var targetPath = target.StartsWith('/') ? NormalizeZipPath(target) : NormalizeZipPath(sourceFolder + "/" + target);
                var role = type.TrimEnd('/')[(type.TrimEnd('/').LastIndexOf('/') + 1)..];
                var targetKey = ZipKey(targetPath);
                if (role == "RootDocument" || !roles.ContainsKey(targetKey)) roles[targetKey] = role;
            }
        }
        return roles;
    }

    private string FindRootPart(Dictionary<string, string> roles, string containerPath)
    {
        var declared = roles.Where(r => r.Value == "RootDocument" && _entries!.ContainsKey(r.Key)).Select(r => r.Key).FirstOrDefault();
        if (declared is not null) return NormalizeZipPath(_entries![declared].FullName);

        var documents = _entries!.Values
            .Select(e => NormalizeZipPath(e.FullName))
            .Where(p => p.EndsWith(".aml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (documents.Count == 1) return documents[0];
        if (documents.Count == 0)
            throw new McpException($"The AMLX container '{Path.GetFileName(containerPath)}' contains no .aml document.");

        var topLevel = documents.Where(p => !p.Contains('/')).ToList();
        if (topLevel.Count == 1) return topLevel[0];

        throw new McpException(
            $"The AMLX container '{Path.GetFileName(containerPath)}' declares no root document and holds {documents.Count} .aml files, " +
            "so it cannot tell which one is the root:" + Environment.NewLine +
            string.Join(Environment.NewLine, documents.Take(20).Select(d => "  " + d)));
    }

    /// <summary>Returns the normalised part path of a reference inside the container, or null.</summary>
    private string? FindContainerEntry(string reference, string baseFolder)
    {
        if (_entries is null || string.IsNullOrWhiteSpace(reference)) return null;

        var candidates = new List<string>();
        var direct = reference.StartsWith('/') || reference.StartsWith('\\')
            ? NormalizeZipPath(reference)
            : NormalizeZipPath(baseFolder + "/" + reference);
        candidates.Add(direct);
        candidates.Add(NormalizeZipPath(reference));

        foreach (var candidate in candidates)
            if (_entries.TryGetValue(ZipKey(candidate), out var entry))
                return NormalizeZipPath(entry.FullName);

        // Last resort: a unique file of that name anywhere in the container.
        var fileName = ZipKey(NormalizeZipPath(reference));
        fileName = fileName[(fileName.LastIndexOf('/') + 1)..];
        var byName = _entries.Where(e => e.Key == fileName || e.Key.EndsWith("/" + fileName, StringComparison.Ordinal)).ToList();
        return byName.Count == 1 ? NormalizeZipPath(byName[0].Value.FullName) : null;
    }

    private XElement LoadContainerEntry(string partPath)
    {
        var entry = _entries![ZipKey(partPath)];
        using var s = entry.Open();
        return SafeLoad(s, partPath);
    }

    /// <summary>Normalises a part path: forward slashes, percent-decoding, no leading slash, "." and ".." resolved.</summary>
    public static string NormalizeZipPath(string path)
    {
        var text = Uri.UnescapeDataString(path.Replace('\\', '/'));
        var parts = new List<string>();
        foreach (var segment in text.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return string.Join("/", parts);
    }

    private static string ZipKey(string path) => NormalizeZipPath(path).ToLowerInvariant();

    private static string ZipDirectoryOf(string partPath)
    {
        var normalized = NormalizeZipPath(partPath);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? "" : normalized[..slash];
    }

    private static bool IsPackageBookkeeping(string partPath) =>
        partPath.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
        || partPath.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ indexing

    private void IndexLinks()
    {
        foreach (var link in Root.Descendants(C + "InternalLink"))
        {
            var a = (string?)link.Attribute("RefPartnerSideA") ?? "";
            var b = (string?)link.Attribute("RefPartnerSideB") ?? "";
            var ia = FindInterface(a);
            var ib = FindInterface(b);
            var oa = ia is null ? null : OwnerOf(ia);
            var ob = ib is null ? null : OwnerOf(ib);
            var info = new LinkInfo(link, NameOf(link), a, b, ia, ib, oa, ob, HierarchyNameOf(oa), HierarchyNameOf(ob));
            Links.Add(info);

            if (oa is not null && ob is not null && oa != ob)
            {
                var detail = $"{info.Name} ({NameOf(ia)} -> {NameOf(ib)})";
                AddEdge(oa, new Edge("link", ob, detail));
                AddEdge(ob, new Edge("link", oa, detail));
            }
        }
    }

    private void IndexReferences()
    {
        foreach (var el in Root.Descendants().Where(e =>
                     e.Name == C + "InternalElement" || e.Name == C + "ExternalInterface" || e.Name == C + "InstanceHierarchy"))
        {
            foreach (var (attr, attrPath) in OwnAttributes(el))
            {
                var value = ValueOf(attr)?.Trim();
                if (string.IsNullOrEmpty(value) || !IsReference(attr, value)) continue;

                ById.TryGetValue(NormId(value), out var target);
                Refs.Add(new RefInfo(el, attrPath, value, target));
                if (target is null) continue;

                var from = AnchorOf(el);
                var to = AnchorOf(target);
                if (from == to) continue;
                AddEdge(from, new Edge("reference", to, attrPath));
                AddEdge(to, new Edge("referenced-by", from, attrPath));
            }
        }
    }

    private void IndexMirrors()
    {
        foreach (var el in Root.Descendants().Where(e => e.Name == C + "InternalElement" || e.Name == C + "ExternalInterface"))
        {
            var masterId = MirrorReferenceOf(el);
            if (masterId is null) continue;

            if (!ById.TryGetValue(NormId(masterId), out var master))
            {
                DanglingMirrors.Add(new DanglingMirror(el, masterId));
                continue;
            }

            var from = AnchorOf(el);
            var to = AnchorOf(master);
            if (from == to) continue;
            AddEdge(from, new Edge("mirror-of", to, el.Name.LocalName == "ExternalInterface" ? NameOf(el) : ""));
            AddEdge(to, new Edge("mirrored-by", from, el.Name.LocalName == "ExternalInterface" ? NameOf(el) : ""));
        }
    }

    private void AddEdge(XElement from, Edge edge)
    {
        if (!_edges.TryGetValue(from, out var list)) _edges[from] = list = new List<Edge>();
        list.Add(edge);
    }

    // ------------------------------------------------------------------ semantics

    /// <summary>
    /// A reference is an attribute typed as an object reference (ObjectReferences
    /// library, a refObj attribute type, xs:IDREF), or an attribute named like
    /// one (refXxx) whose value actually has the shape of an ID. A name alone is
    /// not enough: refTemperature = 25 is a value, not a reference.
    /// </summary>
    public bool IsReference(XElement attribute, string value)
    {
        var type = (string?)attribute.Attribute("RefAttributeType") ?? "";
        var dataType = (string?)attribute.Attribute("AttributeDataType") ?? "";
        if (type.Contains("ObjectReferences", StringComparison.OrdinalIgnoreCase)) return true;
        if (type.EndsWith("/refObj", StringComparison.Ordinal)) return true;
        if (dataType.Equals("xs:IDREF", StringComparison.OrdinalIgnoreCase)) return true;

        if (Options.StrictConventions) return false;

        var name = NameOf(attribute);
        var namedLikeReference = name.Length > 3 && name.StartsWith("ref", StringComparison.Ordinal) && char.IsUpper(name[3]);
        if (!namedLikeReference) return false;
        return ById.ContainsKey(NormId(value)) || Guid.TryParse(NormId(value), out _);
    }

    /// <summary>
    /// CAEX mirror objects carry the master's ID instead of a class path in
    /// RefBaseSystemUnitPath (elements) or RefBaseClassPath (interfaces). A class
    /// path always contains '/', an ID never does.
    /// </summary>
    public static string? MirrorReferenceOf(XElement e)
    {
        var attributeName = e.Name.LocalName switch
        {
            "InternalElement" => "RefBaseSystemUnitPath",
            "ExternalInterface" => "RefBaseClassPath",
            _ => null
        };
        var value = attributeName is null ? null : ((string?)e.Attribute(attributeName))?.Trim();
        if (string.IsNullOrEmpty(value) || value.Contains('/')) return null;
        return value;
    }

    public XElement? MirrorMasterOf(XElement e)
    {
        var id = MirrorReferenceOf(e);
        return id is not null && ById.TryGetValue(NormId(id), out var master) ? master : null;
    }

    /// <summary>The effective system unit class path, following mirror objects to their master.</summary>
    public string? ClassOf(XElement e)
    {
        var current = e;
        for (var guard = 0; guard < 16; guard++)
        {
            var master = MirrorMasterOf(current);
            if (master is null) return MirrorReferenceOf(current) is null ? SystemUnitClassOf(current) : null;
            current = master;
        }
        return null;
    }

    /// <summary>The class and all its base classes, most specific first.</summary>
    public IEnumerable<ClassInfo> ClassChain(string? reference, XElement? context = null)
    {
        var info = ResolveClass(reference, context);
        var seen = new HashSet<XElement>();
        while (info is not null && seen.Add(info.Element))
        {
            yield return info;
            info = ResolveClass((string?)info.Element.Attribute("RefBaseClassPath"), info.Element);
        }
    }

    /// <summary>
    /// Attributes defined along the class chain that the element does not define
    /// itself. For an element the chain starts at its (effective) system unit class,
    /// for a class at its base class.
    /// </summary>
    public List<InheritedAttribute> InheritedAttributes(XElement element, bool includeLayout = false)
    {
        var start = ClassKinds.Contains(element.Name.LocalName)
            ? (string?)element.Attribute("RefBaseClassPath")
            : ClassOf(element);

        var seen = new HashSet<string>(OwnAttributes(element).Select(a => a.Path), StringComparer.Ordinal);
        var result = new List<InheritedAttribute>();
        foreach (var cls in ClassChain(start))
        {
            foreach (var (attribute, path) in OwnAttributes(cls.Element))
            {
                if (!includeLayout && IsInsidePresentation(attribute)) continue;
                if (seen.Add(path)) result.Add(new InheritedAttribute(path, attribute, cls.Key));
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ queries

    /// <summary>A class whose name (not path) matches, used to explain a reference that is neither ID nor path.</summary>
    public ClassInfo? ClassByName(string name) =>
        Classes.Values.FirstOrDefault(c => string.Equals(ShortClass(c.PlainPath), name, StringComparison.OrdinalIgnoreCase));

    public ClassInfo? ResolveClass(string? reference) => ResolveClass(reference, null);

    /// <summary>
    /// Resolves a class reference. CAEX 2.15 documents, the official AutomationML examples
    /// among them, often name a base class without its library; such a name is resolved
    /// when it is unique, or unique within the library of the referencing class.
    /// </summary>
    public ClassInfo? ResolveClass(string? reference, XElement? context)
    {
        var resolved = ResolveClassPath(reference);
        if (resolved is not null || string.IsNullOrWhiteSpace(reference)) return resolved;

        var (_, name, segments) = SplitClassPath(reference.Trim());
        if (segments > 1 || !_byName.TryGetValue(name, out var candidates)) return null;
        if (candidates.Count == 1) return candidates[0];
        var library = context?.AncestorsAndSelf().FirstOrDefault(a => LibraryKinds.Contains(a.Name.LocalName));
        var local = library is null ? new List<ClassInfo>() : candidates.Where(c => c.Library == NameOf(library)).ToList();
        return local.Count == 1 ? local[0] : null;
    }

    private ClassInfo? ResolveClassPath(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (Classes.TryGetValue(reference, out var exact)) return exact;

        var (alias, plain, _) = SplitClassPath(reference.Trim());
        if (alias is not null)
        {
            if (Classes.TryGetValue(alias + "@" + plain, out var aliased)) return aliased;
            if (ExternalRefs.Any(r => !r.Loaded && string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        return _plainIndex.TryGetValue(plain, out var tolerant) ? tolerant : null;
    }

    /// <summary>
    /// Splits a class path into alias and plain path. A segment in square brackets is a
    /// name that may itself contain '/' or '@', as in [ATL_http://opcfoundation.org/UA/]/[NodeId].
    /// </summary>
    public static (string? Alias, string Plain, int Segments) SplitClassPath(string reference)
    {
        var segments = new List<string>();
        string? alias = null;
        var current = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var ch in reference)
        {
            if (ch == '[' && depth++ == 0) continue;
            if (ch == ']' && depth > 0 && --depth == 0) continue;
            if (depth == 0 && ch == '@' && alias is null && segments.Count == 0)
            {
                alias = current.ToString();
                current.Clear();
                continue;
            }
            if (depth == 0 && ch == '/')
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        segments.Add(current.ToString());
        return (alias, string.Join("/", segments), segments.Count);
    }

    /// <summary>Resolves an element by ID (with or without braces), by path, or by unique name.</summary>
    public XElement ResolveElement(string? query)
    {
        var q = query?.Trim() ?? "";
        if (q.Length == 0)
            throw new McpException("Name an element: its ID, a path like Hierarchy/Parent/Child, or its name. Use find_elements to look one up.");

        if (ById.TryGetValue(NormId(q), out var byId))
            return byId.Name == C + "ExternalInterface" ? OwnerOf(byId) ?? byId : byId;

        if (q.Contains('/'))
        {
            var byPath = FindByPath(q);
            if (byPath is not null) return byPath;
        }

        var matches = Root.Descendants()
            .Where(e => NodeKinds.Contains(e.Name.LocalName) && string.Equals(NameOf(e), q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count > 1)
            throw new McpException(
                $"'{q}' is ambiguous ({matches.Count} elements). Use an ID or a path:\n" +
                string.Join("\n", matches.Take(15).Select(m => $"  {PathOf(m)}  id={IdOf(m)}")));

        throw new McpException($"No element '{q}'. Use an ID, a path like Hierarchy/Parent/Child, or find_elements.");
    }

    public XElement? FindByPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        static bool Is(XElement e, string name) => string.Equals(NameOf(e), name, StringComparison.OrdinalIgnoreCase);

        var starts = Hierarchies.Where(h => Is(h, parts[0])).ToList();
        if (starts.Count == 0)
            starts = Hierarchies.SelectMany(h => h.Elements(C + "InternalElement")).Where(e => Is(e, parts[0])).ToList();

        foreach (var start in starts)
        {
            XElement? current = start;
            for (var i = 1; i < parts.Length && current is not null; i++)
                current = current.Elements(C + "InternalElement").FirstOrDefault(e => Is(e, parts[i]));
            if (current is not null) return current;
        }
        return null;
    }

    public IEnumerable<Edge> EdgesOf(XElement element, bool includeContainment = true)
    {
        if (includeContainment)
        {
            var parent = element.Parent;
            if (parent is not null && NodeKinds.Contains(parent.Name.LocalName))
                yield return new Edge("parent", parent, "");
            foreach (var child in element.Elements(C + "InternalElement"))
                yield return new Edge("child", child, "");
        }
        if (_edges.TryGetValue(element, out var list))
            foreach (var edge in list) yield return edge;
    }

    /// <summary>Finds a link partner: an interface ID, or the legacy form "ownerIdOrPath:InterfaceName" used by CAEX 2.15.</summary>
    public XElement? FindInterface(string side)
    {
        if (string.IsNullOrWhiteSpace(side)) return null;
        if (ById.TryGetValue(NormId(side), out var byId) && byId.Name == C + "ExternalInterface") return byId;

        var colon = side.LastIndexOf(':');
        if (colon <= 0) return null;
        var ownerRef = side[..colon];
        var interfaceName = side[(colon + 1)..];
        var owner = ById.TryGetValue(NormId(ownerRef), out var o) ? o : FindByPath(ownerRef);
        return owner?.Descendants(C + "ExternalInterface")
            .FirstOrDefault(i => string.Equals(NameOf(i), interfaceName, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ helpers

    public static string NormId(string id) => id.Trim().Trim('{', '}');

    public static string NameOf(XElement? e) => (string?)e?.Attribute("Name") ?? "";

    public static string IdOf(XElement? e) => (string?)e?.Attribute("ID") ?? "";

    public static string? DescriptionOf(XElement e)
    {
        var text = e.Element(C + "Description")?.Value.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public static string? ValueOf(XElement attribute) =>
        (string?)attribute.Element(C + "Value") ?? (string?)attribute.Element(C + "DefaultValue");

    public static XElement? OwnerOf(XElement @interface) =>
        @interface.Ancestors().FirstOrDefault(a =>
            a.Name.LocalName is "InternalElement" or "SystemUnitClass" or "InstanceHierarchy" or "InterfaceClass" or "RoleClass");

    /// <summary>The element a reference, link or mirror is conceptually attached to.</summary>
    public static XElement AnchorOf(XElement e) =>
        e.Name.LocalName == "ExternalInterface" ? OwnerOf(e) ?? e : e;

    public static string? HierarchyNameOf(XElement? e) =>
        e is null ? null : NameOf(e.AncestorsAndSelf(C + "InstanceHierarchy").FirstOrDefault());

    /// <summary>PathOf for instance elements, the library path for classes.</summary>
    public static string DisplayPathOf(XElement e)
    {
        var path = PathOf(e);
        if (path.Length > 0) return path;
        return string.Join("/", e.AncestorsAndSelf()
            .Where(a => LibraryKinds.Contains(a.Name.LocalName) || ClassKinds.Contains(a.Name.LocalName))
            .Reverse().Select(a => NameOf(a)));
    }

    public static string PathOf(XElement e) =>
        string.Join("/", e.AncestorsAndSelf()
            .Where(a => a.Name.LocalName is "InstanceHierarchy" or "InternalElement" or "ExternalInterface")
            .Reverse().Select(a => NameOf(a)));

    public static IEnumerable<(XElement Attribute, string Path)> OwnAttributes(XElement element, string prefix = "")
    {
        foreach (var attribute in element.Elements(C + "Attribute"))
        {
            var path = prefix.Length == 0 ? NameOf(attribute) : prefix + "/" + NameOf(attribute);
            yield return (attribute, path);
            foreach (var nested in OwnAttributes(attribute, path)) yield return nested;
        }
    }

    /// <summary>
    /// Presentation attributes: geometry that answers no engineering question.
    /// Recognised by their attribute type, which the Diagram Definition library of
    /// the Object Management Group provides (DD_Bounds, DD_Point, DD_Waypoint).
    /// Unless strict conventions are requested, the name conventions of older
    /// libraries are recognised as well.
    /// </summary>
    public bool IsPresentationAttribute(XElement attribute)
    {
        var type = (string?)attribute.Attribute("RefAttributeType") ?? "";
        if (type.Length > 0)
        {
            var plain = type.Contains('@') ? type[(type.IndexOf('@') + 1)..] : type;
            if (plain.Contains("/DD_", StringComparison.Ordinal)) return true;
            if (type.Contains("_DI_", StringComparison.Ordinal)) return true;
            var resolved = ResolveClass(type);
            if (resolved is not null && resolved.Kind == "AttributeType"
                && (resolved.Library.Contains("DD", StringComparison.Ordinal)
                    || resolved.PlainPath.Contains("/DD_", StringComparison.Ordinal)))
                return true;
        }
        if (Options.StrictConventions) return false;

        var name = NameOf(attribute);
        return name is "ViewInformation" or "PortCoordinate" or "LabelOffset"
               || name.StartsWith("Waypoint", StringComparison.Ordinal);
    }

    public bool IsInsidePresentation(XElement attribute) =>
        attribute.AncestorsAndSelf(C + "Attribute").Any(IsPresentationAttribute);

    public static string? SystemUnitClassOf(XElement e) => (string?)e.Attribute("RefBaseSystemUnitPath");

    public static IEnumerable<string> RolesOf(XElement e) =>
        e.Elements(C + "RoleRequirements").Select(r => (string?)r.Attribute("RefBaseRoleClassPath"))
            .Concat(e.Elements(C + "SupportedRoleClass").Select(r => (string?)r.Attribute("RefRoleClassPath")))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!);

    public static string ShortClass(string? path) =>
        string.IsNullOrEmpty(path) ? "" : path[(path.LastIndexOf('/') + 1)..];
}
