#!/usr/bin/env python3
"""M13 S2-T mutation runner. One mutation at a time, against production code."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MACHINE = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayStateMachine.cs")
CONTRACT = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayCallbackContract.cs")
LIMITER = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "EpisodeFrequencyLimiter.cs")
DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
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
 ("M4", "delivery-time revalidation of notifications removed", MACHINE,
  "            if (_state is TrayLifecycleState.Releasing or TrayLifecycleState.Released)\n            {\n                return;\n            }\n        }\n\n        StateChanged?.Invoke(this, EventArgs.Empty);",
  "            if (false)\n            {\n                return;\n            }\n        }\n\n        StateChanged?.Invoke(this, EventArgs.Empty);"),
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

def test():
    r = run(f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Tray" 2>&1')
    out = r.stdout + r.stderr
    if "error CS" in out:
        return ("BUILD-FAIL", 0, 0, out)
    m = re.search(r"Failed:\s+(\d+),\s+Passed:\s+(\d+)", out)
    if m:
        return ("RAN", int(m.group(1)), int(m.group(2)), out)
    if "Build succeeded" not in out and "Passed!" not in out:
        return ("BUILD-FAIL", 0, 0, out)
    return ("UNKNOWN", 0, 0, out)

results = []
which = sys.argv[1:] if len(sys.argv) > 1 else [m[0] for m in MUTATIONS]

for mid, desc, path, old, new in MUTATIONS:
    if mid not in which:
        continue
    src = io.open(path, encoding="utf-8-sig").read()
    if old not in src:
        results.append({"id": mid, "desc": desc, "status": "ANCHOR-NOT-FOUND"})
        print(f"{mid}: ANCHOR NOT FOUND", flush=True)
        continue
    io.open(path, "w", encoding="utf-8", newline="\n").write(src.replace(old, new, 1))
    status, failed, passed, out = test()
    io.open(path, "w", encoding="utf-8", newline="\n").write(src)
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed, "passed": passed})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results.json"), "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
