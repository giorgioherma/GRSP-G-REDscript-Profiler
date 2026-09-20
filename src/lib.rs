use std::{
    cell::RefCell,
    collections::{HashMap, HashSet},
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
    time::{Duration, SystemTime, UNIX_EPOCH},
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

const BIND_FUNCTION_HASH: u32 = 777_921_665;
const VK_F11: i32 = 0x7A;
const HOTKEY_POLL_MS: u64 = 2;
const SPIKE_THRESHOLD_US: u64 = 1_000;
const MAX_SPIKE_EVENTS: usize = 100_000;
const MAX_HOT_PATH_EVENTS: usize = 50_000;
const FINAL_FLUSH_WAIT_MS: u64 = 250;

const STATE_PAUSED: u8 = 0;
const STATE_RECORDING: u8 = 1;
const STATE_COMPLETE: u8 = 2;

const CADENCE_BUCKETS: usize = 10;

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
    target: u64, // static function pointer or virtual CName hash
}

#[derive(Debug, Default, Clone, Copy)]
struct LocalStat {
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    max_inclusive_ticks: u64,
    max_exclusive_ticks: u64,
    max_at_qpc: u64,
    first_qpc: u64,
    last_qpc: u64,
    over_1ms: u64,
    over_5ms: u64,
    over_16ms: u64,
}

#[derive(Debug, Default, Clone)]
struct AggStat {
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    max_inclusive_ticks: u64,
    max_exclusive_ticks: u64,
    max_at_qpc: u64,
    over_1ms: u64,
    over_5ms: u64,
    over_16ms: u64,
    active_frames: u64,
    repeated_frames: u64,
    max_calls_per_frame: u64,
    first_qpc: u64,
    last_qpc: u64,
    last_active_qpc: u64,
    cadence: [u64; CADENCE_BUCKETS],
}

#[derive(Debug, Default, Clone)]
struct EntityAgg {
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    active_frames: u64,
    max_calls_per_frame: u64,
    max_inclusive_ticks: u64,
    over_1ms: u64,
    over_5ms: u64,
    over_16ms: u64,
}

#[derive(Debug, Clone)]
struct FrameStat {
    frame_id: u64,
    frame_start_qpc: u64,
    frame_end_qpc: u64,
    total_calls: u64,
    unique_callsites: u64,
    unique_owners: u64,
    unique_functions: u64,
    max_call_depth: u32,
    observed_inclusive_ticks: u64,
    exclusive_instrumented_ticks: u64,
    largest_call_ticks: u64,
    spike_count: u64,
    partial: bool,
}

#[derive(Debug, Clone)]
struct FrameOwnerStat {
    frame_id: u64,
    owner: String,
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    max_call_ticks: u64,
    spike_count: u64,
}

#[derive(Debug, Clone)]
struct SpikeEvent {
    qpc: u64,
    frame_id: u64,
    key: CallsiteKey,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    depth: u32,
}

#[derive(Debug, Clone)]
struct HotPathEvent {
    qpc: u64,
    frame_id: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    path: Vec<CallsiteKey>,
}

#[derive(Debug, Clone, Copy)]
struct ActiveCall {
    key: CallsiteKey,
    start_qpc: u64,
    child_ticks: u64,
    frame_id: u64,
}

#[derive(Default)]
struct ThreadState {
    capture_id: u64,
    caller_mod_cache: HashMap<usize, bool>,
    virtual_name_seen: HashSet<u64>,
    stack: Vec<ActiveCall>,
    frame_stats: HashMap<CallsiteKey, LocalStat>,
    frame_max_depth: u32,
    registered_capture_id: u64,
}

impl ThreadState {
    fn ensure_capture(&mut self, capture_id: u64) {
        if self.capture_id != capture_id {
            self.capture_id = capture_id;
            self.stack.clear();
            self.frame_stats.clear();
            self.frame_max_depth = 0;
        }
    }
}

thread_local! {
    static TLS_STATE: RefCell<ThreadState> = RefCell::new(ThreadState::default());
}

static FUNCTIONS: LazyLock<RwLock<HashMap<usize, FunctionMeta>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static TARGET_NAMES: LazyLock<RwLock<HashMap<(u8, u64), String>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static CALLSITES: LazyLock<RwLock<HashMap<CallsiteKey, AggStat>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static OWNER_AGG: LazyLock<RwLock<HashMap<String, EntityAgg>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static FUNCTION_AGG: LazyLock<RwLock<HashMap<usize, EntityAgg>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static FRAMES: LazyLock<RwLock<Vec<FrameStat>>> = LazyLock::new(|| RwLock::new(Vec::new()));
static FRAME_OWNERS: LazyLock<RwLock<Vec<FrameOwnerStat>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));
static SPIKES: LazyLock<RwLock<Vec<SpikeEvent>>> = LazyLock::new(|| RwLock::new(Vec::new()));
static HOT_PATHS: LazyLock<RwLock<Vec<HotPathEvent>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));
static OBSERVED_THREADS: LazyLock<RwLock<HashSet<u32>>> =
    LazyLock::new(|| RwLock::new(HashSet::new()));

static CONTROL_THREAD_RUNNING: AtomicBool = AtomicBool::new(false);
static BIND_HOOK_OK: AtomicBool = AtomicBool::new(false);
static STATIC_HOOK_OK: AtomicBool = AtomicBool::new(false);
static VIRTUAL_HOOK_OK: AtomicBool = AtomicBool::new(false);
static FRAME_LISTENER_OK: AtomicBool = AtomicBool::new(false);
static LAST_DUMP_OK: AtomicBool = AtomicBool::new(false);
static FINAL_FLUSH_REQUESTED: AtomicBool = AtomicBool::new(false);
static FINAL_FLUSH_DONE: AtomicBool = AtomicBool::new(false);
static LAST_FINAL_FLUSH_OK: AtomicBool = AtomicBool::new(false);

static PROFILE_STATE: AtomicU8 = AtomicU8::new(STATE_PAUSED);
static CAPTURE_ID: AtomicU64 = AtomicU64::new(0);
static CAPTURE_START_UNIX_MS: AtomicU64 = AtomicU64::new(0);
static CAPTURE_STOP_UNIX_MS: AtomicU64 = AtomicU64::new(0);
static CAPTURE_START_QPC: AtomicU64 = AtomicU64::new(0);
static CAPTURE_STOP_QPC: AtomicU64 = AtomicU64::new(0);
static CAPTURE_DURATION_US: AtomicU64 = AtomicU64::new(0);
static QPC_FREQUENCY: AtomicU64 = AtomicU64::new(0);
static FRAME_ID: AtomicU64 = AtomicU64::new(0);
static FRAME_START_QPC: AtomicU64 = AtomicU64::new(0);
static SPIKES_DROPPED: AtomicU64 = AtomicU64::new(0);
static HOT_PATHS_DROPPED: AtomicU64 = AtomicU64::new(0);

static LAST_CALLSITE_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_OBSERVED_CALLS: AtomicU64 = AtomicU64::new(0);
static LAST_SPIKE_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_HOT_PATH_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_FRAME_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_OWNER_ROWS: AtomicU64 = AtomicU64::new(0);
static LAST_FUNCTION_ROWS: AtomicU64 = AtomicU64::new(0);

