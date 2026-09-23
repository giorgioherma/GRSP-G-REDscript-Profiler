using System.Diagnostics;

namespace GRedscriptProfiler.Manager;

internal sealed class MainForm : Form
{
    private readonly AppSettings appSettings = AppSettings.Load();

    private readonly Panel setupPage = new();
    private readonly Panel profilerPage = new();

    private readonly TextBox gameRoot = new();
    private readonly Button browseGame = new();
    private readonly Label setupGameStatus = new();

    private readonly CheckBox pairFrameTime = new();
    private readonly TextBox companionExe = new();
    private readonly TextBox companionResults = new();
    private readonly Button browseCompanionExe = new();
    private readonly Button browseCompanionResults = new();
    private readonly Label companionStatus = new();
    private readonly LinkLabel capFrameXLink = new();

    private readonly Label status = new();
    private readonly Label managedFiles = new();
    private readonly TextBox captureTitle = new();
    private readonly Button saveCaptureTitle = new();

    private readonly Button install = new();
    private readonly Button collect = new();
    private readonly Button restore = new();
    private readonly Button openResults = new();
    private readonly Button startCompanion = new();
    private readonly Button startGame = new();
    private readonly Button refresh = new();
    private readonly Label restoreOutcome = new();

    private bool busy;
    private bool loadingSettings = true;
    private bool suppressActivationRefresh;
    private StatusInfo? lastStatus;

    public MainForm()
    {
        Text = $"G-REDscript Profiler - {ManagerServices.ProductVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 690);
        MinimumSize = new Size(880, 730);
        Font = new Font("Segoe UI", 9F);

        BuildSetupPage();
        BuildProfilerPage();
        Controls.Add(profilerPage);
        Controls.Add(setupPage);

        gameRoot.Text = appSettings.GameRoot;
        captureTitle.Text = string.IsNullOrWhiteSpace(appSettings.CaptureTitle)
            ? "WORLD"
            : appSettings.CaptureTitle;
        companionExe.Text = appSettings.ExternalProfilerExe;
        companionResults.Text = appSettings.ExternalResultsDirectory;
        pairFrameTime.Checked = appSettings.PairFrameTimeProfiler;

        loadingSettings = false;
        UpdateCompanionControls();
        RefreshCompanionStatus();
        ShowPage(0);

        Shown += async (_, _) =>
        {
            await RefreshStatusAsync(silent: true);
            RefreshCompanionStatus();
        };

        Activated += async (_, _) =>
        {
            if (busy || suppressActivationRefresh)
                return;
            await RefreshStatusAsync(silent: true);
            RefreshCompanionStatus();
        };

        FormClosing += (_, _) => SaveSettingsFromUi();
    }

    private void BuildSetupPage()
    {
        setupPage.Dock = DockStyle.Fill;

        var title = new Label
        {
            Text = "SETUP",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            Location = new Point(20, 18)
        };

        var subtitle = new Label
        {
            Text = "Select Cyberpunk 2077. RED4ext is required; frame-time pairing is optional.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(22, 55)
        };

        var gameGroup = new GroupBox { Text = "Cyberpunk 2077" };
        gameGroup.SetBounds(20, 88, 820, 92);

        gameRoot.SetBounds(18, 30, 680, 26);
        gameRoot.TextChanged += async (_, _) =>
        {
            if (!loadingSettings)
                SaveSettingsFromUi();
            await RefreshStatusAsync(silent: true);
            RenderSetupGameStatus();
        };

        browseGame.Text = "Browse...";
        browseGame.SetBounds(708, 28, 92, 30);
        browseGame.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the Cyberpunk 2077 game folder",
                SelectedPath = Directory.Exists(gameRoot.Text) ? gameRoot.Text : ""
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            gameRoot.Text = dialog.SelectedPath;
            await RefreshStatusAsync(silent: true);
            RenderSetupGameStatus();
        };

        setupGameStatus.SetBounds(18, 62, 780, 20);
        gameGroup.Controls.AddRange([gameRoot, browseGame, setupGameStatus]);

        var companionGroup = new GroupBox { Text = "Optional frame-time capture companion" };
        companionGroup.SetBounds(20, 192, 820, 382);

