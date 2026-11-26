# GRSP engineering rules

## Authority and startup
Read this file, [CURRENT-TASK](CURRENT-TASK.md), [architecture](docs/ARCHITECTURE.md) and [latest checkpoint](docs/LATEST-WORK.md). The [GCO cross-repository assignment](https://github.com/giorgioherma/Gs-Cyberpunk2077_Optimizations/blob/main/CURRENT-TASK.md) controls scope together with the user's latest explicit instructions. Consult relevant technical contracts, not every historical handoff. LATEST-WORK is evidence, not a new assignment.

## Durable rules
- Work on dev/resolver-profiler-handoff. This historical branch name does not authorize a managed Resolver here. Protect stable main, releases and version metadata; no new branches or release changes without explicit scope.
- GRSP owns native REDscript instrumentation (Rust/RED4ext), F11 capture, raw measurement/schema, source and callsite attribution, standalone manager and install/restore. GCO main owns all managed REDscript analysis, Resolver/generator, source-state admission, SCC and combined ZIP; route any such work there. CET owns CET instrumentation/Lua Resolver; G-REDruntime owns optional shared services. No competing managed Resolver, workflow or generator in GRSP.
- Preserve capture completeness, stable identities, frame boundaries, output compatibility, original events and lifecycle/restore. Do not silently change owner APIs, duplicate profiler state or overwrite mods. Preserve raw evidence; a capture is not an installed mod inventory.
- Add native evidence only for a concrete measured need, through the existing writer/hooks and bounded capture-time mechanisms. Distinguish observed edges, unresolved virtual receivers, sampled timing and actual ownership. Never attribute proxy child cost to a mod without proof or sum overlapping CET/GRSP CPU. Missing evidence is NOT_MEASURED, not zero.
- Use the existing owner-native instrumentation CI; verify exact source, native DLL/artifact and hash, actual F11 output, standalone and GCO compatibility. Keep unchanged native payloads pinned; no GCO-native rebuild or release replacement as a side effect. Do not run docs-only CI. Preserve all real capture/restore and output contracts.
- Update the current checkpoint with actual commits, tests, blockers and pending gameplay evidence; communicate schema/interface changes to GCO. Green CI is not gameplay acceptance. Continue only the assigned scope.
