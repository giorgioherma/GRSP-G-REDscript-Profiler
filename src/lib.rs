use std::{
    collections::HashMap,
    env,
    fs::{self, File},
    io::{BufWriter, Write},
    mem,
    path::{Path, PathBuf},
    sync::{
        LazyLock,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
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

const FLUSH_INTERVAL_SECS: u64 = 5;

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
    kind: u8,      // 0=static, 1=virtual
    target: u64,   // static pointer cast to u64, or CName hash for virtual
}

#[derive(Debug, Default, Clone, Copy)]
struct Stat {
    calls: u64,
    total_ns: u128,
    max_ns: u128,
    over_1ms: u64,
    over_5ms: u64,
    over_16ms: u64,
}

static FUNCTIONS: LazyLock<RwLock<HashMap<usize, FunctionMeta>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static CALLSITES: LazyLock<RwLock<HashMap<CallsiteKey, Stat>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static TARGET_NAMES: LazyLock<RwLock<HashMap<(u8, u64), String>>> =
    LazyLock::new(|| RwLock::new(HashMap::new()));

static WRITER_RUNNING: AtomicBool = AtomicBool::new(false);

impl Plugin for RedscriptProfilerAlpha {
    const AUTHOR: &'static U16CStr = wcstr!("RSP alpha");
    const NAME: &'static U16CStr = wcstr!("redscript-profiler-alpha");
    const VERSION: SemVer = SemVer::new(0, 1, 0);

    fn on_init(env: &SdkEnv) {
        let bind_function_addr = addr_hashes::resolve(BIND_FUNCTION_HASH);

        let bind_ok = unsafe {
            env.attach_hook(
                BIND_FUNCTION,
                mem::transmute(bind_function_addr),
                on_bind_function,
            )
        };

        env.info(format!(
            "[RSP alpha] bind-function hook: {}",
            if bind_ok { "OK" } else { "FAILED" }
        ));

        env.add_listener(
            StateType::Initialization,
            StateListener::default().with_on_exit(on_app_init),
        );

        start_writer_thread();
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

    env.info(format!(
        "[RSP alpha] InvokeStatic hook: {} / InvokeVirtual hook: {}",
        if static_ok { "OK" } else { "FAILED" },
        if virtual_ok { "OK" } else { "FAILED" }
    ));

    write_status_file(static_ok, virtual_ok);
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
    let mut guard = CALLSITES.write();
    let stat = guard.entry(key).or_default();

    stat.calls += 1;
    stat.total_ns += ns;
    stat.max_ns = stat.max_ns.max(ns);

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

fn ensure_static_target_name(ptr: usize, key: u64) {
    if TARGET_NAMES.read().contains_key(&(0, key)) {
        return;
    }

    let name = if ptr == 0 {
        "<null>".to_owned()
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

fn start_writer_thread() {
    if WRITER_RUNNING.swap(true, Ordering::SeqCst) {
        return;
    }

    thread::spawn(|| loop {
        thread::sleep(Duration::from_secs(FLUSH_INTERVAL_SECS));
        let _ = dump_results();
    });
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

fn write_status_file(static_ok: bool, virtual_ok: bool) {
    let Some(dir) = results_dir() else {
        return;
    };
    let _ = fs::create_dir_all(&dir);

    let status = format!(
        "RSP alpha 0.1 runtime callsite profiler\n\
         Bind/source mapping: enabled\n\
         InvokeStatic hook: {}\n\
         InvokeVirtual hook: {}\n\
         Flush interval: {} s\n",
        if static_ok { "OK" } else { "FAILED" },
        if virtual_ok { "OK" } else { "FAILED" },
        FLUSH_INTERVAL_SECS
    );

    let _ = fs::write(dir.join("RSP_Alpha_Status.txt"), status);
}

fn dump_results() -> std::io::Result<()> {
    let Some(dir) = results_dir() else {
        return Ok(());
    };
    fs::create_dir_all(&dir)?;

    dump_callsite_csv(&dir.join("RSP_Alpha_CallSites.csv"))?;
    dump_function_map_csv(&dir.join("RSP_Alpha_FunctionMap.csv"))?;

    Ok(())
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
        "owner,source_path,source_function,source_line,call_kind,target,calls,total_ms,avg_us,max_ms,over_1ms,over_5ms,over_16_67ms"
    )?;

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
            "{},{},{},{},{},{},{},{:.6},{:.3},{:.6},{},{},{}",
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
            stat.over_1ms,
            stat.over_5ms,
            stat.over_16ms,
        )?;
    }

    w.flush()
}

fn dump_function_map_csv(path: &Path) -> std::io::Result<()> {
    let funcs = FUNCTIONS.read();
    let mut rows: Vec<_> = funcs.iter().filter(|(_, m)| m.is_mod_source).collect();
    rows.sort_by(|a, b| a.1.source_path.cmp(&b.1.source_path).then(a.1.source_line.cmp(&b.1.source_line)));

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