#[cfg(windows)]
#[link(name = "user32")]
unsafe extern "system" {
    fn GetAsyncKeyState(v_key: i32) -> i16;
    fn GetForegroundWindow() -> *mut c_void;
    fn GetWindowThreadProcessId(hwnd: *mut c_void, process_id: *mut u32) -> u32;
}

#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn QueryPerformanceCounter(value: *mut i64) -> i32;
    fn QueryPerformanceFrequency(value: *mut i64) -> i32;
    fn GetCurrentThreadId() -> u32;
    fn Beep(frequency: u32, duration_ms: u32) -> i32;
}

impl Plugin for RedscriptProfilerAlpha {
    const AUTHOR: &'static U16CStr = wcstr!("RSP alpha");
    const NAME: &'static U16CStr = wcstr!("redscript-profiler-alpha");
    const VERSION: SemVer = SemVer::new(0, 3, 0);

    fn on_init(env: &SdkEnv) {
        init_qpc();

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
            "[RSP alpha 0.3] bind-function hook: {}",
            if bind_ok { "OK" } else { "FAILED" }
        ));

        env.add_listener(
            StateType::Initialization,
            StateListener::default().with_on_exit(on_app_init),
        );

        let frame_ok = env.add_listener(
            StateType::Running,
            StateListener::default().with_on_update(on_running_update),
        );
        FRAME_LISTENER_OK.store(frame_ok, Ordering::Release);

        env.info(format!(
            "[RSP alpha 0.3] running-frame listener: {}",
            if frame_ok { "OK" } else { "FAILED" }
        ));
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
        "[RSP alpha 0.3] InvokeStatic hook: {} / InvokeVirtual hook: {}",
        if static_ok { "OK" } else { "FAILED" },
        if virtual_ok { "OK" } else { "FAILED" }
    ));

    PROFILE_STATE.store(STATE_PAUSED, Ordering::Release);
    clear_stale_results();
    let _ = write_status_file();
    start_control_thread();
}

