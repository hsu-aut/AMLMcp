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

    /// <summary>
    /// The message of a refused call. A tool answers a refusal instead of throwing, so that
    /// the server does not log an ordinary "no such element" as an unhandled exception.
    /// </summary>
    public static string Error(CallToolResult result)
    {
        Assert.True(result.IsError, "the call was expected to be refused");
        return Text(result);
    }

    /// <summary>The values of one property over an array, e.g. every "path" of a hit list.</summary>
    public static List<string?> Strings(JsonElement array, string property) =>
        array.EnumerateArray().Select(e => e.TryGetProperty(property, out var p) ? p.GetString() : null).ToList();
}
