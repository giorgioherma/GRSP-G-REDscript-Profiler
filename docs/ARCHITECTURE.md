# GRSP approved architecture

GRSP owns standalone and GCO-consumed native REDscript profiling, not source generation. The historical Resolver branch name is retained solely for profiler development.

- Rust/RED4ext VM instrumentation and bounded capture-window call-edge, frame and source evidence feed the existing CSV/schema and standalone summary.
- Preserve independent F11 capture/stop/export, owner-managed installation, verification, collection and restoration. GCO orchestrates via the authoritative manager; no second GRSP DLL or install/restore implementation. Direct and MO2 GCO lifecycle changes belong in GCO and must preserve the owner contract.
- GRSP does not decide which mods are installed or optimizable. GCO main owns the managed .reds source analyzer/generator, generic-first order, SCC in CI, cross-profiler correlation and combined ZIP. G-REDruntime supplies optional services; CET owns its separate native and Lua pipeline.
- Retain provenance and distinguish direct observed proxy-child edges from resolved receivers or causal ownership. Never invent aggregate CPU or rewrite source from uncertain evidence.

Technical references: [standalone README](../README.md), [output schema](../OUTPUT_SCHEMA.md), [summary interop](PROFILER_SUMMARY_INTEROP_CONTRACT.md), [integration contract](TOTAL_INTEGRATION_CONTRACT.md), [build](../BUILDING.md), [framework author guide](../FRAMEWORK_AUTHOR_GUIDE.md). Older TOTAL/release labels describe the historical interface; current GCO routing is defined in AGENTS and CURRENT-TASK.
