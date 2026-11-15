# GRSP latest verified work

## 2026-11-15 — REDscript Resolver/Generator ownership and branch handoff

- Repository branch `dev/resolver-profiler-handoff`, prior HEAD `ece5b8e2c65690bb914cc1f90b3c1bd79d740ef8`. Stable `main` (GRSP v1.0.0) untouched.
- New explicit owner decision: GRSP owns native evidence, REDscript Resolver JSON, generic-first source-aware Generator and full effective REDscript compilation validation. GCO owns the genuine PresentMon-backed consolidated report, invokes both owner optimizers and merges one game-relative ZIP. G-REDruntime owns optional reusable services.
- Canonical implementation plan: https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/GRSP_REDscript_Resolver_Blueprint.md. Live stage ledger: [GRSP progress](RESOLVER_PROGRESS.md); GCO [integration progress](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/feature/redscript-resolver-integration/docs/integration/REDSCRIPT-RESOLVER-PROGRESS.md). Existing hand-optimized 61-file partial ZIP is the finite approved semantic reference, not a distributable source fixture.
- **Only documentation organized.** No GRSP implementation, profiler instrumentation changes, new captures, compiler tests, game testing or CI runs; previous GCO CET full source-generation CI #416 and GCO real PresentMon report CI #426 remain separate verified baselines.
- **Next:** verify source-state inventory and collect fresh complete CET/GRSP/PresentMon evidence before choosing first generic transformations. Log every meaningful change and decision here and in `RESOLVER_PROGRESS.md` immediately, with exact results.
