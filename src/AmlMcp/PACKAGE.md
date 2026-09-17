# AutomationML.McpServer

An MCP server for AutomationML: it lets a language model client read and navigate CAEX 3.0, CAEX 2.15
and AMLX documents. It runs locally over stdio and only reads files.

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
Without it the server reads any file the client asks for.

Nine tools: `open_aml_document`, `get_tree`, `find_elements`, `get_element`, `get_neighbors`,
`find_path`, `get_class`, `list_classes`, `check_references`. Each returns readable text and
`structuredContent` next to it.

Documentation and examples: [github.com/hsu-aut/AMLMcp](https://github.com/hsu-aut/AMLMcp).

MIT licensed. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
