//! Read-only post-STOP callback pressure evidence built from GRSP's original
//! InvokeStatic/InvokeVirtual callsite counters. This is NOT callback-entry
//! instrumentation or unchanged-argument measurement. No hot-path overhead.
use super::*;

fn family(name: &str) -> Option<&'static str> {
    let method = name.rsplit("::").next().unwrap_or(name).to_ascii_lowercase();
    if method == "onaction" || method.contains("input")
        || method == "onkey" || method == "onbutton"
    {
        Some("INPUT_ACTION")
    } else if method.contains("update") || method.contains("refresh")
        || method == "tick" || method == "ontick"
    {
        Some("UPDATE_REFRESH")
    } else if matches!(method.as_str(),
        "ongameattached" | "ongamedetached" | "onattach" | "ondetach"
        | "oninitialize" | "onuninitialize")
    {
        Some("LIFECYCLE")
    } else if method.contains("delay") || method.contains("timer") {
        Some("TIMER_CALLBACK")
    } else {
        None
    }
}

fn frame_pressure(counts: &HashMap<u64, u64>) -> (u64, u64, u64, u64) {
    if counts.is_empty() { return (0, 0, 0, 0); }
    let mut values: Vec<_> = counts.values().copied().collect();
    values.sort_unstable();
    let p95 = values[(values.len() - 1) * 95 / 100];
    let repeated = values.iter().filter(|&&n| n > 1).count() as u64;
    (values.len() as u64, p95, *values.last().unwrap_or(&0), repeated)
}

#[derive(Default)]
struct CallbackPressure {
    edges: u64,
    exclusive_ticks: u64,
    root_edges: u64,
    unresolved_edges: u64,
    wrapper_edges: u64,
    targets: HashMap<String, u64>,
    frames: HashMap<u64, u64>,
    callsites: u64,
}