unsafe extern "C" fn on_running_update(_app: &GameApp) {
    if PROFILE_STATE.load(Ordering::Acquire) == STATE_RECORDING {
        let end_qpc = qpc_now();
        let frame_id = FRAME_ID.load(Ordering::Relaxed);
        let start_qpc = FRAME_START_QPC.swap(end_qpc, Ordering::AcqRel);
        flush_current_thread_frame(frame_id, start_qpc, end_qpc, false);
        FRAME_ID.fetch_add(1, Ordering::Relaxed);
        return;
    }

    if FINAL_FLUSH_REQUESTED.swap(false, Ordering::AcqRel) {
        let stop_qpc = CAPTURE_STOP_QPC.load(Ordering::Acquire);
        let frame_id = FRAME_ID.load(Ordering::Relaxed);
        let start_qpc = FRAME_START_QPC.load(Ordering::Relaxed);
        flush_current_thread_frame(frame_id, start_qpc, stop_qpc, true);
        FINAL_FLUSH_DONE.store(true, Ordering::Release);
    }
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

    let key = CallsiteKey {
        caller: frame.func() as *const Function as usize,
        line: instr.line,
        kind: 0,
        target: instr.func as usize as u64,
    };

    if !enter_profiled_call(key) {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    unsafe { cb(i, f, a3, a4) };
    exit_profiled_call(key, qpc_now());
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

    let target_hash = u64::from(instr.name);
    let key = CallsiteKey {
        caller: frame.func() as *const Function as usize,
        line: instr.line,
        kind: 1,
        target: target_hash,
    };

    if !enter_profiled_call(key) {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    register_virtual_target_name(target_hash, instr.name);

    unsafe { cb(i, f, a3, a4) };
    exit_profiled_call(key, qpc_now());
}

fn enter_profiled_call(key: CallsiteKey) -> bool {
    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        return false;
    }

    TLS_STATE.with(|cell| {
        let mut t = cell.borrow_mut();
        let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
        t.ensure_capture(capture_id);
        register_observed_thread(&mut t, capture_id);

        let is_mod = if let Some(v) = t.caller_mod_cache.get(&key.caller) {
            *v
        } else {
            let v = FUNCTIONS
                .read()
                .get(&key.caller)
                .is_some_and(|m| m.is_mod_source);
            t.caller_mod_cache.insert(key.caller, v);
            v
        };

        if !is_mod {
            return false;
        }

        let start_qpc = qpc_now();
        let frame_id = FRAME_ID.load(Ordering::Relaxed);
        t.stack.push(ActiveCall {
            key,
            start_qpc,
            child_ticks: 0,
            frame_id,
        });
        t.frame_max_depth = t.frame_max_depth.max(t.stack.len() as u32);
        true
    })
}

fn exit_profiled_call(expected_key: CallsiteKey, end_qpc: u64) {
    let mut sparse_event: Option<(SpikeEvent, HotPathEvent)> = None;

    TLS_STATE.with(|cell| {
        let mut t = cell.borrow_mut();
        let Some(active) = t.stack.pop() else {
            return;
        };

        if active.key != expected_key {
            t.stack.clear();
            return;
        }

        let inclusive = end_qpc.saturating_sub(active.start_qpc);
        let exclusive = inclusive.saturating_sub(active.child_ticks);

        if let Some(parent) = t.stack.last_mut() {
            parent.child_ticks = parent.child_ticks.saturating_add(inclusive);
        }

        if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING
            || t.capture_id != CAPTURE_ID.load(Ordering::Relaxed)
        {
            return;
        }

        let stat = t.frame_stats.entry(active.key).or_default();
        stat.calls += 1;
        stat.inclusive_ticks = stat.inclusive_ticks.saturating_add(inclusive);
        stat.exclusive_ticks = stat.exclusive_ticks.saturating_add(exclusive);
        stat.first_qpc = if stat.first_qpc == 0 {
            active.start_qpc
        } else {
            stat.first_qpc.min(active.start_qpc)
        };
        stat.last_qpc = stat.last_qpc.max(active.start_qpc);

        if inclusive > stat.max_inclusive_ticks {
            stat.max_inclusive_ticks = inclusive;
            stat.max_at_qpc = active.start_qpc;
        }
        stat.max_exclusive_ticks = stat.max_exclusive_ticks.max(exclusive);

        let inclusive_us = ticks_to_us(inclusive);
        if inclusive_us >= 1_000 {
            stat.over_1ms += 1;
        }
        if inclusive_us >= 5_000 {
            stat.over_5ms += 1;
        }
        if inclusive_us >= 16_670 {
            stat.over_16ms += 1;
        }

        if inclusive_us >= SPIKE_THRESHOLD_US {
            let mut path: Vec<CallsiteKey> = t.stack.iter().map(|x| x.key).collect();
            path.push(active.key);
            let depth = path.len() as u32;
            sparse_event = Some((
                SpikeEvent {
                    qpc: active.start_qpc,
                    frame_id: active.frame_id,
                    key: active.key,
                    inclusive_ticks: inclusive,
                    exclusive_ticks: exclusive,
                    depth,
                },
                HotPathEvent {
                    qpc: active.start_qpc,
                    frame_id: active.frame_id,
                    inclusive_ticks: inclusive,
                    exclusive_ticks: exclusive,
                    path,
                },
            ));
        }
    });

    if let Some((spike, hot_path)) = sparse_event {
        {
            let mut spikes = SPIKES.write();
            if spikes.len() < MAX_SPIKE_EVENTS {
                spikes.push(spike);
            } else {
                SPIKES_DROPPED.fetch_add(1, Ordering::Relaxed);
            }
        }
        {
            let mut paths = HOT_PATHS.write();
            if paths.len() < MAX_HOT_PATH_EVENTS {
                paths.push(hot_path);
            } else {
                HOT_PATHS_DROPPED.fetch_add(1, Ordering::Relaxed);
            }
        }
    }
}

fn register_virtual_target_name(hash: u64, name: CName) {
    let should_publish = TLS_STATE.with(|cell| {
        let mut t = cell.borrow_mut();
        t.virtual_name_seen.insert(hash)
    });

    if should_publish {
        TARGET_NAMES
            .write()
            .entry((1, hash))
            .or_insert_with(|| name.as_str().to_owned());
    }
}

fn register_observed_thread(t: &mut ThreadState, capture_id: u64) {
    if t.registered_capture_id == capture_id {
        return;
    }
    t.registered_capture_id = capture_id;
    OBSERVED_THREADS.write().insert(current_thread_id());
}

fn flush_current_thread_frame(frame_id: u64, start_qpc: u64, end_qpc: u64, partial: bool) {
    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);

    TLS_STATE.with(|cell| {
        let mut t = cell.borrow_mut();
        t.ensure_capture(capture_id);

        let funcs = FUNCTIONS.read();
        let mut owner_frame: HashMap<String, EntityAgg> = HashMap::new();
        let mut function_frame: HashMap<usize, EntityAgg> = HashMap::new();

        let mut total_calls = 0u64;
        let mut observed_inclusive_ticks = 0u64;
        let mut exclusive_ticks = 0u64;
        let mut largest_call_ticks = 0u64;
        let mut spike_count = 0u64;
        let unique_callsites = t.frame_stats.len() as u64;

        {
            let mut global = CALLSITES.write();

            for (key, local) in t.frame_stats.drain() {
                total_calls = total_calls.saturating_add(local.calls);
                observed_inclusive_ticks =
                    observed_inclusive_ticks.saturating_add(local.inclusive_ticks);
                exclusive_ticks = exclusive_ticks.saturating_add(local.exclusive_ticks);
                largest_call_ticks = largest_call_ticks.max(local.max_inclusive_ticks);
                spike_count = spike_count.saturating_add(local.over_1ms);

                let agg = global.entry(key).or_default();
                if agg.first_qpc == 0 {
                    agg.first_qpc = local.first_qpc;
                }
                agg.last_qpc = agg.last_qpc.max(local.last_qpc);

                if agg.last_active_qpc != 0 && local.first_qpc >= agg.last_active_qpc {
                    let gap_us = ticks_to_us(local.first_qpc - agg.last_active_qpc);
                    agg.cadence[cadence_bucket(gap_us)] += 1;
                }
                agg.last_active_qpc = local.first_qpc;

                agg.calls = agg.calls.saturating_add(local.calls);
                agg.inclusive_ticks = agg.inclusive_ticks.saturating_add(local.inclusive_ticks);
                agg.exclusive_ticks = agg.exclusive_ticks.saturating_add(local.exclusive_ticks);
                agg.over_1ms = agg.over_1ms.saturating_add(local.over_1ms);
                agg.over_5ms = agg.over_5ms.saturating_add(local.over_5ms);
                agg.over_16ms = agg.over_16ms.saturating_add(local.over_16ms);
                agg.active_frames += 1;
                if local.calls > 1 {
                    agg.repeated_frames += 1;
                }
                agg.max_calls_per_frame = agg.max_calls_per_frame.max(local.calls);
                if local.max_inclusive_ticks > agg.max_inclusive_ticks {
                    agg.max_inclusive_ticks = local.max_inclusive_ticks;
                    agg.max_at_qpc = local.max_at_qpc;
                }
                agg.max_exclusive_ticks = agg.max_exclusive_ticks.max(local.max_exclusive_ticks);

                if let Some(meta) = funcs.get(&key.caller) {
                    merge_entity_frame(
                        owner_frame.entry(meta.owner.clone()).or_default(),
                        &local,
                    );
                    merge_entity_frame(function_frame.entry(key.caller).or_default(), &local);
                }
            }
        }

        let unique_owners = owner_frame.len() as u64;
        let unique_functions = function_frame.len() as u64;

        {
            let mut owners = OWNER_AGG.write();
            let mut frame_owner_rows = FRAME_OWNERS.write();
            for (owner, frame_stat) in owner_frame {
                merge_entity_global(owners.entry(owner.clone()).or_default(), &frame_stat);

                if frame_stat.calls >= 100
                    || ticks_to_us(frame_stat.exclusive_ticks) >= 250
                    || ticks_to_us(frame_stat.max_inclusive_ticks) >= 1_000
                {
                    frame_owner_rows.push(FrameOwnerStat {
                        frame_id,
                        owner,
                        calls: frame_stat.calls,
                        inclusive_ticks: frame_stat.inclusive_ticks,
                        exclusive_ticks: frame_stat.exclusive_ticks,
                        max_call_ticks: frame_stat.max_inclusive_ticks,
                        spike_count: frame_stat.over_1ms,
                    });
                }
            }
        }

        {
            let mut functions = FUNCTION_AGG.write();
            for (ptr, frame_stat) in function_frame {
                merge_entity_global(functions.entry(ptr).or_default(), &frame_stat);
            }
        }

        FRAMES.write().push(FrameStat {
            frame_id,
            frame_start_qpc: start_qpc,
            frame_end_qpc: end_qpc.max(start_qpc),
            total_calls,
            unique_callsites,
            unique_owners,
            unique_functions,
            max_call_depth: t.frame_max_depth,
            observed_inclusive_ticks,
            exclusive_instrumented_ticks: exclusive_ticks,
            largest_call_ticks,
            spike_count,
            partial,
        });

        t.frame_max_depth = 0;
    });
}

fn merge_entity_frame(entity: &mut EntityAgg, local: &LocalStat) {
    entity.calls = entity.calls.saturating_add(local.calls);
    entity.inclusive_ticks = entity.inclusive_ticks.saturating_add(local.inclusive_ticks);
    entity.exclusive_ticks = entity.exclusive_ticks.saturating_add(local.exclusive_ticks);
    entity.max_calls_per_frame = entity.max_calls_per_frame.saturating_add(local.calls);
    entity.max_inclusive_ticks = entity.max_inclusive_ticks.max(local.max_inclusive_ticks);
    entity.over_1ms = entity.over_1ms.saturating_add(local.over_1ms);
    entity.over_5ms = entity.over_5ms.saturating_add(local.over_5ms);
    entity.over_16ms = entity.over_16ms.saturating_add(local.over_16ms);
}

