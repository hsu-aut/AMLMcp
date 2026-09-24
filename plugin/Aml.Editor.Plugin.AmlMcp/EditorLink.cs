// The other direction: the assistant names an element, the server writes its ID into a
// file, and this watches that file and makes the editor expand and select the element.
using System.IO;
using System.Windows.Threading;
using Aml.Editor.API;

namespace Aml.Editor.Plugin.AmlMcp;

internal sealed class EditorLink : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _report;
    private readonly FileSystemWatcher? _watcher;
    private string? _last;

    public EditorLink(Dispatcher dispatcher, Action<string> report)
    {
        _dispatcher = dispatcher;
        _report = report;

        var directory = Path.GetDirectoryName(McpProbe.SelectFile);
        if (string.IsNullOrEmpty(directory)) return;
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

    public void Dispose() => _watcher?.Dispose();

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
        // A writer may still hold the file, and one write can raise several events.
        var id = Read();
        if (id is null || id == _last) return;
        _last = id;

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var editor = AMLEditor.AMLApplication;
                if (editor is null) { _report($"the assistant pointed at {id}, but the editor API is not available"); return; }
                editor.ExpandObjectById(id);
                editor.SelectObjectById(id);
                _report($"the assistant pointed at {id}");
            }
            catch (Exception ex)
            {
                _report($"could not select {id}: {ex.Message}");
            }
        }));
    }

    private string? Read()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(McpProbe.SelectFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd().Trim();
                return text.Length == 0 ? null : text;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
        }
        return null;
    }
}