pub(super) fn export(developer_dir: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let callsites = CALLSITES.read();
    let roots = ROOTS.read();
    let duration_s = capture_duration_s();
    let total_frames = FRAMES.read().len() as u64;
    let capture = CAPTURE_ID.load(Ordering::Relaxed);
    let mut by_function: HashMap<usize, CallbackPressure> = HashMap::new();

    // No new native hook, per-call profiler allocation, source parsing or
    // arbitrary state read. Existing merged counters are authoritative.
    for (key, stat) in callsites.iter() {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        if !meta.is_mod_source || family(&meta.function).is_none() { continue; }
        let target = target_info(*key, &funcs, &targets);
        let p = by_function.entry(key.caller).or_default();
        p.edges = p.edges.saturating_add(stat.calls);
        p.exclusive_ticks = p.exclusive_ticks.saturating_add(stat.exclusive_ticks);
        p.callsites += 1;
        *p.targets.entry(target.display.clone()).or_default() += stat.calls;
        if target.resolution.starts_with("UNRESOLVED") {
            p.unresolved_edges = p.unresolved_edges.saturating_add(stat.calls);
        }
        if target.display.contains("proxy$") || target.display.contains("wrapper$") {
            p.wrapper_edges = p.wrapper_edges.saturating_add(stat.calls);
        }
        for &(frame_id, count) in &stat.frame_calls {
            let e = p.frames.entry(frame_id).or_default();
            *e = e.saturating_add(count);
        }
    }
    for (key, stat) in roots.iter() {
        if let Some(p) = by_function.get_mut(&key.caller) {
            p.root_edges = p.root_edges.saturating_add(stat.calls);
        }
    }

    let mut callbacks: Vec<_> = funcs.iter()
        .filter(|(_, meta)| meta.is_mod_source && family(&meta.function).is_some())
        .collect();
    callbacks.sort_by(|a, b| {
        let ca = by_function.get(a.0).map_or(0, |x| x.edges);
        let cb = by_function.get(b.0).map_or(0, |x| x.edges);
        cb.cmp(&ca).then(a.1.owner.cmp(&b.1.owner))
            .then(a.1.function.cmp(&b.1.function))
            .then(a.1.source_path.cmp(&b.1.source_path))
    });

    let mut summary = BufWriter::new(File::create(
        developer_dir.join("RSP_CallbackPressure.csv"))?);
    writeln!(summary, "capture_id,stable_function_id,owner,source_function,source_path,source_line,callback_family,outgoing_call_edges,outgoing_edges_per_sec,exclusive_instrumented_ms,active_frames,active_frame_pct,p95_outgoing_edges_per_active_frame,max_outgoing_edges_per_frame,repeated_frames,unique_callsites,unique_targets,root_outgoing_edges,unresolved_target_edges,wrapper_target_edges,top_target,top_target_edges,entry_count_status")?;
    for (ptr, meta) in callbacks {
        let p = by_function.get(ptr);
        let edges = p.map_or(0, |p| p.edges);
        let (active, p95, max, repeated) = p.map_or(
            (0, 0, 0, 0), |p| frame_pressure(&p.frames));
        let top = p.and_then(|p| p.targets.iter().max_by(|a,b|
            a.1.cmp(b.1).then_with(|| b.0.cmp(a.0))));
        let (top_name, top_edges) = top.map_or(
            ("", 0), |(name, edges)| (name.as_str(), *edges));
        writeln!(summary,
            "{},RSPF-{:016X},{},{},{},{},{},{},{:.3},{:.6},{},{:.3},{},{},{},{},{},{},{},{},{},{},{}",
            capture,
            stable_function_hash(meta),
            csv(&meta.owner), csv(&meta.function), csv(&meta.source_path),
            meta.source_line, family(&meta.function).unwrap_or("UNKNOWN"),
            edges, rate(edges, duration_s),
            p.map_or(0.0, |p| ticks_to_ms(p.exclusive_ticks)),
            active, pct(active, total_frames), p95, max, repeated,
            p.map_or(0, |p| p.callsites),
            p.map_or(0, |p| p.targets.len()),
            p.map_or(0, |p| p.root_edges),
            p.map_or(0, |p| p.unresolved_edges),
            p.map_or(0, |p| p.wrapper_edges),
            csv(top_name), top_edges,
            if edges > 0 { "OUTGOING_EDGES_OBSERVED_CALLBACK_ENTRIES_UNKNOWN" }
            else { "NO_OUTGOING_EDGES_OBSERVED_CALLBACK_ENTRIES_UNKNOWN" })?;
    }
    summary.flush()?;

    let mut callsite_rows: Vec<_> = callsites.iter().filter(|(key, _)|
        funcs.get(&key.caller).is_some_and(|meta|
            meta.is_mod_source && family(&meta.function).is_some())).collect();
    callsite_rows.sort_by(|a, b|
        b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks)
            .then(b.1.calls.cmp(&a.1.calls)));
    let mut detail = BufWriter::new(File::create(
        developer_dir.join("RSP_CallbackTargets.csv"))?);
    writeln!(detail, "capture_id,stable_callsite_id,stable_function_id,owner,source_function,source_path,source_line,callback_family,target,target_resolution,call_kind,outgoing_edges,outgoing_edges_per_sec,exclusive_instrumented_ms,active_frame_pct,max_outgoing_edges_per_frame,attribution_note")?;
    for (key, stat) in callsite_rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(detail,
            "{},RSPC-{:016X},RSPF-{:016X},{},{},{},{},{},{},{},{},{},{:.3},{:.6},{:.3},{},{}",
            capture, stable_callsite_hash(meta, *key, &target),
            stable_function_hash(meta), csv(&meta.owner),
            csv(&meta.function), csv(&meta.source_path),
            key.line, family(&meta.function).unwrap_or("UNKNOWN"),
            csv(&target.display), target.resolution, call_kind(key.kind),
            stat.calls, rate(stat.calls, duration_s),
            ticks_to_ms(stat.exclusive_ticks), pct(stat.active_frames, total_frames),
            stat.max_calls_per_frame,
            if target.resolution.starts_with("UNRESOLVED") {
                "UNRESOLVED_TARGET_NO_CAUSAL_ATTRIBUTION"
            } else if target.display.contains("proxy$") || target.display.contains("wrapper$") {
                "WRAPPER_TARGET_PRESERVE_SEMANTICS"
            } else {
                "OBSERVED_CALL_EDGE_NOT_CALLBACK_ENTRY"
            })?;
    }
    detail.flush()?;
    export_shared_reads(developer_dir, &funcs, &targets, &callsites)
}

