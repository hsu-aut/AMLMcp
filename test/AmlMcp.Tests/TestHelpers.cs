using System.Text.Json;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// Every tool answers twice: readable text for the model and structured data for
/// programs. These helpers take the two halves apart so a test can assert both.
/// </summary>
internal static class Answer
{
    public static string Text(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    public static JsonElement Data(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent!.Value;
    }

    /// <summary>The values of one property over an array, e.g. every "path" of a hit list.</summary>
    public static List<string?> Strings(JsonElement array, string property) =>
        array.EnumerateArray().Select(e => e.TryGetProperty(property, out var p) ? p.GetString() : null).ToList();
}