        pairFrameTime.Text = "Run with a frame-time capture tool";
        pairFrameTime.Font = new Font("Segoe UI Semibold", 10F);
        pairFrameTime.SetBounds(18, 28, 300, 24);
        pairFrameTime.CheckedChanged += (_, _) =>
        {
            if (loadingSettings)
                return;
            UpdateCompanionControls();
            RefreshCompanionStatus();
            SaveSettingsFromUi();
            SetActionState(lastStatus);
        };

        var explanation = new Label
        {
            Text = "Pairing GRSP with a frame-time capture tool lets you see whether REDscript activity lines up with actual frame-time spikes or sustained frame-time cost. For synchronized captures, start both tools with F11 before the section you want to measure, then press F11 again to stop the capture.",
            MaximumSize = new Size(770, 0),
            AutoSize = true,
            Location = new Point(18, 62)
        };

        var recommendation = new Label
        {
            Text = "Any frame-time capture tool can work with G-REDscript Profiler, but it was developed and tested with",
            AutoSize = true,
            Location = new Point(18, 118)
        };

        capFrameXLink.Text = "CapFrameX 1.9.1.2 Beta";
        capFrameXLink.AutoSize = true;
        capFrameXLink.Location = new Point(18, 140);
        capFrameXLink.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(CompanionProfilerService.RecommendedProfilerWebsite)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var exeLabel = new Label
        {
            Text = "Profiler executable",
            AutoSize = true,
            Location = new Point(18, 176)
        };
        companionExe.SetBounds(18, 198, 680, 26);
        companionExe.TextChanged += (_, _) =>
        {
            if (loadingSettings)
                return;
            RefreshCompanionStatus();
            SetActionState(lastStatus);
            SaveSettingsFromUi();
        };

        browseCompanionExe.Text = "Browse...";
        browseCompanionExe.SetBounds(708, 196, 92, 30);
        browseCompanionExe.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select your frame-time profiler executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            companionExe.Text = dialog.FileName;
            var suggested = CompanionProfilerService.SuggestResultsDirectory(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(suggested) &&
                (string.IsNullOrWhiteSpace(companionResults.Text) || !Directory.Exists(companionResults.Text)))
            {
                companionResults.Text = suggested;
            }