#[cfg(test)]
mod tests {
    use super::{family, frame_pressure};
    use std::collections::HashMap;

    #[test]
    fn classify_source_method_not_mod() {
        assert_eq!(family("PlayerPuppet::OnAction"), Some("INPUT_ACTION"));
        assert_eq!(family("CustomInputListener::OnInput"), Some("INPUT_ACTION"));
        assert_eq!(family("buffListGameController::UpdateBuffDebuffList"), Some("UPDATE_REFRESH"));
        assert_eq!(family("Foo::OnUpdate"), Some("UPDATE_REFRESH"));
        assert_eq!(family("Foo::OnGameAttached"), Some("LIFECYCLE"));
        assert_eq!(family("Foo::DelayCallback"), Some("TIMER_CALLBACK"));
        assert_eq!(family("Foo::CalculateDamage"), None);
        assert_eq!(family("OnAction"), Some("INPUT_ACTION"));
    }

    #[test]
    fn burst_distribution_never_claims_callback_entries() {
        assert_eq!(frame_pressure(&HashMap::new()), (0, 0, 0, 0));
        let values = HashMap::from([(1, 2), (2, 1), (3, 30), (4, 9), (5, 4)]);
        assert_eq!(frame_pressure(&values), (5, 9, 30, 4));
    }
}

// CET-style repeated-read discovery across all callback-like source functions.
// The existing per-callsite, per-frame counts provide this after F11 STOP,
// without another VM hook, extra hot-path allocation or generated REDscript.
#[derive(Clone, Copy, Eq, PartialEq, Hash)]
struct ReadTarget { kind: u8, target: u64 }

#[derive(Default)]
struct ReadFrame {
    calls: u64,
    first_caller: usize,
    first_owner: u64,
    multiple_callers: bool,
    multiple_owners: bool,
}
impl ReadFrame {
    fn add(&mut self, caller: usize, owner: u64, calls: u64) {
        if self.calls == 0 {
            self.first_caller = caller;
            self.first_owner = owner;
        } else {
            self.multiple_callers |= caller != self.first_caller;
            self.multiple_owners |= owner != self.first_owner;
        }
        self.calls = self.calls.saturating_add(calls);
    }
}

#[derive(Default)]
struct ReadPressure {
    name: String,
    resolution: &'static str,
    calls: u64,
    ticks: u64,
    callsites: u64,
    callers: HashSet<usize>,
    owners: HashSet<u64>,
}

fn read_like_target(name: &str) -> bool {
    let last = name.rsplit("::").next().unwrap_or(name);
    let method = last.split(';').next().unwrap_or(last);
    ["Get", "Is", "Has", "Find", "Query"].iter().any(|prefix|
        method.strip_prefix(prefix).is_some_and(|suffix|
            suffix.chars().next().is_some_and(|c| c.is_ascii_uppercase())))
}

fn read_class(name: &str) -> &'static str {
    if name.contains("ScriptListenerAction") ||
        name.contains("inputScriptListenerAction") { "EVENT_ARGUMENT_READ" }
    else if name.contains("GameInstance::Get") {
        "SHARED_SYSTEM_OR_CONTEXT_READ"
    } else if name.contains("PlayerPuppet::") {
        "PLAYER_LIFETIME_SENSITIVE_READ"
    } else { "READ_LIKE_PURITY_UNKNOWN" }
}

