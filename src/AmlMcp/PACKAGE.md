# AutomationML.McpServer

An MCP server for AutomationML: it lets a language model client read and navigate CAEX 3.0, CAEX 2.15
and AMLX documents, and create one. It runs locally over stdio, needs no AutomationML Editor, and
writes nothing unless it is given a directory with `--write`.

```bash
dotnet tool install --global AutomationML.McpServer
aml-mcp --help
```

Clients that start MCP server packages themselves can use `dnx`:

```json
{
  "mcpServers": {
    "aml": {
      "command": "dnx",
      "args": ["AutomationML.McpServer", "--", "--root", "C:\\models"]
    }
  }
}
```

`--root <dir>` restricts reading to that directory and everything below it, and may be repeated.
Without it the server reads any file the client asks for. `--write <dir>` is the separate fence for
writing: without it nothing is written at all.

Eleven tools: `open_aml_document`, `get_tree`, `find_elements`, `get_element`, `get_neighbors`,
`find_path`, `get_class`, `list_classes`, `check_references`, `show_in_editor`, which needs the
AutomationML Editor panel and is refused without it, and `write_aml_document`, which creates a
document from a description, validates it against the CAEX 3.0 schema and needs a directory given
with `--write`. Each returns readable text and `structuredContent` next to it.

Documentation and examples: [github.com/hsu-aut/AMLMcp](https://github.com/hsu-aut/AMLMcp).

MIT licensed. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
