using System.IO.Compression;
using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>Builds AMLX packages (Open Packaging Conventions ZIP files) for tests.</summary>
internal static class Amlx
{
    public const string RootDocument = "http://schemas.automationml.org/container/relationship/RootDocument";
    public const string Library = "http://schemas.automationml.org/container/relationship/Library";
    public const string AnyContent = "http://schemas.automationml.org/container/relationship/AnyContent";

    public static string Build(Workspace ws, string fileName, IDictionary<string, string> parts,
                               params (string Target, string Type)[] relationships)
    {
        var path = Path.Combine(ws.Dir, fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        foreach (var (name, content) in parts)
            Write(zip, name, content);

        if (relationships.Length > 0)
        {
            var rels = string.Join("", relationships.Select((r, i) =>
                $"<Relationship Id=\"R{i}\" Type=\"{r.Type}\" Target=\"{r.Target}\" />"));
            Write(zip, "_rels/.rels",
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{rels}</Relationships>");
        }

        Write(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"aml\" ContentType=\"model/vnd.automationml+xml\" /></Types>");
        return path;
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}

public sealed class ContainerTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    /// <summary>Root document in a subfolder, library in a sibling folder, one geometry file.</summary>
    private string BuildTypicalContainer(string fileName = "plant.amlx") => Amlx.Build(_ws, fileName,
        new Dictionary<string, string>
        {
            ["model/user.aml"] = Fixtures.UsingLibrary("../lib/MotorLib.aml"),
            ["lib/MotorLib.aml"] = Fixtures.ExternalLibrary,
            ["geometry/fan.dae"] = "<COLLADA />",
        },
        ("/model/user.aml", Amlx.RootDocument),
        ("/lib/MotorLib.aml", Amlx.Library),
        ("/geometry/fan.dae", Amlx.AnyContent));

    [Fact]
    public void Opens_a_container_through_its_root_document_relationship()
    {
        var overview = AmlTools.OpenDocumentText(_store, BuildTypicalContainer());

        Assert.Contains("AMLX container, root document model/user.aml", overview);
        Assert.Contains("Plant: 1 elements", overview);
    }

    [Fact]
    public void Resolves_a_library_inside_the_container_relative_to_the_root_document()
    {
        AmlTools.OpenDocumentText(_store, BuildTypicalContainer());

        var fan = AmlTools.ElementCard(_store, "Plant/Fan");
        var check = AmlTools.CheckText(_store);

        Assert.Contains("class: Motors@MotorLib/Motor", fan);
        Assert.DoesNotContain("NOT resolved", fan);
        Assert.Contains("RatedPower = 3 kW", fan);
        Assert.Contains("no problems found", check);
        Assert.DoesNotContain("not inside the container", check);
    }

    [Fact]
    public void Lists_the_container_parts_with_their_roles()
    {
        var overview = AmlTools.OpenDocumentText(_store, BuildTypicalContainer());

        Assert.Contains("model/user.aml  (RootDocument", overview);
        Assert.Contains("lib/MotorLib.aml  (Library", overview);
        Assert.Contains("geometry/fan.dae  (AnyContent", overview);
        Assert.Contains("1 part(s) are not AutomationML", overview);
        Assert.Contains("[loaded from the container]", overview);
        Assert.DoesNotContain("_rels", overview);
        Assert.DoesNotContain("[Content_Types].xml", overview);
    }

    [Fact]
    public void Without_relationships_a_single_aml_document_is_the_root()
    {
        var path = Amlx.Build(_ws, "bare.amlx", new Dictionary<string, string>
        {
            ["plant215.aml"] = Fixtures.Caex215,
        });

        var overview = AmlTools.OpenDocumentText(_store, path);

        Assert.Contains("root document plant215.aml", overview);
        Assert.Contains("CAEX 2.15", overview);
    }

    [Fact]
    public void Without_relationships_several_aml_documents_are_rejected_with_the_candidates()
    {
        var path = Amlx.Build(_ws, "unclear.amlx", new Dictionary<string, string>
        {
            ["a/one.aml"] = Fixtures.Caex215,
            ["b/two.aml"] = Fixtures.Caex215,
        });

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));

        Assert.Contains("cannot tell which one is the root", ex.Message);
        Assert.Contains("a/one.aml", ex.Message);
        Assert.Contains("b/two.aml", ex.Message);
    }

