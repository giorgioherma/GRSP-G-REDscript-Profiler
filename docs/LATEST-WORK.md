# Latest verified checkpoint

- GRSP profiler branch pre-documentation HEAD `a844c72d093c1653e5974acd1eb6f78bb9281f02`.
- [Native instrumentation CI #14 SUCCESS](https://github.com/giorgioherma/GRSP-G-REDscript-Profiler/actions/runs/43815654121), tested 01d40b7e30b3a73d67e88e482c0cebd675d2ac56. This resolves the unverified CI status recorded in the historical handoff, not the remaining gameplay proof.
- Existing proxy-child evidence records actual directly nested InvokeStatic/InvokeVirtual callsite pairs during accepted F11 capture, capped at 4,096 distinct keys/thread with overflow accounting. It does not establish actual receiver or downstream causality. Real F11 CSV population and runtime overhead validation remain unverified in this documentation audit.
- Managed Resolver and its CI migrated to GCO; historical GRSP handoffs remain in GCO reference history. Do not reintroduce a GRSP-managed optimizer.
- No new GRSP assignment from current CET Project A. See [CURRENT-TASK](../CURRENT-TASK.md). This documentation reset leaves all code, tests, native pins and releases untouched. [Original rules and full evidence ledger](history/2026-11-26-documentation-reset/) retained exactly.
