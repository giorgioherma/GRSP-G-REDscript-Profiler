using System.Text.RegularExpressions;

namespace GRedscriptProfiler.Manager;

internal static partial class ResultReportService
{
    private static List<SourceIntegrationMetric> AnalyzeSourceIntegrations(
        IReadOnlyList<OwnerMetric> owners,
        IReadOnlyList<FunctionMetric> functions)
    {
        var result = new List<SourceIntegrationMetric>();
        var ownerNames = owners.Select(x => x.Owner)
            .Concat(functions.Select(x => x.Owner))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var owner in ownerNames)
        {
            var sourcePaths = functions
                .Where(x => x.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.SourcePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var roots = ResolveOwnerRoots(sourcePaths).ToList();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                try
                {
                    if (File.Exists(root) && root.EndsWith(".reds", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(root);
                    }
                    else if (Directory.Exists(root))
                    {
                        foreach (var path in Directory.EnumerateFiles(root, "*.reds", SearchOption.AllDirectories))
                            files.Add(path);
                    }
                }
                catch
                {
                    // Source scanning is an interpretation aid only. A locked,
                    // moved or deleted source tree must never break report generation.
                }
            }

            // Fallback to the exact source files emitted by GRSP when a complete
            // owner root could not be recovered.
            foreach (var path in sourcePaths)
            {
                try
                {
                    if (File.Exists(path))
                        files.Add(path);
                }
                catch { }
            }

            if (files.Count == 0)
            {
                result.Add(new SourceIntegrationMetric
                {
                    Owner = owner,
                    SourceAvailable = false
                });
                continue;
            }

            var combined = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                try
                {
                    combined.AppendLine(File.ReadAllText(file));
                }
                catch
                {
                    // Keep scanning other source files.
                }
            }

            var text = combined.ToString();
            if (text.Length == 0)
            {
                result.Add(new SourceIntegrationMetric
                {
                    Owner = owner,
                    SourceAvailable = false,
                    FilesScanned = files.Count
                });
                continue;
            }

            var services = new List<string>();
            AddIfPresent(services, text, "Scheduler", "GetScheduler", "ScheduledJob", "RegisterJob", "UnregisterJob");
            AddIfPresent(services, text, "StateCache", "GetStateCache");
            AddIfPresent(services, text, "ContextService", "GetContextService");
            AddIfPresent(services, text, "InputHub", "GetInputHub");
            AddIfPresent(services, text, "EventBus", "GetEventBus");
            AddIfPresent(services, text, "HookBus", "GetHookBus");
            AddIfPresent(services, text, "DirtyFlags", "GetDirtyFlags");

            var hasHotpathCache =
                text.Contains("GRedHotpathCache", StringComparison.OrdinalIgnoreCase) ||
                files.Any(file => Path.GetFileName(file)
                    .Contains("GRedHotpathCache", StringComparison.OrdinalIgnoreCase));
            if (hasHotpathCache)
                services.Add("Hotpath cache");

            var referencesRuntime =
                text.Contains("GRedRuntime", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("G-RedRuntime", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("G-REDruntime", StringComparison.OrdinalIgnoreCase) ||
                services.Count > 0;

            var version = "";
            if (IsInfrastructureOwner(owner))
            {
                var match = Regex.Match(
                    text,
                    @"\b\d+\.\d+\.\d+(?:-[A-Za-z0-9._-]+)?\b",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                    version = match.Value;
            }

            result.Add(new SourceIntegrationMetric
            {
                Owner = owner,
                SourceAvailable = true,
                FilesScanned = files.Count,
                ReferencesGRedRuntime = referencesRuntime,
                Services = services,
                RuntimeVersion = version
            });
        }

        return result
            .OrderByDescending(x => IsInfrastructureOwner(x.Owner))
            .ThenByDescending(x => x.ReferencesGRedRuntime)
            .ThenBy(x => x.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ResolveOwnerRoots(IEnumerable<string> sourcePaths)
    {
        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                continue;

            var normalized = sourcePath.Replace('/', '\\');
            var marker = "\\r6\\scripts\\";
            var index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                yield return sourcePath;
                continue;
            }

            var scriptsRoot = normalized[..(index + marker.Length)];
            var relative = normalized[(index + marker.Length)..];
            var separator = relative.IndexOf('\\');

            if (separator > 0)
            {
                var topLevel = relative[..separator];
                yield return Path.Combine(scriptsRoot, topLevel);
            }
            else
            {
                yield return sourcePath;
            }
        }
    }

    private static void AddIfPresent(
        List<string> services,
        string text,
        string label,
        params string[] markers)
    {
        if (markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            services.Add(label);
    }

    private sealed class SourceIntegrationMetric
    {
        public string Owner { get; init; } = "";
        public bool SourceAvailable { get; init; }
        public int FilesScanned { get; init; }
        public bool ReferencesGRedRuntime { get; init; }
        public List<string> Services { get; init; } = [];
        public string RuntimeVersion { get; init; } = "";
    }
}
