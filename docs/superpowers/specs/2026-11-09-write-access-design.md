# Write access: an assistant generates a whole AutomationML document

Design document, 2026-11-09. Stage 1 of write access for AMLMcp.

## Intent

Today the server only reads. An assistant can explain a document it is given, but it
cannot produce one. Elsewhere in the AutomationML community whole models are already
being generated: Rainer Drath does it with a skill inside ChatGPT, by prompting, and a
complete model comes out the other end.

The point of doing it here is reach. A skill lives inside one product; an MCP server works
with every client that speaks the protocol. That is the one thing this has over the ChatGPT
route, and it decides most of what follows: the path that generates a document must work
without the AutomationML Editor and without Windows, because otherwise half the clients
fall away.

The second thing it has is correctness. A language model writing CAEX gets the same things
wrong every time: it invents IDs, duplicates them, forgets namespaces, writes link partners
in a form that no longer resolves, references class paths that do not exist. This server
already knows all of that, because it reads such documents for a living and reports exactly
those faults. Stage 1 moves that knowledge to the writing side.

## Goals

- An assistant can create a new AutomationML document from a description, through MCP, with
  any client, on Windows, Linux and macOS, with no editor installed.
- The document that comes out resolves: every class path, every link partner, every mirror,
  no duplicate IDs. What `check_references` would call a problem does not get written.
- Before anything is written, the human can see what would be written, in the same words
  `open_aml_document` uses for a document that already exists.
- After writing, the ten read tools work on the new file, so the assistant can check its own
  output and quote IDs and paths for it.
- Writing is off until it is switched on, and the promise that the server does not touch a
  document the user did not offer it stays intact for everybody who does not switch it on.

## Non-goals

- Changing an existing document. That is stage 2, it is decided that it comes, and it belongs
  in the editor with a preview and a confirmation in the panel. Stage 1 does not do it, and
  has to leave the door open for it (see "What stage 1 owes stage 2").
- Writing through the AutomationML Editor. The editor route has real advantages (undo, the
  user watching) and it is where stage 2 goes. For stage 1 it would cost the reach that is
  the whole reason for doing this.
- Geometry, kinematics, COLLADA, AMLX containers as output. A single `.aml` file is the
  output of stage 1; an `.amlx` target path is refused.
- Round-tripping someone else's document: read, modify, write back. That is stage 2 again.

## Anti-goals

Things that would make this a failure even if it technically worked:

- A document that looks like AutomationML and is not. Half-resolved class paths, invented
  library names, links that point nowhere. Worse than no feature, because it damages trust
  in every answer the server gives.
- Writing somewhere the user did not expect, or over a file they still needed.
- A tool that takes raw CAEX XML from the model. Then the model owns correctness again and
  the only thing we have added is a file writer.
- Half a document on disk after an error.

## Constraints

- The server is a .NET program, read-only today, and its read-only promise is written down
  in `README.md` and `SECURITY.md`. Both have to be rewritten as part of this, not quietly
  broken.
- The server parses and renders CAEX itself; there is no Aml.Engine dependency. Writing is
  its own XML output, built with the same `XElement` vocabulary `AmlModel` already uses.
- Reading is fenced with `--root`. Writing gets its own fence, and the two are not the same:
  being allowed to read a directory is not permission to write into it.
- The AutomationML standard libraries are not in this repository and bundling them raises a
  licence question nobody has answered. A generated model that references role and interface
  classes therefore depends on a copy the user already has.
- The MCP clients differ in how many tools they will load and how long they will wait. A
  whole model must not need thirty calls.
- 188 tests on the server and 19 on the plugin currently pass, and a mutation probe showed
  where the suite was thin. New behaviour arrives with tests that would fail without it.

## Decisions made in the conversation

Marked by who made them.

**The user:** reach across all assistants is the point of the feature. The use case is a
whole model from nothing, as in the ChatGPT skill. A conceptual overview before anything is
written was his suggestion. Stage 2, changing a document through the editor, is wanted and
will be built; stage 1 is not a substitute for it.

On the standard libraries he chose the mixed way: when the description names a library, the
document references it; when it names none, the server writes the classes it needs into the
document as libraries of its own, so that a generated document always stands on its own.
Bundling a copy of the AutomationML base libraries stays off the table while the licence
question is open.

