use std::{
    collections::HashMap,
    env,
    ffi::c_void,
    fs::{self, File},
    io::{BufWriter, Write},
    mem,
    path::{Path, PathBuf},
    sync::{
        LazyLock,
        atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering},
    },
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use parking_lot::RwLock;
use red4ext_rs::types::{
    CName, Function, IScriptable, Instr, InvokeStatic, InvokeVirtual, OPCODE_SIZE, RedString,
    StackFrame,
};
use red4ext_rs::{
    GameApp, Plugin, PluginOps, SdkEnv, SemVer, StateListener, StateType, U16CStr, VoidPtr,
    addr_hashes, export_plugin_symbols, hooks, wcstr,
};
use static_assertions::const_assert_eq;

// Proven-safe bind-function hook point used by redscript-dap.
const BIND_FUNCTION_HASH: u32 = 777_921_665;

// Hardcoded shared capture key. We intentionally POLL instead of RegisterHotKey so
// CapFrameX can use the same F11 press at the same time.
const VK_F11: i32 = 0x7A;
const HOTKEY_POLL_MS: u64 = 2;
const SPIKE_THRESHOLD_NS: u128 = 1_000_000;
const MAX_SPIKE_EVENTS: usize = 200_000;

const STATE_PAUSED: u8 = 0;
const STATE_RECORDING: u8 = 1;
const STATE_COMPLETE: u8 = 2;

hooks! {
    static BIND_FUNCTION:
        fn(this: VoidPtr, f: *mut FunctionInfo, arg2: VoidPtr) -> bool;

    static INVOKE_STATIC_HANDLER:
        fn(i: *mut IScriptable, f: *mut StackFrame, a3: VoidPtr, a4: VoidPtr) -> ();

    static INVOKE_VIRTUAL_HANDLER:
        fn(i: *mut IScriptable, f: *mut StackFrame, a3: VoidPtr, a4: VoidPtr) -> ();
}

struct RedscriptProfilerAlpha;

export_plugin_symbols!(RedscriptProfilerAlpha);

#[derive(Debug, Clone)]
struct FunctionMeta {
    function: String,
    source_path: String,
    source_line: u32,
    owner: String,
    is_mod_source: bool,
}

#[derive(Debug, Clone, Copy, Eq, PartialEq, Hash)]
struct CallsiteKey {
    caller: usize,
    line: u16,
    kind: u8,    // 0=static, 1=virtual
    target: u64, // static pointer cast to u64, or CName hash for virtual
}

#[derive(Debug, Default, Clone, Copy)]
struct Stat {
    calls: u64,
    total_ns: u128,
    max_ns: u128,
    max_at_us: u64,
    over_1ms: u64,
    over_5ms: u64,
    over_16ms: u64,
}

#[derive(Debug, Clone, Copy)]
struct SpikeEvent {
    capture_us: u64,
    key: CallsiteKey,
    duration_ns: u128,
}

static FUNCTIONS: LazyLock<RwLock<HashMap<usize, FunctionMeta>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static CALLSITES: LazyLock<RwLock<HashMap<CallsiteKey, Stat>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static TARGET_NAMES: LazyLock<RwLock<HashMap<(u8, u64), String>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static SPIKES: LazyLock<RwLock<Vec<SpikeEvent>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));

static CONTROL_THREAD_RUNNING: AtomicBool = AtomicBool::new(false);
static BIND_HOOK_OK: AtomicBool = AtomicBool::new(false);
static STATIC_HOOK_OK: AtomicBool = AtomicBool::new(false);
static VIRTUAL_HOOK_OK: AtomicBool = AtomicBool::new(false);
static LAST_DUMP_OK: AtomicBool = AtomicBool::new(false);

