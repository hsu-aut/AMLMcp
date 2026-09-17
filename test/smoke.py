# -*- coding: utf-8 -*-
"""Smoke test: talks to aml-mcp over stdio exactly like an MCP client would.

Usage:
    python test/smoke.py path/to/model.aml [more calls as JSON]

The document named first is always opened first, so extra calls find it open.
Without extra calls the script runs a default tour; for the example document
examples/plant.aml every answer is checked against an expectation table, and a
missing expectation fails the run.

An extra call may carry its own expectations and may say that an error is the
expected answer:

    python test/smoke.py examples/plant.aml \\
        '{"name":"find_path","arguments":{"from":"DrillHole","to":"Drilling"},
          "expect":["Path with 2 step(s)"]}'

Environment:
    AML_MCP_EXE              server executable to run
    AML_MCP_SMOKE_TIMEOUT    seconds to wait for one answer (default 60)
"""
import json
import os
import queue
import subprocess
import sys
import threading

HERE = os.path.dirname(os.path.abspath(__file__))
EXAMPLE = "plant.aml"
DEFAULT_TIMEOUT = 60.0


def _find_exe():
    base = os.path.join(HERE, "..", "src", "AmlMcp", "bin", "Release", "net10.0")
    for name in ("aml-mcp.exe", "aml-mcp"):
        candidate = os.path.join(base, name)
        if os.path.exists(candidate):
            return candidate
    return os.path.join(base, "aml-mcp.exe")


EXE = os.environ.get("AML_MCP_EXE") or _find_exe()
TIMEOUT = float(os.environ.get("AML_MCP_SMOKE_TIMEOUT") or DEFAULT_TIMEOUT)


class Timeout(RuntimeError):
    pass


class Client:
    """A minimal MCP client over stdio. Reads in a thread so a hung server cannot block."""

    def __init__(self, exe, timeout=TIMEOUT):
        self.timeout = timeout
        self.proc = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                     stderr=subprocess.DEVNULL, text=True, encoding="utf-8")
        self.next_id = 0
        self._lines = queue.Queue()
        threading.Thread(target=self._pump, daemon=True).start()

    def _pump(self):
        try:
            for line in self.proc.stdout:
                self._lines.put(line)
        finally:
            self._lines.put(None)

    def _readline(self):
        try:
            line = self._lines.get(timeout=self.timeout)
        except queue.Empty:
            raise Timeout(f"no answer from the server within {self.timeout:g}s")
        if line is None:
            raise RuntimeError("server closed the connection")
        return line

    def request(self, method, params=None):
        self.next_id += 1
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        while True:
            reply = json.loads(self._readline())
            if reply.get("id") == self.next_id:
                return reply

    def notify(self, method):
        self.proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method}) + "\n")
        self.proc.stdin.flush()

    def call(self, name, **arguments):
        reply = self.request("tools/call", {"name": name, "arguments": arguments})
        if "error" in reply:
            return True, json.dumps(reply["error"])
        result = reply["result"]
        text = "\n".join(c.get("text", "") for c in result.get("content", []))
        return result.get("isError", False), text

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=10)
        except Exception:
            self.kill()

    def kill(self):
        try:
            self.proc.kill()
        except Exception:
            pass


def opening_call(document):
    """The document is always opened first, whatever else was asked for."""
    expect = []
    if os.path.basename(document).lower() == EXAMPLE:
        expect = ["Document: plant.aml", "CAEX 3.0",
                  "Demo -> DemoLib.aml  [loaded]",
                  "Instance hierarchies (3)",
                  "ProcessView: 2 elements",
                  "BehaviorView: 2 elements",
                  "PlantStructure: 3 elements, 1 of them mirrors",
                  "InternalLinks: 3 total, 2 across hierarchies, 0 unresolved",
                  "PlantStructure <-> ProcessView: 1 link(s)"]
    return {"name": "open_aml_document", "arguments": {"path": document}, "expect": expect}


