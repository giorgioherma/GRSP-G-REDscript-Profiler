RSP ALPHA 0.1 — RUNTIME CALLSITE PROFILER
==============================================

PURPOSE
-------
This is the first native runtime alpha.

It deliberately uses the same REDscript runtime hook points already proven by
redscript-dap:
  - script function bind hook -> Function pointer -> source file mapping
  - InvokeStatic opcode handler
  - InvokeVirtual opcode handler

It DOES NOT modify .reds source files.
It DOES NOT rename mod folders.
It DOES NOT hook CET.
It DOES NOT yet claim full per-function self time.

WHAT IT MEASURES
----------------
For every static/virtual REDscript call made from a function whose source lives
under r6\scripts, it records:

  owner (first folder under r6\scripts)
  source file
  source function
  source line / callsite
  call kind
  target function/method
  calls
  total time
  average time
  maximum time
  >1 ms call count
  >5 ms call count
  >16.67 ms call count

This is an inclusive CALLSITE measurement. It tells us which mod source
locations are causing expensive script calls at runtime.

OUTPUT
------
Every ~5 seconds it overwrites a live snapshot:

  red4ext\plugins\redscript_profiler_alpha\RESULTS\
      RSP_Alpha_Status.txt
      RSP_Alpha_FunctionMap.csv
      RSP_Alpha_CallSites.csv

FIRST TEST
----------
1. Build the DLL.
2. Put:
     redscript_profiler_alpha.dll
   in:
     <Cyberpunk 2077>\red4ext\plugins\

3. Leave the existing RSP probe scripts installed for the first run if desired.
4. Launch normally with your real stack.
5. Play for 30-60 seconds.
6. Exit or alt-tab and copy the RESULTS folder.
7. Also run a short CapFrameX capture so we can estimate profiler overhead.

PASS CRITERIA
-------------
A successful alpha run means:

  - game boots
  - no RED4ext crash
  - RSP_Alpha_Status.txt says both opcode hooks = OK
  - FunctionMap.csv contains RSP-PROBE-A/B/C source paths
  - CallSites.csv receives runtime rows
  - B/C wrappers remain attributable to their own source files
  - profiler overhead is tolerable in CapFrameX

WHAT THIS ALPHA DOES NOT PROVE YET
----------------------------------
It does not catch pure computation between function calls, so this is not yet
the final profiler.

If Alpha 0.1 is stable, Alpha 0.2 adds the riskier direct pure-script execution
hook for full inclusive/self function timing. That hook is the one we must test
carefully alongside CET because CET also touches the pure-script execution path.

WHY THIS ORDER
--------------
The source-mapping and opcode hooks are already used successfully by
redscript-dap, so this is the lowest-risk way to prove our entire runtime data
pipeline against your huge real stack before adding the more invasive hook.

BUILD
-----
Option A — GitHub Actions:
  Put this source folder in a GitHub repo and run the included workflow:
    .github\workflows\build.yml

  It produces an artifact named:
    RSP-Alpha-0.1-GameRoot

Option B — local Windows build:
  Requires Rust/Cargo and the Windows MSVC toolchain.
  Run:
    BUILD_WINDOWS.ps1

Then optionally:
    .\INSTALL_BUILT_ALPHA.ps1 -GameRoot "D:\Games\PC\Cyberpunk 2077"

CREDITS / TECHNICAL BASIS
-------------------------
The FunctionInfo / SourceFileInfo mapping layout and the safe REDscript
BindFunction + InvokeStatic + InvokeVirtual hook strategy are based on
redscript-dap by jekky / jac3km4 (MIT licensed).

redscript-dap:
  https://github.com/jac3km4/redscript-dap

red4ext-rs:
  https://github.com/jac3km4/red4ext-rs
