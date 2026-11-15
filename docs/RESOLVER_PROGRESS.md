# GRSP REDscript Resolver/Generator — live progress and decisions

**Branch:** `dev/resolver-profiler-handoff`. **Owner:** GRSP. **Current baseline:** `ece5b8e2c65690bb914cc1f90b3c1bd79d740ef8` before this documentation change; stable `main` and 1.0.0 release protected.
**Canonical blueprint:** https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP_REDscript_Resolver_Blueprint.md
**GCO integration branch:** `feature/redscript-resolver-integration`, forked from completed CET/PresentMon baseline `3caccafbdf39ec2b6474b69ed8f34c9392e2ff20`.

## Current stage

| Stage | Status | Evidence/next gate |
| --- | --- | --- |
| R0 owner contract | Documentation setup | GRSP owns Resolver/Generator/compiler; GCO owns coordination/merge; verify both repo handoffs |
| R1 private approved reference inventory | NOT STARTED | Index 61 paths/owners/provenance only; **no semantic implementation before generics** |
| R2 fresh capture analysis | MEASURED SHORTLIST; SOURCE PROOF PENDING | Verified one new complete 309.6 s WORLD three-profiler capture, analyzed full GRSP developer CSVs; live source identity, attribution, multiple scenarios still pending; see [audit](MEASURED-GENERIC-OPPORTUNITIES-2026-11-15.md) |
| R3 REDscript Resolver JSON | NOT STARTED | GRSP-owned versioned JSON from real GRSP+PresentMon evidence |
| R4 source admission | NOT STARTED | Pristine/partial/complete/drift/dependency structural states |
| R5 source-wide measured generic passes FIRST | NOT STARTED | Fresh data → rank generic opportunities across installed mods → compile/idempotent validated pass output |
| R6 GRSP/G-REDruntime generic enablers | CONDITIONAL | Interleave with R2–R5 as needed to unlock generic value; verify native overhead/runtime contracts |
| R7 generic-output gap matrix then semantic remainder | BLOCKED BY GENERIC COVERAGE | After validated generics classify all 61 per operation; implement only structural SEMANTIC_REMAINDER; zero is valid |
| R8 compile and output proof | NOT STARTED | Actual full effective stack + source-state/idempotence/negative tests |
| R9 GCO integration | NOT STARTED | .NET10 headless contract, one CI and real output, preserve CET |
| R10 combined ZIP/gameplay | NOT STARTED | GCO collision-safe merged ZIP, game validation, fresh before/after |

## Change log and agent protocol

### 2026-11-15 — REDscript owner handoff preparation

- Accepted GRSP ownership; prior GCO-owned REDscript backend decision superseded. Detailed implementation plan lives in the new GCO REDscript feature branch, **not** in a duplicated GRSP blueprint.
- Source/code changes: **none**. New GRSP optimization code, compiler execution, gameplay acceptance and fresh profiler captures: **not performed**. CET source generation last verified in GCO milestone CI #416; GCO consolidated genuine PresentMon report CI #426.
- **Next (historical initial note; superseded by sequencing below):** private 61-file inventory is read-only only. Fresh measurements and structurally proven generic passes come before any portfolio semantic work. No optimization generated from the old sample ZIP.

**MANDATORY FOR EACH FOLLOW-UP:** Append a dated entry with exact changed paths/commit, measured source/capture identity (not secrets), testing/CI run and outcome, compiler/gameplay outcome, new decisions, blockers and next single action. Update this ledger and `docs/LATEST-WORK.md` promptly, before handing off work. Record partial/red runs honestly.

### 2026-11-15 — explicit generic-before-portfolio execution gate

- **Owner decision:** After fresh CET/GRSP/PresentMon captures, use current full-stack source and native evidence to rank generic opportunities; develop measured reusable passes FIRST, evolving GRSP instrumentation/G-REDruntime if needed. Compile and prove actual generic output and idempotence. Then compare each operation in the 61 approved reference files with generic output and existing optimization state.
- **R7 is strictly blocked by an R5/R6 generic coverage report** and a five-state portfolio gap matrix: `GENERIC_COVERED`, `ALREADY_PRESENT`, `SEMANTIC_REMAINDER`, `UNPROVEN_OR_DRIFTED`, `NOT_APPLICABLE`. Only remaining structurally proven approved semantic recipes can be generated. Zero residual work is acceptable; 61 modified files are not a target. R1 only indexes private metadata.
- **Docs-only verification:** no new native GRSP measurements, generator source, compiler run, CI result or gameplay test. The existing GCO CET #416 and PresentMon reporting #426 remain unchanged prior baselines. GCO blueprint and progress ledger updated in matching GCO documentation commit `a064acf511d94d75922cf33dd295cea80513870b`.
- **Next:** obtain fresh profiler evidence and current effective REDscript source to rank first generic opportunities; do not implement 61 semantic recipes first.

### 2026-11-15 — R2 first fresh-capture generic opportunity audit (read-only)

- **Evidence only:** locally inspected the user's newly collected/restored GCO triple capture, real consolidated `PRESENTMON_2_6` chronology and all complete GRSP developer CSVs. Private raw capture and third-party `.reds` sources **not added to this repository**. [Detailed measured opportunity audit](MEASURED-GENERIC-OPPORTUNITIES-2026-11-15.md) records exact rates, source-match limits, counterexamples and gates.
- Two general families to test: **input/action dispatch + unchanged-device-state gating** (large `custom_quickslots` and `PlayerPuppet.OnAction` evidence) and **UI/update duplicate-state suppression** (large Dark Future `SetData` calls), plus source-local stable reads and conditional poller consolidation. Exact `OnUpdate` is sparse; broad `Update` methods vary by semantics. The largest `FlushingEtiquette` wrapper is mainly an *unresolved proxy* and is **not** safe optimization attribution.
- **Stage R2 is a shortlist, not completion**: earlier local `r6LIVEnow.zip` source is only a dated snapshot, 3 captured hot source paths fail path-match, and callback values/invalidation are not currently measured. Need true current live-source admission and potentially owner-native targeted instrumentation. No generator/semantic work or gain claims.
- **Tests:** offline ZIP/CSV/JSON consistency and source-neighborhood audit only. CI **NOT RUN**; new GRSP compiler validation **NOT RUN**; gameplay **NOT TESTED**. No source changes. **Next:** source-authoritative R3 evidence schema and high-impact generic A, preserving input consumption and lifecycle; only later R7 gap matrix.