fn merge_entity_global(global: &mut EntityAgg, frame: &EntityAgg) {
    global.calls = global.calls.saturating_add(frame.calls);
    global.inclusive_ticks = global.inclusive_ticks.saturating_add(frame.inclusive_ticks);
    global.exclusive_ticks = global.exclusive_ticks.saturating_add(frame.exclusive_ticks);
    global.active_frames += 1;
    global.max_calls_per_frame = global.max_calls_per_frame.max(frame.max_calls_per_frame);
    global.max_inclusive_ticks = global.max_inclusive_ticks.max(frame.max_inclusive_ticks);
    global.over_1ms = global.over_1ms.saturating_add(frame.over_1ms);
    global.over_5ms = global.over_5ms.saturating_add(frame.over_5ms);
    global.over_16ms = global.over_16ms.saturating_add(frame.over_16ms);
}

fn start_control_thread() {
    if CONTROL_THREAD_RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }

    thread::spawn(|| {
        let mut was_down = false;
        loop {
            let down = hotkey_is_down();

            if down && !was_down && game_is_foreground() {
                if PROFILE_STATE.load(Ordering::Acquire) == STATE_RECORDING {
                    finish_capture();
                } else {
                    begin_capture();
                }
            }

            was_down = down;
            thread::sleep(Duration::from_millis(HOTKEY_POLL_MS));
        }
    });
}

fn begin_capture() {
    clear_measurement_state();

    SPIKES_DROPPED.store(0, Ordering::Relaxed);
    HOT_PATHS_DROPPED.store(0, Ordering::Relaxed);
    LAST_DUMP_OK.store(false, Ordering::Relaxed);
    LAST_FINAL_FLUSH_OK.store(false, Ordering::Relaxed);
    FINAL_FLUSH_REQUESTED.store(false, Ordering::Relaxed);
    FINAL_FLUSH_DONE.store(false, Ordering::Relaxed);

    let capture_id = CAPTURE_ID.fetch_add(1, Ordering::Relaxed) + 1;
    let start_qpc = qpc_now();

    CAPTURE_START_UNIX_MS.store(unix_ms_now(), Ordering::Relaxed);
    CAPTURE_STOP_UNIX_MS.store(0, Ordering::Relaxed);
    CAPTURE_START_QPC.store(start_qpc, Ordering::Release);
    CAPTURE_STOP_QPC.store(0, Ordering::Relaxed);
    CAPTURE_DURATION_US.store(0, Ordering::Relaxed);
    FRAME_ID.store(0, Ordering::Relaxed);
    FRAME_START_QPC.store(start_qpc, Ordering::Release);

    let _ = capture_id;
    PROFILE_STATE.store(STATE_RECORDING, Ordering::Release);
    signal_start();
}

fn finish_capture() {
    let stop_qpc = qpc_now();
    let start_qpc = CAPTURE_START_QPC.load(Ordering::Acquire);
    let duration_us = if stop_qpc >= start_qpc {
        ticks_to_us(stop_qpc - start_qpc)
    } else {
        0
    };

    CAPTURE_STOP_QPC.store(stop_qpc, Ordering::Release);
    CAPTURE_STOP_UNIX_MS.store(unix_ms_now(), Ordering::Relaxed);
    CAPTURE_DURATION_US.store(duration_us, Ordering::Relaxed);

    // Close the measurement window before waiting, beeping or writing files.
    PROFILE_STATE.store(STATE_COMPLETE, Ordering::Release);
    FINAL_FLUSH_DONE.store(false, Ordering::Relaxed);
    FINAL_FLUSH_REQUESTED.store(true, Ordering::Release);

    signal_stop();

    let mut waited = 0u64;
    while waited < FINAL_FLUSH_WAIT_MS && !FINAL_FLUSH_DONE.load(Ordering::Acquire) {
        thread::sleep(Duration::from_millis(2));
        waited += 2;
    }

    let final_flush_ok = FINAL_FLUSH_DONE.load(Ordering::Acquire);
    if !final_flush_ok {
        FINAL_FLUSH_REQUESTED.store(false, Ordering::Release);
    }
    LAST_FINAL_FLUSH_OK.store(final_flush_ok, Ordering::Release);

    snapshot_last_counts();
    let dump_ok = dump_results().is_ok();
    LAST_DUMP_OK.store(dump_ok, Ordering::Release);
    let _ = write_status_file();

    if dump_ok {
        clear_measurement_state();
    }
}

fn snapshot_last_counts() {
    let callsites = CALLSITES.read();
    LAST_CALLSITE_ROWS.store(callsites.len() as u64, Ordering::Relaxed);
    LAST_OBSERVED_CALLS.store(
        callsites.values().map(|s| s.calls).sum::<u64>(),
        Ordering::Relaxed,
    );
    drop(callsites);

    LAST_SPIKE_ROWS.store(SPIKES.read().len() as u64, Ordering::Relaxed);
    LAST_HOT_PATH_ROWS.store(HOT_PATHS.read().len() as u64, Ordering::Relaxed);
    LAST_FRAME_ROWS.store(FRAMES.read().len() as u64, Ordering::Relaxed);
    LAST_OWNER_ROWS.store(OWNER_AGG.read().len() as u64, Ordering::Relaxed);
    LAST_FUNCTION_ROWS.store(FUNCTION_AGG.read().len() as u64, Ordering::Relaxed);
}

fn clear_measurement_state() {
    CALLSITES.write().clear();
    OWNER_AGG.write().clear();
    FUNCTION_AGG.write().clear();
    FRAMES.write().clear();
    FRAME_OWNERS.write().clear();
    SPIKES.write().clear();
    HOT_PATHS.write().clear();
    OBSERVED_THREADS.write().clear();
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

fn cadence_bucket(gap_us: u64) -> usize {
    match gap_us {
        0..=99 => 0,
        100..=999 => 1,
        1_000..=4_999 => 2,
        5_000..=11_999 => 3,
        12_000..=24_999 => 4,
        25_000..=74_999 => 5,
        75_000..=199_999 => 6,
        200_000..=749_999 => 7,
        750_000..=1_499_999 => 8,
        _ => 9,
    }
}

fn cadence_bucket_name(index: usize) -> &'static str {
    match index {
        0 => "<0.1ms",
        1 => "0.1-1ms",
        2 => "1-5ms",
        3 => "5-12ms",
        4 => "12-25ms",
        5 => "25-75ms",
        6 => "75-200ms",
        7 => "200-750ms",
        8 => "750-1500ms",
        _ => ">1500ms",
    }
}

fn dominant_cadence(stat: &AggStat) -> &'static str {
    let mut best_index = 0usize;
    let mut best_count = 0u64;
    for (i, count) in stat.cadence.iter().enumerate() {
        if *count > best_count {
            best_count = *count;
            best_index = i;
        }
    }
    if best_count == 0 {
        "n/a"
    } else {
        cadence_bucket_name(best_index)
    }
}