static PROFILE_STATE: AtomicU8 = AtomicU8::new(STATE_PAUSED);
static CAPTURE_ID: AtomicU64 = AtomicU64::new(0);
static CAPTURE_START_UNIX_MS: AtomicU64 = AtomicU64::new(0);
static CAPTURE_STOP_UNIX_MS: AtomicU64 = AtomicU64::new(0);
static CAPTURE_ELAPSED_US: AtomicU64 = AtomicU64::new(0);
static CAPTURE_DURATION_US: AtomicU64 = AtomicU64::new(0);
static SPIKES_DROPPED: AtomicU64 = AtomicU64::new(0);

// Persisted summary of the most recently completed capture. We clear the large
// in-memory callsite/spike maps after a successful dump so the NEXT F11 start is
// almost instantaneous and stays aligned with CapFrameX.
static LAST_CALLSITE_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_OBSERVED_CALLS: AtomicU64 = AtomicU64::new(0);
static LAST_SPIKE_ROWS: AtomicU64 = AtomicU64::new(0);

#[cfg(windows)]
#[link(name = "user32")]
unsafe extern "system" {
    fn GetAsyncKeyState(v_key: i32) -> i16;
    fn GetForegroundWindow() -> *mut c_void;
    fn GetWindowThreadProcessId(hwnd: *mut c_void, process_id: *mut u32) -> u32;
}

impl Plugin for RedscriptProfilerAlpha {
    const AUTHOR: &'static U16CStr = wcstr!("RSP alpha");
    const NAME: &'static U16CStr = wcstr!("redscript-profiler-alpha");
    const VERSION: SemVer = SemVer::new(0, 2, 0);

    fn on_init(env: &SdkEnv) {
        let bind_function_addr = addr_hashes::resolve(BIND_FUNCTION_HASH);

        let bind_ok = unsafe {
            env.attach_hook(
                BIND_FUNCTION,
                mem::transmute(bind_function_addr),
                on_bind_function,
            )
        };

        BIND_HOOK_OK.store(bind_ok, Ordering::Release);

        env.info(format!(
            "[RSP alpha 0.2] bind-function hook: {}",
            if bind_ok { "OK" } else { "FAILED" }
        ));

        env.add_listener(
            StateType::Initialization,
            StateListener::default().with_on_exit(on_app_init),
        );
    }
}

unsafe extern "C" fn on_app_init(_app: &GameApp) {
    let handlers = addr_hashes::resolve(addr_hashes::OpcodeHandlers)
        as *const unsafe extern "C" fn(
            i: *mut IScriptable,
            f: *mut StackFrame,
            a3: VoidPtr,
            a4: VoidPtr,
        );

    let invoke_static_handler = unsafe { *handlers.add(InvokeStatic::OPCODE.into()) };
    let invoke_virtual_handler = unsafe { *handlers.add(InvokeVirtual::OPCODE.into()) };

    let env = RedscriptProfilerAlpha::env();

    let static_ok = unsafe {
        env.attach_hook(
            INVOKE_STATIC_HANDLER,
            invoke_static_handler,
            on_invoke_static,
        )
    };

    let virtual_ok = unsafe {
        env.attach_hook(
            INVOKE_VIRTUAL_HANDLER,
            invoke_virtual_handler,
            on_invoke_virtual,
        )
    };

    STATIC_HOOK_OK.store(static_ok, Ordering::Release);
    VIRTUAL_HOOK_OK.store(virtual_ok, Ordering::Release);

    env.info(format!(
        "[RSP alpha 0.2] InvokeStatic hook: {} / InvokeVirtual hook: {}",
        if static_ok { "OK" } else { "FAILED" },
        if virtual_ok { "OK" } else { "FAILED" }
    ));

    PROFILE_STATE.store(STATE_PAUSED, Ordering::Release);
    clear_stale_results();
    let _ = write_status_file();
    start_control_thread();
}

