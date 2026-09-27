using System.IO;
using System.Text.Json;
using Aml.Editor.Plugin.AmlMcp;
using Xunit;

namespace Aml.Editor.Plugin.AmlMcp.Tests;

/// <summary>An editor that records what it was asked to do, and can refuse like the real one.</summary>
internal sealed class FakeEditor : IEditorSelection
{
    public string? CurrentDocument { get; set; }
    public bool Available { get; set; } = true;
    public Exception? Throws { get; set; }
    public List<string> Selected { get; } = new();

    public void Select(string id)
    {
        if (Throws is not null) throw Throws;
        Selected.Add(id);
    }
}

public sealed class EditorLinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "amlmcp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEditor _editor = new();
    private readonly List<string> _reported = new();
    private readonly EditorLink _link;
    private readonly string _requestFile;

    public EditorLinkTests()
    {
        Directory.CreateDirectory(_directory);
        _requestFile = Path.Combine(_directory, "show-in-editor.json");
        // Straight through instead of onto a dispatcher, so a test sees the result at once.
        _link = new EditorLink(_editor, action => action(), _reported.Add, _requestFile, watch: false);
    }

    public void Dispose()
    {
        _link.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Request(string id, string document, string? nonce = null, string path = "Plant/Station")
    {
        nonce ??= Guid.NewGuid().ToString("N");
        File.WriteAllText(_requestFile, JsonSerializer.Serialize(new { nonce, document, id, path }));
        return nonce;
    }

    private ShowAck? Ack()
    {
        var file = _link.AckFile;
        return File.Exists(file) ? JsonSerializer.Deserialize<ShowAck>(File.ReadAllText(file)) : null;
    }

    [Fact]
    public void A_request_for_the_open_document_selects_and_confirms()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");
        var nonce = Request("{the-id}", _editor.CurrentDocument);

        _link.Poll();

        Assert.Equal(new[] { "{the-id}" }, _editor.Selected);
        var ack = Ack();
        Assert.Equal(nonce, ack!.Nonce);
        Assert.Equal("selected", ack.Status);
        Assert.Contains("Plant/Station", Assert.Single(_reported));
    }

    [Fact]
    public void The_same_request_is_answered_once_however_often_it_is_seen()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");
        Request("{the-id}", _editor.CurrentDocument, nonce: "same-nonce");

        _link.Poll();
        _link.Poll();
        _link.Poll();

        Assert.Single(_editor.Selected);
    }

    [Fact]
    public void The_same_element_can_be_shown_again_under_a_new_nonce()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");

        Request("{the-id}", _editor.CurrentDocument);
        _link.Poll();
        Request("{the-id}", _editor.CurrentDocument);
        _link.Poll();

        Assert.Equal(2, _editor.Selected.Count);
    }

    [Fact]
    public void An_element_of_another_document_is_refused_and_named()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "open.aml");
        Request("{the-id}", Path.Combine(_directory, "other.aml"));

        _link.Poll();

        Assert.Empty(_editor.Selected);
        var ack = Ack();
        Assert.Equal("mismatch", ack!.Status);
        Assert.Contains("open.aml", ack.Detail);
        Assert.Contains("other.aml", ack.Detail);
    }

    [Fact]
    public void The_same_file_through_a_different_spelling_still_counts_as_the_open_one()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");
        Request("{the-id}", Path.Combine(_directory, "sub", "..", "PLANT.aml"));

        _link.Poll();

        Assert.Single(_editor.Selected);
        Assert.Equal("selected", Ack()!.Status);
    }

    [Fact]
    public void Without_an_editor_the_assistant_is_told_instead_of_being_left_waiting()
    {
        _editor.Available = false;
        Request("{the-id}", Path.Combine(_directory, "plant.aml"));

        _link.Poll();

        Assert.Empty(_editor.Selected);
        Assert.Equal("unavailable", Ack()!.Status);
    }

    [Fact]
    public void A_failing_selection_is_reported_with_its_reason()
    {
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");
        _editor.Throws = new InvalidOperationException("no such element in the tree");
        Request("{the-id}", _editor.CurrentDocument);

        _link.Poll();

        var ack = Ack();
        Assert.Equal("failed", ack!.Status);
        Assert.Contains("no such element", ack.Detail);
        Assert.Contains("no such element", Assert.Single(_reported));
    }

    [Fact]
    public void A_bare_id_is_not_a_request_and_is_left_alone()
    {
        // CAEX IDs are written in braces, so a bare one looks like JSON. It carries no nonce,
        // cannot be answered, and must not be selected over and over.
        _editor.CurrentDocument = Path.Combine(_directory, "plant.aml");
        File.WriteAllText(_requestFile, "{D51CBBD7-BF5F-4063-A1E1-F2FABB298F16}");

        _link.Poll();
        _link.Poll();

        Assert.Empty(_editor.Selected);
        Assert.Null(Ack());
    }

    [Fact]
    public void Rubbish_in_the_file_is_ignored_rather_than_thrown()
    {
        File.WriteAllText(_requestFile, "{ this is not json");

        _link.Poll();

        Assert.Empty(_editor.Selected);
        Assert.Null(Ack());
    }

    [Fact]
    public void Nothing_happens_while_there_is_no_request()
    {
        _link.Poll();

        Assert.Empty(_editor.Selected);
        Assert.Null(Ack());
        Assert.Empty(_reported);
    }
}