**Mine, as my call:**

1. **The server writes the file, not the editor.** Stage 1 only. The plugin may offer to
   open the file afterwards, which gives visibility without putting the editor in the
   writing path. Rationale: reach, and the editor's own services rename on insert and empty
   references on delete, which is a problem stage 2 has to solve and stage 1 need not.
2. **One call writes one document.** Not a sequence of fine-grained edits. A whole model
   over thirty calls is slow, breaks in the middle, and leaves something half-built.
   Fine-grained operations answer a different question, which is editing, which is stage 2.
3. **The model passes a structured description, never XML.** Hierarchies, elements with
   class, roles, attributes and interfaces, and the connections between them. The server
   renders CAEX from it.
4. **The server owns everything a language model gets wrong.** IDs, the namespace, the
   schema version, the writer information, the form link partners are written in, mirror
   references, class paths with an alias, role requirements.
5. **Writing is off by default** and is switched on with its own command line option.
   Without it the server refuses to write and says so.
6. **An existing file is not overwritten** unless the caller says to.
7. **A description that does not render to a resolvable document is refused**, with the
   reason, and nothing is written.
7a. **Every document is validated against the CAEX 3.0 schema before it lands**, and the
   schema travels with the server as an embedded resource. Added on 2026-11-10: stage 1
   first relied on structural tests alone, and a review found six schema violations in
   every written document that those tests did not see. Conformance by construction is not
   conformance that is proved. `hsu-aut/AmlEngine.js` carries the same schema file, which
   settles the question of whether it may be shipped.
