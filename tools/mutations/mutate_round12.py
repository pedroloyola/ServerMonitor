#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 4: the three invariants (M65-M70)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout|FullyQualifiedName~FailSafe"

MUTATIONS = [
 ("M65", "the projection stops reading the clock, so an overdue episode still reads as usable", [
   (MACHINE,
    "        if (_episodeActive\n            && monotonicNow >= _deadlineTimestamp\n            && projected is TrayAffordanceState.Available or TrayAffordanceState.Recovering)",
    "        if (false && _episodeActive\n            && monotonicNow >= _deadlineTimestamp\n            && projected is TrayAffordanceState.Available or TrayAffordanceState.Recovering)")]),

 ("M66", "a refused continuation is abandoned instead of terminalizing the episode", [
   (MACHINE,
    "                Dispatch(new TrayEvent(TrayEventKind.ContinuationRefused, generation, ShellOutcome.NotPerformed));",
    "                _ = generation;")]),

 ("M67", "the refusal is routed through the deadline, which has not passed yet", [
   (MACHINE,
    "                Dispatch(new TrayEvent(TrayEventKind.ContinuationRefused, generation, ShellOutcome.NotPerformed));",
    "                Dispatch(new TrayEvent(TrayEventKind.DeadlineObserved, generation, ShellOutcome.NotPerformed));")]),

 ("M68", "the effect release stops being unconditional", [
   (MACHINE,
    "            try\n            {\n                if (outcome.Publish)\n                {\n                    PublishIfCurrent(outcome);\n                }\n            }\n            finally\n            {",
    "            if (true)\n            {\n                if (outcome.Publish)\n                {\n                    PublishIfCurrent(outcome);\n                }\n            }\n\n            {")]),

 ("M69", "a subscriber exception is allowed to escape into the machine", [
   (MACHINE,
    "                try\n                {\n                    StateChanged?.Invoke(this, EventArgs.Empty);\n                }\n                catch (Exception exception)",
    "                try\n                {\n                    StateChanged?.Invoke(this, EventArgs.Empty);\n                }\n                catch (Exception exception) when (false)")]),

 ("M70", "the delivery-time deadline check moves back BEFORE the probe", [
   (MACHINE,
    "                AtInvocationForTests?.Invoke();\n\n                // Immediately before the event goes out",
    "                if (ProjectState(_state) == TrayAffordanceState.Available\n                    && outcome.Deadline != 0\n                    && _time.GetTimestamp() >= outcome.Deadline)\n                {\n                    return;\n                }\n\n                AtInvocationForTests?.Invoke();\n\n                // MOVED"),
   (MACHINE,
    "                if (ProjectState(_state) == TrayAffordanceState.Available\n                    && outcome.Deadline != 0\n                    && _time.GetTimestamp() >= outcome.Deadline)\n                {\n                    _logger.LogWarning(",
    "                if (false)\n                {\n                    _logger.LogWarning(")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round12.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
