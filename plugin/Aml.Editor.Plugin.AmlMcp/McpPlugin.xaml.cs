// Registers the aml-mcp server with an AI assistant and checks the connection,
// without leaving the AutomationML Editor.
using System.IO;
using System.Windows;
using Aml.Editor.Plugin.WPFBase;

namespace Aml.Editor.Plugin.AmlMcp;

public partial class McpPlugin : PluginViewBase
{
    private string? _documentPath;
    private bool _rootEdited;

    public McpPlugin()
    {
        InitializeComponent();
        DisplayName = "AmlMcp";
        IsReactive = false;

        RootBox.TextChanged += (_, _) => _rootEdited = true;
        ServerLine.Text = ServerPath() is { } exe
            ? $"{exe}\nversion {McpProbe.Version(exe) ?? "unknown"}"
            : "aml-mcp.exe was not found next to the plugin. Build it with publish-server.ps1.";
    }

    public override string PackageName => "Aml.Editor.Plugin.AmlMcp";

    public override bool CanClose => true;

    public override void ChangeAMLFilePath(string amlFilePath)
    {
        base.ChangeAMLFilePath(amlFilePath);
        _documentPath = amlFilePath;
        if (_rootEdited || string.IsNullOrWhiteSpace(amlFilePath)) return;
        RootBox.Text = Path.GetDirectoryName(amlFilePath) ?? "";
        _rootEdited = false;
    }

    /// <summary>The server shipped with the plugin, or a local build while developing.</summary>
    private static string? ServerPath()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "runtime", "aml-mcp.exe");
        if (File.Exists(beside)) return beside;

        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "AmlMcp", "bin", "Release", "net10.0", "aml-mcp.exe"));
        return File.Exists(repository) ? repository : null;
    }

    private string Root() => RootBox.Text.Trim();

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) { Output.Text = ServerLine.Text; return; }

        TestButton.IsEnabled = false;
        Output.Text = "starting the server ...";
        var root = Root();
        var document = _documentPath;

        Task.Run(() =>
        {
            var lines = new List<string>();
            try
            {
                using var probe = new McpProbe(exe, root);
                lines.AddRange(probe.Run(document));
            }
            catch (Exception ex)
            {
                lines.Add($"failed: {ex.Message}");
            }
            Dispatcher.Invoke(() =>
            {
                Output.Text = string.Join(Environment.NewLine, lines);
                TestButton.IsEnabled = true;
            });
        });
    }

    private void OnRegisterClaudeDesktop(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) { Output.Text = ServerLine.Text; return; }
        try
        {
            var path = McpProbe.RegisterWithClaudeDesktop(exe, Root());
            Output.Text = $"written to {path}\n\nRestart Claude Desktop, then ask it about a file in {Root()}.";
        }
        catch (Exception ex)
        {
            Output.Text = $"failed: {ex.Message}";
        }
    }

    private void OnCopyConfiguration(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) { Output.Text = ServerLine.Text; return; }
        var json = McpProbe.Configuration(exe, Root()).ToJsonString(McpProbe.Pretty);
        Clipboard.SetText(json);
        Output.Text = "copied to the clipboard:\n\n" + json;
    }
}
