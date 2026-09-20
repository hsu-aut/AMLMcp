// A very small MCP client: starts the server, speaks the protocol over stdio and
// reports what came back. Enough to prove the connection from inside the editor.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Aml.Editor.Plugin.AmlMcp;

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

    /// <summary>Opens the document and returns what the server answered, line by line.</summary>
    public IEnumerable<string> Run(string? documentPath)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var initialize = Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "AutomationML Editor", ["version"] = "1" },
        }, timeout);
        Notify("notifications/initialized");

        var server = initialize?["serverInfo"];
        yield return $"connected to {server?["name"]} {server?["version"]}";

        var tools = Request("tools/list", null, timeout)?["tools"]?.AsArray();
        yield return $"{tools?.Count ?? 0} tools: {string.Join(", ", tools?.Select(t => t?["name"]?.ToString()) ?? [])}";

        if (string.IsNullOrWhiteSpace(documentPath))
        {
            yield return "";
            yield return "No document is open in the editor, so nothing was read.";
            yield break;
        }

        foreach (var line in Call("open_aml_document", new JsonObject { ["path"] = documentPath }, timeout)) yield return line;
        foreach (var line in Call("check_references", new JsonObject(), timeout)) yield return line;
    }

    private IEnumerable<string> Call(string tool, JsonNode arguments, TimeSpan timeout)
    {
        yield return "";
        yield return $"--- {tool}";
        var result = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, timeout);
        var text = result?["content"]?.AsArray().FirstOrDefault()?["text"]?.ToString() ?? "(no answer)";
        foreach (var line in text.Split('\n')) yield return line.TrimEnd('\r');
    }

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
