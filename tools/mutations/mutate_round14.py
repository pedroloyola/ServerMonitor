#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 6: the fifth ring (M76-M79)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
CONTRACT = os.path.join(SRC, "Services", "ITrayAffordanceSource.cs")
LIFECYCLE = os.path.join(SRC, "Services", "TrayAffordanceLifecycle.cs")
ADAPTER = os.path.join(SRC, "Shell", "Tray", "OwnedTrayIconAdapter.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = ("FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout"
          "|FullyQualifiedName~FailSafe|FullyQualifiedName~WindowClose")

MUTATIONS = [
  # M76 keeps its original form as a NOTE, not as a run: adding the return type back to the interface
 # does not compile, because every test double implements void. That is a stronger outcome than a failing
 # test — the compiler refuses the shape — but it is not a mutation result, so it is not counted as a kill.
 # M76 below is the mutation that CAN compile: a readable permission comes back beside the commit, which
 # is exactly how the defect was reintroduced last time (the name went, the value stayed).
 ("M76", "a readable permission returns beside the commit", [
   (LIFECYCLE,
    "    public void EnterBackground(Action enterBackground)",
    "    public bool CanEnterBackground\n    {\n        get { lock (_sync) { return !_degradedForSession; } }\n    }\n\n    public void EnterBackground(Action enterBackground)")]),

 ("M77", "the multicast delivery is validated once instead of per subscriber", [
   (MACHINE,
    "                    foreach (var handler in StateChanged?.GetInvocationList() ?? [])\n                    {\n                        if (!IsStillDeliverable(outcome))",
    "                    foreach (var handler in StateChanged?.GetInvocationList() ?? [])\n                    {\n                        if (false)")]),

 ("M78", "the per-subscriber revalidation ignores the deadline", [
   (MACHINE,
    "        return ProjectState(_state) != TrayAffordanceState.Available\n               || outcome.Deadline == 0\n               || _time.GetTimestamp() < outcome.Deadline;",
    "        return true;")]),

 ("M79", "the per-subscriber revalidation ignores a Release", [
   (MACHINE,
    "        if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)\n        {\n            return false;\n        }\n\n        return ProjectState(_state) != TrayAffordanceState.Available",
    "        if (false)\n        {\n            return false;\n        }\n\n        return ProjectState(_state) != TrayAffordanceState.Available")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round14.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
