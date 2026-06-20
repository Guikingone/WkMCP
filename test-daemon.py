#!/usr/bin/env python3
"""
Test of the persistent WolvenKit daemon.

Runs the daemon, waits for the {"ready":true} signal (HashService loading,
~6 s), then sends requests while measuring the latency of each one. The goal:
after startup, each request must be fast (HashService stays warm).

Usage: python3 test-daemon.py
"""
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

# UTF-8 output: this script prints non-ASCII characters (→, accents,
# daemon output). Without this, the Windows console (cp1252) crashes print().
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
DAEMON = os.path.join(HERE, "src", "WkDaemon", "bin", "Debug", "net8.0", "WkDaemon.dll")
DOTNET = (os.environ.get("WKMCP_DOTNET")
          or shutil.which("dotnet")
          or "/opt/homebrew/opt/dotnet@8/libexec/dotnet")

if not os.path.exists(DAEMON):
    sys.exit(f"Daemon not found: {DAEMON}\nBuild: dotnet build src/WkDaemon")

# Fixture to exercise the pack verb. tempfile.gettempdir() is cross-platform:
# /tmp on Unix, %TEMP% on Windows.
TMP = tempfile.gettempdir()
SRC = os.path.join(TMP, "wkpack_src")
PACK_OUT = os.path.join(TMP, "wkpack_out2")
shutil.rmtree(SRC, ignore_errors=True)
os.makedirs(os.path.join(SRC, "base", "test"), exist_ok=True)
with open(os.path.join(SRC, "base", "test", "x.txt"), "w") as f:
    f.write("compressible content " * 100)
os.makedirs(PACK_OUT, exist_ok=True)

t0 = time.time()
proc = subprocess.Popen(
    [DOTNET, DAEMON],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    encoding="utf-8", errors="replace", bufsize=1)

ready = proc.stdout.readline()
print(f"=== Daemon ready after {time.time() - t0:.1f} s: {ready.strip()} ===\n")


def call(rid, argv):
    proc.stdin.write(json.dumps({"id": rid, "argv": argv}) + "\n")
    proc.stdin.flush()
    t = time.time()
    resp = json.loads(proc.stdout.readline())
    dt = (time.time() - t) * 1000
    out = (resp.get("output") or "").strip().replace("\n", "  |  ")
    print(f"#{rid}  {argv}")
    print(f"     → {dt:6.0f} ms   exit={resp.get('exit')}   {out[:170]}")


call(1, ["--version"])
call(2, ["hash", "WolvenKit"])
call(3, ["hash", "base", "test.mesh"])
call(4, ["pack", SRC, "--outpath", PACK_OUT])
call(5, ["resolve-hash", "11641284607983081698", "42"])
call(6, ["tweakdb-resolve", "42"])
call(7, ["tweakdb-query", os.path.join(TMP, "nonexistent-tweakdb.bin"), "Items"])

proc.stdin.close()
try:
    proc.wait(timeout=10)
except subprocess.TimeoutExpired:
    proc.kill()

err = (proc.stderr.read() or "").strip()
if err:
    print("\n=== daemon stderr (last 10 lines) ===")
    print("\n".join(err.splitlines()[-10:]))
