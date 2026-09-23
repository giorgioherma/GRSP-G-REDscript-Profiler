# GRSP 0.5.0 Public Preview — G-REDscript Profiler

GRSP is a RED4ext profiler for **Cyberpunk 2077 REDscript mod workloads**. It is intended for normal mod users who want to identify sustained script load and visible stutter sources, while retaining a compact developer subset for authors building shared-runtime or optimization projects such as G-REDruntime.

## What 0.5.0 changes

The Alpha 0.4 measurement core is retained: exact observed `InvokeStatic` / `InvokeVirtual` call-edge timing, per-thread shards, nested inclusive/exclusive-instrumented timing, real game-frame boundaries, stable IDs and bounded spike capture.

The output layer is redesigned for public use:

- a self-contained `GRSP_Report.html` with a **Top Mods by Observed REDscript Cost** bar chart;
- `GRSP_ByMod.csv` ranked by exclusive instrumented milliseconds per second;
- explicit sustained / burst / mixed workload classification;
- 50 ms `GRSP_Timeline.csv` buckets for correlation with the CET native profiler;
- Unix timestamps in public frame and spike outputs;
- a much smaller public output set;
- a `Developer` folder containing only the most useful framework-design exports.

No new file I/O, sorting, HTML generation or report work occurs while the capture is recording. Public summaries are generated **after STOP**.

## Install

GRSP now builds two artifacts.

The legacy/manual **GameRoot** artifact remains available unchanged in shape:

```text
red4ext\plugins\redscript_profiler_alpha.dll
red4ext\plugins\redscript_profiler_alpha\RSP_Scenario.txt
GRSP_README.txt
GRSP_MANIFEST.json
GRSP_FRAMEWORK_AUTHOR_GUIDE.txt
```

The **Standalone** artifact adds the lifecycle manager:

```text
G-REDscript-Profiler.exe
payload\
  redscript_profiler_alpha.dll
  RSP_Scenario.txt
GRSP_README.txt
GRSP_MANIFEST.json
...
```

The manager owns installation lifecycle only. It does not contain another capture engine.

It can:

```text
select/check the Cyberpunk root
install or update the packaged GRSP DLL
back up and verify a pre-existing DLL before replacement
preserve and back up a pre-existing RSP_Scenario.txt
edit the scenario only while the file remains in known managed state
find and copy the latest completed native capture
restore/uninstall without deleting native RESULTS
start Cyberpunk 2077
```

Changed or unknown user files are not blindly overwritten during install or restore. The manager records ownership state under `red4ext\plugins\.grsp_manager_state.json`.

Headless use is available through the same EXE, for example:

```text
G-REDscript-Profiler.exe --action status --game-root "D:\Games\Cyberpunk 2077"
G-REDscript-Profiler.exe --action install --game-root "D:\Games\Cyberpunk 2077"
G-REDscript-Profiler.exe --action scenario --game-root "D:\Games\Cyberpunk 2077" --scenario COMBAT
G-REDscript-Profiler.exe --action collect --game-root "D:\Games\Cyberpunk 2077"
G-REDscript-Profiler.exe --action restore --game-root "D:\Games\Cyberpunk 2077"
```

The internal DLL name remains `redscript_profiler_alpha.dll` in 0.5.0 so existing Alpha installations are replaced instead of loading two profilers at once.

## Capture

GRSP starts paused. Fully load a save before profiling.

`F11` is intentionally shared with CapFrameX:

```text
F11 #1 -> START  (one high beep)
F11 #2 -> STOP   (two low beeps)
```

Edit the first non-comment line in:

```text
red4ext\plugins\redscript_profiler_alpha\RSP_Scenario.txt
```

before a capture. Useful labels include:

```text
WORLD
DRIVING
COMBAT
WANTED
UI
IDLE
```

The scenario file is read only at START, before recording begins.

## Public output

Each capture is preserved under:

```text
red4ext\plugins\redscript_profiler_alpha\RESULTS\
  Capture_0001_WORLD_<start_unix_ms>\
```

Open **`GRSP_Report.html`** first.

Public files:

```text
GRSP_Report.html
GRSP_Summary.csv
GRSP_ByMod.csv
GRSP_ByFunction.csv
GRSP_Timeline.csv
GRSP_Frames.csv
GRSP_Spikes.csv
GRSP_Markers.csv
GRSP_FrameworkCandidates.csv
GRSP_Status.txt
```

