# G-REDscript Profiler 0.5.0 Public Preview

This preview keeps the proven REDscript measurement core and turns it into a complete standalone product.

## Standalone product

- Adds the self-contained .NET 8 `G-REDscript-Profiler.exe` manager.
- Uses the public installed DLL name `G-REDscript-Profiler.dll`.
- Uses the sibling data folder `red4ext\plugins\G-REDscript-Profiler\`.
- Renames the user-facing capture label file to `CaptureTitle.txt`.
- Uses a two-page Setup → Install/Capture/Recovery workflow modeled on G-CET Runtime Profiler.
- Keeps GRSP standalone: only Cyberpunk 2077 + RED4ext are required.

## Optional frame-time companion

- Frame-time pairing is optional and never required for REDscript profiling.
- CapFrameX is the tested/recommended companion.
- Other profilers and compatible CapFrameX versions can be linked.
- CapFrameX configuration is inspected read-only where useful.
- External capture results are always copied only; their source files are never moved, deleted or modified.

## Installation / restore

- Installation never overwrites an existing `G-REDscript-Profiler.dll`.
- An exact already-installed DLL is reported as already installed and usable.
- A different GRSP DLL blocks installation.
- Legacy `redscript_profiler_alpha.dll` is detected and blocks installation to prevent two profiler DLLs loading together.
- Restore archives remaining GRSP live output, removes the managed DLL/data, and intentionally leaves only the final empty `G-REDscript-Profiler\` data folder.

## Collection

- GRSP-owned live output is archived with copy verification and then removed from the game folder.
- Capture folders and associated live metadata are therefore cleared from the game after collection.
- External frame-time files remain untouched at their source.

## Capture behavior

- F11 START / F11 STOP is unchanged.
- The profiler still starts paused.
- If Cyberpunk closes while a capture is active, shutdown acts as an implicit STOP and exports the work captured so far.

## Reporting retained from the 0.5.0 public layer

- `GRSP_Report.html`
- `GRSP_ByMod.csv`
- workload classification and wrapper-attribution cautions
- 50 ms `GRSP_Timeline.csv`
- Unix timestamps for cross-profiler correlation
- compact `Developer\` outputs for framework/optimization analysis
- shared-target owner evidence for profiling-driven G-REDruntime work

## TOTAL Profiler contract

G's Cyberpunk 2077 TOTAL Profiler consumes the exact standalone GRSP release unchanged. TOTAL-specific orchestration, synchronization, CapFrameX handling and correlation belong to TOTAL rather than a modified GRSP variant.
