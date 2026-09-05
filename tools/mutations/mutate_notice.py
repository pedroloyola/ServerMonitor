#!/usr/bin/env python3
"""M13 S2-T mutation runner, CV-17/CV-18 fail-safe exit notice (M36-M40)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
LIFECYCLE = os.path.join(SRC, "Services", "AppLifecycleController.cs")
CONTRACT = os.path.join(SRC, "Services", "NotificationActivationContract.cs")
NOTIF = os.path.join(SRC, "Services", "WindowsAppNotificationService.cs")
NOTICE = os.path.join(SRC, "Services", "FailSafeExitNotice.cs")
BOUNDARY = os.path.join(SRC, "Services", "IUserNotificationService.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~FailSafe|FullyQualifiedName~WindowsAppNotification|FullyQualifiedName~Notification"

MUTATIONS = [
 ("M36", "the notice is emitted when the CAS for Exiting was LOST", [
   (LIFECYCLE,
    '            _logger.LogDebug("Exit already in progress; ignoring the {Reason} request.", reason);\n            return;',
    '            _logger.LogDebug("Exit already in progress; ignoring the {Reason} request.", reason);\n            RunStep(nameof(_onExitCommitted), () => _onExitCommitted?.Invoke(reason));\n            return;')]),

 ("M37", "a failing notice is allowed to prevent the true exit", [
   (LIFECYCLE,
    "        RunStep(nameof(_onExitCommitted), () => _onExitCommitted?.Invoke(reason));",
    "        _onExitCommitted?.Invoke(reason);")]),

 ("M37b", "the notice swallows nothing, so a platform failure escapes into the exit path", [
   (NOTICE,
    "        catch (Exception exception)\n        {",
    "        catch (Exception exception) when (false)\n        {")]),

 ("M38", "the action vocabulary is widened beyond the literal pair", [
   (CONTRACT,
    '            ("FailSafeExit", "OpenDashboard") => NotificationAction.OpenDashboard,',
    '            ("FailSafeExit", _) => NotificationAction.OpenDashboard,')]),

 ("M38b", "the fail-safe kind is accepted under any kind spelling", [
   (CONTRACT,
    '            (_, _) => NotificationAction.None' if False else '            _ => NotificationAction.None',
    '            (_, "OpenDashboard") => NotificationAction.OpenDashboard,\n            _ => NotificationAction.None')]),

 ("M39", "the expiration is made long", [
   (NOTIF,
    "    internal static readonly TimeSpan FailSafeExitNoticeLifetime = TimeSpan.FromMinutes(30);",
    "    internal static readonly TimeSpan FailSafeExitNoticeLifetime = TimeSpan.FromDays(30);")]),

 ("M39b", "the expiration is removed entirely", [
   (NOTIF,
    "                    expiresOnReboot: true,\n                    expiresAfter: FailSafeExitNoticeLifetime);",
    "                    expiresOnReboot: true,\n                    expiresAfter: null);")]),

 ("M40", "the notice boundary stops being fire-and-forget", [
   (BOUNDARY,
    "    void ShowFailSafeExitNotice(string title, string body) { }",
    "    Task ShowFailSafeExitNotice(string title, string body) => Task.CompletedTask;")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-notice.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
