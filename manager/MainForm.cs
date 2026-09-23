using System.Diagnostics;
using System.Text.Json;

namespace GRedscriptProfiler.Manager;

internal sealed class MainForm : Form
{
    private readonly TextBox _gameRoot = new() { Dock = DockStyle.Fill };
    private readonly TextBox _scenario = new() { Width = 180 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(940, 0) };
    private readonly RichTextBox _log = new() { Dock = DockStyle.Fill, ReadOnly = true, Height = 145 };
    private readonly Button _install = new() { Text = "INSTALL", AutoSize = true };
    private readonly Button _restore = new() { Text = "RESTORE / UNINSTALL", AutoSize = true };
    private readonly Button _collect = new() { Text = "COLLECT LATEST", AutoSize = true };
    private readonly Button _saveScenario = new() { Text = "SAVE SCENARIO", AutoSize = true };
    private readonly Button _startGame = new() { Text = "START CYBERPUNK", AutoSize = true };
    private bool _busy;

    public MainForm()
    {
        Text = $"G-REDscript Profiler {ManagerServices.ProductVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 570);
        Size = new Size(980, 650);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 7,
            AutoSize = false
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "GRSP standalone manager — installation lifecycle only"
        };
        root.Controls.Add(title);

        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(930, 0),
            Text = "Measurement stays inside redscript_profiler_alpha.dll. F11 #1 starts capture; F11 #2 stops it. The manager installs, verifies, collects and restores."
        };
        root.Controls.Add(note);

        var gameRow = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 3, AutoSize = true };
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        gameRow.Controls.Add(new Label { Text = "Cyberpunk root:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        gameRow.Controls.Add(_gameRoot, 1, 0);
        var browse = new Button { Text = "BROWSE", AutoSize = true };
        browse.Click += (_, _) => BrowseGameRoot();
        gameRow.Controls.Add(browse, 2, 0);
        root.Controls.Add(gameRow);

        var scenarioRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        scenarioRow.Controls.Add(new Label { Text = "Scenario:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
        scenarioRow.Controls.Add(_scenario);
        scenarioRow.Controls.Add(_saveScenario);
        var refresh = new Button { Text = "REFRESH STATUS", AutoSize = true };
        scenarioRow.Controls.Add(refresh);
        root.Controls.Add(scenarioRow);

        root.Controls.Add(_status);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true
        };
        buttons.Controls.Add(_install);
        buttons.Controls.Add(_collect);
        var openNative = new Button { Text = "OPEN NATIVE RESULTS", AutoSize = true };
        buttons.Controls.Add(openNative);
        var openArchive = new Button { Text = "OPEN COLLECTED RESULTS", AutoSize = true };
        buttons.Controls.Add(openArchive);
        buttons.Controls.Add(_restore);
        buttons.Controls.Add(_startGame);
        root.Controls.Add(buttons);

        root.Controls.Add(_log);
        root.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(930, 0),
            Text = "Native output remains red4ext\\plugins\\redscript_profiler_alpha\\RESULTS. COLLECT copies a completed capture into this package's RESULTS folder and verifies the copy; it does not move or delete the native capture."
        });

        refresh.Click += async (_, _) => await RefreshStatusAsync();
        _install.Click += async (_, _) => await RunActionAsync(() =>
        {
            var r = ManagerServices.InstallOrUpdate(GameRoot);
            return $"GRSP {r.Mode}. DLL: {r.DllPath}";
        });
        _saveScenario.Click += async (_, _) => await RunActionAsync(() =>
            $"Scenario saved: {ManagerServices.SaveScenario(GameRoot, _scenario.Text)}");
        _collect.Click += async (_, _) => await RunActionAsync(() =>
            $"Collected: {ManagerServices.CollectLatest(GameRoot)}");
        _restore.Click += async (_, _) =>
        {
            if (MessageBox.Show(
                    this,
                    "Restore/uninstall GRSP managed by this package? Native RESULTS will be left untouched.",
                    "GRSP restore",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes)
                await RunActionAsync(() => ManagerServices.Restore(GameRoot));
        };
        _startGame.Click += async (_, _) => await RunActionAsync(() => ManagerServices.StartCyberpunk(GameRoot), refreshAfter: false);
        openNative.Click += (_, _) => OpenFolder(ManagerServices.NativeResultsPath(GameRoot));
        openArchive.Click += (_, _) => OpenFolder(ManagerServices.ArchiveResultsDirectory);

        _gameRoot.TextChanged += (_, _) => SaveSettingsBestEffort();
        FormClosing += (_, _) => SaveSettingsBestEffort();

        LoadSettingsBestEffort();
        Shown += async (_, _) => await RefreshStatusAsync();
    }

    private string GameRoot => _gameRoot.Text.Trim();

    private async Task RefreshStatusAsync()
    {
        if (_busy) return;
        try
        {
            var status = await Task.Run(() => ManagerServices.GetStatus(GameRoot));
            RenderStatus(status);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void RenderStatus(StatusInfo s)
    {
        _status.Text =
            $"State: {s.State} — {s.Message}\n" +
            $"RED4ext: {(s.Red4extPresent ? "found" : "not found")}    " +
            $"DLL: {(s.DllPresent ? ShortHash(s.InstalledHash) : "not installed")}    " +
            $"Managed: {(s.ManagedStatePresent ? "yes" : "no")}\n" +
            $"Scenario: {(s.ScenarioPresent ? s.Scenario : "not installed")}    " +
            $"Completed native captures: {s.CompletedCaptureCount}" +
            (string.IsNullOrWhiteSpace(s.LatestCapture) ? "" : $"\nLatest: {s.LatestCapture}");

        if (!_scenario.Focused && !string.IsNullOrWhiteSpace(s.Scenario))
            _scenario.Text = s.Scenario;

        _saveScenario.Enabled = s.ManagedStatePresent && s.ScenarioPresent;
        _collect.Enabled = s.CompletedCaptureCount > 0;
    }

    private async Task RunActionAsync(Func<string> action, bool refreshAfter = true)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var message = await Task.Run(action);
            Log(message);
            if (refreshAfter)
            {
                var status = await Task.Run(() => ManagerServices.GetStatus(GameRoot));
                RenderStatus(status);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "GRSP", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        _install.Enabled = !busy;
        _restore.Enabled = !busy;
        _startGame.Enabled = !busy;
        if (busy)
        {
            _saveScenario.Enabled = false;
            _collect.Enabled = false;
        }
    }

    private void BrowseGameRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select the Cyberpunk 2077 game root",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(GameRoot) ? GameRoot : ""
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _gameRoot.Text = dialog.SelectedPath;
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"Folder does not exist yet:\n{path}", "GRSP", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void Log(string message)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private static string ShortHash(string hash) =>
        string.IsNullOrWhiteSpace(hash) ? "unknown" : hash[..Math.Min(12, hash.Length)];

    private string SettingsPath => Path.Combine(AppContext.BaseDirectory, "GRSP_Manager_Settings.json");

    private void LoadSettingsBestEffort()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var settings = JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(SettingsPath), ManagerServices.JsonOptions);
            if (settings is not null)
                _gameRoot.Text = settings.GameRoot ?? "";
        }
        catch { }
    }

    private void SaveSettingsBestEffort()
    {
        try
        {
            var json = JsonSerializer.Serialize(new ManagerSettings { GameRoot = GameRoot }, ManagerServices.JsonOptions);
            File.WriteAllText(SettingsPath, json + Environment.NewLine);
        }
        catch { }
    }

    private sealed class ManagerSettings
    {
        public string? GameRoot { get; set; }
    }
}