// Per-target bounded stream; omitted rows are reported, not treated as zero.
fn accumulate_read_frame(
    frames: &mut HashMap<u64, ReadFrame>, dropped: &mut u64,
    frame: u64, caller: usize, owner: u64, calls: u64, cap: usize
) {
    if let Some(row) = frames.get_mut(&frame) {
        row.add(caller, owner, calls);
    } else if frames.len() < cap {
        let mut row = ReadFrame::default();
        row.add(caller, owner, calls);
        frames.insert(frame, row);
    } else {
        *dropped = dropped.saturating_add(1);
    }
}

fn export_shared_reads(
    output: &Path,
    funcs: &HashMap<usize, FunctionMeta>,
    names: &HashMap<(u8, u64), String>,
    callsites: &HashMap<CallsiteKey, AggStat>,
) -> std::io::Result<()> {
    const MAX_GROUPS: usize = 128;
    const MAX_FRAME_KEYS_PER_TARGET: usize = 65_536;
    let mut groups: HashMap<ReadTarget, ReadPressure> = HashMap::new();
    for (key, stat) in callsites {
        let Some(meta) = funcs.get(&key.caller) else { continue };
        if !meta.is_mod_source || family(&meta.function).is_none() { continue }
        let target = target_info(*key, funcs, names);
        if target.resolution.starts_with("UNRESOLVED") ||
            !read_like_target(&target.display) { continue }
        let row = groups.entry(ReadTarget { kind: key.kind, target: key.target })
            .or_default();
        if row.name.is_empty() {
            row.name = target.display;
            row.resolution = target.resolution;
        }
        row.calls = row.calls.saturating_add(stat.calls);
        row.ticks = row.ticks.saturating_add(stat.exclusive_ticks);
        row.callsites = row.callsites.saturating_add(1);
        row.callers.insert(key.caller);
        row.owners.insert(meta.owner_hash);
    }
    let mut selected: Vec<_> = groups.iter().filter(|(_, r)| r.calls >= 300)
        .map(|(key, r)| (*key, r.calls, r.ticks)).collect();
    selected.sort_by(|a, b| b.1.cmp(&a.1).then(b.2.cmp(&a.2))
        .then(a.0.kind.cmp(&b.0.kind)).then(a.0.target.cmp(&b.0.target)));
    selected.truncate(MAX_GROUPS);
    let admitted: HashSet<ReadTarget> = selected.iter().map(|row| row.0).collect();
    groups.retain(|key, _| admitted.contains(key));

    let mut writer = BufWriter::new(File::create(
        output.join("RSP_CallbackReuse.csv"))?);
    writeln!(writer, "capture_id,read_target_id,call_kind,target,target_resolution,read_candidate_class,callback_source_functions,source_owners,source_callsites,outgoing_edges,edges_per_sec,exclusive_instrumented_ms,observed_active_frames,same_frame_extra_edges,multi_callback_source_frames,multi_owner_frames,max_edges_per_frame,frame_evidence_status,dropped_frame_rows,callback_entries_proven,cache_safe_proven")?;
    for (key, _, _) in selected {
        let Some(row) = groups.get(&key) else { continue };
        // One target at a time. Release its frame map before the next one:
        // unrelated targets cannot exhaust a global evidence budget.
        let mut frames: HashMap<u64, ReadFrame> = HashMap::new();
        let mut dropped_frames = 0u64;
        for (site, stat) in callsites {
            if site.kind != key.kind || site.target != key.target { continue }
            let Some(meta) = funcs.get(&site.caller) else { continue };
            if !meta.is_mod_source || family(&meta.function).is_none() { continue }
            for &(frame, calls) in &stat.frame_calls {
                accumulate_read_frame(&mut frames, &mut dropped_frames,
                    frame, site.caller, meta.owner_hash, calls,
                    MAX_FRAME_KEYS_PER_TARGET);
            }
        }
        let extra = frames.values().map(|r| r.calls.saturating_sub(1))
            .fold(0u64, u64::saturating_add);
        let multiple_callbacks = frames.values()
            .filter(|r| r.multiple_callers).count();
        let multiple_owners = frames.values()
            .filter(|r| r.multiple_owners).count();
        let max = frames.values().map(|r| r.calls).max().unwrap_or(0);
        let id = stable_hash64(&format!("{}|{}", call_kind(key.kind), row.name));
        writeln!(writer,
            "{},RSPR-{:016X},{},{},{},{},{},{},{},{},{:.3},{:.6},{},{},{},{},{},{},{},false,false",
            CAPTURE_ID.load(Ordering::Relaxed), id, call_kind(key.kind),
            csv(&row.name), row.resolution, read_class(&row.name),
            row.callers.len(), row.owners.len(), row.callsites,
            row.calls, rate(row.calls, capture_duration_s()),
            ticks_to_ms(row.ticks), frames.len(), extra,
            multiple_callbacks, multiple_owners, max,
            if dropped_frames > 0 { "TRUNCATED_NOT_ZERO_WORK" }
            else { "CAPTURED_FRAME_OVERLAP" }, dropped_frames)?;
    }
    writer.flush()
}