`RESULTS\RSP_SessionIndex.csv` remains an append-only index across captures, and `LATEST.txt` points to the newest capture.

### GRSP_ByMod.csv

This is the primary public ranking. Important fields:

- `exclusive_ms_per_sec` — sustained observed REDscript-side cost;
- `observed_exclusive_share_pct` — share of the profiler's observed exclusive work;
- `active_frame_pct` — how continuously the owner is active;
- `max_frame_exclusive_ms` — largest owner contribution in one observed game frame;
- `max_spike_ms` — largest captured call boundary;
- `workload_pattern` — `SUSTAINED`, `BURSTY`, `MIXED`, or `BACKGROUND`;
- `attribution_note` — flags cases where wrapper attribution needs extra caution.

The table is decision support, **not an automatic uninstall list**. GRSP cannot know a mod's dependency graph, importance to the user, or whether measured time inside a wrapper boundary belongs entirely to the wrapper's own script logic.

### Existing mod layout is authoritative

GRSP does not require a profiler-specific mod format, manifest, folder rename or wrapper package.

Owner attribution follows the REDscript source path already emitted by the compiler:

```text
r6/scripts/ModName/.../*.reds  -> owner = ModName
r6/scripts/Foo.reds            -> owner = Foo
```

Both absolute and game-relative source paths are accepted, and both `/` and `\` separators are normalized. This keeps profiling compatible with mods in their existing install layout.

## CET profiler correlation

GRSP 0.5.0 is designed to run in the same window as the CET native profiler.

The public timing files expose the same two useful axes:

```text
capture-relative time
Unix epoch milliseconds
```

`GRSP_Timeline.csv` uses **50 ms buckets** so it can be joined against the CET profiler timeline. `GRSP_Markers.csv` carries START / STOP timing, while `GRSP_Frames.csv` and `GRSP_Spikes.csv` include Unix timestamps for direct event correlation.

Recommended workflow:

1. bind both profilers to the same F11 capture window;
2. use the same scenario label/name in both runs when possible;
3. align START markers by Unix time;
4. compare the 50 ms timelines;
5. inspect GRSP spike/frame rows around CET spikes;
6. use CapFrameX for the actual rendered-frame result.

The three tools answer different questions:

```text
GRSP       -> REDscript ownership/call structure
CET profiler -> Lua/CET callback ownership
CapFrameX  -> what the player actually experienced
```

## Developer subset

The `Developer` folder contains a small set useful for framework authors without restoring the old research-output flood:

```text
RSP_FunctionMap.csv
RSP_CallSites.csv
RSP_SharedTargets.csv
RSP_Cadence.csv
RSP_WrapperChains.csv
RSP_WorkMap.csv
```

`GRSP_FrameworkCandidates.csv` remains in the public root because it is useful to authors deciding whether repeated work suggests dirty/event gates, shared state, caching/indexing or wrapper consolidation.

See `FRAMEWORK_AUTHOR_GUIDE.md` in the source package.

## Measurement interpretation

GRSP observes `InvokeStatic` / `InvokeVirtual` activity whose caller maps to `r6\scripts` mod source.

It is **not** a whole-CPU profiler, GPU profiler, complete VM instruction trace, or proof that a high-ranked mod is defective.

`exclusive_instrumented_ms` means:

```text
inclusive observed call time
- time spent in nested calls also observed by GRSP
```

Uninstrumented/native/base work may still remain inside an observed boundary. This is especially important for wrapper-heavy mods. Virtual target resolution also does not always identify the concrete runtime implementation owner.

## Trust gates

Treat a capture as reliable when:

```text
shard_merge_ok = true
merged_shards == observed_threads
frame_quality = GOOD
unresolved_static_calls is zero/negligible
dropped_spikes = 0 or understood
dropped_hot_paths = 0 or understood
```

## Build

The profiler DLL still builds with Rust/MSVC:

```powershell
.\BUILD_WINDOWS.ps1
```

The standalone manager is .NET 8 WinForms:

```powershell
dotnet publish manager\GRedscriptProfiler.Manager.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

Measurement remains entirely in:

```text
target\release\redscript_profiler_alpha.dll
```

## Technical basis / credit

Function/source bind mapping and the `BindFunction` + `InvokeStatic` + `InvokeVirtual` hook strategy are based on the open-source `redscript-dap` work by jekky / jac3km4 (MIT). `red4ext-rs` supplies the RED4ext Rust bindings and remains pinned to revision `c44146c` for this preview.
