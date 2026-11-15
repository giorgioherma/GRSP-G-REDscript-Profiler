# GRSP development agent contract — REDscript Resolver and Generator

**Authoritative branch:** `dev/resolver-profiler-handoff`; protect GRSP `main` (standalone v1.0.0). **Latest scope decision:** 2026-11-15, GRSP owns the REDscript Resolver, Generator, instrumentation and full-stack compiler validation. The older GCO-owned backend decision is superseded only for this development scope.

## Mandatory read order

1. `README.md`, `OUTPUT_SCHEMA.md`, `docs/RESOLVER_HANDOFF.md`, `docs/PROFILER_SUMMARY_INTEROP_CONTRACT.md` and `docs/TOTAL_INTEGRATION_CONTRACT.md` (historical standalone/TOTAL contract where applicable).
2. [Canonical GCO REDscript blueprint](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP_REDscript_Resolver_Blueprint.md) and [current GCO REDscript progress](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md).
3. `docs/RESOLVER_PROGRESS.md`, `docs/LATEST-WORK.md`, original native writers/manager source and current GRSP branch HEAD. Check current GCO CET/PresentMon completion and any source-owner changes before integration.

## Source ownership and implementation

- GRSP source owns REDscript profiling, evidence identity, Resolver JSON, safe source transformation, pass generation and actual full-stack REDscript compilation. Keep standalone GRSP capture and optional CapFrameX operation intact.
- GCO invokes GRSP and CET through their native headless contracts, correlates genuine PresentMon 2.6 chronology, reports and merges one clean ZIP. No duplicate owner optimizers, no second UI, no new capture/restore gates.
- Keep generic passes first and based on **fresh** measured work and current installed `r6/scripts`. The owner's hand-optimized 61-file partial reference is the *only* currently approved semantic portfolio, applied through structural predicates, not copies or version locks. Do not commit/distribute proprietary mod ZIPs or private captures.
- G-REDruntime `main` owns optional shared runtime APIs. Expand only for a proven reusable optimization and explicit dependency compatibility; do not rewrite unrelated framework code.
- Structural parsing/proof, pristine/partial/fully optimized states, idempotence, safe source-drift refusal, exact mod ownership, lifecycle preservation, compiler success, missing-dependency handling and no writes to installed sources are mandatory.
- Never invent performance improvements or combine overlapping CET and REDscript timings. Missing PresentMon is `NOT_CAPTURED`; do not imitate a CapFrameX schema.

## Progress, decisions and CI discipline (mandatory)

- **Immediately after every meaningful implementation/decision:** update `docs/RESOLVER_PROGRESS.md` and `docs/LATEST-WORK.md`; also update `docs/RESOLVER_HANDOFF.md` for contracts and GCO's progress/decision docs when interfaces or ownership change. Keep checkpoints per R0–R10, exact commit/CI links, checks run, passed/failed/skipped, known blockers and next action.
- Preserve source-owner installation/restoration and public release behavior. Reuse existing CI; add only tests that protect real output/safety/integration, and report compiler/gameplay checks separately from CI. Do not claim completion or create a release because documentation or green CI exists.
