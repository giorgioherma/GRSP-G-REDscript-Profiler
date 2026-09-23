# G-REDscript Profiler 0.5.0 Public Preview

G-REDscript Profiler (GRSP) is a standalone RED4ext profiler for Cyberpunk 2077 REDscript workloads.

It measures observed `InvokeStatic` / `InvokeVirtual` call-edge activity from REDscript mod sources and turns that into public summaries, timelines, spikes, per-mod cost views and a small developer subset for framework/optimization work.

## Dependencies

Required:

- Cyberpunk 2077
- RED4ext

Optional:

- a frame-time capture companion

GRSP works fully standalone. The optional companion exists only so a REDscript capture can be archived beside a frame-time capture. CapFrameX is the tested/recommended companion, but another profiler or another compatible CapFrameX version may be linked.

GRSP never installs, configures, modifies, moves or deletes files owned by the external frame-time profiler. External capture collection is copy-only.

## Standalone package

The public standalone package is:

```text
G-REDscript-Profiler.exe
payload\
  G-REDscript-Profiler.dll
  CaptureTitle.txt
RESULTS\
GRSP_README.txt
GRSP_MANIFEST.json
...
```

The manager and native DLL are one product. The DLL is the only measurement engine; the manager handles setup, installation, capture naming, collection and restore.

The installed game layout is intentionally simple:

```text
Cyberpunk 2077\
└─ red4ext\
   └─ plugins\
      ├─ G-REDscript-Profiler.dll
      └─ G-REDscript-Profiler\
         ├─ CaptureTitle.txt
         └─ RESULTS\
```

The DLL sits beside the data folder at the same `red4ext\plugins` level.

## Standalone workflow

The manager follows the same general two-page workflow used by G-CET Runtime Profiler, but GRSP has far fewer compatibility checks.

### Setup

1. Select the Cyberpunk 2077 game root.
2. GRSP verifies the game executable.
3. GRSP verifies RED4ext is installed.
4. Optionally enable **Run with a frame-time capture tool**.
5. If enabled, link the profiler executable and its capture/results folder.

CapFrameX is recognized read-only where possible so GRSP can report its capture key and suggest its capture directory. Unknown/custom profilers are accepted; the user is asked to verify synchronized F11 capture manually.

### Install, capture & recovery

1. Install G-REDscript Profiler.
2. Give the run a capture title.
3. If using a frame-time companion, start that tool.
4. Start Cyberpunk 2077.
5. In game:
   - **F11 #1** = START
   - **F11 #2** = STOP + EXPORT
6. Close Cyberpunk before collection.
7. Use **COLLECT RESULTS / CLEAR LIVE**.
8. Use **RESTORE ORIGINAL STATE** when profiling is finished.

The profiler starts paused. No pause/resume mode is currently exposed. If the game closes while recording, shutdown is treated as an implicit STOP and GRSP exports the useful capture accumulated so far.

## Existing installation rule

Installation never overwrites an existing profiler DLL.

Before deployment GRSP checks:

```text
red4ext\plugins\G-REDscript-Profiler.dll
```

If the exact packaged DLL is already present, the manager reports that this GRSP build is already installed and the user can run it.

If a different `G-REDscript-Profiler.dll` is present, installation is refused.

The legacy development DLL `redscript_profiler_alpha.dll` is also detected and blocks installation so two GRSP profiler DLLs cannot be loaded together.

Normal public use should rarely hit these cases because the guided workflow ends with restore.

## Capture title

The manager writes the title to:

```text
red4ext\plugins\G-REDscript-Profiler\CaptureTitle.txt
```

The first non-comment line is read at F11 START. The normalized title becomes part of the native capture directory name.

Example:

```text
CITY_DRIVING
```

produces a capture similar to:

```text
Capture_0001_CITY_DRIVING_<start_unix_ms>
```

## Collection ownership

GRSP-owned live output is temporary game-side data.

On collection, completed captures and related GRSP metadata are copied and verified into the standalone package's `RESULTS` directory, then removed from the live game folder.

This is deliberately equivalent to a safe move operation:

```text
game-side GRSP output
        ↓ copy
verify exact archive
        ↓
delete GRSP-owned live source
```

This keeps the live game folder clean for the next run.

External frame-time data follows a different rule:

```text
external profiler capture
        ↓ COPY ONLY
GRSP archive\FrameTime\...
```

The external source is never removed or modified.

## Restore behavior

Restore requires Cyberpunk 2077 to be closed.

If uncollected GRSP live output exists, it is archived first. The manager then removes its managed DLL and managed data.

One deliberate exception remains:

```text
red4ext\plugins\G-REDscript-Profiler\
```

The final empty data folder is intentionally left behind. The DLL, capture-title file, state and live output are removed.

Unknown or changed profiler DLLs are never blindly deleted.

## Public output

Each native completed capture contains:

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
Developer\
  RSP_FunctionMap.csv
  RSP_CallSites.csv
  RSP_SharedTargets.csv
  RSP_Cadence.csv
  RSP_WrapperChains.csv
  RSP_WorkMap.csv
```

Open `GRSP_Report.html` first.

The public timeline uses 50 ms buckets and exposes capture-relative plus Unix timing for correlation with other performance data.

## Existing REDscript mod layouts

GRSP does not require mods to adopt a profiler-specific structure.

Owner attribution follows the REDscript source path already emitted by the compiler:

```text
r6/scripts/ModName/.../*.reds  -> owner = ModName
r6/scripts/Foo.reds            -> owner = Foo
```

The user's mod stack may of course contain modified/optimized REDscript overrides. GRSP simply profiles what is installed.

## Headless interface

The same standalone EXE exposes the lifecycle interface used by higher-level orchestration:

```text
G-REDscript-Profiler.exe --status --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --install --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --title CITY_DRIVING --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --collect --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --restore --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --start --game "D:\Games\Cyberpunk 2077" --json
```

`--scenario` remains a compatibility alias for `--title`.

## TOTAL Profiler boundary

G-REDscript Profiler is standalone first.

G's Cyberpunk 2077 TOTAL Profiler should bundle and consume the exact published standalone GRSP release unchanged. TOTAL may provide its own orchestration/adapters, synchronized workflow, CapFrameX handling and correlator, but it should not ship a TOTAL-specific GRSP DLL or manager variant.

See `docs/TOTAL_INTEGRATION_CONTRACT.md`.

## Measurement interpretation

GRSP is not a whole-CPU profiler, GPU profiler or complete VM trace.

`exclusive_instrumented_ms` means observed inclusive time minus nested calls that GRSP also observed. Native/base work can remain inside an observed wrapper boundary, and virtual targets do not always identify a concrete runtime implementation owner.

Useful trust gates include:

```text
shard_merge_ok = true
merged_shards == observed_threads
frame_quality = GOOD
unresolved_static_calls is zero/negligible
dropped_spikes = 0 or understood
dropped_hot_paths = 0 or understood
```

## Build

Native DLL:

```powershell
.\BUILD_WINDOWS.ps1
```

Final local DLL:

```text
target\release\G-REDscript-Profiler.dll
```

Standalone manager:

```powershell
dotnet publish manager\GRedscriptProfiler.Manager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Technical basis / credit

The function/source bind mapping and `BindFunction` + `InvokeStatic` + `InvokeVirtual` hook strategy are based on the open-source `redscript-dap` work by jekky / jac3km4 (MIT). `red4ext-rs` supplies the RED4ext Rust bindings and remains pinned to revision `c44146c` for this preview.
