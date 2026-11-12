# AMLMcp

An MCP server for AutomationML. It lets a language model client read and navigate CAEX 3.0, CAEX 2.15
and AMLX documents without loading the XML into its context, and create one.

- Eleven tools over one open document: overview, tree, search, element card, neighbours, path, class
  card, class list, reference check, show it in the editor, and write a new document
- Resolves external libraries, class inheritance, mirror objects and object references
- Every answer names IDs and paths, so a client can check each statement in the model

It knows CAEX, not any particular domain library, so it works the same on a plant structure, a
process description, a behaviour model, or all three in one document. The server is a program of its
own and runs on Windows, Linux and macOS, with no editor installed. For those who work in the
AutomationML Editor there is a panel in `plugin/` that carries the server, registers it with an
assistant and points it at the document in the editor.

## Repository

| Path | Content |
|---|---|
| `src/AmlMcp/` | The server, `aml-mcp` |
| `test/AmlMcp.Tests/` | The test suite |
| `test/smoke.py` | Protocol level test, speaks MCP over stdio |
| `examples/` | Four small documents to try it on |
| `plugin/` | AutomationML Editor panel that ships the server and registers it with an assistant (Windows), with its own tests |
| `CHANGELOG.md` | What changed in each version |

## Build

Requires the .NET 10 SDK.

```bash
dotnet build src/AmlMcp -c Release
dotnet test AmlMcp.sln
```

The executable is `src/AmlMcp/bin/Release/net10.0/aml-mcp` (`.exe` on Windows).

