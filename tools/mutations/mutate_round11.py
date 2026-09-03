#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 3 of the Atlas/Vigil corrections (M53-M58)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
ADAPTER = os.path.join(SRC, "Shell", "Tray", "OwnedTrayIconAdapter.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout|FullyQualifiedName~FailSafe"

MUTATIONS = [
 ("M53", "effects are runnable before their transition publishes", [
   (MACHINE,
    "        var effect = new Effect(kind, _generation, ++_sequence, delay);\n        _emittedFrom = _emittedFrom == 0 ? effect.Sequence : _emittedFrom;\n        _emittedTo = effect.Sequence;\n        _pending.Add(new PendingEffect(effect, Ready: false));",
    "        var effect = new Effect(kind, _generation, ++_sequence, delay);\n        _emittedFrom = _emittedFrom == 0 ? effect.Sequence : _emittedFrom;\n        _emittedTo = effect.Sequence;\n        _pending.Add(new PendingEffect(effect, Ready: true));")]),

 ("M54", "the drain SKIPS an unready effect instead of stopping at it", [
   (MACHINE,
    "                    var head = _pending[0];\n                    if (!head.Ready)\n                    {",
    "                    var head = _pending.FirstOrDefault(candidate => candidate.Ready);\n                    if (head.Effect.Sequence == 0)\n                    {")]),

 ("M55", "the check and the invocation stop being one critical section", [
   (MACHINE,
    "                _deliveredSequence = token;\n\n                AtInvocationForTests?.Invoke();\n                StateChanged?.Invoke(this, EventArgs.Empty);\n            }\n        }",
    "                _deliveredSequence = token;\n            }\n\n            AtInvocationForTests?.Invoke();\n            StateChanged?.Invoke(this, EventArgs.Empty);\n        }")]),

 ("M56", "a refused continuation runs inline on the timer thread", [
   (MACHINE,
    "                if (!_marshalToUi(callback))\n                {\n                    _logger.LogWarning(",
    "                if (!_marshalToUi(callback))\n                {\n                    callback();\n                    _logger.LogWarning(")]),

 ("M57", "the adapter refusal falls back to running inline", [
   (ADAPTER,
    "        return dispatcher.TryEnqueue(() => continuation());",
    "        if (dispatcher.TryEnqueue(() => continuation()))\n        {\n            return true;\n        }\n\n        continuation();\n        return true;")]),

 ("M58", "the DPI update bypasses the router and goes straight to the shell", [
   (ADAPTER,
    "        RouteShellUpdate(machine, () => registration.UpdateForDpi(dpi));",
    "        _ = machine;\n        registration.UpdateForDpi(dpi);")]),
]


def test():
    r = subprocess.run(
        f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --filter "{FILTER}" '
        f'--blame-hang --blame-hang-timeout 90s 2>&1',
        shell=True, capture_output=True, text=True, cwd=ROOT)
    out = r.stdout + r.stderr
    if "error CS" in out:
        return ("BUILD-FAIL", 0, 0, out)
    if "Aborted" in out or "crashed" in out:
        return ("ABORTED", 0, 0, out)
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round11.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
