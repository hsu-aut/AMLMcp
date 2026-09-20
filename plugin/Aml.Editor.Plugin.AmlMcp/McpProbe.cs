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
internal sealed record ProbeResult(bool Ok, string Headline, string Facts, string Verdict, string Details);

internal sealed class McpProbe : IDisposable
{
    /// <summary>Writing a JsonNode with options needs a resolver, or it throws at run time.</summary>
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

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
                "Open an AutomationML file in the editor, then test again.", details.ToString());
        }

        var (openText, openData) = Call("open_aml_document", new JsonObject { ["path"] = documentPath }, timeout);
        details.AppendLine().AppendLine("--- open_aml_document").AppendLine(openText);

        var (checkText, checkData) = Call("check_references", new JsonObject(), timeout);
        details.AppendLine().AppendLine("--- check_references").AppendLine(checkText);
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

        return new ProbeResult(problems == 0, Path.GetFileName(documentPath), facts, verdict, details.ToString());
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

    public static JsonObject Configuration(string exePath, string? root)
    {
        var args = new JsonArray();
        if (!string.IsNullOrWhiteSpace(root))
        {
            args.Add("--root");
            args.Add(root);
        }
        return new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["aml"] = new JsonObject { ["command"] = exePath, ["args"] = args },
            },
        };
    }

    /// <summary>Writes the server into the Claude Desktop configuration, keeping other servers.</summary>
    public static string RegisterWithClaudeDesktop(string exePath, string? root)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject configuration;
        try
        {
            configuration = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"{path} is not valid JSON. Fix or remove the file, then try again.");
        }

        if (configuration["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            configuration["mcpServers"] = servers;
        }
        servers["aml"] = Configuration(exePath, root)["mcpServers"]!["aml"]!.DeepClone();

        File.WriteAllText(path, configuration.ToJsonString(Pretty));
        return path;
    }
}