fn classify_work(stat: &AggStat, total_frames: u64, caller: &str, target: &str) -> (&'static str, &'static str) {
    let active_pct = pct(stat.active_frames, total_frames);
    let calls_per_active = if stat.active_frames > 0 {
        stat.calls as f64 / stat.active_frames as f64
    } else {
        0.0
    };
    let avg_exclusive_us = if stat.calls > 0 {
        ticks_to_us_f64(stat.exclusive_ticks) / stat.calls as f64
    } else {
        0.0
    };
    let max_ms = ticks_to_ms(stat.max_inclusive_ticks);
    let cadence = dominant_cadence(stat);

    if calls_per_active >= 200.0 || stat.max_calls_per_frame >= 1_000 {
        ("INNER_LOOP_EXPLOSION", "index/cache/algorithm")
    } else if active_pct >= 90.0 && calls_per_active <= 5.0 {
        ("FRAME_BOUND_POLLING", "event/cache/dirty-flag")
    } else if active_pct >= 90.0 {
        ("FRAME_BOUND_REPEAT_WORK", "cache/consolidate/conditional-schedule")
    } else if active_pct <= 20.0 && stat.max_calls_per_frame >= 100 {
        ("BURST_WORKER", "inspect-event-workload/cache")
    } else if matches!(cadence, "75-200ms" | "200-750ms" | "750-1500ms") {
        ("PERIODIC_POLLING", "event/lower-cadence/shared-state")
    } else if avg_exclusive_us >= 100.0 || max_ms >= 5.0 {
        ("HIGH_COST_CALL", "optimize-function/downstream")
    } else if caller.contains("wrapper$") || target.contains("wrapper$") {
        ("WRAPPER_CHAIN_CANDIDATE", "early-exit/consolidate/shared-cache")
    } else {
        ("MIXED", "inspect-callgraph")
    }
}

fn init_qpc() {
    #[cfg(windows)]
    {
        let mut freq = 0i64;
        let ok = unsafe { QueryPerformanceFrequency(&mut freq as *mut i64) };
        if ok != 0 && freq > 0 {
            QPC_FREQUENCY.store(freq as u64, Ordering::Release);
        } else {
            QPC_FREQUENCY.store(10_000_000, Ordering::Release);
        }
    }

    #[cfg(not(windows))]
    {
        QPC_FREQUENCY.store(1_000_000, Ordering::Release);
    }
}

#[cfg(windows)]
fn qpc_now() -> u64 {
    let mut value = 0i64;
    let ok = unsafe { QueryPerformanceCounter(&mut value as *mut i64) };
    if ok != 0 && value >= 0 {
        value as u64
    } else {
        0
    }
}

#[cfg(not(windows))]
fn qpc_now() -> u64 {
    unix_ms_now().saturating_mul(1_000)
}

fn ticks_to_us(ticks: u64) -> u64 {
    let freq = QPC_FREQUENCY.load(Ordering::Acquire).max(1);
    ((ticks as u128).saturating_mul(1_000_000u128) / freq as u128)
        .min(u64::MAX as u128) as u64
}

fn ticks_to_us_f64(ticks: u64) -> f64 {
    let freq = QPC_FREQUENCY.load(Ordering::Acquire).max(1) as f64;
    ticks as f64 * 1_000_000.0 / freq
}

fn ticks_to_ms(ticks: u64) -> f64 {
    ticks_to_us_f64(ticks) / 1_000.0
}

fn capture_ms_from_qpc(qpc: u64) -> f64 {
    let start = CAPTURE_START_QPC.load(Ordering::Acquire);
    if qpc < start {
        0.0
    } else {
        ticks_to_ms(qpc - start)
    }
}

fn unix_ms_now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis().min(u64::MAX as u128) as u64)
        .unwrap_or(0)
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

#[cfg(windows)]
fn current_thread_id() -> u32 {
    unsafe { GetCurrentThreadId() }
}

#[cfg(not(windows))]
fn current_thread_id() -> u32 {
    0
}

fn signal_start() {
    #[cfg(windows)]
    unsafe {
        Beep(1_100, 70);
    }
}