    [Fact]
    public void A_library_missing_in_the_container_is_taken_from_disk_with_a_note()
    {
        _ws.Write("MotorLib.aml", Fixtures.ExternalLibrary);
        var path = Amlx.Build(_ws, "leaky.amlx", new Dictionary<string, string>
        {
            ["user.aml"] = Fixtures.UsingLibrary("MotorLib.aml"),
        }, ("/user.aml", Amlx.RootDocument));

        var overview = AmlTools.OpenDocumentText(_store, path);
        var check = AmlTools.CheckText(_store);

        Assert.Contains("[loaded from disk, NOT from the container]", overview);
        Assert.Contains("is not inside the container and was loaded from disk", check);
        Assert.Contains("no problems found", check);
    }

    [Fact]
    public void A_library_missing_everywhere_is_reported()
    {
        var path = Amlx.Build(_ws, "broken.amlx", new Dictionary<string, string>
        {
            ["user.aml"] = Fixtures.UsingLibrary("MotorLib.aml"),
        }, ("/user.aml", Amlx.RootDocument));

        AmlTools.OpenDocumentText(_store, path);

        Assert.Contains("not in the container and not next to it on disk", AmlTools.CheckText(_store));
    }

    [Fact]
    public void Percent_encoded_references_resolve_to_part_names_with_spaces()
    {
        var path = Amlx.Build(_ws, "spaces.amlx", new Dictionary<string, string>
        {
            ["user.aml"] = Fixtures.UsingLibrary("lib/Motor%20Lib.aml"),
            ["lib/Motor Lib.aml"] = Fixtures.ExternalLibrary,
        }, ("/user.aml", Amlx.RootDocument));

        AmlTools.OpenDocumentText(_store, path);

        Assert.Contains("no problems found", AmlTools.CheckText(_store));
    }

    [Fact]
    public void A_container_with_the_wrong_extension_is_recognised()
    {
        var overview = AmlTools.OpenDocumentText(_store, BuildTypicalContainer("misnamed.aml"));

        Assert.Contains("AMLX container", overview);
    }

