<p align="center">
  <img src="assets/g-red-icon.png" alt="G-REDscript Profiler" width="192">
</p>

# G-REDscript Profiler

**v1.0.0 — first stable release**

G-REDscript Profiler (GRSP) is a standalone RED4ext profiler for Cyberpunk 2077 REDscript workloads. It measures observed REDscript call-edge activity and produces per-mod, per-function, frame, spike, timeline, and framework-facing reports.

## Download and run

The public release is a **portable ZIP**. No installer is required.

**Canonical v1.0.0 download:** [G-REDscript-Profiler-v1.0.0.zip](https://github.com/giorgioherma/GRSP-G-REDscript-Profiler/releases/download/v1.0.0/G-REDscript-Profiler-v1.0.0.zip)

GitHub **Releases** is the canonical download location for v1.0.0.

```text
G-REDscript-Profiler-v1.0.0.zip
└─ G-REDscript-Profiler\
   ├─ G-REDscript-Profiler.exe
   ├─ MANIFEST.json
   ├─ VERSION.txt
   ├─ app\
   │  ├─ G-REDscript-Profiler.App.exe
   │  ├─ G-REDscript-Profiler.App.deps.json
   │  ├─ G-REDscript-Profiler.App.runtimeconfig.json
   │  └─ .NET runtime files...
   ├─ payload\
   │  ├─ G-REDscript-Profiler.dll
   │  └─ CaptureTitle.txt
   ├─ RESULTS\
   └─ docs\
      ├─ README.md
      ├─ CHANGELOG.md
      ├─ RELEASE_NOTES.md
      ├─ FRAMEWORK_AUTHOR_GUIDE.md
      ├─ OUTPUT_SCHEMA.md
      ├─ TOTAL_INTEGRATION_CONTRACT.md
      └─ THIRD_PARTY_NOTICE.txt
```

Extract the ZIP to a normal writable folder and run `G-REDscript-Profiler.exe`.

The root EXE is a small native launcher. The self-contained WinForms application and .NET runtime live under `app\`, matching the G-CET Runtime Profiler package layout. Settings remain package-local beside the root launcher rather than in Windows roaming/app-data folders.

## Requirements

Required:

- Cyberpunk 2077
- RED4ext

Optional:

- a frame-time capture companion

GRSP works fully standalone. CapFrameX is the tested/recommended frame-time companion, but another profiler or compatible CapFrameX version can be linked.

## Installed game layout

The manager installs only the profiler DLL and its sibling data folder:

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

1. Run `G-REDscript-Profiler.exe`.
2. On **SETUP**, select the Cyberpunk 2077 folder.
3. GRSP verifies the game and RED4ext.
4. Optionally link a frame-time profiler executable and its capture/results folder.
5. Continue to **INSTALL -> CAPTURE -> RESTORE**.
6. Review the status list and use **INSTALL PROFILER**.
7. Give the run a capture title.
8. When the page reports **PROFILER IS READY!**, optionally launch the frame-time tool and then start Cyberpunk.
9. In game:
   - **F11 #1** = START
   - **F11 #2** = STOP + EXPORT
10. Close Cyberpunk before collecting.
11. Use **COLLECT RESULTS / CLEAR LIVE**.
12. When finished profiling, use **RESTORE ORIGINAL STATE**.

If Cyberpunk closes while a capture is active, shutdown acts as an implicit STOP and GRSP exports the useful work captured so far.

### Status language

The second page uses the same status convention as G-CET Runtime Profiler:

- ✅ confirmed good / completed
- ⚠️ optional, pending, unknown, or degraded-but-usable
- ❌ blocking error requiring attention

The optional frame-time companion never decides whether the core GRSP profiler is ready.

## Capture title

The manager writes the title to:

```text
red4ext\plugins\G-REDscript-Profiler\CaptureTitle.txt
```

The first non-comment line is read when F11 starts a capture. The normalized title becomes part of the capture folder name.

Example:

```text
CITY_DRIVING
```

produces a capture similar to:

```text
Capture_0001_CITY_DRIVING_<start_unix_ms>
```

## Existing installation rule

GRSP never overwrites an existing profiler DLL.

Before deployment it checks:

```text
red4ext\plugins\G-REDscript-Profiler.dll
```

- exact packaged DLL already present: reported as already installed/usable
- different G-REDscript Profiler DLL: installation is blocked
- legacy `redscript_profiler_alpha.dll`: installation is blocked so two profiler DLLs cannot load together

Normal guided usage should end with restore, so these conflicts should be uncommon.

## Results ownership

GRSP-owned live output is temporary game-side data.

On collection:

```text
live GRSP output
   ↓ copy
verify archive
   ↓
remove GRSP-owned live source
```

Collected standalone results are stored under:

```text
G-REDscript-Profiler\RESULTS\
```

External frame-time data follows a different hard rule:

```text
external profiler capture
   ↓ COPY ONLY
GRSP result\FrameTime\...
```

Files owned by an external profiler are never moved, deleted, or modified at source.

## Restore behavior

Restore requires Cyberpunk 2077 to be closed.

If uncollected GRSP output remains, it is archived first. The manager then removes the managed DLL and managed data.

One deliberate exception remains: the final empty folder

```text
red4ext\plugins\G-REDscript-Profiler\
```

is intentionally allowed to remain after restore.

Unknown or changed profiler DLLs are never blindly deleted.

## Collected result handoff

The game-side native capture is preserved, but the standalone archive is normalized to the same front-door pattern as G-CET so TOTAL receives a stable structure:

```text
Capture_0001_<TITLE>_<start_unix_ms>\
├─ GRSP_Report.html
├─ GRSP_Summary.json
├─ Data\
│  ├─ Runtime\
│  │  ├─ GRSP_Summary.csv
│  │  ├─ GRSP_ByMod.csv
│  │  ├─ GRSP_ByFunction.csv
│  │  ├─ GRSP_Timeline.csv
│  │  ├─ GRSP_Frames.csv
│  │  ├─ GRSP_Spikes.csv
│  │  ├─ GRSP_Markers.csv
│  │  └─ GRSP_FrameworkCandidates.csv
│  ├─ Developer\
│  │  ├─ RSP_FunctionMap.csv
│  │  ├─ RSP_CallSites.csv
│  │  ├─ RSP_SharedTargets.csv
│  │  ├─ RSP_Cadence.csv
│  │  ├─ RSP_WrapperChains.csv
│  │  └─ RSP_WorkMap.csv
│  └─ Metadata\
│     ├─ GRSP_Status.txt
│     ├─ RSP_Alpha_Status.txt
│     ├─ RSP_SessionIndex.csv
│     └─ other GRSP-owned collection metadata...
└─ FrameTime\                 (optional)
   ├─ CompanionManifest.json
   └─ <copied frame-time capture>
```

The two root files are the stable entry points: `GRSP_Report.html` for the results report and `GRSP_Summary.json` for machine consumption. Native measurement files are kept under `Data\`; optional external frame-time data remains under `FrameTime\`, matching the CET-side handoff shape.

When a recognized CapFrameX capture was copied, the report adds rendered frametime, CPU Active, GPU Active, frame-sequence synchronization, slow-frame/script overlap, recorded-spike overlap, worst-frame evidence, and a synchronized timeline. Hold **Shift** and use the mouse wheel over the graph to zoom around the pointer.

The framework section also performs a best-effort, read-only scan of the live REDscript source paths recorded by GRSP. This lets the report distinguish a mod that has **already adopted G-RedRuntime** (Scheduler, StateCache, ContextService, InputHub, EventBus, HookBus, DirtyFlags or GRedHotpathCache) from a mod that only presents a framework-candidate workload shape. A residual signal on an already integrated mod means inspect the remaining hot path; it is not a recommendation to integrate the framework again.

The public timeline uses 50 ms buckets and exposes capture-relative plus Unix timing for correlation with other profilers.

## REDscript mod layouts

GRSP does not require mods to adopt a profiler-specific structure.

Owner attribution follows the REDscript source path already emitted by the compiler:

```text
r6/scripts/ModName/.../*.reds  -> owner = ModName
r6/scripts/Foo.reds            -> owner = Foo
```

GRSP profiles the installed mod stack as-is, including user-created override/optimization patches.

## Headless interface

The same standalone EXE exposes the lifecycle interface used by higher-level orchestration:

```text
G-REDscript-Profiler.exe --status --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --install --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --title CITY_DRIVING --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --collect --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --report --capture "RESULTS\Capture_..." --json
G-REDscript-Profiler.exe --restore --game "D:\Games\Cyberpunk 2077" --json
G-REDscript-Profiler.exe --start --game "D:\Games\Cyberpunk 2077" --json
```

`--scenario` remains a compatibility alias for `--title`.

## TOTAL Profiler boundary

G-REDscript Profiler is standalone first.

G's Cyberpunk 2077 TOTAL Profiler consumes the **exact published standalone release unchanged**. TOTAL may orchestrate it, synchronize it with the CET profiler and CapFrameX, copy/read completed results, and correlate those outputs.

TOTAL does not ship a TOTAL-specific GRSP DLL or manager variant.

See `docs/TOTAL_INTEGRATION_CONTRACT.md`.

## Measurement interpretation

GRSP is not a whole-CPU profiler, GPU profiler, or complete VM trace.

`exclusive_instrumented_ms` is observed inclusive time minus nested calls that GRSP also observed. Native/base work can remain inside an observed wrapper boundary, and virtual targets do not always identify a concrete implementation owner.

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

Standalone manager app:

```powershell
dotnet publish manager\GRedscriptProfiler.Manager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

The public package places that managed app/runtime under `app\` and uses the small native `G-REDscript-Profiler.exe` launcher at the package root.

## Technical basis / credit

The function/source bind mapping and `BindFunction` + `InvokeStatic` + `InvokeVirtual` hook strategy are based on the open-source `redscript-dap` work by jekky / jac3km4 (MIT). `red4ext-rs` supplies the RED4ext Rust bindings and remains pinned to revision `c44146c`.
