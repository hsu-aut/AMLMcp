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
/// <c>--strict-conventions</c> (or <c>AML_MCP_STRICT=1</c>) limits the interpretation of
/// attributes to what CAEX and the shared AutomationML libraries define: references are
/// recognised by their attribute type, presentation data by the Diagram Definition types.
/// Without it the server additionally applies name conventions that older libraries use,
/// such as an attribute called ViewInformation holding geometry.
/// </para>
/// </summary>
public sealed record ServerOptions(IReadOnlyList<string> Roots, bool StrictConventions)
{
    public static ServerOptions Default { get; } = new(Array.Empty<string>(), false);

    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        var roots = new List<string>();
        var strict = Environment.GetEnvironmentVariable("AML_MCP_STRICT") is "1" or "true";

        var fromEnvironment = Environment.GetEnvironmentVariable("AML_MCP_ROOTS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            roots.AddRange(fromEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
                case "--strict-conventions":
                    strict = true;
                    break;
                case "--help" or "-h" or "--version":
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Run with --help to see the accepted options.");
            }
        }

        var normalized = roots
            .Select(r => r.Trim().Trim('"'))
            .Where(r => r.Length > 0)
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ServerOptions(normalized, strict);
    }

    /// <summary>True when the path lies inside one of the configured roots, or when no root is configured.</summary>
    public bool Allows(string fullPath)
    {
        if (Roots.Count == 0) return true;
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        return Roots.Any(root =>
            candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public string RootsDescription => Roots.Count == 0 ? "no restriction" : string.Join(", ", Roots);
}