unsafe extern "C" fn on_bind_function(
    this: VoidPtr,
    info: *mut FunctionInfo,
    arg: VoidPtr,
    cb: unsafe extern "C" fn(this: VoidPtr, f: *mut FunctionInfo, arg2: VoidPtr) -> bool,
) -> bool {
    let ret = unsafe { cb(this, info, arg) };

    let Some(info) = (unsafe { info.as_ref() }) else {
        return ret;
    };
    if info.func.is_null() || info.source_info.is_null() {
        return ret;
    }

    let func = unsafe { &*info.func };
    let source = unsafe { &*info.source_info };

    let source_path = source.path.to_string_lossy().into_owned();
    let function = function_name(func);
    let owner = owner_from_path(&source_path);
    let is_mod_source = is_mod_source(&source_path);

    FUNCTIONS.write().insert(
        info.func as usize,
        FunctionMeta {
            function,
            source_path,
            source_line: info.source_line,
            owner,
            is_mod_source,
        },
    );

    ret
}

unsafe extern "C" fn on_invoke_static(
    i: *mut IScriptable,
    f: *mut StackFrame,
    a3: VoidPtr,
    a4: VoidPtr,
    cb: unsafe extern "C" fn(i: *mut IScriptable, f: *mut StackFrame, a3: VoidPtr, a4: VoidPtr),
) {
    // Critical Alpha 0.2 change: while PAUSED/COMPLETE, the hook does almost
    // nothing beyond one atomic branch. Startup/load work is NOT recorded.
    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    let Some(frame) = (unsafe { f.as_ref() }) else {
        unsafe { cb(i, f, a3, a4) };
        return;
    };

    if !frame.has_code() {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    let Some(instr) = (unsafe { frame.instr_at::<InvokeStatic>(-OPCODE_SIZE) }) else {
        unsafe { cb(i, f, a3, a4) };
        return;
    };

    let caller = frame.func() as *const Function as usize;
    let line = instr.line;
    let target_ptr = instr.func as usize;

    let should_profile = FUNCTIONS
        .read()
        .get(&caller)
        .is_some_and(|m| m.is_mod_source);

    if !should_profile {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    let target_key = target_ptr as u64;
    ensure_static_target_name(target_ptr, target_key);

    let start = Instant::now();
    unsafe { cb(i, f, a3, a4) };
    let elapsed = start.elapsed();

    // If F11 stopped the capture while this call was executing, don't let a
    // boundary-crossing call leak into the completed window.
    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        return;
    }

    record_callsite(
        CallsiteKey {
            caller,
            line,
            kind: 0,
            target: target_key,
        },
        elapsed,
    );
}

unsafe extern "C" fn on_invoke_virtual(
    i: *mut IScriptable,
    f: *mut StackFrame,
    a3: VoidPtr,
    a4: VoidPtr,
    cb: unsafe extern "C" fn(i: *mut IScriptable, f: *mut StackFrame, a3: VoidPtr, a4: VoidPtr),
) {
    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    let Some(frame) = (unsafe { f.as_ref() }) else {
        unsafe { cb(i, f, a3, a4) };
        return;
    };

    if !frame.has_code() {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    let Some(instr) = (unsafe { frame.instr_at::<InvokeVirtual>(-OPCODE_SIZE) }) else {
        unsafe { cb(i, f, a3, a4) };
        return;
    };

    let caller = frame.func() as *const Function as usize;
    let line = instr.line;
    let target_name = instr.name;
    let target_hash = u64::from(target_name);

    let should_profile = FUNCTIONS
        .read()
        .get(&caller)
        .is_some_and(|m| m.is_mod_source);

    if !should_profile {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    TARGET_NAMES
        .write()
        .entry((1, target_hash))
        .or_insert_with(|| target_name.as_str().to_owned());

    let start = Instant::now();
    unsafe { cb(i, f, a3, a4) };
    let elapsed = start.elapsed();

    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        return;
    }

    record_callsite(
        CallsiteKey {
            caller,
            line,
            kind: 1,
            target: target_hash,
        },
        elapsed,
    );
}

fn record_callsite(key: CallsiteKey, elapsed: Duration) {
    let ns = elapsed.as_nanos();
    let capture_us = CAPTURE_ELAPSED_US.load(Ordering::Relaxed);

    {
        let mut guard = CALLSITES.write();
        let stat = guard.entry(key).or_default();

        stat.calls += 1;
        stat.total_ns += ns;

        if ns > stat.max_ns {
            stat.max_ns = ns;
            stat.max_at_us = capture_us;
        }

        if ns >= 1_000_000 {
            stat.over_1ms += 1;
        }
        if ns >= 5_000_000 {
            stat.over_5ms += 1;
        }
        if ns >= 16_670_000 {
            stat.over_16ms += 1;
        }
    }

    // Individual event timeline is intentionally sparse: only >=1 ms events.
    // This gives us CapFrameX correlation without recording millions of rows.
    if ns >= SPIKE_THRESHOLD_NS {
        let mut spikes = SPIKES.write();
        if spikes.len() < MAX_SPIKE_EVENTS {
            spikes.push(SpikeEvent {
                capture_us,
                key,
                duration_ns: ns,
            });
        } else {
            SPIKES_DROPPED.fetch_add(1, Ordering::Relaxed);
        }
    }
}

fn ensure_static_target_name(ptr: usize, key: u64) {
    if TARGET_NAMES.read().contains_key(&(0, key)) {
        return;
    }

    let name = if ptr == 0 {
        "<null>".to_owned()
    } else if let Some(meta) = FUNCTIONS.read().get(&ptr) {
        meta.function.clone()
    } else {
        let func = unsafe { &*(ptr as *const Function) };
        function_name(func)
    };

    TARGET_NAMES.write().insert((0, key), name);
}

fn function_name(func: &Function) -> String {
    let raw = func.name().as_str();
    let short = raw.split_once(';').map_or(raw, |(n, _)| n);
    match func.parent() {
        Some(parent) => format!("{}::{}", parent.name().as_str(), short),
        None => short.to_owned(),
    }
}

fn is_mod_source(path: &str) -> bool {
    let p = path.replace('/', "\\").to_ascii_lowercase();
    p.contains("\\r6\\scripts\\")
}

fn owner_from_path(path: &str) -> String {
    let normalized = path.replace('/', "\\");
    let lower = normalized.to_ascii_lowercase();
    let marker = "\\r6\\scripts\\";

    if let Some(pos) = lower.find(marker) {
        let tail = &normalized[pos + marker.len()..];
        if let Some((owner, _)) = tail.split_once('\\') {
            return owner.to_owned();
        }
        if !tail.is_empty() {
            return tail.to_owned();
        }
    }

    "<non-r6-script>".to_owned()
}

fn start_control_thread() {
    if CONTROL_THREAD_RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }

    thread::spawn(|| {
        let mut was_down = false;
        let mut capture_start: Option<Instant> = None;

        loop {
            if PROFILE_STATE.load(Ordering::Relaxed) == STATE_RECORDING {
                if let Some(start) = capture_start {
                    CAPTURE_ELAPSED_US.store(duration_to_us(start.elapsed()), Ordering::Relaxed);
                }
            }

            let down = hotkey_is_down();

            if down && !was_down && game_is_foreground() {
                if PROFILE_STATE.load(Ordering::Acquire) == STATE_RECORDING {
                    if let Some(start) = capture_start.take() {
                        finish_capture(start);
                    }
                } else {
                    capture_start = Some(begin_capture());
                }
            }

            was_down = down;
            thread::sleep(Duration::from_millis(HOTKEY_POLL_MS));
        }
    });
}

fn begin_capture() -> Instant {
    // These should already be empty after the previous successful dump, but
    // clear defensively before opening a new window.
    CALLSITES.write().clear();
    SPIKES.write().clear();

    SPIKES_DROPPED.store(0, Ordering::Relaxed);
    LAST_CALLSITE_ROWS.store(0, Ordering::Relaxed);
    LAST_OBSERVED_CALLS.store(0, Ordering::Relaxed);
    LAST_SPIKE_ROWS.store(0, Ordering::Relaxed);
    LAST_DUMP_OK.store(false, Ordering::Relaxed);

    CAPTURE_STOP_UNIX_MS.store(0, Ordering::Relaxed);
    CAPTURE_DURATION_US.store(0, Ordering::Relaxed);
    CAPTURE_ELAPSED_US.store(0, Ordering::Relaxed);
    CAPTURE_START_UNIX_MS.store(unix_ms_now(), Ordering::Relaxed);
    CAPTURE_ID.fetch_add(1, Ordering::Relaxed);

    let start = Instant::now();
    PROFILE_STATE.store(STATE_RECORDING, Ordering::Release);
    start
}

fn finish_capture(start: Instant) {
    let duration_us = duration_to_us(start.elapsed());

    // Close the measurement window BEFORE any file I/O. From this point the
    // opcode hooks immediately bypass profiling again.
    CAPTURE_ELAPSED_US.store(duration_us, Ordering::Relaxed);
    CAPTURE_DURATION_US.store(duration_us, Ordering::Relaxed);
    CAPTURE_STOP_UNIX_MS.store(unix_ms_now(), Ordering::Relaxed);
    PROFILE_STATE.store(STATE_COMPLETE, Ordering::Release);

    let (callsite_rows, observed_calls) = {
        let stats = CALLSITES.read();
        let rows = stats.len() as u64;
        let calls = stats.values().map(|s| s.calls).sum::<u64>();
        (rows, calls)
    };
    let spike_rows = SPIKES.read().len() as u64;

    LAST_CALLSITE_ROWS.store(callsite_rows, Ordering::Relaxed);
    LAST_OBSERVED_CALLS.store(observed_calls, Ordering::Relaxed);
    LAST_SPIKE_ROWS.store(spike_rows, Ordering::Relaxed);

    let dump_ok = dump_results().is_ok();
    LAST_DUMP_OK.store(dump_ok, Ordering::Release);
    let _ = write_status_file();

    // Keep the generated CSVs on disk, but free the big runtime measurement
    // maps so the next shared F11 press does not spend time clearing 40k rows.
    if dump_ok {
        CALLSITES.write().clear();
        SPIKES.write().clear();
    }
}

#[cfg(windows)]
fn hotkey_is_down() -> bool {
    let state = unsafe { GetAsyncKeyState(VK_F11) };
    (state as u16 & 0x8000) != 0
}

#[cfg(not(windows))]
fn hotkey_is_down() -> bool {
    false
}

#[cfg(windows)]
fn game_is_foreground() -> bool {
    let hwnd = unsafe { GetForegroundWindow() };
    if hwnd.is_null() {
        return false;
    }

    let mut foreground_pid = 0u32;
    unsafe {
        GetWindowThreadProcessId(hwnd, &mut foreground_pid as *mut u32);
    }

    foreground_pid == std::process::id()
}

#[cfg(not(windows))]
fn game_is_foreground() -> bool {
    true
}

fn duration_to_us(duration: Duration) -> u64 {
    duration.as_micros().min(u64::MAX as u128) as u64
}

fn unix_ms_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis().min(u64::MAX as u128) as u64)
        .unwrap_or(0)
}