fn signal_stop() {
    #[cfg(windows)]
    unsafe {
        Beep(650, 60);
        thread::sleep(Duration::from_millis(35));
        Beep(650, 60);
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

fn clear_stale_results() {
    let Some(dir) = results_dir() else {
        return;
    };

    for name in [
        "RSP_Alpha_Capture.csv",
        "RSP_Alpha_Markers.csv",
        "RSP_Alpha_FunctionMap.csv",
        "RSP_Alpha_CallSites.csv",
        "RSP_Alpha_ByOwner.csv",
        "RSP_Alpha_ByFunction.csv",
        "RSP_Alpha_SharedTargets.csv",
        "RSP_Alpha_Edges.csv",
        "RSP_Alpha_Cadence.csv",
        "RSP_Alpha_WrapperChains.csv",
        "RSP_Alpha_Frames.csv",
        "RSP_Alpha_FrameOwners.csv",
        "RSP_Alpha_Spikes.csv",
        "RSP_Alpha_HotPaths.csv",
        "RSP_Alpha_WorkMap.csv",
    ] {
        let _ = fs::remove_file(dir.join(name));
    }
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

    let status = format!(
        "RSP alpha 0.3 runtime mapping profiler\n\
         Bind/source mapping hook: {}\n\
         InvokeStatic hook: {}\n\
         InvokeVirtual hook: {}\n\
         Running-frame listener: {}\n\
         State: {}\n\
         Hotkey: F11 shared with CapFrameX\n\
         Audio signal: START=1 high beep / STOP=2 low beeps\n\
         Startup/load profiling: OFF by default\n\
         Per-call global aggregation locks: REMOVED\n\
         Per-call timing: QPC start/end retained\n\
         Nested instrumented call stack: ENABLED\n\
         Exact game-frame tagging: ENABLED\n\
         Capture ID: {}\n\
         Capture duration ms: {:.3}\n\
         QPC frequency: {}\n\
         Mapped mod functions: {}\n\
         Observed Redscript hook threads in last live capture: {}\n\
         Last completed callsite rows: {}\n\
         Last completed observed calls: {}\n\
         Last completed owner rows: {}\n\
         Last completed function rows: {}\n\
         Last completed frame rows: {}\n\
         Last completed spike rows: {}\n\
         Last completed hot-path rows: {}\n\
         Dropped spike rows: {}\n\
         Dropped hot-path rows: {}\n\
         Final partial-frame flush: {}\n\
         Last CSV dump: {}\n",
        ok(BIND_HOOK_OK.load(Ordering::Acquire)),
        ok(STATIC_HOOK_OK.load(Ordering::Acquire)),
        ok(VIRTUAL_HOOK_OK.load(Ordering::Acquire)),
        ok(FRAME_LISTENER_OK.load(Ordering::Acquire)),
        state_name(PROFILE_STATE.load(Ordering::Acquire)),
        CAPTURE_ID.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        QPC_FREQUENCY.load(Ordering::Acquire),
        mapped_mod_function_count(),
        OBSERVED_THREADS.read().len(),
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_OWNER_ROWS.load(Ordering::Relaxed),
        LAST_FUNCTION_ROWS.load(Ordering::Relaxed),
        LAST_FRAME_ROWS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        LAST_HOT_PATH_ROWS.load(Ordering::Relaxed),
        SPIKES_DROPPED.load(Ordering::Relaxed),
        HOT_PATHS_DROPPED.load(Ordering::Relaxed),
        ok(LAST_FINAL_FLUSH_OK.load(Ordering::Acquire)),
        if LAST_DUMP_OK.load(Ordering::Acquire) { "OK" } else { "NOT YET / FAILED" },
    );

    fs::write(dir.join("RSP_Alpha_Status.txt"), status)
}

fn ok(v: bool) -> &'static str {
    if v { "OK" } else { "FAILED" }
}

fn dump_results() -> std::io::Result<()> {
    let Some(dir) = results_dir() else {
        return Ok(());
    };
    fs::create_dir_all(&dir)?;

    dump_capture_csv(&dir.join("RSP_Alpha_Capture.csv"))?;
    dump_markers_csv(&dir.join("RSP_Alpha_Markers.csv"))?;
    dump_function_map_csv(&dir.join("RSP_Alpha_FunctionMap.csv"))?;
    dump_callsites_csv(&dir.join("RSP_Alpha_CallSites.csv"))?;
    dump_by_owner_csv(&dir.join("RSP_Alpha_ByOwner.csv"))?;
    dump_by_function_csv(&dir.join("RSP_Alpha_ByFunction.csv"))?;
    dump_shared_targets_csv(&dir.join("RSP_Alpha_SharedTargets.csv"))?;
    dump_edges_csv(&dir.join("RSP_Alpha_Edges.csv"))?;
    dump_cadence_csv(&dir.join("RSP_Alpha_Cadence.csv"))?;
    dump_frames_csv(&dir.join("RSP_Alpha_Frames.csv"))?;
    dump_frame_owners_csv(&dir.join("RSP_Alpha_FrameOwners.csv"))?;
    dump_spikes_csv(&dir.join("RSP_Alpha_Spikes.csv"))?;
    dump_hot_paths_csv(&dir.join("RSP_Alpha_HotPaths.csv"))?;
    dump_wrapper_chains_csv(&dir.join("RSP_Alpha_WrapperChains.csv"))?;
    dump_work_map_csv(&dir.join("RSP_Alpha_WorkMap.csv"))?;
    Ok(())
}

fn dump_capture_csv(path: &Path) -> std::io::Result<()> {
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,version,state,start_unix_ms,stop_unix_ms,start_qpc,stop_qpc,qpc_frequency,duration_ms,hotkey,hotkey_poll_ms,mapped_mod_functions,observed_threads,frame_rows,callsite_rows,observed_calls,owner_rows,function_rows,spike_rows,hot_path_rows,dropped_spikes,dropped_hot_paths,final_flush_ok")?;
    writeln!(
        w,
        "{},0.3.0,{},{},{},{},{},{},{:.3},F11,{},{},{},{},{},{},{},{},{},{},{},{},{}",
        CAPTURE_ID.load(Ordering::Relaxed),
        state_name(PROFILE_STATE.load(Ordering::Acquire)),
        CAPTURE_START_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_START_QPC.load(Ordering::Relaxed),
        CAPTURE_STOP_QPC.load(Ordering::Relaxed),
        QPC_FREQUENCY.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        HOTKEY_POLL_MS,
        mapped_mod_function_count(),
        OBSERVED_THREADS.read().len(),
        LAST_FRAME_ROWS.load(Ordering::Relaxed),
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_OWNER_ROWS.load(Ordering::Relaxed),
        LAST_FUNCTION_ROWS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        LAST_HOT_PATH_ROWS.load(Ordering::Relaxed),
        SPIKES_DROPPED.load(Ordering::Relaxed),
        HOT_PATHS_DROPPED.load(Ordering::Relaxed),
        LAST_FINAL_FLUSH_OK.load(Ordering::Acquire),
    )?;
    w.flush()
}

fn dump_markers_csv(path: &Path) -> std::io::Result<()> {
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,event,qpc,capture_ms,unix_ms")?;
    let id = CAPTURE_ID.load(Ordering::Relaxed);
    writeln!(
        w,
        "{},START,{},0.000,{}",
        id,
        CAPTURE_START_QPC.load(Ordering::Relaxed),
        CAPTURE_START_UNIX_MS.load(Ordering::Relaxed)
    )?;
    writeln!(
        w,
        "{},STOP,{},{:.3},{}",
        id,
        CAPTURE_STOP_QPC.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed)
    )?;
    w.flush()
}

fn dump_function_map_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let mut rows: Vec<_> = funcs.iter().filter(|(_, m)| m.is_mod_source).collect();
    rows.sort_by(|a, b| {
        a.1.owner
            .cmp(&b.1.owner)
            .then(a.1.source_path.cmp(&b.1.source_path))
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

fn dump_callsites_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let total_frames = FRAMES.read().len() as u64;

    let mut rows: Vec<_> = stats.iter().collect();
    rows.sort_by(|a, b| b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,source_path,source_function,source_line,call_kind,target,target_resolution,calls,calls_per_sec,observed_inclusive_ms,exclusive_instrumented_ms,avg_inclusive_us,avg_exclusive_us,max_inclusive_ms,max_exclusive_ms,max_at_capture_ms,active_frames,active_frame_pct,repeated_frames,max_calls_per_frame,calls_per_active_frame,dominant_cadence,over_1ms,over_5ms,over_16_67ms")?;

    let duration_s = capture_duration_s();
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},{},{},{},{},{},{},{},{},{:.3},{:.6},{:.6},{:.3},{:.3},{:.6},{:.6},{:.3},{},{:.3},{},{},{:.3},{},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&meta.owner),
            csv(&meta.source_path),
            csv(&meta.function),
            key.line,
            call_kind(key.kind),
            csv(&target.display),
            target.resolution,
            stat.calls,
            rate(stat.calls, duration_s),
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.exclusive_ticks),
            avg_us(stat.inclusive_ticks, stat.calls),
            avg_us(stat.exclusive_ticks, stat.calls),
            ticks_to_ms(stat.max_inclusive_ticks),
            ticks_to_ms(stat.max_exclusive_ticks),
            capture_ms_from_qpc(stat.max_at_qpc),
            stat.active_frames,
            pct(stat.active_frames, total_frames),
            stat.repeated_frames,
            stat.max_calls_per_frame,
            if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 },
            dominant_cadence(stat),
            stat.over_1ms,
            stat.over_5ms,
            stat.over_16ms,
        )?;
    }
    w.flush()
}

fn dump_by_owner_csv(path: &Path) -> std::io::Result<()> {
    let owners = OWNER_AGG.read();
    let total_frames = FRAMES.read().len() as u64;
    let duration_s = capture_duration_s();
    let mut rows: Vec<_> = owners.iter().collect();
    rows.sort_by(|a, b| b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,calls,calls_per_sec,observed_inclusive_ms,exclusive_instrumented_ms,active_frames,active_frame_pct,calls_per_active_frame,max_calls_per_frame,max_call_ms,over_1ms,over_5ms,over_16_67ms")?;
    for (owner, stat) in rows {
        writeln!(
            w,
            "{},{},{},{:.3},{:.6},{:.6},{},{:.3},{:.3},{},{:.6},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(owner),
            stat.calls,
            rate(stat.calls, duration_s),
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.exclusive_ticks),
            stat.active_frames,
            pct(stat.active_frames, total_frames),
            if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 },
            stat.max_calls_per_frame,
            ticks_to_ms(stat.max_inclusive_ticks),
            stat.over_1ms,
            stat.over_5ms,
            stat.over_16ms,
        )?;
    }
    w.flush()
}

