using ModelContextProtocol;
using Xunit;

namespace AmlMcp.Tests;

/// <summary>Change 1: --root also fences in the libraries an ExternalReference points at.</summary>
public sealed class ExternalReferenceRootTests : IDisposable
{
    private readonly Workspace _inside = new();
    private readonly Workspace _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    private string WriteDocumentPointingOutside()
    {
        _outside.Write("MotorLib.aml", Fixtures.ExternalLibrary);
        var target = Path.Combine(_outside.Dir, "MotorLib.aml").Replace('\\', '/');
        return _inside.Write("user.aml", Fixtures.UsingLibrary(target));
    }

    [Fact]
    public void A_library_outside_the_roots_is_not_loaded()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        var overview = AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        Assert.Contains("NOT loaded: outside the directories this server may read", overview);
        Assert.Contains(_inside.Dir, overview);

        var model = store.Get(null);
        var reference = Assert.Single(model.ExternalRefs);
        Assert.False(reference.Loaded);
        Assert.Null(reference.ResolvedFile);
        Assert.Contains("outside the directories this server may read", reference.Problem);
        Assert.DoesNotContain(model.Classes.Keys, k => k.Contains("MotorLib/Motor", StringComparison.Ordinal));
    }

    [Fact]
    public void The_check_says_the_library_was_not_loaded_and_why()
    {
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir }));
        AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        var check = AmlTools.CheckText(store);
        var data = Answer.Data(AmlTools.CheckReferences(store));

        Assert.Contains("external library not loaded: Motors ->", check);
        Assert.Contains("outside the directories this server may read", check);
        Assert.Contains("consequence: 1 class path(s)", check);
        Assert.Contains(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "externalLibraryNotLoaded"
                 && p.GetProperty("message").GetString()!.Contains("outside the directories this server may read"));

        // The class the document builds on is gone with the library, and the card says so.
        var fan = AmlTools.ElementCard(store, "Plant/Fan");
        Assert.Contains("class: Motors@MotorLib/Motor  (NOT resolved)", fan);
        Assert.DoesNotContain("A motor from an external library", fan);
    }

    [Fact]
    public void Without_a_root_restriction_the_same_document_loads_the_library()
    {
        var store = new DocumentStore();
        var overview = AmlTools.OpenDocumentText(store, WriteDocumentPointingOutside());

        Assert.Contains("[loaded]", overview);
        Assert.Contains("no problems found", AmlTools.CheckText(store));
        Assert.Contains("class meaning: A motor from an external library.", AmlTools.ElementCard(store, "Plant/Fan"));
    }
}

/// <summary>Change 2: the command line is checked, a typo does not silently widen the fence.</summary>
public sealed class CommandLineTests
{
    [Fact]
    public void An_unknown_argument_is_refused_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--roots", "C:\\models" }));

        Assert.Contains("Unknown argument '--roots'", ex.Message);
        Assert.Contains("--help", ex.Message);
    }

    [Fact]
    public void A_stray_argument_is_refused_as_well()
    {
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--strict-conventions", "surprise" }));
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "-r", "." }));
    }

    [Fact]
    public void Root_without_a_directory_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--root" }));

        Assert.Equal("--root needs a directory.", ex.Message);
    }

    [Fact]
    public void Help_and_version_are_accepted()
    {
        foreach (var argument in new[] { "--help", "-h", "--version" })
        {
            var options = ServerOptions.Parse(new[] { argument });

            Assert.Empty(options.Roots);
            Assert.False(options.StrictConventions);
        }

        var withRoot = ServerOptions.Parse(new[] { "--help", "--root", ".", "--strict-conventions" });
        Assert.Single(withRoot.Roots);
        Assert.True(withRoot.StrictConventions);
    }
}

/// <summary>Changes 3 and 4: every refusal names the file or the element and says what to do.</summary>
public sealed class ClearRefusalTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void An_empty_path_asks_for_a_path()
    {
        foreach (var path in new[] { "", "   ", "\t" })
        {
            var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));
            Assert.Contains("A file path is required", ex.Message);
        }

        // A path a client wrapped in quotes is still a path.
        var real = _ws.Write("quoted.aml", Fixtures.Caex215);
        Assert.Contains("CAEX 2.15", AmlTools.OpenDocumentText(_store, $"\"{real}\""));
    }

    [Fact]
    public void A_directory_is_refused_with_advice()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, _ws.Dir));

        Assert.Contains("is a directory. Name an .aml or .amlx file inside it.", ex.Message);
        Assert.Contains(_ws.Dir, ex.Message);
    }

    [Fact]
    public void A_file_that_vanished_after_being_opened_is_not_answered_from_memory()
    {
        var path = _ws.Write("gone.aml", Fixtures.Caex215);
        AmlTools.OpenDocumentText(_store, path);
        File.Delete(path);

        var ex = Assert.Throws<McpException>(() => AmlTools.CheckText(_store));

        Assert.Contains("was open, but the file is gone now.", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Broken_XML_is_blamed_on_the_document_not_on_the_server()
    {
        var path = _ws.Write("broken.aml", "<CAEXFile SchemaVersion=\"3.0\" xmlns=\"http://www.dke.de/CAEX\"><InstanceHierarchy Name=\"x\">");

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenDocumentText(_store, path));

        Assert.Contains($"Could not read '{path}' as AutomationML:", ex.Message);
        Assert.DoesNotContain("Internal error", ex.Message);
    }

    [Fact]
    public void An_empty_element_reference_explains_how_to_name_one()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("rich.aml", Fixtures.Rich30));
        var model = _store.Get(null);

        foreach (var query in new string?[] { null, "", "   " })
        {
            var ex = Assert.Throws<McpException>(() => model.ResolveElement(query));
            Assert.Contains("Name an element", ex.Message);
            Assert.Contains("find_elements", ex.Message);
        }

        Assert.Contains("Name an element", Assert.Throws<McpException>(() => AmlTools.ElementCard(_store, "")).Message);
    }
}

