# Security

## Reporting

Report a vulnerability through [private vulnerability reporting](https://github.com/hsu-aut/AMLMcp/security/advisories/new)
on this repository. If that is not possible, open an issue that says a security report is waiting,
without details, and we will arrange a channel.

## What this server does

It reads AutomationML files and answers questions about them over stdio. It never writes to a
document, opens no network connection, and runs with the rights of the user who started it.

Two things follow from that, and both are the operator's decision:

**Reading is fenced with `--root`.** Without it the server reads any file the client asks for.
With it, that directory and everything below it is all it will touch, including the libraries an
`ExternalReference` points at and the document an editor points at through `--follow`. Unknown
command line arguments are refused, so a typo cannot widen the fence unnoticed. The server prints
the readable directories to stderr when it starts.

**Document content reaches the language model as it is.** Descriptions, names and attribute values
are passed through verbatim. A document from an untrusted source is untrusted input to whatever
model the client uses; treat it that way. XML is parsed with DTD processing disabled, so external
entities are not resolved.

## Supported versions

The latest release. Fixes go into a new version rather than into older ones.
