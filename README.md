# RSP Alpha 0.3 — Runtime Mapping Profiler

Alpha 0.3 changes the goal from “show me large Redscript activity” to **“map the architecture of Redscript runtime work.”**

It keeps the proven Alpha 0.2 bind/source mapping and `InvokeStatic` / `InvokeVirtual` hooks, but adds controlled frame-aware nested profiling, cadence analysis, owner/function/target maps, sparse hot paths, and a first automatic work classification.

## Capture control

The profiler still starts **PAUSED**.

Hard-coded key: **F11**.

```text
F11 #1
  -> clear old runtime measurement
  -> START capture
  -> one HIGH beep

F11 #2
  -> STOP capture immediately
  -> two LOW beeps
  -> final partial-frame flush
  -> write CSVs

F11 #3
  -> fresh capture
```

The key is polled rather than registered exclusively, so CapFrameX can use **the same F11 press**.

No CSV disk writes happen during the capture.

## Important Alpha 0.3 timing change

Alpha 0.2 used a control-thread capture clock. Alpha 0.3 uses Windows **QueryPerformanceCounter (QPC)** for per-call timing and capture-relative timestamps.

Alpha 0.3 also registers a RED4ext **Running game-state `OnUpdate` listener**, which is used as the profiler's actual game-frame boundary. Every recorded call is associated with the current game frame.

This gives us a much stronger CapFrameX correlation surface:

```text
frame_id
capture_ms
calls in frame
owners in frame
call depth
exclusive instrumented time
largest call
spike count
```

## Profiler-overhead work

Alpha 0.2 updated a shared locked `HashMap` on every observed invocation. With hundreds of thousands of observed calls per second, that is expensive.

Alpha 0.3 removes that design.

### Alpha 0.3 hot path

```text
Invoke hook
  -> one capture-state atomic check
  -> thread-local caller eligibility cache
  -> QPC start
  -> push small thread-local stack entry
  -> original opcode handler
  -> QPC end
  -> pop stack
  -> update THREAD-LOCAL per-frame HashMap
```

Global aggregate maps are merged **once per game frame**, not once per call.

Global locks remain only for sparse/rare operations such as:

- first-seen function/source metadata
- first-seen virtual target name per thread
- >=1 ms sparse spike/hot-path capture
- once-per-frame aggregate merge

This is intended to lower profiler overhead **without sampling away calls or dropping per-call duration measurement**.

We deliberately did **not** switch to sampling because that would weaken the exact call-count/cadence data we want for optimization planning.

## Nested timing: what “exclusive” means

Alpha 0.3 maintains a thread-local nested stack around the already-proven Invoke hooks.

For an observed call edge:

```text
inclusive = edge exit - edge entry
exclusive_instrumented = inclusive - time spent in nested instrumented child edges
```

Example:

```text
A -> B        20 ms inclusive
    B -> C    17 ms inclusive

A -> B exclusive_instrumented ~= 3 ms
```

This removes a large amount of nested double-counting.

However, **this is not yet guaranteed full VM function self-time**. Calls made from uninstrumented/base-script paths and native/intrinsic work can remain inside the exclusive edge. The CSV deliberately calls the field `exclusive_instrumented_ms` rather than claiming exact function self-time.

We are still avoiding `CScript_RunPureScript` in this alpha because CET also hooks that execution path. The current hooks have already survived very large captures and are our safer foundation.

## New runtime maps

Alpha 0.3 exports:

```text
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
RSP_Alpha_WrapperChains.csv
RSP_Alpha_Frames.csv
RSP_Alpha_FrameOwners.csv
RSP_Alpha_Spikes.csv
RSP_Alpha_HotPaths.csv
RSP_Alpha_WorkMap.csv
```

### RSP_Alpha_Capture.csv

Capture identity and integrity data:

- capture start / stop Unix time
- QPC start / stop / frequency
- duration
- number of mapped functions
- observed Redscript hook thread count
- frame count
- call count
- row counts
- dropped sparse event counts
- final partial-frame flush status

### RSP_Alpha_Markers.csv

Explicit START / STOP markers for synchronization.

```text
event,qpc,capture_ms,unix_ms
START,...,0.000,...
STOP,...,131940.123,...
```

### RSP_Alpha_FunctionMap.csv

Runtime function pointer -> owner/source/function mapping.

### RSP_Alpha_CallSites.csv

Detailed callsite aggregate:

- owner/source/function/line
- static vs virtual
- target
- direct vs unresolved target resolution
- calls / calls per second
- observed inclusive time
- exclusive instrumented time
- averages / maxima
- maximum timestamp
- active frames
- active-frame percentage
- repeated-frame count
- max calls in one frame
- calls per active frame
- dominant cadence
- >=1 / >=5 / >=16.67 ms counts

### RSP_Alpha_ByOwner.csv

Per source-owner runtime shape:

- calls/sec
- active-frame percentage
- calls per active frame
- max calls per frame
- observed inclusive time
- exclusive instrumented time
- spike counts

This is the first “which mods own the runtime pressure?” table.

### RSP_Alpha_ByFunction.csv

Per source function **outgoing call activity**.

This does not pretend to be perfect function self-time. It tells us which source functions are generating the observed call-edge traffic and associated measured subtree/exclusive edge time.

