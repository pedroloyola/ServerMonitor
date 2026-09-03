#!/usr/bin/env python3
"""M13 S2-T mutation runner, wiring/flyout/theme set (M26-M35)."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
SRC = os.path.join(ROOT, "src", "ServerMonitor.App")
GATE = os.path.join(SRC, "Shell", "Tray", "FlyoutReentrancyGate.cs")
FLYOUT = os.path.join(SRC, "Shell", "Tray", "TrayFlyoutWindow.cs")
ADAPTER = os.path.join(SRC, "Shell", "Tray", "OwnedTrayIconAdapter.cs")
APP = os.path.join(SRC, "App.xaml.cs")
ROOTSET = os.path.join(SRC, "Services", "ThemeRootSet.cs")
THEME = os.path.join(SRC, "Services", "ThemeService.cs")

DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")
FILTER = "FullyQualifiedName~Tray|FullyQualifiedName~Theme|FullyQualifiedName~Flyout"

MUTATIONS = [
 ("M26", "the CV-9 gate admits every request", [
   (GATE,
    "            if (_open)",
    "            if (false)")]),

 ("M27", "Close stops being idempotent, so a second one hands out an extra slot", [
   (GATE, "    private bool _open;", "    private int _open;"),
   (GATE,
    "        get { lock (_sync) { return _open; } }",
    "        get { lock (_sync) { return _open > 0; } }"),
   (GATE,
    "            if (_open)\n            {\n                return false;\n            }\n\n            _open = true;\n            return true;",
    "            if (_open > 0)\n            {\n                return false;\n            }\n\n            _open++;\n            return true;"),
   (GATE, "            _open = false;", "            _open--;")]),

 ("M28", "the menu order puts Exit first", [
   (FLYOUT,
    "        TrayCommand.Open,\n        TrayCommand.ToggleCompact,",
    "        TrayCommand.Exit,\n        TrayCommand.Open,\n        TrayCommand.ToggleCompact,")]),

 ("M29", "a menu item is dropped from the order", [
   (FLYOUT,
    "        TrayCommand.RefreshAll,\n        TrayCommand.Settings,\n        TrayCommand.Exit\n    ];",
    "        TrayCommand.Settings,\n        TrayCommand.Exit\n    ];")]),

 ("M30", "a menu item resolves the wrong resource key", [
   (FLYOUT,
    'TrayCommand.Settings => "TraySettingsMenuItem",',
    'TrayCommand.Settings => "TraySettingsMenuItemX",')]),

 ("M31", "attaching a theme root REPLACES the previous one", [
   (ROOTSET,
    "            _roots.Add(root);\n            return true;",
    "            _roots.Clear();\n            _roots.Add(root);\n            return true;")]),

 ("M32", "the theme is applied only to the most recent root", [
   (ROOTSET,
    "        foreach (var root in Snapshot())\n        {\n            apply(root);\n        }",
    "        foreach (var root in Snapshot().TakeLast(1))\n        {\n            apply(root);\n        }")]),

 ("M33", "the affordance source is a SECOND instance rather than the icon owner", [
   (APP,
    "        services.AddSingleton<ITrayAffordanceSource>(sp =>\n            sp.GetRequiredService<Shell.Tray.OwnedTrayIconAdapter>());",
    "        services.AddSingleton<ITrayAffordanceSource>(sp => new Shell.Tray.OwnedTrayIconAdapter(\n            sp.GetRequiredService<IThemeService>(),\n            sp.GetRequiredService<ILocalizationService>(),\n            sp.GetRequiredService<IAppLifecycleController>,\n            sp.GetRequiredService<IProcessTerminator>(),\n            sp.GetRequiredService<ILoggerFactory>()));")]),

 ("M34", "the capability is registered in the container (T14c over real descriptors)", [
   (APP,
    "        services.AddSingleton<TrayAffordanceLifecycle>();",
    "        services.AddSingleton<Shell.Tray.INativeTrayRegistration>(_ => null!);\n        services.AddSingleton<TrayAffordanceLifecycle>();")]),

 ("M35", "the adapter reports Available before anything is registered", [
   (ADAPTER,
    "            return machine?.State ?? TrayAffordanceState.Unavailable;",
    "            return machine?.State ?? TrayAffordanceState.Available;")]),
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

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-wiring.json"),
        "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
