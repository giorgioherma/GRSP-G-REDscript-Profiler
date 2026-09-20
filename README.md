# RSP Alpha 0.3.1 — Framework Mapping / Multithread Fix

Alpha 0.3 showed the right profiler architecture but exposed two problems in the first mapping pass:

1. REDscript activity was observed on multiple hook threads, while Alpha 0.3 only flushed the TLS belonging to the thread that received the Running-state callback.
2. The Running-state callback registered successfully but did not continue reliably with the old `red4ext-rs` callback ABI used by the pinned revision.

Alpha 0.3.1 fixes those before any Redscript optimization framework is designed.

## What this build is for

This is a **mapping profiler**, not yet an optimizer.

The objective is to answer:

- which mods are permanently active vs event/burst driven;
- which observed roots explode into large descendant call trees;
- which callsites behave like polling, repeated same-frame work, or inner loops;
- which targets are redundantly queried by many different mods;
- which runtime domains (blackboards, UI, mappins, equipment, combat, etc.) dominate traffic;
- where mod-to-mod nested call transitions occur;
- how deep wrapper chains become;
- how work is distributed across REDscript hook threads;
- which common primitives a future shared `G-RedRuntime` should actually provide.

The framework should be designed **from this evidence**, not guessed in advance.

---

## Capture control

The profiler starts **PAUSED**.

`F11` is still the hardcoded shared key:

- first press: fresh capture begins;
- second press: capture window closes immediately, existing profiled roots drain, all thread shards are merged, then CSVs are written;
- third press: another fresh capture.

The DLL polls F11 rather than registering an exclusive Windows hotkey, so CapFrameX may use the same F11 press.

Audible confirmation:

- START = one high beep;
- STOP = two low beeps.

There are no periodic CSV writes during the measurement window.

---

## Runtime-overhead policy

Accuracy remains the priority.

Alpha 0.3.1 still performs QPC timing for every observed profiled `InvokeStatic` / `InvokeVirtual` call. It does **not** sample calls and does not extrapolate counts.

The hot path avoids the Alpha 0.2 global aggregation lock. Each observed thread writes to its own leaked, thread-owned shard. Global aggregation occurs after STOP, when:

1. state becomes `STOPPING`;
2. no new profiled roots may enter;
3. already-running roots are allowed to finish;
4. `ACTIVE_ROOTS` reaches zero;
5. all registered shards are quiescent and are merged once.

This retains exact observed call counts/timings while keeping the per-call path local.

Calls that cross the requested STOP boundary are not added as completed call measurements if they finish after the stop QPC. Nested calls that completed before STOP remain valid.

---

## Frame callback fix

The project deliberately remains pinned to:

```toml
red4ext-rs rev = c44146c
```

because that is the revision already proven with our bind/opcode layouts.

That old Rust binding models game-state callbacks as void-returning, while the current RED4ext game-state ABI uses a bool-returning `OnUpdate`. Alpha 0.3.1 installs a compatibility callback that returns `false`, keeping `Running::OnUpdate` alive while preserving the known VM-hook revision.

After every capture check:

```text
RSP_Alpha_Capture.csv
```

Important fields:

```text
frame_callbacks
frame_quality
observed_threads
merged_shards
shard_merge_ok
stop_drain_ms
```

For a trustworthy mapping capture we want:

```text
shard_merge_ok = true
merged_shards == observed_threads
frame_quality = GOOD
```

`frame_callbacks` should be in the same general order as the rendered/game frames for the capture duration, not `1` as in the broken Alpha 0.3 baseline.

---

## New framework-design signals

### Root amplification

A **root** is the start of an observed mod-origin call chain when the profiler's instrumented stack is empty.

For every root Alpha 0.3.1 tracks:

```text
root calls
root calls/sec
root active frames
total root inclusive time
average root duration
max root duration
total observed descendant calls
average descendants per root
max descendants from one root
```

This distinguishes a root that cheaply checks state from a root that detonates thousands of downstream calls.

Output:

```text
RSP_Alpha_Roots.csv
```

### Cross-mod nested edges

When an observed nested call transitions from one source owner to another source owner, Alpha 0.3.1 records that edge.

Example concept:

```text
Mod A wrapper
    -> Mod B wrapper
        -> Mod C helper
```

Output:

```text
RSP_Alpha_CrossModEdges.csv
```

This is intended to reveal wrapper ecosystems and mod-to-mod chatter that may benefit from shared state or event bridges.

### Thread map

Output:

```text
RSP_Alpha_Threads.csv
```

Per observed thread it reports calls, owners, functions, root calls, cross-mod edge traffic, inclusive/exclusive instrumented totals, and first/last activity.

This tells us whether a future shared runtime service needs to be thread-safe or whether a domain is effectively confined to one execution thread.

### Target domains

Target names are grouped **heuristically at export time** into domains such as:

