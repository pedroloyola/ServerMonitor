#!/usr/bin/env python3
"""CS8509 differential proof: the SAME mutation compiled twice, with and without the escalation."""
import io, subprocess, os, re

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
MACHINE = os.path.join(ROOT, "src", "ServerMonitor.App", "Shell", "Tray", "TrayStateMachine.cs")
CSPROJ  = os.path.join(ROOT, "src", "ServerMonitor.App", "ServerMonitor.App.csproj")
DOTNET  = os.path.expanduser("~/.dotnet/dotnet.exe")
PROJ    = os.path.join(ROOT, "src", "ServerMonitor.App", "ServerMonitor.App.csproj")

# The mutation: one arm of the exhaustive switch is deleted.
ARM_OLD = "            EffectKind.ScheduleDeadline => (NativeTrayOperation.None, false),\n"
ARM_NEW = ""

ESC_OLD = "<WarningsAsErrors>$(WarningsAsErrors);CS8509</WarningsAsErrors>"
ESC_NEW = "<!-- escalation removed for the differential proof -->"

machine_src = io.open(MACHINE, encoding="utf-8-sig").read()
csproj_src  = io.open(CSPROJ,  encoding="utf-8-sig").read()
assert ARM_OLD in machine_src, "arm anchor not found"
assert ESC_OLD in csproj_src,  "escalation anchor not found"

def build(label):
    r = subprocess.run(f'"{DOTNET}" build "{PROJ}" -c Debug -p:Platform=x64 --no-incremental',
                       shell=True, capture_output=True, text=True, cwd=ROOT)
    out = r.stdout + r.stderr
    err = len(re.findall(r"error CS8509", out))
    warn = len(re.findall(r"warning CS8509", out))
    ok = "Build succeeded" in out
    print(f"{label}: build_succeeded={ok}  error_CS8509={err}  warning_CS8509={warn}")
    return ok, err, warn

try:
    io.open(MACHINE, "w", encoding="utf-8", newline="\n").write(machine_src.replace(ARM_OLD, ARM_NEW, 1))
    print("C1 = mutation + escalation APPLIED (the tree as delivered)")
    build("C1")
    io.open(CSPROJ, "w", encoding="utf-8", newline="\n").write(csproj_src.replace(ESC_OLD, ESC_NEW, 1))
    print("C2 = the SAME mutation, escalation REMOVED")
    build("C2")
finally:
    io.open(MACHINE, "w", encoding="utf-8", newline="\n").write(machine_src)
    io.open(CSPROJ,  "w", encoding="utf-8", newline="\n").write(csproj_src)
    print("restored")
    build("BASELINE")
