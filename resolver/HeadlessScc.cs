using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GRedscript.Resolver;

/// <summary>
/// Headless access to the *official* redscript 0.5.31 scc_lib.dll API.
/// Uses the owner's exported ABI, never recompiles or reimplements SCC.
/// CLI scc.exe enables GUI popups on compiler errors and can hang Windows CI.
/// </summary>
internal static class HeadlessScc
{
    private const string Marker = "GRSP_R6_ISOLATED_WORKSPACE";
    private const int ErrorBufferBytes = 2 * 1024 * 1024;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NewSettings(IntPtr r6Dir);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetPath(IntPtr settings, IntPtr path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DisablePopup(IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr Compile(IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetSuccess(IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint CopyError(IntPtr result, IntPtr buffer, nuint bufferSize);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeResult(IntPtr result);

    internal static int Run(Dictionary<string, string> options)
    {
        string Value(string key) =>
            options.TryGetValue(key, out string? value) &&
            !string.IsNullOrWhiteSpace(value)
                ? Path.GetFullPath(value)
                : throw new ArgumentException("Missing SCC option: " + key);

        if (options.Keys.Any(x => x is not ("--workspace" or "--r6" or
                "--cache" or "--compiled" or "--scc-native" or
                "--out" or "--log")))
            throw new ArgumentException("Unsupported headless SCC option");

        string workspace = Value("--workspace");
        string r6 = Value("--r6");
        string cache = Value("--cache");
        string output = Value("--compiled");
        string library = Value("--scc-native");
        string report = Value("--out");
        string log = Value("--log");

        // No headless SCC entrypoint may write into a normal game tree.
        // The owner runner explicitly creates this dedicated isolated copy.
        if (!r6.Equals(Path.Combine(workspace, "r6"),
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(workspace, ".grsp-r6-workspace")) ||
            File.ReadAllText(Path.Combine(workspace, ".grsp-r6-workspace")).Trim() != Marker ||
            !File.Exists(cache) || !File.Exists(library) ||
            !Directory.Exists(Path.Combine(r6, "scripts")) ||
            !Directory.Exists(Path.Combine(r6, "cache")) ||
            !cache.Equals(Path.Combine(r6, "cache", "final.redscripts"),
                StringComparison.OrdinalIgnoreCase) ||
            !output.Equals(cache + ".modded", StringComparison.OrdinalIgnoreCase) ||
            report.StartsWith(workspace + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            log.StartsWith(workspace + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            File.Exists(output) || File.Exists(report) || File.Exists(log))
            throw new InvalidDataException(
                "Headless SCC requires an untouched, explicitly isolated source/cache workspace");

        Directory.CreateDirectory(Path.Combine(r6, "logs"));
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var started = System.Diagnostics.Stopwatch.StartNew();
        byte[] before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cache));
        string error = "";
        bool success = false;
        IntPtr lib = IntPtr.Zero;
        try
        {
            lib = NativeLibrary.Load(library);
            T Resolve<T>(string symbol) where T : Delegate =>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, symbol));
            var allocate = Resolve<NewSettings>("scc_settings_new");
            var setCache = Resolve<SetPath>("scc_settings_set_custom_cache_file");
            var setOutput = Resolve<SetPath>("scc_settings_set_output_cache_file");
            var disablePopup = Resolve<DisablePopup>("scc_settings_disable_error_popup");
            var compile = Resolve<Compile>("scc_compile");
            var getSuccess = Resolve<GetSuccess>("scc_get_success");
            var copyError = Resolve<CopyError>("scc_copy_error");
            var free = Resolve<FreeResult>("scc_free_result");

            IntPtr r6Arg = Marshal.StringToCoTaskMemUTF8(r6);
            IntPtr cacheArg = Marshal.StringToCoTaskMemUTF8(cache);
            IntPtr outputArg = Marshal.StringToCoTaskMemUTF8(output);
            IntPtr result = IntPtr.Zero;
            try
            {
                IntPtr settings = allocate(r6Arg);
                if (settings == IntPtr.Zero)
                    throw new InvalidDataException("SCC native settings allocation failed");
                setCache(settings, cacheArg);
                setOutput(settings, outputArg);
                // This is the critical difference from scc.exe: the upstream
                // CLI hardcodes show_error_popup=true and blocks headless CI.
                disablePopup(settings);
                result = compile(settings); // takes ownership of Box<SccSettings>
                if (result == IntPtr.Zero)
                    throw new InvalidDataException("SCC native result missing");
                success = getSuccess(result) != IntPtr.Zero;
                if (!success)
                {
                    IntPtr buffer = Marshal.AllocHGlobal(ErrorBufferBytes);
                    try
                    {
                        nuint n = copyError(result, buffer, (nuint)ErrorBufferBytes);
                        if (n > (nuint)(ErrorBufferBytes - 1))
                            throw new InvalidDataException("SCC error message length invalid");
                        byte[] bytes = new byte[(int)n];
                        Marshal.Copy(buffer, bytes, 0, bytes.Length);
                        error = Encoding.UTF8.GetString(bytes);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally
            {
                if (result != IntPtr.Zero) free(result);
                Marshal.FreeCoTaskMem(r6Arg);
                Marshal.FreeCoTaskMem(cacheArg);
                Marshal.FreeCoTaskMem(outputArg);
            }
        }
        finally { if (lib != IntPtr.Zero) NativeLibrary.Free(lib); }

        byte[] after = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(cache));
        if (!before.AsSpan().SequenceEqual(after))
            throw new InvalidDataException("Headless SCC changed original vanilla cache");
        if (success && (!File.Exists(output) || new FileInfo(output).Length < 1048576))
            throw new InvalidDataException("Headless SCC success did not produce compiled output");

        // Error may contain private mod paths and source lines; it stays only
        // on the private CI runner. Public CI prints counts/types, not paths.
        // The official C ABI intentionally returns a *user-facing* error
        // summary listing source owners, not compiler diagnostic codes.
        // The same SCC library writes actual diagnostic codes to r6/logs.
        // Read these only inside the isolated workspace, retain the full
        // source-bearing text in runner-private --log and publish counts only.
        string compilerLogs = string.Join("\n",
            Directory.EnumerateFiles(Path.Combine(r6, "logs"), "redscript*.log")
                .Where(file => new FileInfo(file).Length <= 8 * 1024 * 1024)
                .Select(File.ReadAllText));
        string details = error + "\n" + compilerLogs;
        File.WriteAllText(log, details, new UTF8Encoding(false));
        var codeCounts = Regex.Matches(details, @"\[[A-Z][A-Z0-9_]+\]")
            .Select(m => m.Value)
            .Where(code => code is not ("[INFO]" or "[WARN]" or "[ERROR]" or
                "[DEBUG]" or "[TRACE]"))
            .GroupBy(x => x, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var status = new {
            schemaVersion = "grsp.scc.headless.v1",
            success,
            guiErrorPopupDisabled = true,
            originalCacheUnchanged = true,
            generatedOutputPresent = File.Exists(output),
            generatedOutputBytes = File.Exists(output) ? new FileInfo(output).Length : 0,
            errorCodeCounts = codeCounts,
            diagnosticBytes = Encoding.UTF8.GetByteCount(error),
            nativeCompilerLogBytes = Encoding.UTF8.GetByteCount(compilerLogs),
            compilerDiagnosticCodesFound = codeCounts.Values.Sum(),
            elapsedSeconds = Math.Round(started.Elapsed.TotalSeconds, 2),
            gameValidated = false,
            deployable = false
        };
        File.WriteAllText(report, JsonSerializer.Serialize(
            status, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("GRSP headless SCC: " +
            (success ? "COMPILED" : "COMPILER_ERRORS") +
            " elapsedSeconds=" + status.elapsedSeconds +
            " errorCodes=" + string.Join(",", codeCounts.Select(x => x.Key + ":" + x.Value)) +
            " originalCacheUnchanged=true popupDisabled=true");
        return success ? 0 : 3;
    }
}
