# AMLMcp

An MCP server for AutomationML. It lets a language model client read and navigate CAEX 3.0, CAEX 2.15
and AMLX documents without loading the XML into its context.

- Nine tools over one open document: overview, tree, search, element card, neighbours, path, class
  card, class list, reference check
- Resolves external libraries, class inheritance, mirror objects and object references
- Every answer names IDs and paths, so a client can check each statement in the model

It knows CAEX, not any particular domain library, so it works the same on a plant structure, a
process description, a behaviour model, or all three in one document.

## Repository

| Path | Content |
|---|---|
| `src/AmlMcp/` | The server, `aml-mcp` |
| `test/AmlMcp.Tests/` | 113 tests |
| `test/smoke.py` | Protocol level test, speaks MCP over stdio |
| `examples/` | Four small documents to try it on |

## Build

Requires the .NET 10 SDK.

```bash
dotnet build src/AmlMcp -c Release
dotnet test AmlMcp.sln
```

The executable is `src/AmlMcp/bin/Release/net10.0/aml-mcp` (`.exe` on Windows). With the first release
it is also a NuGet tool, `dotnet tool install --global AutomationML.McpServer`, and a self contained
binary for Windows, Linux and macOS.

## Connect a client

```bash
claude mcp add aml -s user -- /path/to/aml-mcp --root /path/to/your/models
```

On Windows the `claude` command is a PowerShell script, and PowerShell eats the `--` before the CLI
sees it. Go through `cmd` there:

```powershell
cmd /c 'claude mcp add aml -s user -- "C:\path\to\aml-mcp.exe" --root C:\models'
```

Other clients take the same command in their configuration file:

```json
{
  "mcpServers": {
    "aml": {
      "command": "aml-mcp",
      "args": ["--root", "C:\\models"]
    }
  }
}
```

## Tools

| Tool | Answers |
|---|---|
| `open_aml_document` | What is in this file: container parts, external libraries and whether they resolve, class libraries, hierarchies with element counts, and how the hierarchies are connected |
| `get_tree` | The containment tree of a hierarchy or element |
| `find_elements` | Elements by text, class or role; class and role filters follow inheritance |
| `get_element` | One element: class, roles, own and inherited attributes, interfaces, children, and every link, reference and mirror relation in both directions |
| `get_neighbors` | What an element is connected to, up to four hops, optionally only across hierarchies |
| `find_path` | The shortest chain of connections between two elements |
| `get_class` | One class: inheritance, attributes with defaults, interfaces, subclasses, how often it is used |
| `list_classes` | The classes of all libraries available to the document |
| `check_references` | Class paths that do not resolve, missing libraries, links without partners, dangling references, mirrors without master, duplicate IDs |

Each tool returns text for the model and `structuredContent` for programs; the record types are in
[`src/AmlMcp/Dto.cs`](src/AmlMcp/Dto.cs). There are two resources, `aml://documents` and
`aml://libraries`, and three prompts, `explain_document`, `trace_to_plant` and `review_document`.

## Options

| Option | Effect |
|---|---|
| `--root <dir>` | Read only below this directory, repeatable. Also applies to the libraries an `ExternalReference` points at. Environment: `AML_MCP_ROOTS` |
| `--strict-conventions` | Interpret only what CAEX and the shared libraries define, without the attribute name conventions of older libraries. Environment: `AML_MCP_STRICT=1` |
| `--help`, `--version` | Print usage or version |

Unknown arguments are refused, so a typo such as `--roots` cannot silently start a server without a
restriction. Without `--root` the server reads whatever the user who started it can read.

## What it understands

- **CAEX 2.15** documents have no XML namespace and write link partners as `ID:Interface` or
  `Path:Interface`; both forms resolve.
- **AMLX containers** are recognised by content, opened through their root document relationship, and
  external references resolve inside the package. The check reports a container that is not self contained.
- **External libraries** are loaded from disk next to the document, nested references included. A
  missing library is reported once, with the class paths it takes down with it.
- **Class inheritance**: attributes an element does not set itself are shown with their defaults and the
  class they come from.
- **Mirror objects** are navigable in both directions and are not reported as broken class paths.
- **Layout** attributes (bounds, waypoints, port coordinates) are hidden unless asked for.

Graph traversal happens in the server, not in the model, which keeps small local models usable.

## Notes

The server only reads. XML is parsed with DTD processing disabled. Descriptions and attribute values
reach the language model as they are, so treat documents from untrusted sources accordingly.

## Related

[AMLFPB.js](https://github.com/hsu-aut/AMLFPB.js) and
[AMLPetriNet](https://github.com/hsu-aut/AMLPetriNet) edit VDI/VDE 3682 process descriptions and
Petri nets inside AML documents; their domain libraries are in
[aml-graphical-description-languages](https://github.com/hsu-aut/aml-graphical-description-languages).

## Licence

MIT. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
