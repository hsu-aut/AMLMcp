# -*- coding: utf-8 -*-
"""Checks that the editor plugin package carries the server, in the version it claims.

The plugin promises to bring the server with it. A package without it installs and starts,
and only then tells the user that aml-mcp.exe is missing, and a version published to
nuget.org cannot be replaced. Run after building the plugin solution:

    python .github/scripts/check-plugin-package.py
"""
import glob
import os
import re
import subprocess
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PACKAGES = glob.glob(os.path.join(ROOT, "build", "**", "Aml.Editor.Plugin.AmlMcp.*.nupkg"), recursive=True)


def fail(message):
    print("FAIL: " + message)
    sys.exit(1)


if len(PACKAGES) != 1:
    fail("expected exactly one plugin package, found %d: %s" % (len(PACKAGES), PACKAGES))

package = PACKAGES[0]
size = os.path.getsize(package)
print("package: %s (%.1f MB)" % (os.path.basename(package), size / 1024 / 1024))

with zipfile.ZipFile(package) as archive:
    nuspec = next((n for n in archive.namelist() if n.endswith(".nuspec")), None)
    if nuspec is None:
        fail("no nuspec in the package")
    declared = re.search(r"<version>(.*?)</version>", archive.read(nuspec).decode("utf-8"), re.S)
    if declared is None:
        fail("no version in the nuspec")
    version = declared.group(1).strip()

    servers = [n for n in archive.namelist() if n.endswith("runtime/aml-mcp.exe")]
    if not servers:
        fail("the package carries no server: %s" % ", ".join(archive.namelist()))

    with tempfile.TemporaryDirectory() as directory:
        archive.extract(servers[0], directory)
        server = os.path.join(directory, servers[0].replace("/", os.sep))
        print("server:  %s (%.1f MB)" % (servers[0], os.path.getsize(server) / 1024 / 1024))

        if sys.platform != "win32":
            print("ok:      %s is in the package (version not checked, not Windows)" % servers[0])
            sys.exit(0)

        result = subprocess.run([server, "--version"], capture_output=True, text=True, timeout=60)
        reported = result.stdout.strip()
        if result.returncode != 0:
            fail("the packed server does not run: %s %s" % (result.returncode, result.stderr.strip()))
        if reported != version:
            fail("the package says %s, the server inside says %s" % (version, reported))

print("ok:      package %s carries the matching server" % version)
