using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal sealed class AppSettings
{
    public string GameRoot { get; set; } = "";
    public string CaptureTitle { get; set; } = "WORLD";
    public bool PairFrameTimeProfiler { get; set; } = true;
    public string ExternalProfilerExe { get; set; } = "";
    public string ExternalResultsDirectory { get; set; } = "";
    public string LastCollectedExternalSource { get; set; } = "";
    public string LastCollectedExternalFingerprint { get; set; } = "";

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "G-REDscript-Profiler.settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(
                       File.ReadAllText(SettingsPath),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json + Environment.NewLine);
            File.Move(temp, SettingsPath, true);
        }
        catch
        {
            // Package-local settings are convenience-only and must never block profiling.
        }
    }
}
