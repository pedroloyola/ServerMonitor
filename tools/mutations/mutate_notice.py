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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-notice.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