Ready made instead of built: every [release](https://github.com/hsu-aut/AMLMcp/releases) carries a self
contained binary for Windows (x64), Linux (x64) and macOS (x64 and arm64), and the editor panel. As a NuGet tool
(`dotnet tool install --global AutomationML.McpServer`) it follows once the package is published.

## Connect a client

```bash
claude mcp add aml -s user -- /path/to/aml-mcp --root /path/to/your/models
```

On Windows the `claude` command is a PowerShell script, and PowerShell eats the `--` before the CLI
sees it. Go through `cmd` there:

```powershell
cmd /c 'claude mcp add aml -s user -- "C:\path\to\aml-mcp.exe" --root C:\models'
```

Then ask a question. The first call opens a document, the rest work on it:

> Open examples/plant.aml. Which station carries out the step DrillHole, and how is it tied to the
> behaviour view?

Paths are resolved against the working directory of the server process, which the client decides, so
give absolute ones unless you set `--root` and know where the client starts the server.

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

## AutomationML Editor

The panel in `plugin/` carries the server, registers it with Claude Desktop in one click, and reads the
document that is open in the editor. It keeps `--follow` up to date, so the assistant answers about
whatever document you open there, and it watches `--select`, so the assistant can expand and highlight
an element in the tree. A checkbox next to the readable directory passes it as `--write` as well, so
the assistant may create new documents there; it is off until the user ticks it, and the open
document is never changed either way. Claude Desktop is registered in one click; every other MCP
client is registered by pointing the panel at its configuration file, which it writes under the key
that file already uses. Build it on Windows with the .NET 10 SDK; the server is published into the
plugin as part of that build:

```powershell
dotnet build plugin\AmlMcp.Editor.sln -c Release
```

Then add `build\Plugins\Aml.Editor.Plugin.AmlMcp\Release` as a source in the PlugIn Manager of the
editor and install "AmlMcp" from there. Published releases carry the same package, and once it is on
nuget.org the PlugIn Manager finds it by itself. The editor is not needed for anything else here.

## Tools

| Tool | Answers |
|---|---|
| `open_aml_document` | What is in this file: container parts, external libraries and whether they resolve, class libraries, hierarchies with element counts, and how the hierarchies are connected. Without a path it reads the document the editor has open |
| `get_tree` | The containment tree of a hierarchy or element |
| `find_elements` | Elements by text, class or role; class and role filters follow inheritance. It searches the instance hierarchies; matches inside the templates of system unit classes are counted, not listed. For the classes themselves use `list_classes` |
| `get_element` | One element: class, roles, own and inherited attributes, interfaces, children, and every link, reference and mirror relation in both directions. An interface ID answers about its owner and says so |
| `get_neighbors` | What an element is connected to, up to four hops, optionally only across hierarchies |
| `find_path` | The shortest chain of connections between two elements |
| `get_class` | One class: inheritance, attributes with defaults, interfaces, subclasses, how often it is used |
| `list_classes` | The classes of all libraries available to the document |
| `check_references` | Class paths that do not resolve, missing libraries, an alias whose library does not carry the class, links without partners, dangling references, mirrors without master, duplicate IDs with the elements behind them |
| `show_in_editor` | Expands and selects one element in the AutomationML Editor the user is working in, so an answer becomes a place in their tree. The editor confirms; without an answer the tool reports that nothing was shown |
| `write_aml_document` | Creates a document from a description, not from XML: the server renders the CAEX, generates every ID, writes the link partners in the form that resolves and adds the classes a description uses when it names no library. With `preview` it renders and checks without writing. It refuses a description that would not resolve, and writes nothing in that case |

Each tool returns text for the model and `structuredContent` for programs; the record types are in
[`src/AmlMcp/Dto.cs`](src/AmlMcp/Dto.cs). There are two resources, `aml://documents` and
`aml://libraries`, and three prompts, `explain_document`, `trace_to_plant` and `review_document`.

## Options

| Option | Effect |
|---|---|
| `--root <dir>` | Read only below this directory, repeatable. Also applies to the libraries an `ExternalReference` points at. Environment: `AML_MCP_ROOTS`, several directories separated by `;` on Windows and `:` elsewhere |
| `--follow <file>` | Answer about the document whose path this file holds, as long as the client names none. The editor panel keeps it up to date, so switching documents needs no restart. Environment: `AML_MCP_FOLLOW` |
| `--select <file>` | Where `show_in_editor` writes its request, as JSON with `nonce`, `document`, `id` and `path`. The editor panel watches the file, selects the element and answers in `<file>.ack`. Environment: `AML_MCP_SELECT` |
| `--write <dir>` | Allow writing below this directory, repeatable. Without it nothing is written. A readable directory is not a writable one, and a writable directory should be named in `--root` too, or the read tools refuse what was written there. The server warns at startup when it is not. Environment: `AML_MCP_WRITE`, several directories separated by `;` on Windows and `:` elsewhere |
| `--strict-conventions` | Interpret only what CAEX and the shared libraries define. Without it an attribute named `refSomething` whose value is an ID counts as a reference, and `ViewInformation`, `PortCoordinate`, `LabelOffset` and `Waypoint...` count as layout. Environment: `AML_MCP_STRICT=1` |
| `--help`, `--version` | Print usage or version |

Unknown arguments are refused, so a typo such as `--roots` cannot silently start a server without a
restriction. A directory that does not exist is accepted, though, and then nothing is readable: the
server prints the line `aml-mcp: readable directories: ...` to stderr when it starts, which is where
a mistyped path shows. Without `--root` the server reads whatever the user who started it can read.

## What it understands

- **CAEX 2.15** documents have no XML namespace and write link partners as `ID:Interface` or
  `Path:Interface`; both forms resolve.
- **AMLX containers** are recognised by content, opened through their root document relationship, and
  external references resolve inside the package. The check reports a container that is not self contained.
- **External libraries** are loaded from disk next to the document, nested references included. A
  missing library is reported once, with the class paths it takes down with it.
- **Class inheritance**: attributes an element does not set itself are shown with their defaults and the
  class they come from.
- **Mirror objects** are navigable in both directions and are not reported as broken class paths. A base
  class written without its library is a class, not a mirror; the check says which library is missing.
- **Names are not unique** in CAEX. A path that leads to two elements is refused with both IDs, and an
  ID used twice names every element that carries it.
- **Layout** attributes (bounds, waypoints, port coordinates) are hidden unless asked for.
- **A written document** is proved before it lands: validated against the CAEX 3.0 schema the server
  carries, then written beside the target, read back with the same reader and run through the same
  check. A document the schema rejects, or that `check_references` would call a problem, is never
  written.

Graph traversal happens in the server, not in the model, which keeps small local models usable.

## Notes

The server writes only where it was allowed to: the file given with `--select`, to tell an editor
which element to show, and a document below a directory given with `--write`. Without `--write`
nothing is written, an existing file is kept unless the caller asks for it to be replaced, and the
document an editor has open is never replaced. XML is parsed with DTD processing disabled, so a
document carrying a DOCTYPE is refused and an external entity is never resolved. Descriptions and attribute values reach the language model as they
are, so treat documents from untrusted sources accordingly.

## Related

[AMLFPB.js](https://github.com/hsu-aut/AMLFPB.js) and
[AMLPetriNet](https://github.com/hsu-aut/AMLPetriNet) edit VDI/VDE 3682 process descriptions and
Petri nets inside AML documents; their domain libraries are in
[aml-graphical-description-languages](https://github.com/hsu-aut/aml-graphical-description-languages).

## Licence

MIT. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
