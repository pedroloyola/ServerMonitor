#!/usr/bin/env python3
"""M13 S2-T mutation runner, Atlas/Vigil correction round (M46-M52)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
ADAPTER = os.path.join(SRC, "Shell", "Tray", "OwnedTrayIconAdapter.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout|FullyQualifiedName~FailSafe"

MUTATIONS = [
 ("M46", "the dequeue moves back outside the gate, so two drainers can invert the order", [
   (MACHINE,
    "        lock (_nativeGate)\n        {\n            while (true)\n            {\n                Effect effect;\n                lock (_decision)\n                {\n                    if (_pending.Count == 0)\n                    {\n                        return;\n                    }\n\n                    effect = _pending[0];\n                    _pending.RemoveAt(0);",
    "        {\n            while (true)\n            {\n                Effect effect;\n                lock (_decision)\n                {\n                    if (_pending.Count == 0)\n                    {\n                        return;\n                    }\n\n                    effect = _pending[0];\n                    _pending.RemoveAt(0);"),
   (MACHINE,
    "        // The gate is already held by DrainEffects, which owns it for the whole drain.\n        var ok = _executor.Run(operation);",
    "        bool ok;\n        lock (_nativeGate)\n        {\n            ok = _executor.Run(operation);\n        }")]),

 ("M48", "Release no longer dominates at delivery time", [
   (MACHINE,
    "                if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)\n                {\n                    // Release dominates. Suppressed here rather than at decision time, because the\n                    // Release may have won AFTER this delivery was decided.\n                    return;\n                }",
    "                if (false)\n                {\n                    return;\n                }")]),

 ("M49", "a decision taken before the deadline may be delivered as Available after it", [
   (MACHINE,
    "                if (Project(_state) == TrayAffordanceState.Available\n                    && outcome.Deadline != 0\n                    && _time.GetTimestamp() >= outcome.Deadline)",
    "                if (false && Project(_state) == TrayAffordanceState.Available\n                    && outcome.Deadline != 0\n                    && _time.GetTimestamp() >= outcome.Deadline)")]),

 ("M50", "scheduled recovery attempts run wherever the timer fires", [
   (MACHINE,
    "        var timer = _time.CreateTimer(_ => _marshalToUi(callback), null, delay, Timeout.InfiniteTimeSpan);",
    "        var timer = _time.CreateTimer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);")]),

 ("M51", "the DPI update goes straight to the shell, outside the gate", [
   (MACHINE,
    "        lock (_nativeGate)\n        {\n            shellCall();\n        }",
    "        shellCall();")]),

 ("M52", "a new effect kind is added and the coverage test does not notice", [
   (MACHINE,
    "    private enum EffectKind",
    "    private enum EffectKindUnusedMarker { None }\n\n    private enum EffectKind"),
   (MACHINE,
    "            EffectKind.FailSafeExit => (NativeTrayOperation.None, false)",
    "            EffectKind.FailSafeExit => (NativeTrayOperation.None, false),\n            EffectKind.Rogue => (NativeTrayOperation.Add, false)"),
   (MACHINE,
    "        FailSafeExit\n    }",
    "        FailSafeExit,\n        Rogue\n    }")]),
]


def test():
    r = subprocess.run(
        f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --filter "{FILTER}" 2>&1',
        shell=True, capture_output=True, text=True, cwd=ROOT)
    out = r.stdout + r.stderr
    if "error CS" in out:
        return ("BUILD-FAIL", 0, 0, out)
    m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
    if m:
        return ("RAN", int(m.group(1)), int(m.group(2)), out)
    if "Passed!" in out:
        m2 = re.search(r"Passed:\s+(\d+)", out)
        return ("RAN", 0, int(m2.group(1)) if m2 else 0, out)
    return ("UNKNOWN", 0, 0, out)


results = []
which = sys.argv[1:] if len(sys.argv) > 1 else [m[0] for m in MUTATIONS]

for mid, desc, edits in MUTATIONS:
    if mid not in which:
        continue
    originals = {}
    ok = True
    for path, old, new in edits:
        if path not in originals:
            originals[path] = io.open(path, encoding="utf-8-sig").read()
        src = io.open(path, encoding="utf-8-sig").read()
        if old not in src:
            ok = False
            break
        io.open(path, "w", encoding="utf-8", newline="\n").write(src.replace(old, new, 1))
    if not ok:
        for path, original in originals.items():
            io.open(path, "w", encoding="utf-8", newline="\n").write(original)
        print(f"{mid}: ANCHOR NOT FOUND", flush=True)
        results.append({"id": mid, "desc": desc, "status": "ANCHOR-NOT-FOUND"})
        continue
    status, failed, passed, out = test()
    for path, original in originals.items():
        io.open(path, "w", encoding="utf-8", newline="\n").write(original)
    names = sorted(set(re.findall(r"^\s+Failed\s+(\S+)", out, re.M)))
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed,
                    "passed": passed, "tests": names})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)
    for n in names:
        print(f"      {n}", flush=True)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round10.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