fn clear_stale_results() {
    let Some(dir) = results_dir() else {
        return;
    };

    // Alpha 0.1 wrote cumulative startup-inclusive CSVs. Remove stale snapshots
    // once on load so they cannot be mistaken for an Alpha 0.2 capture.
    for name in [
        "RSP_Alpha_Capture.csv",
        "RSP_Alpha_CallSites.csv",
        "RSP_Alpha_Spikes.csv",
        "RSP_Alpha_FunctionMap.csv",
    ] {
        let _ = fs::remove_file(dir.join(name));
    }
}

fn results_dir() -> Option<PathBuf> {
    let exe = env::current_exe().ok()?;
    let game_root = exe.parent()?.parent()?.parent()?;
    Some(
        game_root
            .join("red4ext")
            .join("plugins")
            .join("redscript_profiler_alpha")
            .join("RESULTS"),
    )
}

fn state_name(state: u8) -> &'static str {
    match state {
        STATE_RECORDING => "RECORDING",
        STATE_COMPLETE => "COMPLETE",
        _ => "PAUSED",
    }
}

fn mapped_mod_function_count() -> u64 {
    FUNCTIONS
        .read()
        .values()
        .filter(|m| m.is_mod_source)
        .count() as u64
}

fn write_status_file() -> std::io::Result<()> {
    let Some(dir) = results_dir() else {
        return Ok(());
    };
    fs::create_dir_all(&dir)?;

    let state = PROFILE_STATE.load(Ordering::Acquire);
    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
    let start_ms = CAPTURE_START_UNIX_MS.load(Ordering::Relaxed);
    let stop_ms = CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed);
    let duration_us = CAPTURE_DURATION_US.load(Ordering::Relaxed);
    let dropped = SPIKES_DROPPED.load(Ordering::Relaxed);

    let status = format!(
        "RSP alpha 0.2 controlled runtime callsite profiler\n\
         Bind/source mapping hook: {}\n\
         InvokeStatic hook: {}\n\
         InvokeVirtual hook: {}\n\
         State: {}\n\
         Hotkey: F11 (shared key; polling, not exclusive registration)\n\
         Foreground guard: Cyberpunk process only\n\
         Hotkey poll interval: {} ms\n\
         Startup/load profiling: OFF by default\n\
         CSV writes during capture: NONE\n\
         Capture ID: {}\n\
         Capture start Unix ms: {}\n\
         Capture stop Unix ms: {}\n\
         Capture duration ms: {:.3}\n\
         Mapped mod functions: {}\n\
         Last completed callsite rows: {}\n\
         Last completed observed calls: {}\n\
         Last completed spike rows (>=1 ms): {}\n\
         Dropped spike rows: {}\n\
         Last CSV dump: {}\n",
        if BIND_HOOK_OK.load(Ordering::Acquire) { "OK" } else { "FAILED" },
        if STATIC_HOOK_OK.load(Ordering::Acquire) { "OK" } else { "FAILED" },
        if VIRTUAL_HOOK_OK.load(Ordering::Acquire) { "OK" } else { "FAILED" },
        state_name(state),
        HOTKEY_POLL_MS,
        capture_id,
        start_ms,
        stop_ms,
        duration_us as f64 / 1_000.0,
        mapped_mod_function_count(),
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        dropped,
        if LAST_DUMP_OK.load(Ordering::Acquire) { "OK" } else { "NOT YET / FAILED" },
    );

    fs::write(dir.join("RSP_Alpha_Status.txt"), status)
}

