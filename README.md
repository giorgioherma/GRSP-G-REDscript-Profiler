RSP ALPHA 0.4.0 — SCENARIO MATRIX PROFILER
==========================================

PURPOSE
-------
Alpha 0.3.2 stabilized the profiler itself: complete multithread shard merging,
real frame callbacks, post-capture static target resolution, correct partial-frame
semantics, nested timing, cadence, roots, cross-mod edges, target domains, and
framework signals.

Alpha 0.4.0 deliberately DOES NOT add new measured hot-path instrumentation.
Instead it turns the stable profiler into a controlled multi-scenario collection
tool so IDLE / WORLD / COMBAT / UI captures can be compared without overwriting
one another or mixing files.

CAPTURE KEY
-----------
F11 remains shared with CapFrameX:

  F11 #1 -> START
  F11 #2 -> STOP

Audio:
  START = one high beep
  STOP  = two low beeps

SCENARIO LABEL
--------------
Edit:

  red4ext\plugins\redscript_profiler_alpha\RSP_Scenario.txt

The first non-empty, non-comment line is used as the scenario label. Recommended:

  IDLE
  WORLD
  COMBAT
  UI

The file is read ONLY when F11 starts a capture, before PROFILE_STATE becomes
RECORDING. You can edit it between captures without restarting Cyberpunk and it
adds no file I/O inside the measured window.

MULTI-CAPTURE OUTPUT
--------------------
Each completed capture is preserved in its own directory:

  red4ext\plugins\redscript_profiler_alpha\RESULTS\
      RSP_Alpha_Status.txt
      RSP_SessionIndex.csv
      LATEST.txt
      Capture_0001_IDLE_<start_unix_ms>\
      Capture_0002_WORLD_<start_unix_ms>\
      Capture_0003_COMBAT_<start_unix_ms>\
      Capture_0004_UI_<start_unix_ms>\

Each Capture_* folder contains the complete profiler output set and its own
RSP_Alpha_Status.txt.

RSP_SessionIndex.csv is append-only for the current RESULTS directory and gives
one summary row per capture, including scenario, duration, total calls, semantic
calls, wrapper/intrinsic traffic, framework-shared calls, root/descendant work,
cross-mod traffic, thread/shard counts, frame quality, and static-resolution trust.

LATEST.txt points to the most recently completed capture folder.

FRAMEWORK-DESIGN SUMMARY COUNTERS
---------------------------------
Alpha 0.4 adds only POST-CAPTURE summary calculations. They do not affect measured
runtime activity:

  intrinsic_calls
      language-level traffic such as Operator* / Cast

  wrapper_calls
      wrapper/proxy plumbing

  semantic_calls
      observed calls excluding LANGUAGE_INTRINSIC and SCRIPT_WRAPPER domains

  framework_shared_calls
      semantic target traffic where the same target is used by >=3 source owners

  root_calls
      observed root callsite executions

  descendant_calls
      total nested descendants generated beneath observed roots

  cross_mod_calls
      nested transitions where parent and child source owners differ

These counters are intended to make scenario-to-scenario framework design easier.
They are not a replacement for the detailed CSVs.

PER-CAPTURE OUTPUTS
-------------------
  RSP_Alpha_Status.txt
  RSP_Alpha_Capture.csv
  RSP_Alpha_Markers.csv
  RSP_Alpha_FunctionMap.csv
  RSP_Alpha_CallSites.csv
  RSP_Alpha_ByOwner.csv
  RSP_Alpha_ByFunction.csv
  RSP_Alpha_SharedTargets.csv
  RSP_Alpha_Edges.csv
  RSP_Alpha_Cadence.csv
  RSP_Alpha_Frames.csv
  RSP_Alpha_FrameOwners.csv
  RSP_Alpha_Spikes.csv
  RSP_Alpha_HotPaths.csv
  RSP_Alpha_WrapperChains.csv
  RSP_Alpha_WorkMap.csv
  RSP_Alpha_Threads.csv
  RSP_Alpha_Roots.csv
  RSP_Alpha_CrossModEdges.csv
  RSP_Alpha_TargetDomains.csv
  RSP_Alpha_OwnerDomains.csv
  RSP_Alpha_FrameworkSignals.csv

TRUST GATES
-----------
A capture is suitable for framework analysis when:

  shard_merge_ok = true
  merged_shards == observed_threads
  frame_quality = GOOD
  unresolved_static_calls is zero or negligible
  dropped_spikes = 0 (or explicitly understood)
  dropped_hot_paths = 0 (or explicitly understood)

CAPFRAMEX PAIRING
-----------------
Keep the CapFrameX file from the SAME F11 window with the corresponding Capture_*
folder. The scenario label and start_unix_ms are now embedded in the profiler
capture metadata/folder name to make accidental file mismatches easier to spot.

The Alpha 0.3.2 validation package that led to this build had internally clean
RSP data, but the CapFrameX JSON bundled with it was from a different window
(about 116.9 s vs the RSP 154.0 s capture). That did not invalidate RSP 0.3.2,
but it meant cross-tool correlation could not be evaluated from that bundle.

RECOMMENDED NEXT DATASET
------------------------
Run four controlled captures using the same installed mod stack:

  1. IDLE
  2. WORLD
  3. COMBAT
  4. UI

See SCENARIO_MATRIX_PLAN.md for exact intent and handling.

MEASUREMENT MODEL
-----------------
The instrumentation remains the Alpha 0.3.2 model:

  - exact observed InvokeStatic / InvokeVirtual call counts
  - QPC timing for every observed call (no sampling)
  - per-thread private shards
  - root-only active barrier
  - STOPPING quiescence merge
  - nested instrumented stack
  - inclusive + exclusive-instrumented timing
  - real Running-state frame boundaries
  - cadence and active-frame analysis
  - root amplification
  - cross-mod nesting
  - sparse >=1 ms spikes / hot paths
  - post-capture static target resolution
  - stable function/callsite IDs across captures

LIMITS
------
This remains an observed call-edge profiler, not a complete VM instruction trace.
Not every REDscript intrinsic/VM operation appears as an InvokeStatic/InvokeVirtual
edge. exclusive_instrumented_ms is exclusive relative to other instrumented edges,
not guaranteed complete function self time. Virtual target names do not always
identify the concrete implementation owner.

BUILD
-----
GitHub Actions:

  cargo build --release

Artifact:

  RSP-Alpha-0.4.0-GameRoot

The staged artifact contains:

  red4ext\plugins\redscript_profiler_alpha.dll
  red4ext\plugins\redscript_profiler_alpha\RSP_Scenario.txt
  RSP_ALPHA_README.txt
  RSP_ALPHA_MANIFEST.json
  RSP_SCENARIO_MATRIX_PLAN.txt

CREDITS / TECHNICAL BASIS
-------------------------
Bind/source mapping and InvokeStatic / InvokeVirtual hook strategy are based on
redscript-dap by jac3km4 (MIT). red4ext-rs is pinned to revision c44146c.
