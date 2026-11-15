# GRSP -> REDscript generic Resolver: development handoff

**Development branch:** `dev/resolver-profiler-handoff`. **Release baseline:** `main` (GRSP 1.0.0), kept unchanged.

## Approved ownership and REDscript development (2026-11-15)

- **GRSP owns REDscript instrumentation, the new REDscript Resolver, generic-first source-aware Generator, and full-stack REDscript compilation validation.** This supersedes the older GCO-owned Resolver/Generator language below. Work only on GRSP `dev/resolver-profiler-handoff`; `main` and GRSP 1.0.0 stable behavior remain unchanged.
- **GCO owns only cross-domain planning, verified sealed evidence transport, actual PresentMon-backed consolidated reports, invoking CET/GRSP headless owner interfaces, and the final combined game-relative ZIP.** No duplicate REDscript parser/generator in GCO. GCO development branch: `feature/redscript-resolver-integration`, branched from its completed CET/PresentMon integration feature; GCO `main` stays protected.
- **Canonical reviewed blueprint:** [GRSP-owned REDscript Resolver blueprint](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP_REDscript_Resolver_Blueprint.md); [GCO progress ledger](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md); [GRSP owner progress ledger](RESOLVER_PROGRESS.md). Read before editing. The 61-file gameplay-verified partial reference is the *only currently approved mod-specific semantic portfolio*; source proofs, fresh captures and full compiler validation still govern applicability.
- **Agents must promptly update** `docs/RESOLVER_PROGRESS.md` and `docs/LATEST-WORK.md` in the same development change (or immediately after exact CI completion) with commit SHA, contracts/results, stage status, decisions, failures, source/gameplay/compile proof and next step. On ownership or interface changes, update this handoff and GCO decisions/progress as well. Never mark CI green or gameplay verified without exact evidence.
- Fresh CET + GRSP + PresentMon captures select and prioritize generic opportunities across current live sources; GCO supplies genuine `PRESENTMON_2_6` chronology, or `NOT_CAPTURED` when absent. Keep standalone GRSP optional CapFrameX compatibility; do not fabricate or fall back to CapFrameX in GCO. GRSP and CET CPU domains are not additive.
- **Enforced sequencing (2026-11-15):** Fresh measured generic discovery → implement/compile/validate high-value generic passes (with narrowly proven GRSP and optional G-REDruntime enhancements as needed) → inspect *actual generic output* against the approved 61-file reference, at the level of each behavioral transform → port **only** verified `SEMANTIC_REMAINDER`, if any. Before generic proof, R1 may only index private reference metadata, never implement 61 semantic recipes. R7 requires a recorded five-state matrix: `GENERIC_COVERED`, `ALREADY_PRESENT`, `SEMANTIC_REMAINDER`, `UNPROVEN_OR_DRIFTED`, `NOT_APPLICABLE`. Zero semantic changes can be the correct result; gameplay/compiler proof is still required before accepting any remainder.
- Do not begin source rewriting until source-state admission and measurements support it. No silent live game writes, fixed mod inventory, broad semantic catalog, source-format regex guessing, duplicate patches, or mandatory framework dependency unless proven.

## Historical initial decision (2026-11-04; ownership corrected above)

- Build **one unified user-facing Resolver** for CET and REDscript, with **separate source-aware optimization backends**. Do not transplant REDscript parsing/compilation into the CET Lua/0-Engine generator.
- CET remains owned by `giorgioherma/G-CET_Runtime_Profiler` and its authoritative `dev/resolver-profiler-handoff` branch. GRSP owns REDscript measurements **and** its source-aware Resolver/Generator and compiler validation; `G-REDruntime_Framework` owns optional runtime modules. The existing GCO app invokes the separate CET and GRSP owner backends; it does not reimplement their optimizers.
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

## Current priority gate: measured generic passes before portfolio semantics

- The initial private portfolio inventory is **reference bookkeeping only**. Do not create semantic transforms from those 61 files first.
- Source-wide ranked generic evidence (not a preset rule/mod list), conservative structural admission, compiler-checked source-state handling and first safe generic outputs must be verified before a semantic gap matrix can authorize any residual approved recipe.
- If a reusable optimization requires missing measurement or shared scheduling/cache services, evolve GRSP/G-REDruntime at their respective sources before/during generic work; do not turn that missing evidence into guessed semantics.
- All decisions, including unproven candidates and exclusion reasons, must be logged promptly in GRSP and GCO progress/decision handoffs.

## First development checkpoint

Audit **fresh complete GRSP raw outputs** and source structure for the highest-value broadly applicable generic rule families; do not assume historical G0/G1 priority or a 61-file semantic queue. Add only the demonstrably missing instrumentation in this GRSP branch, with regression tests and proof of bounded recording overhead. Leave `main`, public 1.0.0 release and the G-REdruntime `main` unchanged. The unified GCO app and its REDscript development branch **already exist**; the GRSP REDscript Resolver/Generator implementation is **not yet created**.

**Status:** architecture and handoff recorded; no new profiler instrumentation or REDscript optimization has yet been implemented, compiled or gameplay-validated.

## 2026-11-15 — native instrumentation strict GCO F11 feature and CET proximity plan

- The optional gco_explicit_f11 Rust Cargo feature now source-owns the F11 STOP-only GCO variant; independent GRSP source CI #2 PASS on 61b0be963b9c16827be5c4ea5df12b5b61bd5e11, manual native build pending. GCO is configured to import only an exact verified owner artifact; no in-GCO native instrumentation compile or automatic fallback. Old standalone GRSP behavior is preserved.
- CET native player/location service is **planned by CET Resolver** in future combined optimized ZIP. GRSP may use its source-proven NEAR/FAR/UNKNOWN dependency for generic REDscript work after typed ABI, strict-live and lifecycle proof. This is not a reason to skip unresolved FlushingEtiquette minimap wrappedMethod work; see [GCO evidence note](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP-NATIVE-PLAYER-PROXIMITY.md).
