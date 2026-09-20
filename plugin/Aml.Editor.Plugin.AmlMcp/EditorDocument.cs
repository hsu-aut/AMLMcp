// The editor tells a plugin about the open file through ChangeAMLFilePath, but only when
// it changes. A panel that is installed or opened while a document is already loaded never
// hears about it, so this asks the editor's own view model. Brittle by nature: every
// failure just means the user types the directory, which the panel allows anyway.
using System.IO;
using System.Reflection;
using System.Windows;

namespace Aml.Editor.Plugin.AmlMcp;

internal static class EditorDocument
{
    public static string? CurrentPath()
    {
        try
        {
            var context = Application.Current?.MainWindow?.DataContext;
            return context is null ? null : FindPath(context, depth: 0, seen: new HashSet<object>(ReferenceEqualityComparer.Instance));
        }
        catch
        {
            return null;
        }
    }

    private static string? FindPath(object node, int depth, HashSet<object> seen)
    {
        if (depth > 2 || !seen.Add(node)) return null;

        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;

            object? value;
            try { value = property.GetValue(node); }
            catch { continue; }

            switch (value)
            {
                case string text when LooksLikeDocument(text):
                    return text;
                case string:
                case null:
                case ValueType:
                    continue;
                default:
                    if (value.GetType().Namespace?.StartsWith("Aml.", StringComparison.Ordinal) == true &&
                        FindPath(value, depth + 1, seen) is { } found)
                        return found;
                    continue;
            }
        }
        return null;
    }

    private static bool LooksLikeDocument(string text) =>
        (text.EndsWith(".aml", StringComparison.OrdinalIgnoreCase) ||
         text.EndsWith(".amlx", StringComparison.OrdinalIgnoreCase)) &&
        File.Exists(text);
}
