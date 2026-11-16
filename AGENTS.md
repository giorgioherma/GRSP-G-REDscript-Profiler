# GRSP development agent contract — profiler and native instrumentation only

**Authoritative GRSP development branch:** `dev/resolver-profiler-handoff`. Protect GRSP `main` and standalone v1.0.0. **Latest user-approved scope correction: 2026-11-16.**

## Current ownership (supersedes 2026-11-15 managed Resolver decisions)

- **GRSP owns:** native REDscript profiler, capture/F11, measurement and developer CSV writers, quality/attribution diagnostics, standalone install/restore and profiler compatibility. Fix missing native instrumentation and original profiler lifecycle behavior here.
- **GCO owns:** independent managed REDscript Resolver, source-aware generic pass generator, source-state admission, real full-stack SCC verification, combined CET/REDscript analysis and final one-ZIP orchestration. **Develop all new REDscript Resolver/Generator/compilation implementation in GCO `dev/redscript/resolver_handoff`**, not in GRSP.
- **CET owns** its Lua profiler/Resolver/generator. **G-REDruntime owns** optional shared REDscript runtime services. GCO retains the one existing two-page UI, one .NET 10 runtime and one canonical Windows CI.
- **Authoritative current handoff:** [GCO-local REDscript Resolver decision](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/REDSCRIPT-RESOLVER-LOCAL-HANDOFF.md), [R0–R10 blueprint](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/GRSP_REDscript_Resolver_Blueprint.md), [GCO progress](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/dev/redscript/resolver_handoff/docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md).
- The existing GRSP branch `resolver/` directory, tests and managed workflow are **historical, already verified migration input** (GRSP CI #61; GCO real private source CI #482). **Do not extend that implementation or keep two active generation backends.** Do not delete/disable this proven code or its last working integration until GCO-local parity and safe removal are verified. Historical CI/documentation evidence remains legitimate, not a new GRSP implementation mandate.

## Mandatory read order for profiler changes

1. `README.md`, `OUTPUT_SCHEMA.md`, `docs/LATEST-WORK.md`, `docs/PROFILER_SUMMARY_INTEROP_CONTRACT.md`, `docs/TOTAL_INTEGRATION_CONTRACT.md`.
2. Native source and profiler manager/install/restore contracts, current dev branch HEAD, and current GCO capture interface.
3. For optimizer/evidence questions only, use the **GCO** handoff/blueprint/progress linked above. The former `docs/RESOLVER_HANDOFF.md` is a historical development record, not permission to build a GRSP-owned managed Resolver.

## Native instrumentation and safety

- GRSP raw evidence is measurement, not an installed mod inventory or an optimization authorization. Preserve stable IDs, F11 completeness, source attribution, frame boundaries and original captured evidence.
- For a measured generic opportunity, extend the **existing native GRSP writer** only when its missing evidence is specifically justified and bounded. Demonstrate overhead and capture/restore safety. Do not add LuaJIT allocation/GC hooks or invent performance improvements.
- Maintain the independent `.github/workflows/native-instrumentation.yml` source/native build path for profiler changes. Do not rebuild GRSP's native DLL in GCO or silently republish the stable standalone release. GRSP managed Resolver CI is **legacy verification only** pending the source move, not where new pass work belongs.
- Preserve standalone GRSP optional CapFrameX compatibility; GCO consumes real PresentMon and must not fake CapFrameX data or add CET/GRSP CPU as a total.

## Progress and verification

- Update `docs/LATEST-WORK.md` and relevant native handoffs after actual profiler changes; update GCO's REDscript handoff/progress for evidence-interface changes. Keep concrete branches, commits, CI status, untested gameplay, ownership and blockers.
- Protect original game files, GRSP native profiler, source-owner install/restore, stable releases and real captures. Green CI is not gameplay validation.