    [Fact]
    public void A_corrupt_container_gives_a_clear_message()
    {
        var path = _ws.Write("corrupt.amlx", "PK this is not a real zip archive");

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));

        Assert.Contains("AMLX container", ex.Message);
        Assert.Contains("cannot be read", ex.Message);
    }

    /// <summary>
    /// Mirrors the layout Aml.Engine's AutomationMLContainer writes (checked against a
    /// container built with that API): the library, geometry and logic relationships
    /// sit in a relationship file next to the root document, not in _rels/.rels, and
    /// the parts start with a byte order mark.
    /// </summary>
    [Fact]
    public void Reads_relationships_written_the_way_Aml_Engine_writes_them()
    {
        const string bom = "﻿";
        const string ns = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string type = "http://schemas.automationml.org/container/relationship/";
        var path = Amlx.Build(_ws, "engine.amlx", new Dictionary<string, string>
        {
            ["model/user.aml"] = bom + Fixtures.UsingLibrary("../lib/MotorLib.aml").TrimStart(),
            ["lib/MotorLib.aml"] = bom + Fixtures.ExternalLibrary.TrimStart(),
            ["geometry/fan.dae"] = "<COLLADA />",
            ["logic/logic.xml"] = "<project />",
            ["_rels/.rels"] = bom + $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"{ns}\">" +
                              $"<Relationship Type=\"{type}RootDocument\" Target=\"/model/user.aml\" Id=\"R1\" />" +
                              $"<Relationship Type=\"{type}PLCopenXml\" Target=\"/logic/logic.xml\" Id=\"R2\" /></Relationships>",
            ["model/_rels/user.aml.rels"] = bom + $"<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"{ns}\">" +
                                            $"<Relationship Type=\"{type}Library\" Target=\"/lib/MotorLib.aml\" Id=\"R3\" />" +
                                            $"<Relationship Type=\"{type}Collada\" Target=\"/geometry/fan.dae\" Id=\"R4\" />" +
                                            $"<Relationship Type=\"{type}PLCopenXml\" Target=\"/logic/logic.xml\" Id=\"R5\" /></Relationships>",
        });

        var overview = AmlTools.OpenDocumentText(_store, path);

        Assert.Contains("root document model/user.aml", overview);
        Assert.Contains("lib/MotorLib.aml  (Library", overview);
        Assert.Contains("geometry/fan.dae  (Collada", overview);
        Assert.Contains("logic/logic.xml  (PLCopenXml", overview);
        Assert.Contains("[loaded from the container]", overview);
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
    }

    /// <summary>
    /// Packing a document changes where it is read from and nothing else. The same
    /// CAEX text as a loose file and as the root document of a container must give
    /// the same answers, word for word; only the file name may differ.
    /// </summary>
    [Fact]
    public void A_container_answers_exactly_like_the_same_document_as_a_loose_file()
    {
        foreach (var (name, content) in new[] { ("rich", Fixtures.Rich30), ("tricky", Fixtures.Tricky30) })
        {
            var loosePath = _ws.Write(name + ".aml", content);
            var packedPath = Amlx.Build(_ws, name + ".amlx",
                new Dictionary<string, string> { [name + ".aml"] = content }, ("/" + name + ".aml", Amlx.RootDocument));

            var loose = new DocumentStore();
            var packed = new DocumentStore();
            AmlTools.OpenDocumentText(loose, loosePath);
            AmlTools.OpenDocumentText(packed, packedPath);

            var model = loose.Get(null);
            var elements = model.Root.Descendants(AmlModel.C + "InternalElement")
                .Select(AmlModel.PathOf).Distinct().ToList();
            Assert.NotEmpty(elements);

            foreach (var element in elements)
            {
                Assert.Equal(AmlTools.ElementCard(loose, element), AmlTools.ElementCard(packed, element));
                Assert.Equal(AmlTools.ElementCard(loose, element, includeLayout: true),
                             AmlTools.ElementCard(packed, element, includeLayout: true));
                Assert.Equal(AmlTools.NeighborsText(loose, element, depth: 2, includeContainment: true),
                             AmlTools.NeighborsText(packed, element, depth: 2, includeContainment: true));
            }

            Assert.Equal(AmlTools.FindElementsText(loose), AmlTools.FindElementsText(packed));
            Assert.Equal(AmlTools.FindElementsText(loose, query: "Motor"), AmlTools.FindElementsText(packed, query: "Motor"));
            Assert.Equal(AmlTools.TreeText(loose), AmlTools.TreeText(packed));
            Assert.Equal(AmlTools.ListClassesText(loose), AmlTools.ListClassesText(packed));
            Assert.Equal(AmlTools.FindPathText(loose, elements[0], elements[^1]),
                         AmlTools.FindPathText(packed, elements[0], elements[^1]));

            // The check names the file it checked; everything below that first line must match.
            Assert.Equal(WithoutFirstLine(AmlTools.CheckText(loose)), WithoutFirstLine(AmlTools.CheckText(packed)));
            Assert.Contains($"Reference check for {name}.aml", AmlTools.CheckText(loose));
            Assert.Contains($"Reference check for {name}.amlx", AmlTools.CheckText(packed));
        }
    }

    private static string WithoutFirstLine(string text) => text[(text.IndexOf('\n') + 1)..];
}
