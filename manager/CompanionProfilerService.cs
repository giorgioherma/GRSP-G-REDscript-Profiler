using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal sealed record CompanionProfilerStatus(
    bool Enabled,
    bool ExeFound,
    string Kind,
    string DisplayName,
    string StartKey,
    bool StartKeyKnown,
    bool StartKeyIsF11,
    string? SuggestedResultsDirectory,
    string Message);

internal sealed record CompanionCollectResult(
    bool Copied,
    int FileCount,
    string Message,
    string? Source,
    string? Destination,
    string? Fingerprint);

internal static class CompanionProfilerService
{
    private const string CapFrameXWebsite = "https://github.com/CXWorld/CapFrameX/releases";

    public static string RecommendedProfilerWebsite => CapFrameXWebsite;

    public static CompanionProfilerStatus Inspect(AppSettings settings)
    {
        if (!settings.PairFrameTimeProfiler)
            return new(false, false, "disabled", "None", "-", false, false, null,
                "Optional frame-time pairing is disabled.");

        var exe = settings.ExternalProfilerExe.Trim();
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return new(true, false, "unknown", "Not linked", "UNKNOWN", false, false, null,
                "Select a frame-time profiler executable, or continue without one.");

        var kind = DetectKind(exe);
        if (kind == "capframex")
        {
            var (captures, config) = DetectCapFrameXPaths(exe);
            var key = ReadCapFrameXStartKey(config);
            return new(
                true,
                true,
                "capframex",
                "CapFrameX",
                key ?? "UNKNOWN",
                !string.IsNullOrWhiteSpace(key),
                string.Equals(key, "F11", StringComparison.OrdinalIgnoreCase),
                captures,
                key is null
                    ? "CapFrameX detected. Capture key could not be read; verify F11 manually."
                    : string.Equals(key, "F11", StringComparison.OrdinalIgnoreCase)
                        ? "CapFrameX detected. Capture START key is F11."
                        : $"CapFrameX detected. Capture START key is {key}; use F11 for synchronized starts.");
        }

        var display = Path.GetFileNameWithoutExtension(exe);
        return new(
            true,
            true,
            "custom",
            string.IsNullOrWhiteSpace(display) ? "Custom profiler" : display,
            "UNKNOWN",
            false,
            false,
            null,
            "Custom profiler detected. Its capture key format is unknown; verify F11 manually.");
    }

