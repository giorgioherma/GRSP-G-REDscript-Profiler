using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal static class Program
{
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
            var action = parsed.Get("action")?.ToLowerInvariant()
                ?? (parsed.Has("status") ? "status" : null)
                ?? (parsed.Has("install") ? "install" : null)
                ?? (parsed.Has("restore") || parsed.Has("uninstall") ? "restore" : null)
                ?? (parsed.Has("collect") ? "collect" : null)
                ?? (parsed.Has("start") ? "start" : null);

            if (string.IsNullOrWhiteSpace(action))
                throw new ArgumentException("Specify --action status|install|restore|collect|scenario|start.");

            var gameRoot = parsed.Get("game-root") ?? parsed.Get("gameroot") ?? "";
            object result = action switch
            {
                "status" => ManagerServices.GetStatus(gameRoot),
                "install" => ManagerServices.InstallOrUpdate(gameRoot),
                "restore" or "uninstall" => new { message = ManagerServices.Restore(gameRoot) },
                "collect" => new { path = ManagerServices.CollectLatest(gameRoot) },
                "scenario" => new { scenario = ManagerServices.SaveScenario(gameRoot, parsed.Get("scenario") ?? throw new ArgumentException("--scenario is required.")) },
                "start" => new { message = ManagerServices.StartCyberpunk(gameRoot) },
                _ => throw new ArgumentException($"Unknown action: {action}")
            };

            Console.WriteLine(JsonSerializer.Serialize(new { ok = true, result }, ManagerServices.JsonOptions));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, ManagerServices.JsonOptions));
            return 1;
        }
    }
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
