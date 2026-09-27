// The other direction: the assistant names an element, the server writes a request, this
// selects the element in the editor and writes back what happened. Without that answer the
// server does not tell the assistant that anything was shown.
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// <summary>How many answered requests to remember, so the set cannot grow all session.</summary>
    private const int Remembered = 64;

    private readonly IEditorSelection _editor;
    private readonly Action<Action> _onUiThread;
    private readonly Action<string> _report;
    private readonly string _requestFile;
    private readonly FileSystemWatcher? _watcher;
    private readonly Queue<string> _answered = new();
    private readonly HashSet<string> _answeredSet = new(StringComparer.Ordinal);
    // The watcher fires on a pool thread and the timer on the UI thread; without this both
    // can pass the duplicate check for the same request and then collide writing the answer.
    private readonly object _gate = new();

    /// <param name="watch">
    /// False drives the link by <see cref="Poll"/> alone, which is what the tests do: a live
    /// watcher would answer requests between their steps.
    /// </param>
    public EditorLink(IEditorSelection editor, Action<Action> onUiThread, Action<string> report, string requestFile, bool watch = true)
    {
        _editor = editor;
        _onUiThread = onUiThread;
        _report = report;
        _requestFile = requestFile;

        var directory = Path.GetDirectoryName(_requestFile);
        if (!watch || string.IsNullOrEmpty(directory)) return;
        try
        {
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_requestFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => Poll();
            _watcher.Created += (_, _) => Poll();
            _watcher.Renamed += (_, _) => Poll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher = null;
        }
    }

    public void Dispose() => _watcher?.Dispose();

    public string AckFile => _requestFile + ".ack";

    /// <summary>
    /// Looks at the request file and answers a request that has not been answered yet.
    /// Called by the watcher, and on a timer, because a watcher misses events on some shares.
    /// </summary>
    public void Poll()
    {
        ShowRequest request;
        lock (_gate)
        {
            var read = Read();
            // Each request is answered once. Two requests for the same element differ in their nonce.
            if (read is null || _answeredSet.Contains(read.Nonce)) return;
            Remember(read.Nonce);
            request = read;
        }

        _onUiThread(() => Show(request));
    }

    private void Remember(string nonce)
    {
        _answeredSet.Add(nonce);
        _answered.Enqueue(nonce);
        while (_answered.Count > Remembered) _answeredSet.Remove(_answered.Dequeue());
    }

    private void Show(ShowRequest request)
    {
        try
        {
            if (!_editor.Available)
            {
                Answer(request, "unavailable", "the editor API is not available in this session");
                return;
            }

            // The assistant may be talking about another document than the one on screen.
            var open = _editor.CurrentDocument;
            if (!string.IsNullOrEmpty(open) && !string.IsNullOrEmpty(request.Document) && !SameFile(open!, request.Document))
            {
                Answer(request, "mismatch",
                    $"the editor has {Path.GetFileName(open)} open, the element belongs to {Path.GetFileName(request.Document)}");
                _report($"the assistant pointed at an element of {Path.GetFileName(request.Document)}, which is not the open document");
                return;
            }

            _editor.Select(request.Id);
            Answer(request, "selected");
            _report($"the assistant pointed at {request.Path}");
        }
        catch (Exception ex)
        {
            Answer(request, "failed", ex.Message);
            _report($"could not select {request.Path}: {ex.Message}");
        }
    }

    private static bool SameFile(string one, string other)
    {
        try
        {
            return string.Equals(Path.GetFullPath(one), Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(one, other, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void Answer(ShowRequest request, string status, string? detail = null)
    {
        try
        {
            File.WriteAllText(AckFile, JsonSerializer.Serialize(new ShowAck(request.Nonce, status, detail)));
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
                if (!File.Exists(_requestFile)) return null;
                using var stream = new FileStream(_requestFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd().Trim();
                if (text.Length == 0) return null;

                var request = JsonSerializer.Deserialize<ShowRequest>(text);
                // Anything without a nonce cannot be answered, and would be answered forever.
                return request is null || string.IsNullOrEmpty(request.Nonce) ? null : request;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
        return null;
    }
}