    public static CompanionCollectResult CollectLatest(AppSettings settings, string grspDestination)
    {
        if (!settings.PairFrameTimeProfiler)
            return new(false, 0, "Frame-time companion disabled.", null, null, null);

        var sourceRoot = settings.ExternalResultsDirectory.Trim();
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return new(false, 0, "Frame-time results folder is not configured or does not exist.", null, null, null);

        var sourceFull = Path.GetFullPath(sourceRoot);
        var destinationFull = Path.GetFullPath(grspDestination);
        if (PathsOverlap(sourceFull, destinationFull))
            return new(false, 0,
                "Frame-time results folder overlaps the GRSP archive location; companion copy was skipped to prevent recursive/self-copy.",
                null, null, null);

        var status = Inspect(settings);
        var candidate = status.Kind == "capframex"
            ? FindNewestCapFrameXCapture(sourceRoot)
            : FindNewestTopLevelItem(sourceRoot);

        if (candidate is null)
            return new(false, 0, "No frame-time result was found in the selected results folder.", null, null, null);

        var fingerprint = FingerprintItem(candidate);
        if (string.Equals(settings.LastCollectedExternalSource, candidate, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.LastCollectedExternalFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            WriteManifest(grspDestination, status, sourceRoot, candidate, null, 0, fingerprint,
                "No new external capture detected; previous source was not copied again.");
            return new(false, 0,
                "No new frame-time capture detected; GRSP results were collected normally.",
                candidate, null, fingerprint);
        }

        var frameTimeRoot = Path.Combine(grspDestination, "FrameTime");
        Directory.CreateDirectory(frameTimeRoot);

        string target;
        int count;
        if (File.Exists(candidate))
        {
            target = UniquePath(frameTimeRoot, Path.GetFileName(candidate));
            CopyFileVerified(candidate, target);
            count = 1;
        }
        else
        {
            target = UniquePath(
                frameTimeRoot,
                Path.GetFileName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            count = CopyDirectoryVerified(candidate, target);
        }

        WriteManifest(
            grspDestination,
            status,
            sourceRoot,
            candidate,
            target,
            count,
            fingerprint,
            "Copied. External source was left untouched.");

        settings.LastCollectedExternalSource = candidate;
        settings.LastCollectedExternalFingerprint = fingerprint;
        settings.Save();

        return new(
            true,
            count,
            $"Copied frame-time companion result ({count} file{(count == 1 ? "" : "s")}). External originals were left untouched.",
            candidate,
            target,
            fingerprint);
    }

    public static string? SuggestResultsDirectory(string exePath)
    {
        if (!File.Exists(exePath) || DetectKind(exePath) != "capframex")
            return null;

        return DetectCapFrameXPaths(exePath).Captures;
    }

    private static string DetectKind(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        if (name.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase))
            return "capframex";

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if ((info.ProductName?.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (info.FileDescription?.Contains("CapFrameX", StringComparison.OrdinalIgnoreCase) ?? false))
                return "capframex";
        }
        catch { }

        return "custom";
    }

    private static (string? Captures, string? Config) DetectCapFrameXPaths(string exePath)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        string? captures = null;
        string? config = null;
        var portable = Path.Combine(baseDir, "portable.json");

        if (File.Exists(portable))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(portable));
                if (doc.RootElement.TryGetProperty("paths", out var paths))
                {
                    if (paths.TryGetProperty("captures", out var c) && c.ValueKind == JsonValueKind.String)
                        captures = ResolveRelative(baseDir, c.GetString());
                    if (paths.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.String)
                        config = ResolveRelative(baseDir, cfg.GetString());
                }
            }
            catch { }
        }

        captures ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CapFrameX",
            "Captures");
        config ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CapFrameX",
            "Configuration");

        return (captures, config);
    }

    private static string? ResolveRelative(string baseDir, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Path.GetFullPath(Path.IsPathRooted(value)
            ? value
            : Path.Combine(baseDir, value));
    }

    private static string? ReadCapFrameXStartKey(string? configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            return null;

        var settingsPath = Path.Combine(configDirectory, "AppSettings.json");
        if (!File.Exists(settingsPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (TryFindStringProperty(doc.RootElement, "CaptureHotKey", out var key))
                return key;
        }
        catch { }

        return null;
    }

    private static bool TryFindStringProperty(JsonElement element, string propertyName, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    value = prop.Value.GetString();
                    return !string.IsNullOrWhiteSpace(value);
                }

                if (TryFindStringProperty(prop.Value, propertyName, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindStringProperty(item, propertyName, out value))
                    return true;
            }
        }

        value = null;
        return false;
    }

    private static string? FindNewestCapFrameXCapture(string root) =>
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("portable.json", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static string? FindNewestTopLevelItem(string root)
    {
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Stamp: File.GetLastWriteTimeUtc(path)))
            .Concat(
                Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    .Select(path => (Path: path, Stamp: LatestWriteUtc(path))))
            .OrderByDescending(x => x.Stamp)
            .ToList();

        return candidates.FirstOrDefault().Path;
    }

    private static DateTime LatestWriteUtc(string directory)
    {
        var latest = Directory.GetLastWriteTimeUtc(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (stamp > latest)
                latest = stamp;
        }

        return latest;
    }

    private static string FingerprintItem(string path)
    {
        if (File.Exists(path))
            return Sha256(path);

        if (!Directory.Exists(path))
            return "";

        var rows = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(path, file).Replace('\\', '/') + "\0" + Sha256(file))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        var material = string.Join("\n", rows);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static void CopyFileVerified(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var expected = Sha256(source);
        File.Copy(source, target, false);

        if (!string.Equals(expected, Sha256(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(target);
            throw new IOException($"External result copy verification failed: {source}");
        }
    }

    private static int CopyDirectoryVerified(string source, string target)
    {
        Directory.CreateDirectory(target);
        var count = 0;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            CopyFileVerified(file, Path.Combine(target, relative));
            count++;
        }

        if (!string.Equals(FingerprintItem(source), FingerprintItem(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(target, true);
            throw new IOException($"External result directory verification failed: {source}");
        }

        return count;
    }

    private static bool PathsOverlap(string left, string right)
    {
        static string WithSeparator(string path) =>
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        var a = WithSeparator(Path.GetFullPath(left));
        var b = WithSeparator(Path.GetFullPath(right));

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string UniquePath(string parent, string name)
    {
        var candidate = Path.Combine(parent, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var i = 1;
        do
        {
            candidate = Path.Combine(parent, $"{stem}-{i++}{ext}");
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static void WriteManifest(
        string grspDestination,
        CompanionProfilerStatus status,
        string resultsRoot,
        string source,
        string? copiedTo,
        int fileCount,
        string fingerprint,
        string note)
    {
        var frameTimeRoot = Path.Combine(grspDestination, "FrameTime");
        Directory.CreateDirectory(frameTimeRoot);

        var manifest = new
        {
            collectedUtc = DateTime.UtcNow.ToString("O"),
            profiler = status.DisplayName,
            kind = status.Kind,
            configuredResultsDirectory = resultsRoot,
            detectedStartKey = status.StartKey,
            startKeyKnown = status.StartKeyKnown,
            startKeyIsF11 = status.StartKeyIsF11,
            source,
            copiedTo,
            fileCount,
            sourceFingerprint = fingerprint,
            sourceDeleted = false,
            note
        };

        File.WriteAllText(
            Path.Combine(frameTimeRoot, "CompanionManifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine);
    }
}
