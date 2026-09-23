# GRSP Framework Author Guide

GRSP's public report answers **which owners cost time**. The developer subset is for the next question: **what repeated work could safely be shared, cached, gated, indexed, or redistributed?**

## Start here

Use these files together:

```text
GRSP_ByMod.csv
GRSP_ByFunction.csv
GRSP_FrameworkCandidates.csv
Developer/RSP_Cadence.csv
Developer/RSP_SharedTargets.csv
Developer/RSP_WrapperChains.csv
Developer/RSP_CallSites.csv
```

Do not optimize from raw call count alone. High-frequency callbacks may be latency-critical. In particular, AI/NPC combat and reaction paths should normally keep their native cadence; make each call cheaper before considering throttling.

## Compatibility rule for shared frameworks

Treat the installed mod layout and public surface as inputs, not as a format that mods must convert into.

A shared runtime should therefore:

```text
keep third-party files at their normal r6/scripts paths
keep existing modules/classes/public methods stable unless a proven fix requires otherwise
require no GRSP-specific or framework-specific manifest from the mod
keep optional-mod adapters outside the framework core when they reference optional types
ship integrations as in-place differential overlays, not repackaged shadow copies
remain dormant when no integration consumes a service
```

"Unchanged mod format" does not mean every third-party source file must remain byte-identical. A measured integration may change implementation code, but it should preserve the mod's normal package structure and external compatibility contract.

## Common patterns

### High duty / polling

Evidence:

- high `active_frame_pct`;
- stable activity in IDLE/WORLD;
- repeated queries whose result rarely changes.

Candidate solution:

```text
event or dirty-state invalidation
conditional scheduler
adaptive cadence for non-critical presentation work
```

### Shared query duplication

Evidence:

- the same semantic target appears under many owners in `RSP_SharedTargets.csv`;
- `calling_owners` shows the exact installed source owners participating without requiring any mod metadata;
- `framework_candidate_ge3owners=true` identifies broad non-intrinsic/non-wrapper duplication worth source inspection;
- repeated `GetGame`, system resolution, quest/fact, equipment or blackboard access.

Do not treat that flag as permission to cache a result. It only identifies cross-owner duplication; lifetime, invalidation and gameplay semantics still determine whether a G-REDruntime service or per-mod adapter is safe.

Candidate solution:

```text
shared state/cache service
callback-scoped resolution
versioned invalidation
```

### Search / recomputation

Evidence:

- large descendant amplification;
- repeated scans or deterministic work;
- hot functions with many calls per active frame.

Candidate solution:

```text
index
memoization
precomputation
cheap early rejection
```

### UI rebuilding

Evidence:

- UI owner stays active while its window is not rendered;
- repeated widget search/set calls with unchanged values.

Candidate solution:

```text
sleep when hidden
rendered-visibility gate
dirty UI updates
retain widget handles
```

### Wrapper chains

Evidence:

- `WRAPPER_ATTRIBUTION_CAUTION` in `GRSP_ByMod.csv`;
- deep/repeated paths in `RSP_WrapperChains.csv`.

Candidate solution:

```text
early reject irrelevant cases
share callback-local state
consolidate only profiler-proven chains
```

Do not assume the wrapper's reported time is entirely its own work; wrapped native/base processing can remain inside the observed interval.

### Burst / stutter work

Evidence:

- low sustained ms/s but high `max_frame_exclusive_ms` or `max_spike_ms`;
- concentrated events in `GRSP_Spikes.csv` / `GRSP_Timeline.csv`.

Candidate solution for non-critical work:

```text
batching
admission limits
staggering / phase allocation
incremental initialization
```

Do not stagger gameplay-critical AI decisions merely to improve a chart.

## Multi-runtime correlation

GRSP 1.0.0 uses 50 ms timestamped owner buckets specifically so a framework project can correlate:

```text
GRSP_Timeline.csv
CET_Runtime_Profile_Timeline.csv
CapFrameX JSON
```

Use Unix timestamps for cross-tool alignment, then inspect capture-relative time within the aligned window.

A large CapFrameX hitch with little GRSP/CET time is evidence that the remaining stall is probably elsewhere: native engine work, streaming, AI/physics outside the observed scripting boundary, assets, or another native plugin.

## Framework design rule

Prefer this order:

```text
1. do not run irrelevant work
2. do not run unchanged work
3. do not rediscover stable state
4. do not scan what can be indexed
5. preserve latency-critical cadence
6. distribute remaining non-critical bursts across frames
```

The framework should reduce work first and redistribute only what cannot be removed.