#[cfg(test)]
mod callback_reuse_tests {
    use super::*;

    #[test]
    fn selects_source_read_shapes_without_granting_cache_safety() {
        assert!(read_like_target("ScriptGameInstance::GetPlayerSystem"));
        assert!(read_like_target("gameinputScriptListenerAction::GetName"));
        assert!(read_like_target("PlayerPuppet::IsInCombat"));
        assert!(!read_like_target("OperatorLess"));
        assert!(!read_like_target("Get"));
        assert!(!read_like_target("Foo::SetData"));
        assert!(!read_like_target("proxy$5021"));
        assert_eq!(read_class("gameinputScriptListenerAction::GetName"),
            "EVENT_ARGUMENT_READ");
        assert_eq!(read_class("ScriptGameInstance::GetTimeSystem"),
            "SHARED_SYSTEM_OR_CONTEXT_READ");
    }

    #[test]
    fn frame_budget_is_independent_per_target_and_repeat_calls_are_preserved() {
        let mut first = HashMap::new();
        let mut dropped_first = 0u64;
        accumulate_read_frame(&mut first, &mut dropped_first, 0, 11, 21, 2, 2);
        accumulate_read_frame(&mut first, &mut dropped_first, 1, 11, 21, 1, 2);
        accumulate_read_frame(&mut first, &mut dropped_first, 0, 12, 22, 3, 2);
        accumulate_read_frame(&mut first, &mut dropped_first, 2, 12, 22, 4, 2);
        assert_eq!(first.len(), 2);
        assert_eq!(dropped_first, 1);
        assert_eq!(first[&0].calls, 5);
        assert!(first[&0].multiple_callers);
        assert!(first[&0].multiple_owners);
        let mut next = HashMap::new();
        let mut dropped_next = 0u64;
        accumulate_read_frame(&mut next, &mut dropped_next, 2, 13, 23, 8, 2);
        assert_eq!(next.len(), 1);
        assert_eq!(next[&2].calls, 8);
        assert_eq!(dropped_next, 0);
    }

    #[test]
    fn repeated_reads_are_frame_overlaps_not_proven_redundant_calls() {
        let mut frame = ReadFrame::default();
        frame.add(1, 11, 3);
        frame.add(1, 11, 2);
        assert_eq!(frame.calls, 5);
        assert!(!frame.multiple_callers);
        assert!(!frame.multiple_owners);
        frame.add(2, 11, 4);
        assert!(frame.multiple_callers);
        assert!(!frame.multiple_owners);
        frame.add(3, 12, 1);
        assert!(frame.multiple_owners);
        assert_eq!(frame.calls.saturating_sub(1), 9);
    }
}