fn dump_by_function_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let functions = FUNCTION_AGG.read();
    let total_frames = FRAMES.read().len() as u64;
    let duration_s = capture_duration_s();
    let mut rows: Vec<_> = functions.iter().collect();
    rows.sort_by(|a, b| b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,source_path,source_line,source_function,outgoing_calls,calls_per_sec,observed_inclusive_ms,exclusive_instrumented_ms,active_frames,active_frame_pct,calls_per_active_frame,max_calls_per_frame,max_call_ms,over_1ms,over_5ms,over_16_67ms")?;
    for (ptr, stat) in rows {
        let Some(meta) = funcs.get(ptr) else { continue; };
        writeln!(
            w,
            "{},{},{},{},{},{},{:.3},{:.6},{:.6},{},{:.3},{:.3},{},{:.6},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&meta.owner),
            csv(&meta.source_path),
            meta.source_line,
            csv(&meta.function),
            stat.calls,
            rate(stat.calls, duration_s),
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.exclusive_ticks),
            stat.active_frames,
            pct(stat.active_frames, total_frames),
            if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 },
            stat.max_calls_per_frame,
            ticks_to_ms(stat.max_inclusive_ticks),
            stat.over_1ms,
            stat.over_5ms,
            stat.over_16ms,
        )?;
    }
    w.flush()
}

#[derive(Default)]
struct TargetSummary {
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    max_call_ticks: u64,
    owners: HashSet<String>,
    source_functions: HashSet<usize>,
    max_callsite_active_pct: f64,
}

fn dump_shared_targets_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let total_frames = FRAMES.read().len() as u64;
    let duration_s = capture_duration_s();
    let mut grouped: HashMap<(u8, u64), TargetSummary> = HashMap::new();

    for (key, stat) in stats.iter() {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let entry = grouped.entry((key.kind, key.target)).or_default();
        entry.calls = entry.calls.saturating_add(stat.calls);
        entry.inclusive_ticks = entry.inclusive_ticks.saturating_add(stat.inclusive_ticks);
        entry.exclusive_ticks = entry.exclusive_ticks.saturating_add(stat.exclusive_ticks);
        entry.max_call_ticks = entry.max_call_ticks.max(stat.max_inclusive_ticks);
        entry.owners.insert(meta.owner.clone());
        entry.source_functions.insert(key.caller);
        entry.max_callsite_active_pct = entry
            .max_callsite_active_pct
            .max(pct(stat.active_frames, total_frames));
    }

    let mut rows: Vec<_> = grouped.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,call_kind,target,target_resolution,total_calls,calls_per_sec,unique_calling_owners,unique_source_functions,observed_inclusive_ms,exclusive_instrumented_ms,max_call_ms,max_callsite_active_frame_pct")?;
    for ((kind, target_key), stat) in rows {
        let key = CallsiteKey { caller: 0, line: 0, kind: *kind, target: *target_key };
        let target = target_info(key, &funcs, &targets);
        writeln!(
            w,
            "{},{},{},{},{},{:.3},{},{},{:.6},{:.6},{:.6},{:.3}",
            CAPTURE_ID.load(Ordering::Relaxed),
            call_kind(*kind),
            csv(&target.display),
            target.resolution,
            stat.calls,
            rate(stat.calls, duration_s),
            stat.owners.len(),
            stat.source_functions.len(),
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.exclusive_ticks),
            ticks_to_ms(stat.max_call_ticks),
            stat.max_callsite_active_pct,
        )?;
    }
    w.flush()
}

fn dump_edges_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let total_frames = FRAMES.read().len() as u64;
    let mut rows: Vec<_> = stats.iter().collect();
    rows.sort_by(|a, b| b.1.exclusive_ticks.cmp(&a.1.exclusive_ticks));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,caller_owner,caller_function,caller_source,caller_line,call_kind,callee_owner,callee_function,target_resolution,calls,observed_inclusive_ms,exclusive_instrumented_ms,max_inclusive_ms,active_frames,active_frame_pct")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},{},{},{},{},{},{},{},{},{},{:.6},{:.6},{:.6},{},{:.3}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&meta.owner),
            csv(&meta.function),
            csv(&meta.source_path),
            key.line,
            call_kind(key.kind),
            csv(&target.owner),
            csv(&target.function),
            target.resolution,
            stat.calls,
            ticks_to_ms(stat.inclusive_ticks),
            ticks_to_ms(stat.exclusive_ticks),
            ticks_to_ms(stat.max_inclusive_ticks),
            stat.active_frames,
            pct(stat.active_frames, total_frames),
        )?;
    }
    w.flush()
}

fn dump_cadence_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let total_frames = FRAMES.read().len() as u64;
    let mut rows: Vec<_> = stats.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,source_function,source_line,target,calls,active_frames,active_frame_pct,repeated_frames,max_calls_per_frame,calls_per_active_frame,first_seen_ms,last_seen_ms,dominant_cadence,gap_lt_0_1ms,gap_0_1_1ms,gap_1_5ms,gap_5_12ms,gap_12_25ms,gap_25_75ms,gap_75_200ms,gap_200_750ms,gap_750_1500ms,gap_gt_1500ms")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},{},{},{},{},{},{},{:.3},{},{},{:.3},{:.3},{:.3},{},{},{},{},{},{},{},{},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&meta.owner),
            csv(&meta.function),
            key.line,
            csv(&target.display),
            stat.calls,
            stat.active_frames,
            pct(stat.active_frames, total_frames),
            stat.repeated_frames,
            stat.max_calls_per_frame,
            if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 },
            capture_ms_from_qpc(stat.first_qpc),
            capture_ms_from_qpc(stat.last_qpc),
            dominant_cadence(stat),
            stat.cadence[0], stat.cadence[1], stat.cadence[2], stat.cadence[3], stat.cadence[4],
            stat.cadence[5], stat.cadence[6], stat.cadence[7], stat.cadence[8], stat.cadence[9],
        )?;
    }
    w.flush()
}

fn dump_frames_csv(path: &Path) -> std::io::Result<()> {
    let frames = FRAMES.read();
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,frame_id,frame_start_ms,frame_end_ms,frame_duration_ms,total_calls,unique_callsites,unique_owners,unique_functions,max_call_depth,observed_inclusive_ms,exclusive_instrumented_ms,largest_call_ms,spike_count,partial")?;
    for f in frames.iter() {
        writeln!(
            w,
            "{},{},{:.3},{:.3},{:.3},{},{},{},{},{},{:.6},{:.6},{:.6},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            f.frame_id,
            capture_ms_from_qpc(f.frame_start_qpc),
            capture_ms_from_qpc(f.frame_end_qpc),
            ticks_to_ms(f.frame_end_qpc.saturating_sub(f.frame_start_qpc)),
            f.total_calls,
            f.unique_callsites,
            f.unique_owners,
            f.unique_functions,
            f.max_call_depth,
            ticks_to_ms(f.observed_inclusive_ticks),
            ticks_to_ms(f.exclusive_instrumented_ticks),
            ticks_to_ms(f.largest_call_ticks),
            f.spike_count,
            f.partial,
        )?;
    }
    w.flush()
}

