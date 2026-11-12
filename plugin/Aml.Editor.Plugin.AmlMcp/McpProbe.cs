// A very small MCP client: starts the server, speaks the protocol over stdio and
// reports what came back. Enough to prove the connection from inside the editor.
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Aml.Editor.Plugin.AmlMcp;

/// <summary>What one run found: a line for the panel, and the full answers behind it.</summary>
internal sealed record ProbeResult(bool Ok, string Headline, string Facts, string Verdict, string Details,
    IReadOnlyList<string> Questions);

internal sealed class McpProbe : IDisposable
{
    /// <summary>Writing a JsonNode with options needs a resolver, or it throws at run time.</summary>
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>The file the server reads with --follow: it holds the document the editor shows.</summary>
    public static string PointerFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmlMcp", "open-document.txt");

    /// <summary>The file show_in_editor writes into: the ID of the element to select.</summary>
    public static string SelectFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmlMcp", "show-in-editor.json");

    public static void PointAt(string? documentPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
            File.WriteAllText(PointerFile, documentPath ?? "");
        }
        catch (IOException) { /* the panel works without it, the path just has to be named */ }
        catch (UnauthorizedAccessException) { }
    }

    private readonly Process _process;
    private int _id;

    public McpProbe(string exePath, string? root)
    {
        var info = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(root))
        {
            info.ArgumentList.Add("--root");
            info.ArgumentList.Add(root);
        }
        info.ArgumentList.Add("--follow");
        info.ArgumentList.Add(PointerFile);
        info.ArgumentList.Add("--select");
        info.ArgumentList.Add(SelectFile);
        _process = Process.Start(info) ?? throw new InvalidOperationException($"{exePath} did not start.");
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* going away anyway */ }
        _process.Dispose();
    }

    private JsonNode? Request(string method, JsonNode? parameters, TimeSpan timeout)
    {
        var id = ++_id;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        _process.StandardInput.WriteLine(message.ToJsonString());
        _process.StandardInput.Flush();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = ReadLine(deadline - DateTime.UtcNow);
            if (line is null) break;
            var reply = JsonNode.Parse(line);
            if (reply?["id"]?.GetValue<int>() == id)
            {
                if (reply["error"] is { } error) throw new InvalidOperationException(error["message"]?.ToString() ?? "unknown error");
                return reply["result"];
            }
        }
        throw new TimeoutException($"{method} did not answer within {timeout.TotalSeconds:0} s.");
    }

    private string? ReadLine(TimeSpan timeout)
    {
        var read = Task.Run(() => _process.StandardOutput.ReadLine());
        return read.Wait(timeout) ? read.Result : null;
    }

    private void Notify(string method)
    {
        _process.StandardInput.WriteLine(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToJsonString());
        _process.StandardInput.Flush();
    }

    /// <summary>Connects, opens the document and checks it.</summary>
    public ProbeResult Run(string? documentPath)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var details = new StringBuilder();
        var started = Stopwatch.StartNew();

        var initialize = Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "AutomationML Editor", ["version"] = "1" },
        }, timeout);
        Notify("notifications/initialized");

        var server = initialize?["serverInfo"];
        var tools = Request("tools/list", null, timeout)?["tools"]?.AsArray();
        var toolNames = string.Join(", ", (tools?.Select(t => t?["name"]?.ToString()) ?? []).OrderBy(n => n));
        details.AppendLine($"server: {server?["name"]} {server?["version"]}");
        details.AppendLine($"tools: {toolNames}");

        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return new ProbeResult(false, "No document open", $"{tools?.Count ?? 0} tools ready",
                "Open an AutomationML file in the editor, then test again.", details.ToString(), []);
        }

        var (openText, openData) = Call("open_aml_document", new JsonObject { ["path"] = documentPath }, timeout);
        details.AppendLine().AppendLine("--- open_aml_document").AppendLine(openText);

        var (checkText, checkData) = Call("check_references", new JsonObject(), timeout);
        details.AppendLine().AppendLine("--- check_references").AppendLine(checkText);

        var (_, treeData) = Call("get_tree", new JsonObject { ["depth"] = 4 }, timeout);
        started.Stop();

        var hierarchies = openData?["hierarchies"]?.AsArray();
        var elements = hierarchies?.Sum(h => h?["elements"]?.GetValue<int>() ?? 0) ?? 0;
        var problems = checkData?["problems"]?.AsArray()?.Count ?? 0;
        var notes = checkData?["notes"]?.AsArray()?.Count ?? 0;

        var facts = string.Join("   ·   ", new[]
        {
            $"CAEX {openData?["schemaVersion"]}",
            Size(openData?["sizeBytes"]?.GetValue<long>() ?? 0),
            $"{hierarchies?.Count ?? 0} hierarchies",
            $"{elements} elements",
            $"{openData?["links"]} links",
            $"{openData?["references"]} references",
        });

        var verdict = (problems == 0 ? "no problems found" : $"{problems} problem(s) found")
                      + (notes > 0 ? $"   ·   {notes} note(s)" : "")
                      + string.Format(System.Globalization.CultureInfo.InvariantCulture, "   ·   read in {0:0.0} s", started.Elapsed.TotalSeconds);

        return new ProbeResult(problems == 0, Path.GetFileName(documentPath), facts, verdict, details.ToString(),
            Questions(Path.GetFileName(documentPath), hierarchies, treeData, problems));
    }

    /// <summary>
    /// Questions about this very document: the views it actually has, and the element that
    /// ties the most of them together. A generic question makes for a poor demonstration.
    /// </summary>
    private static List<string> Questions(string fileName, JsonArray? hierarchies, JsonNode? tree, int problems)
    {
        var views = (hierarchies ?? []).Select(h => h?["name"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToList();
        var questions = new List<string>();

        questions.Add(views.Count switch
        {
            0 or 1 => "Tell me what is modelled in the document I have open in the editor.",
            <= 3 => $"In the document open in the editor: explain how {Join(views)} belong together.",
            _ => $"The document open in the editor has {views.Count} views, {Join(views.Take(2).ToList())} among them. How do they belong together?",
        });

        var hotspot = Hotspot(tree?["nodes"]?.AsArray());
        if (hotspot is not null)
            questions.Add($"In the document open in the editor: what is {hotspot} connected to in the other views? Name the IDs so I can check.");
        else if (views.Count > 0)
            questions.Add($"In {fileName}: list the elements in {views[0]} and what each of them is linked to.");

        questions.Add(problems > 0
            ? "Check the open document for broken references and explain what is missing."
            : "Check the open document: does every class path resolve, and is every link complete?");

        return questions;
    }

    /// <summary>The element with the most connections into other hierarchies.</summary>
    private static string? Hotspot(JsonArray? nodes)
    {
        string? best = null;
        var most = 0;
        void Walk(JsonNode? node)
        {
            if (node is null) return;
            var crossing = node["crossHierarchyConnections"]?.GetValue<int>() ?? 0;
            if (crossing > most)
            {
                most = crossing;
                best = node["name"]?.ToString();
            }
            foreach (var child in node["children"]?.AsArray() ?? []) Walk(child);
        }
        foreach (var node in nodes ?? []) Walk(node);
        return best;
    }

    private static string Join(List<string?> names) =>
        names.Count <= 2
            ? string.Join(" and ", names)
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>Asks the server to show an element, and returns what it said.</summary>
    public string ShowInEditor(string element)
    {
        try
        {
            var (text, _) = Call("show_in_editor", new JsonObject { ["element"] = element }, TimeSpan.FromSeconds(30));
            return text.Replace(Environment.NewLine, " | ").Replace("\n", " | ");
        }
        catch (Exception ex)
        {
            return "refused: " + ex.Message;
        }
    }

    /// <summary>Which document the server answers about when none is named.</summary>
    public string AskWithoutNamingADocument()
    {
        var (_, data) = Call("check_references", new JsonObject(), TimeSpan.FromSeconds(30));
        return data?["document"]?.ToString() ?? "(unknown)";
    }

    private (string Text, JsonNode? Data) Call(string tool, JsonNode arguments, TimeSpan timeout)
    {
        var result = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, timeout);
        var text = result?["content"]?.AsArray().FirstOrDefault()?["text"]?.ToString() ?? "(no answer)";
        return (text, result?["structuredContent"]);
    }

    private static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.#} MB" : $"{bytes / 1024.0:0} KB";

    public static string? Version(string exePath)
    {
        try
        {
            var info = new ProcessStartInfo(exePath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return output.Length == 0 ? null : output;
        }
        catch
        {
            return null;
        }
    }

    /// <param name="writable">
    /// Whether the assistant may create new documents in <paramref name="root"/>. Off unless the
    /// user ticks it in the panel: the server writes nothing without a fence, and the fence is
    /// the same directory the panel already reads, so what was written can be read back. Without
    /// a readable directory there is nothing to write into either.
    /// </param>
    public static JsonObject Configuration(string exePath, string? root, bool writable = false)
    {
        var args = new JsonArray();
        if (!string.IsNullOrWhiteSpace(root))
        {
            args.Add("--root");
            args.Add(root);
            if (writable)
            {
                args.Add("--write");
                args.Add(root);
            }
        }
        args.Add("--follow");
        args.Add(PointerFile);
        args.Add("--select");
        args.Add(SelectFile);
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["aml"] = new JsonObject { ["command"] = exePath, ["args"] = args },
            },
        };
    }

    /// <summary>Where Claude Desktop keeps its configuration.</summary>
    public static string ClaudeDesktopConfigFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");

    /// <summary>True when Claude Desktop is configured exactly as this panel would configure it.</summary>
    public static bool ClaudeDesktopIsCurrent(string exePath, string? root, string? configFile = null,
        bool writable = false)
    {
        try
        {
            var path = configFile ?? ClaudeDesktopConfigFile;
            if (!File.Exists(path)) return false;
            var stored = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["mcpServers"]?["aml"];
            var wanted = Configuration(exePath, root, writable)["mcpServers"]!["aml"];
            return stored is not null && JsonNode.DeepEquals(stored, wanted);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the stored entry already allows writing. The panel asks on startup: the checkbox
    /// would otherwise show no permission while the configuration still carries one, and the panel
    /// would then call its own entry out of date and offer to withdraw it unasked.
    /// </summary>
    public static bool ClaudeDesktopMayWrite(string? configFile = null)
    {
        try
        {
            var path = configFile ?? ClaudeDesktopConfigFile;
            if (!File.Exists(path)) return false;
            var args = (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["mcpServers"]?["aml"]?["args"];
            return args is JsonArray stored && stored.Any(a => a?.ToString() == "--write");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes the server into the Claude Desktop configuration, keeping other servers.</summary>
    public static string RegisterWithClaudeDesktop(string exePath, string? root, string? configFile = null,
        bool writable = false)
        => RegisterInFile(exePath, root, configFile ?? ClaudeDesktopConfigFile, writable).Path;

    /// <summary>Where the entry went: the file, the key it went under, and what else is in there.</summary>
    public sealed record ClientEntry(string Path, string Key, int OtherServers);

    /// <summary>
    /// Writes the server into any MCP client's configuration file and keeps everything else in it.
    /// The clients disagree on one thing: most of them, Claude Desktop and Claude Code among them,
    /// list servers under "mcpServers", while VS Code uses "servers" and wants the transport named
    /// in the entry. The file decides which it is; a file that has neither key, and a file that
    /// does not exist yet, get the common shape.
    /// </summary>
    public static ClientEntry RegisterInFile(string exePath, string? root, string configFile,
        bool writable = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configFile)!);

        JsonObject configuration;
        try
        {
            configuration = File.Exists(configFile)
                ? JsonNode.Parse(File.ReadAllText(configFile)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"{configFile} is not valid JSON. Fix or remove the file, then try again.");
        }

        var key = configuration["mcpServers"] is not JsonObject && configuration["servers"] is JsonObject
            ? "servers"
            : "mcpServers";
        if (configuration[key] is not JsonObject servers)
        {
            servers = new JsonObject();
            configuration[key] = servers;
        }

        var entry = (JsonObject)Configuration(exePath, root, writable)["mcpServers"]!["aml"]!.DeepClone();
        if (key == "servers") entry["type"] = "stdio";
        servers["aml"] = entry;

        File.WriteAllText(configFile, configuration.ToJsonString(Pretty));
        return new ClientEntry(configFile, key, servers.Count - 1);
    }
}