8. **Standard libraries are referenced, not shipped**, and a document without a named
   library is written self-contained. The description may name an external library; the
   server writes an `ExternalReference` for it and refuses if it does not resolve. Names it
   none, the server writes the role, interface and system unit classes the description uses
   into the document itself. Either way a generated document opens and checks out on its
   own. (The user's decision; see above.)

## What stage 1 can express

Settled after the builder check; my call, because the effort sits in the schema, not in
these constructs.

In scope, because a model of a plant is not usable without them:

- Several instance hierarchies in one document, nested elements to any depth.
- Class libraries of the document's own: role, system unit, interface and attribute type
  libraries with new classes, including a base class within the same document. A generated
  document must be able to stand on its own when the user has no standard libraries at hand.
- Elements with a system unit class, role requirements and supported roles.
- Attributes with value, default value, unit and nested sub-attributes.
- External interfaces with an interface class.
- InternalLinks between interfaces, within a hierarchy and across hierarchies.
- Mirror objects, and references of the refObj family. They are cheap to render (an ID in
  `RefBaseSystemUnitPath`, an attribute with the reference type) and they are what the
  read side is about: how the views of one plant hang together.

Out of scope for stage 1: anything from the non-goals above, and `SourceObjectInformation`,
revisions and change history.

## The shape of it

One tool. Working name `write_aml_document`, final naming left to the builder.

It takes the target path, the description, and two switches: one that renders and validates
without touching the disk, one that allows replacing an existing file.

It gives back what `open_aml_document` gives back for a document that exists: the overview
(libraries, hierarchies with element counts by class, links, how the hierarchies connect)
followed by the reference check. With the dry switch, that is all it does. Without it, the
file is written first and the overview describes what is now on disk.

Why that output and not a success message: the user already reads that overview and trusts
it. A preview in the same words is a preview they can judge. It also means the assistant
gets the same text whether it previewed or wrote, so it can report the same facts either way.

The four steps from the assistant's side:

1. Build the description from what the user asked for.
2. Call with the dry switch. Show the human the overview and the check.
3. Call again to write.
4. Use the read tools on the new file to confirm, quoting IDs and paths.

### The write fence

My call, in the pattern of `--root` and with the same containment check, including the drive
root case that was fixed there:

- One option, `--write <dir>`, repeatable. It both switches writing on and bounds it. There
  is no second "allow" switch: one thing to get wrong is better than two, and a reader of a
  client configuration can see at a glance where the server may write.
- Without `--write`, nothing is written anywhere. A read fence is never a write permission,
  and a directory given to both has to be named in both.
- Environment variable `AML_MCP_WRITE`, same separator rules as `AML_MCP_ROOTS`.
- The target path is resolved to a full path, symlinks included, and the resolved path has to
  lie inside a write directory. `..` therefore cannot leave it.
- Missing directories below a write directory are created; nothing is created outside one.
- The server prints the writable directories to stderr at startup, next to the readable ones.

### How a write happens

My call, so that nothing can be half written and so that the promise about resolving is kept
by construction:

1. Render the description into XML in memory.
2. Write it to a temporary file beside the target.
3. Load that temporary file with the ordinary reader and run the ordinary check on it.
4. On a blocking finding: delete the temporary file, refuse, and report the finding. Nothing
   else happened.
5. Otherwise move the temporary file onto the target, atomically, the way the editor
   handshake already does it.

A target the editor currently has open through `--follow` is refused even with the overwrite
switch, because the editor would not know.

### What blocks a write

The structured check already separates problems from notes. Problems block: an unresolved
class path, a link without a partner, a dangling reference, a duplicate ID, a mirror without
its master, an alias whose library does not carry the class, an external library that cannot
be loaded. Notes do not block, and are reported with the answer.

That the list is exactly the problems of `check_references` is the point: the server refuses
to write the document it would afterwards complain about.

### IDs

Derived by the server from a stable key, the element's path inside the document plus its
kind, so the same description yields the same document. Renaming an element changes its ID;
for stage 1 that is acceptable, because every write produces a new document and there is no
identity to preserve. The description cannot supply IDs.

Two elements with the same name under the same parent are refused. The read side treats such
a path as ambiguous and refuses to answer about it, so writing one would be writing a
document we cannot talk about afterwards.

### The dry run

Works without `--write`, because it writes nothing. It is the one way a user can see what an
assistant would produce before deciding to grant write access at all.

A real write checks the fence before it renders, so a model that may not write learns that
at once instead of after the work.

## What stage 1 owes stage 2

Stage 2 changes an existing document, through the editor, with a confirmation. It is decided
that it comes, so stage 1 has to be built so that stage 2 is an addition and not a rewrite.
Three seams, my call:

- **The renderer takes a part, not only a whole document.** Rendering one element with its
  attributes, interfaces and links is the same code that renders a hierarchy full of them.
  A renderer that can only produce a complete `CAEXFile` would have to be taken apart again.
- **Validation runs on a document in memory, not only on a file.** Stage 1 may still do it
  by writing a temporary file and reading it back, because that reuses the whole reader. But
  what the check needs is a loaded model, and that entry point is what stage 2 will call
  with the editor's document, where there is no file to read.
- **The channel to the editor exists and is proven.** `show_in_editor` writes a request with
  a nonce, the panel answers in an ack file, the tool reports what the editor said and says
  so honestly when no answer comes. Stage 2 is that pattern with a bigger payload and a
  confirmation step in the panel. Stage 1 must not grow a second, different mechanism for
  talking to the editor.

## What is left to the builder

- The schema of the description: the exact fields, what is required, what defaults, how
  nesting is expressed, how an element names the interface a link should attach to. It has
  to be writable by a language model from a tool description alone, which means few
  required fields and names that match the CAEX vocabulary the model already knows.
- Whether the description is one JSON object or a small set of parameters, and how that plays
  with clients that flatten tool arguments.
- The exact wording of the refusals, in the register of the existing ones.
- The hash and encoding behind the derived IDs.
- Where the overview rendering is shared with the read side, so the two cannot drift apart.
  There is a precedent in the code: text and structured halves are produced from one model,
  with a test that holds them together.
- The tool annotations follow from the behaviour: not read-only, destructive (it can replace
  a file when told to), idempotent (same description and path, same file), not open-world.
- The tests. The existing suites show the register: one fixture per fault, named after the
  behaviour, and the regression tests for defects found in real documents. A small test
  library of role and interface classes is part of the fixtures.
- Not in stage 1: a button in the editor panel for "open what the assistant just wrote". It
  would make one change into two repositories; it waits for stage 2.

## Open for the user

Nothing. The design is approved as it stands. What is still undecided is listed under "What
is left to the builder" and is the builder's to settle.