/// <summary>Change 5: an alias whose library is missing does not borrow a foreign class.</summary>
public sealed class AliasFallbackTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    // The document has a library of its own whose plain path is the one the missing
    // external library would answer with. Answering with it would be a wrong answer.
    private const string SameNameLocally = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="user.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="DoesNotExist.aml" Alias="Motors" />
          <SystemUnitClassLib Name="MotorLib">
            <SystemUnitClass Name="Motor">
              <Description>A local motor that has nothing to do with the external one.</Description>
              <Attribute Name="RatedPower" AttributeDataType="xs:double" Unit="kW"><DefaultValue>99</DefaultValue></Attribute>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{66666666-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{66666666-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Motors@MotorLib/Motor" />
            <InternalElement Name="Local" ID="{66666666-0000-0000-0000-000000000002}" RefBaseSystemUnitPath="MotorLib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasFallbackTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", SameNameLocally));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_class_behind_a_missing_alias_does_not_fall_back_to_a_local_class_of_the_same_path()
    {
        var fan = AmlTools.ElementCard(_store, "Plant/Fan");

        Assert.Contains("class: Motors@MotorLib/Motor  (NOT resolved)", fan);
        Assert.DoesNotContain("A local motor", fan);
        Assert.DoesNotContain("RatedPower = 99", fan);
        Assert.Null(_store.Get(null).ResolveClass("Motors@MotorLib/Motor"));

        var data = Answer.Data(AmlTools.GetElement(_store, "Plant/Fan"));
        Assert.False(data.GetProperty("classResolved").GetBoolean());
        Assert.False(data.TryGetProperty("classDescription", out _));
    }

    [Fact]
    public void The_local_class_of_the_same_path_still_answers_for_itself()
    {
        var local = AmlTools.ElementCard(_store, "Plant/Local");

        Assert.Contains("class: MotorLib/Motor", local);
        Assert.DoesNotContain("(NOT resolved)", local);
        Assert.Contains("A local motor", local);
        Assert.Contains("RatedPower = 99 kW  (from MotorLib/Motor)", local);
    }

    [Fact]
    public void The_check_blames_the_missing_library_once()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("external library not loaded: Motors -> DoesNotExist.aml", check);
        Assert.Contains("consequence: 1 class path(s) used 1x cannot be resolved, e.g. Motors@MotorLib/Motor", check);
        Assert.Contains("1 problem(s)", check);
    }
}

/// <summary>Change 6, second half: the interface name of a 2.15 link is matched without regard to case.</summary>
public sealed class LegacyLinkCaseTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_legacy_link_resolves_although_owner_and_interface_are_spelled_differently()
    {
        var mixedCase = Fixtures.Caex215
            .Replace("RefPartnerSideA=\"Plant/Tank:Drain\"", "RefPartnerSideA=\"plant/tank:drain\"")
            .Replace("RefPartnerSideB=\"Plant/Valve:In\"", "RefPartnerSideB=\"PLANT/VALVE:IN\"");
        AmlTools.OpenDocumentText(_store, _ws.Write("plant215.aml", mixedCase));

        var check = AmlTools.CheckText(_store);

        Assert.Contains("no problems found", check);
        Assert.Contains("--link DrainPipe", AmlTools.ElementCard(_store, "Plant/Valve"));
        Assert.Contains("Plant/Tank", AmlTools.ElementCard(_store, "Plant/Valve"));
    }
}

/// <summary>Change 12: a document that references itself is read once.</summary>
public sealed class SelfReferenceTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string ReferencesItself = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="self.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="self.aml" Alias="Self" />
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Motor">
              <Description>The one and only motor class.</Description>
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{77770000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{77770000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Self@Lib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public SelfReferenceTests()
    {
        AmlTools.OpenDocumentText(_store, _ws.Write("self.aml", ReferencesItself));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_document_is_parsed_once_and_both_spellings_mean_the_same_class()
    {
        var model = _store.Get(null);

        Assert.True(model.Classes.ContainsKey("Lib/Motor"));
        Assert.True(model.Classes.ContainsKey("Self@Lib/Motor"));
        // A second parse would produce a second, equal-looking XML tree.
        Assert.Same(model.Classes["Lib/Motor"].Element, model.Classes["Self@Lib/Motor"].Element);
        Assert.Same(model.Root.Document, model.Classes["Self@Lib/Motor"].Element.Document);
    }

    [Fact]
    public void The_self_reference_is_reported_as_loaded_and_costs_no_problem()
    {
        Assert.Contains("Self -> self.aml  [loaded]", AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "self.aml")));
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
        Assert.Contains("class: Self@Lib/Motor", AmlTools.ElementCard(_store, "Plant/Fan"));
        Assert.DoesNotContain("NOT resolved", AmlTools.ElementCard(_store, "Plant/Fan"));
    }
}

/// <summary>Change 13: a library missing deeper in the chain is reported, not swallowed.</summary>
public sealed class NestedLibraryTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string MiddleLibrary = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="MiddleLib.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="BottomLib.aml" Alias="Bottom" />
          <SystemUnitClassLib Name="MiddleLib">
            <SystemUnitClass Name="Motor" RefBaseClassPath="Bottom@BottomLib/Machine">
              <Description>A motor from the middle library.</Description>
            </SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    private const string TopDocument = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="top.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="MiddleLib.aml" Alias="Middle" />
          <InstanceHierarchy Name="Plant" ID="{88880000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fan" ID="{88880000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Middle@MiddleLib/Motor" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public NestedLibraryTests()
    {
        _ws.Write("MiddleLib.aml", MiddleLibrary);
        AmlTools.OpenDocumentText(_store, _ws.Write("top.aml", TopDocument));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_library_one_level_down_is_loaded_and_the_one_below_it_is_reported_missing()
    {
        var overview = AmlTools.OpenDocumentText(_store, Path.Combine(_ws.Dir, "top.aml"));
        var check = AmlTools.CheckText(_store);

        // The overview lists what this document declares; the alias Bottom belongs to
        // MiddleLib, not to top.aml, so it is reported by the check instead.
        Assert.Contains("Middle -> MiddleLib.aml  [loaded]", overview);
        Assert.DoesNotContain("Bottom ->", overview);
        Assert.Contains("library of an external library not loaded: Bottom -> BottomLib.aml", check);

        var m = _store.Get(null);
        Assert.True(Assert.Single(m.ExternalRefs).Loaded);
        Assert.Equal("Middle", m.ExternalRefs[0].Alias);
        Assert.False(Assert.Single(m.NestedLibraryProblems).Loaded);
        Assert.Equal("Bottom", m.NestedLibraryProblems[0].Alias);
    }

    [Fact]
    public void The_class_that_was_found_still_answers_and_its_missing_base_class_is_named()
    {
        var fan = AmlTools.ElementCard(_store, "Plant/Fan");
        var card = AmlTools.ClassCard(_store, "Middle@MiddleLib/Motor");

        Assert.Contains("class: Middle@MiddleLib/Motor", fan);
        Assert.Contains("class meaning: A motor from the middle library.", fan);
        Assert.Contains("Bottom@BottomLib/Machine (NOT resolved)", card);
    }
}

