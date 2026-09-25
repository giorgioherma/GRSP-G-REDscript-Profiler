# Profiler Summary Interop Contract

Contract version: **1.0**

This contract defines the small common surface shared by G-REDscript Profiler and G-CET Runtime Profiler for combined consumers such as G's Cyberpunk 2077 TOTAL Profiler.

The standalone profilers intentionally keep domain-specific sections. REDscript and CET do not measure the same execution layer, so similarly named values are only standardized when their units and interpretation are genuinely compatible.

## Identification

Each summary exposes:

```json
"interop": {
  "contractVersion": "1.0",
  "producer": "...",
  "domain": "redscript | cet"
}
```

`schemaVersion` remains producer-specific and may evolve independently.

## Common capture fields

Both summaries expose these canonical fields under `capture`:

- `title` — normalized capture title shared by the synchronized run.
- `durationSeconds` — duration represented by that profiler's measured capture.
- `measuredCalls` — calls/events measured by that profiler.
- `callsPerSecond` — measured call/event rate in that profiler domain.
- `exclusiveMsPerSecond` — observed exclusive measured work per second in that profiler domain.
- `measuredOneCorePct` — `exclusiveMsPerSecond / 10`, expressed as the equivalent percentage of one CPU core.

These fields provide common units and presentation names. They do **not** imply that REDscript calls and CET callbacks are the same kind of event.

Producer-specific compatibility aliases and extra capture metrics may remain beside these fields.

## Common owner fields

Entries in `topOwners` share:

- `owner`
- `infrastructure`
- `calls`
- `callsPerSecond`
- `exclusiveMsPerSecond`
- `measuredOneCorePct`
- `measuredSharePct`

Additional fields remain domain-specific. For example, REDscript frame activity and wrapper attribution are not forced into CET semantics, and CET callback maxima are not renamed to REDscript frame maxima.

## Frame-time surface

When a standalone profiler is paired with CapFrameX, both summaries use the same `frameTime.frames` field names for the common frametime/CPU/GPU statistics.

The common synchronization keys are:

- `frameTime.sync.quality`
- `frameTime.sync.correlated`
- `frameTime.sync.exactAlignment`
- `frameTime.sync.alignmentMethod`

Producer-specific alignment diagnostics remain available beside them.

`frameTime.worstFrames[].frameIndex` is the canonical CapFrameX frame index. Domain-specific evidence remains separate, such as REDscript `scriptMs` and CET `cetWindowMs`.

In TOTAL Profiler there is one synchronized CapFrameX capture for the whole run. TOTAL may therefore use the shared CapFrameX source directly and treat standalone `frameTime` sections as optional producer-local evidence.

## Findings and data index

Both summaries keep the same finding shape:

- `Title`
- `Evidence`
- `Explanation`

Both also expose `dataIndex` with the same categories:

- `runtime`
- `scheduler`
- `developer`
- `metadata`
- `frameTime`
- `other`

A category may be empty when it does not apply to that profiler.

## Combined interpretation rule

Common units are intended for compact side-by-side presentation, not blind arithmetic across profiler domains.

In particular:

- do not add REDscript and CET `exclusiveMsPerSecond` and call the result total CPU time;
- do not subtract either or both from CapFrameX frametime to infer an engine/native remainder;
- keep REDscript-specific and CET-specific diagnosis in separate sections where their measurement semantics differ;
- use the raw timeline/marker CSVs for precise cross-profiler synchronization and hitch correlation.

The JSON summaries are the normalized summary/metadata surface. Raw profiler data remains the authoritative fine-grained correlation source.
