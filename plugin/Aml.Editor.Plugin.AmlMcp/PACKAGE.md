# AmlMcp for the AutomationML Editor

Makes the document you have open in the editor readable for an AI assistant, and keeps the two in
step: open another file in the editor, and the assistant answers about that one, with no restart.

The panel carries the server, so nothing else has to be installed.

## What the panel does

- **Read the open document.** Starts the server, reads the file that is open in the editor and
  reports what is in it: views, elements, links, references, and whether everything resolves.
- **Register with Claude Desktop.** Writes the entry into the assistant's configuration, keeping any
  other servers that are already there.
- **Copy configuration.** The same entry as JSON, for any other MCP client.
- **Questions to try.** Three questions about the document you just read, with the names of its own
  views and of the element that ties the most of them together.

The assistant then works through nine tools: an overview of the document, the containment tree, a
search, an element card with every link and reference, neighbours, the path between two elements,
class cards, the class list, and a reference check. Every answer names element IDs and paths, so any
statement can be looked up in the editor's tree.

## What it reads

Only what you allow. The panel sets a readable directory, by default the folder of the open
document, and the server refuses everything outside it, including libraries an `ExternalReference`
points at. It reads; it never writes to your files.

Documents are read directly: `.aml` in CAEX 3.0 and CAEX 2.15, and `.amlx` containers. External
libraries, class inheritance, mirror objects and the object reference attributes are resolved.

## Installation

PlugIn Manager of the AutomationML Editor (6.4 or later), install "AmlMcp", restart the editor. The
panel appears as a tab.

Source, the server on its own for Windows, Linux and macOS, and the documentation:
[github.com/hsu-aut/AMLMcp](https://github.com/hsu-aut/AMLMcp).

MIT licensed. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
