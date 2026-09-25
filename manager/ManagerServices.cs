using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static class ManagerServices
{
    public const string ProductVersion = "1.0.0";
    public const string PluginFileName = "G-REDscript-Profiler.dll";
    public const string LegacyPluginFileName = "redscript_profiler_alpha.dll";
    public const string DataFolderName = "G-REDscript-Profiler";
    public const string CaptureTitleFileName = "CaptureTitle.txt";
    public const string StateFileName = ".manager_state.json";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string PackageRoot => PackagePaths.PackageRoot;
    public static string PayloadDirectory => Path.Combine(PackageRoot, "payload");
    public static string PayloadDll => Path.Combine(PayloadDirectory, PluginFileName);
    public static string PayloadCaptureTitle => Path.Combine(PayloadDirectory, CaptureTitleFileName);
    public static string ArchiveResultsDirectory => Path.Combine(PackageRoot, "RESULTS");

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

        status.GameRootValid = File.Exists(GameExe(gameRoot));
        status.Red4extPresent = Directory.Exists(Path.Combine(gameRoot, "red4ext"));
        status.PayloadPresent = File.Exists(PayloadDll);
        status.PayloadHash = status.PayloadPresent ? Sha256(PayloadDll) : "";

        var target = TargetDll(gameRoot);
        var legacyTarget = LegacyTargetDll(gameRoot);
        var statePath = StatePath(gameRoot);
        status.LegacyDllPresent = File.Exists(legacyTarget);
        status.DllPresent = File.Exists(target);
        status.InstalledHash = status.DllPresent ? Sha256(target) : "";
        status.DllMatchesCurrentPackage =
            status.DllPresent &&
            status.PayloadPresent &&
            string.Equals(status.InstalledHash, status.PayloadHash, StringComparison.OrdinalIgnoreCase);
        status.ManagedStatePresent = File.Exists(statePath);

        GrspManagerState? state = null;
        if (status.ManagedStatePresent)
        {
            try
            {
                state = LoadState(statePath);
            }
            catch (Exception ex)
            {
                status.State = "INVALID_MANAGED_STATE";
                status.Message = "GRSP manager state is invalid, but the ownership marker is present and RESTORE ORIGINAL STATE remains available: " + ex.Message;
            }
        }

        if (state is not null)
        {
            status.ManagedInstalledHash = state.InstalledDllHash;
            if (!status.DllPresent)
            {
                status.State = "MANAGED_DLL_MISSING";
                status.Message = "This package has managed state, but G-REDscript-Profiler.dll is missing. RESTORE ORIGINAL STATE remains available and will clean the managed scope.";
            }
            else if (!string.Equals(status.InstalledHash, state.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
            {
                status.State = "MANAGED_DLL_CHANGED";
                status.Message = "The managed G-REDscript-Profiler.dll changed after installation. RESTORE ORIGINAL STATE remains authoritative and will remove the managed profiler scope.";
            }
            else if (status.DllMatchesCurrentPackage)
            {
                status.State = "INSTALLED_CURRENT";
                status.Message = "G-REDscript Profiler is installed and ready.";
            }
            else
            {
                status.State = "INSTALLED_OTHER_PACKAGE";
                status.Message = "A different managed G-REDscript Profiler build is installed. Restore it before installing this package.";
            }
        }
        else if (status.DllPresent)
        {
            if (status.DllMatchesCurrentPackage)
            {
                status.State = "PREEXISTING_CURRENT";
                status.Message = "This exact G-REDscript Profiler build is already installed. Nothing will be installed over it; you can run the profiler.";
            }
            else
            {
                status.State = "INSTALLED_OTHER_VERSION";
                status.Message = "A version of G-REDscript Profiler is already installed. Installation is blocked so it is never overwritten.";
            }
        }
        else if (string.IsNullOrEmpty(status.State))
        {
            if (status.LegacyDllPresent)
            {
                status.State = "LEGACY_GRSP_PRESENT";
                status.Message = "A legacy redscript_profiler_alpha.dll installation was detected. Restore/remove it before installing G-REDscript Profiler so two profiler DLLs cannot load together.";
            }
            else if (DirectoryHasEntries(DataDirectory(gameRoot)))
            {
                status.State = "STALE_DATA";
                status.Message = "The G-REDscript-Profiler data folder contains files from another or incomplete installation. It will not be overwritten.";
            }
            else
            {
                status.State = "NOT_INSTALLED";
                status.Message = "G-REDscript Profiler is not installed.";
            }
        }

        var titlePath = CaptureTitlePath(gameRoot);
        status.CaptureTitlePresent = File.Exists(titlePath);
        status.CaptureTitle = status.CaptureTitlePresent
            ? ReadCaptureTitle(titlePath)
            : ReadPayloadCaptureTitle();

        status.NativeResultsPath = NativeResultsPath(gameRoot);
        status.LatestCapture = LatestCompletedCapture(gameRoot) ?? "";
        status.CompletedCaptureCount = Directory.Exists(status.NativeResultsPath)
            ? Directory.EnumerateDirectories(status.NativeResultsPath, "Capture_*", SearchOption.TopDirectoryOnly)
                .Count(IsCompletedCapture)
            : 0;

        return status;
    }

    public static InstallResult Install(string gameRoot)
    {
        EnsureInstallPreconditions(gameRoot);

        var target = TargetDll(gameRoot);
        var dataDir = DataDirectory(gameRoot);
        var statePath = StatePath(gameRoot);

        if (File.Exists(LegacyTargetDll(gameRoot)))
            throw new InvalidOperationException(
                "A legacy redscript_profiler_alpha.dll installation was detected. Restore/remove it first. G-REDscript Profiler will never install beside an older profiler DLL.");

        if (File.Exists(target))
        {
            var installedHash = Sha256(target);
            var packagedHash = Sha256(PayloadDll);
            if (string.Equals(installedHash, packagedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "This exact G-REDscript Profiler build is already installed. Nothing was overwritten. You can run the profiler.");

            throw new InvalidOperationException(
                "A version of G-REDscript Profiler is already installed. Restore/remove that installation first. This installer never overwrites an existing profiler DLL.");
        }

        if (File.Exists(statePath))
            throw new InvalidOperationException("A previous GRSP manager state remains. Use Restore Original State before reinstalling.");

        if (DirectoryHasEntries(dataDir))
            throw new InvalidOperationException(
                "The G-REDscript-Profiler data folder is not empty. Installation stopped before changing anything.");

        Directory.CreateDirectory(dataDir);

        var dllHash = Sha256(PayloadDll);
        var titleHash = Sha256(PayloadCaptureTitle);
        var state = new GrspManagerState
        {
            FormatVersion = 2,
            PackageVersion = ProductVersion,
            CreatedUtc = DateTime.UtcNow,
            InstalledDllHash = dllHash,
            InstalledCaptureTitleHash = titleHash,
            ManagedCaptureTitleHash = titleHash
        };

        // Record ownership before adding the first managed game file. If installation
        // is interrupted, Restore can safely remove only this profiler-owned scope.
        SaveState(statePath, state);

        if (File.Exists(target))
            throw new InvalidOperationException("G-REDscript-Profiler.dll appeared during installation; refusing to overwrite it.");

        CopyFileVerified(PayloadDll, target);

        var titlePath = CaptureTitlePath(gameRoot);
        if (File.Exists(titlePath))
            throw new InvalidOperationException("CaptureTitle.txt appeared during installation; refusing to overwrite it.");

        CopyFileVerified(PayloadCaptureTitle, titlePath);
        Directory.CreateDirectory(NativeResultsPath(gameRoot));

        return new InstallResult("installed", target, dataDir, dllHash);
    }

    // Compatibility alias for the first manager preview and TOTAL prototypes.
    public static InstallResult InstallOrUpdate(string gameRoot) => Install(gameRoot);

    public static string SaveCaptureTitle(string gameRoot, string captureTitle)
    {
        EnsureValidRoot(gameRoot);

        if (!File.Exists(TargetDll(gameRoot)))
            throw new InvalidOperationException("G-REDscript Profiler is not installed.");

        if (!File.Exists(PayloadDll) ||
            !string.Equals(Sha256(TargetDll(gameRoot)), Sha256(PayloadDll), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Capture title editing is available only for the exact profiler build bundled with this manager.");

        var path = CaptureTitlePath(gameRoot);
        if (!File.Exists(path))
            throw new InvalidOperationException("CaptureTitle.txt is missing from the installed profiler data folder.");

        var clean = SafeCaptureTitle(captureTitle);
        var text = clean + Environment.NewLine;
        WriteTextVerified(path, text);

        var statePath = StatePath(gameRoot);
        if (File.Exists(statePath))
        {
            var state = LoadState(statePath);
            state.ManagedCaptureTitleHash = Sha256(path);
            SaveState(statePath, state);
        }

        return clean;
    }

    // Legacy headless alias. The UI now calls this value a capture title.
    public static string SaveScenario(string gameRoot, string scenario) =>
        SaveCaptureTitle(gameRoot, scenario);

    public static string Restore(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before restoring G-REDscript Profiler.");

        var statePath = StatePath(gameRoot);
        if (!File.Exists(statePath))
            return "No managed G-REDscript Profiler installation was found. Nothing was changed.";

        // The state file itself is the ownership marker. Install refuses to begin
        // when either the profiler DLL already exists or the data folder is non-empty,
        // so once this marker exists the DLL path and data folder are G-REDscript's
        // managed scope. Restore must therefore be the unconditional exit path:
        // changed/missing DLLs, changed capture metadata, runtime output, or even an
        // unreadable state JSON must never trap the user in an installed state.
        string? archived = null;
        string? archiveWarning = null;

        try
        {
            archived = CollectLiveResultsInternal(gameRoot, requireCompletedCapture: false);
        }
        catch (Exception ex)
        {
            archiveWarning = ex.Message;

            // Best-effort preservation of the whole managed data folder before
            // cleanup. Failure here is reported, but it does not block restore.
            try
            {
                var dataDir = DataDirectory(gameRoot);
                if (DirectoryHasEntries(dataDir))
                {
                    Directory.CreateDirectory(ArchiveResultsDirectory);
                    var recovery = UniqueDirectory(
                        ArchiveResultsDirectory,
                        $"RestoreRecovery_{DateTime.Now:yyyyMMdd-HHmmss}");
                    CopyDirectoryVerified(dataDir, recovery);
                    archived = recovery;
                }
            }
            catch (Exception recoveryEx)
            {
                archiveWarning += " Recovery copy also failed: " + recoveryEx.Message;
            }
        }

        var target = TargetDll(gameRoot);
        DeleteFileForce(target);

        // This directory was empty/absent before our managed install, so the manager
        // owns its contents. Restore it to that original empty state regardless of
        // what changed inside the managed folder while profiling.
        var managedDataDir = DataDirectory(gameRoot);
        DeleteDirectoryContentsForce(managedDataDir);
        Directory.CreateDirectory(managedDataDir);

        if (File.Exists(target))
            throw new IOException("Restore could not remove G-REDscript-Profiler.dll. Close any process locking the file and run RESTORE ORIGINAL STATE again.");

        if (File.Exists(statePath) || DirectoryHasEntries(managedDataDir))
            throw new IOException("Restore could not clear the managed G-REDscript-Profiler data folder. Close any process locking those files and run RESTORE ORIGINAL STATE again.");

        var message = string.IsNullOrWhiteSpace(archived)
            ? "G-REDscript Profiler removed. The empty G-REDscript-Profiler data folder was intentionally left in place."
            : "G-REDscript Profiler removed. Remaining live profiler output was archived to: " + archived +
              ". The empty G-REDscript-Profiler data folder was intentionally left in place.";

        if (!string.IsNullOrWhiteSpace(archiveWarning))
            message += " Note: normal live-result archiving reported: " + archiveWarning;

        return message;
    }

    public static string CollectLatest(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before collecting results so the live profiler folder can be moved cleanly.");
        return CollectLiveResultsInternal(gameRoot, requireCompletedCapture: true)
            ?? throw new InvalidOperationException("No completed G-REDscript Profiler capture was found.");
    }

    private static string? CollectLiveResultsInternal(string gameRoot, bool requireCompletedCapture)
    {
        var results = NativeResultsPath(gameRoot);
        if (!Directory.Exists(results) || !Directory.EnumerateFileSystemEntries(results).Any())
        {
            if (requireCompletedCapture)
                throw new InvalidOperationException("No completed G-REDscript Profiler capture was found.");
            return null;
        }

        Directory.CreateDirectory(ArchiveResultsDirectory);

        var captures = Directory.EnumerateDirectories(results, "Capture_*", SearchOption.TopDirectoryOnly)
            .Where(IsCompletedCapture)
            .OrderBy(Directory.GetLastWriteTimeUtc)
            .ToList();

        if (captures.Count == 0 && requireCompletedCapture)
            throw new InvalidOperationException("No completed G-REDscript Profiler capture was found. Live files were left untouched.");

        string? latestDestination = null;
        foreach (var source in captures)
            latestDestination = ArchiveDirectoryAndRemoveSource(source);

        var leftovers = Directory.EnumerateFileSystemEntries(results).ToList();
        if (leftovers.Count > 0)
        {
            if (latestDestination is null)
            {
                latestDestination = UniqueDirectory(
                    ArchiveResultsDirectory,
                    $"RecoveredLiveOutput_{DateTime.Now:yyyyMMdd-HHmmss}");
                Directory.CreateDirectory(latestDestination);
            }

            var metadataRoot = Path.Combine(latestDestination, "Data", "Metadata");
            Directory.CreateDirectory(metadataRoot);

            foreach (var entry in leftovers)
            {
                var target = UniquePath(metadataRoot, Path.GetFileName(entry));
                if (File.Exists(entry))
                {
                    CopyFileVerified(entry, target);
                    File.Delete(entry);
                }
                else if (Directory.Exists(entry))
                {
                    CopyDirectoryVerified(entry, target);
                    Directory.Delete(entry, true);
                }
            }
        }

        if (Directory.Exists(results) && !Directory.EnumerateFileSystemEntries(results).Any())
            Directory.Delete(results);

        return latestDestination;
    }

    private static string ArchiveDirectoryAndRemoveSource(string source)
    {
        var fingerprint = DirectoryFingerprint(source);
        var baseName = Path.GetFileName(
            source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var destination = Path.Combine(ArchiveResultsDirectory, baseName);

        if (Directory.Exists(destination))
            destination = UniqueDirectory(ArchiveResultsDirectory, baseName);

        var temp = Path.Combine(ArchiveResultsDirectory, $".collecting-{Guid.NewGuid():N}");
        try
        {
            CopyDirectoryVerified(source, temp);
            if (!string.Equals(DirectoryFingerprint(temp), fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Collected capture failed verification.");

            // Only reorganize after the exact source copy has been verified.
            // The live capture remains untouched until the normalized archive is complete.
            NormalizeArchiveLayout(temp);

            Directory.Move(temp, destination);
            Directory.Delete(source, true);
            return destination;
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }

    private static void NormalizeArchiveLayout(string captureRoot)
    {
        var dataRoot = Path.Combine(captureRoot, "Data");
        var runtimeRoot = Path.Combine(dataRoot, "Runtime");
        var developerRoot = Path.Combine(dataRoot, "Developer");
        var metadataRoot = Path.Combine(dataRoot, "Metadata");

        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(developerRoot);
        Directory.CreateDirectory(metadataRoot);

        var developer = Path.Combine(captureRoot, "Developer");
        if (Directory.Exists(developer))
        {
            foreach (var file in Directory.EnumerateFiles(developer, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(developer, file);
                var target = Path.Combine(developerRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target, false);
            }

            Directory.Delete(developer, true);
        }

        foreach (var file in Directory.EnumerateFiles(captureRoot, "*", SearchOption.TopDirectoryOnly).ToList())
        {
            var name = Path.GetFileName(file);

            // These are the two stable front-door handoff files generated by
            // the manager after collection. Preserve them if rebuilding an
            // already processed capture.
            if (name.Equals(ResultReportService.ReportFileName, StringComparison.OrdinalIgnoreCase) ||
                name.Equals(ResultReportService.SummaryFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = ResultReportService.GetArchiveRelativePath(name);
            var target = Path.Combine(captureRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, false);
        }
    }

    public static string? LatestCompletedCapture(string gameRoot)
    {
        var results = NativeResultsPath(gameRoot);
        if (!Directory.Exists(results))
            return null;

        var latest = Path.Combine(results, "LATEST.txt");
        if (File.Exists(latest))
        {
            try
            {
                var raw = File.ReadLines(latest).FirstOrDefault()?.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var path = Path.IsPathRooted(raw) ? raw : Path.Combine(results, raw);
                    if (IsCompletedCapture(path))
                        return Path.GetFullPath(path);
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

        return "Cyberpunk 2077 started. F11 starts the G-REDscript Profiler capture; F11 again stops it.";
    }

    public static string DataDirectory(string gameRoot) =>
        Path.Combine(PluginDirectory(gameRoot), DataFolderName);

    public static string NativeResultsPath(string gameRoot) =>
        Path.Combine(DataDirectory(gameRoot), "RESULTS");

    public static string CaptureTitlePath(string gameRoot) =>
        Path.Combine(DataDirectory(gameRoot), CaptureTitleFileName);

    // Compatibility alias for older manager callers.
    public static string ScenarioPath(string gameRoot) => CaptureTitlePath(gameRoot);

    private static bool IsCompletedCapture(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "GRSP_Summary.csv")) &&
        File.Exists(Path.Combine(path, "GRSP_Status.txt"));

    private static void EnsureInstallPreconditions(string gameRoot)
    {
        EnsureValidRoot(gameRoot);
        if (IsGameRunning())
            throw new InvalidOperationException("Close Cyberpunk 2077 before installing G-REDscript Profiler.");
        if (!File.Exists(PayloadDll))
            throw new FileNotFoundException("Packaged G-REDscript-Profiler.dll is missing.", PayloadDll);
        if (!File.Exists(PayloadCaptureTitle))
            throw new FileNotFoundException("Packaged CaptureTitle.txt is missing.", PayloadCaptureTitle);
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

    private static string PluginDirectory(string gameRoot) =>
        Path.Combine(gameRoot, "red4ext", "plugins");

    private static string TargetDll(string gameRoot) =>
        Path.Combine(PluginDirectory(gameRoot), PluginFileName);

    private static string LegacyTargetDll(string gameRoot) =>
        Path.Combine(PluginDirectory(gameRoot), LegacyPluginFileName);

    private static string StatePath(string gameRoot) =>
        Path.Combine(DataDirectory(gameRoot), StateFileName);

    private static string GameExe(string gameRoot) =>
        Path.Combine(gameRoot, "bin", "x64", "Cyberpunk2077.exe");

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void CopyFileVerified(string source, string destination)
    {
        var sourceHash = Sha256(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
        var copiedHash = Sha256(destination);
        if (!string.Equals(sourceHash, copiedHash, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new IOException($"Copy verification failed: {destination}");
        }
    }

    private static void CopyDirectoryVerified(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            CopyFileVerified(file, target);
        }

        if (!string.Equals(DirectoryFingerprint(source), DirectoryFingerprint(destination), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(destination, true);
            throw new IOException($"Directory copy verification failed: {source}");
        }
    }

    private static void WriteTextVerified(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        var temp = path + ".grsp-writing";
        File.WriteAllBytes(temp, bytes);
        if (!File.ReadAllBytes(temp).SequenceEqual(bytes))
        {
            File.Delete(temp);
            throw new IOException("Capture title write verification failed.");
        }
        File.Move(temp, path, true);
    }

    private static string DirectoryFingerprint(string root)
    {
        if (!Directory.Exists(root))
            return "";

        var rows = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Relative: Path.GetRelativePath(root, path).Replace('\\', '/'), Path: path))
            .OrderBy(x => x.Relative, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Relative, StringComparer.Ordinal)
            .Select(x => x.Relative + "\0" + Sha256(x.Path));

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", rows))))
            .ToLowerInvariant();
    }

    private static string SafeCaptureTitle(string value)
    {
        var chars = value.Trim().ToUpperInvariant()
            .Take(48)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' ? ch : '_')
            .ToArray();

        var clean = new string(chars).Trim('.', '_');
        if (string.IsNullOrWhiteSpace(clean))
            throw new ArgumentException("Capture title is empty.");

        return clean;
    }

    private static string ReadCaptureTitle(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(x => x.Trim())
                .FirstOrDefault(x => x.Length > 0 && !x.StartsWith('#')) ?? "UNLABELED";
        }
        catch
        {
            return "UNREADABLE";
        }
    }

    private static string ReadPayloadCaptureTitle() =>
        File.Exists(PayloadCaptureTitle) ? ReadCaptureTitle(PayloadCaptureTitle) : "WORLD";

    private static bool DirectoryHasEntries(string path) =>
        Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();

    private static void DeleteFileForce(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // Deletion below remains authoritative; attribute cleanup is best effort.
        }

        File.Delete(path);
    }

    private static void DeleteDirectoryContentsForce(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // Directory deletion below will report any real filesystem lock.
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length))
        {
            try
            {
                File.SetAttributes(directory, FileAttributes.Normal);
            }
            catch
            {
                // Best effort only.
            }
        }

        DeleteDirectoryContents(path);
    }

    private static void DeleteDirectoryContents(string path)
    {
        if (!Directory.Exists(path))
            return;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            DeleteFileForce(file);

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
            Directory.Delete(directory, true);
    }

    private static string UniqueDirectory(string parent, string baseName)
    {
        var candidate = Path.Combine(parent, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;

        var i = 2;
        do
        {
            candidate = Path.Combine(parent, $"{baseName}_copy{i++}");
        }
        while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
    }

    private static string UniquePath(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var i = 2;
        do
        {
            candidate = Path.Combine(parent, $"{stem}_copy{i++}{ext}");
        }
        while (Directory.Exists(candidate) || File.Exists(candidate));

        return candidate;
    }

    private static GrspManagerState LoadState(string path) =>
        JsonSerializer.Deserialize<GrspManagerState>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("G-REDscript Profiler manager state file is invalid.");

    private static void SaveState(string path, GrspManagerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(state, JsonOptions) + Environment.NewLine;
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        _ = LoadState(temp);
        File.Move(temp, path, true);
    }
}

internal sealed class GrspManagerState
{
    public int FormatVersion { get; set; }
    public string PackageVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string InstalledDllHash { get; set; } = "";
    public string InstalledCaptureTitleHash { get; set; } = "";
    public string ManagedCaptureTitleHash { get; set; } = "";
}

internal sealed class StatusInfo
{
    public string GameRoot { get; set; } = "";
    public bool GameRootValid { get; set; }
    public bool Red4extPresent { get; set; }
    public bool PayloadPresent { get; set; }
    public string PayloadHash { get; set; } = "";
    public bool DllPresent { get; set; }
    public bool LegacyDllPresent { get; set; }
    public string InstalledHash { get; set; } = "";
    public bool DllMatchesCurrentPackage { get; set; }
    public bool ManagedStatePresent { get; set; }
    public string ManagedInstalledHash { get; set; } = "";
    public bool CaptureTitlePresent { get; set; }
    public string CaptureTitle { get; set; } = "";
    public string NativeResultsPath { get; set; } = "";
    public string LatestCapture { get; set; } = "";
    public int CompletedCaptureCount { get; set; }
    public string State { get; set; } = "";
    public string Message { get; set; } = "";
}

internal sealed record InstallResult(
    string Mode,
    string DllPath,
    string DataDirectory,
    string InstalledHash);
