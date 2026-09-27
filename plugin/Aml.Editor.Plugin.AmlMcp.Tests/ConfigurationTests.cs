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
    public void A_configuration_that_is_not_json_is_refused_with_its_path()
    {
        File.WriteAllText(_configFile, "this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => McpProbe.RegisterWithClaudeDesktop(Exe, @"C:\models", _configFile));

        Assert.Contains(_configFile, ex.Message);
        Assert.False(McpProbe.ClaudeDesktopIsCurrent(Exe, @"C:\models", _configFile));
    }
}
