using ModelContextProtocol;

namespace AmlMcp;

/// <summary>
/// Keeps opened AutomationML documents in memory. A document is reloaded
/// transparently when its file changed on disk, so an editor session and the
/// MCP server can work on the same file.
/// </summary>
public sealed class DocumentStore
{
    private readonly Dictionary<string, AmlModel> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ServerOptions _options;
    private string? _current;
    private string? _followed;

    public DocumentStore() : this(ServerOptions.Default) { }

    public DocumentStore(ServerOptions options) => _options = options;

    public ServerOptions Options => _options;

    public AmlModel Open(string path)
    {
        var full = FullPath(path);
        if (!_options.Allows(full))
            throw new McpException(
                $"'{full}' is outside the directories this server may read ({_options.RootsDescription}).");
        if (Directory.Exists(full))
            throw new McpException($"'{full}' is a directory. Name an .aml or .amlx file inside it.");
        if (!File.Exists(full))
            throw new McpException($"File not found: {full}");
        lock (_gate)
        {
            AmlModel model;
            try { model = AmlModel.Load(full, _options); }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new McpException($"Could not read '{full}' as AutomationML: {ex.Message}");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Internal error while reading '{full}': {ex.GetType().Name}: {ex.Message}");
            }
            _documents[full] = model;
            _current = full;
            // What the editor points at right now counts as seen, so an unchanged pointer
            // does not override the document the client just asked for.
            _followed = Followed();
            return model;
        }
    }

    /// <summary>Returns the given document, or the most recently opened one.</summary>
    public AmlModel Get(string? path)
    {
        lock (_gate)
        {
            string? key = string.IsNullOrWhiteSpace(path) ? Current() : FullPath(path);
            if (key is null)
                throw new McpException(FollowedProblem is { } why
                    ? $"No document to work on: {why}."
                    : _options.FollowFile is null
                        ? "No AutomationML document is open. Call open_aml_document with the path of an .aml or .amlx file."
                        : "No document is open in the editor, and none was named. Open a file in the AutomationML editor, or call open_aml_document with a path.");

            if (!_documents.TryGetValue(key, out var model))
                return Open(key);

            if (!File.Exists(key))
                throw new McpException($"'{key}' was open, but the file is gone now.");

            if (File.GetLastWriteTimeUtc(key) != model.LoadedWriteTimeUtc)
                return Open(key);

            _current = key;
            return model;
        }
    }

    /// <summary>
    /// The document to answer about when the client names none: what an editor last put into
    /// the file given with --follow, otherwise the one opened last through this server.
    /// </summary>
    private string? Current()
    {
        var pointed = Followed();
        if (pointed is not null && !string.Equals(pointed, _followed, StringComparison.OrdinalIgnoreCase))
        {
            _followed = pointed;
            return pointed;
        }
        return _current ?? pointed;
    }

    /// <summary>The document an editor points at, and why it cannot be used when it cannot.</summary>
    public string? FollowedProblem { get; private set; }

    private string? Followed()
    {
        FollowedProblem = null;
        if (_options.FollowFile is not { } file) return null;
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file).Trim().Trim('"');
            if (text.Length == 0) return null;
            var full = Path.GetFullPath(text);
            if (!File.Exists(full))
            {
                FollowedProblem = $"the editor points at '{full}', which does not exist";
                return null;
            }
            if (!_options.Allows(full))
            {
                FollowedProblem = $"the editor has '{full}' open, which is outside the directories this server may read ({_options.RootsDescription})";
                return null;
            }
            return full;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or PathTooLongException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Turns what a client sent into a full path, with a usable message when it cannot.</summary>
    private static string FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpException("A file path is required, for example the path of an .aml or .amlx file.");
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"'{path}' is not a usable file path: {ex.Message}");
        }
    }

    public IReadOnlyCollection<string> OpenDocuments
    {
        get { lock (_gate) return _documents.Keys.ToList(); }
    }
}
