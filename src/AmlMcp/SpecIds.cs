using System.Security.Cryptography;
using System.Text;

namespace AmlMcp;

/// <summary>
/// The IDs of a written document, derived from where each thing sits in it. Two runs over
/// the same description produce the same document, and nothing can hand in an ID of its
/// own: that is how a duplicate ID becomes impossible rather than merely unlikely.
/// </summary>
public static class SpecIds
{
    /// <summary>
    /// The key of a thing: its path through the document and what kind of thing it is. The
    /// segments are kept apart, so Plant/LineDrill and Plant/Line/Drill are not one key.
    /// </summary>
    public static string KeyOf(params string[] parts) =>
        parts.Length < 2
            ? string.Join("/", parts)
            : string.Join("/", parts[..^1]) + "|" + parts[^1];

    /// <summary>A stable ID for a key, in the braced form the reader expects.</summary>
    public static string IdFor(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var bytes = hash[..16];
        // Make it a well-formed UUID: version 8 (custom) and the RFC variant.
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes).ToString("B");
    }
}