            RefreshCompanionStatus();
        };

        var resultsLabel = new Label
        {
            Text = "Capture / results folder",
            AutoSize = true,
            Location = new Point(18, 236)
        };
        companionResults.SetBounds(18, 258, 680, 26);
        companionResults.TextChanged += (_, _) =>
        {
            if (loadingSettings)
                return;
            RefreshCompanionStatus();
            SaveSettingsFromUi();
        };

        browseCompanionResults.Text = "Browse...";
        browseCompanionResults.SetBounds(708, 256, 92, 30);
        browseCompanionResults.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the frame-time profiler capture/results folder",
                SelectedPath = Directory.Exists(companionResults.Text) ? companionResults.Text : ""
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            companionResults.Text = dialog.SelectedPath;
            RefreshCompanionStatus();
            SaveSettingsFromUi();
        };

        companionStatus.SetBounds(18, 300, 780, 62);
        companionStatus.Font = new Font("Consolas", 9.5F);

        companionGroup.Controls.AddRange([
            pairFrameTime, explanation, recommendation, capFrameXLink,
            exeLabel, companionExe, browseCompanionExe,
            resultsLabel, companionResults, browseCompanionResults,
            companionStatus
        ]);

        var next = new Button
        {
            Text = "CONTINUE →",
            Width = 140,
            Height = 38,
            Left = 700,
            Top = 602
        };
        next.Click += async (_, _) =>
        {
            SaveSettingsFromUi();
            await RefreshStatusAsync();
            if (lastStatus?.GameRootValid == true && lastStatus.Red4extPresent)
                ShowPage(1);
        };

        setupPage.Controls.AddRange([title, subtitle, gameGroup, companionGroup, next]);
    }

    private void BuildProfilerPage()
    {
        profilerPage.Dock = DockStyle.Fill;

        var title = new Label
        {
            Text = "INSTALL, CAPTURE & RECOVERY",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            Location = new Point(20, 18)
        };

        var back = new Button
        {
            Text = "← SETUP",
            Width = 100,
            Height = 30,
            Left = 740,
            Top = 18
        };
        back.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            ShowPage(0);
        };

        var statusGroup = new GroupBox { Text = "Profiler status" };
        statusGroup.SetBounds(20, 66, 820, 156);
        status.SetBounds(18, 27, 775, 92);
        status.Font = new Font("Consolas", 9.5F);

        refresh.Text = "REFRESH";
        refresh.SetBounds(694, 118, 100, 28);
        refresh.Click += async (_, _) => await RefreshStatusAsync();
        statusGroup.Controls.AddRange([status, refresh]);

        var captureGroup = new GroupBox { Text = "Capture" };
        captureGroup.SetBounds(20, 232, 820, 144);

        var captureLabel = new Label
        {
            Text = "Capture title",
            AutoSize = true,
            Location = new Point(18, 28)
        };
        captureTitle.SetBounds(110, 24, 420, 26);
        captureTitle.TextChanged += (_, _) =>
        {
            if (!loadingSettings)
                SaveSettingsFromUi();
        };

        saveCaptureTitle.Text = "SAVE TITLE";
        saveCaptureTitle.SetBounds(544, 22, 120, 30);
        saveCaptureTitle.Click += async (_, _) => await SaveCaptureTitleAsync();

        var captureHint = new Label
        {
            Text = "The title is read when capture starts and becomes part of the capture folder name.\r\nF11 #1 = START   ·   F11 #2 = STOP + EXPORT   ·   closing the game while recording also exports what was captured.",
            MaximumSize = new Size(775, 0),
            AutoSize = true,
            Location = new Point(18, 66)
        };
        captureGroup.Controls.AddRange([captureLabel, captureTitle, saveCaptureTitle, captureHint]);

        var filesGroup = new GroupBox { Text = "Restore" };
        filesGroup.SetBounds(20, 386, 820, 94);
        managedFiles.SetBounds(18, 24, 775, 58);
        managedFiles.MaximumSize = new Size(775, 0);
        managedFiles.AutoSize = true;
        filesGroup.Controls.Add(managedFiles);

        install.Text = "INSTALL PROFILER";
        install.SetBounds(20, 496, 230, 42);
        install.Click += async (_, _) => await InstallAsync();

        collect.Text = "COLLECT RESULTS / CLEAR LIVE";
        collect.SetBounds(265, 496, 300, 42);
        collect.Click += async (_, _) => await CollectAsync();

        restore.Text = "RESTORE ORIGINAL STATE";
        restore.SetBounds(580, 496, 260, 42);
        restore.Click += async (_, _) =>
        {
            if (busy)
                return;

            DialogResult answer;
            suppressActivationRefresh = true;
            try
            {
                answer = MessageBox.Show(
                    this,
                    "All files will be returned to their original state.",
                    Text,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                suppressActivationRefresh = false;
            }

            if (answer == DialogResult.Yes)
                await RestoreAsync();
        };

        openResults.Text = "Open Results Folder";
        openResults.SetBounds(20, 552, 230, 36);
        openResults.Click += (_, _) => OpenResultsFolder();

        startCompanion.Text = "START FRAME-TIME TOOL";
        startCompanion.SetBounds(265, 552, 300, 36);
        startCompanion.Click += (_, _) => StartFrameTimeTool();

        startGame.Text = "START CYBERPUNK";
        startGame.SetBounds(580, 552, 260, 36);
        startGame.Click += async (_, _) => await StartCyberpunkAsync();

        restoreOutcome.SetBounds(20, 604, 820, 34);
        restoreOutcome.Font = new Font("Segoe UI Semibold", 10F);
        restoreOutcome.TextAlign = ContentAlignment.MiddleLeft;
        restoreOutcome.Visible = false;

        profilerPage.Controls.AddRange([
            title, back, statusGroup, captureGroup, filesGroup,
            install, collect, restore, openResults, startCompanion, startGame, restoreOutcome
        ]);
    }

    private void ShowPage(int page)
    {
        setupPage.Visible = page == 0;
        profilerPage.Visible = page == 1;

        if (page == 0)
        {
            setupPage.BringToFront();
            RenderSetupGameStatus();
            RefreshCompanionStatus();
        }
        else
        {
            profilerPage.BringToFront();
            RenderStatus(lastStatus);
        }
    }

    private async Task RefreshStatusAsync(bool silent = false)
    {
        if (busy)
            return;

        var root = gameRoot.Text.Trim();
        if (!LooksLikeGameRoot(root))
        {
            lastStatus = null;
            status.Text =
                "Game:       NOT FOUND\r\n" +
                "RED4ext:    -\r\n" +
                "GRSP:       -\r\n" +
                "Live captures: -";
            RenderSetupGameStatus();
            RenderManagedFiles(null);
            SetActionState(null);
            return;
        }

        try
        {
            SetBusy(true);
            var snapshot = await Task.Run(() => ManagerServices.GetStatus(root));
            lastStatus = snapshot;
            RenderStatus(snapshot);
            RenderSetupGameStatus();
            RenderManagedFiles(snapshot);

            if (!captureTitle.Focused &&
                !string.IsNullOrWhiteSpace(snapshot.CaptureTitle) &&
                snapshot.CaptureTitle != "UNREADABLE")
            {
                captureTitle.Text = snapshot.CaptureTitle;
            }

            SetBusy(false);
            SetActionState(snapshot);
        }
        catch (Exception ex)
        {
            lastStatus = null;
            SetBusy(false);
            status.Text = "STATUS ERROR:\r\n" + ex.Message;
            RenderSetupGameStatus();
            RenderManagedFiles(null);
            SetActionState(null);

            if (!silent)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RenderSetupGameStatus()
    {
        if (!LooksLikeGameRoot(gameRoot.Text.Trim()))
        {
            setupGameStatus.Text = "Game: NOT FOUND";
            return;
        }

        if (lastStatus is null)
        {
            setupGameStatus.Text = "Game: FOUND    RED4ext: checking...";
            return;
        }

        setupGameStatus.Text =
            $"Game: {(lastStatus.GameRootValid ? "FOUND" : "NOT FOUND")}    " +
            $"RED4ext: {(lastStatus.Red4extPresent ? "FOUND" : "NOT FOUND")}";
    }

    private void RenderStatus(StatusInfo? snapshot)
    {
        if (snapshot is null)
        {
            status.Text = "Select a valid Cyberpunk 2077 folder.";
            SetActionState(null);
            return;
        }

        var dll = snapshot.State switch
        {
            "INSTALLED_CURRENT" => "INSTALLED · CURRENT · MANAGED",
            "PREEXISTING_CURRENT" => "INSTALLED · CURRENT · UNMANAGED",
            "INSTALLED_OTHER_VERSION" => "INSTALLED · DIFFERENT VERSION",
            "INSTALLED_OTHER_PACKAGE" => "INSTALLED · DIFFERENT MANAGED BUILD",
            "LEGACY_GRSP_PRESENT" => "LEGACY ALPHA DLL DETECTED",
            "NOT_INSTALLED" => "NOT INSTALLED",
            _ => snapshot.State
        };

        status.Text =
            $"Game:       {(snapshot.GameRootValid ? "FOUND" : "NOT FOUND")}\r\n" +
            $"RED4ext:    {(snapshot.Red4extPresent ? "FOUND" : "NOT FOUND")}\r\n" +
            $"GRSP:       {dll}\r\n" +
            $"Live captures: {snapshot.CompletedCaptureCount}\r\n" +
            snapshot.Message;

        SetActionState(snapshot);
    }

    private void RenderManagedFiles(StatusInfo? snapshot)
    {
        managedFiles.Text = "After restore, the game folder will be returned to its original state.";
    }

    private void RefreshCompanionStatus()
    {
        if (!loadingSettings)
            SyncSettingsFromUi();

        var snapshot = CompanionProfilerService.Inspect(appSettings);
        if (!snapshot.Enabled)
        {
            companionStatus.Text =
                "GRSP START:      F11\r\n" +
                "Frame-time:      DISABLED";
            return;
        }

        var results = string.IsNullOrWhiteSpace(companionResults.Text)
            ? "NOT SET"
            : Directory.Exists(companionResults.Text) ? "FOUND" : "NOT FOUND";

        var keyText = snapshot.StartKeyKnown
            ? snapshot.StartKeyIsF11 ? "F11 ✓" : snapshot.StartKey + "  (use F11 to sync)"
            : "UNKNOWN — verify F11 manually";

        companionStatus.Text =
            "GRSP START:      F11 ✓\r\n" +
            $"Frame-time:     {snapshot.DisplayName} · START {keyText}\r\n" +
            $"Results folder: {results}";
    }

    private void UpdateCompanionControls()
    {
        companionExe.Enabled = !busy;
        companionResults.Enabled = !busy;
        browseCompanionExe.Enabled = !busy;
        browseCompanionResults.Enabled = !busy;
    }

    private void SetActionState(StatusInfo? snapshot)
    {
        if (snapshot is null)
        {
            install.Enabled = false;
            collect.Enabled = false;
            restore.Enabled = false;
            saveCaptureTitle.Enabled = false;
            startCompanion.Enabled = false;
            startGame.Enabled = false;
            return;
        }

        var managedCurrent = snapshot.State == "INSTALLED_CURRENT" && snapshot.ManagedStatePresent;
        var validInstalled = snapshot.State is "INSTALLED_CURRENT" or "PREEXISTING_CURRENT";

        install.Enabled = !busy && snapshot.GameRootValid && snapshot.Red4extPresent && snapshot.State == "NOT_INSTALLED";
        collect.Enabled = !busy && snapshot.CompletedCaptureCount > 0;
        restore.Enabled = !busy && managedCurrent;
        saveCaptureTitle.Enabled = !busy && validInstalled && snapshot.CaptureTitlePresent;

        var companionPath = companionExe.Text.Trim();
        startCompanion.Enabled =
            !busy &&
            pairFrameTime.Checked &&
            !string.IsNullOrWhiteSpace(companionPath) &&
            File.Exists(companionPath);

        var gameExe = GetGameExe(snapshot.GameRoot);
        var gameRunning = ManagerServices.IsGameRunning();
        startGame.Enabled = !busy && validInstalled && File.Exists(gameExe) && !gameRunning;
        startGame.Text = gameRunning ? "CYBERPUNK RUNNING" : "START CYBERPUNK";
    }

    private async Task InstallAsync()
    {
        if (busy)
            return;

        ClearRestoreOutcome();

        try
        {
            SetBusy(true);
            await Task.Run(() => ManagerServices.Install(gameRoot.Text.Trim()));

            if (!string.IsNullOrWhiteSpace(captureTitle.Text))
                await Task.Run(() => ManagerServices.SaveCaptureTitle(gameRoot.Text.Trim(), captureTitle.Text));

            MessageBox.Show(
                this,
                "G-REDscript Profiler installed.\r\n\r\n" +
                "F11 #1 = START\r\n" +
                "F11 #2 = STOP + EXPORT\r\n\r\n" +
                "The profiler starts paused and works without any frame-time companion.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync(silent: true);
        }
    }

    private async Task SaveCaptureTitleAsync()
    {
        if (busy)
            return;

        try
        {
            SetBusy(true);
            var saved = await Task.Run(() =>
                ManagerServices.SaveCaptureTitle(gameRoot.Text.Trim(), captureTitle.Text));
            captureTitle.Text = saved;
            SaveSettingsFromUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync(silent: true);
        }
    }

    private async Task CollectAsync()
    {
        if (busy)
            return;

        try
        {
            SaveSettingsFromUi();
            SetBusy(true);

            var destination = await Task.Run(() =>
                ManagerServices.CollectLatest(gameRoot.Text.Trim()));

            CompanionCollectResult? companion = null;
            string? companionError = null;

            if (pairFrameTime.Checked)
            {
                try
                {
                    companion = await Task.Run(() =>
                        CompanionProfilerService.CollectLatest(appSettings, destination));
                }
                catch (Exception ex)
                {
                    companionError = ex.Message;
                }
            }

            if (Directory.Exists(destination))
                Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });

            var companionText = !pairFrameTime.Checked
                ? "Frame-time companion: disabled."
                : companionError is not null
                    ? "Frame-time companion: GRSP collection succeeded, but companion copy failed: " + companionError
                    : "Frame-time companion: " + (companion?.Message ?? "not collected.");

            MessageBox.Show(
                this,
                "GRSP results archived successfully and live profiler output was cleared.\r\n\r\n" +
                "Archive folder:\r\n" + destination + "\r\n\r\n" +
                companionText,
                Text,
                MessageBoxButtons.OK,
                companionError is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync(silent: true);
        }
    }

    private async Task RestoreAsync()
    {
        if (busy)
            return;

        try
        {
            ShowRestoreProgress("RESTORING ORIGINAL STATE...");
            SetBusy(true);
            restoreOutcome.Refresh();

            var message = await Task.Run(() => ManagerServices.Restore(gameRoot.Text.Trim()));
            var verified = await Task.Run(() => ManagerServices.GetStatus(gameRoot.Text.Trim()));

            if (verified.ManagedStatePresent || verified.DllPresent)
                throw new InvalidOperationException("Restore returned, but managed profiler state or DLL is still present.");

            lastStatus = verified;
            ShowRestoreOutcome(true, "RESTORE SUCCESSFUL — files returned to their original state.");
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowRestoreOutcome(false, "RESTORE NOT COMPLETED — managed state was preserved where needed.");
            MessageBox.Show(
                this,
                "Restore could not complete safely.\r\n\r\n" + ex.Message,
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
            await RefreshStatusAsync(silent: true);
        }
    }

    private async Task StartCyberpunkAsync()
    {
        if (busy)
            return;

        try
        {
            if (lastStatus?.CaptureTitlePresent == true && !string.IsNullOrWhiteSpace(captureTitle.Text))
                await Task.Run(() => ManagerServices.SaveCaptureTitle(gameRoot.Text.Trim(), captureTitle.Text));

            ManagerServices.StartCyberpunk(gameRoot.Text.Trim());
            SetActionState(lastStatus);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartFrameTimeTool()
    {
        var exe = companionExe.Text.Trim();
        if (!File.Exists(exe))
        {
            MessageBox.Show(this, "The configured frame-time profiler executable was not found.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenResultsFolder()
    {
        Directory.CreateDirectory(ManagerServices.ArchiveResultsDirectory);
        Process.Start(new ProcessStartInfo(ManagerServices.ArchiveResultsDirectory)
        {
            UseShellExecute = true
        });
    }

    private void ShowRestoreProgress(string message)
    {
        restoreOutcome.Text = "… " + message;
        restoreOutcome.ForeColor = SystemColors.ControlText;
        restoreOutcome.Visible = true;
        restoreOutcome.BringToFront();
    }

    private void ShowRestoreOutcome(bool success, string message)
    {
        restoreOutcome.Text = (success ? "✓ " : "✗ ") + message;
        restoreOutcome.ForeColor = success ? Color.ForestGreen : Color.Firebrick;
        restoreOutcome.Visible = true;
        restoreOutcome.BringToFront();
    }

    private void ClearRestoreOutcome()
    {
        restoreOutcome.Text = "";
        restoreOutcome.Visible = false;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UseWaitCursor = value;

        browseGame.Enabled = !value;
        pairFrameTime.Enabled = !value;
        captureTitle.Enabled = !value;
        refresh.Enabled = !value;
        openResults.Enabled = !value;

        UpdateCompanionControls();
        SetActionState(lastStatus);
    }

    private void SyncSettingsFromUi()
    {
        appSettings.GameRoot = gameRoot.Text.Trim();
        appSettings.CaptureTitle = captureTitle.Text.Trim();
        appSettings.PairFrameTimeProfiler = pairFrameTime.Checked;
        appSettings.ExternalProfilerExe = companionExe.Text.Trim();
        appSettings.ExternalResultsDirectory = companionResults.Text.Trim();
    }

    private void SaveSettingsFromUi()
    {
        if (loadingSettings)
            return;

        SyncSettingsFromUi();
        appSettings.Save();
    }

    private static bool LooksLikeGameRoot(string root) =>
        !string.IsNullOrWhiteSpace(root) &&
        Directory.Exists(root) &&
        File.Exists(GetGameExe(root));

    private static string GetGameExe(string root) =>
        Path.Combine(root ?? "", "bin", "x64", "Cyberpunk2077.exe");
}
