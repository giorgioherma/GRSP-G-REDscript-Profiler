//! Post-STOP source/VM boundary attribution for costly unresolved virtual
//! proxy and wrapper calls. Does not resolve the target receiver, count
//! callback entries, change VM hooks, or charge downstream cost to the caller.
use super::*;

fn is_proxy(name: &str) -> bool {
    name.contains("proxy$") || name.contains("wrapper$")
}

fn classification(resolution: &str, target: &str) -> &'static str {
    if !is_proxy(target) { return "NOT_PROXY_OR_WRAPPER"; }
    if resolution == "UNRESOLVED_VIRTUAL" { "UNRESOLVED_VIRTUAL_RECEIVER" }
    else if resolution == "UNRESOLVED_STATIC" { "UNRESOLVED_STATIC_TARGET" }
    else { "NAMED_TARGET_RECEIVER_NOT_PROVEN" }
}

/// Exact captured frames and callsite times only. Sparse hot paths are
/// threshold- and cap-limited; their count is not a complete call histogram.
pub(super) fn export(developer_dir: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let callsites = CALLSITES.read();
    let hot_paths = HOT_PATHS.read();
    let dropped = HOT_PATHS_DROPPED.load(Ordering::Relaxed);
    let capture = CAPTURE_ID.load(Ordering::Relaxed);
    let duration = capture_duration_s();

    let mut rows: Vec<_> = callsites.iter().filter_map(|(key, stat)| {
        let meta = funcs.get(&key.caller)?;
        let target = target_info(*key, &funcs, &targets);
        if !is_proxy(&target.display) { return None; }
        Some((*key, stat, meta, target))
    }).collect();
    rows.sort_by(|a, b|
        b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks)
            .then(b.1.calls.cmp(&a.1.calls))
            .then(a.2.function.cmp(&b.2.function)));

    // One bounded pass over the existing sparse hot-path buffer.
    // Never scan the whole buffer again per callsite at F11 collection.
    let mut path_context: HashMap<CallsiteKey, (u64, usize, String)> = HashMap::new();
    for event in hot_paths.iter() {
        let Some(&last) = event.path.last() else { continue; };
        let mut parent = String::new();
        if event.path.len() >= 2 {
            let ancestor = event.path[event.path.len() - 2];
            if let Some(caller) = funcs.get(&ancestor.caller) {
                parent = format!("{}::{}", caller.owner, caller.function);
            }
        }
        let entry = path_context.entry(last)
            .or_insert_with(|| (0, 0, String::new()));
        entry.0 = entry.0.saturating_add(1);
        entry.1 = entry.1.max(event.path.len());
        if entry.2.is_empty() {
            entry.2 = parent;
        } else if !parent.is_empty() && entry.2 != parent {
            entry.2 = "MULTIPLE_CAPTURED_PARENTS".to_owned();
        }
    }

    let mut out = BufWriter::new(File::create(
        developer_dir.join("RSP_ProxyAttribution.csv"))?);
    writeln!(out, "capture_id,stable_callsite_id,source_owner,source_function,source_path,source_line,call_kind,target,target_resolution,attribution_status,outgoing_edges,outgoing_edges_per_sec,exclusive_instrumented_ms,observed_inclusive_ms,max_inclusive_ms,active_frames,max_edges_per_frame,sparse_observed_paths,sparse_observed_max_depth,sparse_observed_parent,hot_paths_dropped_global,receiver_proven,callback_entries_known,causality_proven")?;

    for (key, stat, meta, target) in rows {
        let (sampled, max_depth, parent) = path_context.get(&key)
            .map(|(count, depth, parent)| (*count, *depth, parent.as_str()))
            .unwrap_or((0, 0, ""));
        writeln!(out,
            "{},RSPC-{:016X},{},{},{},{},{},{},{},{},{},{:.3},{:.6},{:.6},{:.6},{},{},{},{},{},{},{},{},{}",
            capture,
            stable_callsite_hash(meta, key, &target),
            csv(&meta.owner), csv(&meta.function), csv(&meta.source_path),
            key.line, call_kind(key.kind), csv(&target.display),
            target.resolution, classification(target.resolution, &target.display),
            stat.calls, rate(stat.calls, duration),
            ticks_to_ms(stat.exclusive_ticks),
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.max_inclusive_ticks),
            stat.active_frames, stat.max_calls_per_frame,
            sampled, max_depth, csv(&parent), dropped,
            false, false, false)?;
    }
    out.flush()
}

#[cfg(test)]
mod tests {
    use super::{classification, is_proxy};

    #[test]
    fn unknown_virtual_proxy_never_claims_receiver_or_causality() {
        assert!(is_proxy("proxy$205935"));
        assert!(is_proxy("wrapper$205934"));
        assert!(!is_proxy("IsAtToilet"));
        assert_eq!(classification("UNRESOLVED_VIRTUAL", "proxy$205935"),
            "UNRESOLVED_VIRTUAL_RECEIVER");
        assert_eq!(classification("DIRECT", "wrapper$205934"),
            "NAMED_TARGET_RECEIVER_NOT_PROVEN");
        assert_eq!(classification("UNRESOLVED_VIRTUAL", "IsAtToilet"),
            "NOT_PROXY_OR_WRAPPER");
    }
}