/// <summary>
/// A CAEX alias belongs to the document that declares it. A failed reference inside an
/// external library must not make an unrelated class path of the main document unresolvable.
/// </summary>
public sealed class AliasScopeTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string LibraryWithMissingReference = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="LibA.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="Missing.aml" Alias="Gone" />
          <SystemUnitClassLib Name="LibA">
            <SystemUnitClass Name="Pump" />
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    // The document uses the alias Gone for a library of its own, and that library loads.
    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="top.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="LibA.aml" Alias="A" />
          <SystemUnitClassLib Name="Local">
            <SystemUnitClass Name="Thing"><Description>A class of this very document.</Description></SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{99990000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="E" ID="{99990000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Gone@Local/Thing" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasScopeTests()
    {
        _ws.Write("LibA.aml", LibraryWithMissingReference);
        AmlTools.OpenDocumentText(_store, _ws.Write("top.aml", Document));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_failed_reference_inside_a_library_does_not_block_a_class_of_the_main_document()
    {
        var card = AmlTools.ElementCard(_store, "Plant/E");

        Assert.Contains("class: Gone@Local/Thing", card);
        Assert.DoesNotContain("NOT resolved", card);
        Assert.Contains("class meaning: A class of this very document.", card);
    }

    [Fact]
    public void The_missing_library_is_still_reported_once_and_attributed_to_the_library()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("library of an external library not loaded: Gone -> Missing.aml", check);
        Assert.Single(check.Split("Gone -> Missing.aml").Skip(1));
    }
}

public sealed class RootArgumentTests
{
    [Fact]
    public void An_option_cannot_be_swallowed_as_the_directory_of_root()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--root", "--strict-conventions" }));

        Assert.Contains("--root needs a directory", ex.Message);
    }
}

