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


def test():
    r = subprocess.run(
        f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --filter "{FILTER}" 2>&1',
        shell=True, capture_output=True, text=True, cwd=ROOT)
    out = r.stdout + r.stderr
    if "error CS" in out:
        return ("BUILD-FAIL", 0, 0, out)
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-notice.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
