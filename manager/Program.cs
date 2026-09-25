using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static class Program
{
    private static readonly JsonSerializerOptions HeadlessJsonOptions = new()
    {
        WriteIndented = false
    };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
            return RunHeadless(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunHeadless(string[] args)
    {
        try
        {
            var parsed = CliArgs.Parse(args);
            if (parsed.Has("help"))
            {
                Console.WriteLine(HelpText);
                return 0;
            }

            var action = parsed.Get("action")?.ToLowerInvariant()
                ?? (parsed.Has("status") ? "status" : null)
                ?? (parsed.Has("install") ? "install" : null)
                ?? (parsed.Has("restore") || parsed.Has("uninstall") ? "restore" : null)
                ?? (parsed.Has("collect") ? "collect" : null)
                ?? (parsed.Has("report") ? "report" : null)
                ?? (parsed.Has("title") ? "title" : null)
                ?? (parsed.Has("scenario") ? "title" : null)
                ?? (parsed.Has("start") ? "start" : null);

            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Specify --action status|install|restore|collect|report|title|start.");

            var gameRoot = parsed.Get("game-root") ?? parsed.Get("gameroot") ?? parsed.Get("game") ?? "";
            object result = action switch
            {
                "status" => ManagerServices.GetStatus(gameRoot),
                "install" => ManagerServices.Install(gameRoot),
                "restore" or "uninstall" => new { message = ManagerServices.Restore(gameRoot) },
                "collect" => CollectWithReport(gameRoot),
                "report" => BuildReport(
                    parsed.Get("capture") ??
                    throw new ArgumentException("--capture <folder> is required for --report.")),
                "title" => new
                {
                    title = ManagerServices.SaveCaptureTitle(
                        gameRoot,
                        parsed.Get("title") ?? parsed.Get("scenario") ?? throw new ArgumentException("--title is required."))
                },
                "start" => new { message = ManagerServices.StartCyberpunk(gameRoot) },
                _ => throw new ArgumentException($"Unknown action: {action}")
            };

            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result }, HeadlessJsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, HeadlessJsonOptions));
            return 1;
        }
    }

    private static object CollectWithReport(string gameRoot)
    {
        var path = ManagerServices.CollectLatest(gameRoot);
        try
        {
            var report = ResultReportService.Generate(path);
            return new
            {
                path,
                report = report.ReportPath,
                summary = report.SummaryPath,
                reportError = (string?)null
            };
        }
        catch (Exception ex)
        {
            // Collection already succeeded and raw data is safe. Report failure
            // must not turn a successful archive into a destructive retry.
            return new
            {
                path,
                report = (string?)null,
                summary = (string?)null,
                reportError = ex.Message
            };
        }
    }

    private static object BuildReport(string capture)
    {
        var report = ResultReportService.Generate(capture);
        return new
        {
            report = report.ReportPath,
            summary = report.SummaryPath,
            findings = report.FindingsCount,
            frameTime = report.HasFrameTimeData,
            frameTimeSync = report.FrameTimeSyncQuality
        };
    }

    private const string HelpText =
        "G-REDscript Profiler headless interface\n" +
        "\n" +
        "  --status   --game <root> --json\n" +
        "  --install  --game <root> --json\n" +
        "  --title <name> --game <root> --json\n" +
        "  --scenario <name> is retained as a compatibility alias\n" +
        "  --collect  --game <root> --json\n" +
        "  --report   --capture <capture-folder> --json\n" +
        "  --restore  --game <root> --json\n" +
        "  --start    --game <root> --json\n" +
        "\n" +
        "  --action <name> and --game-root <root> are equivalent orchestration aliases.\n";
}

internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (!raw.StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = raw[2..];
            string? value = null;
            var equals = key.IndexOf('=');
            if (equals >= 0)
            {
                value = key[(equals + 1)..];
                key = key[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            result._values[key] = value;
        }
        return result;
    }

    public bool Has(string key) => _values.ContainsKey(key);
    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;
}
