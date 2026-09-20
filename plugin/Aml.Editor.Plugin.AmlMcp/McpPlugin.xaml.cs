// Registers the aml-mcp server with an AI assistant and reads the open document,
// without leaving the AutomationML Editor.
using System.IO;
using System.Windows;
using System.Windows.Media;
using Aml.Editor.Plugin.WPFBase;

namespace Aml.Editor.Plugin.AmlMcp;

public partial class McpPlugin : PluginViewBase
{
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    private string? _documentPath;
    private bool _rootEdited;

    public McpPlugin()
    {
        InitializeComponent();
        DisplayName = "AmlMcp";
        IsReactive = false;

        RootBox.TextChanged += (_, _) => _rootEdited = true;
        ShowQuestions(null);

        // The editor only reports a change; a document opened before this panel existed
        // has to be looked up once.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_documentPath is null && EditorDocument.CurrentPath() is { } open) UseDocument(open);
        }), System.Windows.Threading.DispatcherPriority.Background);

        if (ServerPath() is { } exe)
        {
            var version = McpProbe.Version(exe);
            Status(version is not null, version is not null
                ? $"aml-mcp {version} is ready on this machine"
                : "aml-mcp was found but did not answer");
            ServerLine.Text = exe;
        }
        else
        {
            Status(false, "aml-mcp.exe was not found next to the plugin");
            ServerLine.Text = "Build it with plugin\\publish-server.ps1 and install the plugin again.";
        }
    }

    public override string PackageName => "Aml.Editor.Plugin.AmlMcp";

    public override bool CanClose => true;

    public override void ChangeAMLFilePath(string amlFilePath)
    {
        base.ChangeAMLFilePath(amlFilePath);
        UseDocument(amlFilePath);
    }

    private void UseDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _documentPath = path;
        ShowQuestions(path);
        if (_rootEdited) return;
        RootBox.Text = Path.GetDirectoryName(path) ?? "";
        _rootEdited = false;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Directory the assistant may read" };
        if (Directory.Exists(Root())) dialog.InitialDirectory = Root();
        if (dialog.ShowDialog() == true) RootBox.Text = dialog.FolderName;
    }

    /// <summary>The server shipped with the plugin, or a local build while developing.</summary>
    private static string? ServerPath()
    {
        // AppContext.BaseDirectory is the editor's folder here, not the plugin's.
        var here = Path.GetDirectoryName(typeof(McpPlugin).Assembly.Location);
        if (string.IsNullOrEmpty(here)) return null;

        var beside = Path.Combine(here, "runtime", "aml-mcp.exe");
        if (File.Exists(beside)) return beside;

        var repository = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "src", "AmlMcp", "bin", "Release", "net10.0", "aml-mcp.exe"));
        return File.Exists(repository) ? repository : null;
    }

    private string Root() => RootBox.Text.Trim();

    private void Status(bool ok, string text)
    {
        StatusDot.Foreground = ok ? Good : Bad;
        StatusText.Text = text;
    }

    private void ShowQuestions(string? documentPath)
    {
        var name = string.IsNullOrWhiteSpace(documentPath) ? "the document" : Path.GetFileName(documentPath);
        Question1.Text = $"Open {name} and tell me what is in it.";
        Question2.Text = "Which instance hierarchies exist, and how are they connected to each other?";
        Question3.Text = "Pick one element and show me everything it is linked to, with IDs.";
    }

    private void OnTest(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) return;

        TestButton.IsEnabled = false;
        Status(true, "reading ...");
        var root = Root();
        var document = _documentPath;

        Task.Run(() =>
        {
            ProbeResult result;
            try
            {
                using var probe = new McpProbe(exe, root);
                result = probe.Run(document);
            }
            catch (Exception ex)
            {
                result = new ProbeResult(false, "The server did not answer", "", ex.Message, ex.ToString());
            }
            Dispatcher.Invoke(() =>
            {
                ResultBox.Visibility = Visibility.Visible;
                ResultHeadline.Text = result.Headline;
                ResultFacts.Text = result.Facts;
                ResultVerdict.Text = result.Verdict;
                ResultVerdict.Foreground = result.Ok ? Good : Bad;
                Details.Text = result.Details;
                Status(result.Ok, result.Ok
                    ? $"the assistant can read {result.Headline}"
                    : "see the result below");
                TestButton.IsEnabled = true;
            });
        });
    }

    private void OnRegisterClaudeDesktop(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) return;
        try
        {
            var path = McpProbe.RegisterWithClaudeDesktop(exe, Root());
            Status(true, "registered with Claude Desktop, restart it to pick the server up");
            ResultBox.Visibility = Visibility.Visible;
            ResultHeadline.Text = "Claude Desktop";
            ResultFacts.Text = path;
            ResultVerdict.Text = $"The assistant may now read {Root()}";
            ResultVerdict.Foreground = Good;
            Details.Text = McpProbe.Configuration(exe, Root()).ToJsonString(McpProbe.Pretty);
        }
        catch (Exception ex)
        {
            Status(false, ex.Message);
        }
    }

    private void OnCopyConfiguration(object sender, RoutedEventArgs e)
    {
        if (ServerPath() is not { } exe) return;
        var json = McpProbe.Configuration(exe, Root()).ToJsonString(McpProbe.Pretty);
        Clipboard.SetText(json);
        Status(true, "configuration copied, paste it into your assistant's settings");
        ResultBox.Visibility = Visibility.Visible;
        ResultHeadline.Text = "Configuration";
        ResultFacts.Text = "in the clipboard";
        ResultVerdict.Text = "";
        Details.Text = json;
        DetailsExpander.IsExpanded = true;
    }

    private void OnCopyQuestions(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(string.Join(Environment.NewLine, Question1.Text, Question2.Text, Question3.Text));
        Status(true, "questions copied");
    }
}
