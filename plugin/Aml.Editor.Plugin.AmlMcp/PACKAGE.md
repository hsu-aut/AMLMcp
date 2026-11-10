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

The assistant then works through eleven tools: an overview of the document, the containment tree, a
search, an element card with every link and reference, neighbours, the path between two elements,
class cards, the class list, and a reference check. Every answer names element IDs and paths.

The tenth goes the other way: **the assistant can select an element in your tree**. Ask which station
performs a step, and instead of leaving you to search an ID, it expands the hierarchy and highlights
the element in the editor. The panel answers every such request, so the assistant can tell you when
the editor is closed or shows another document instead of claiming it pointed at something.

The eleventh writes: **the assistant can create a new document**. Describe a plant or a process and
it builds the CAEX, derives the IDs, and refuses anything that would not resolve. This one is off
until you allow it, see below.

## What it reads

Only what you allow. The panel sets a readable directory, by default the folder of the open
document, and the server refuses everything outside it, including libraries an `ExternalReference`
points at.

Documents are read directly: `.aml` in CAEX 3.0 and CAEX 2.15, and `.amlx` containers. External
libraries, class inheritance, mirror objects and the object reference attributes are resolved.

## What it writes

Nothing, until you tick **Let the assistant create new documents in this directory**. Without the
tick the assistant can still show you what it would write, in the same words the panel uses for a
document that exists, and no file is touched by that.

With the tick it may create new `.aml` files in that one directory and nowhere else. Each one is
rendered by the server, validated against the CAEX 3.0 schema and run through the same reference
check the panel shows you, and a document that fails either is not written at all. An existing file
is kept unless the assistant is told to replace it, and the document you have open in the editor is
never replaced and never changed.

## Installation

PlugIn Manager of the AutomationML Editor (6.4 or later), install "AmlMcp", restart the editor. The
panel appears as a tab.

Source, the server on its own for Windows, Linux and macOS, and the documentation:
[github.com/hsu-aut/AMLMcp](https://github.com/hsu-aut/AMLMcp).

MIT licensed. Developed at the Institute of Automation Technology, Helmut Schmidt University Hamburg.
