# Security

## Reporting

Report a vulnerability through [private vulnerability reporting](https://github.com/hsu-aut/AMLMcp/security/advisories/new)
on this repository. If that is not possible, open an issue that says a security report is waiting,
without details, and we will arrange a channel.

## What this server does

It reads AutomationML files and answers questions about them over stdio, opens no network
connection, and runs with the rights of the user who started it. It writes in two places, both of
which the operator has to switch on: the file given with `--select`, which holds the element an
editor should show and next to which the editor answers, and a document written with
`write_aml_document` below a directory given with `--write`.

Three things follow from that, and all three are the operator's decision:

**Reading is fenced with `--root`.** Without it the server reads any file the client asks for.
With it, that directory and everything below it is all it will touch, including the libraries an
`ExternalReference` points at and the document an editor points at through `--follow`. Unknown
command line arguments are refused, so a typo cannot widen the fence unnoticed. The server prints
the readable directories to stderr when it starts.

**Writing is off until it is switched on, and fenced separately with `--write`.** Without it
nothing is written at all: an assistant that asks is told that the server may not write, and may ask
for a preview instead, which renders and checks a described document without touching the disk. A
directory the server may read is not a directory it may write into; a directory given to both has to
be named in both. The resolved path is what is checked, so a link inside a write directory that
points out of it does not widen the fence. An existing file is kept unless the caller explicitly asks
for it to be replaced, and the document an editor has open through `--follow` is never replaced. What
is written is proved first: it is rendered, written beside the target, read back with the ordinary
reader and run through the ordinary reference check, and a document with a problem is not written at
all. The server prints the writable directories to stderr when it starts.

**Document content reaches the language model as it is.** Descriptions, names and attribute values
are passed through verbatim. A document from an untrusted source is untrusted input to whatever
model the client uses; treat it that way. XML is parsed with DTD processing disabled: a document that carries a DOCTYPE is refused, so an
external entity is never resolved.

## Supported versions

The latest release. Fixes go into a new version rather than into older ones.