fn dump_results() -> std::io::Result<()> {
    let Some(dir) = results_dir() else {
        return Ok(());
    };
    fs::create_dir_all(&dir)?;

    dump_capture_csv(&dir.join("RSP_Alpha_Capture.csv"))?;
    dump_callsite_csv(&dir.join("RSP_Alpha_CallSites.csv"))?;
    dump_spikes_csv(&dir.join("RSP_Alpha_Spikes.csv"))?;
    dump_function_map_csv(&dir.join("RSP_Alpha_FunctionMap.csv"))?;

    Ok(())
}

fn dump_capture_csv(path: &Path) -> std::io::Result<()> {
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);

    writeln!(
        w,
        "capture_id,state,start_unix_ms,stop_unix_ms,duration_ms,hotkey,poll_ms,mapped_mod_functions,callsite_rows,observed_calls,spike_rows,dropped_spikes"
    )?;

    writeln!(
        w,
        "{},{},{},{},{:.3},F11,{},{},{},{},{},{}",
        CAPTURE_ID.load(Ordering::Relaxed),
        state_name(PROFILE_STATE.load(Ordering::Acquire)),
        CAPTURE_START_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        HOTKEY_POLL_MS,
        mapped_mod_function_count(),
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        SPIKES_DROPPED.load(Ordering::Relaxed),
    )?;

    w.flush()
}

