#!/usr/bin/env python3
"""M13 S2-T mutation runner, round 9: the door itself (M95-M97)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
TRAYSERVICE = os.path.join(SRC, "Services", "TrayService.cs")
ICONTROLLER = os.path.join(SRC, "Services", "IApplicationWindowController.cs")
APP = os.path.join(SRC, "App.xaml.cs")
MAINWINDOW = os.path.join(SRC, "MainWindow.xaml.cs")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
CONTROLLER = os.path.join(SRC, "Services", "ApplicationWindowController.cs")
LIFECYCLE = os.path.join(SRC, "Services", "TrayAffordanceLifecycle.cs")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = ("FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout"
          "|FullyQualifiedName~FailSafe|FullyQualifiedName~WindowClose")

MUTATIONS = [
 # THE MUTATION THE CONDITION REQUIRES: a third consumer takes the capability. Optional parameter so the
 # mutation compiles everywhere and the only thing that changes is the set of holders -- which is exactly
 # what the enumeration asserts.
 ("M95", "the hide capability is injected into another consumer", [
   (TRAYSERVICE,
    "    TimeSpan? iconRetryDelay = null) : IHostedService",
    "    TimeSpan? iconRetryDelay = null,\n    IWindowHideCapability? hideCapability = null) : IHostedService")]),

 # The other half of the CV-20 cure: back into the container, where anyone can ask for it.
 ("M96", "the hide capability is registered in the container", [
   (APP,
    "        services.AddSingleton<IApplicationWindowController>(sp =>\n            sp.GetRequiredService<ApplicationWindowController>());",
    "        services.AddSingleton<IApplicationWindowController>(sp =>\n            sp.GetRequiredService<ApplicationWindowController>());\n        services.AddSingleton<IWindowHideCapability>(sp =>\n            sp.GetRequiredService<ApplicationWindowController>().TakeHideCapability());")]),

 # And the original shape: the act back on the contract every consumer holds. A default interface
 # implementation, so no implementer has to change and the mutation is purely additive.
 ("M97", "HideToBackground returns to the general window contract", [
   (ICONTROLLER,
    "    void AttachWindowFactory(Func<Window> factory);",
    "    void HideToBackground()\n    {\n    }\n\n    void AttachWindowFactory(Func<Window> factory);")]),
 # ---------------------------------------------------------------- the SECOND door
 # HideForMinimize was identical window mechanics to HideToBackground, on the general contract, with its
 # caller guarded only by "the service is started". These three put each piece of that back.
 ("M98", "a refused minimize exits the application", [
   (LIFECYCLE,
    "            case TrayGuardedOperation.HideForMinimize:\n                // Emphatically NOT an exit.",
    "            case TrayGuardedOperation.HideForMinimize:\n                _lifecycleController.RequestExit(ExitReason.UserClosedWindow);\n                // Emphatically NOT an exit.")]),

 ("M99", "HideForMinimize returns to the general window contract", [
   (ICONTROLLER,
    "    void AttachWindowFactory(Func<Window> factory);",
    "    void HideForMinimize()\n    {\n    }\n\n    void AttachWindowFactory(Func<Window> factory);")]),

 ("M100", "the minimize request is dispatched as a background entry", [
   (MACHINE,
    "                case TrayGuardedOperation.HideForMinimize:\n                    operations.HideForMinimize();",
    "                case TrayGuardedOperation.HideForMinimize:\n                    operations.EnterBackground();")]),
 # ---------------------------------------------------------------- the SERVICE LOCATOR
 # THE MUTATION THE CONDITION REQUIRES. App.ServicesHost is public, so any production type can resolve
 # and act without passing the machine, the capability holders or the exit path. This is production code
 # -- MainWindow -- and the IL scan sees the call site whether or not the resolution would succeed at
 # runtime, which is the whole point of proving call sites by metadata instead of by declared parameters.
 ("M101", "a window hide is reached through the service locator", [
   (MAINWINDOW,
    "    private readonly IApplicationWindowController _windowController;",
    "    private readonly IApplicationWindowController _windowController;\n\n    private void HideBySideDoor() =>\n        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions\n            .GetRequiredService<ServerMonitor.App.Services.IWindowHideCapability>(\n                ServerMonitor.App.App.ServicesHost.Services)\n            .HideToBackground();")]),

 ("M102", "the hide capability can be taken more than once", [
   (CONTROLLER,
    "        if (_hideCapability is not null)\n        {\n            throw new InvalidOperationException(",
    "        if (false)\n        {\n            throw new InvalidOperationException(")]),
 # ---------------------------------------------------------------- CV-22
 # Escalating on the ABSENCE of a consumer is right for a LOSS and wrong for everything else: widen it and
 # a process with no loss consumer quits the moment the tray comes up successfully. The equivalent
 # mutation died before the mechanism was rewritten around AcknowledgeLoss, and the coverage did not
 # follow it -- so this pins the half that was left unproven.
 ("M103", "the absence of a loss consumer escalates on ANY notification", [
   (MACHINE,
    "                if (delivered is TrayAffordanceState.Lost or TrayAffordanceState.Unavailable\n                    && IsStillDeliverable(outcome))",
    "                if (IsStillDeliverable(outcome))")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round17.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
