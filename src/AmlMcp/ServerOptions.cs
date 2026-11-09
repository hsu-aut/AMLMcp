namespace AmlMcp;

/// <summary>
/// Settings from the command line or the environment.
///
/// <para>
/// <c>--root &lt;dir&gt;</c> (repeatable, or <c>AML_MCP_ROOTS</c> with the platform path
/// separator) restricts which directories the server may read. Without it the server
/// reads whatever the user who started it can read.
/// </para>
/// <para>
/// <c>--write &lt;dir&gt;</c> (repeatable, or <c>AML_MCP_WRITE</c> with the platform path
/// separator) is the separate fence for writing. It switches writing on and bounds it at
/// the same time: without it nothing is written anywhere, and a directory the server may
/// read is not a directory it may write into.
/// </para>
/// <para>
/// <c>--strict-conventions</c> (or <c>AML_MCP_STRICT=1</c>) limits the interpretation of
/// attributes to what CAEX and the shared AutomationML libraries define: references are
/// recognised by their attribute type, presentation data by the Diagram Definition types.
/// Without it the server additionally applies name conventions that older libraries use,
/// such as an attribute called ViewInformation holding geometry.
/// </para>
/// </summary>
public sealed record ServerOptions(
    IReadOnlyList<string> Roots,
    bool StrictConventions,
    string? FollowFile = null,
    string? SelectFile = null,
    IReadOnlyList<string>? WriteRootsOrNull = null)
{
    public static ServerOptions Default { get; } = new(Array.Empty<string>(), false);

    /// <summary>The directories the server may write into. Empty means it writes nowhere.</summary>
    public IReadOnlyList<string> WriteRoots => WriteRootsOrNull ?? Array.Empty<string>();

    public static ServerOptions Parse(IReadOnlyList<string> args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    /// <summary>
    /// The same parse with the environment handed in, so that a test does not have to change
    /// the environment of the whole process to cover what the variables do.
    /// </summary>
    public static ServerOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var roots = new List<string>();
        var writeRoots = new List<string>();
        var strict = environment("AML_MCP_STRICT") is "1" or "true";
        var follow = environment("AML_MCP_FOLLOW");
        var select = environment("AML_MCP_SELECT");

        var fromEnvironment = environment("AML_MCP_ROOTS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            roots.AddRange(fromEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var writeFromEnvironment = environment("AML_MCP_WRITE");
        if (!string.IsNullOrWhiteSpace(writeFromEnvironment))
            writeRoots.AddRange(writeFromEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // Unknown arguments are an error: --roots must not silently mean no restriction.
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--root" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    roots.Add(args[++i]);
                    break;
                case "--root":
                    throw new ArgumentException("--root needs a directory.");
                // The fence for writing. It switches writing on and bounds it at once.
                case "--write" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    writeRoots.Add(args[++i]);
                    break;
                case "--write":
                    throw new ArgumentException("--write needs a directory.");
                case "--strict-conventions":
                    strict = true;
                    break;
                // A file that holds the path of the document an editor currently shows.
                case "--follow" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    follow = args[++i];
                    break;
                case "--follow":
                    throw new ArgumentException("--follow needs a file.");
                // A file an editor watches: the server writes into it which element to show.
                case "--select" when i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal):
                    select = args[++i];
                    break;
                case "--select":
                    throw new ArgumentException("--select needs a file.");
                case "--help" or "-h" or "--version":
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Run with --help to see the accepted options.");
            }
        }

        return new ServerOptions(Normalize(roots), strict,
            string.IsNullOrWhiteSpace(follow) ? null : Path.GetFullPath(follow.Trim().Trim('"')),
            string.IsNullOrWhiteSpace(select) ? null : Path.GetFullPath(select.Trim().Trim('"')),
            Normalize(writeRoots));
    }

    private static List<string> Normalize(IEnumerable<string> directories) => directories
        .Select(r => r.Trim().Trim('"'))
        .Where(r => r.Length > 0)
        .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>True when the path lies inside one of the configured roots, or when no root is configured.</summary>
    public bool Allows(string fullPath) => Roots.Count == 0 || Within(fullPath, Roots);

    /// <summary>
    /// True when the path may be written. Unlike reading, an empty fence allows nothing:
    /// a server nobody gave a write directory to writes nowhere.
    /// </summary>
    public bool AllowsWrite(string fullPath) => AllowsWrite(fullPath, LinkTargetOf);

    /// <summary>
    /// The same with the link resolution handed in. A link inside a write directory that
    /// points out of it must not widen the fence, so the resolved path is checked as well.
    /// </summary>
    public bool AllowsWrite(string fullPath, Func<string, string?> resolveLink)
    {
        if (WriteRoots.Count == 0) return false;
        if (!Within(fullPath, WriteRoots)) return false;
        var resolved = resolveLink(fullPath);
        return resolved is null || Within(resolved, WriteRoots);
    }

    private static bool Within(string fullPath, IReadOnlyList<string> directories)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return directories.Any(root =>
        {
            // A drive root already ends in a separator, and "C:\\" matches nothing.
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                   || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Where a link leads, or null when the path is no link. The target itself may not exist
    /// yet, so the deepest directory that does exist is examined too: writing into a
    /// directory that is a link elsewhere is writing elsewhere.
    /// </summary>
    private static string? LinkTargetOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (File.ResolveLinkTarget(full, returnFinalTarget: true) is { } file) return file.FullName;
            if (Directory.ResolveLinkTarget(full, returnFinalTarget: true) is { } directory) return directory.FullName;

            for (var parent = Path.GetDirectoryName(full); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (!Directory.Exists(parent)) continue;
                if (Directory.ResolveLinkTarget(parent, returnFinalTarget: true) is not { } target) return null;
                return Path.Combine(target.FullName, Path.GetRelativePath(parent, full));
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable is not writable: let the caller's own check refuse it later.
            return null;
        }
    }

    public string RootsDescription => Roots.Count == 0 ? "no restriction" : string.Join(", ", Roots);

    public string WriteRootsDescription => WriteRoots.Count == 0 ? "none" : string.Join(", ", WriteRoots);
}
