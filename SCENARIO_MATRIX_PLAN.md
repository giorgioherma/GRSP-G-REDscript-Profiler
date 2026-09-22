# GRSP Scenario Matrix

A single capture can identify heavy owners; a scenario matrix distinguishes permanent background cost from feature-driven work.

Recommended captures with the same mod stack:

- `IDLE` — quiet loaded world, minimal input.
- `WORLD` — normal city traversal.
- `DRIVING` — sustained vehicle traversal.
- `COMBAT` — several active NPCs.
- `WANTED` — high streaming/AI pressure and rapid movement.
- `UI` — representative map/inventory/vendor/mod UI use.

Use the same F11 window for GRSP and CapFrameX. If also running the CET profiler, use the same capture window there as well. The GRSP 50 ms timeline and Unix timestamps are specifically intended for three-way correlation.

For optimization projects, compare owners across scenarios before changing code. A high IDLE duty cycle is a stronger dormancy/dirty-gate candidate than work that appears only when its feature is actively used.
