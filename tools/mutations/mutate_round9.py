#!/usr/bin/env python3
"""M13 S2-T mutation runner, consolidated correction round (M41-M46)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
APP = os.path.join(SRC, "App.xaml.cs")
MACHINE = os.path.join(SRC, "Shell", "Tray", "TrayStateMachine.cs")
CONTRACT = os.path.join(SRC, "Services", "ITrayAffordanceSource.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout|FullyQualifiedName~FailSafe"

MUTATIONS = [
 ("M41", "the degraded session is never evaluated at startup", [
   (APP,
    "        services.GetRequiredService<TrayAffordanceLifecycle>().Evaluate();",
    "        _ = services;")]),

 ("M41b", "the startup evaluation is removed from OnLaunched", [
   (APP,
    "            EvaluateStartupAffordance(ServicesHost.Services);",
    "            // EvaluateStartupAffordance(ServicesHost.Services);")]),

 ("M41c", "startup resolves the policy but never evaluates it", [
   (APP,
    "        services.GetRequiredService<TrayAffordanceLifecycle>().Evaluate();",
    "        _ = services.GetRequiredService<TrayAffordanceLifecycle>();")]),

 ("M42", "the lifecycle learns of the loss only after the shell I/O", [
   (MACHINE,
    "            try\n            {\n                if (outcome.Publish)",
    "            ReleaseEmittedEffects(outcome);\n            DrainEffects();\n            try\n            {\n                if (outcome.Publish)")]),

 ("M43", "a redundant delete for an already removed icon is treated as a failure", [
   (MACHINE,
    "        if (_effect == ShellEffectState.Deleted)\n        {\n            return;\n        }\n\n        if (trayEvent.Success)\n        {\n            _effect = ShellEffectState.Deleted;\n            _shellMayHoldAnIcon = false;\n            return;\n        }",
    "        if (trayEvent.Success)\n        {\n            _effect = ShellEffectState.Deleted;\n            _shellMayHoldAnIcon = false;\n            return;\n        }"),
   (MACHINE,
    "        if (_effect == ShellEffectState.Deleted)\n        {\n            // Same rule as HandleCleanupCompleted",
    "        if (false)\n        {\n            // Same rule as HandleCleanupCompleted")]),

 ("M44", "the redundant-delete rule swallows a GENUINE cleanup failure", [
   (MACHINE,
    "        if (_effect == ShellEffectState.Deleted)\n        {\n            return;\n        }\n\n        if (trayEvent.Success)",
    "        if (_effect != ShellEffectState.Unverified)\n        {\n            return;\n        }\n\n        if (trayEvent.Success)")]),

 ("M45", "the affordance ordinals are shifted", [
   (CONTRACT,
    "    Unavailable = 0,",
    "    Unavailable = 10,")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-round9.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
