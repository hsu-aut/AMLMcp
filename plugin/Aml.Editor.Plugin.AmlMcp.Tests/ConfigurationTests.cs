using System.IO;
using System.Text.Json.Nodes;
using Aml.Editor.Plugin.AmlMcp;
using Xunit;

namespace Aml.Editor.Plugin.AmlMcp.Tests;

/// <summary>
/// The panel writes into a file another application owns, and it tells the user when that
/// entry is out of date. Both are checked here against a configuration in a temp directory,
/// never against the real one.
/// </summary>
public sealed class ConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "amlmcp-config-" + Guid.NewGuid().ToString("N"));
    private readonly string _configFile;
    private const string Exe = @"C:\somewhere\aml-mcp.exe";

    public ConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
        _configFile = Path.Combine(_directory, "claude_desktop_config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void The_entry_names_the_server_the_readable_directory_and_both_link_files()
    {
        var entry = McpProbe.Configuration(Exe, @"C:\models")["mcpServers"]!["aml"]!;
        var arguments = entry["args"]!.AsArray().Select(a => a!.ToString()).ToList();

        Assert.Equal(Exe, entry["command"]!.ToString());
        Assert.Equal(new[] { "--root", @"C:\models", "--follow", McpProbe.PointerFile, "--select", McpProbe.SelectFile }, arguments);
    }

    [Fact]
    public void Without_a_readable_directory_the_root_argument_is_left_out()
    {
        var arguments = McpProbe.Configuration(Exe, "   ")["mcpServers"]!["aml"]!["args"]!.AsArray()
            .Select(a => a!.ToString()).ToList();

        Assert.DoesNotContain("--root", arguments);
        Assert.Contains("--follow", arguments);
    }

    [Fact]
    public void Registering_keeps_the_other_servers_of_the_assistant()
    {
        File.WriteAllText(_configFile, """
            {
              "mcpServers": { "zotero": { "command": "zotero-mcp.exe" } },
              "preferences": { "menuBarEnabled": false }
            }
            """);

        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile);

        var written = JsonNode.Parse(File.ReadAllText(_configFile))!;
        Assert.Equal("zotero-mcp.exe", written["mcpServers"]!["zotero"]!["command"]!.ToString());
        Assert.Equal(Exe, written["mcpServers"]!["aml"]!["command"]!.ToString());
        Assert.False(written["preferences"]!["menuBarEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Registering_into_a_missing_file_creates_it()
    {
        var path = McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile);

        Assert.Equal(_configFile, path);
        Assert.True(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }

    [Fact]
    public void A_configuration_that_does_not_exist_is_not_current()
    {
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }

    [Fact]
    public void An_entry_written_by_an_older_version_is_recognised_as_out_of_date()
    {
        File.WriteAllText(_configFile, """
            { "mcpServers": { "aml": { "command": "C:\\somewhere\\aml-mcp.exe", "args": ["--root", "C:\\models"] } } }
            """);

        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }

    [Fact]
    public void Changing_the_readable_directory_makes_the_entry_out_of_date()
    {
        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile);

        Assert.True(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\other", _configFile));
    }

    [Fact]
    public void Permission_to_create_documents_adds_the_write_fence_for_the_same_directory()
    {
        var arguments = McpProbe.Configuration(Exe, @"C:\models", writable: true)["mcpServers"]!["aml"]!["args"]!
            .AsArray().Select(a => a!.ToString()).ToList();

        Assert.Equal(
            new[] { "--root", @"C:\models", "--write", @"C:\models", "--follow", McpProbe.PointerFile, "--select", McpProbe.SelectFile },
            arguments);
    }

    [Fact]
    public void Without_that_permission_the_entry_carries_no_write_fence()
    {
        var arguments = McpProbe.Configuration(Exe, @"C:\models")["mcpServers"]!["aml"]!["args"]!
            .AsArray().Select(a => a!.ToString()).ToList();

        Assert.DoesNotContain("--write", arguments);
    }

    [Fact]
    public void Without_a_directory_there_is_nothing_to_write_into_either()
    {
        var arguments = McpProbe.Configuration(Exe, "   ", writable: true)["mcpServers"]!["aml"]!["args"]!
            .AsArray().Select(a => a!.ToString()).ToList();

        Assert.DoesNotContain("--write", arguments);
        Assert.DoesNotContain("--root", arguments);
    }

    [Fact]
    public void Granting_the_permission_makes_the_entry_out_of_date()
    {
        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile);

        Assert.True(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile, writable: true));
    }

    [Fact]
    public void Withdrawing_the_permission_makes_the_entry_out_of_date_as_well()
    {
        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile, writable: true);

        Assert.True(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile, writable: true));
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }

    [Fact]
    public void A_stored_permission_is_read_back_so_the_panel_can_show_it()
    {
        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile, writable: true);

        Assert.True(McpProbe.ClaudeDesktopMayWrite(_configFile));
    }

    [Fact]
    public void Without_a_stored_permission_the_panel_is_told_so()
    {
        McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile);

        Assert.False(McpProbe.ClaudeDesktopMayWrite(_configFile));
    }

    [Fact]
    public void A_missing_configuration_grants_no_permission()
    {
        Assert.False(McpProbe.ClaudeDesktopMayWrite(_configFile));
    }

    [Fact]
    public void Another_clients_file_keeps_the_key_it_already_uses()
    {
        File.WriteAllText(_configFile, """
            { "servers": { "other": { "command": "other.exe" } } }
            """);

        var written = McpProbe.RegisterInFile(Exe, @"C:\models", _configFile);

        Assert.Equal("servers", written.Key);
        Assert.Equal(1, written.OtherServers);
        var json = JsonNode.Parse(File.ReadAllText(_configFile))!;
        Assert.Equal(Exe, json["servers"]!["aml"]!["command"]!.ToString());
        Assert.Equal("other.exe", json["servers"]!["other"]!["command"]!.ToString());
        Assert.Null(json["mcpServers"]);
    }

    [Fact]
    public void A_client_that_wants_a_transport_named_gets_one()
    {
        File.WriteAllText(_configFile, """{ "servers": { } }""");

        McpProbe.RegisterInFile(Exe, @"C:\models", _configFile);

        var json = JsonNode.Parse(File.ReadAllText(_configFile))!;
        Assert.Equal("stdio", json["servers"]!["aml"]!["type"]!.ToString());
    }

    [Fact]
    public void A_file_that_uses_mcpServers_is_written_the_way_Claude_Desktop_expects()
    {
        File.WriteAllText(_configFile, """{ "mcpServers": { } }""");

        var written = McpProbe.RegisterInFile(Exe, @"C:\models", _configFile);

        Assert.Equal("mcpServers", written.Key);
        var json = JsonNode.Parse(File.ReadAllText(_configFile))!;
        Assert.Null(json["mcpServers"]!["aml"]!["type"]);
    }

    [Fact]
    public void A_file_that_does_not_exist_yet_is_given_the_common_shape()
    {
        var fresh = Path.Combine(_directory, "new-client.json");

        var written = McpProbe.RegisterInFile(Exe, @"C:\models", fresh, writable: true);

        Assert.Equal("mcpServers", written.Key);
        Assert.Equal(0, written.OtherServers);
        var args = JsonNode.Parse(File.ReadAllText(fresh))!["mcpServers"]!["aml"]!["args"]!.AsArray()
            .Select(a => a!.ToString()).ToList();
        Assert.Contains("--write", args);
    }

    [Fact]
    public void A_client_file_that_is_not_json_is_refused_with_its_path()
    {
        File.WriteAllText(_configFile, "this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => McpProbe.RegisterInFile(Exe, @"C:\models", _configFile));

        Assert.Contains(_configFile, ex.Message);
    }

    [Fact]
    public void A_configuration_that_is_not_json_is_refused_with_its_path()
    {
        File.WriteAllText(_configFile, "this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile));

        Assert.Contains(_configFile, ex.Message);
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }
}
