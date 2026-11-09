using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// The server owns the IDs of a written document. They are derived from where the thing
/// sits, so the same description yields the same document and a model cannot invent a
/// duplicate.
/// </summary>
public sealed class SpecIdTests
{
    [Fact]
    public void The_same_key_always_gives_the_same_id()
    {
        var key = SpecIds.KeyOf("Plant", "Line", "DrillingStation", "element");

        Assert.Equal(SpecIds.IdFor(key), SpecIds.IdFor(key));
    }

    [Fact]
    public void Different_keys_give_different_ids()
    {
        var one = SpecIds.IdFor(SpecIds.KeyOf("Plant", "Line", "element"));
        var other = SpecIds.IdFor(SpecIds.KeyOf("Plant", "Line", "interface"));
        var third = SpecIds.IdFor(SpecIds.KeyOf("Plant", "Line", "Drill", "element"));

        Assert.NotEqual(one, other);
        Assert.NotEqual(one, third);
        Assert.NotEqual(other, third);
    }

    [Fact]
    public void An_id_is_a_guid_in_braces()
    {
        var id = SpecIds.IdFor(SpecIds.KeyOf("Plant", "element"));

        Assert.StartsWith("{", id, StringComparison.Ordinal);
        Assert.EndsWith("}", id, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(AmlModel.NormId(id), out _), $"'{id}' is no GUID");
    }

    [Fact]
    public void A_key_is_built_from_the_path_and_the_kind()
    {
        Assert.Equal("Plant/Line/Drill|element", SpecIds.KeyOf("Plant", "Line", "Drill", "element"));
    }

    /// <summary>
    /// Two elements whose paths differ only in where the boundary sits must not collide, so
    /// the key keeps the segments apart instead of gluing them together.
    /// </summary>
    [Fact]
    public void Segments_that_run_together_do_not_collide()
    {
        var one = SpecIds.IdFor(SpecIds.KeyOf("Plant", "LineDrill", "element"));
        var other = SpecIds.IdFor(SpecIds.KeyOf("Plant", "Line", "Drill", "element"));

        Assert.NotEqual(one, other);
    }
}