```text
BLACKBOARD
STATUS_EFFECT
EQUIPMENT_INVENTORY
QUEST_JOURNAL_FACTS
MAP_MAPPIN
UI_HUD
VEHICLE
COMBAT
NPC_AI
PLAYER_ENTITY
TIME_SCHEDULING
INPUT_ACTION
AUDIO
OTHER
```

Outputs:

```text
RSP_Alpha_TargetDomains.csv
RSP_Alpha_OwnerDomains.csv
```

These classifications are triage aids, not semantic truth. They add zero per-call runtime work because classification occurs only after STOP.

### Framework signals

`RSP_Alpha_FrameworkSignals.csv` combines owner-level evidence into candidate signals such as:

```text
HIGH_DUTY
AMPLIFICATION
SHARED_QUERY_CONSUMER
WRAPPER_HEAVY
CROSS_MOD_CHATTER
MULTITHREAD
```

and candidate primitives such as:

```text
EVENT_OR_DIRTY_GATE
CACHE_INDEX_ALGORITHM
SHARED_STATE_SERVICE
WRAPPER_CONSOLIDATION
EVENT_BRIDGE_OR_SHARED_STATE
THREAD_SAFE_CORE_SERVICE
```

These are deliberately labelled as signals. They do not automatically prove that a mod should be rewritten in that way; source inspection still follows profiling.

---

## Stable IDs for multi-scenario captures

Raw runtime function pointers are not suitable for joining separate game sessions.

Alpha 0.3.1 adds stable FNV-1a-derived IDs based on source/function/callsite identity:

```text
RSPF-...   stable function ID
RSPC-...   stable callsite ID
```

They appear in key output tables so later captures such as idle / world / combat / UI can be joined even if runtime pointers change.

An unresolved static target can still reduce stability because the target may fall back to a pointer representation; the resolution column remains authoritative.

---

## Outputs

After STOP:

```text
red4ext\plugins\redscript_profiler_alpha\RESULTS\

RSP_Alpha_Status.txt

RSP_Alpha_Capture.csv
RSP_Alpha_Markers.csv
RSP_Alpha_FunctionMap.csv

RSP_Alpha_CallSites.csv
RSP_Alpha_ByOwner.csv
RSP_Alpha_ByFunction.csv
RSP_Alpha_SharedTargets.csv
RSP_Alpha_Edges.csv
RSP_Alpha_Cadence.csv

RSP_Alpha_Frames.csv
RSP_Alpha_FrameOwners.csv

RSP_Alpha_Spikes.csv
RSP_Alpha_HotPaths.csv
RSP_Alpha_WrapperChains.csv
RSP_Alpha_WorkMap.csv

RSP_Alpha_Threads.csv
RSP_Alpha_Roots.csv
RSP_Alpha_CrossModEdges.csv
RSP_Alpha_TargetDomains.csv
RSP_Alpha_OwnerDomains.csv
RSP_Alpha_FrameworkSignals.csv
```

---

## Measurement interpretation

This profiler observes `InvokeStatic` / `InvokeVirtual` activity whose caller is mapped to `r6\scripts` mod source.

It is **not** a count of every REDscript VM instruction or intrinsic operation.

`exclusive_instrumented_ms` means:

```text
inclusive observed call time
- time spent in nested calls also observed by this profiler
```

It is more useful than raw inclusive totals but is **not yet guaranteed full VM function self-time**, because uninstrumented/base/native work can remain inside the interval.

Virtual targets still use method-name resolution rather than guaranteed concrete runtime implementation ownership. `target_resolution` must be respected.

---

## First Alpha 0.3.1 test

Use the same JIG run first. Do not change the gameplay scenario yet; this first run validates the profiler itself.

1. Build Alpha 0.3.1 in GitHub Actions.
2. Replace the existing profiler DLL.
3. Launch Cyberpunk and fully load the save.
4. Keep CapFrameX and RSP on F11.
5. Press F11; confirm one high beep.
6. Run the same JIG route.
7. Press F11; confirm two low beeps.
8. Wait for CSV export.
9. Send the complete `RESULTS` folder plus matching CapFrameX capture.

Before analyzing mods, verify:

```text
shard_merge_ok = true
merged_shards == observed_threads
frame_quality = GOOD
observed_calls = tens of millions / same order as Alpha 0.2 baseline
```

If those hold, the next stage is not another profiler redesign. We run deliberately different scenarios and use the stable IDs to build the workload map that will define `G-RedRuntime`.

---

## Build

GitHub Actions:

```text
.github\workflows\build.yml
```

or local Windows build:

```powershell
BUILD_WINDOWS.ps1
```

Final DLL:

```text
red4ext\plugins\redscript_profiler_alpha.dll
```

---

## Technical basis / credit

Function/source bind mapping and the `BindFunction` + `InvokeStatic` + `InvokeVirtual` hook strategy are based on the open-source `redscript-dap` work by jekky / jac3km4 (MIT).

`red4ext-rs` supplies the RED4ext Rust bindings.
