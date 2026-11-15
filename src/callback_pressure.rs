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
    detail.flush()
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