### RSP_Alpha_SharedTargets.csv

Groups all callsites by target and reports:

- total calls
- calls/sec
- number of different calling owners
- number of different source functions
- observed time
- max call

This is specifically intended to detect future G-RedRuntime consolidation opportunities such as many mods repeatedly querying the same game state/API.

### RSP_Alpha_Edges.csv

Caller -> callee graph.

Static targets are mapped back to bound function metadata when possible.

Virtual dispatch currently remains `UNRESOLVED_VIRTUAL` at the concrete implementation level; the virtual method name is still retained.

### RSP_Alpha_Cadence.csv

Per callsite runtime rhythm.

It tracks active-frame gaps in buckets:

```text
<0.1 ms
0.1-1 ms
1-5 ms
5-12 ms
12-25 ms
25-75 ms
75-200 ms
200-750 ms
750-1500 ms
>1500 ms
```

It also reports:

- active-frame percentage
- repeated frames
- max calls/frame
- calls/active-frame
- first / last seen
- dominant cadence

This helps distinguish:

```text
frame-bound polling
periodic polling
inner-loop explosions
event-driven bursts
rare expensive work
```

### RSP_Alpha_Frames.csv

One row per actual profiler game-frame boundary:

- frame duration according to RED4ext Running-state boundaries
- total observed calls
- unique callsites
- unique owners
- unique source functions
- max nested observed call depth
- observed inclusive sum
- exclusive instrumented sum
- largest single observed call
- spike count

The observed inclusive sum can double-count nested work. `exclusive_instrumented_ms` is the more useful non-overlapping metric inside the instrumented subtree.

### RSP_Alpha_FrameOwners.csv

Sparse per-frame owner attribution.

To avoid gigantic output, an owner/frame row is emitted only when that owner had at least one of:

```text
>=100 calls in the frame
>=0.25 ms exclusive instrumented time
>=1 ms maximum observed call
```

This is the main file for correlating a bad CapFrameX frame with the Redscript owners active during that frame.

### RSP_Alpha_Spikes.csv

Every retained >=1 ms observed call edge with:

- exact QPC-based capture timestamp
- game frame ID
- owner/source/function
- target
- inclusive duration
- exclusive instrumented duration
- nested depth

### RSP_Alpha_HotPaths.csv

Sparse nested path snapshot for >=1 ms observed calls.

This gives paths such as:

```text
OwnerA::FunctionA@123 -> FunctionB
|
OwnerB::FunctionB@456 -> FunctionC
|
OwnerC::FunctionC@789 -> Target
```

The profiler keeps only sparse slow-path events; it does **not** dump every call stack.

### RSP_Alpha_WrapperChains.csv

Aggregates hot paths containing at least two `wrapper$` entries.

This is aimed at finding stacked mod wrappers that repeatedly traverse the same state/query chain.

### RSP_Alpha_WorkMap.csv

First-pass automatic heuristic classification per callsite.

Possible labels:

```text
INNER_LOOP_EXPLOSION
FRAME_BOUND_POLLING
FRAME_BOUND_REPEAT_WORK
BURST_WORKER
PERIODIC_POLLING
HIGH_COST_CALL
WRAPPER_CHAIN_CANDIDATE
MIXED
```

And a corresponding likely treatment such as:

```text
index/cache/algorithm
event/cache/dirty-flag
cache/consolidate/conditional-schedule
event/lower-cadence/shared-state
optimize-function/downstream
```

These are **triage hints**, not final optimization decisions. We inspect the source before changing a mod.

## First Alpha 0.3 test

Use the same JIG scenario as Alpha 0.2.

```text
1. Build Alpha 0.3.
2. Replace the Alpha 0.2 DLL.
3. Launch Cyberpunk.
4. Fully load the JIG save.
5. CapFrameX hotkey = F11.
6. Press F11.
   - CapFrameX starts.
   - RSP starts.
   - hear one HIGH beep.
7. Run the same JIG route.
8. Press F11 again.
   - capture closes immediately.
   - hear two LOW beeps.
   - RSP writes files after measurement has stopped.
9. Send the entire RESULTS folder plus the matching CapFrameX capture.
```

For the first run, verify `RSP_Alpha_Capture.csv` reports:

```text
final_flush_ok = true
observed_threads = 1   (expected, but we are measuring this rather than assuming it)
```

If `observed_threads > 1`, that is important evidence and we will revise the TLS/frame-flush model before trusting aggregate completeness.

## What we are NOT doing yet

Alpha 0.3 intentionally does not:

- rewrite any third-party Redscript mod
- create G-RedRuntime yet
- hook `CScript_RunPureScript`
- claim perfect VM function self-time
- record every call event to disk/memory
- sample calls and extrapolate totals

The purpose of this pass is to build the map that tells us what G-RedRuntime should actually contain.

## Technical basis / credit

Function/source binding and the proven BindFunction + InvokeStatic + InvokeVirtual interception approach are based on `redscript-dap` by jekky / jac3km4 (MIT).

`red4ext-rs` is pinned to revision:

```text
c44146c
```

Alpha 0.3 also uses the RED4ext Running game-state update listener as a frame boundary.