fn dump_callsite_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();

    let mut rows: Vec<_> = stats.iter().collect();
    rows.sort_by(|a, b| b.1.total_ns.cmp(&a.1.total_ns));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);

    writeln!(
        w,
        "capture_id,owner,source_path,source_function,source_line,call_kind,target,calls,total_ms,avg_us,max_ms,max_at_capture_ms,over_1ms,over_5ms,over_16_67ms"
    )?;

    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);

    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else {
            continue;
        };

        let target = targets
            .get(&(key.kind, key.target))
            .cloned()
            .unwrap_or_else(|| format!("0x{:016X}", key.target));

        let kind = if key.kind == 0 { "static" } else { "virtual" };
        let total_ms = stat.total_ns as f64 / 1_000_000.0;
        let avg_us = if stat.calls > 0 {
            stat.total_ns as f64 / stat.calls as f64 / 1_000.0
        } else {
            0.0
        };
        let max_ms = stat.max_ns as f64 / 1_000_000.0;

        writeln!(
            w,
            "{},{},{},{},{},{},{},{},{:.6},{:.3},{:.6},{:.3},{},{},{}",
            capture_id,
            csv(&meta.owner),
            csv(&meta.source_path),
            csv(&meta.function),
            key.line,
            kind,
            csv(&target),
            stat.calls,
            total_ms,
            avg_us,
            max_ms,
            stat.max_at_us as f64 / 1_000.0,
            stat.over_1ms,
            stat.over_5ms,
            stat.over_16ms,
        )?;
    }

    w.flush()
}

