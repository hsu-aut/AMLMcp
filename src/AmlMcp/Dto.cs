using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmlMcp;

// Machine-readable shapes returned next to the human-readable text of a tool, so
// that clients and programs can consume the answers without parsing prose.

public sealed record ExternalReferenceInfo(string Alias, string Path, bool Loaded, string? ResolvedFrom, string? Problem, bool OutsideContainer);

public sealed record ContainerPartInfo(string Path, long Size, string? Role);

public sealed record LibraryInfo(string Name, int Classes, bool External);

public sealed record HierarchyInfo(string Name, string? Id, int Elements, int Mirrors, string? Description, IReadOnlyList<ClassCountInfo> ByClass);

public sealed record ClassCountInfo(string Class, int Count);

public sealed record HierarchyConnectionInfo(string From, string To, string Kind, int Count);

public sealed record DocumentInfo(
    string Path,
    string FileName,
    string SchemaVersion,
    long SizeBytes,
    bool IsContainer,
    string? RootPart,
    IReadOnlyList<ContainerPartInfo> ContainerParts,
    IReadOnlyList<ExternalReferenceInfo> ExternalReferences,
    IReadOnlyList<LibraryInfo> Libraries,
    IReadOnlyList<HierarchyInfo> Hierarchies,
    int Links,
    int CrossHierarchyLinks,
    int UnresolvedLinks,
    int References,
    int CrossHierarchyReferences,
    int DanglingReferences,
    int DuplicateIds,
    IReadOnlyList<HierarchyConnectionInfo> HierarchyConnections);

public sealed record ElementRef(string Id, string Name, string Path, string? Hierarchy, string? Class, bool IsMirror);

public sealed record AttributeInfo(string Path, string? Value, string? Unit, bool IsDefault, string? InheritedFrom);

public sealed record InterfaceInfo(string Id, string Name, string? Class, string? MirrorOf);

public sealed record ConnectionInfo(string Kind, string Detail, string TargetId, string TargetPath, string? TargetHierarchy, bool CrossHierarchy);

public sealed record ElementInfo(
    string Id,
    string Name,
    string Kind,
    string Path,
    string? Hierarchy,
    ElementRef? MirrorOf,
    string? Class,
    bool ClassResolved,
    string? ClassDescription,
    IReadOnlyList<string> Roles,
    string? Description,
    IReadOnlyList<AttributeInfo> Attributes,
    IReadOnlyList<InterfaceInfo> Interfaces,
    IReadOnlyList<ElementRef> Children,
    IReadOnlyList<ConnectionInfo> Connections,
    IReadOnlyList<string> DanglingReferences,
    int HiddenPresentationAttributes);

public sealed record ElementListInfo(int Total, int Shown, IReadOnlyList<ElementRef> Elements);

public sealed record PathStepInfo(string Kind, string Detail, ElementRef To);

public sealed record PathInfo(bool Found, ElementRef? From, ElementRef? To, int Steps, IReadOnlyList<PathStepInfo> Path, string? Reason);

public sealed record ClassDetailInfo(
    string Key,
    string Kind,
    string Library,
    bool External,
    string SourceFile,
    string? Description,
    IReadOnlyList<string> InheritsFrom,
    IReadOnlyList<AttributeInfo> Attributes,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> SupportedRoles,
    IReadOnlyList<string> Subclasses,
    int UsedByElements);

public sealed record ClassListInfo(int Total, int Shown, IReadOnlyList<ClassSummaryInfo> Classes);

public sealed record ClassMatchesInfo(int Total, IReadOnlyList<ClassDetailInfo> Classes);

public sealed record TreeNodeInfo(
    string Id,
    string Name,
    string Path,
    string? Class,
    string? MirrorOf,
    int CrossHierarchyConnections,
    int HiddenChildren,
    IReadOnlyList<TreeNodeInfo> Children);

public sealed record TreeInfo(string? Root, int Depth, bool Truncated, IReadOnlyList<TreeNodeInfo> Nodes);

public sealed record NeighborInfo(int Hop, string Kind, string Detail, ElementRef From, ElementRef To, bool CrossHierarchy);

public sealed record NeighborsInfo(ElementRef From, int Depth, int Total, IReadOnlyList<NeighborInfo> Neighbors);

public sealed record ClassSummaryInfo(string Key, string Kind, string Library, bool External, string? Description);

public sealed record ProblemInfo(string Kind, string Message);

public sealed record CheckInfo(
    string Document,
    bool Ok,
    IReadOnlyList<ProblemInfo> Problems,
    IReadOnlyList<ProblemInfo> Notes,
    int Classes,
    int Links,
    int References,
    int Ids);

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
