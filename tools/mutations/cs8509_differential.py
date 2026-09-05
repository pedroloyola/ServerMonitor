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

# BYTES, NOT TEXT. This script used to read as text and write back with newline="\n", which silently
# converted two CRLF sources to LF and then printed "restored" anyway -- the measuring instrument
# rewriting the tree it measures, and announcing success for something it had not done. The originals are
# now held as raw bytes, put back byte-for-byte, and the restore is VERIFIED rather than announced.
MACHINE_BYTES = open(MACHINE, "rb").read()
CSPROJ_BYTES  = open(CSPROJ,  "rb").read()

machine_src = io.open(MACHINE, encoding="utf-8-sig").read()
csproj_src  = io.open(CSPROJ,  encoding="utf-8-sig").read()
assert ARM_OLD in machine_src, "arm anchor not found"
assert ESC_OLD in csproj_src,  "escalation anchor not found"

MACHINE_EOL = "\r\n" if b"\r\n" in MACHINE_BYTES else "\n"
CSPROJ_EOL  = "\r\n" if b"\r\n" in CSPROJ_BYTES  else "\n"

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
    io.open(MACHINE, "w", encoding="utf-8", newline=MACHINE_EOL).write(
        machine_src.replace(ARM_OLD, ARM_NEW, 1))
    print("C1 = mutation + escalation APPLIED (the tree as delivered)")
    build("C1")
    io.open(CSPROJ, "w", encoding="utf-8", newline=CSPROJ_EOL).write(
        csproj_src.replace(ESC_OLD, ESC_NEW, 1))
    print("C2 = the SAME mutation, escalation REMOVED")
    build("C2")
finally:
    open(MACHINE, "wb").write(MACHINE_BYTES)
    open(CSPROJ,  "wb").write(CSPROJ_BYTES)

    # VERIFIED, not announced.
    ok = (open(MACHINE, "rb").read() == MACHINE_BYTES
          and open(CSPROJ, "rb").read() == CSPROJ_BYTES)
    print("restored byte-for-byte" if ok else "RESTORE FAILED -- THE TREE IS NOT AS IT WAS")
    if not ok:
        raise SystemExit(4)
    build("BASELINE")
