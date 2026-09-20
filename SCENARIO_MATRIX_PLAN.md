RSP ALPHA 0.4 — SCENARIO MATRIX COLLECTION PLAN
================================================

Goal: discover what a future shared Redscript optimization framework should
actually provide by separating permanent background work from world, combat,
and UI-specific work.

GENERAL RULES
-------------
- Use the same installed mod stack for all four captures.
- Let the save finish loading before profiling.
- Use F11 for both RSP and CapFrameX.
- Keep the matching CapFrameX JSON beside the matching Capture_* folder.
- Aim for ~120 seconds per scenario. Consistency matters more than exact length.
- Do not intentionally trigger unrelated menus/events during a scenario.

SCENARIO 1 — IDLE
-----------------
Set RSP_Scenario.txt to:

  IDLE

Stand in a quiet loaded world location. No menu, no driving, no combat, minimal
camera/input. This isolates permanent polling, wrapper traffic, recurring timers,
background state queries, and mods that remain active without gameplay demand.

SCENARIO 2 — WORLD
------------------
Set:

  WORLD

Use the repeatable JIG/world route. Normal movement/driving and world streaming,
without deliberately entering combat or spending time in menus. This captures
general runtime/world activity and gives continuity with the existing baseline.

SCENARIO 3 — COMBAT
-------------------
Set:

  COMBAT

Spend most of the window in active combat. Prefer a repeatable location with
several NPCs. Include normal damage/status/weapon/AI activity. Avoid map/vendor
menus. This isolates combat/NPC/status-effect/event workloads.

SCENARIO 4 — UI
---------------
Set:

  UI

Spend the window actively using representative UI: map/minimap interactions,
inventory/equipment, journal, vendor/shop or other mod-heavy interfaces that are
part of normal play. Avoid combat. This isolates UI, map, inventory, journal,
widget and menu-specific workloads.

WHAT WE WILL COMPARE
--------------------
Across scenarios, stable RSPF/RSPC IDs let us identify:

- owners active in all four scenarios -> global/background framework candidates
- targets shared by many owners in all scenarios -> shared-state/cache candidates
- work present only in combat/UI/world -> domain-specific adapters/services
- frame-bound work in IDLE -> strongest polling/dirty-gate candidates
- high root descendant amplification -> cache/index/algorithm candidates
- wrapper/cross-mod traffic common everywhere -> wrapper consolidation/event bus candidates
- targets that move sharply between scenarios -> feature-driven rather than permanent work
- semantic calls vs language-intrinsic noise -> actual framework-relevant traffic

DO NOT OPTIMIZE YET
-------------------
The matrix pass is discovery. Do not patch individual mods between these four
captures. We want one unchanged stack so differences are caused by scenario, not
code changes.
