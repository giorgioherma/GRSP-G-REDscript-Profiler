use std::{
    cell::{Cell, UnsafeCell},
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
const STOP_DRAIN_WAIT_MS: u64 = 10_000;

const STATE_PAUSED: u8 = 0;
const STATE_RECORDING: u8 = 1;
const STATE_STOPPING: u8 = 2;
const STATE_COMPLETE: u8 = 3;

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
    owner_hash: u64,
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
    frame_calls: Vec<(u64, u64)>,
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
    over_5ms: u64,
    over_16ms: u64,
}

#[derive(Debug, Clone)]
struct SpikeEvent {
    qpc: u64,
    frame_id: u64,
    thread_id: u32,
    key: CallsiteKey,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    depth: u32,
}

#[derive(Debug, Clone)]
struct HotPathEvent {
    qpc: u64,
    frame_id: u64,
    thread_id: u32,
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
    is_root: bool,
    descendant_calls: u64,
}

#[derive(Debug, Clone)]
struct CallerCacheEntry {
    is_mod: bool,
    owner_hash: u64,
}

#[derive(Debug, Default, Clone)]
struct RootStat {
    calls: u64,
    inclusive_ticks: u64,
    max_inclusive_ticks: u64,
    descendant_calls: u64,
    max_descendant_calls: u64,
    frame_calls: Vec<(u64, u64)>,
}

#[derive(Debug, Clone, Copy, Eq, PartialEq, Hash)]
struct CrossModKey {
    parent: CallsiteKey,
    child: CallsiteKey,
}

#[derive(Debug, Default, Clone)]
struct CrossModStat {
    calls: u64,
    frame_calls: Vec<(u64, u64)>,
}

#[derive(Debug, Clone)]
struct FrameBoundary {
    frame_id: u64,
    start_qpc: u64,
    end_qpc: u64,
    partial: bool,
}

#[derive(Default)]
struct ThreadState {
    capture_id: u64,
    thread_id: u32,
    caller_cache: HashMap<usize, CallerCacheEntry>,
    virtual_name_seen: HashSet<u64>,
    stack: Vec<ActiveCall>,
    current_frame_id: u64,
    frame_initialized: bool,
    frame_stats: HashMap<CallsiteKey, LocalStat>,
    frame_max_depth: u32,
    aggregate: HashMap<CallsiteKey, AggStat>,
    frames: Vec<FrameStat>,
    frame_owners: Vec<FrameOwnerStat>,
    roots: HashMap<CallsiteKey, RootStat>,
    cross_mod_edges: HashMap<CrossModKey, CrossModStat>,
    first_qpc: u64,
    last_qpc: u64,
    total_calls: u64,
}

impl ThreadState {
    fn reset_capture(&mut self, capture_id: u64) {
        self.capture_id = capture_id;
        self.stack.clear();
        self.current_frame_id = 0;
        self.frame_initialized = false;
        self.frame_stats.clear();
        self.frame_max_depth = 0;
        self.aggregate.clear();
        self.frames.clear();
        self.frame_owners.clear();
        self.roots.clear();
        self.cross_mod_edges.clear();
        self.first_qpc = 0;
        self.last_qpc = 0;
        self.total_calls = 0;
    }

    fn ensure_capture(&mut self, capture_id: u64) {
        if self.capture_id != capture_id {
            self.reset_capture(capture_id);
        }
    }
}

struct ThreadShard {
    state: UnsafeCell<ThreadState>,
}

// SAFETY MODEL: only the owning hook thread mutates its ThreadState while
// PROFILE_STATE is RECORDING/STOPPING. The control thread reads/merges shards
// only after PROFILE_STATE has entered STOPPING and ACTIVE_ROOTS has reached 0.
// Shards are intentionally leaked until process exit so registry pointers stay valid.
unsafe impl Send for ThreadShard {}
unsafe impl Sync for ThreadShard {}

thread_local! {
    static TLS_SHARD: Cell<*mut ThreadShard> = const { Cell::new(std::ptr::null_mut()) };
}

static SHARDS: LazyLock<RwLock<Vec<usize>>> = LazyLock::new(|| RwLock::new(Vec::new()));
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
static ROOTS: LazyLock<RwLock<HashMap<CallsiteKey, RootStat>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static CROSS_MOD_EDGES: LazyLock<RwLock<HashMap<CrossModKey, CrossModStat>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));
static FRAME_BOUNDARIES: LazyLock<RwLock<Vec<FrameBoundary>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));
static FRAMES: LazyLock<RwLock<Vec<FrameStat>>> = LazyLock::new(|| RwLock::new(Vec::new()));
static FRAME_OWNERS: LazyLock<RwLock<Vec<FrameOwnerStat>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));
static SPIKES: LazyLock<RwLock<Vec<SpikeEvent>>> = LazyLock::new(|| RwLock::new(Vec::new()));
static HOT_PATHS: LazyLock<RwLock<Vec<HotPathEvent>>> =
    LazyLock::new(|| RwLock::new(Vec::new()));

static CONTROL_THREAD_RUNNING: AtomicBool = AtomicBool::new(false);
static BIND_HOOK_OK: AtomicBool = AtomicBool::new(false);
static STATIC_HOOK_OK: AtomicBool = AtomicBool::new(false);
static VIRTUAL_HOOK_OK: AtomicBool = AtomicBool::new(false);
static FRAME_LISTENER_OK: AtomicBool = AtomicBool::new(false);
static LAST_DUMP_OK: AtomicBool = AtomicBool::new(false);
static LAST_SHARD_MERGE_OK: AtomicBool = AtomicBool::new(false);
static ACTIVE_ROOTS: AtomicU64 = AtomicU64::new(0);
static FRAME_CALLBACKS: AtomicU64 = AtomicU64::new(0);
static MERGED_SHARDS: AtomicU64 = AtomicU64::new(0);
static STOP_DRAIN_US: AtomicU64 = AtomicU64::new(0);
static QUIESCENT_QPC: AtomicU64 = AtomicU64::new(0);

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
    const VERSION: SemVer = SemVer::new(0, 3, 2);

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
            "[RSP alpha 0.3.2] bind-function hook: {}",
            if bind_ok { "OK" } else { "FAILED" }
        ));

        env.add_listener(
            StateType::Initialization,
            StateListener::default().with_on_exit(on_app_init),
        );

        // red4ext-rs rev c44146c models GameState callbacks as void, while the
        // current RED4ext runtime uses the modern bool-returning callback ABI.
        // Keep the proven red4ext-rs revision for the VM hook layouts, but pass a
        // bool-returning Running::OnUpdate callback through the pointer slot.
        let frame_cb: unsafe extern "C" fn(&GameApp) = unsafe {
            mem::transmute(
                on_running_update_compat as unsafe extern "C" fn(&GameApp) -> bool
            )
        };
        let frame_ok = env.add_listener(
            StateType::Running,
            StateListener::default().with_on_update(frame_cb),
        );
        FRAME_LISTENER_OK.store(frame_ok, Ordering::Release);

        env.info(format!(
            "[RSP alpha 0.3.2] running-frame listener: {}",
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
        "[RSP alpha 0.3.2] InvokeStatic hook: {} / InvokeVirtual hook: {}",
        if static_ok { "OK" } else { "FAILED" },
        if virtual_ok { "OK" } else { "FAILED" }
    ));

    PROFILE_STATE.store(STATE_PAUSED, Ordering::Release);
    clear_stale_results();
    let _ = write_status_file();
    start_control_thread();
}

