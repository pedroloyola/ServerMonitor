#!/usr/bin/env python3
"""M13 S2-T mutation runner, T14/CV-20 set. Supports multi-edit mutations."""
import io, subprocess, sys, os, json, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MACHINE = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayStateMachine.cs")
APP = os.path.join(ROOT, "src", "ServerMonitor.App", "App.xaml.cs")
NATIVE = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "NativeTrayRegistration.cs")
DOTNET = os.path.expanduser("~/.dotnet/dotnet.exe")
TESTS = os.path.join(ROOT, "tests", "ServerMonitor.App.Tests", "ServerMonitor.App.Tests.csproj")

MUTATIONS = [
 ("M19", "the executor stops being private-nested", [
   (MACHINE,
    "    private sealed class EffectExecutor(INativeTrayRegistration native)",
    "    internal sealed class EffectExecutor(INativeTrayRegistration native)")]),
 ("M20", "the state machine retains the capability in a field of its own", [
   (MACHINE,
    "    private readonly EffectExecutor _executor;",
    "    private readonly EffectExecutor _executor;\n    private readonly INativeTrayRegistration _retainedNative;"),
   (MACHINE,
    "        // The capability is forwarded, never retained by this class.\n        _executor = new EffectExecutor(native);",
    "        _retainedNative = native;\n        _executor = new EffectExecutor(native);")]),
 ("M21", "the capability is registered in the composition root", [
   (APP,
    "                services.AddSingleton<ILocalizationService, LocalizationService>();",
    "                services.AddSingleton<ServerMonitor.App.Shell.Tray.INativeTrayRegistration>(_ => null!);\n                services.AddSingleton<ILocalizationService, LocalizationService>();")]),
 ("M22", "a closure captures the capability in a compiler-generated field", [
   (MACHINE,
    "        _escalateTermination = escalateTermination ?? throw new ArgumentNullException(nameof(escalateTermination));",
    "        var captured = escalateTermination ?? throw new ArgumentNullException(nameof(escalateTermination));\n        _escalateTermination = () => { native.Delete(); captured(); };")]),
  ("M23", "the tooltip is no longer fitted to the szTip buffer", [
   (NATIVE,
    "        return tip.Length > MaxTooltipLength ? tip[..MaxTooltipLength] : tip;",
 "        return tip;")]),
 ("M24", "the old HICON is freed BEFORE NIM_MODIFY instead of after", [
   (NATIVE,
    "        var data = Describe(NIF_ICON);",
    "        if (previous != 0) { DestroyIcon(previous); }  var data = Describe(NIF_ICON);")]),
 ("M25", "the tray host window becomes a message-only child", [
   (os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayHostWindow.cs"),
    "            hWndParent: 0,   // top-level and unowned. NOT HWND_MESSAGE.",
    "            hWndParent: -3,  // HWND_MESSAGE")]),
]

def test():
    r = subprocess.run(f'"{DOTNET}" test "{TESTS}" -c Debug -p:Platform=x64 --filter "FullyQualifiedName~Tray" 2>&1',
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
    results.append({"id": mid, "desc": desc, "status": status, "failed": failed, "passed": passed, "tests": names})
    print(f"{mid}: {status} failed={failed} passed={passed}  -- {desc}", flush=True)
    for n in names:
        print(f"      {n}", flush=True)

io.open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutation-results-t14.json"), "w", encoding="utf-8").write(json.dumps(results, indent=2))
print("DONE")
