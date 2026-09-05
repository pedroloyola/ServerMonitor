#!/usr/bin/env python3
"""M13 S2-T mutation runner. One mutation at a time, against production code."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MACHINE = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayStateMachine.cs")
CONTRACT = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayCallbackContract.cs")
LIMITER = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "EpisodeFrequencyLimiter.cs")
DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")

# This runner predates the shared FILTER constant; it always used this one inline.
FILTER = "FullyQualifiedName~Tray"
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")

MUTATIONS = [
 ("M1", "Transition may emit Add during Releasing", MACHINE,
  "        if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)\n        {\n            return TerminalOnly(trayEvent);\n        }",
  "        if (false)\n        {\n            return TerminalOnly(trayEvent);\n        }"),
 ("M2", "a late pre-Release Add success publishes Available", MACHINE,
  "        if (trayEvent.Generation != _generation || !_episodeActive)\n        {\n            ReconcileStale(trayEvent);\n            return;\n        }",
  "        if (false)\n        {\n            ReconcileStale(trayEvent);\n            return;\n        }"),
 ("M3", "a late Add receives no compensating Delete", MACHINE,
  "        if (trayEvent.MayHaveCreatedAnEffect)\n        {\n            _effect = ShellEffectState.MayExist;\n            _shellMayHoldAnIcon = true;\n            _cleanupAttempts = 0;\n            Emit(EffectKind.DeleteIcon, TimeSpan.Zero);\n        }",
  "        if (trayEvent.MayHaveCreatedAnEffect)\n        {\n            _effect = ShellEffectState.Deleted;\n        }"),
 # M4 DELETED, not left to print ANCHOR NOT FOUND. Superseded by M55 (mutate_round11.py), which
 # attacks delivery-time revalidation against the code as it stands. A row that can only ever fail to
 # anchor is not a measurement, and tolerating it forces the runner to tolerate every broken anchor.
 ("M5", "a false Shell_NotifyIcon result is treated as success", MACHINE,
  "                    if (!_native.Add())",
  "                    if (!_native.Add() && false)"),
 ("M6", "a successful Shell_NotifyIcon result is treated as failure", MACHINE,
  "                    return _native.SetVersion()\n                        ? ShellOutcome.Succeeded",
  "                    return _native.SetVersion() && false\n                        ? ShellOutcome.Succeeded"),
 ("M7", "TaskbarCreated recovery removed", MACHINE,
  "                BeginEpisode(monotonicNow);\n                Emit(EffectKind.ScheduleDeadline, RecoveryDeadline);\n                Emit(EffectKind.ScheduleDebounce, DebounceDelay);\n                break;",
  "                break;"),
 ("M8", "a successful recovery resets the frequency history", LIMITER,
  "    internal bool TryBeginEpisode(long monotonicTimestamp)\n    {",
  "    internal void ResetForSuccess() { _count = 0; _next = 0; }\n\n    internal bool TryBeginEpisode(long monotonicTimestamp)\n    {"),
 ("M9", "Available retained after an admitted TaskbarCreated", MACHINE,
  "        _attemptsUsed = 0;\n        _state = TrayLifecycleState.Recovering;",
  "        _attemptsUsed = 0;\n        if (_state != TrayLifecycleState.Available) { _state = TrayLifecycleState.Recovering; }"),
 ("M10", "an unverifiable cleanup is allowed to keep living", MACHINE,
  "        _effect = ShellEffectState.Unverified;\n\n        // CleanupVerified=false is never a steady state",
  "        _effect = ShellEffectState.Deleted;\n\n        // CleanupVerified=false is never a steady state"),
 ("M11", "a default arm is added to the effect switch", MACHINE,
  "            EffectKind.FailSafeExit => (NativeTrayOperation.None, false)\n            // No `_ =>` arm on purpose. CS8509 is escalated to an error in the csproj.",
  "            EffectKind.FailSafeExit => (NativeTrayOperation.None, false),\n            _ => (NativeTrayOperation.None, false)"),
 ("M12", "the fail-safe RunOnce marks on entry instead of after a normal return", MACHINE,
  "            try\n            {\n                _requestAuthoritativeExit();\n\n                // Marked only AFTER a normal return: an exception must not consume the single shot.\n                lock (_decision)\n                {\n                    _failSafeCompleted = true;\n                }\n\n                return;\n            }",
  "            try\n            {\n                lock (_decision) { _failSafeCompleted = true; }\n                _requestAuthoritativeExit();\n                return;\n            }"),
 ("M13", "the CV-19 carve-out is removed so stale AddCompleted is discarded", MACHINE,
  "        if (trayEvent.Generation != 0 && trayEvent.Generation != _generation\n            && trayEvent.Kind != TrayEventKind.AddCompleted)",
  "        if (trayEvent.Generation != 0 && trayEvent.Generation != _generation)"),
 ("M14", "the deadline preamble step is removed", MACHINE,
  "        if (_episodeActive && monotonicNow >= _deadlineTimestamp)",
  "        if (false && _episodeActive && monotonicNow >= _deadlineTimestamp)"),
 ("M15", "the message-identity check is removed", CONTRACT,
  "        if (message != CallbackMessage)\n        {\n            return null;\n        }",
  "        if (false)\n        {\n            return null;\n        }"),
 ("M16", "the icon-id check is removed", CONTRACT,
  "        if (iconId != IconId)\n        {\n            return null;\n        }",
  "        if (false)\n        {\n            return null;\n        }"),
 ("M17", "the closed v4 event list is opened", CONTRACT,
  "            WM_CONTEXTMENU => TrayCallbackAction.ContextMenu,\n            _ => null",
  "            WM_CONTEXTMENU => TrayCallbackAction.ContextMenu,\n            _ => TrayCallbackAction.Open"),
 ("M18", "the anchor sanitisation is removed", CONTRACT,
  "        if (decoded == TrayCallbackAction.ContextMenu && !isOnScreen(anchor))",
  "        if (false && decoded == TrayCallbackAction.ContextMenu && !isOnScreen(anchor))"),
]

def run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True, cwd=ROOT)

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

for mid, desc, path, old, new in MUTATIONS:
    if mid not in which:
        continue
    # Byte-exact restore -- see the README. Restoring through a text write with newline="\n" rewrote CRLF
    # sources as LF and left the tree dirty after every run.
    original_bytes = open(path, "rb").read()
    src = io.open(path, encoding="utf-8-sig").read()
    if old not in src:
        results.append({"id": mid, "desc": desc, "status": "ANCHOR-NOT-FOUND"})
        print(f"{mid}: ANCHOR NOT FOUND", flush=True)
        sys.exit(5)
    eol = "\r\n" if b"\r\n" in original_bytes else "\n"
    io.open(path, "w", encoding="utf-8", newline=eol).write(src.replace(old, new, 1))
    status, failed, passed, out = test()
    open(path, "wb").write(original_bytes)
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed, "passed": passed})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)
    require_ran(mid, status)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results.json"), "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
