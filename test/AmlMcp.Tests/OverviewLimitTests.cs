using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// Every list the server hands out is bounded and says how much it left out. The overview
/// counted the elements of a hierarchy by class and listed every class it found: a document
/// with a thousand classes in one hierarchy would have put a thousand entries into an answer
/// whose whole purpose is to fit.
/// </summary>
public sealed class OverviewLimitTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    private static string DocumentWith(int classes)
    {
        var library = string.Join("\n    ", Enumerable.Range(1, classes).Select(i => $"<SystemUnitClass Name=\"C{i}\" />"));
        var elements = string.Join("\n    ", Enumerable.Range(1, classes).Select(i =>
            $"<InternalElement Name=\"E{i}\" ID=\"{{aaaa0000-0000-0000-0000-{i:D12}}}\" RefBaseSystemUnitPath=\"Lib/C{i}\" />"));
        return $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="many.aml" xmlns="http://www.dke.de/CAEX">
              <InstanceHierarchy Name="Plant" ID="{aaaa0000-0000-0000-0000-0000000000f1}">
                {{elements}}
              </InstanceHierarchy>
              <SystemUnitClassLib Name="Lib">
                {{library}}
              </SystemUnitClassLib>
            </CAEXFile>
            """;
    }

    [Fact]
    public void The_overview_lists_at_most_six_classes_per_hierarchy_and_counts_the_rest()
    {
        var result = AmlTools.OpenAmlDocument(_store, _ws.Write("many.aml", DocumentWith(20)));

        var hierarchy = Answer.Data(result).GetProperty("hierarchies").EnumerateArray().First();

        Assert.Equal(6, hierarchy.GetProperty("byClass").GetArrayLength());
        Assert.Equal(20, hierarchy.GetProperty("distinctClasses").GetInt32());
    }

    /// <summary>Under the limit nothing is left out, and the count still says how many there are.</summary>
    [Fact]
    public void A_small_document_loses_nothing()
    {
        var result = AmlTools.OpenAmlDocument(_store, _ws.Write("few.aml", DocumentWith(3)));

        var hierarchy = Answer.Data(result).GetProperty("hierarchies").EnumerateArray().First();

        Assert.Equal(3, hierarchy.GetProperty("byClass").GetArrayLength());
        Assert.Equal(3, hierarchy.GetProperty("distinctClasses").GetInt32());
    }
}
