# -*- coding: utf-8 -*-
"""Checks the packaging metadata of this repository.

1. Both `server.json` files validate against the MCP server schema they declare.
2. The two files are byte-identical.
3. The version in both files matches the version the project actually builds with.
   `Directory.Build.props` is the single place that version is written; when the .NET SDK
   is available the check asks MSBuild for the resolved PackageVersion instead of reading
   the file, so a <Version> added to a csproj later cannot slip past.

Usage:
    python .github/scripts/check-metadata.py            # validates online against the declared $schema
    python .github/scripts/check-metadata.py --schema <file>   # validates against a local copy

Exit code 0 when everything agrees, 1 otherwise.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SERVER_JSONS = [os.path.join(ROOT, "server.json"),
                os.path.join(ROOT, "src", "AmlMcp", ".mcp", "server.json")]
PROPS = os.path.join(ROOT, "Directory.Build.props")
PROJECT = os.path.join(ROOT, "src", "AmlMcp", "AmlMcp.csproj")

problems = []


def fail(message):
    problems.append(message)
    print("FAIL: " + message)


def ok(message):
    print("ok:   " + message)


def project_version():
    """The version the package will carry: from MSBuild when it is available, else from the props."""
    if shutil.which("dotnet"):
        try:
            output = subprocess.run(
                ["dotnet", "msbuild", PROJECT, "-getProperty:PackageVersion", "-nologo"],
                capture_output=True, text=True, timeout=300, cwd=ROOT)
            resolved = output.stdout.strip().splitlines()[-1].strip() if output.stdout.strip() else ""
            if output.returncode == 0 and resolved:
                return resolved, "MSBuild PackageVersion of src/AmlMcp/AmlMcp.csproj"
            print("note: could not ask MSBuild for the version, falling back to Directory.Build.props")
        except (OSError, subprocess.SubprocessError, IndexError):
            print("note: could not ask MSBuild for the version, falling back to Directory.Build.props")

    with open(PROPS, encoding="utf-8") as f:
        text = f.read()
    match = re.search(r"<VersionPrefix>\s*([^<\s]+)\s*</VersionPrefix>", text)
    if not match:
        fail("Directory.Build.props declares no <VersionPrefix>")
        return None, None
    return match.group(1), "<VersionPrefix> in Directory.Build.props"


def load_schema(path_or_none, url):
    if path_or_none:
        with open(path_or_none, encoding="utf-8") as f:
            return json.load(f)
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--schema", help="local copy of the MCP server schema")
    args = parser.parse_args()

    try:
        import jsonschema
    except ImportError:
        print("this check needs the jsonschema package: pip install jsonschema")
        return 2

    expected, source = project_version()
    if expected:
        ok(f"project version {expected} ({source})")

    blobs = []
    for path in SERVER_JSONS:
        with open(path, "rb") as f:
            blobs.append(f.read())

    if blobs[0] != blobs[1]:
        fail("server.json and src/AmlMcp/.mcp/server.json are not byte-identical")
    else:
        ok("both server.json files are byte-identical")

    for path, blob in zip(SERVER_JSONS, blobs):
        relative = os.path.relpath(path, ROOT)
        document = json.loads(blob.decode("utf-8"))
        schema = load_schema(args.schema, document["$schema"])
        errors = sorted(jsonschema.Draft7Validator(schema).iter_errors(document),
                        key=lambda e: list(e.path))
        if errors:
            for error in errors:
                location = "/".join(str(p) for p in error.path) or "(root)"
                fail(f"{relative}: {location}: {error.message}")
        else:
            ok(f"{relative} validates against {document['$schema']}")

        if expected:
            versions = {"version": document.get("version")}
            for index, package in enumerate(document.get("packages", [])):
                versions[f"packages[{index}].version"] = package.get("version")
            for where, value in versions.items():
                if value != expected:
                    fail(f"{relative}: {where} is {value!r}, expected {expected!r} "
                         f"from {source}")
            if all(value == expected for value in versions.values()):
                ok(f"{relative}: all {len(versions)} version field(s) are {expected}")

    print()
    print("metadata check failed" if problems else "metadata check passed")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
