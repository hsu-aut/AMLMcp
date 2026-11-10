# Changelog

Versions with a tag were released; 0.1.3 and 0.1.7 existed only in the repository, and what they
changed is part of 0.2.0. Anyone coming from 0.1.2 therefore gets everything listed below at once.

## 0.2.0

Not released yet.

### The server writes

- `write_aml_document` creates a CAEX 3.0 document from a structured description: instance
  hierarchies to any depth, class libraries of the document's own, system unit classes, role
  requirements, attributes with units and sub-attributes, external interfaces, InternalLinks within
  and across hierarchies, mirror objects and the refObj family. The description holds no XML and no
  IDs; the server renders the CAEX and derives every ID from the element's path in the document, so
  the same description yields the same document.
- A preview renders, validates and checks a description without touching the disk, and answers in
  the same words `open_aml_document` uses for a document that exists. It works without `--write`.
- Writing is off until it is switched on. `--write <dir>` both switches it on and bounds it, is
  repeatable, and reads from `AML_MCP_WRITE`. A readable directory is not a writable one. The target
  path is resolved before it is checked, so `..` cannot leave the fence, and the server prints the
  writable directories at startup and warns about one it may write but not read.
- Every document is validated against the CAEX 3.0 schema, which travels with the server, and is
  then written beside the target, read back with the ordinary reader and run through the ordinary
  reference check. A document the schema rejects, or the check calls a problem, is not written at
  all. Nothing lands half written: the file is moved onto the target in one step.
- A description that would not resolve is refused with the reason: a class path that leads nowhere, a
  link without a partner, a duplicate name under one parent, two links of one hierarchy with the
  same name, a name with a slash in it, an attribute that carries both a reference and a value, a
  description of nothing but empty hierarchies. An existing file is kept unless the caller asks for
  it to be replaced, and the document an editor has open through `--follow` is never replaced.

### The editor panel

- `show_in_editor` points the editor at an element. It is a handshake: the panel confirms what it
  did, and the tool reports that the editor showed another document, or that none answered, instead
  of claiming success. A request left over from an earlier session is ignored.
- A checkbox next to the readable directory lets the assistant create new documents in that
  directory, by passing it as the write fence as well. It is off until the user ticks it, the panel
  shows the entry it writes, and the document open in the editor is never changed.
- The plugin package carries the matching server, which the release checks before publishing.

### Reading, corrected

- Link partners in brackets, `Ref` attributes written with a capital letter and parallel edges
  between two elements are resolved.
- A bare class name keeps its class, uses are counted apart, and an answer that was cut says so.
- `check_references` reports a declared alias whose library does not carry the class.
- An ambiguous path is refused, naming the elements behind the repeated ID.
- Class library templates no longer count as elements of a hierarchy, and the answer names where
  they sit.
- The element card shows self links, interface and class IDs, and which hierarchies an edge crosses.
- A drive root such as `C:\` is accepted as `--root` instead of fencing out the whole drive.
- A refusal is answered as a refusal instead of logged as a crash, short class kinds are accepted,
  and the log is quiet.
- The class breakdown of the structured overview is capped at six entries, with `distinctClasses`
  next to it, the way the text half was already capped.

## 0.1.2

Security policy.

## 0.1.1

Packaging.

## 0.1.0

First release: the MCP server with nine reading tools over CAEX 3.0, CAEX 2.15 and AMLX documents,
two resources, three prompts, and the AutomationML Editor panel.
