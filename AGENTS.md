# GRSP development agent contract — profiler and native instrumentation only

## STOP / repository routing — mandatory before any work

**This repository is NOT the REDscript Resolver repository.** The former `dev/resolver-profiler-handoff` branch is obsolete. The **current native-only development branch is `dev/native-instrumentation`**. The old branch name never authorizes managed Resolver work here.

If a request involves **REDscript Resolver, R0–R10, R4/R5, generic optimization passes, scanner, generated .reds files, source admission, SCC compiler proof, idempotence, one combined ZIP, or cross-profiler interpretation**:
1. **Stop all managed Resolver changes in GRSP.** Open `giorgioherma/Gs-Cyberpunk2077_Optimizations` on **`dev/redscript/resolver_handoff`**.
2. Read that branch's `AGENTS.md`, `docs/integration/REDSCRIPT-RESOLVER-LOCAL-HANDOFF.md`, `docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md` and `docs/LATEST-WORK.md`; continue there.
3. Return to GRSP **only for a separately scoped native profiler/instrumentation change** (Rust VM hooks, F11, CSV evidence, install/restore). Keep source-owned native implementation here, and report its evidence/interface to GCO. Do not build a competing Resolver or move native profiler ownership into GCO.
4. **Never treat old GRSP Resolver documentation or a checkout of this branch as the current task destination.** Historical Resolver handoffs were archived byte-for-byte under GCO `docs/integration/reference/` and deleted here.

**Mandatory routing check:** before editing a file, ask whether the change creates/updates optimization analysis, planning, generation, source proof or compilation. If yes, the destination is **GCO**, even when a measured hotspot originated in a GRSP capture. No new managed Resolver directories, tests, CI or documents may be created here.

**Authoritative GRSP native instrumentation development branch:** `dev/native-instrumentation`. Do not continue development on `dev/resolver-profiler-handoff`. Protect GRSP `main` and standalone v1.0.0. **Latest user-approved scope correction: 2026-11-16.**

## Current ownership (supersedes 2026-11-15 managed Resolver decisions)

- **GRSP owns:** native REDscript profiler, capture/F11, measurement and developer CSV writers, quality/attribution diagnostics, standalone install/restore and profiler compatibility. Fix missing native instrumentation and original profiler lifecycle behavior here.
- **GCO owns:** independent managed REDscript Resolver, source-aware generic pass generator, source-state admission, real full-stack SCC verification, combined CET/REDscript analysis and final one-ZIP orchestration. **Develop all new REDscript Resolver/Generator/compilation implementation in GCO `dev/redscript/resolver_handoff`**, not in GRSP.
- **CET owns** its Lua profiler/Resolver/generator. **G-REDruntime owns** optional shared REDscript runtime services. GCO retains the one existing two-page UI, one .NET 10 runtime and one canonical Windows CI.
- **Authoritative current handoff:** [GCO-local REDscript Resolver decision](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/REDSCRIPT-RESOLVER-LOCAL-HANDOFF.md), [R0–R10 blueprint](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/GRSP_REDscript_Resolver_Blueprint.md), [GCO progress](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md).
- **Migration complete:** tested managed Resolver, tests and SCC runner were moved into GCO `src/GRedscript.Resolver` / `tests/GRedscript.Resolver.Tests`. [GCO CI #483 SUCCESS](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/actions/runs/43798781332): 41 CLI tests, private 2,319-file source, untouched/optimized SCC, 323 app/package contracts. GRSP's former `resolver/`, `tests/resolver/` and managed resolver workflow have therefore been **retired from this dev branch**; all history remains in Git. Do not rebuild or regenerate managed REDscript optimizer code in GRSP.

## Mandatory read order for profiler changes

1. `README.md`, `OUTPUT_SCHEMA.md`, `docs/LATEST-WORK.md`, `docs/PROFILER_SUMMARY_INTEROP_CONTRACT.md`, `docs/TOTAL_INTEGRATION_CONTRACT.md`.
2. Native source and profiler manager/install/restore contracts, current dev branch HEAD, and current GCO capture interface.
3. For optimizer/evidence questions only, use the **GCO** handoff/blueprint/progress linked above. The former `docs/RESOLVER_HANDOFF.md` and `docs/RESOLVER_PROGRESS.md` were migrated as exact archived records into GCO `docs/integration/reference/` and removed from this branch. All active managed Resolver development stays in GCO.

## Native instrumentation and safety

- GRSP raw evidence is measurement, not an installed mod inventory or an optimization authorization. Preserve stable IDs, F11 completeness, source attribution, frame boundaries and original captured evidence.
- For a measured generic opportunity, extend the **existing native GRSP writer** only when its missing evidence is specifically justified and bounded. Demonstrate overhead and capture/restore safety. Do not add LuaJIT allocation/GC hooks or invent performance improvements.
- Maintain the independent `.github/workflows/native-instrumentation.yml` source/native build path on `dev/native-instrumentation` for profiler changes. **Crash safety:** never add unmanaged pointer dereferences, additional VM hooks or allocations/re-entrant mutable state inside the measured opcode path without strict proof and regression tests. Do not rebuild GRSP's native DLL in GCO or silently republish the stable standalone release. The GRSP managed Resolver CI has been **removed** after the successful GCO-local source/contract/real-stack migration.
- Preserve standalone GRSP optional CapFrameX compatibility; GCO consumes real PresentMon and must not fake CapFrameX data or add CET/GRSP CPU as a total.

## Progress and verification

- Update `docs/LATEST-WORK.md` and relevant native handoffs after actual profiler changes; update GCO's REDscript handoff/progress for evidence-interface changes. Keep concrete branches, commits, CI status, untested gameplay, ownership and blockers.
- Protect original game files, GRSP native profiler, source-owner install/restore, stable releases and real captures. Green CI is not gameplay validation.
