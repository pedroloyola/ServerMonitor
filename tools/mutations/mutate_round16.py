#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 8: the machine owns the operation (M87-M94)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
LIFECYCLE = os.path.join(SRC, "Services", "TrayAffordanceLifecycle.cs")
ADAPTER = os.path.join(SRC, "Shell", "Tray", "OwnedTrayIconAdapter.cs")
COORD = os.path.join(SRC, "Services", "WindowCloseCoordinator.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = ("FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout"
          "|FullyQualifiedName~FailSafe|FullyQualifiedName~WindowClose")

MUTATIONS = [
 # The sixth ring is "the machine owns the operation and decides atomically". Each of these takes one
 # piece of that away.
 ("M87", "the operation is performed OUTSIDE the decision lock, so the interval comes back", [
   (MACHINE,
    "        lock (_decision)\n        {\n            var operations = _operations\n                ?? throw new InvalidOperationException(\n                    \"No guarded operations are registered; the machine has nothing it may perform.\");\n\n            if (Project(_state, _time.GetTimestamp()) != TrayAffordanceState.Available)\n            {\n                operations.FallBackToExit();\n                return;\n            }\n\n            switch (operation)\n            {\n                case TrayGuardedOperation.EnterBackground:\n                    operations.EnterBackground();\n                    break;\n                default:",
    "        ITrayGuardedOperations operations;\n        bool permitted;\n        lock (_decision)\n        {\n            operations = _operations\n                ?? throw new InvalidOperationException(\n                    \"No guarded operations are registered; the machine has nothing it may perform.\");\n            permitted = Project(_state, _time.GetTimestamp()) == TrayAffordanceState.Available;\n        }\n\n        {\n            if (!permitted)\n            {\n                operations.FallBackToExit();\n                return;\n            }\n\n            switch (operation)\n            {\n                case TrayGuardedOperation.EnterBackground:\n                    operations.EnterBackground();\n                    break;\n                default:")]),

 ("M88", "the affordance guard is skipped entirely", [
   (MACHINE,
    "            if (Project(_state, _time.GetTimestamp()) != TrayAffordanceState.Available)\n            {\n                operations.FallBackToExit();\n                return;\n            }",
    "            if (false)\n            {\n                operations.FallBackToExit();\n                return;\n            }")]),

 # The QUIET failure: neither outcome happens, so the window is neither hidden nor closed. This is the
 # A12 zombie reached through the politest possible door, and it is why there is no silent branch.
 ("M89", "a refused operation is silent instead of falling back", [
   (MACHINE,
    "                operations.FallBackToExit();\n                return;",
    "                return;")]),

 ("M90", "the machine's operations slot accepts a second registration", [
   (MACHINE,
    "            if (_operations is not null)\n            {\n                throw new InvalidOperationException(\n                    \"The guarded operations are already registered; there is exactly one set.\");\n            }\n\n            _operations = operations;",
    "            _operations = operations;")]),

 ("M91", "the adapter lets a latecomer displace the guarded operations", [
   (ADAPTER,
    "            if (_operations is not null)\n            {\n                throw new InvalidOperationException(\n                    \"The guarded operations are already registered; there is exactly one set.\");\n            }\n\n            _operations = operations;",
    "            _operations = operations;")]),

 ("M92", "the session gate is dropped from the lifecycle's Perform", [
   (LIFECYCLE,
    "            if (_degradedForSession)\n            {\n                // Our gate refused, so OUR fallback runs.",
    "            if (false)\n            {\n                // Our gate refused, so OUR fallback runs.")]),

 ("M93", "the lifecycle refuses in silence instead of falling back", [
   (LIFECYCLE,
    "                ((ITrayGuardedOperations)this).FallBackToExit();\n                return;",
    "                return;")]),

 # The defect itself, put back: a surface that accepts the caller's own code, which is a place to capture
 # the authorisation and replay it after the affordance is gone.
 ("M94", "a delegate-accepting entry point returns to the affordance surface", [
   (LIFECYCLE,
    "    public void Perform(TrayGuardedOperation operation)",
    "    public void EnterBackground(Action enterBackground)\n    {\n        lock (_sync)\n        {\n            if (!_degradedForSession)\n            {\n                enterBackground();\n            }\n        }\n    }\n\n    public void Perform(TrayGuardedOperation operation)")]),
]