unsafe extern "C" fn on_running_update_compat(_app: &GameApp) -> bool {
    if PROFILE_STATE.load(Ordering::Acquire) == STATE_RECORDING {
        let end_qpc = qpc_now();
        let frame_id = FRAME_ID.fetch_add(1, Ordering::AcqRel);
        let start_qpc = FRAME_START_QPC.swap(end_qpc, Ordering::AcqRel);
        FRAME_BOUNDARIES.write().push(FrameBoundary {
            frame_id,
            start_qpc,
            end_qpc: end_qpc.max(start_qpc),
            // Capture begins asynchronously from the game-frame callback, so
            // the first observed boundary represents only the tail of that
            // game frame. Later complete boundaries are not partial.
            partial: frame_id == 0,
        });
        FRAME_CALLBACKS.fetch_add(1, Ordering::Relaxed);
    }

    // Current RED4ext semantics: false keeps OnUpdate active. Running ignores
    // completion anyway, but returning false is the conservative choice.
    false
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
    let owner_hash = stable_hash64(&owner);
    let is_mod_source = is_mod_source(&source_path);

    FUNCTIONS.write().insert(
        info.func as usize,
        FunctionMeta {
            function,
            source_path,
            source_line: info.source_line,
            owner,
            owner_hash,
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

    if !enter_profiled_call(key, None) {
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

    if !enter_profiled_call(key, Some(instr.name)) {
        unsafe { cb(i, f, a3, a4) };
        return;
    }

    unsafe { cb(i, f, a3, a4) };
    exit_profiled_call(key, qpc_now());
}

fn with_thread_state<R>(f: impl FnOnce(&mut ThreadState) -> R) -> R {
    TLS_SHARD.with(|cell| {
        let mut ptr = cell.get();
        if ptr.is_null() {
            let mut initial = ThreadState::default();
            initial.thread_id = current_thread_id();
            let shard = Box::new(ThreadShard {
                state: UnsafeCell::new(initial),
            });
            ptr = Box::into_raw(shard);
            SHARDS.write().push(ptr as usize);
            cell.set(ptr);
        }

        // SAFETY: this function is called only by the owning hook thread while
        // recording. Cross-thread reads happen only after the STOPPING barrier.
        unsafe { f(&mut *(*ptr).state.get()) }
    })
}

fn caller_cache_entry(t: &mut ThreadState, caller: usize) -> CallerCacheEntry {
    if let Some(v) = t.caller_cache.get(&caller) {
        return v.clone();
    }

    let entry = FUNCTIONS
        .read()
        .get(&caller)
        .map(|m| CallerCacheEntry {
            is_mod: m.is_mod_source,
            owner_hash: m.owner_hash,
        })
        .unwrap_or(CallerCacheEntry {
            is_mod: false,
            owner_hash: 0,
        });

    t.caller_cache.insert(caller, entry.clone());
    entry
}

fn add_frame_call(rows: &mut Vec<(u64, u64)>, frame_id: u64, calls: u64) {
    if let Some(last) = rows.last_mut() {
        if last.0 == frame_id {
            last.1 = last.1.saturating_add(calls);
            return;
        }
    }
    rows.push((frame_id, calls));
}

fn roll_thread_frame(t: &mut ThreadState, frame_id: u64) {
    if !t.frame_initialized {
        t.current_frame_id = frame_id;
        t.frame_initialized = true;
        return;
    }

    if t.current_frame_id != frame_id {
        finalize_thread_frame(t, false);
        t.current_frame_id = frame_id;
        t.frame_initialized = true;
    }
}

fn finalize_thread_frame(t: &mut ThreadState, partial: bool) {
    if !t.frame_initialized || t.frame_stats.is_empty() {
        t.frame_max_depth = 0;
        return;
    }

    let frame_id = t.current_frame_id;
    let funcs = FUNCTIONS.read();
    let mut owner_frame: HashMap<String, EntityAgg> = HashMap::new();
    let mut function_ids: HashSet<usize> = HashSet::new();

    let mut total_calls = 0u64;
    let mut observed_inclusive_ticks = 0u64;
    let mut exclusive_ticks = 0u64;
    let mut largest_call_ticks = 0u64;
    let mut spike_count = 0u64;
    let unique_callsites = t.frame_stats.len() as u64;

    for (key, local) in t.frame_stats.drain() {
        total_calls = total_calls.saturating_add(local.calls);
        observed_inclusive_ticks = observed_inclusive_ticks.saturating_add(local.inclusive_ticks);
        exclusive_ticks = exclusive_ticks.saturating_add(local.exclusive_ticks);
        largest_call_ticks = largest_call_ticks.max(local.max_inclusive_ticks);
        spike_count = spike_count.saturating_add(local.over_1ms);

        let agg = t.aggregate.entry(key).or_default();
        if agg.first_qpc == 0 {
            agg.first_qpc = local.first_qpc;
        } else if local.first_qpc != 0 {
            agg.first_qpc = agg.first_qpc.min(local.first_qpc);
        }
        agg.last_qpc = agg.last_qpc.max(local.last_qpc);
        agg.calls = agg.calls.saturating_add(local.calls);
        agg.inclusive_ticks = agg.inclusive_ticks.saturating_add(local.inclusive_ticks);
        agg.exclusive_ticks = agg.exclusive_ticks.saturating_add(local.exclusive_ticks);
        agg.over_1ms = agg.over_1ms.saturating_add(local.over_1ms);
        agg.over_5ms = agg.over_5ms.saturating_add(local.over_5ms);
        agg.over_16ms = agg.over_16ms.saturating_add(local.over_16ms);
        if local.max_inclusive_ticks > agg.max_inclusive_ticks {
            agg.max_inclusive_ticks = local.max_inclusive_ticks;
            agg.max_at_qpc = local.max_at_qpc;
        }
        agg.max_exclusive_ticks = agg.max_exclusive_ticks.max(local.max_exclusive_ticks);
        add_frame_call(&mut agg.frame_calls, frame_id, local.calls);

        if let Some(meta) = funcs.get(&key.caller) {
            let e = owner_frame.entry(meta.owner.clone()).or_default();
            e.calls = e.calls.saturating_add(local.calls);
            e.inclusive_ticks = e.inclusive_ticks.saturating_add(local.inclusive_ticks);
            e.exclusive_ticks = e.exclusive_ticks.saturating_add(local.exclusive_ticks);
            e.max_calls_per_frame = e.max_calls_per_frame.saturating_add(local.calls);
            e.max_inclusive_ticks = e.max_inclusive_ticks.max(local.max_inclusive_ticks);
            e.over_1ms = e.over_1ms.saturating_add(local.over_1ms);
            e.over_5ms = e.over_5ms.saturating_add(local.over_5ms);
            e.over_16ms = e.over_16ms.saturating_add(local.over_16ms);
            function_ids.insert(key.caller);
        }
    }

    for (owner, stat) in owner_frame.iter() {
        t.frame_owners.push(FrameOwnerStat {
            frame_id,
            owner: owner.clone(),
            calls: stat.calls,
            inclusive_ticks: stat.inclusive_ticks,
            exclusive_ticks: stat.exclusive_ticks,
            max_call_ticks: stat.max_inclusive_ticks,
            spike_count: stat.over_1ms,
            over_5ms: stat.over_5ms,
            over_16ms: stat.over_16ms,
        });
    }

    t.frames.push(FrameStat {
        frame_id,
        frame_start_qpc: 0,
        frame_end_qpc: 0,
        total_calls,
        unique_callsites,
        unique_owners: owner_frame.len() as u64,
        unique_functions: function_ids.len() as u64,
        max_call_depth: t.frame_max_depth,
        observed_inclusive_ticks,
        exclusive_instrumented_ticks: exclusive_ticks,
        largest_call_ticks,
        spike_count,
        partial,
    });

    t.frame_max_depth = 0;
}

fn enter_profiled_call(key: CallsiteKey, virtual_name: Option<CName>) -> bool {
    if PROFILE_STATE.load(Ordering::Relaxed) != STATE_RECORDING {
        return false;
    }

    with_thread_state(|t| {
        let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
        t.ensure_capture(capture_id);

        let caller_info = caller_cache_entry(t, key.caller);
        if !caller_info.is_mod {
            return false;
        }

        // Virtual CName text is easiest to retain while the bytecode operand is
        // in hand. Publish only on first sight per thread and do it before QPC
        // timing starts. Static Function* names are resolved after STOP from the
        // merged callsite map, adding zero static-name work to the capture path.
        if key.kind == 1 {
            if let Some(name) = virtual_name {
                if t.virtual_name_seen.insert(key.target) {
                    TARGET_NAMES
                        .write()
                        .entry((1, key.target))
                        .or_insert_with(|| name.as_str().to_owned());
                }
            }
        }

        let is_root = t.stack.is_empty();
        if is_root {
            ACTIVE_ROOTS.fetch_add(1, Ordering::AcqRel);
            if PROFILE_STATE.load(Ordering::Acquire) != STATE_RECORDING {
                ACTIVE_ROOTS.fetch_sub(1, Ordering::AcqRel);
                return false;
            }

            let frame_id = FRAME_ID.load(Ordering::Relaxed);
            roll_thread_frame(t, frame_id);
        }

        let frame_id = if t.frame_initialized {
            t.current_frame_id
        } else {
            FRAME_ID.load(Ordering::Relaxed)
        };

        if !is_root {
            if let Some(root) = t.stack.first_mut() {
                root.descendant_calls = root.descendant_calls.saturating_add(1);
            }

            if let Some(parent_key) = t.stack.last().map(|p| p.key) {
                let parent_info = caller_cache_entry(t, parent_key.caller);
                if parent_info.owner_hash != 0
                    && caller_info.owner_hash != 0
                    && parent_info.owner_hash != caller_info.owner_hash
                {
                    let edge = t
                        .cross_mod_edges
                        .entry(CrossModKey {
                            parent: parent_key,
                            child: key,
                        })
                        .or_default();
                    edge.calls = edge.calls.saturating_add(1);
                    add_frame_call(&mut edge.frame_calls, frame_id, 1);
                }
            }
        }

        let start_qpc = qpc_now();
        t.stack.push(ActiveCall {
            key,
            start_qpc,
            child_ticks: 0,
            frame_id,
            is_root,
            descendant_calls: 0,
        });
        t.frame_max_depth = t.frame_max_depth.max(t.stack.len() as u32);
        true
    })
}

fn exit_profiled_call(expected_key: CallsiteKey, end_qpc: u64) {
    let mut sparse_event: Option<(SpikeEvent, HotPathEvent)> = None;
    let mut finished_root = false;

    with_thread_state(|t| {
        let Some(active) = t.stack.pop() else {
            return;
        };

        finished_root = active.is_root;

        if active.key != expected_key {
            finished_root = finished_root || t.stack.first().is_some_and(|x| x.is_root);
            t.stack.clear();
            return;
        }

        let inclusive = end_qpc.saturating_sub(active.start_qpc);
        let exclusive = inclusive.saturating_sub(active.child_ticks);

        if let Some(parent) = t.stack.last_mut() {
            parent.child_ticks = parent.child_ticks.saturating_add(inclusive);
        }

        let state = PROFILE_STATE.load(Ordering::Acquire);
        let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
        let stop_qpc = CAPTURE_STOP_QPC.load(Ordering::Acquire);
        let inside_window = t.capture_id == capture_id
            && (state == STATE_RECORDING || state == STATE_STOPPING)
            && (state == STATE_RECORDING || stop_qpc == 0 || end_qpc <= stop_qpc);

        if inside_window {
            let stat = t.frame_stats.entry(active.key).or_default();
            stat.calls = stat.calls.saturating_add(1);
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

            t.total_calls = t.total_calls.saturating_add(1);
            t.first_qpc = if t.first_qpc == 0 {
                active.start_qpc
            } else {
                t.first_qpc.min(active.start_qpc)
            };
            t.last_qpc = t.last_qpc.max(end_qpc);

            if active.is_root {
                let root = t.roots.entry(active.key).or_default();
                root.calls = root.calls.saturating_add(1);
                root.inclusive_ticks = root.inclusive_ticks.saturating_add(inclusive);
                root.max_inclusive_ticks = root.max_inclusive_ticks.max(inclusive);
                root.descendant_calls = root.descendant_calls.saturating_add(active.descendant_calls);
                root.max_descendant_calls = root.max_descendant_calls.max(active.descendant_calls);
                add_frame_call(&mut root.frame_calls, active.frame_id, 1);
            }

            if inclusive_us >= SPIKE_THRESHOLD_US {
                let mut path: Vec<CallsiteKey> = t.stack.iter().map(|x| x.key).collect();
                path.push(active.key);
                let depth = path.len() as u32;
                sparse_event = Some((
                    SpikeEvent {
                        qpc: active.start_qpc,
                        frame_id: active.frame_id,
                        thread_id: t.thread_id,
                        key: active.key,
                        inclusive_ticks: inclusive,
                        exclusive_ticks: exclusive,
                        depth,
                    },
                    HotPathEvent {
                        qpc: active.start_qpc,
                        frame_id: active.frame_id,
                        thread_id: t.thread_id,
                        inclusive_ticks: inclusive,
                        exclusive_ticks: exclusive,
                        path,
                    },
                ));
            }
        }
    });

    if finished_root {
        ACTIVE_ROOTS.fetch_sub(1, Ordering::AcqRel);
    }

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

fn resolve_static_target_names_post_capture() {
    // Resolve each unique InvokeStatic target only after the capture is closed
    // and shards are merged. This restores semantic target names with zero
    // static-name resolution work on the measured hot path.
    let funcs = FUNCTIONS.read();
    let callsites = CALLSITES.read();
    let existing = TARGET_NAMES.read();
    let mut unresolved: HashSet<u64> = HashSet::new();

    for key in callsites.keys() {
        if key.kind == 0
            && !funcs.contains_key(&(key.target as usize))
            && !existing.contains_key(&(0, key.target))
        {
            unresolved.insert(key.target);
        }
    }
    drop(existing);
    drop(callsites);

    if unresolved.is_empty() {
        return;
    }

    let mut names = TARGET_NAMES.write();
    for target in unresolved {
        let ptr = target as usize;
        let name = if ptr == 0 {
            "<null-static>".to_owned()
        } else {
            let func = unsafe { &*(ptr as *const Function) };
            function_name(func)
        };
        names.entry((0, target)).or_insert(name);
    }
}

fn merge_agg_stat(dst: &mut AggStat, src: &AggStat) {
    dst.calls = dst.calls.saturating_add(src.calls);
    dst.inclusive_ticks = dst.inclusive_ticks.saturating_add(src.inclusive_ticks);
    dst.exclusive_ticks = dst.exclusive_ticks.saturating_add(src.exclusive_ticks);
    dst.over_1ms = dst.over_1ms.saturating_add(src.over_1ms);
    dst.over_5ms = dst.over_5ms.saturating_add(src.over_5ms);
    dst.over_16ms = dst.over_16ms.saturating_add(src.over_16ms);
    if dst.first_qpc == 0 {
        dst.first_qpc = src.first_qpc;
    } else if src.first_qpc != 0 {
        dst.first_qpc = dst.first_qpc.min(src.first_qpc);
    }
    dst.last_qpc = dst.last_qpc.max(src.last_qpc);
    if src.max_inclusive_ticks > dst.max_inclusive_ticks {
        dst.max_inclusive_ticks = src.max_inclusive_ticks;
        dst.max_at_qpc = src.max_at_qpc;
    }
    dst.max_exclusive_ticks = dst.max_exclusive_ticks.max(src.max_exclusive_ticks);
    dst.frame_calls.extend_from_slice(&src.frame_calls);
}

fn merge_root_stat(dst: &mut RootStat, src: &RootStat) {
    dst.calls = dst.calls.saturating_add(src.calls);
    dst.inclusive_ticks = dst.inclusive_ticks.saturating_add(src.inclusive_ticks);
    dst.max_inclusive_ticks = dst.max_inclusive_ticks.max(src.max_inclusive_ticks);
    dst.descendant_calls = dst.descendant_calls.saturating_add(src.descendant_calls);
    dst.max_descendant_calls = dst.max_descendant_calls.max(src.max_descendant_calls);
    dst.frame_calls.extend_from_slice(&src.frame_calls);
}

fn normalize_frame_calls(rows: &mut Vec<(u64, u64)>) {
    rows.sort_by_key(|x| x.0);
    let mut write = 0usize;
    for read in 0..rows.len() {
        let current = rows[read];
        if write > 0 && rows[write - 1].0 == current.0 {
            let previous = rows[write - 1].1;
            rows[write - 1].1 = previous.saturating_add(current.1);
        } else {
            rows[write] = current;
            write += 1;
        }
    }
    rows.truncate(write);
}

fn boundary_start_map() -> HashMap<u64, u64> {
    FRAME_BOUNDARIES
        .read()
        .iter()
        .map(|b| (b.frame_id, b.start_qpc))
        .collect()
}

fn normalize_callsite_frames(stat: &mut AggStat, starts: &HashMap<u64, u64>) {
    normalize_frame_calls(&mut stat.frame_calls);
    stat.active_frames = stat.frame_calls.len() as u64;
    stat.repeated_frames = stat.frame_calls.iter().filter(|(_, c)| *c > 1).count() as u64;
    stat.max_calls_per_frame = stat.frame_calls.iter().map(|(_, c)| *c).max().unwrap_or(0);
    stat.cadence = [0; CADENCE_BUCKETS];
    stat.last_active_qpc = 0;

    let mut last_start = 0u64;
    for (frame_id, _) in stat.frame_calls.iter() {
        if let Some(start) = starts.get(frame_id).copied() {
            if last_start != 0 && start >= last_start {
                let gap_us = ticks_to_us(start - last_start);
                stat.cadence[cadence_bucket(gap_us)] += 1;
            }
            last_start = start;
        }
    }
    stat.last_active_qpc = last_start;
}

fn add_final_partial_boundary(stop_qpc: u64) {
    let frame_id = FRAME_ID.load(Ordering::Relaxed);
    let start_qpc = FRAME_START_QPC.load(Ordering::Acquire);
    if start_qpc == 0 || stop_qpc <= start_qpc {
        return;
    }

    let mut boundaries = FRAME_BOUNDARIES.write();
    if boundaries.last().is_some_and(|b| b.frame_id == frame_id) {
        return;
    }
    boundaries.push(FrameBoundary {
        frame_id,
        start_qpc,
        end_qpc: stop_qpc,
        partial: true,
    });
}

fn merge_all_shards() -> bool {
    if ACTIVE_ROOTS.load(Ordering::Acquire) != 0 {
        return false;
    }

    CALLSITES.write().clear();
    OWNER_AGG.write().clear();
    FUNCTION_AGG.write().clear();
    ROOTS.write().clear();
    CROSS_MOD_EDGES.write().clear();
    FRAMES.write().clear();
    FRAME_OWNERS.write().clear();

    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
    let shard_ptrs = SHARDS.read().clone();
    let mut merged_shards = 0u64;
    let mut frame_rows: HashMap<u64, FrameStat> = HashMap::new();
    let mut frame_owner_rows: HashMap<(u64, String), FrameOwnerStat> = HashMap::new();

    {
        let mut global_callsites = CALLSITES.write();
        let mut global_roots = ROOTS.write();
        let mut global_cross = CROSS_MOD_EDGES.write();

        for raw in shard_ptrs {
            let ptr = raw as *mut ThreadShard;
            if ptr.is_null() {
                continue;
            }

            // SAFETY: STOPPING barrier + ACTIVE_ROOTS==0 guarantees no hook
            // thread is mutating the capture data while we merge it.
            let t = unsafe { &mut *(*ptr).state.get() };
            if t.capture_id != capture_id {
                continue;
            }

            // A shard ending its local activity does not make the game frame
            // partial. Global partial-frame semantics come only from capture
            // boundaries recorded in FRAME_BOUNDARIES.
            finalize_thread_frame(t, false);
            merged_shards += 1;

            for (key, local) in t.aggregate.iter() {
                merge_agg_stat(global_callsites.entry(*key).or_default(), local);
            }
            for (key, local) in t.roots.iter() {
                merge_root_stat(global_roots.entry(*key).or_default(), local);
            }
            for (key, local) in t.cross_mod_edges.iter() {
                let e = global_cross.entry(*key).or_default();
                e.calls = e.calls.saturating_add(local.calls);
                e.frame_calls.extend_from_slice(&local.frame_calls);
            }

            for f in t.frames.iter() {
                let e = frame_rows.entry(f.frame_id).or_insert(FrameStat {
                    frame_id: f.frame_id,
                    frame_start_qpc: 0,
                    frame_end_qpc: 0,
                    total_calls: 0,
                    unique_callsites: 0,
                    unique_owners: 0,
                    unique_functions: 0,
                    max_call_depth: 0,
                    observed_inclusive_ticks: 0,
                    exclusive_instrumented_ticks: 0,
                    largest_call_ticks: 0,
                    spike_count: 0,
                    partial: false,
                });
                e.total_calls = e.total_calls.saturating_add(f.total_calls);
                e.unique_callsites = e.unique_callsites.saturating_add(f.unique_callsites);
                e.unique_owners = e.unique_owners.saturating_add(f.unique_owners);
                e.unique_functions = e.unique_functions.saturating_add(f.unique_functions);
                e.max_call_depth = e.max_call_depth.max(f.max_call_depth);
                e.observed_inclusive_ticks = e.observed_inclusive_ticks.saturating_add(f.observed_inclusive_ticks);
                e.exclusive_instrumented_ticks = e.exclusive_instrumented_ticks.saturating_add(f.exclusive_instrumented_ticks);
                e.largest_call_ticks = e.largest_call_ticks.max(f.largest_call_ticks);
                e.spike_count = e.spike_count.saturating_add(f.spike_count);
                // Ignore shard-local finalization as a source of global
                // partial-frame state. Capture boundaries are authoritative.
            }

            for r in t.frame_owners.iter() {
                let e = frame_owner_rows
                    .entry((r.frame_id, r.owner.clone()))
                    .or_insert(FrameOwnerStat {
                        frame_id: r.frame_id,
                        owner: r.owner.clone(),
                        calls: 0,
                        inclusive_ticks: 0,
                        exclusive_ticks: 0,
                        max_call_ticks: 0,
                        spike_count: 0,
                        over_5ms: 0,
                        over_16ms: 0,
                    });
                e.calls = e.calls.saturating_add(r.calls);
                e.inclusive_ticks = e.inclusive_ticks.saturating_add(r.inclusive_ticks);
                e.exclusive_ticks = e.exclusive_ticks.saturating_add(r.exclusive_ticks);
                e.max_call_ticks = e.max_call_ticks.max(r.max_call_ticks);
                e.spike_count = e.spike_count.saturating_add(r.spike_count);
                e.over_5ms = e.over_5ms.saturating_add(r.over_5ms);
                e.over_16ms = e.over_16ms.saturating_add(r.over_16ms);
            }
        }
    }

    MERGED_SHARDS.store(merged_shards, Ordering::Relaxed);

    let starts = boundary_start_map();
    {
        let mut callsites = CALLSITES.write();
        for stat in callsites.values_mut() {
            normalize_callsite_frames(stat, &starts);
        }
    }
    {
        let mut roots = ROOTS.write();
        for stat in roots.values_mut() {
            normalize_frame_calls(&mut stat.frame_calls);
        }
    }
    {
        let mut cross = CROSS_MOD_EDGES.write();
        for stat in cross.values_mut() {
            normalize_frame_calls(&mut stat.frame_calls);
        }
    }

    let boundaries = FRAME_BOUNDARIES.read().clone();
    for b in boundaries.iter() {
        let e = frame_rows.entry(b.frame_id).or_insert(FrameStat {
            frame_id: b.frame_id,
            frame_start_qpc: b.start_qpc,
            frame_end_qpc: b.end_qpc,
            total_calls: 0,
            unique_callsites: 0,
            unique_owners: 0,
            unique_functions: 0,
            max_call_depth: 0,
            observed_inclusive_ticks: 0,
            exclusive_instrumented_ticks: 0,
            largest_call_ticks: 0,
            spike_count: 0,
            partial: b.partial,
        });
        e.frame_start_qpc = b.start_qpc;
        e.frame_end_qpc = b.end_qpc;
        e.partial |= b.partial;
    }

    let frame_unique_owner_counts: HashMap<u64, u64> = frame_owner_rows.keys().fold(
        HashMap::new(),
        |mut acc, (frame_id, _owner)| {
            *acc.entry(*frame_id).or_default() += 1;
            acc
        },
    );

    // Rebuild owner aggregates from exact owner+frame merges so an owner active
    // on multiple Redscript threads in one game frame counts as one active frame.
    {
        let mut owners = OWNER_AGG.write();
        let mut frame_owner_out = FRAME_OWNERS.write();
        let mut owner_frame_counts: HashMap<String, u64> = HashMap::new();

        for ((_frame_id, owner), r) in frame_owner_rows.into_iter() {
            let e = owners.entry(owner.clone()).or_default();
            e.calls = e.calls.saturating_add(r.calls);
            e.inclusive_ticks = e.inclusive_ticks.saturating_add(r.inclusive_ticks);
            e.exclusive_ticks = e.exclusive_ticks.saturating_add(r.exclusive_ticks);
            e.max_calls_per_frame = e.max_calls_per_frame.max(r.calls);
            e.max_inclusive_ticks = e.max_inclusive_ticks.max(r.max_call_ticks);
            e.over_1ms = e.over_1ms.saturating_add(r.spike_count);
            e.over_5ms = e.over_5ms.saturating_add(r.over_5ms);
            e.over_16ms = e.over_16ms.saturating_add(r.over_16ms);
            *owner_frame_counts.entry(owner.clone()).or_default() += 1;

            if r.calls >= 100
                || ticks_to_us(r.exclusive_ticks) >= 250
                || ticks_to_us(r.max_call_ticks) >= 1_000
            {
                frame_owner_out.push(r);
            }
        }

        for (owner, count) in owner_frame_counts {
            if let Some(e) = owners.get_mut(&owner) {
                e.active_frames = count;
            }
        }
    }

    // Rebuild function aggregates from global callsites and exact frame IDs.
    {
        let funcs = FUNCTIONS.read();
        let callsites = CALLSITES.read();
        let mut functions = FUNCTION_AGG.write();
        let mut function_frames: HashMap<usize, HashMap<u64, u64>> = HashMap::new();

        for (key, stat) in callsites.iter() {
            let e = functions.entry(key.caller).or_default();
            e.calls = e.calls.saturating_add(stat.calls);
            e.inclusive_ticks = e.inclusive_ticks.saturating_add(stat.inclusive_ticks);
            e.exclusive_ticks = e.exclusive_ticks.saturating_add(stat.exclusive_ticks);
            e.max_inclusive_ticks = e.max_inclusive_ticks.max(stat.max_inclusive_ticks);
            e.over_1ms = e.over_1ms.saturating_add(stat.over_1ms);
            e.over_5ms = e.over_5ms.saturating_add(stat.over_5ms);
            e.over_16ms = e.over_16ms.saturating_add(stat.over_16ms);
            let frames = function_frames.entry(key.caller).or_default();
            for (frame_id, calls) in stat.frame_calls.iter() {
                let slot = frames.entry(*frame_id).or_default();
                *slot = slot.saturating_add(*calls);
            }
        }
        drop(funcs);
        for (ptr, frames) in function_frames {
            if let Some(e) = functions.get_mut(&ptr) {
                e.active_frames = frames.len() as u64;
                e.max_calls_per_frame = frames.values().copied().max().unwrap_or(0);
            }
        }
    }

    // Improve frame uniqueness fields from global callsite frame membership.
    {
        let funcs = FUNCTIONS.read();
        let callsites = CALLSITES.read();
        let mut frame_callsites: HashMap<u64, u64> = HashMap::new();
        let mut frame_functions: HashMap<u64, HashSet<usize>> = HashMap::new();
        for (key, stat) in callsites.iter() {
            for (frame_id, _) in stat.frame_calls.iter() {
                *frame_callsites.entry(*frame_id).or_default() += 1;
                frame_functions.entry(*frame_id).or_default().insert(key.caller);
            }
        }
        drop(funcs);
        for (frame_id, f) in frame_rows.iter_mut() {
            f.unique_callsites = frame_callsites.get(frame_id).copied().unwrap_or(0);
            f.unique_functions = frame_functions.get(frame_id).map_or(0, |s| s.len() as u64);
        }
    }

    for (frame_id, f) in frame_rows.iter_mut() {
        f.unique_owners = frame_unique_owner_counts.get(frame_id).copied().unwrap_or(0);
    }

    let mut frames: Vec<_> = frame_rows.into_values().collect();
    frames.sort_by_key(|f| f.frame_id);
    *FRAMES.write() = frames;
    FRAME_OWNERS.write().sort_by(|a, b| a.frame_id.cmp(&b.frame_id).then(a.owner.cmp(&b.owner)));

    true
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
    LAST_SHARD_MERGE_OK.store(false, Ordering::Relaxed);
    ACTIVE_ROOTS.store(0, Ordering::Relaxed);
    FRAME_CALLBACKS.store(0, Ordering::Relaxed);
    MERGED_SHARDS.store(0, Ordering::Relaxed);
    STOP_DRAIN_US.store(0, Ordering::Relaxed);
    QUIESCENT_QPC.store(0, Ordering::Relaxed);

    let _capture_id = CAPTURE_ID.fetch_add(1, Ordering::Relaxed) + 1;
    let start_qpc = qpc_now();

    CAPTURE_START_UNIX_MS.store(unix_ms_now(), Ordering::Relaxed);
    CAPTURE_STOP_UNIX_MS.store(0, Ordering::Relaxed);
    CAPTURE_START_QPC.store(start_qpc, Ordering::Release);
    CAPTURE_STOP_QPC.store(0, Ordering::Relaxed);
    CAPTURE_DURATION_US.store(0, Ordering::Relaxed);
    FRAME_ID.store(0, Ordering::Relaxed);
    FRAME_START_QPC.store(start_qpc, Ordering::Release);

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

    // STOPPING blocks all new profiled roots immediately. Existing roots may
    // finish, pop their TLS stacks and publish calls that ended inside the
    // requested measurement window.
    PROFILE_STATE.store(STATE_STOPPING, Ordering::Release);
    signal_stop();

    let drain_start = qpc_now();
    let mut waited = 0u64;
    while waited < STOP_DRAIN_WAIT_MS && ACTIVE_ROOTS.load(Ordering::Acquire) != 0 {
        thread::sleep(Duration::from_millis(2));
        waited += 2;
    }
    STOP_DRAIN_US.store(ticks_to_us(qpc_now().saturating_sub(drain_start)), Ordering::Relaxed);

    let quiescent = ACTIVE_ROOTS.load(Ordering::Acquire) == 0;
    if quiescent {
        QUIESCENT_QPC.store(qpc_now(), Ordering::Release);
        add_final_partial_boundary(stop_qpc);
    }

    let merge_ok = quiescent && merge_all_shards();
    LAST_SHARD_MERGE_OK.store(merge_ok, Ordering::Release);
    if merge_ok {
        resolve_static_target_names_post_capture();
    }
    PROFILE_STATE.store(STATE_COMPLETE, Ordering::Release);

    snapshot_last_counts();
    let dump_ok = dump_results().is_ok();
    LAST_DUMP_OK.store(dump_ok, Ordering::Release);
    let _ = write_status_file();

    if dump_ok {
        // Global export maps can be released immediately. Per-thread shards are
        // generation-reset lazily on the next capture and remain valid pointers.
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
    ROOTS.write().clear();
    CROSS_MOD_EDGES.write().clear();
    FRAME_BOUNDARIES.write().clear();
    FRAMES.write().clear();
    FRAME_OWNERS.write().clear();
    SPIKES.write().clear();
    HOT_PATHS.write().clear();
}


fn stable_hash64(text: &str) -> u64 {
    const OFFSET: u64 = 0xcbf29ce484222325;
    const PRIME: u64 = 0x100000001b3;
    let mut hash = OFFSET;
    for byte in text.as_bytes() {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(PRIME);
    }
    hash
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
    {
        // Do not block STOPPING/quiescence accounting on synchronous Win32
        // Beep calls. The measurement window has already closed at STOP_QPC;
        // this worker is purely user feedback.
        thread::spawn(|| unsafe {
            Beep(650, 60);
            thread::sleep(Duration::from_millis(35));
            Beep(650, 60);
        });
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
        "RSP_Alpha_Threads.csv",
        "RSP_Alpha_Roots.csv",
        "RSP_Alpha_CrossModEdges.csv",
        "RSP_Alpha_TargetDomains.csv",
        "RSP_Alpha_OwnerDomains.csv",
        "RSP_Alpha_FrameworkSignals.csv",
    ] {
        let _ = fs::remove_file(dir.join(name));
    }
}

fn state_name(state: u8) -> &'static str {
    match state {
        STATE_RECORDING => "RECORDING",
        STATE_STOPPING => "STOPPING",
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


fn observed_thread_count() -> usize {
    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
    SHARDS
        .read()
        .iter()
        .filter(|raw| {
            let ptr = **raw as *mut ThreadShard;
            if ptr.is_null() {
                return false;
            }
            let t = unsafe { &*(*ptr).state.get() };
            t.capture_id == capture_id
        })
        .count()
}

fn frame_quality() -> &'static str {
    let duration_s = capture_duration_s();
    let callbacks = FRAME_CALLBACKS.load(Ordering::Relaxed) as f64;
    if duration_s < 0.25 {
        return "TOO_SHORT";
    }
    let hz = callbacks / duration_s;
    if hz >= 20.0 {
        "GOOD"
    } else if hz >= 5.0 {
        "LOW_RATE"
    } else {
        "FAILED_OR_STALLED"
    }
}

fn static_resolution_summary() -> (u64, u64, u64, u64) {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let callsites = CALLSITES.read();
    let mut unique_seen = HashSet::new();
    let mut named_unique = 0u64;
    let mut unresolved_unique = 0u64;
    let mut named_calls = 0u64;
    let mut unresolved_calls = 0u64;

    for (key, stat) in callsites.iter() {
        if key.kind != 0 {
            continue;
        }
        let named = funcs.contains_key(&(key.target as usize))
            || targets.contains_key(&(0, key.target));
        if named {
            named_calls = named_calls.saturating_add(stat.calls);
        } else {
            unresolved_calls = unresolved_calls.saturating_add(stat.calls);
        }
        if unique_seen.insert(key.target) {
            if named {
                named_unique += 1;
            } else {
                unresolved_unique += 1;
            }
        }
    }

    (named_unique, unresolved_unique, named_calls, unresolved_calls)
}

fn write_status_file() -> std::io::Result<()> {
    let Some(dir) = results_dir() else {
        return Ok(());
    };
    fs::create_dir_all(&dir)?;

    let (named_static_targets, unresolved_static_targets, named_static_calls, unresolved_static_calls) =
        static_resolution_summary();

    let status = format!(
        "RSP alpha 0.3.2 framework-mapping profiler\n\
         Bind/source mapping hook: {}\n\
         InvokeStatic hook: {}\n\
         InvokeVirtual hook: {}\n\
         Running-frame listener: {}\n\
         Frame callback quality: {}\n\
         Frame callbacks: {}\n\
         State: {}\n\
         Hotkey: F11 shared with CapFrameX\n\
         Audio signal: START=1 high beep / STOP=2 low beeps\n\
         Startup/load profiling: OFF by default\n\
         Hot-path design: thread-owned shards, no per-call global aggregation lock\n\
         Per-call timing: QPC start/end retained\n\
         Nested instrumented call stack: ENABLED\n\
         Multithread shard merge: {}\n\
         Capture ID: {}\n\
         Capture duration ms: {:.3}\n\
         Stop drain ms: {:.3}\n\
         QPC frequency: {}\n\
         Mapped mod functions: {}\n\
         Observed Redscript hook threads: {}\n\
         Merged thread shards: {}\n\
         Named static targets: {}\n\
         Unresolved static targets: {}\n\
         Named static calls: {}\n\
         Unresolved static calls: {}\n\
         Last completed callsite rows: {}\n\
         Last completed observed calls: {}\n\
         Last completed owner rows: {}\n\
         Last completed function rows: {}\n\
         Last completed frame rows: {}\n\
         Last completed spike rows: {}\n\
         Last completed hot-path rows: {}\n\
         Dropped spike rows: {}\n\
         Dropped hot-path rows: {}\n\
         Last CSV dump: {}\n",
        ok(BIND_HOOK_OK.load(Ordering::Acquire)),
        ok(STATIC_HOOK_OK.load(Ordering::Acquire)),
        ok(VIRTUAL_HOOK_OK.load(Ordering::Acquire)),
        ok(FRAME_LISTENER_OK.load(Ordering::Acquire)),
        frame_quality(),
        FRAME_CALLBACKS.load(Ordering::Relaxed),
        state_name(PROFILE_STATE.load(Ordering::Acquire)),
        ok(LAST_SHARD_MERGE_OK.load(Ordering::Acquire)),
        CAPTURE_ID.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        STOP_DRAIN_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        QPC_FREQUENCY.load(Ordering::Acquire),
        mapped_mod_function_count(),
        observed_thread_count(),
        MERGED_SHARDS.load(Ordering::Relaxed),
        named_static_targets,
        unresolved_static_targets,
        named_static_calls,
        unresolved_static_calls,
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_OWNER_ROWS.load(Ordering::Relaxed),
        LAST_FUNCTION_ROWS.load(Ordering::Relaxed),
        LAST_FRAME_ROWS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        LAST_HOT_PATH_ROWS.load(Ordering::Relaxed),
        SPIKES_DROPPED.load(Ordering::Relaxed),
        HOT_PATHS_DROPPED.load(Ordering::Relaxed),
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

    // Alpha 0.3.2 framework-design outputs.
    dump_threads_csv(&dir.join("RSP_Alpha_Threads.csv"))?;
    dump_roots_csv(&dir.join("RSP_Alpha_Roots.csv"))?;
    dump_cross_mod_edges_csv(&dir.join("RSP_Alpha_CrossModEdges.csv"))?;
    dump_target_domains_csv(&dir.join("RSP_Alpha_TargetDomains.csv"))?;
    dump_owner_domains_csv(&dir.join("RSP_Alpha_OwnerDomains.csv"))?;
    dump_framework_signals_csv(&dir.join("RSP_Alpha_FrameworkSignals.csv"))?;
    Ok(())
}

fn dump_capture_csv(path: &Path) -> std::io::Result<()> {
    let (named_static_targets, unresolved_static_targets, named_static_calls, unresolved_static_calls) =
        static_resolution_summary();

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,version,state,start_unix_ms,stop_unix_ms,start_qpc,stop_qpc,quiescent_qpc,qpc_frequency,duration_ms,stop_drain_ms,hotkey,hotkey_poll_ms,mapped_mod_functions,observed_threads,merged_shards,frame_callbacks,frame_quality,frame_rows,callsite_rows,observed_calls,owner_rows,function_rows,spike_rows,hot_path_rows,dropped_spikes,dropped_hot_paths,named_static_targets,unresolved_static_targets,named_static_calls,unresolved_static_calls,shard_merge_ok")?;
    writeln!(
        w,
        "{},0.3.2,{},{},{},{},{},{},{},{:.3},{:.3},F11,{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{},{}",
        CAPTURE_ID.load(Ordering::Relaxed),
        state_name(PROFILE_STATE.load(Ordering::Acquire)),
        CAPTURE_START_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed),
        CAPTURE_START_QPC.load(Ordering::Relaxed),
        CAPTURE_STOP_QPC.load(Ordering::Relaxed),
        QUIESCENT_QPC.load(Ordering::Relaxed),
        QPC_FREQUENCY.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        STOP_DRAIN_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        HOTKEY_POLL_MS,
        mapped_mod_function_count(),
        observed_thread_count(),
        MERGED_SHARDS.load(Ordering::Relaxed),
        FRAME_CALLBACKS.load(Ordering::Relaxed),
        frame_quality(),
        LAST_FRAME_ROWS.load(Ordering::Relaxed),
        LAST_CALLSITE_ROWS.load(Ordering::Relaxed),
        LAST_OBSERVED_CALLS.load(Ordering::Relaxed),
        LAST_OWNER_ROWS.load(Ordering::Relaxed),
        LAST_FUNCTION_ROWS.load(Ordering::Relaxed),
        LAST_SPIKE_ROWS.load(Ordering::Relaxed),
        LAST_HOT_PATH_ROWS.load(Ordering::Relaxed),
        SPIKES_DROPPED.load(Ordering::Relaxed),
        HOT_PATHS_DROPPED.load(Ordering::Relaxed),
        named_static_targets,
        unresolved_static_targets,
        named_static_calls,
        unresolved_static_calls,
        LAST_SHARD_MERGE_OK.load(Ordering::Acquire),
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
        "{},STOP_REQUEST,{},{:.3},{}",
        id,
        CAPTURE_STOP_QPC.load(Ordering::Relaxed),
        CAPTURE_DURATION_US.load(Ordering::Relaxed) as f64 / 1_000.0,
        CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed)
    )?;
    let quiet = QUIESCENT_QPC.load(Ordering::Relaxed);
    if quiet != 0 {
        writeln!(
            w,
            "{},QUIESCENT,{},{:.3},{}",
            id,
            quiet,
            capture_ms_from_qpc(quiet),
            CAPTURE_STOP_UNIX_MS.load(Ordering::Relaxed)
        )?;
    }
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
    writeln!(w, "function_ptr,stable_function_id,owner,source_path,source_line,function")?;
    for (ptr, meta) in rows {
        writeln!(
            w,
            "0x{:016X},RSPF-{:016X},{},{},{},{}",
            ptr,
            stable_function_hash(meta),
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
    writeln!(w, "capture_id,stable_function_id,stable_callsite_id,owner,source_path,source_function,source_line,call_kind,target,target_resolution,calls,calls_per_sec,observed_inclusive_ms,exclusive_instrumented_ms,avg_inclusive_us,avg_exclusive_us,max_inclusive_ms,max_exclusive_ms,max_at_capture_ms,active_frames,active_frame_pct,repeated_frames,max_calls_per_frame,calls_per_active_frame,dominant_cadence,over_1ms,over_5ms,over_16_67ms")?;

    let duration_s = capture_duration_s();
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},RSPF-{:016X},RSPC-{:016X},{},{},{},{},{},{},{},{},{:.3},{:.6},{:.6},{:.3},{:.3},{:.6},{:.6},{:.3},{},{:.3},{},{},{:.3},{},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_function_hash(meta),
            stable_callsite_hash(meta, *key, &target),
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
    writeln!(w, "capture_id,stable_function_id,owner,source_path,source_line,source_function,outgoing_calls,calls_per_sec,observed_inclusive_ms,exclusive_instrumented_ms,active_frames,active_frame_pct,calls_per_active_frame,max_calls_per_frame,max_call_ms,over_1ms,over_5ms,over_16_67ms")?;
    for (ptr, stat) in rows {
        let Some(meta) = funcs.get(ptr) else { continue; };
        writeln!(
            w,
            "{},RSPF-{:016X},{},{},{},{},{},{:.3},{:.6},{:.6},{},{:.3},{:.3},{},{:.6},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_function_hash(meta),
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
    writeln!(w, "capture_id,call_kind,target,target_domain,target_resolution,total_calls,calls_per_sec,unique_calling_owners,unique_source_functions,observed_inclusive_ms,exclusive_instrumented_ms,max_call_ms,max_callsite_active_frame_pct")?;
    for ((kind, target_key), stat) in rows {
        let key = CallsiteKey { caller: 0, line: 0, kind: *kind, target: *target_key };
        let target = target_info(key, &funcs, &targets);
        writeln!(
            w,
            "{},{},{},{},{},{},{:.3},{},{},{:.6},{:.6},{:.6},{:.3}",
            CAPTURE_ID.load(Ordering::Relaxed),
            call_kind(*kind),
            csv(&target.display),
            target_domain(&target.display),
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
    writeln!(w, "capture_id,stable_callsite_id,caller_owner,caller_function,caller_source,caller_line,call_kind,callee_owner,callee_function,target_resolution,calls,observed_inclusive_ms,exclusive_instrumented_ms,max_inclusive_ms,active_frames,active_frame_pct")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},RSPC-{:016X},{},{},{},{},{},{},{},{},{},{:.6},{:.6},{:.6},{},{:.3}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_callsite_hash(meta, *key, &target),
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
    writeln!(w, "capture_id,stable_callsite_id,owner,source_function,source_line,target,calls,active_frames,active_frame_pct,repeated_frames,max_calls_per_frame,calls_per_active_frame,first_seen_ms,last_seen_ms,dominant_cadence,gap_lt_0_1ms,gap_0_1_1ms,gap_1_5ms,gap_5_12ms,gap_12_25ms,gap_25_75ms,gap_75_200ms,gap_200_750ms,gap_750_1500ms,gap_gt_1500ms")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        writeln!(
            w,
            "{},RSPC-{:016X},{},{},{},{},{},{},{:.3},{},{},{:.3},{:.3},{:.3},{},{},{},{},{},{},{},{},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_callsite_hash(meta, *key, &target),
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
    writeln!(w, "capture_id,capture_ms,frame_id,thread_id,stable_callsite_id,owner,source_path,source_function,source_line,call_kind,target,duration_ms,exclusive_instrumented_ms,depth,threshold")?;
    for e in spikes {
        let Some(meta) = funcs.get(&e.key.caller) else { continue; };
        let target = target_info(e.key, &funcs, &targets);
        let duration_ms = ticks_to_ms(e.inclusive_ticks);
        let threshold = if duration_ms >= 16.67 { ">=16.67ms" } else if duration_ms >= 5.0 { ">=5ms" } else { ">=1ms" };
        writeln!(
            w,
            "{},{:.3},{},{},RSPC-{:016X},{},{},{},{},{},{},{:.6},{:.6},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            capture_ms_from_qpc(e.qpc),
            e.frame_id,
            e.thread_id,
            stable_callsite_hash(meta, e.key, &target),
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
    writeln!(w, "capture_id,capture_ms,frame_id,thread_id,depth,duration_ms,exclusive_leaf_ms,path")?;
    for e in paths {
        writeln!(
            w,
            "{},{:.3},{},{},{},{:.6},{:.6},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            capture_ms_from_qpc(e.qpc),
            e.frame_id,
            e.thread_id,
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
    writeln!(w, "capture_id,stable_callsite_id,owner,source_function,source_line,target,calls,calls_per_sec,active_frame_pct,calls_per_active_frame,max_calls_per_frame,exclusive_instrumented_ms,max_call_ms,dominant_cadence,classification,likely_treatment")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        let (class, treatment) = classify_work(stat, total_frames, &meta.function, &target.display);
        writeln!(
            w,
            "{},RSPC-{:016X},{},{},{},{},{},{:.3},{:.3},{:.3},{},{:.6},{:.6},{},{},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_callsite_hash(meta, *key, &target),
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


fn stable_function_hash(meta: &FunctionMeta) -> u64 {
    stable_hash64(&format!(
        "{}|{}|{}",
        meta.source_path.replace('/', "\\").to_ascii_lowercase(),
        meta.source_line,
        meta.function
    ))
}

fn stable_callsite_hash(meta: &FunctionMeta, key: CallsiteKey, target: &TargetInfo) -> u64 {
    stable_hash64(&format!(
        "{:016X}|{}|{}|{}",
        stable_function_hash(meta),
        key.line,
        call_kind(key.kind),
        target.display
    ))
}

fn target_domain(name: &str) -> &'static str {
    let n = name.to_ascii_lowercase();
    if n.starts_with("operator") || n == "cast" || n.ends_with("::cast") {
        "LANGUAGE_INTRINSIC"
    } else if n.contains("wrapper$") || n.contains("proxy$") {
        "SCRIPT_WRAPPER"
    } else if n.contains("blackboard") {
        "BLACKBOARD"
    } else if n.contains("statuseffect") || n.contains("status_effect") {
        "STATUS_EFFECT"
    } else if n.contains("equipment") || n.contains("inventory") || n.contains("item") {
        "EQUIPMENT_INVENTORY"
    } else if n.contains("quest") || n.contains("journal") || n.contains("fact") {
        "QUEST_JOURNAL_FACTS"
    } else if n.contains("mappin") || n.contains("minimap") || n.contains("worldmap") || n.contains("mapcontroller") || n.contains("mapmenu") {
        "MAP_MAPPIN"
    } else if n.contains("hud") || n.contains("widget") || n.contains("ink") || n.contains("uicontroller") || n.contains("uisystem") {
        "UI_HUD"
    } else if n.contains("vehicle") || n.contains("car") || n.contains("bike") {
        "VEHICLE"
    } else if n.contains("combat") || n.contains("damage") || n.contains("hit") || n.contains("weapon") {
        "COMBAT"
    } else if n.contains("npc") || n.contains("puppet") || n.contains("ai") {
        "NPC_AI"
    } else if n.contains("player") || n.contains("gameinstance") || n.contains("entity") {
        "PLAYER_ENTITY"
    } else if n.contains("time") || n.contains("clock") || n.contains("delay") || n.contains("timer") {
        "TIME_SCHEDULING"
    } else if n.contains("input") || n.contains("action") || n.contains("key") {
        "INPUT_ACTION"
    } else if n.contains("audio") || n.contains("sound") {
        "AUDIO"
    } else {
        "OTHER"
    }
}

fn dump_threads_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
    let duration_s = capture_duration_s();
    let shard_ptrs = SHARDS.read().clone();
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,thread_id,calls,calls_per_sec,unique_owners,unique_functions,root_calls,cross_mod_edges,observed_inclusive_ms,exclusive_instrumented_ms,first_seen_ms,last_seen_ms")?;

    for raw in shard_ptrs {
        let ptr = raw as *mut ThreadShard;
        if ptr.is_null() {
            continue;
        }
        let t = unsafe { &*(*ptr).state.get() };
        if t.capture_id != capture_id || t.total_calls == 0 {
            continue;
        }
        let mut owners = HashSet::new();
        let mut functions = HashSet::new();
        let mut inclusive = 0u64;
        let mut exclusive = 0u64;
        for (key, stat) in t.aggregate.iter() {
            functions.insert(key.caller);
            if let Some(meta) = funcs.get(&key.caller) {
                owners.insert(meta.owner.clone());
            }
            inclusive = inclusive.saturating_add(stat.inclusive_ticks);
            exclusive = exclusive.saturating_add(stat.exclusive_ticks);
        }
        let root_calls = t.roots.values().map(|r| r.calls).sum::<u64>();
        let cross_calls = t.cross_mod_edges.values().map(|r| r.calls).sum::<u64>();
        writeln!(
            w,
            "{},{},{},{:.3},{},{},{},{},{:.6},{:.6},{:.3},{:.3}",
            capture_id,
            t.thread_id,
            t.total_calls,
            rate(t.total_calls, duration_s),
            owners.len(),
            functions.len(),
            root_calls,
            cross_calls,
            ticks_to_ms(inclusive),
            ticks_to_ms(exclusive),
            capture_ms_from_qpc(t.first_qpc),
            capture_ms_from_qpc(t.last_qpc),
        )?;
    }
    w.flush()
}

fn dump_roots_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let roots = ROOTS.read();
    let total_frames = FRAMES.read().len() as u64;
    let duration_s = capture_duration_s();
    let mut rows: Vec<_> = roots.iter().collect();
    rows.sort_by(|a, b| b.1.descendant_calls.cmp(&a.1.descendant_calls));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,stable_callsite_id,owner,source_function,source_path,source_line,target,root_calls,root_calls_per_sec,active_frames,active_frame_pct,total_root_ms,avg_root_ms,max_root_ms,descendant_calls,avg_descendants_per_root,max_descendants_per_root")?;
    for (key, stat) in rows {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        let active_frames = stat.frame_calls.len() as u64;
        writeln!(
            w,
            "{},RSPC-{:016X},{},{},{},{},{},{},{:.3},{},{:.3},{:.6},{:.6},{:.6},{},{:.3},{}",
            CAPTURE_ID.load(Ordering::Relaxed),
            stable_callsite_hash(meta, *key, &target),
            csv(&meta.owner),
            csv(&meta.function),
            csv(&meta.source_path),
            key.line,
            csv(&target.display),
            stat.calls,
            rate(stat.calls, duration_s),
            active_frames,
            pct(active_frames, total_frames),
            ticks_to_ms(stat.inclusive_ticks),
            if stat.calls > 0 { ticks_to_ms(stat.inclusive_ticks) / stat.calls as f64 } else { 0.0 },
            ticks_to_ms(stat.max_inclusive_ticks),
            stat.descendant_calls,
            if stat.calls > 0 { stat.descendant_calls as f64 / stat.calls as f64 } else { 0.0 },
            stat.max_descendant_calls,
        )?;
    }
    w.flush()
}

fn dump_cross_mod_edges_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let edges = CROSS_MOD_EDGES.read();
    let total_frames = FRAMES.read().len() as u64;
    let mut rows: Vec<_> = edges.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,parent_owner,parent_function,parent_line,parent_target,child_owner,child_function,child_line,child_target,calls,active_frames,active_frame_pct")?;
    for (edge, stat) in rows {
        let Some(parent) = funcs.get(&edge.parent.caller) else { continue; };
        let Some(child) = funcs.get(&edge.child.caller) else { continue; };
        let parent_target = target_info(edge.parent, &funcs, &targets);
        let child_target = target_info(edge.child, &funcs, &targets);
        let active_frames = stat.frame_calls.len() as u64;
        writeln!(
            w,
            "{},{},{},{},{},{},{},{},{},{},{},{:.3}",
            CAPTURE_ID.load(Ordering::Relaxed),
            csv(&parent.owner),
            csv(&parent.function),
            edge.parent.line,
            csv(&parent_target.display),
            csv(&child.owner),
            csv(&child.function),
            edge.child.line,
            csv(&child_target.display),
            stat.calls,
            active_frames,
            pct(active_frames, total_frames),
        )?;
    }
    w.flush()
}

#[derive(Default)]
struct DomainSummary {
    calls: u64,
    inclusive_ticks: u64,
    exclusive_ticks: u64,
    owners: HashSet<String>,
    targets: HashSet<String>,
    callsites: u64,
}

fn dump_target_domains_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let mut grouped: HashMap<String, DomainSummary> = HashMap::new();
    for (key, stat) in stats.iter() {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        let domain = target_domain(&target.display).to_owned();
        let e = grouped.entry(domain).or_default();
        e.calls = e.calls.saturating_add(stat.calls);
        e.inclusive_ticks = e.inclusive_ticks.saturating_add(stat.inclusive_ticks);
        e.exclusive_ticks = e.exclusive_ticks.saturating_add(stat.exclusive_ticks);
        e.owners.insert(meta.owner.clone());
        e.targets.insert(target.display);
        e.callsites += 1;
    }
    let mut rows: Vec<_> = grouped.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,domain,calls,calls_per_sec,unique_owners,unique_targets,unique_callsites,observed_inclusive_ms,exclusive_instrumented_ms")?;
    for (domain, e) in rows {
        writeln!(w, "{},{},{},{:.3},{},{},{},{:.6},{:.6}",
            CAPTURE_ID.load(Ordering::Relaxed), domain, e.calls, rate(e.calls, capture_duration_s()),
            e.owners.len(), e.targets.len(), e.callsites, ticks_to_ms(e.inclusive_ticks), ticks_to_ms(e.exclusive_ticks))?;
    }
    w.flush()
}

fn dump_owner_domains_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let stats = CALLSITES.read();
    let mut grouped: HashMap<(String, String), DomainSummary> = HashMap::new();
    for (key, stat) in stats.iter() {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        let target = target_info(*key, &funcs, &targets);
        let domain = target_domain(&target.display).to_owned();
        let e = grouped.entry((meta.owner.clone(), domain)).or_default();
        e.calls = e.calls.saturating_add(stat.calls);
        e.inclusive_ticks = e.inclusive_ticks.saturating_add(stat.inclusive_ticks);
        e.exclusive_ticks = e.exclusive_ticks.saturating_add(stat.exclusive_ticks);
        e.targets.insert(target.display);
        e.callsites += 1;
    }
    let mut rows: Vec<_> = grouped.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));
    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,domain,calls,calls_per_sec,unique_targets,unique_callsites,observed_inclusive_ms,exclusive_instrumented_ms")?;
    for ((owner, domain), e) in rows {
        writeln!(w, "{},{},{},{},{:.3},{},{},{:.6},{:.6}",
            CAPTURE_ID.load(Ordering::Relaxed), csv(owner), domain, e.calls, rate(e.calls, capture_duration_s()),
            e.targets.len(), e.callsites, ticks_to_ms(e.inclusive_ticks), ticks_to_ms(e.exclusive_ticks))?;
    }
    w.flush()
}

fn is_framework_shared_target(name: &str) -> bool {
    let domain = target_domain(name);
    domain != "LANGUAGE_INTRINSIC" && domain != "SCRIPT_WRAPPER"
}

fn dump_framework_signals_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let targets = TARGET_NAMES.read();
    let callsites = CALLSITES.read();
    let owners = OWNER_AGG.read();
    let roots = ROOTS.read();
    let cross = CROSS_MOD_EDGES.read();
    let total_frames = FRAMES.read().len() as u64;

    let mut target_owners: HashMap<(u8, u64), HashSet<String>> = HashMap::new();
    for (key, _stat) in callsites.iter() {
        if let Some(meta) = funcs.get(&key.caller) {
            target_owners.entry((key.kind, key.target)).or_default().insert(meta.owner.clone());
        }
    }

    let mut owner_unique_targets: HashMap<String, HashSet<(u8, u64)>> = HashMap::new();
    let mut owner_shared_targets: HashMap<String, HashSet<(u8, u64)>> = HashMap::new();
    let mut owner_framework_shared_targets: HashMap<String, HashSet<(u8, u64)>> = HashMap::new();
    let mut owner_wrapper_calls: HashMap<String, u64> = HashMap::new();
    for (key, stat) in callsites.iter() {
        let Some(meta) = funcs.get(&key.caller) else { continue; };
        owner_unique_targets.entry(meta.owner.clone()).or_default().insert((key.kind, key.target));
        if target_owners.get(&(key.kind, key.target)).map_or(0, |x| x.len()) >= 3 {
            owner_shared_targets.entry(meta.owner.clone()).or_default().insert((key.kind, key.target));
            let target = target_info(*key, &funcs, &targets);
            if is_framework_shared_target(&target.display) {
                owner_framework_shared_targets
                    .entry(meta.owner.clone())
                    .or_default()
                    .insert((key.kind, key.target));
            }
        }
        if meta.function.contains("wrapper$") {
            *owner_wrapper_calls.entry(meta.owner.clone()).or_default() += stat.calls;
        }
    }

    let mut owner_root: HashMap<String, (u64, u64, u64)> = HashMap::new();
    for (key, stat) in roots.iter() {
        if let Some(meta) = funcs.get(&key.caller) {
            let e = owner_root.entry(meta.owner.clone()).or_default();
            e.0 = e.0.saturating_add(stat.calls);
            e.1 = e.1.saturating_add(stat.descendant_calls);
            e.2 = e.2.max(stat.max_descendant_calls);
        }
    }

    let mut cross_out: HashMap<String, u64> = HashMap::new();
    let mut cross_in: HashMap<String, u64> = HashMap::new();
    for (edge, stat) in cross.iter() {
        if let (Some(p), Some(c)) = (funcs.get(&edge.parent.caller), funcs.get(&edge.child.caller)) {
            *cross_out.entry(p.owner.clone()).or_default() += stat.calls;
            *cross_in.entry(c.owner.clone()).or_default() += stat.calls;
        }
    }

    let capture_id = CAPTURE_ID.load(Ordering::Relaxed);
    let shard_ptrs = SHARDS.read().clone();
    let mut owner_threads: HashMap<String, HashSet<u32>> = HashMap::new();
    for raw in shard_ptrs {
        let ptr = raw as *mut ThreadShard;
        if ptr.is_null() { continue; }
        let t = unsafe { &*(*ptr).state.get() };
        if t.capture_id != capture_id { continue; }
        for key in t.aggregate.keys() {
            if let Some(meta) = funcs.get(&key.caller) {
                owner_threads.entry(meta.owner.clone()).or_default().insert(t.thread_id);
            }
        }
    }

    let file = File::create(path)?;
    let mut w = BufWriter::new(file);
    writeln!(w, "capture_id,owner,calls,calls_per_sec,active_frame_pct,calls_per_active_frame,root_calls,avg_descendants_per_root,max_descendants_per_root,unique_targets,shared_targets_ge3owners,framework_shared_targets_ge3owners,wrapper_calls,cross_mod_calls_out,cross_mod_calls_in,threads,signals,framework_primitives")?;

    let mut rows: Vec<_> = owners.iter().collect();
    rows.sort_by(|a, b| b.1.calls.cmp(&a.1.calls));
    for (owner, stat) in rows {
        let (root_calls, desc, max_desc) = owner_root.get(owner).copied().unwrap_or((0, 0, 0));
        let threads = owner_threads.get(owner).map_or(0, |x| x.len());
        let unique_targets = owner_unique_targets.get(owner).map_or(0, |x| x.len());
        let shared_targets = owner_shared_targets.get(owner).map_or(0, |x| x.len());
        let framework_shared_targets = owner_framework_shared_targets.get(owner).map_or(0, |x| x.len());
        let wrapper_calls = owner_wrapper_calls.get(owner).copied().unwrap_or(0);
        let active_pct = pct(stat.active_frames, total_frames);
        let calls_per_active = if stat.active_frames > 0 { stat.calls as f64 / stat.active_frames as f64 } else { 0.0 };
        let avg_desc = if root_calls > 0 { desc as f64 / root_calls as f64 } else { 0.0 };

        let mut signals = Vec::new();
        let mut primitives = Vec::new();
        if active_pct >= 80.0 {
            signals.push("HIGH_DUTY");
            primitives.push("EVENT_OR_DIRTY_GATE");
        }
        if calls_per_active >= 100.0 || max_desc >= 500 {
            signals.push("AMPLIFICATION");
            primitives.push("CACHE_INDEX_ALGORITHM");
        }
        if framework_shared_targets >= 5 {
            signals.push("SHARED_QUERY_CONSUMER");
            primitives.push("SHARED_STATE_SERVICE");
        }
        if wrapper_calls > 0 && wrapper_calls.saturating_mul(10) >= stat.calls {
            signals.push("WRAPPER_HEAVY");
            primitives.push("WRAPPER_CONSOLIDATION");
        }
        if cross_out.get(owner).copied().unwrap_or(0) > 0 || cross_in.get(owner).copied().unwrap_or(0) > 0 {
            signals.push("CROSS_MOD_CHATTER");
            primitives.push("EVENT_BRIDGE_OR_SHARED_STATE");
        }
        if threads > 1 {
            signals.push("MULTITHREAD");
            primitives.push("THREAD_SAFE_CORE_SERVICE");
        }
        if signals.is_empty() {
            signals.push("NO_STRONG_SIGNAL");
            primitives.push("SOURCE_INSPECTION");
        }

        writeln!(
            w,
            "{},{},{},{:.3},{:.3},{:.3},{},{:.3},{},{},{},{},{},{},{},{},{},{}",
            capture_id,
            csv(owner),
            stat.calls,
            rate(stat.calls, capture_duration_s()),
            active_pct,
            calls_per_active,
            root_calls,
            avg_desc,
            max_desc,
            unique_targets,
            shared_targets,
            framework_shared_targets,
            wrapper_calls,
            cross_out.get(owner).copied().unwrap_or(0),
            cross_in.get(owner).copied().unwrap_or(0),
            threads,
            csv(&signals.join("|")),
            csv(&primitives.join("|")),
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
        if let Some(name) = targets.get(&(0, key.target)) {
            return TargetInfo {
                display: name.clone(),
                owner: "<unbound-static>".to_owned(),
                function: name.clone(),
                resolution: "NAMED_STATIC",
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
