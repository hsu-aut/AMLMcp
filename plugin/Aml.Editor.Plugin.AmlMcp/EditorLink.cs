// The other direction: the assistant names an element, the server writes a request, this
// selects the element in the editor and writes back what happened. Without that answer the
// server does not tell the assistant that anything was shown.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Aml.Editor.API;

namespace Aml.Editor.Plugin.AmlMcp;

internal sealed record ShowRequest(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path);

internal sealed record ShowAck(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail = null);

internal sealed class EditorLink : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _report;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _poll;
    private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
    private DateTime _lastSeenWrite;

    public EditorLink(Dispatcher dispatcher, Action<string> report)
    {
        _dispatcher = dispatcher;
        _report = report;

        var directory = Path.GetDirectoryName(McpProbe.SelectFile);
        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
                _watcher = new FileSystemWatcher(directory, Path.GetFileName(McpProbe.SelectFile))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => Handle();
                _watcher.Created += (_, _) => Handle();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _watcher = null;
            }
        }

        // A watcher misses events on some shares and after a lost handle, and the request is
        // worthless a few seconds late, so the file is looked at regularly as well.
        _poll = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _poll.Tick += (_, _) => Handle();
        _poll.Start();
    }

    public void Dispose()
    {
        _poll.Stop();
        _watcher?.Dispose();
    }

    /// <summary>The document the editor currently shows, straight from its own API.</summary>
    public static string? CurrentDocument()
    {
        try
        {
            return AMLEditor.AMLApplication?.ActiveDocument?.FilePath;
        }
        catch
        {
            return null;
        }
    }

    private void Handle()
    {
        ShowRequest? request;
        try
        {
            if (!File.Exists(McpProbe.SelectFile)) return;
            var written = File.GetLastWriteTimeUtc(McpProbe.SelectFile);
            if (written == _lastSeenWrite && _answered.Count > 0) return;
            _lastSeenWrite = written;

            request = Read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        // Each request is answered once. Two requests for the same element differ in their nonce.
        if (request is null || !_answered.Add(request.Nonce)) return;

        _dispatcher.BeginInvoke(new Action(() => Show(request)));
    }

    private void Show(ShowRequest request)
    {
        try
        {
            var editor = AMLEditor.AMLApplication;
            if (editor is null)
            {
                Answer(request, "unavailable", "the editor API is not available in this session");
                return;
            }

            // The assistant may be talking about another document than the one on screen.
            var open = CurrentDocument();
            if (!string.IsNullOrEmpty(open) && !string.IsNullOrEmpty(request.Document) &&
                !string.Equals(Path.GetFullPath(open), Path.GetFullPath(request.Document), StringComparison.OrdinalIgnoreCase))
            {
                Answer(request, "mismatch", $"the editor has {Path.GetFileName(open)} open, the element belongs to {Path.GetFileName(request.Document)}");
                _report($"the assistant pointed at an element of {Path.GetFileName(request.Document)}, which is not the open document");
                return;
            }

            editor.ExpandObjectById(request.Id);
            editor.SelectObjectById(request.Id);
            Answer(request, "selected");
            _report($"the assistant pointed at {request.Path}");
        }
        catch (Exception ex)
        {
            Answer(request, "failed", ex.Message);
            _report($"could not select {request.Path}: {ex.Message}");
        }
    }

    private void Answer(ShowRequest request, string status, string? detail = null)
    {
        try
        {
            File.WriteAllText(McpProbe.SelectFile + ".ack",
                JsonSerializer.Serialize(new ShowAck(request.Nonce, status, detail)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _report("the element was selected, but the assistant could not be told");
        }
    }

    private ShowRequest? Read()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(McpProbe.SelectFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd().Trim();
                if (text.Length == 0) return null;
                // Older servers wrote the bare ID.
                return text.StartsWith("{", StringComparison.Ordinal)
                    ? JsonSerializer.Deserialize<ShowRequest>(text)
                    : new ShowRequest(text, "", text, text);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or JsonException or FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }
        return null;
    }
}