fn dump_spikes_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let mut spikes = SPIKES.read().clone();
    spikes.sort_by_key(|e| e.capture_us);

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);

    writeln!(
        w,
        "capture_id,capture_ms,owner,source_path,source_function,source_line,call_kind,target,duration_ms,threshold"
    )?;

    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);

    for event in spikes {
        let Some(meta) = funcs.get(&event.key.caller) else {
            continue;
        };

        let target = targets
            .get(&(event.key.kind, event.key.target))
            .cloned()
            .unwrap_or_else(|| format!("0x{:016X}", event.key.target));

        let kind = if event.key.kind == 0 { "static" } else { "virtual" };
        let threshold = if event.duration_ns >= 16_670_000 {
            ">=16.67ms"
        } else if event.duration_ns >= 5_000_000 {
            ">=5ms"
        } else {
            ">=1ms"
        };

        writeln!(
            w,
            "{},{:.3},{},{},{},{},{},{},{:.6},{}",
            capture_id,
            event.capture_us as f64 / 1_000.0,
            csv(&meta.owner),
            csv(&meta.source_path),
            csv(&meta.function),
            event.key.line,
            kind,
            csv(&target),
            event.duration_ns as f64 / 1_000_000.0,
            threshold,
        )?;
    }

    w.flush()
}

fn dump_function_map_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let mut rows: Vec<_> = funcs.iter().filter(|(_, m)| m.is_mod_source).collect();
    rows.sort_by(|a, b| {
        a.1.source_path
            .cmp(&b.1.source_path)
            .then(a.1.source_line.cmp(&b.1.source_line))
    });

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "function_ptr,owner,source_path,source_line,function")?;

    for (ptr, meta) in rows {
        writeln!(
            w,
            "0x{:016X},{},{},{},{}",
            ptr,
            csv(&meta.owner),
            csv(&meta.source_path),
            meta.source_line,
            csv(&meta.function)
        )?;
    }

    w.flush()
}

fn csv(s: &str) -> String {
    let escaped = s.replace('"', "\"\"");
    format!("\"{}\"", escaped)
}

#[repr(C)]
struct FunctionInfo {
    vft: VoidPtr,
    name: CName,
    unk: u64,
    func: *mut Function,
    padding: [u8; 160],
    source_info: *mut SourceFileInfo,
    source_line: u32,
}

const_assert_eq!(mem::size_of::<FunctionInfo>(), 208);

#[repr(C)]
struct SourceFileInfo {
    vfs: VoidPtr,
    name: CName,
    unk: u64,
    crc: u32,
    index: u32,
    path_hash: u32,
    path: RedString,
}

const_assert_eq!(mem::size_of::<SourceFileInfo>(), 72);