def default_tour(document):
    """The tour, with what every answer must say about examples/plant.aml.

    The calls that name an element only run for the example document; for any
    other document the tour asks the questions every document can answer.
    """
    known = os.path.basename(document).lower() == EXAMPLE

    def call(name, expect, **arguments):
        return {"name": name, "arguments": arguments, "expect": expect if known else []}

    tour = [
        call("check_references", ["no problems found",
                                  "checked: 6 library classes, 3 links, 0 references, 20 IDs"]),
        call("get_tree", ["ProcessView  [InstanceHierarchy]",
                          "BehaviorView  [InstanceHierarchy]",
                          "PlantStructure  [InstanceHierarchy]",
                          "DrillingStation  [Drill]  <2 cross-hierarchy connection(s)>",
                          "(mirror of PlantStructure/Line/DrillingStation)"]),
        call("find_elements", ["7 match(es):", "ProcessView/DrillHole", "(mirror)"]),
        call("list_classes", ["6 class(es):", "Demo@DemoUnitLib/Drill  (SystemUnitClass)",
                              "AutomationML_ObjectReferences_AttributeTypeLib/refBaseObj  (AttributeType)"]),
    ]
    if not known:
        return tour

    return tour + [
        call("get_element", ["path: PlantStructure/Line/DrillingStation",
                             "class: Demo@DemoUnitLib/Drill",
                             "role: Demo@DemoRoleLib/Station",
                             "SpindleSpeed = 2400 1/min",
                             "Manufacturer = Example Corp  (from Demo@DemoUnitLib/Equipment)",
                             "--link StepToStation",
                             "[other hierarchy: ProcessView]"],
             element="PlantStructure/Line/DrillingStation"),
        call("find_path", ["Path with 2 step(s)", "StepToStation", "StationToBehavior",
                           "BehaviorView/Drilling"],
             **{"from": "DrillHole", "to": "Drilling"}),
        call("get_neighbors", ["Neighbours of PlantStructure/Line/DrillingStation",
                               "hop 1 from DrillingStation", "hop 2 from DrillHole",
                               "<--mirrored by--"],
             element="PlantStructure/Line/DrillingStation", depth=2),
        call("find_elements", ["5 match(es):", "PlantStructure/DrillingStation", "(mirror)"],
             query="Drill"),
        call("get_class", ["Demo@DemoUnitLib/Drill  (SystemUnitClass)",
                           "inherits from: Demo@DemoUnitLib/Equipment",
                           "SpindleSpeed = 3000 1/min (default)",
                           "used by 1 element(s)"],
             classPath="Drill"),
        call("list_classes", ["2 class(es):", "Demo@DemoRoleLib/Station  (RoleClass)",
                              "Demo@DemoRoleLib/Step  (RoleClass)"],
             kind="RoleClass"),
    ]


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    document = sys.argv[1]

    try:
        extra = [json.loads(a) for a in sys.argv[2:]]
    except json.JSONDecodeError as ex:
        print(f"not a JSON call: {ex}")
        return 2

    calls = [opening_call(document)] + (extra if extra else default_tour(document))

    client = Client(EXE)
    failures = []
    try:
        init = client.request("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "aml-mcp-smoke", "version": "0.1"},
        })
        info = init["result"]["serverInfo"]
        print(f"server: {info['name']} {info.get('version')}")
        client.notify("notifications/initialized")

        tools = client.request("tools/list")["result"]["tools"]
        print(f"tools ({len(tools)}): " + ", ".join(t["name"] for t in tools))
        for call in calls:
            if call["name"] not in {t["name"] for t in tools}:
                failures.append(f"{call['name']}: no such tool")

        for call in calls:
            arguments = call.get("arguments", {})
            is_error, text = client.call(call["name"], **arguments)
            print("\n" + "=" * 100)
            print(f"{call['name']} {json.dumps(arguments, ensure_ascii=False)}"
                  + ("   [ERROR]" if is_error else ""))
            print("-" * 100)
            print(text)

            if is_error and not call.get("expectError"):
                failures.append(f"{call['name']}: unexpected error")
                continue
            if call.get("expectError") and not is_error:
                failures.append(f"{call['name']}: expected an error, got an answer")
                continue
            for wanted in call.get("expect", []):
                if wanted not in text:
                    failures.append(f"{call['name']}: missing {wanted!r}")
                    print(f"   MISSING: {wanted!r}")
    except (Timeout, RuntimeError) as ex:
        failures.append(str(ex))
        client.kill()
        print(f"\n{ex}")
    else:
        client.close()

    checked = sum(len(c.get("expect", [])) for c in calls)
    print(f"\n{len(calls)} call(s), {checked} expectation(s) checked, {len(failures)} failure(s)")
    for failure in failures:
        print(f"  FAIL {failure}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