/// <summary>CAEX 2.15 documents name base classes without their library.</summary>
public sealed class BareClassNameTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile FileName="bare.aml" SchemaVersion="2.15">
          <RoleClassLib Name="BaseRoles">
            <RoleClass Name="Base">
              <RoleClass Name="Structure" RefBaseClassPath="Base" />
            </RoleClass>
          </RoleClassLib>
          <RoleClassLib Name="OtherRoles">
            <RoleClass Name="Twin" />
          </RoleClassLib>
          <RoleClassLib Name="MoreRoles">
            <RoleClass Name="Twin" />
            <RoleClass Name="UsesTwin" RefBaseClassPath="Twin" />
          </RoleClassLib>
          <RoleClassLib Name="Orphans">
            <RoleClass Name="Lost" RefBaseClassPath="Nowhere" />
          </RoleClassLib>
        </CAEXFile>
        """;

    public BareClassNameTests() => AmlTools.OpenDocumentText(_store, _ws.Write("bare.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_unique_bare_name_resolves()
    {
        var chain = _store.Get(null).ClassChain("BaseRoles/Base/Structure").Select(c => c.Key).ToList();

        Assert.Equal(new[] { "BaseRoles/Base/Structure", "BaseRoles/Base" }, chain);
    }

    [Fact]
    public void A_shared_bare_name_resolves_within_the_library_of_the_class_that_uses_it()
    {
        var chain = _store.Get(null).ClassChain("MoreRoles/UsesTwin").Select(c => c.Key).ToList();

        Assert.Equal(new[] { "MoreRoles/UsesTwin", "MoreRoles/Twin" }, chain);
    }

    [Fact]
    public void A_shared_bare_name_without_context_is_not_guessed()
    {
        Assert.Null(_store.Get(null).ResolveClass("Twin"));
    }

    [Fact]
    public void An_unresolved_class_reference_names_the_class_that_makes_it()
    {
        var check = AmlTools.CheckText(_store);

        Assert.Contains("unresolved class path: Nowhere  (used 1x, e.g. by Orphans/Lost)", check);
        Assert.DoesNotContain("unresolved class path: Base ", check);
        Assert.DoesNotContain("unresolved class path: Twin", check);
    }
}

/// <summary>Library names with '/' in them are written in square brackets, as the OPC UA libraries do.</summary>
public sealed class BracketedClassPathTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="ua.aml" xmlns="http://www.dke.de/CAEX">
          <AttributeTypeLib Name="ATL_http://opcfoundation.org/UA/">
            <AttributeType Name="NodeId" AttributeDataType="xs:string" />
          </AttributeTypeLib>
          <SystemUnitClassLib Name="SUC_http://opcfoundation.org/UA/">
            <SystemUnitClass Name="FolderType"><Description>An OPC UA folder.</Description></SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{12340000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Objects" ID="{12340000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="[SUC_http://opcfoundation.org/UA/]/[FolderType]">
              <Attribute Name="NodeId" RefAttributeType="[ATL_http://opcfoundation.org/UA/]/[NodeId]"><Value>ns=0;i=85</Value></Attribute>
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public BracketedClassPathTests() => AmlTools.OpenDocumentText(_store, _ws.Write("ua.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Bracketed_segments_resolve()
    {
        var card = AmlTools.ElementCard(_store, "Plant/Objects");

        Assert.Contains("class meaning: An OPC UA folder.", card);
        Assert.DoesNotContain("NOT resolved", card);
        Assert.Contains("no problems found", AmlTools.CheckText(_store));
    }

    [Fact]
    public void An_alias_is_only_split_off_outside_the_brackets()
    {
        var (alias, plain, segments) = AmlModel.SplitClassPath("Lib@[ATL_http://user@host/UA/]/[NodeId]");

        Assert.Equal("Lib", alias);
        Assert.Equal("ATL_http://user@host/UA//NodeId", plain);
        Assert.Equal(2, segments);
    }
}

/// <summary>A library behind an http address is not downloaded; that is a note, not a defect.</summary>
public sealed class RemoteLibraryTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="remote.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="https://token123@example.org/share/RemoteLib.aml" Alias="Remote" />
          <InstanceHierarchy Name="Plant" ID="{56780000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Pump" ID="{56780000-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Remote@RemoteLib/Pump" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    private const string Library = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="RemoteLib.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="RemoteLib">
            <SystemUnitClass Name="Pump"><Description>A pump from the remote library.</Description></SystemUnitClass>
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Without_a_local_copy_it_is_a_note_and_the_token_is_not_shown()
    {
        var overview = AmlTools.OpenDocumentText(_store, _ws.Write("remote.aml", Document));
        var check = AmlTools.CheckText(_store);

        Assert.Contains("no problems found", check);
        Assert.Contains("note: remote library not downloaded: Remote -> https://example.org/share/RemoteLib.aml", check);
        Assert.Contains("1 class path(s) used 1x from it are not checked", check);
        Assert.DoesNotContain("token123", overview + check);
    }

    [Fact]
    public void A_local_copy_next_to_the_document_is_used()
    {
        _ws.Write("RemoteLib.aml", Library);
        AmlTools.OpenDocumentText(_store, _ws.Write("remote.aml", Document));

        Assert.Contains("class meaning: A pump from the remote library.", AmlTools.ElementCard(_store, "Plant/Pump"));
        Assert.Contains("was read from the local copy", AmlTools.CheckText(_store));
    }
}

/// <summary>With --follow the server answers about the document an editor currently shows.</summary>
public sealed class FollowTheEditorTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private DocumentStore StoreFollowing(string pointer, bool restricted = true)
    {
        var arguments = restricted
            ? new[] { "--root", _ws.Dir, "--follow", pointer }
            : new[] { "--follow", pointer };
        return new DocumentStore(ServerOptions.Parse(arguments));
    }

    [Fact]
    public void Without_an_open_document_the_pointed_at_file_is_used()
    {
        var plant = _ws.Write("plant.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", plant);
        var store = StoreFollowing(pointer);

        Assert.Contains("Plant/Pump", AmlTools.ElementCard(store, "Pump"));
    }

    [Fact]
    public void A_document_the_editor_switches_to_is_picked_up_without_a_restart()
    {
        var first = _ws.Write("first.aml", Fixtures.Caex215);
        var second = _ws.Write("second.aml", Fixtures.Rich30);
        var pointer = _ws.Write("open-document.txt", first);
        var store = StoreFollowing(pointer);

        Assert.Contains("Reference check for first.aml", AmlTools.CheckText(store));

        File.WriteAllText(pointer, second);

        Assert.Contains("Reference check for second.aml", AmlTools.CheckText(store));
    }

    [Fact]
    public void A_document_the_client_opened_itself_still_wins_until_the_editor_moves_on()
    {
        var followed = _ws.Write("followed.aml", Fixtures.Caex215);
        var chosen = _ws.Write("chosen.aml", Fixtures.Rich30);
        var pointer = _ws.Write("open-document.txt", followed);
        var store = StoreFollowing(pointer);

        AmlTools.OpenDocumentText(store, chosen);

        Assert.Contains("Reference check for chosen.aml", AmlTools.CheckText(store));
    }

    [Fact]
    public void A_pointer_outside_the_roots_or_a_broken_one_is_ignored()
    {
        using var outside = new Workspace();
        var secret = outside.Write("secret.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", secret);
        var store = StoreFollowing(pointer);

        Assert.Contains("outside the directories this server may read",
            Assert.Throws<McpException>(() => AmlTools.CheckText(store)).Message);

        File.WriteAllText(pointer, "this is not a path");
        Assert.Contains("which does not exist", Assert.Throws<McpException>(() => AmlTools.CheckText(store)).Message);
    }

    [Fact]
    public void The_option_needs_a_file()
    {
        Assert.Contains("--follow needs a file", Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--follow" })).Message);
        Assert.Equal(Path.GetFullPath("pointer.txt"), ServerOptions.Parse(new[] { "--follow", "pointer.txt" }).FollowFile);
    }
}

/// <summary>The client may ask for the overview without naming a file at all.</summary>
public sealed class OpenWithoutAPathTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Omitting_the_path_reads_the_document_the_editor_points_at()
    {
        var plant = _ws.Write("plant.aml", Fixtures.Caex215);
        var pointer = _ws.Write("open-document.txt", plant);
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _ws.Dir, "--follow", pointer }));

        var text = Answer.Text(AmlTools.OpenAmlDocument(store));

        Assert.Contains("Document: plant.aml", text);
        Assert.Contains("CAEX 2.15", text);
    }

    [Fact]
    public void Without_a_pointer_the_refusal_says_what_to_do()
    {
        var store = new DocumentStore();

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenAmlDocument(store));

        Assert.Contains("call open_aml_document with the path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FollowedDocumentOutsideTheRootTests : IDisposable
{
    private readonly Workspace _inside = new();
    private readonly Workspace _outside = new();

    public void Dispose()
    {
        _inside.Dispose();
        _outside.Dispose();
    }

    [Fact]
    public void The_refusal_names_the_file_and_the_fence()
    {
        var elsewhere = _outside.Write("plant.aml", Fixtures.Caex215);
        var pointer = _inside.Write("open-document.txt", elsewhere);
        var store = new DocumentStore(ServerOptions.Parse(new[] { "--root", _inside.Dir, "--follow", pointer }));

        var ex = Assert.Throws<McpException>(() => AmlTools.OpenAmlDocument(store));

        Assert.Contains("the editor has", ex.Message);
        Assert.Contains(elsewhere, ex.Message);
        Assert.Contains("outside the directories this server may read", ex.Message);
    }
}

/// <summary>
/// Every tool states all four hints. Without them a client has to assume the defaults of the
/// specification, which are destructive and open world, and would warn about a server that reads.
/// </summary>
public sealed class ToolAnnotationTests
{
    [Fact]
    public void All_tools_declare_that_they_only_read_a_local_document()
    {
        var tools = typeof(AmlTools).GetMethods()
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().FirstOrDefault())
            .Where(a => a is not null)
            .ToList();

        Assert.Equal(10, tools.Count);
        Assert.All(tools, tool =>
        {
            Assert.True(tool!.Idempotent);
            Assert.False(tool.Destructive);
            Assert.False(tool.OpenWorld);
        });

        // Nine read. show_in_editor writes a file for the editor, and says so.
        Assert.Equal(9, tools.Count(t => t!.ReadOnly));
        Assert.Equal("show_in_editor", Assert.Single(tools, t => !t!.ReadOnly)!.Name);
    }
}

/// <summary>
/// An assistant can point the editor at an element, and only claims success when the editor
/// confirms. A file nobody reads must not read as "shown".
/// </summary>
public sealed class ShowInEditorTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly TimeSpan _patience = EditorHandshake.Patience;

    public ShowInEditorTests() => EditorHandshake.Patience = TimeSpan.FromMilliseconds(600);

    public void Dispose()
    {
        EditorHandshake.Patience = _patience;
        _ws.Dispose();
    }

    private string SelectFile => Path.Combine(_ws.Dir, "show-in-editor.json");

    private DocumentStore Store(bool withEditor = true)
    {
        var arguments = withEditor
            ? new[] { "--root", _ws.Dir, "--select", SelectFile }
            : new[] { "--root", _ws.Dir };
        var store = new DocumentStore(ServerOptions.Parse(arguments));
        AmlTools.OpenDocumentText(store, _ws.Write("rich.aml", Fixtures.Rich30));
        return store;
    }

    private readonly HashSet<string> _answered = new();

    /// <summary>
    /// Stands in for the editor panel: answers a request once. Like the panel it tells requests
    /// apart by their nonce, so an old file is not answered again.
    /// </summary>
    private Task PlayEditor(string status, string? detail = null, Action<ShowRequest>? inspect = null) => Task.Run(() =>
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(SelectFile))
                {
                    var request = System.Text.Json.JsonSerializer.Deserialize<ShowRequest>(File.ReadAllText(SelectFile));
                    if (request is not null && _answered.Add(request.Nonce))
                    {
                        inspect?.Invoke(request);
                        File.WriteAllText(EditorHandshake.AckFileFor(SelectFile),
                            System.Text.Json.JsonSerializer.Serialize(new ShowAck(request.Nonce, status, detail)));
                        return;
                    }
                }
            }
            catch (IOException) { /* the server may still be writing */ }
            Thread.Sleep(10);
        }
    });

    [Fact]
    public void The_request_names_the_document_the_element_and_a_nonce()
    {
        var store = Store();
        ShowRequest? seen = null;
        var editor = PlayEditor("selected", inspect: r => seen = r);

        var result = AmlTools.ShowInEditor(store, "Station1");
        editor.Wait();

        Assert.NotNull(seen);
        Assert.Equal(Answer.Data(result).GetProperty("id").GetString(), seen!.Id);
        Assert.Equal(Path.Combine(_ws.Dir, "rich.aml"), seen.Document);
        Assert.Equal("Station1", Path.GetFileName(seen.Path));
        Assert.NotEmpty(seen.Nonce);
        Assert.Contains("confirmed", Answer.Text(result));
    }

    [Fact]
    public void Showing_the_same_element_twice_works_the_second_time_as_well()
    {
        var store = Store();
        var nonces = new List<string>();

        for (var round = 0; round < 2; round++)
        {
            var editor = PlayEditor("selected", inspect: r => nonces.Add(r.Nonce));
            AmlTools.ShowInEditor(store, "Station1");
            editor.Wait();
        }

        Assert.Equal(2, nonces.Count);
        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public void Without_an_answer_the_call_does_not_claim_that_anything_was_shown()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.ShowInEditor(Store(), "Station1"));

        Assert.Contains("No AutomationML Editor answered", ex.Message);
        Assert.Contains("Nothing was shown", ex.Message);
    }

    [Fact]
    public void An_editor_showing_another_document_says_so()
    {
        var editor = PlayEditor("mismatch", "the editor has another document open: other.aml");

        var ex = Assert.Throws<McpException>(() => AmlTools.ShowInEditor(Store(), "Station1"));
        editor.Wait();

        Assert.Contains("did not show it", ex.Message);
        Assert.Contains("other.aml", ex.Message);
    }

    [Fact]
    public void Without_an_editor_the_refusal_tells_the_user_what_to_do()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.ShowInEditor(Store(withEditor: false), "Station1"));

        Assert.Contains("AmlMcp panel", ex.Message);
        Assert.Contains("Register", ex.Message);
    }

    [Fact]
    public void An_unknown_element_is_refused_before_anything_is_written()
    {
        var store = Store();

        Assert.Throws<McpException>(() => AmlTools.ShowInEditor(store, "NoSuchElement"));

        Assert.False(File.Exists(SelectFile));
    }

    [Fact]
    public void An_answer_to_an_older_request_is_not_taken_for_this_one()
    {
        var store = Store();
        File.WriteAllText(EditorHandshake.AckFileFor(SelectFile),
            System.Text.Json.JsonSerializer.Serialize(new ShowAck("an-old-nonce", "selected")));

        Assert.Throws<McpException>(() => AmlTools.ShowInEditor(store, "Station1"));
    }
}

/// <summary>The library resource is read whole, so it must not be unbounded.</summary>
public sealed class LibraryResourceTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_large_library_is_capped_and_says_what_it_left_out()
    {
        var classes = string.Join("\n", Enumerable.Range(0, 500)
            .Select(i => $"    <SystemUnitClass Name=\"Class{i:000}\" />"));
        var document = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="big.aml" xmlns="http://www.dke.de/CAEX">
              <SystemUnitClassLib Name="BigLib">
            {classes}
              </SystemUnitClassLib>
            </CAEXFile>
            """;
        AmlTools.OpenDocumentText(_store, _ws.Write("big.aml", document));

        var text = AmlResources.ClassLibraries(_store);

        Assert.Contains("BigLib  (embedded), 500 classes", text);
        Assert.Contains("class(es) not listed here", text);
        Assert.InRange(text.Split('\n').Length, 10, 120);
    }
}

