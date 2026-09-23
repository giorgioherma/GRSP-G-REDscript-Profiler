using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static class ManagerServices
{
    public const string ProductVersion = "0.5.0 Public Preview";
    public const string StateFileName = ".grsp_manager_state.json";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string PayloadDirectory => Path.Combine(AppContext.BaseDirectory, "payload");
    public static string PayloadDll => Path.Combine(PayloadDirectory, "redscript_profiler_alpha.dll");
    public static string PayloadScenario => Path.Combine(PayloadDirectory, "RSP_Scenario.txt");
    public static string ArchiveResultsDirectory => Path.Combine(AppContext.BaseDirectory, "RESULTS");

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName("Cyberpunk2077").Length > 0; }
        catch { return false; }
    }

    public static StatusInfo GetStatus(string gameRoot)
    {
        var status = new StatusInfo { GameRoot = gameRoot ?? "" };
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
        {
            status.State = "NO_GAME_ROOT";
            status.Message = "Select the Cyberpunk 2077 game root.";
            return status;
        }

        var exe = GameExe(gameRoot);
        var red4ext = Path.Combine(gameRoot, "red4ext");
        status.GameRootValid = File.Exists(exe);
        status.Red4extPresent = Directory.Exists(red4ext);
        status.PayloadPresent = File.Exists(PayloadDll);
        status.PayloadHash = status.PayloadPresent ? Sha256(PayloadDll) : "";

        var target = TargetDll(gameRoot);
        var statePath = StatePath(gameRoot);
        status.DllPresent = File.Exists(target);
        status.InstalledHash = status.DllPresent ? Sha256(target) : "";
        status.ManagedStatePresent = File.Exists(statePath);

        GrspManagerState? state = null;
        if (status.ManagedStatePresent)
        {
            try { state = LoadState(statePath); }
            catch (Exception ex)
            {
                status.State = "INVALID_MANAGED_STATE";
                status.Message = ex.Message;
            }
        }

        if (state is not null)
        {
            status.OriginalDllMode = state.DllMode;
            status.ManagedInstalledHash = state.InstalledDllHash;
            status.ScenarioOwnership = state.ScenarioMode;
            if (!status.DllPresent)
            {
                status.State = "MANAGED_DLL_MISSING";
                status.Message = "Managed state exists but the installed GRSP DLL is missing.";
            }
            else if (!string.Equals(status.InstalledHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
            {
                status.State = "MANAGED_DLL_CHANGED";
                status.Message = "Managed state exists but the installed DLL changed outside this manager.";
            }
            else if (status.PayloadPresent && string.Equals(status.InstalledHash, status.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                status.State = "INSTALLED_CURRENT";
                status.Message = "GRSP is installed and matches this package.";
            }
            else
            {
                status.State = "INSTALLED_OTHER_PACKAGE";
                status.Message = "GRSP is managed, but this package contains a different DLL build.";
            }
        }
        else if (!status.ManagedStatePresent && status.DllPresent)
        {
            if (status.PayloadPresent && string.Equals(status.InstalledHash, status.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                status.State = "PREEXISTING_SAME";
                status.Message = "The exact packaged GRSP DLL is already installed, but is not managed by this package.";
            }
            else
            {
                status.State = "UNKNOWN_DLL";
                status.Message = "A different redscript_profiler_alpha.dll is installed. Install will back it up and verify the backup before replacement.";
            }
        }
        else if (!status.ManagedStatePresent && string.IsNullOrEmpty(status.State))
        {
            status.State = "NOT_INSTALLED";
            status.Message = "GRSP is not installed.";
        }

        var scenario = ScenarioPath(gameRoot);
        status.ScenarioPresent = File.Exists(scenario);
        status.Scenario = status.ScenarioPresent ? ReadScenarioLabel(scenario) : ReadPayloadScenario();
        status.NativeResultsPath = NativeResultsPath(gameRoot);
        status.LatestCapture = LatestCompletedCapture(gameRoot) ?? "";
        status.CompletedCaptureCount = Directory.Exists(status.NativeResultsPath)
            ? Directory.EnumerateDirectories(status.NativeResultsPath, "Capture_*", SearchOption.TopDirectoryOnly)
                .Count(IsCompletedCapture)
            : 0;
        return status;
    }

    public static InstallResult InstallOrUpdate(string gameRoot)
    {
        EnsureInstallPreconditions(gameRoot);

        var payloadHash = Sha256(PayloadDll);
        var pluginDir = PluginDirectory(gameRoot);
        Directory.CreateDirectory(pluginDir);

        var target = TargetDll(gameRoot);
        var statePath = StatePath(gameRoot);

        if (File.Exists(statePath))
        {
            var state = LoadState(statePath);
            if (!File.Exists(target))
                throw new InvalidOperationException("Managed state exists but the installed DLL is missing. Restore/reconcile before reinstalling.");

            var currentHash = Sha256(target);
            if (!string.Equals(currentHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Installed GRSP DLL changed outside the manager. It will not be overwritten.");

            if (!string.Equals(currentHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(state.DllMode, "preexisting-same", StringComparison.OrdinalIgnoreCase))
                {
                    var backup = DllBackupPath(gameRoot);
                    if (File.Exists(backup))
                        throw new InvalidOperationException($"Cannot safely upgrade: backup path already exists: {backup}");
                    CopyFileVerified(target, backup);
                    state.DllMode = "replaced";
                    state.OriginalDllHash = currentHash;
                    state.DllBackupPath = backup;
                }

                CopyFileVerified(PayloadDll, target, overwrite: true);
                state.InstalledDllHash = payloadHash;
                state.PackageVersion = ProductVersion;
                SaveState(statePath, state);
                return new InstallResult("updated", target, ScenarioPath(gameRoot), payloadHash);
            }

            return new InstallResult("already-managed", target, ScenarioPath(gameRoot), payloadHash);
        }

        var dllMode = "added";
        var originalDllHash = "";
        var dllBackup = "";
        if (File.Exists(target))
        {
            originalDllHash = Sha256(target);
            if (string.Equals(originalDllHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                dllMode = "preexisting-same";
            }
            else
            {
                dllMode = "replaced";
                dllBackup = DllBackupPath(gameRoot);
                if (File.Exists(dllBackup))
                    throw new InvalidOperationException($"Refusing to overwrite an untracked DLL backup: {dllBackup}");
                CopyFileVerified(target, dllBackup);
            }
        }

        var scenarioPath = ScenarioPath(gameRoot);
        var scenarioMode = File.Exists(scenarioPath) ? "preexisting" : "added";
        var originalScenarioHash = "";
        var scenarioBackup = "";
        var defaultScenarioHash = "";

        if (scenarioMode == "preexisting")
        {
            originalScenarioHash = Sha256(scenarioPath);
            scenarioBackup = ScenarioBackupPath(gameRoot);
            if (File.Exists(scenarioBackup))
                throw new InvalidOperationException($"Refusing to overwrite an untracked scenario backup: {scenarioBackup}");
            CopyFileVerified(scenarioPath, scenarioBackup);
        }
        else
        {
            if (!File.Exists(PayloadScenario))
                throw new FileNotFoundException("Packaged RSP_Scenario.txt is missing.", PayloadScenario);
            defaultScenarioHash = Sha256(PayloadScenario);
        }

        var state = new GrspManagerState
        {
            FormatVersion = 1,
            PackageVersion = ProductVersion,
            CreatedUtc = DateTime.UtcNow,
            DllMode = dllMode,
            OriginalDllHash = originalDllHash,
            InstalledDllHash = payloadHash,
            DllBackupPath = dllBackup,
            ScenarioMode = scenarioMode,
            OriginalScenarioHash = originalScenarioHash,
            ScenarioBackupPath = scenarioBackup,
            InstalledDefaultScenarioHash = defaultScenarioHash,
            ManagedScenarioHash = ""
        };

        // Persist recovery metadata after verified backups and before the first user-owned mutation.
        SaveState(statePath, state);

        if (!string.Equals(dllMode, "preexisting-same", StringComparison.OrdinalIgnoreCase))
            CopyFileVerified(PayloadDll, target, overwrite: true);
        else if (!string.Equals(Sha256(target), payloadHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Pre-existing GRSP DLL changed during installation.");

        if (scenarioMode == "added")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(scenarioPath)!);
            if (File.Exists(scenarioPath))
                throw new InvalidOperationException("Scenario file appeared during installation; refusing to overwrite it.");
            CopyFileVerified(PayloadScenario, scenarioPath);
        }

        return new InstallResult(dllMode, target, scenarioPath, payloadHash);
    }

    public static string SaveScenario(string gameRoot, string scenario)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before changing the scenario file.");

        var statePath = StatePath(gameRoot);
        if (!File.Exists(statePath))
            throw new InvalidOperationException("GRSP is not managed by this package. Install it before using manager scenario editing.");

        var state = LoadState(statePath);
        var path = ScenarioPath(gameRoot);
        if (!File.Exists(path))
            throw new InvalidOperationException("Managed scenario file is missing.");

        var currentHash = Sha256(path);
        var expectedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(state.OriginalScenarioHash)) expectedHashes.Add(state.OriginalScenarioHash);
        if (!string.IsNullOrWhiteSpace(state.InstalledDefaultScenarioHash)) expectedHashes.Add(state.InstalledDefaultScenarioHash);
        if (!string.IsNullOrWhiteSpace(state.ManagedScenarioHash)) expectedHashes.Add(state.ManagedScenarioHash);
        if (expectedHashes.Count > 0 && !expectedHashes.Contains(currentHash))
            throw new InvalidOperationException("Scenario file changed outside the manager. It will not be overwritten.");

        var clean = SafeScenario(scenario);
        WriteTextVerified(path, clean + Environment.NewLine);
        state.ManagedScenarioHash = Sha256(path);
        SaveState(statePath, state);
        return clean;
    }

    public static string Restore(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before restoring/uninstalling GRSP.");

        var statePath = StatePath(gameRoot);
        if (!File.Exists(statePath))
            return "No GRSP Manager state was found; nothing was changed.";

        var state = LoadState(statePath);
        var target = TargetDll(gameRoot);
        var scenario = ScenarioPath(gameRoot);

        // Preflight everything first so a conflict cannot leave a half-restored installation.
        // Also recognize a transaction interrupted after state/backups were written but before
        // the packaged DLL fully replaced the target.
        var dllAction = "none";
        if (state.DllMode == "added")
        {
            if (!File.Exists(target))
            {
                dllAction = "none"; // install never reached the DLL copy
            }
            else if (string.Equals(Sha256(target), state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
            {
                dllAction = "remove-owned";
            }
            else
            {
                throw new InvalidOperationException("Installed GRSP DLL changed outside the manager. Restore stopped without overwriting it.");
            }
        }
        else if (state.DllMode == "replaced")
        {
            if (string.IsNullOrWhiteSpace(state.DllBackupPath) || !File.Exists(state.DllBackupPath))
                throw new InvalidOperationException("Original DLL backup is missing.");
            if (!string.Equals(Sha256(state.DllBackupPath), state.OriginalDllHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original DLL backup failed verification.");

            if (!File.Exists(target))
            {
                throw new InvalidOperationException("Replaced GRSP DLL is missing. Restore stopped without guessing whether another tool removed it.");
            }

            var currentDllHash = Sha256(target);
            if (string.Equals(currentDllHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
                dllAction = "restore-original";
            else if (string.Equals(currentDllHash, state.OriginalDllHash, StringComparison.OrdinalIgnoreCase))
                dllAction = "leave-original"; // interrupted before replacement completed
            else
                throw new InvalidOperationException("Installed GRSP DLL changed outside the manager. Restore stopped without overwriting it.");
        }
        // preexisting-same is user-owned and is intentionally never deleted/replaced on restore.

        var scenarioAction = "none";
        if (state.ScenarioMode == "preexisting")
        {
            if (string.IsNullOrWhiteSpace(state.ScenarioBackupPath) || !File.Exists(state.ScenarioBackupPath))
                throw new InvalidOperationException("Original scenario backup is missing.");
            if (!string.Equals(Sha256(state.ScenarioBackupPath), state.OriginalScenarioHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original scenario backup failed verification.");
            if (!File.Exists(scenario))
                throw new InvalidOperationException("Pre-existing scenario file is now missing. Restore stopped without guessing.");

            var current = Sha256(scenario);
            if (string.Equals(current, state.OriginalScenarioHash, StringComparison.OrdinalIgnoreCase))
                scenarioAction = "leave-original";
            else if (!string.IsNullOrWhiteSpace(state.ManagedScenarioHash) &&
                     string.Equals(current, state.ManagedScenarioHash, StringComparison.OrdinalIgnoreCase))
                scenarioAction = "restore-original";
            else
                throw new InvalidOperationException("Scenario file changed outside the manager. Restore stopped without overwriting it.");
        }
        else if (state.ScenarioMode == "added" && File.Exists(scenario))
        {
            var current = Sha256(scenario);
            var expected = !string.IsNullOrWhiteSpace(state.ManagedScenarioHash)
                ? state.ManagedScenarioHash
                : state.InstalledDefaultScenarioHash;
            scenarioAction = !string.IsNullOrWhiteSpace(expected) &&
                             string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)
                ? "remove-owned"
                : "preserve-modified";
        }

        if (dllAction == "restore-original")
        {
            CopyFileVerified(state.DllBackupPath, target, overwrite: true);
            if (!string.Equals(Sha256(target), state.OriginalDllHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original DLL restoration failed verification.");
        }
        else if (dllAction == "remove-owned")
        {
            File.Delete(target);
        }

        if (state.DllMode == "replaced" && File.Exists(state.DllBackupPath))
            File.Delete(state.DllBackupPath);

        if (scenarioAction == "restore-original")
        {
            CopyFileVerified(state.ScenarioBackupPath, scenario, overwrite: true);
            if (!string.Equals(Sha256(scenario), state.OriginalScenarioHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original scenario restoration failed verification.");
        }
        else if (scenarioAction == "remove-owned")
        {
            File.Delete(scenario);
        }

        if (state.ScenarioMode == "preexisting" && File.Exists(state.ScenarioBackupPath))
            File.Delete(state.ScenarioBackupPath);

        File.Delete(statePath);

        return scenarioAction == "preserve-modified"
            ? "GRSP DLL restored/removed. Modified scenario file was preserved. Native RESULTS were left untouched."
            : "GRSP restored to its pre-manager DLL/scenario ownership state. Native RESULTS were left untouched.";
    }

    public static string CollectLatest(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        var source = LatestCompletedCapture(gameRoot)
            ?? throw new InvalidOperationException("No completed GRSP capture was found.");

        Directory.CreateDirectory(ArchiveResultsDirectory);
        var sourceFingerprint = DirectoryFingerprint(source);
        var baseName = Path.GetFileName(source);
        var destination = Path.Combine(ArchiveResultsDirectory, baseName);

        if (Directory.Exists(destination))
        {
            if (string.Equals(DirectoryFingerprint(destination), sourceFingerprint, StringComparison.OrdinalIgnoreCase))
                return destination;

            var i = 2;
            do destination = Path.Combine(ArchiveResultsDirectory, $"{baseName}_copy{i++}");
            while (Directory.Exists(destination));
        }

        var temp = Path.Combine(ArchiveResultsDirectory, $".copying-{Guid.NewGuid():N}");
        try
        {
            CopyTree(source, temp);
            if (!string.Equals(DirectoryFingerprint(temp), sourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Collected capture failed verification.");
            Directory.Move(temp, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    public static string? LatestCompletedCapture(string gameRoot)
    {
        var results = NativeResultsPath(gameRoot);
        if (!Directory.Exists(results)) return null;

        var latest = Path.Combine(results, "LATEST.txt");
        if (File.Exists(latest))
        {
            try
            {
                var raw = File.ReadAllText(latest).Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var path = Path.IsPathRooted(raw) ? raw : Path.Combine(results, raw);
                    if (IsCompletedCapture(path)) return Path.GetFullPath(path);
                }
            }
            catch { }
        }

        return Directory.EnumerateDirectories(results, "Capture_*", SearchOption.TopDirectoryOnly)
            .Where(IsCompletedCapture)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public static string StartCyberpunk(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        var exe = GameExe(gameRoot);
        Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true
        });
        return "Cyberpunk 2077 started. GRSP capture remains native: F11 starts, F11 stops.";
    }

    public static string NativeResultsPath(string gameRoot) =>
        Path.Combine(gameRoot, "red4ext", "plugins", "redscript_profiler_alpha", "RESULTS");

    public static string ScenarioPath(string gameRoot) =>
        Path.Combine(gameRoot, "red4ext", "plugins", "redscript_profiler_alpha", "RSP_Scenario.txt");

    private static bool IsCompletedCapture(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "GRSP_Summary.csv")) &&
        File.Exists(Path.Combine(path, "GRSP_Status.txt"));

    private static void EnsureInstallPreconditions(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before installing/updating GRSP.");
        if (!File.Exists(PayloadDll))
            throw new FileNotFoundException("Packaged GRSP DLL is missing.", PayloadDll);
        if (!File.Exists(PayloadScenario))
            throw new FileNotFoundException("Packaged RSP_Scenario.txt is missing.", PayloadScenario);
    }

    private static void EnsureValidRoot(string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot) || !Directory.Exists(gameRoot))
            throw new DirectoryNotFoundException("Cyberpunk 2077 game root does not exist.");
        if (!File.Exists(GameExe(gameRoot)))
            throw new InvalidOperationException(@"Cyberpunk2077.exe was not found under bin\x64.");
        if (!Directory.Exists(Path.Combine(gameRoot, "red4ext")))
            throw new InvalidOperationException("RED4ext was not found in the selected game root.");
    }

    private static string PluginDirectory(string gameRoot) => Path.Combine(gameRoot, "red4ext", "plugins");
    private static string TargetDll(string gameRoot) => Path.Combine(PluginDirectory(gameRoot), "redscript_profiler_alpha.dll");
    private static string StatePath(string gameRoot) => Path.Combine(PluginDirectory(gameRoot), StateFileName);
    private static string DllBackupPath(string gameRoot) => Path.Combine(PluginDirectory(gameRoot), "redscript_profiler_alpha.GRSPManager.ORIGINAL.dll");
    private static string ScenarioBackupPath(string gameRoot) => Path.Combine(Path.GetDirectoryName(ScenarioPath(gameRoot))!, "RSP_Scenario.GRSPManager.ORIGINAL.txt");
    private static string GameExe(string gameRoot) => Path.Combine(gameRoot, "bin", "x64", "Cyberpunk2077.exe");

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void CopyFileVerified(string source, string destination, bool overwrite = false)
    {
        var sourceHash = Sha256(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite);
        var copiedHash = Sha256(destination);
        if (!string.Equals(sourceHash, copiedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Backup/copy verification failed: {destination}");
    }

    private static void WriteTextVerified(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        var temp = path + ".grsp-writing";
        File.WriteAllBytes(temp, bytes);
        if (!File.ReadAllBytes(temp).SequenceEqual(bytes))
            throw new IOException("Scenario write verification failed.");
        File.Move(temp, path, true);
    }

    private static string DirectoryFingerprint(string root)
    {
        if (!Directory.Exists(root)) return "";
        var rows = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Path: path))
            .OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Relative, StringComparer.Ordinal)
            .Select(x => x.Relative + "\0" + Sha256(x.Path));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows)))).ToLowerInvariant();
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            CopyFileVerified(file, target);
        }
    }

    private static string SafeScenario(string value)
    {
        var chars = value.Trim().ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray();
        var clean = new string(chars).Trim('.', '_');
        if (string.IsNullOrWhiteSpace(clean))
            throw new ArgumentException("Scenario label is empty.");
        return clean;
    }

    private static string ReadScenarioLabel(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 0 && !x.StartsWith('#')) ?? "UNLABELED";
        }
        catch { return "UNREADABLE"; }
    }

    private static string ReadPayloadScenario() =>
        File.Exists(PayloadScenario) ? ReadScenarioLabel(PayloadScenario) : "WORLD";

    private static GrspManagerState LoadState(string path) =>
        JsonSerializer.Deserialize<GrspManagerState>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("GRSP Manager state file is invalid.");

    private static void SaveState(string path, GrspManagerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine;
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        _ = LoadState(temp); // parse verification before replace
        File.Move(temp, path, true);
    }
}

internal sealed class GrspManagerState
{
    public int FormatVersion { get; set; }
    public string PackageVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string DllMode { get; set; } = "";
    public string OriginalDllHash { get; set; } = "";
    public string InstalledDllHash { get; set; } = "";
    public string DllBackupPath { get; set; } = "";
    public string ScenarioMode { get; set; } = "";
    public string OriginalScenarioHash { get; set; } = "";
    public string ScenarioBackupPath { get; set; } = "";
    public string InstalledDefaultScenarioHash { get; set; } = "";
    public string ManagedScenarioHash { get; set; } = "";
}

internal sealed class StatusInfo
{
    public string GameRoot { get; set; } = "";
    public bool GameRootValid { get; set; }
    public bool Red4extPresent { get; set; }
    public bool PayloadPresent { get; set; }
    public string PayloadHash { get; set; } = "";
    public bool DllPresent { get; set; }
    public string InstalledHash { get; set; } = "";
    public bool ManagedStatePresent { get; set; }
    public string ManagedInstalledHash { get; set; } = "";
    public string OriginalDllMode { get; set; } = "";
    public bool ScenarioPresent { get; set; }
    public string ScenarioOwnership { get; set; } = "";
    public string Scenario { get; set; } = "";
    public string NativeResultsPath { get; set; } = "";
    public string LatestCapture { get; set; } = "";
    public int CompletedCaptureCount { get; set; }
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
}

internal sealed record InstallResult(string Mode, string DllPath, string ScenarioPath, string InstalledHash);
