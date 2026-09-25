# GRSP 1.0.0 Output Schema

## Public files

### `GRSP_Report.html`
Human-first post-capture interpretation rebuilt by the standalone manager after collection. It preserves the native measurement outputs and adds sustained workload, call-volume, hot-function, heavy-frame, spike, capture-health, G-RedRuntime/framework and optional CapFrameX correlation views.

When frame-time data is recognized and safely aligned, the report includes a synchronized rendered-frametime / CPU Active / GPU Active / measured REDscript graph with Shift + mouse-wheel zoom.

### `GRSP_Summary.json`
Compact machine-readable counterpart to the enhanced HTML report. Includes capture/trust metrics, top owners/functions, script-heavy frames, recorded spikes, framework signals, best-effort live-source G-RedRuntime integration detection, adaptive findings and optional CapFrameX synchronization/correlation evidence.

### `GRSP_Summary.csv`
One row per capture. Includes capture Unix start/stop, duration, observed call rate, total exclusive-instrumented work, average/P95/P99/max script work per game frame, spike counts, trust fields and top owner.

### `GRSP_ByMod.csv`
One row per source owner/mod, sorted by `exclusive_ms_per_sec` descending.

`workload_pattern` is a presentation heuristic:

- `SUSTAINED` — significant persistent observed cost;
- `BURSTY` — low average but large frame/call burst;
- `MIXED` — both sustained and bursty;
- `BACKGROUND` — below those presentation thresholds.

`attribution_note=WRAPPER_ATTRIBUTION_CAUTION` means a meaningful share of the owner's traffic is wrapper plumbing. Inspect source/wrapper chains before attributing all observed time to the mod's own logic.

### `GRSP_ByFunction.csv`
Stable-function-ID function ranking with source path/line, call rate, exclusive-instrumented time, active-frame percentage and spike counts.

### `GRSP_Timeline.csv`
50 ms per-owner buckets. Primary join surface for the CET profiler timeline.

Time fields:

```text
bucket_start_ms / bucket_end_ms
bucket_start_unix_ms / bucket_end_unix_ms
```

### `GRSP_Frames.csv`
Per game frame script-side activity with both capture-relative and Unix time.

### `GRSP_Spikes.csv`
Chronological observed call events at or above the profiler's 1 ms threshold, including Unix time and stable callsite ID.

### `GRSP_Markers.csv`
START / STOP / quiescence markers with QPC, relative time and Unix time.

### `GRSP_FrameworkCandidates.csv`
Heuristic signals for framework authors. Signals are triage hints, never automatic rewrite instructions.

The enhanced report places these signals beside a best-effort read-only scan of the source paths recorded in `GRSP_ByFunction.csv`. If current source files are still available, it detects existing G-RedRuntime usage such as Scheduler, StateCache, ContextService, InputHub, EventBus, HookBus, DirtyFlags and GRedHotpathCache. Source availability is not required for report generation.

## Developer files

- `RSP_FunctionMap.csv` — stable function/source map.
- `RSP_CallSites.csv` — detailed callsite aggregates.
- `RSP_SharedTargets.csv` — target aggregates with the exact sorted `calling_owners` set. `framework_candidate_ge3owners=true` marks non-intrinsic/non-wrapper targets observed from at least three owners; it is evidence for inspection, not proof that sharing/caching is semantically safe.
- `RSP_Cadence.csv` — observed call cadence distribution.
- `RSP_WrapperChains.csv` — repeated wrapper chains.
- `RSP_WorkMap.csv` — workload classification view.

## Cross-tool correlation

Use Unix milliseconds to align capture windows between GRSP, the CET profiler and CapFrameX. Once aligned, use capture-relative milliseconds for local event inspection.

GRSP's 50 ms timeline is intentionally the same granularity previously used by the CET runtime profiler project so owner activity can be compared bucket-for-bucket after aligning START markers.