/// <summary>
/// Three defects that real documents brought out: bracketed link partners, an attribute
/// named RefDataSource, and two relations between the same pair of elements.
/// </summary>
public sealed class RealDocumentDefectTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private (DocumentStore Store, string Overview) Open(string fileName, string document)
    {
        var store = new DocumentStore();
        return (store, AmlTools.OpenDocumentText(store, _ws.Write(fileName, document)));
    }

    /// <summary>
    /// CAEX 2.15 wraps a link partner in brackets, [owner]:[interface], which the editor
    /// writes by default. Every link of such a document used to have no partner.
    /// </summary>
    [Fact]
    public void Link_partners_in_brackets_resolve_like_the_bare_form()
    {
        var (store, overview) = Open("bracketed.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile FileName="bracketed.aml" SchemaVersion="2.15">
              <InstanceHierarchy Name="Cell">
                <InternalElement Name="Robot" ID="{33333333-0000-0000-0000-000000000001}">
                  <ExternalInterface Name="Tool coupling" ID="{33333333-0000-0000-0000-0000000000a1}" />
                </InternalElement>
                <InternalElement Name="Gripper" ID="{33333333-0000-0000-0000-000000000002}">
                  <ExternalInterface Name="Mount" ID="{33333333-0000-0000-0000-0000000000b1}" />
                </InternalElement>
                <InternalLink Name="Mounted"
                              RefPartnerSideA="[{33333333-0000-0000-0000-000000000001}]:[Tool coupling]"
                              RefPartnerSideB="[{33333333-0000-0000-0000-000000000002}]:[Mount]" />
              </InstanceHierarchy>
            </CAEXFile>
            """);

        Assert.Contains("1 total, 0 across hierarchies, 0 unresolved", overview);
        Assert.Contains("no problems found", Answer.Text(AmlTools.CheckReferences(store)));
        Assert.Contains("--link Mounted", Answer.Text(AmlTools.GetNeighbors(store, "Cell/Robot")));
    }

    /// <summary>
    /// The attribute conventions are written RefSomething as often as refSomething, but an
    /// attribute that declares an ID is not a reference to one.
    /// </summary>
    [Fact]
    public void A_capital_Ref_names_a_reference_and_an_xs_ID_does_not()
    {
        var (store, overview) = Open("refs.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile FileName="refs.aml" SchemaVersion="2.15">
              <InstanceHierarchy Name="Plant">
                <InternalElement Name="Variable" ID="{44444444-0000-0000-0000-000000000001}">
                  <Attribute Name="RefDataSource" AttributeDataType="xs:string"><Value>{44444444-0000-0000-0000-000000000002}</Value></Attribute>
                  <Attribute Name="Identification" AttributeDataType="xs:ID"><Value>{44444444-0000-0000-0000-000000000002}</Value></Attribute>
                </InternalElement>
                <InternalElement Name="Source" ID="{44444444-0000-0000-0000-000000000002}" />
              </InstanceHierarchy>
            </CAEXFile>
            """);

        Assert.Contains("1 resolved", overview);
        var neighbours = Answer.Text(AmlTools.GetNeighbors(store, "Plant/Variable"));
        Assert.Contains("--RefDataSource-->  Plant/Source", neighbours);
        Assert.DoesNotContain("Identification", neighbours);
    }

    /// <summary>
    /// Two elements can be related twice, and which relation it is carries the meaning:
    /// one points at the other and is pointed back at. Only one of them used to be reported.
    /// </summary>
    [Fact]
    public void Two_relations_between_the_same_pair_are_both_reported()
    {
        var (store, _) = Open("both-ways.aml", """
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile FileName="both-ways.aml" SchemaVersion="2.15">
              <InstanceHierarchy Name="Process">
                <InternalElement Name="Step" ID="{66666666-0000-0000-0000-000000000001}">
                  <Attribute Name="refObj" AttributeDataType="xs:string"><Value>{66666666-0000-0000-0000-000000000002}</Value></Attribute>
                </InternalElement>
                <InternalElement Name="Operator" ID="{66666666-0000-0000-0000-000000000002}">
                  <Attribute Name="refProcess" AttributeDataType="xs:string"><Value>{66666666-0000-0000-0000-000000000001}</Value></Attribute>
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """);

        var result = AmlTools.GetNeighbors(store, "Process/Step");
        var text = Answer.Text(result);

        Assert.Contains("--refObj-->  Process/Operator", text);
        Assert.Contains("<--refProcess--  Process/Operator", text);
        Assert.Equal(2, Answer.Data(result).GetProperty("total").GetInt32());
    }
}

