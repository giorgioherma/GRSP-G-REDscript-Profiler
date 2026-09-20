RSP ALPHA 0.2 — CONTROLLED CAPTURE
==================================

WHAT CHANGED
------------
Alpha 0.1 proved the native REDscript hooks and source/mod ownership mapping.
Alpha 0.2 fixes the measurement window before we add deeper self-time profiling.

The profiler now starts PAUSED. Startup, REDscript compilation, mod initialization,
save loading, and session initialization are not recorded unless you deliberately
press the capture key during them.

HARD-CODED CAPTURE KEY
----------------------
F11 toggles the profiler:

  F11 #1 -> clear runtime counters and START a fresh capture
  F11 #2 -> STOP immediately, freeze the window, and write CSVs
  F11 #3 -> start another fresh capture

The DLL POLLS F11; it does NOT register/consume an exclusive Windows hotkey.
That is intentional so CapFrameX can use the SAME F11 press.

For synchronized testing, configure CapFrameX capture start/stop to F11 too.
The profiler checks the key every 2 ms and only responds while Cyberpunk is the
foreground process.

IMPORTANT MEASUREMENT BEHAVIOR
------------------------------
While PAUSED/COMPLETE, the opcode hooks perform only a cheap state check before
calling the original handler. They do not collect runtime rows.

While RECORDING, the profiler collects callsite timings in memory.

There are NO periodic CSV writes during the capture. The old Alpha 0.1 five-second
writer is gone because disk I/O inside the measurement window can contaminate a
CapFrameX comparison.

On the second F11 press, recording is disabled first, then the files are written.

OUTPUT
------
After a completed capture:

  red4ext\plugins\redscript_profiler_alpha\RESULTS\
      RSP_Alpha_Status.txt
      RSP_Alpha_Capture.csv
      RSP_Alpha_FunctionMap.csv
      RSP_Alpha_CallSites.csv
      RSP_Alpha_Spikes.csv

RSP_Alpha_Capture.csv
  one-row capture metadata: capture ID, start/stop Unix ms, duration, row counts

RSP_Alpha_CallSites.csv
  aggregate callsite timing for ONLY the completed F11 window
  includes max_at_capture_ms, an approximate capture-relative timestamp of each
  callsite's worst observed invocation

RSP_Alpha_Spikes.csv
  sparse event timeline containing each observed >=1 ms callsite invocation
  with capture-relative time. This is the first direct bridge for matching a
  CapFrameX frametime spike to REDscript activity at approximately the same time.

  Timeline timestamp granularity is approximately the 2 ms control-poll interval.

RSP_Alpha_FunctionMap.csv
  runtime function pointer -> source file / owner mapping

RSP_Alpha_Status.txt
  hook state plus metadata for the most recently completed capture

FIRST CONTROLLED TEST
---------------------
1. Replace Alpha 0.1 DLL with the newly built Alpha 0.2 DLL.
2. Launch Cyberpunk normally.
3. Let the game fully load into the save.
4. Configure CapFrameX start/stop hotkey to F11.
5. Choose a repeatable runtime scenario (for example 60 seconds driving).
6. Press F11 once. Both RSP and CapFrameX begin.
7. Run the scenario.
8. Press F11 again. Both stop; RSP then writes its CSVs.
9. Send the RESULTS files plus the matching CapFrameX capture.

SCOPE
-----
Alpha 0.2 is still an inclusive CALLSITE profiler around InvokeStatic and
InvokeVirtual. It is NOT yet full function self-time profiling.

Do not add nested owner totals together: wrappers can include the same downstream
work. The point of this alpha is to establish a clean, reproducible, synchronized
runtime window first.

If this is stable, the next step is the thread-local function call stack:

  inclusive = function exit - function entry
  self      = inclusive - child time

That is where exact per-function self cost becomes possible.

BUILD
-----
The existing GitHub Actions workflow still works.

  .github\workflows\build.yml

It runs cargo build --release and packages:

  red4ext\plugins\redscript_profiler_alpha.dll

LOCAL BUILD
-----------
Requires Rust/Cargo and the Windows MSVC toolchain:

  BUILD_WINDOWS.ps1

CREDITS / TECHNICAL BASIS
-------------------------
FunctionInfo / SourceFileInfo mapping and the REDscript BindFunction +
InvokeStatic + InvokeVirtual hook strategy are based on redscript-dap by
jekky / jac3km4 (MIT licensed).

redscript-dap:
  https://github.com/jac3km4/redscript-dap

red4ext-rs:
  https://github.com/jac3km4/red4ext-rs
