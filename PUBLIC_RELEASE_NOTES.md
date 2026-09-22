# GRSP 0.5.0 Public Preview

This release converts the internal Alpha 0.4 scenario-matrix profiler into a public-user reporting layer without changing the proven measured hot path.

## Added

- `GRSP_Report.html` with top-owner bar chart and burst/spike tables.
- `GRSP_ByMod.csv` ranked by sustained observed REDscript cost.
- sustained / burst / mixed workload classification.
- wrapper-attribution caution flag.
- timestamped 50 ms `GRSP_Timeline.csv` for CET profiler correlation.
- Unix timestamps in public frame and spike exports.
- `GRSP_Summary.csv` with P95/P99/max observed script work per game frame.
- compact `Developer` folder for framework authors.

## Removed from default public output

The large Alpha research matrix of roots, cross-mod edges, domains, threads, raw hot paths and other specialized tables is no longer dumped as separate public files. The core measurement model remains available internally and the most useful framework-facing views are retained.

## Not changed

- F11 START/STOP behavior.
- exact observed `InvokeStatic` / `InvokeVirtual` timing.
- QPC timing.
- thread-owned capture shards.
- nested inclusive/exclusive-instrumented accounting.
- real game-frame boundaries.
- stable function/callsite IDs.
- 1 ms bounded spike capture.
