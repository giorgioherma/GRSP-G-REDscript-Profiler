# TOTAL Profiler integration contract

`G-REDscript-Profiler` is a standalone product first. G's Cyberpunk 2077 TOTAL Profiler consumes the exact standalone release unchanged.

TOTAL must not:

- maintain a separate GRSP DLL build;
- rename or patch the bundled GRSP profiler DLL;
- maintain a second GRSP install/restore implementation;
- redirect GRSP's standalone result folder;
- create a TOTAL-specific GRSP capture mode or output schema.

TOTAL may:

- bundle the exact standalone GRSP release under its own components directory;
- invoke `G-REDscript-Profiler.exe` headlessly;
- verify GRSP status;
- set the capture title through the standalone headless interface;
- ask GRSP to collect completed live output;
- copy/read the completed standalone result as input to TOTAL;
- orchestrate GRSP together with the exact standalone CET profiler and CapFrameX;
- correlate the three result sources and generate TOTAL-owned combined output.

## Restore behavior

TOTAL must call the standalone GRSP restore implementation rather than recreating cleanup logic. Once the standalone manager ownership marker exists, `RESTORE ORIGINAL STATE` is the authoritative exit path: changed or missing managed DLLs, changed profiler-owned data, read-only runtime files, and unreadable manager-state contents do not remove the restore path. Install remains conservative and may refuse unknown/pre-existing state.

## Dependency rule

The files bundled by TOTAL for a GRSP release must be byte-identical to the published standalone package for that release.

Combined-capture synchronization, session orchestration and correlation belong in TOTAL. GRSP remains usable without TOTAL and without a frame-time profiler.

If TOTAL needs a new GRSP lifecycle capability, expose it in the standalone manager/headless contract first rather than creating a TOTAL-only profiler variant.

## Frame-time companion

The standalone GUI may optionally pair with an external frame-time capture tool. CapFrameX is the tested recommendation, but it is not a GRSP dependency.

That companion layer is convenience-only and is not part of TOTAL's GRSP dependency contract. TOTAL may manage its own CapFrameX orchestration while still consuming the exact standalone GRSP package unchanged.

## Result ownership

GRSP owns its live output under:

```text
red4ext\plugins\G-REDscript-Profiler\RESULTS\
```

Standalone collection verifies the archive copy and then removes the GRSP-owned live source so the game folder stays clean.

The standalone archived-capture handoff is stable:

```text
<capture>/
├─ GRSP_Report.html
├─ GRSP_Summary.json
├─ Data/
│  ├─ Runtime/
│  ├─ Developer/
│  └─ Metadata/
└─ FrameTime/      # optional
```

TOTAL should treat `GRSP_Summary.json` as the machine-facing entry point and may read deeper native files under `Data/` when required. This mirrors the standalone CET profiler's root-report/root-summary plus nested-data contract and avoids TOTAL-specific remapping.

Files belonging to an external frame-time profiler are always copied only. Their source files are never moved, deleted or modified.
