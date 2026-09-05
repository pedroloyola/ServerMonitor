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
 # M71 re-anchored in round 8: the commit takes a VALUE and performs an operation the machine owns, so
 # "decide and then do not perform" is expressed against that. The property is unchanged -- the decision
 # and the act are one step -- and it is distinct from M88 (no guard at all) and M89 (refuse in silence).
 ("M71", "the commit decides and then does not perform the act", [
   (MACHINE,
    "                case TrayGuardedOperation.EnterBackground:\n                    operations.EnterBackground();\n                    break;",
    "                case TrayGuardedOperation.EnterBackground:\n                    break;")]),

 # M72 RETIRED in round 8, and for the reason M49/M70 and M69 were retired: it became a DUPLICATE.
 # "the session gate is dropped from the commit" is now the same edit as M92, because the lifecycle has
 # exactly one gate and one place to drop it. Verified rather than assumed: M92 is killed by
 # A_recovered_affordance_does_not_undo_the_degradation_for_this_session, which is the test that killed
 # M72.

 # M73 re-anchored in round 8, and it is the sixth ring written as a mutation: it gives the coordinator
 # its window controller back (an OPTIONAL parameter, so production alone changes) and hides after
 # asking. That is precisely "the caller keeps the decision and acts later" -- the defect the ring
 # removes -- and it is now visible in the constructor, which is where the structural test looks.
 ("M73", "the coordinator gets the window back and hides after asking", [
   # Re-anchored in round 9, and the re-anchoring is itself evidence. The old form took an
   # IApplicationWindowController and hid through it; that no longer COMPILES, because the general window
   # contract has lost every hide. So the mutation now has to take the CAPABILITY -- which is exactly the
   # thing round 9 restricted to two holders, and the enumeration test sees a third appear.
   (COORDINATOR,
    "    Action<TrayGuardedOperation> perform,\n    ILogger<WindowCloseCoordinator> logger)",
    "    Action<TrayGuardedOperation> perform,\n    ILogger<WindowCloseCoordinator> logger,\n    IWindowHideCapability? hideCapability = null)"),
   (COORDINATOR,
    "        perform(TrayGuardedOperation.EnterBackground);\n        return true;",
    "        perform(TrayGuardedOperation.EnterBackground);\n        hideCapability?.HideToBackground();\n        return true;")]),

 ("M74", "an unacknowledged loss is swallowed instead of escalating", [
   (MACHINE,
    "                    if (!acknowledged)",
    "                    if (false)")]),

 # Re-anchored in round 7, and this is ATLAS-O3-OVERESCALATION written as a mutation: it puts the
 # escalation back into the OBSERVER catch, where a defective bystander becomes a quit button. In round 6
 # this mutation could only widen a condition inside a shared catch; now the two channels are separate,
 # so it has to reintroduce the coupling explicitly -- which is the honest form of it.
 # Re-anchored in round 7, and this is ATLAS-O3-OVERESCALATION written as a mutation: it puts the
 # escalation back into the OBSERVER catch, where a defective bystander becomes a quit button. In round 6
 # it could only widen a condition inside a shared catch; now that the two channels are separate it has
 # to reintroduce the coupling explicitly, which is the honest form of it.
 ("M75", "an observer failure escalates, so a noisy bystander can end the process", [
   (MACHINE,
    "                    catch (Exception exception)\n                    {\n                        // The machine never lets foreign code decide",
    "                    catch (Exception exception)\n                    {\n                        _failSafeRequested = true;\n                        // The machine never lets foreign code decide")]),
]


def build():
    """A SEPARATE build whose EXIT CODE is the verdict.

    The previous version ran `dotnet test` and inspected only the text for "error CS". That let every
    MSBuild failure through -- MSB3021 from a locked test DLL above all -- and the runner then measured
    the PREVIOUS assembly while reporting a clean row. The Total check that was supposed to catch it does
    not: a stale assembly has the SAME test count, so an equal Total is normal under mutation and proves
    nothing about which bytes were loaded. That criterion measured in my favour, which is the same defect
    it was written to catch, one layer up.

    --no-incremental so a mutation cannot be skipped as "up to date"; the return code, not the log, is
    what decides.
    """
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

    # The runner's exit code and the summary have to agree. `dotnet test` exits non-zero when tests fail,
    # which is EXPECTED for a killed mutation -- so the check is consistency, not success: a zero-failure
    # summary alongside a non-zero exit means something happened that the summary does not describe.
    if (failed > 0) != (r.returncode != 0):
        return (f"EXIT-MISMATCH(failed={failed} exit={r.returncode})", failed, passed, out)

    return (_verdict("RAN", total), failed, passed, out)


BASELINE_TOTAL = None


def _verdict(status, total):
    # Kept as a SECONDARY signal only. It cannot detect a stale assembly (same tests, same Total); what
    # detects that is the build's exit code above.
    if BASELINE_TOTAL is not None and total is not None and total != BASELINE_TOTAL:
        return f"TOTAL-MOVED(total={total} expected={BASELINE_TOTAL})"
    return status


def require_ran(mid, status, failed=None):
    """FAIL CLOSED. A row that did not run is not a result, and a matrix that keeps going after one is a
    matrix that looks green and measured nothing. Anything but RAN stops the runner with a non-zero exit.
    """
    if not status.startswith("RAN"):
        print(f"ABORTING: {mid} did not run -- {status}", flush=True)
        sys.exit(2)
    if failed is not None and failed:
        print(f"ABORTING: {mid} baseline is not green (failed={failed})", flush=True)
        sys.exit(3)


results = []
which = sys.argv[1:] if len(sys.argv) > 1 else [m[0] for m in MUTATIONS]

# The unmutated baseline, measured before anything is touched. It must RUN and it must be green, or every
# row below it is meaningless -- so it aborts rather than printing a warning nobody reads.
_b_status, _b_failed, _b_passed, _b_out = test()
_bm = re.search(r"Total:\s+(\d+)", _b_out)
BASELINE_TOTAL = int(_bm.group(1)) if _bm else None
print(f"baseline: status={_b_status} failed={_b_failed} total={BASELINE_TOTAL}", flush=True)
require_ran("baseline", _b_status, _b_failed)

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
        # An anchor that no longer matches is not a pass and not a failure -- it is the ABSENCE of a
        # result, and a matrix that shrugs and carries on reports a count that is missing a row nobody
        # notices. Superseded mutations are deleted outright, so anything reaching here is a real break.
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round13.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
