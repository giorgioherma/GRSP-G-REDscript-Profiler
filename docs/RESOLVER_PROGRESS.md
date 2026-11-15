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

### 2026-11-15 — R6 instrumentation pass 1 (developer callback pressure, no new hook)

- Added `src/callback_pressure.rs` with post-STOP callback-family pressure/target CSVs and actual Rust unit tests. Output is sourced from existing callsite, target and game-frame counters; callback **entries and unchanged-state rates remain unknown**. No semantic rewrites or profiler install/restore changes.
- New GRSP-only `.github/workflows/native-instrumentation.yml`: branch push runs Rust/source contracts; **native release DLL build is manual**, SHA256-provenance artifact only. User requested CET-style separate native CI. Not integrated into GCO package until verified capture and explicit source-owner pin.
- No native CI result, new in-game capture, REDscript compilation, or gameplay evidence is claimed by this commit. **Next:** source-contract CI and manual native build, then one new capture for first/after instrumentation comparison; preserve GRSP v1.0.0 release.

### 2026-11-15 — independent GRSP instrumentation source CI #1 SUCCESS

- [GRSP instrumentation CI #1](https://github.com/giorgioherma/GRSP-G-REDscript-Profiler/actions/runs/43716286993) **SUCCESS** at `19c90f43acbf93d9785be6ec3dfe29814d070e7d`. Validated actual Windows Rust `cargo test --lib --locked`: **2 passed, 0 failed** for callback classification and outgoing edge/frame-burst distribution; `cargo check --locked` passed; three source/lifecycle boundary contracts passed.
- **Native release DLL build job SKIPPED by design** because the run was a push, not an explicit `workflow_dispatch`. No native instrumentation artifact, GCO update, new game capture, in-game profiler safety or REDscript gameplay proof yet. Keep manual native code build distinct from source contract CI, per CET owner model.
- **Next:** manually dispatch the same GRSP instrumentation workflow on `dev/resolver-profiler-handoff`, verify release DLL/SHA256 and one new game capture, then evaluate true missing callback-entry/unchanged-state evidence before expanding hooks.

### 2026-11-15 — CI isolation blocker resolved without GRSP stable release changes

- The native release workflow remains manual-only on `dev/resolver-profiler-handoff` and its source CI #1 passed. **Do not modify stable `main`** to expose GitHub's manual UI button: its existing default workflow repackages/publishes v1.0.0 on push. Dispatch by the documented authenticated `gh workflow run native-instrumentation.yml --repo giorgioherma/GRSP-G-REDscript-Profiler --ref dev/resolver-profiler-handoff` or REST API. Native dispatch itself is not yet executed in this stage.
- GCO report rankings are independently green in [#433](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/actions/runs/43716510455) without replacing the GRSP native DLL. Next required owner evidence is manual native build with artifact hash, then a fresh real game capture of `RSP_CallbackPressure.csv` and `RSP_CallbackTargets.csv`.

### 2026-11-15 — native GCO build variant source owner established

- GRSP owns `gco_explicit_f11` feature code and strict F11-stop marker directly in `src/lib.rs`; default standalone shutdown diagnostics preserved. GRSP native workflow release builds this feature **only by explicit manual dispatch**, GCO will consume a verified release artifact instead of compiling feature itself.
- Tests/CI **pending** for this change; true installed GRSP callback-entry counts and unchanged-value rates remain unknown. No REDscript optimizer source or user installation modified.

### 2026-11-15 — GRSP strict-F11 source feature CI #2 SUCCESS; native artifact still manual

- [Independent GRSP CI #2](https://github.com/giorgioherma/GRSP-G-REDscript-Profiler/actions/runs/43717964494) **SUCCESS** on source feature commit 61b0be963b9c16827be5c4ea5df12b5b61bd5e11. Windows compiled/tested original standalone and opt-in gco_explicit_f11 variants: **2/2 Rust unit tests passed in each build**, both cargo check commands passed; source/lifecycle contracts passed. Manual-only native release DLL job was **SKIPPED as required**. The existing standalone GRSP main release remains untouched.
- GCO main commit f0e30e92ec470aa7cfd5c2544f83b308e49f15a1 provides a **fail-closed manual owner-artifact consumer**: exact SHA/workflow/feature/provenance, no GRSP new native DLL built by GCO, original baseline source path retained on ordinary pushes. The opt-in GCO artifact build and actual F11 game capture remain **NOT TESTED** until a manually built GRSP native DLL exists. RSP_CallbackPressure.csv records outgoing VM call edges, never true callback entries.
- REDscript native player + NEAR/FAR source-owned cache: CET Resolver can deploy typed-native static locations and fail-open Lua host; plan as a **known future generated dependency** but any REDscript bridge needs typed ABI and source proof. FlushingEtiquette static toilets/sinks are a generic native FAR candidate; its unresolved minimap base proxy is a distinct issue. [GCO source-backed handoff](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP-NATIVE-PLAYER-PROXIMITY.md). Do not implement portfolio semantic remainder before generic rules.
- **Next:** manual GRSP native DLL CI and resulting GCO importer test, then fresh in-game GRSP developer CSV validation, live-source proof and generic candidate evaluation.

### 2026-11-15 — GCO baseline CI #435 green; GRSP native manual release remains outstanding

- [GCO #435](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/actions/runs/43719910633) SUCCESS on 0be794152b30a11c0eebea0b03883ba27543ccd8: 323 GCO package/install/restore contracts, real three-profiler reporter, WPF regression. The normal GCO bundle still uses the historical stable GRSP native source and DOES NOT contain the new GRSP callback-pressure CSV writer.
- GCO owner-native import now resolves exact source SHA from the successful manual GRSP workflow run rather than a stale static dev HEAD. Strict F11 + SHA/provenance gates are already committed; independent GRSP gco_explicit_f11 Rust checks passed CI #2. Manual release DLL build and first captured in-game evidence NOT YET DONE. Preserve GRSP stable main and gameplay/capture requirements.

### 2026-11-15 — automatic independent owner-native CI handoff to GCO main

- Separate GRSP workflow now emits the GCO strict-F11 native instrumentation DLL+source SHA/Cargo.lock/checksum/provenance on approved dev pushes after unit/compiler/source gates; no manual dispatch prerequisite. Native source commit `5046d331`, test correction `f36e9d8` following #3 real failed outdated-test contract; owner CI #4 PENDING. Stable GRSP main remains protected.
- GCO main automatically discovers the latest **successful owner-native** artifact, imports that SHA-proven DLL into existing GCO package and has removed the legacy local GRSP Rust patch/build/cached fallback. No change to profiler manager install/restore or R7 semantics; actual F11 capture acceptance outstanding.