/// <summary>
/// A class card says how often the class is used and lists what belongs to it. Both used
/// to mislead: subclasses were counted as elements, and long lists just stopped.
/// </summary>
public sealed class ClassCardCountTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    public ClassCardCountTests()
    {
        var attributes = string.Join("\n", Enumerable.Range(0, 60)
            .Select(i => $"      <Attribute Name=\"A{i:00}\" AttributeDataType=\"xs:string\"><DefaultValue>{i}</DefaultValue></Attribute>"));
        var subclasses = string.Join("\n", Enumerable.Range(0, 25)
            .Select(i => $"    <SystemUnitClass Name=\"Small{i:00}\" RefBaseClassPath=\"Lib/Big\" />"));
        var document = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <CAEXFile SchemaVersion="3.0" FileName="big-class.aml" xmlns="http://www.dke.de/CAEX">
              <RoleClassLib Name="Roles">
                <RoleClass Name="Machine" />
              </RoleClassLib>
              <SystemUnitClassLib Name="Lib">
                <SystemUnitClass Name="Big">
            {{attributes}}
                </SystemUnitClass>
            {{subclasses}}
              </SystemUnitClassLib>
              <InstanceHierarchy Name="Plant">
                <InternalElement Name="TheOne" ID="{77777777-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Lib/Big">
                  <RoleRequirements RefBaseRoleClassPath="Roles/Machine" />
                </InternalElement>
              </InstanceHierarchy>
            </CAEXFile>
            """;
        AmlTools.OpenDocumentText(_store, _ws.Write("big-class.aml", document));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void Subclasses_are_not_counted_as_elements_that_use_the_class()
    {
        var card = AmlTools.ClassCard(_store, "Lib/Big");

        Assert.Contains("used by 1 element(s) in this document, and by 25 class(es) in the libraries", card);
    }

    /// <summary>A role requirement states the class of the element that holds it, once.</summary>
    [Fact]
    public void An_element_that_requires_a_role_counts_once_for_that_role()
    {
        Assert.Contains("used by 1 element(s) in this document", AmlTools.ClassCard(_store, "Roles/Machine"));
    }

    [Fact]
    public void A_card_that_leaves_something_out_says_how_much()
    {
        var card = AmlTools.ClassCard(_store, "Lib/Big");

        Assert.Contains("... 10 more", card);
        Assert.Contains("subclasses: ", card);
        Assert.Contains(", ... 5 more", card);
    }
}

/// <summary>
/// A class path may carry an alias. If an ExternalReference declares that alias, the class
/// has to be in the library it points at; the server used to read it from any library of
/// the same name without a word.
/// </summary>
public sealed class AliasMismatchTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Library = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="PartLib.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="Shared">
            <SystemUnitClass Name="Bolt" />
          </SystemUnitClassLib>
        </CAEXFile>
        """;

    /// <summary>
    /// Vendor declares PartLib.aml, which has Shared/Bolt but no Shared/Nut. The document
    /// embeds a Shared library of its own, and that one does have a Nut.
    /// </summary>
    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="user.aml" xmlns="http://www.dke.de/CAEX">
          <ExternalReference Path="PartLib.aml" Alias="Vendor" />
          <AttributeTypeLib Name="OMG_DD_AttributeTypeLib">
            <AttributeType Name="DD_Bounds" AttributeDataType="xs:string" />
          </AttributeTypeLib>
          <SystemUnitClassLib Name="Shared">
            <SystemUnitClass Name="Nut" />
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{88888888-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Fastener" ID="{88888888-0000-0000-0000-000000000001}" RefBaseSystemUnitPath="Vendor@Shared/Nut">
              <Attribute Name="Bounds" AttributeDataType="xs:string" RefAttributeType="OMG_DD@OMG_DD_AttributeTypeLib/DD_Bounds" />
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AliasMismatchTests()
    {
        _ws.Write("PartLib.aml", Library);
        AmlTools.OpenDocumentText(_store, _ws.Write("user.aml", Document));
    }

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_declared_alias_that_does_not_carry_the_class_is_reported()
    {
        var check = AmlTools.CheckText(_store);
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("alias does not carry the class: Vendor@Shared/Nut", check);
        Assert.Contains("the library behind Vendor has no Shared/Nut", check);
        Assert.Contains("Shared/Nut from Shared was read instead", check);
        Assert.Contains(Answer.Strings(data.GetProperty("problems"), "kind"), k => k == "aliasWithoutTheClass");

        // The class is still shown, so the element card stays usable.
        Assert.Contains("class: Vendor@Shared/Nut", AmlTools.ElementCard(_store, "Plant/Fastener"));
    }

    /// <summary>
    /// An alias no ExternalReference declares is how AML documents name the standard
    /// libraries they embed. Reporting those would bury the real finding.
    /// </summary>
    [Fact]
    public void An_alias_of_an_embedded_standard_library_is_left_alone()
    {
        var check = AmlTools.CheckText(_store);

        Assert.DoesNotContain("OMG_DD", check);
        Assert.Equal(1, check.Split('\n').Count(l => l.Contains("alias does not carry the class")));
    }
}

/// <summary>
/// CAEX does not make names unique, and some documents reuse an ID. Both used to be
/// answered with the first element found and nothing said about the others.
/// </summary>
public sealed class AmbiguityTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="twins.aml" xmlns="http://www.dke.de/CAEX">
          <InstanceHierarchy Name="Plant" ID="{99990000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Assembly" ID="{99990000-0000-0000-0000-000000000001}">
              <InternalElement Name="Spring" ID="{99990000-0000-0000-0000-000000000002}" />
              <InternalElement Name="Spring" ID="{99990000-0000-0000-0000-000000000003}" />
            </InternalElement>
            <InternalElement Name="First" ID="{99990000-0000-0000-0000-00000000000a}" />
            <InternalElement Name="Second" ID="{99990000-0000-0000-0000-00000000000a}" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public AmbiguityTests() => AmlTools.OpenDocumentText(_store, _ws.Write("twins.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_path_that_leads_to_two_elements_is_refused_with_both_ids()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.ElementCard(_store, "Plant/Assembly/Spring"));

        Assert.Contains("leads to 2 elements", ex.Message);
        Assert.Contains("{99990000-0000-0000-0000-000000000002}", ex.Message);
        Assert.Contains("{99990000-0000-0000-0000-000000000003}", ex.Message);
    }

    [Fact]
    public void An_unambiguous_path_still_answers()
    {
        Assert.Contains("path: Plant/Assembly", AmlTools.ElementCard(_store, "Plant/Assembly"));
    }

    [Fact]
    public void A_repeated_id_names_both_elements_and_says_which_one_answers()
    {
        var check = AmlTools.CheckText(_store);
        var card = AmlTools.ElementCard(_store, "{99990000-0000-0000-0000-00000000000a}");
        var data = Answer.Data(AmlTools.CheckReferences(_store));

        Assert.Contains("duplicate ID: {99990000-0000-0000-0000-00000000000a}  (Plant/First, Plant/Second", check);
        Assert.Contains("answers about this ID are about Plant/First", check);

        Assert.Contains("2 elements carry this ID", card);
        Assert.Contains("the others are Plant/Second", card);

        Assert.Contains(data.GetProperty("problems").EnumerateArray(),
            p => p.GetProperty("kind").GetString() == "duplicateId"
                 && p.GetProperty("message").GetString()!.Contains("Plant/Second"));
    }
}

/// <summary>
/// A class library holds templates: elements, interfaces and links of its own. They used
/// to be counted and searched as if they were part of the plant.
/// </summary>
public sealed class ClassTemplateTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();
    private readonly string _overview;

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="templates.aml" xmlns="http://www.dke.de/CAEX">
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Station">
              <InternalElement Name="Drive" ID="{aaaa0000-0000-0000-0000-000000000001}">
                <ExternalInterface Name="Port" ID="{aaaa0000-0000-0000-0000-0000000000a1}" />
              </InternalElement>
              <InternalElement Name="Sensor" ID="{aaaa0000-0000-0000-0000-000000000002}">
                <ExternalInterface Name="Port" ID="{aaaa0000-0000-0000-0000-0000000000a2}" />
              </InternalElement>
              <InternalLink Name="Wiring"
                            RefPartnerSideA="{aaaa0000-0000-0000-0000-0000000000a1}"
                            RefPartnerSideB="{aaaa0000-0000-0000-0000-0000000000a2}" />
            </SystemUnitClass>
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{aaaa0000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Drive" ID="{aaaa0000-0000-0000-0000-000000000011}" RefBaseSystemUnitPath="Lib/Station" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public ClassTemplateTests() =>
        _overview = AmlTools.OpenDocumentText(_store, _ws.Write("templates.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void The_overview_says_how_many_links_belong_to_the_templates()
    {
        Assert.Contains("InternalLinks: 1 total", _overview);
        Assert.Contains("(1 of them inside class library templates)", _overview);
        Assert.Contains("Plant: 1 elements", _overview);
    }

    [Fact]
    public void The_search_answers_about_the_plant_and_counts_the_templates_apart()
    {
        var text = AmlTools.FindElementsText(_store, "Drive");
        var data = Answer.Data(AmlTools.FindElements(_store, "Drive"));

        Assert.Contains("1 match(es)", text);
        Assert.Contains("Plant/Drive", text);
        Assert.DoesNotContain("Lib/Station/Drive  [", text);
        Assert.Contains("1 further match(es) are templates inside class libraries", text);

        Assert.Equal(1, data.GetProperty("total").GetInt32());
        Assert.Equal(1, data.GetProperty("inClassLibraries").GetInt32());
    }

    [Fact]
    public void A_template_element_says_where_it_sits_instead_of_showing_an_empty_hierarchy()
    {
        var card = AmlTools.ElementCard(_store, "{aaaa0000-0000-0000-0000-000000000001}");

        Assert.Contains("path: Lib/Station/Drive", card);
        Assert.Contains("this is a template inside a class library", card);
        Assert.DoesNotContain("hierarchy: \n", card);
    }
}

/// <summary>
/// Smaller things an element card used to pass over: a link inside one element, an ID
/// that names an interface or a class, and an interface class in the wrong attribute.
/// </summary>
public sealed class ElementCardDetailTests : IDisposable
{
    private readonly Workspace _ws = new();
    private readonly DocumentStore _store = new();

    private const string Document = """
        <?xml version="1.0" encoding="utf-8"?>
        <CAEXFile SchemaVersion="3.0" FileName="details.aml" xmlns="http://www.dke.de/CAEX">
          <InterfaceClassLib Name="Interfaces">
            <InterfaceClass Name="Coupling" />
          </InterfaceClassLib>
          <SystemUnitClassLib Name="Lib">
            <SystemUnitClass Name="Loop" ID="{bbbb0000-0000-0000-0000-00000000c001}" />
          </SystemUnitClassLib>
          <InstanceHierarchy Name="Plant" ID="{bbbb0000-0000-0000-0000-0000000000f1}">
            <InternalElement Name="Bypass" ID="{bbbb0000-0000-0000-0000-000000000001}">
              <ExternalInterface Name="In" ID="{bbbb0000-0000-0000-0000-0000000000a1}" RefBaseClassPath="Interfaces/Coupling">
                <Attribute Name="NodeId" AttributeDataType="xs:string"><Value>ns=2;i=42</Value></Attribute>
              </ExternalInterface>
              <ExternalInterface Name="Out" ID="{bbbb0000-0000-0000-0000-0000000000a2}" RefBaseSystemUnitPath="Lib/Loop" />
            </InternalElement>
            <InternalLink Name="Shortcut"
                          RefPartnerSideA="{bbbb0000-0000-0000-0000-0000000000a1}"
                          RefPartnerSideB="{bbbb0000-0000-0000-0000-0000000000a2}" />
          </InstanceHierarchy>
        </CAEXFile>
        """;

    public ElementCardDetailTests() => AmlTools.OpenDocumentText(_store, _ws.Write("details.aml", Document));

    public void Dispose() => _ws.Dispose();

    [Fact]
    public void A_link_between_two_interfaces_of_one_element_is_named()
    {
        var card = AmlTools.ElementCard(_store, "Plant/Bypass");

        Assert.Contains("link Shortcut runs between two interfaces of this element: In -> Out", card);
        Assert.DoesNotContain("connections: none", card);
    }

    [Fact]
    public void An_interface_id_says_so_and_shows_what_the_interface_carries()
    {
        var card = AmlTools.ElementCard(_store, "{bbbb0000-0000-0000-0000-0000000000a1}");

        Assert.Contains("the ID names the interface In", card);
        Assert.Contains("attributes of the interface In:", card);
        Assert.Contains("NodeId = ns=2;i=42", card);
    }

    [Fact]
    public void An_interface_class_in_the_wrong_attribute_is_pointed_out()
    {
        Assert.Contains("RefBaseSystemUnitPath=Lib/Loop is the wrong attribute for an interface",
            AmlTools.ElementCard(_store, "Plant/Bypass"));
    }

    [Fact]
    public void The_id_of_a_class_sends_the_caller_to_get_class()
    {
        var ex = Assert.Throws<McpException>(() => AmlTools.ElementCard(_store, "{bbbb0000-0000-0000-0000-00000000c001}"));

        Assert.Contains("SystemUnitClass Lib/Loop", ex.Message);
        Assert.Contains("get_class", ex.Message);
    }
}
