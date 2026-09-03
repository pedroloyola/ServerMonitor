#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 5: the fourth ring (M71-M75)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
LIFECYCLE = os.path.join(SRC, "Services", "TrayAffordanceLifecycle.cs")
COORDINATOR = os.path.join(SRC, "Services", "WindowCloseCoordinator.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = ("FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout"
          "|FullyQualifiedName~FailSafe|FullyQualifiedName~WindowClose")

MUTATIONS = [
 ("M71", "the commit hands back a permission instead of performing the act", [
   (MACHINE,
    "            if (Project(_state, _time.GetTimestamp()) != TrayAffordanceState.Available)\n            {\n                return false;\n            }\n\n            enterBackground();\n            return true;",
    "            var permitted = Project(_state, _time.GetTimestamp()) == TrayAffordanceState.Available;\n            if (!permitted)\n            {\n                return false;\n            }\n\n            return true;")]),

 ("M72", "the session gate is dropped from the commit", [
   (LIFECYCLE,
    "            if (_degradedForSession)\n            {\n                return false;\n            }\n\n            // The session gate is ours",
    "            if (false)\n            {\n                return false;\n            }\n\n            // The session gate is ours")]),

 ("M73", "the coordinator goes back to gating on a value it read", [
   (COORDINATOR,
    "        if (backgroundSettings.BackgroundMonitoringEnabled\n            && tryEnterBackground(windowController.HideToBackground))\n        {\n            lifecycleController.EnterBackground();",
    "        if (backgroundSettings.BackgroundMonitoringEnabled\n            && tryEnterBackground(() => { }))\n        {\n            windowController.HideToBackground();\n            lifecycleController.EnterBackground();")]),

 ("M74", "an unacknowledged loss is swallowed like any other subscriber failure", [
   (MACHINE,
    "                    if (delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)",
    "                    if (false && delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)")]),

 ("M75", "every subscriber failure escalates, so a noisy observer can end the process", [
   (MACHINE,
    "                    if (delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)",
    "                    if (true || delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable)")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round13.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
