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

 # M69 RETIRED in round 7, and it is the M49/M70 lesson a third time: do not re-anchor a mutation that
 # has become a DUPLICATE of another one. Round 7 gave every observer its own catch, and from that moment
 # "an observer exception escapes into the machine" and "the observers are not isolated" are the same
 # edit -- there is only one catch left to disable. Re-anchoring it would have produced two rows for one
 # property, which is how M49 and M70 ended up covering for each other.
 #
 # M83 inherits it, and the inheritance was VERIFIED rather than assumed: M83 is killed by
 # A_subscriber_that_throws_cannot_block_the_compensating_delete, which is precisely M69's property --
 # foreign code must not decide whether the machine's own bookkeeping completes.


 # M70 RETIRED for the same reason as M49, and it is the same defect seen from the other side: M70 moved
 # the check EARLIER, and by round 6 that no longer weakened anything, because the copy in front of each
 # handler still enforced it. Both mutations were measuring a duplicate. The duplicate is gone; M78 and
 # M79 attack the single remaining statement of the rule.
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
    # A STALE RUN MUST NOT BE READABLE AS A SURVIVAL. A mutation never changes which tests exist, so the
    # Total is invariant: if it moves, the build did not land (a locked test DLL silently fails the build
    # with MSB3021 and the previous assembly runs instead) and "failed=0" would mean nothing at all. This
    # has cost three rounds; it is now checked rather than remembered.
    total = None
    mt = re.search(r"Total:\s+(\d+)", out)
    if mt:
        total = int(mt.group(1))

    m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
    if m:
        return (_verdict("RAN", total), int(m.group(1)), int(m.group(2)), out)
    if "Passed!" in out:
        m2 = re.search(r"Passed:\s+(\d+)", out)
        return (_verdict("RAN", total), 0, int(m2.group(1)) if m2 else 0, out)
    return ("UNKNOWN", 0, 0, out)


BASELINE_TOTAL = None


def _verdict(status, total):
    if BASELINE_TOTAL is not None and total is not None and total != BASELINE_TOTAL:
        return f"STALE-ASSEMBLY(total={total} expected={BASELINE_TOTAL})"
    return status


results = []
which = sys.argv[1:] if len(sys.argv) > 1 else [m[0] for m in MUTATIONS]

# The unmutated total, measured before anything is touched, so every row below can be checked against it.
_b_status, _b_failed, _b_passed, _b_out = test()
_bm = re.search(r"Total:\s+(\d+)", _b_out)
BASELINE_TOTAL = int(_bm.group(1)) if _bm else None
print(f"baseline: status={_b_status} failed={_b_failed} total={BASELINE_TOTAL}", flush=True)
if _b_failed:
    print("BASELINE IS NOT GREEN -- every row below is meaningless until it is", flush=True)

for mid, desc, edits in MUTATIONS:
    if mid not in which:
        continue
    # BYTE-EXACT RESTORE. Restoring through a text write with newline="\n" rewrote CRLF sources as LF,
    # so every run left three files "modified" in git with a "LF will be replaced by CRLF" warning --
    # tree noise produced by the measuring instrument. Originals are now kept as raw bytes and put back
    # unchanged, and the mutated write reuses whatever line ending the file already had.
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
        continue
    status, failed, passed, out = test()
    for path, original in originals.items():
        open(path, "wb").write(original)
    names = sorted(set(re.findall(r"^\s+Failed\s+(\S+)", out, re.M)))
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed,
                    "passed": passed, "tests": names})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)
    for n in names:
        print(f"      {n}", flush=True)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round12.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