def build():
    r = subprocess.run(
        f'"{DOTNET}" build "{TESTS}" -c Debug -p:Platform=x64 --no-incremental',
        shell=True, capture_output=True, text=True, cwd=ROOT)
    return r.returncode, r.stdout + r.stderr


def test():
    code, out = build()
    if code != 0:
        return ("BUILD-FAIL", 0, 0, out)

    r = subprocess.run(
        f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --no-build --filter "{FILTER}" '
        f'--blame-hang --blame-hang-timeout 90s',
        shell=True, capture_output=True, text=True, cwd=ROOT)
    out += r.stdout + r.stderr

    if "error CS" in out or "error MSB" in out:
        return ("BUILD-FAIL", 0, 0, out)
    if "Aborted" in out or "crashed" in out:
        return ("ABORTED", 0, 0, out)

    total = None
    mt = re.search(r"Total:\s+(\d+)", out)
    if mt:
        total = int(mt.group(1))

    m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
    if not m:
        return ("UNKNOWN", 0, 0, out)

    failed, passed = int(m.group(1)), int(m.group(2))
    if (failed > 0) != (r.returncode != 0):
        return (f"EXIT-MISMATCH(failed={failed} exit={r.returncode})", failed, passed, out)

    return (_verdict("RAN", total), failed, passed, out)


BASELINE_TOTAL = None


def _verdict(status, total):
    if BASELINE_TOTAL is not None and total is not None and total != BASELINE_TOTAL:
        return f"TOTAL-MOVED(total={total} expected={BASELINE_TOTAL})"
    return status


def require_ran(mid, status, failed=None):
    if not status.startswith("RAN"):
        print(f"ABORTING: {mid} did not run -- {status}", flush=True)
        sys.exit(2)
    if failed is not None and failed:
        print(f"ABORTING: {mid} baseline is not green (failed={failed})", flush=True)
        sys.exit(3)


results = []
which = sys.argv[1:] if len(sys.argv) > 1 else [m[0] for m in MUTATIONS]

_b_status, _b_failed, _b_passed, _b_out = test()
_bm = re.search(r"Total:\s+(\d+)", _b_out)
BASELINE_TOTAL = int(_bm.group(1)) if _bm else None
print(f"baseline: status={_b_status} failed={_b_failed} total={BASELINE_TOTAL}", flush=True)
require_ran("baseline", _b_status, _b_failed)

for mid, desc, edits in MUTATIONS:
    if mid not in which:
        continue
    originals = {}
    ok = True
    for path, old, new in edits:
        if path not in originals:
            originals[path] = open(path, "rb").read()
        src = io.open(path, encoding="utf-8-sig").read()
        if old not in src:
            ok = False
            break
        eol = "\r\n" if b"\r\n" in originals[path] else "\n"
        io.open(path, "w", encoding="utf-8", newline=eol).write(src.replace(old, new, 1))
    if not ok:
        for path, original in originals.items():
            open(path, "wb").write(original)
        print(f"{mid}: ANCHOR NOT FOUND", flush=True)
        results.append({"id": mid, "desc": desc, "status": "ANCHOR-NOT-FOUND"})
        sys.exit(5)
    status, failed, passed, out = test()
    for path, original in originals.items():
        open(path, "wb").write(original)
    names = sorted(set(re.findall(r"^\s+Failed\s+(\S+)", out, re.M)))
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed,
                    "passed": passed, "tests": names})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)
    require_ran(mid, status)
    for n in names:
        print(f"      {n}", flush=True)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round16.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
