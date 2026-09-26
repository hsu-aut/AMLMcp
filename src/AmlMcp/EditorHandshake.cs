using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmlMcp;

/// <summary>What the server asks an editor to show.</summary>
public sealed record ShowRequest(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("path")] string Path);

/// <summary>What the editor answers: "selected", or why it could not.</summary>
public sealed record ShowAck(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string? Detail = null);

/// <summary>
/// The exchange between the server and an editor plugin, over two files: the server writes a
/// request, the editor writes an acknowledgement. Without one the server does not claim that
/// anything was shown, because nobody may be listening at all.
/// </summary>
public static class EditorHandshake
{
    /// <summary>How long to wait for the editor. Shortened by the tests.</summary>
    internal static TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(4);

    public static string AckFileFor(string requestFile) => requestFile + ".ack";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ShowRequest Write(string requestFile, string document, string id, string path)
    {
        var request = new ShowRequest(Guid.NewGuid().ToString("N"), document, id, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(requestFile)!);
        // An answer to an older request must not be mistaken for an answer to this one.
        Delete(AckFileFor(requestFile));

        // Written beside the file and then moved into place: an editor reading it never sees
        // half a request, and a reader holding the old file does not fail the write.
        var temporary = requestFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(request, Json));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, requestFile, overwrite: true);
                return request;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }

    /// <summary>Waits for the editor's answer to this request, or null when none comes.</summary>
    public static ShowAck? WaitForAck(string requestFile, string nonce)
    {
        var ackFile = AckFileFor(requestFile);
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (Read(ackFile) is { } ack && ack.Nonce == nonce) return ack;
            Thread.Sleep(50);
        }
        return null;
    }

    private static ShowAck? Read(string ackFile)
    {
        try
        {
            if (!File.Exists(ackFile)) return null;
            using var stream = new FileStream(ackFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Length == 0 ? null : JsonSerializer.Deserialize<ShowAck>(text);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Delete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the nonce still tells them apart */ }
    }
}
