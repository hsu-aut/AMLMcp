// The one place that talks to the AutomationML Editor. Behind an interface so the file
// handshake around it can be tested without an editor.
using Aml.Editor.API;

namespace Aml.Editor.Plugin.AmlMcp;

internal interface IEditorSelection
{
    /// <summary>The document the editor shows, or null when there is none.</summary>
    string? CurrentDocument { get; }

    /// <summary>True when the editor is there to be asked at all.</summary>
    bool Available { get; }

    /// <summary>Expands and selects the element with this ID.</summary>
    void Select(string id);
}

internal sealed class AmlEditorSelection : IEditorSelection
{
    public string? CurrentDocument
    {
        get
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
    }

    public bool Available
    {
        get
        {
            try
            {
                return AMLEditor.AMLApplication is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Select(string id)
    {
        var editor = AMLEditor.AMLApplication ?? throw new InvalidOperationException("the editor API is not available in this session");
        editor.ExpandObjectById(id);
        editor.SelectObjectById(id);
    }
}
