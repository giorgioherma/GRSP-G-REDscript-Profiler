# GRSP -> REDscript generic Resolver: development handoff

**Development branch:** `dev/resolver-profiler-handoff`. **Release baseline:** `main` (GRSP 1.0.0), kept unchanged.

## Decision (2026-11-04)

- Build **one unified user-facing Resolver** for CET and REDscript, with **separate source-aware optimization backends**. Do not transplant REDscript parsing/compilation into the CET Lua/0-Engine generator.
- CET remains owned by `giorgioherma/G-CET_Runtime_Profiler` and its authoritative `dev/resolver-profiler-handoff` branch. GRSP remains the authoritative REDscript *measurement* producer; `G-REDruntime_Framework` owns its runtime modules. The future unified app owns orchestration and REDscript Resolver backend integration, while invoking or adapting the existing CET Resolver rather than duplicating it.
- REDscript default is **generic optimization only**. Do not build an extensive semantic rule catalog. Optional user-selected stack-specific semantics may be developed later with explicit source and gameplay proof.
- The old TOTAL Profiler is historical UI/capture-correlation reference, **not** an authoritative CET component or dependency. No legacy CET binaries or installers are carried forward.
- Source inputs are the user's *current installed* `r6/scripts` tree (read-only). An older GRSP capture is supporting evidence, never proof that a mod is currently installed. Do not distribute original third-party mod sources.

## Existing GRSP evidence — consume before adding instrumentation

`GRSP_Summary.json` is a compact view and intentionally truncates top-owner/function lists. It is **not** a complete optimization input.

Use complete captured `Data/Developer/RSP_FunctionMap.csv`, `RSP_CallSites.csv`,
`RSP_SharedTargets.csv`, `RSP_Cadence.csv`, `RSP_WrapperChains.csv` and `RSP_WorkMap.csv`, alongside `Data/Runtime/GRSP_ByMod.csv`, `GRSP_ByFunction.csv`, `GRSP_Frames.csv`, `GRSP_Timeline.csv` and capture-quality metadata. Preserve original raw files, IDs, units, provenance, uncertainties, and source paths. The existing common summary `interop.contractVersion=1.0` remains unchanged.

GRSP observes REDscript call-edge activity; this is **not** a whole REDengine instruction trace or an allocation profiler. Nested/wrapper time and cross-owner edges can overlap. Never sum them blindly or claim native/collector work as REDscript-owned.

## Resolver instrumentation additions, in order of necessity

1. **Lossless complete handoff validation**: stable source function/callsite and owner identities, capture quality and dropped-event diagnostics, full callsite/cadence/active-frame data; distinguish unobserved, unavailable, and genuinely zero work. Prefer extending existing GRSP output writers/report model, not a duplicate hidden exporter.
2. **Source-aware applicability evidence**: match captured callsites to the user's live `.reds` source and its actual function/method/receiver shape; flag stale or moved source and unproven compiled ownership. Do not use version locks, guessed method associations or formatting-only regex admission.
3. **Framework job/dispatch visibility if a candidate requires it**: per-job actual runs/s, cost, wake/pause events and dependency/lifecycle evidence; no claim of dormancy from a scheduler registration alone. Attribute framework vs client time without double counting.
4. **Allocation/object creation investigation, conditional**: add bounded native measurement only after a specific safe REDengine allocation boundary and attribution coverage are demonstrated. Do not copy CET's LuaJIT allocation/GC hooks or G-GC controller into GRSP.

## Generic REDscript optimization boundary

Investigate generic opportunities using *both* complete profiler evidence and current source proof: repeated immutable same-scope reads, safe guard ordering, duplicate pure provider lookups, redundant background polling with a complete event/invalidation proof, and framework input/dispatcher consolidation only when equivalence and ownership are demonstrated. Source-first safe generic candidates may be considered without a matching capture row, but no observed CPU benefit is fabricated.

Preserve callback order, native/REDscript execution cadence, return and side effects, exception behavior, public signatures, player/session lifetimes, listener registration/cleanup and compile-time optional-mod dependencies. A `G-REdruntime` dependency is introduced **only if the selected optimization requires and can verify it**. Existing framework operations are recognized; do not add duplicates.

## Generation/validation

- Separate REDscript source-admission and generator implementation: pristine, partial and completed source recognized independently; report no new work when fully proven.
- Stage source edits and any adapters from **current installed originals**, then invoke the authoritative supported REDscript compiler on a fully resolved composed input before offering deployment. Failed compilation, missing dependency or source drift => refuse; do not patch generated broken output after the fact.
- Preserve original file paths, copy/verify backups, and never silently overwrite live mods. Generate a reviewable changed-files overlay and source manifest; deployment/restore is explicit and transactional.
- Verify source integrity, compile, full-stack compatibility, idempotence, dependency ownership and before/after GRSP + optional CapFrameX performance. CI is not gameplay validation.
- CET's generic -> native -> generic finalization -> semantic ordering remains within CET, not a REDscript parser requirement.

## First development checkpoint

Audit the **existing complete GRSP raw outputs** against the information needed for the first one or two source-proven generic rule families. Add only the demonstrably missing instrumentation in this GRSP branch, with regression tests and proof of bounded recording overhead. Leave `main`, public 1.0.0 release and the G-REdruntime `main` unchanged. New unified-app repository and REDscript generator source are **not yet created**.

**Status:** architecture and handoff recorded; no new profiler instrumentation or REDscript optimization has yet been implemented, compiled or gameplay-validated.