fn dump_frame_owners_csv(path: &Path) -> std::io::Result<()> {
    let rows = FRAME_OWNERS.read();
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,frame_id,owner,calls,observed_inclusive_ms,exclusive_instrumented_ms,max_call_ms,spike_count")?;
    for r in rows.iter() {
        writeln!(
            w,
            "{},{},{},{},{:.6},{:.6},{:.6},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            r.frame_id,
            csv(&r.owner),
            r.calls,
            ticks_to_ms(r.inclusive_ticks),
            ticks_to_ms(r.exclusive_ticks),
            ticks_to_ms(r.max_call_ticks),
            r.spike_count,
        )?;
    }
    w.flush()
}

fn dump_spikes_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let mut spikes = SPIKES.read().clone();
    spikes.sort_by_key(|e| e.qpc);

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,capture_ms,frame_id,owner,source_path,source_function,source_line,call_kind,target,duration_ms,exclusive_instrumented_ms,depth,threshold")?;
    for e in spikes {
        let Some(meta) = funcs.get(&e.key.caller) else { continue; };
        let target = target_info(e.key, &funcs, &targets);
        let duration_ms = ticks_to_ms(e.inclusive_ticks);
        let threshold = if duration_ms >= 16.67 { ">=16.67ms" } else if duration_ms >= 5.0 { ">=5ms" } else { ">=1ms" };
        writeln!(
            w,
            "{},{:.3},{},{},{},{},{},{},{},{:.6},{:.6},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            capture_ms_from_qpc(e.qpc),
            e.frame_id,
            csv(&meta.owner),
            csv(&meta.source_path),
            csv(&meta.function),
            e.key.line,
            call_kind(e.key.kind),
            csv(&target.display),
            duration_ms,
            ticks_to_ms(e.exclusive_ticks),
            e.depth,
            threshold,
        )?;
    }
    w.flush()
}

fn dump_hot_paths_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let mut paths = HOT_PATHS.read().clone();
    paths.sort_by(|a, b| b.inclusive_ticks.cmp(&a.inclusive_ticks));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,capture_ms,frame_id,depth,duration_ms,exclusive_leaf_ms,path")?;
    for e in paths {
        writeln!(
            w,
            "{},{:.3},{},{},{:.6},{:.6},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            capture_ms_from_qpc(e.qpc),
            e.frame_id,
            e.path.len(),
            ticks_to_ms(e.inclusive_ticks),
            ticks_to_ms(e.exclusive_ticks),
            csv(&format_path(&e.path, &funcs, &targets)),
        )?;
    }
    w.flush()
}

fn dump_wrapper_chains_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let paths = HOT_PATHS.read();
    let mut grouped: HashMap<String, (u64, u64, u64, usize)> = HashMap::new();

    for e in paths.iter() {
        let formatted = format_path(&e.path, &funcs, &targets);
        let wrapper_count = formatted.matches("wrapper$").count();
        if wrapper_count < 2 {
            continue;
        }
        let entry = grouped.entry(formatted).or_insert((0, 0, 0, wrapper_count));
        entry.0 += 1;
        entry.1 = entry.1.saturating_add(e.inclusive_ticks);
        entry.2 = entry.2.max(e.inclusive_ticks);
    }

    let mut rows: Vec<_> = grouped.iter().collect();
    rows.sort_by(|a, b| b.1.1.cmp(&a.1.1));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,wrapper_count,observations,total_observed_ms,max_observed_ms,path")?;
    for (path_text, (count, total, max, wrappers)) in rows {
        writeln!(
            w,
            "{},{},{},{:.6},{:.6},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            wrappers,
            count,
            ticks_to_ms(*total),
            ticks_to_ms(*max),
            csv(path_text),
        )?;
    }
    w.flush()
}

fn dump_work_map_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let total_frames = FRAMES.read().len() as u64;
    let duration_s = capture_duration_s();
    let mut rows: Vec<_> = stats.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,source_function,source_line,target,calls,calls_per_sec,active_frame_pct,calls_per_active_frame,max_calls_per_frame,exclusive_instrumented_ms,max_call_ms,dominant_cadence,classification,likely_treatment")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        let (class, treatment) = classify_work(stat, total_frames, &meta.function, &target.display);
        writeln!(
            w,
            "{},{},{},{},{},{},{:.3},{:.3},{:.3},{},{:.6},{:.6},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&meta.owner),
            csv(&meta.function),
            key.line,
            csv(&target.display),
            stat.calls,
            rate(stat.calls, duration_s),
            pct(stat.active_frames, total_frames),
            if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 },
            stat.max_calls_per_frame,
            ticks_to_ms(stat.exclusive_ticks),
            ticks_to_ms(stat.max_inclusive_ticks),
            dominant_cadence(stat),
            class,
            treatment,
        )?;
    }
    w.flush()
}

struct TargetInfo {
    display: String,
    owner: String,
    function: String,
    resolution: &'static str,
}

fn target_info(
    key: CallsiteKey,
    funcs: &HashMap<usize, FunctionMeta>,
    targets: &HashMap<(u8, u64), String>,
) -> TargetInfo {
    if key.kind == 0 {
        let ptr = key.target as usize;
        if let Some(meta) = funcs.get(&ptr) {
            return TargetInfo {
                display: meta.function.clone(),
                owner: meta.owner.clone(),
                function: meta.function.clone(),
                resolution: "DIRECT",
            };
        }
        return TargetInfo {
            display: format!("<static@0x{:016X}>", key.target),
            owner: "<unresolved>".to_owned(),
            function: format!("0x{:016X}", key.target),
            resolution: "UNRESOLVED_STATIC",
        };
    }

    let name = targets
        .get(&(1, key.target))
        .cloned()
        .unwrap_or_else(|| format!("0x{:016X}", key.target));
    TargetInfo {
        display: name.clone(),
        owner: "<unresolved-virtual>".to_owned(),
        function: name,
        resolution: "UNRESOLVED_VIRTUAL",
    }
}

fn format_path(
    path: &[CallsiteKey],
    funcs: &HashMap<usize, FunctionMeta>,
    targets: &HashMap<(u8, u64), String>,
) -> String {
    let mut parts = Vec::with_capacity(path.len());
    for key in path {
        let caller = funcs
            .get(&key.caller)
            .map(|m| format!("{}::{}", m.owner, m.function))
            .unwrap_or_else(|| format!("0x{:016X}", key.caller));
        let target = target_info(*key, funcs, targets);
        parts.push(format!("{}@{} -> {}", caller, key.line, target.display));
    }
    parts.join(" | ")
}

fn call_kind(kind: u8) -> &'static str {
    if kind == 0 { "static" } else { "virtual" }
}

fn capture_duration_s() -> f64 {
    (CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000_000.0).max(0.000_001)
}

fn rate(count: u64, duration_s: f64) -> f64 {
    count as f64 / duration_s.max(0.000_001)
}

fn avg_us(ticks: u64, calls: u64) -> f64 {
    if calls == 0 { 0.0 } else { ticks_to_us_f64(ticks) / calls as f64 }
}

fn pct(part: u64, whole: u64) -> f64 {
    if whole == 0 { 0.0 } else { part as f64 * 100.0 / whole as f64 }
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
