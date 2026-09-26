using System.Diagnostics;
using System.Reflection;

namespace GRedscriptProfiler.Manager;

internal sealed class MainForm : Form
{
    private readonly AppSettings appSettings = AppSettings.Load();

    // Match the G-CET v1.0.0 visual language exactly where the
    // REDscript lifecycle permits it.
    private static readonly Color ThemeBg = Color.FromArgb(8, 13, 18);
    private static readonly Color ThemePanel = Color.FromArgb(14, 23, 31);
    private static readonly Color ThemePanelAlt = Color.FromArgb(11, 18, 25);
    private static readonly Color ThemeBorder = Color.FromArgb(40, 71, 82);
    private static readonly Color ThemeText = Color.FromArgb(232, 243, 246);
    private static readonly Color ThemeMuted = Color.FromArgb(172, 188, 197);
    private static readonly Color ThemeInactive = Color.FromArgb(104, 118, 126);
    private static readonly Color ThemeDisabledSurface = Color.FromArgb(18, 25, 31);
    private static readonly Color ThemeDisabledBorder = Color.FromArgb(49, 61, 68);
    private static readonly Color ThemeCyan = Color.FromArgb(54, 244, 244);
    private static readonly Color ThemeMagenta = Color.FromArgb(255, 63, 215);
    private static readonly Color ThemeGreen = Color.FromArgb(94, 255, 130);
    private static readonly Color ThemeAmber = Color.FromArgb(255, 216, 64);
    private static readonly Color ThemeRed = Color.FromArgb(255, 82, 100);

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

    private readonly RichTextBox status = new();
    private readonly Label managedFiles = new();
    private readonly GroupBox readyGroup = new();
    private readonly Label readyHeading = new();
    private readonly Label readyInstallInstruction = new();
    private readonly Label readyCaptureInstructions = new();
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
        Text = $"G-REDscript Profiler - v{ManagerServices.ProductVersion}";
        try
        {
            var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            if (executableIcon is not null)
                Icon = executableIcon;
        }
        catch
        {
            // Cosmetic only.
        }
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(860, 735);
        MinimumSize = new Size(880, 775);
        Font = new Font("Segoe UI", 9F);
        BackColor = ThemeBg;
        ForeColor = ThemeText;

        BuildSetupPage();
        BuildProfilerPage();
        Controls.Add(profilerPage);
        Controls.Add(setupPage);
        ApplyTheme();

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
            ThemedDialog.ApplyDarkTitleBar(this);
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

        var logo = CreateHeaderLogo(new Point(20, 5));
        var title = new Label
        {
            Text = "SETUP",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            ForeColor = ThemeCyan,
            Location = new Point(100, 18)
        };
        var subtitle = new Label
        {
            Text = "Select Cyberpunk 2077. RED4ext is required; frame-time pairing is optional.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(100, 52)
        };

        var gameGroup = new GroupBox { Text = "Cyberpunk 2077" };
        gameGroup.SetBounds(20, 88, 820, 92);
        gameRoot.SetBounds(18, 30, 680, 26);
        gameRoot.TextChanged += async (_, _) =>
        {
            ClearRestoreOutcome();
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
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

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
            if (loadingSettings) return;
            UpdateCompanionControls();
            RefreshCompanionStatus();
            SaveSettingsFromUi();
            SetActionState(lastStatus);
            if (lastStatus is not null)
                RenderStatus(lastStatus);
            RenderReadyState(lastStatus);
        };

        var explanation = new Label
        {
            Text = "G-REDscript Profiler works standalone. Pairing it with a frame-time capture lets you compare REDscript activity with actual frame-time behavior. This build was designed and tested alongside CapFrameX 1.9.1.2 Beta, but you can use a profiler you already have.",
            MaximumSize = new Size(770, 0),
            AutoSize = true,
            ForeColor = ThemeText,
            Location = new Point(18, 62)
        };

        capFrameXLink.Text = "CapFrameX releases";
        capFrameXLink.AutoSize = true;
        capFrameXLink.Location = new Point(18, 118);
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
                ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        var syncHint = new Label
        {
            Text = "For synchronized captures, use the same START key. G-REDscript Profiler uses F11.",
            AutoSize = true,
            ForeColor = ThemeText,
            Location = new Point(150, 118)
        };

        var exeLabel = new Label
        {
            Text = "Profiler executable",
            AutoSize = true,
            ForeColor = ThemeText,
            Location = new Point(18, 158)
        };
        companionExe.SetBounds(18, 180, 680, 26);
        companionExe.TextChanged += (_, _) =>
        {
            if (loadingSettings) return;
            RefreshCompanionStatus();
            SetActionState(lastStatus);
            SaveSettingsFromUi();
            if (lastStatus is not null)
                RenderStatus(lastStatus);
            RenderReadyState(lastStatus);
        };

        browseCompanionExe.Text = "Browse...";
        browseCompanionExe.SetBounds(708, 178, 92, 30);
        browseCompanionExe.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Select your frame-time profiler executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            companionExe.Text = dialog.FileName;
            var suggested = CompanionProfilerService.SuggestResultsDirectory(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(suggested) &&
                (string.IsNullOrWhiteSpace(companionResults.Text) || !Directory.Exists(companionResults.Text)))
                companionResults.Text = suggested;

            RefreshCompanionStatus();
        };

        var resultsLabel = new Label
        {
            Text = "Capture / results folder",
            AutoSize = true,
            ForeColor = ThemeText,
            Location = new Point(18, 218)
        };
        companionResults.SetBounds(18, 240, 680, 26);
        companionResults.TextChanged += (_, _) =>
        {
            if (loadingSettings) return;
            RefreshCompanionStatus();
            SaveSettingsFromUi();
            SetActionState(lastStatus);
            if (lastStatus is not null)
                RenderStatus(lastStatus);
            RenderReadyState(lastStatus);
        };

        browseCompanionResults.Text = "Browse...";
        browseCompanionResults.SetBounds(708, 238, 92, 30);
        browseCompanionResults.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the frame-time profiler capture/results folder",
                SelectedPath = Directory.Exists(companionResults.Text) ? companionResults.Text : ""
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            companionResults.Text = dialog.SelectedPath;
            RefreshCompanionStatus();
            SetActionState(lastStatus);
            SaveSettingsFromUi();
        };

        companionStatus.SetBounds(18, 282, 780, 62);
        companionStatus.Font = new Font("Consolas", 9.5F);

        companionGroup.Controls.AddRange([
            pairFrameTime, explanation, capFrameXLink, syncHint,
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

        setupPage.Controls.AddRange([logo, title, subtitle, gameGroup, companionGroup, next]);
        AddHeaderAccent(setupPage, 74);
    }

    private void BuildProfilerPage()
    {
        profilerPage.Dock = DockStyle.Fill;

        var logo = CreateHeaderLogo(new Point(20, 5));
        var title = new Label
        {
            Text = "INSTALL -> CAPTURE -> RESTORE",
            Font = new Font("Segoe UI Semibold", 18F),
            AutoSize = true,
            ForeColor = ThemeCyan,
            Location = new Point(100, 18)
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
        statusGroup.SetBounds(20, 66, 820, 286);
        status.SetBounds(18, 27, 775, 214);
        status.Font = new Font("Segoe UI", 9.5F);
        status.ReadOnly = true;
        status.BorderStyle = BorderStyle.None;
        status.ScrollBars = RichTextBoxScrollBars.None;
        status.DetectUrls = false;
        status.TabStop = false;
        status.BackColor = ThemePanelAlt;
        status.ForeColor = ThemeText;

        refresh.Text = "REFRESH";
        refresh.SetBounds(694, 246, 100, 28);
        refresh.Click += async (_, _) => await RefreshStatusAsync();
        statusGroup.Controls.AddRange([status, refresh]);

        var captureGroup = new GroupBox { Text = "Capture" };
        captureGroup.SetBounds(20, 362, 820, 72);

        var captureLabel = new Label
        {
            Text = "Capture title",
            AutoSize = true,
            ForeColor = ThemeText,
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

        captureGroup.Controls.AddRange([captureLabel, captureTitle, saveCaptureTitle]);

        readyGroup.Text = "";
        readyGroup.SetBounds(20, 444, 820, 144);

        readyHeading.SetBounds(18, 16, 775, 26);
        readyHeading.Font = new Font("Segoe UI Semibold", 11F);
        readyHeading.AutoSize = false;

        readyInstallInstruction.SetBounds(18, 44, 775, 20);
        readyInstallInstruction.Font = new Font("Segoe UI", 9.5F);
        readyInstallInstruction.AutoSize = false;
        readyInstallInstruction.Text = "1. Install G-REDscript Profiler and save a capture title.";

        readyCaptureInstructions.SetBounds(18, 64, 775, 72);
        readyCaptureInstructions.Font = new Font("Segoe UI", 9.5F);
        readyCaptureInstructions.AutoSize = false;
        readyCaptureInstructions.Text =
            "2. Run your Frame-time Capture Tool if you're using one and enter the game.\r\n" +
            "3. To start measurement press F11. To stop capture and prep the results press F11 again.\r\n" +
            "4. Return to installer and COLLECT RESULTS.\r\n" +
            "5. After usage RESTORE ORIGINAL STATE to finish.";

        readyGroup.Controls.AddRange([readyHeading, readyInstallInstruction, readyCaptureInstructions]);

        install.Text = "INSTALL PROFILER";
        install.SetBounds(20, 598, 230, 42);
        install.Click += async (_, _) => await InstallAsync();

        collect.Text = "COLLECT RESULTS / CLEAR LIVE";
        collect.SetBounds(265, 598, 300, 42);
        collect.Click += async (_, _) => await CollectAsync();

        restore.Text = "RESTORE ORIGINAL STATE";
        restore.SetBounds(580, 598, 260, 42);
        restore.Click += async (_, _) =>
        {
            if (busy) return;

            // Restore remains the user's exit path from any manager-owned state.
            // The only intentional runtime gate is Cyberpunk itself.
            if (ManagerServices.IsGameRunning())
            {
                ThemedDialog.Show(
                    this,
                    "Cyberpunk 2077 is still running.\r\n\r\n" +
                    "Close Cyberpunk 2077, then click RESTORE ORIGINAL STATE again.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            DialogResult answer;
            suppressActivationRefresh = true;
            try
            {
                answer = ThemedDialog.Show(
                    this,
                    "Restore the G-REDscript profiler-managed game state?\r\n\r\n" +
                    "The profiler-managed DLL and data folder will be removed. " +
                    "Any live profiler output is archived first when possible.",
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
        openResults.SetBounds(20, 650, 230, 36);
        openResults.Click += (_, _) => OpenResultsFolder();

        startCompanion.Text = "START FRAME-TIME TOOL";
        startCompanion.SetBounds(265, 650, 300, 36);
        startCompanion.Click += (_, _) => StartFrameTimeTool();

        startGame.Text = "START CYBERPUNK";
        startGame.SetBounds(580, 650, 260, 36);
        startGame.Click += async (_, _) => await StartCyberpunkAsync();

        restoreOutcome.SetBounds(20, 690, 820, 34);
        restoreOutcome.Font = new Font("Segoe UI Semibold", 10F);
        restoreOutcome.TextAlign = ContentAlignment.MiddleLeft;
        restoreOutcome.Visible = false;

        profilerPage.Controls.AddRange([
            logo, title, back, statusGroup, captureGroup, readyGroup,
            install, collect, restore, openResults, startCompanion, startGame, restoreOutcome
        ]);
        AddHeaderAccent(profilerPage, 59);
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
            RenderReadyState(lastStatus);
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
                "Game: Not found ❌\r\n" +
                "RED4ext: Not installed ❌\r\n" +
                "REDscript Profiler: unavailable ❌\r\n" +
                "G-REDscript PROFILER IS NOT INSTALLED. ❌\r\n" +
                "Live Files: -";
            ColorizeStatusText();
            RenderSetupGameStatus();
            RenderManagedFiles(null);
            RenderReadyState(null);
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
            RenderReadyState(snapshot);

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
            ColorizeStatusText();
            RenderSetupGameStatus();
            RenderManagedFiles(null);
            RenderReadyState(null);
            SetActionState(null);

            if (!silent)
                ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RenderSetupGameStatus()
    {
        if (!LooksLikeGameRoot(gameRoot.Text.Trim()))
        {
            setupGameStatus.Text = "Game: NOT FOUND";
            setupGameStatus.ForeColor = ThemeRed;
            return;
        }

        if (lastStatus is null)
        {
            setupGameStatus.Text = "Game: FOUND    RED4ext: checking...";
            setupGameStatus.ForeColor = ThemeAmber;
            return;
        }

        setupGameStatus.Text =
            $"Game: {(lastStatus.GameRootValid ? "FOUND" : "NOT FOUND")}    " +
            $"RED4ext: {(lastStatus.Red4extPresent ? "FOUND" : "NOT FOUND")}";

        setupGameStatus.ForeColor =
            lastStatus.GameRootValid && lastStatus.Red4extPresent
                ? ThemeGreen
                : ThemeRed;
    }

    private void RenderStatus(StatusInfo? snapshot)
    {
        if (snapshot is null)
        {
            status.Text = "Select a valid Cyberpunk 2077 folder.";
            ColorizeStatusText();
            SetActionState(null);
            return;
        }

        var profilerLine = snapshot.State switch
        {
            "INSTALLED_CURRENT" => "Installed and managed ✅",
            "PREEXISTING_CURRENT" => snapshot.CaptureTitlePresent
                ? "Exact profiler build already installed ✅"
                : "Profiler DLL present, data/metadata incomplete ⚠️",
            "NOT_INSTALLED" => "Ready to deploy DLL, results folder and metadata ⚠️",
            "INSTALLED_OTHER_VERSION" => "Different G-REDscript Profiler version already installed ❌",
            "INSTALLED_OTHER_PACKAGE" => "Different managed G-REDscript Profiler build installed ❌",
            "LEGACY_GRSP_PRESENT" => "Legacy alpha profiler DLL detected ❌",
            "STALE_DATA" => "Existing profiler data folder is not clean ❌",
            "MANAGED_DLL_CHANGED" => "Managed profiler DLL changed ⚠️",
            "MANAGED_DLL_MISSING" => "Managed profiler DLL missing · restore available ⚠️",
            _ => snapshot.Message + " ⚠️"
        };

        SyncSettingsFromUi();
        var companion = CompanionProfilerService.Inspect(appSettings);
        var companionConfigured = HasConfiguredCompanion();

        var frameLine = companionConfigured
            ? $"Frame-Time Profiler: {companion.DisplayName} found ✅"
            : "Frame-Time Profiler: Not provided ⚠️";

        string syncLines;
        if (!companionConfigured)
        {
            syncLines =
                "Synced keybind: Need frame capture tool ⚠️\r\n" +
                "    - G-REDscript Profiler: F11 ✅";
        }
        else if (companion.StartKeyKnown && companion.StartKeyIsF11)
        {
            syncLines =
                "Synced keybind: YES ✅\r\n" +
                "    - G-REDscript Profiler: F11 ✅\r\n" +
                "    - Frame-Time Profiler: F11 ✅";
        }
        else
        {
            var externalKey = companion.StartKeyKnown
                ? companion.StartKey + " ⚠️"
                : "Unknown ⚠️";
            syncLines =
                "Synced keybind: NO ⚠️\r\n" +
                "    - G-REDscript Profiler: F11 ✅\r\n" +
                $"    - Frame-Time Profiler: {externalKey}";
        }

        var installed = IsProfilerReady(snapshot);
        var blocked = HasCriticalProfilerError(snapshot);
        var installState = installed
            ? "INSTALLED. ✅"
            : blocked ? "BLOCKED. ❌" : "NOT INSTALLED. ⚠️";

        status.Text =
            $"Game: {(snapshot.GameRootValid ? "Found ✅" : "Not found ❌")}\r\n" +
            $"RED4ext: {(snapshot.Red4extPresent ? "Installed ✅" : "Not installed ❌")}\r\n" +
            $"REDscript Profiler: {profilerLine}\r\n" +
            "optional:\r\n" +
            frameLine + "\r\n" +
            syncLines + "\r\n\r\n" +
            $"G-REDscript PROFILER IS {installState}\r\n" +
            $"Live Files: {snapshot.CompletedCaptureCount}";

        ColorizeStatusText();
        SetActionState(snapshot);
    }

    private void ColorizeStatusText()
    {
        status.SuspendLayout();
        try
        {
            status.SelectAll();
            status.SelectionColor = ThemeText;

            ColorStatusMarkers("✅", ThemeGreen);
            ColorStatusMarkers("❌", ThemeRed);
            ColorStatusMarkers("⚠️", ThemeAmber);
            ColorStatusMarkers("⚠", ThemeAmber);

            status.Select(0, 0);
            status.SelectionLength = 0;
        }
        finally
        {
            status.ResumeLayout();
        }
    }

    private void ColorStatusMarkers(string marker, Color color)
    {
        var searchFrom = 0;
        while (searchFrom < status.TextLength)
        {
            var index = status.Text.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                break;

            status.Select(index, marker.Length);
            status.SelectionColor = color;
            searchFrom = index + marker.Length;
        }
    }

    private bool IsProfilerReady(StatusInfo? snapshot) =>
        snapshot is not null &&
        snapshot.GameRootValid &&
        snapshot.Red4extPresent &&
        snapshot.State is "INSTALLED_CURRENT" or "PREEXISTING_CURRENT" &&
        snapshot.CaptureTitlePresent;

    private static bool HasCriticalProfilerError(StatusInfo? snapshot) =>
        snapshot is null ||
        !snapshot.GameRootValid ||
        !snapshot.Red4extPresent ||
        snapshot.State is
            "INSTALLED_OTHER_VERSION" or
            "INSTALLED_OTHER_PACKAGE" or
            "LEGACY_GRSP_PRESENT" or
            "STALE_DATA" or
            "MANAGED_DLL_CHANGED" or
            "MANAGED_DLL_MISSING" or
            "INVALID_MANAGED_STATE";

    private bool HasConfiguredCompanion() =>
        pairFrameTime.Checked &&
        File.Exists(companionExe.Text.Trim()) &&
        Directory.Exists(companionResults.Text.Trim());

    private void RenderReadyState(StatusInfo? snapshot)
    {
        var ready = IsProfilerReady(snapshot);
        var blocked = HasCriticalProfilerError(snapshot);
        var installed = snapshot?.State is "INSTALLED_CURRENT" or "PREEXISTING_CURRENT";

        readyHeading.Text = ready
            ? "PROFILER IS READY!"
            : "PROFILER IS NOT READY!";

        readyHeading.ForeColor = ready
            ? ThemeGreen
            : blocked ? ThemeRed : ThemeAmber;

        readyInstallInstruction.Enabled = true;
        readyCaptureInstructions.Enabled = true;
        readyInstallInstruction.ForeColor = installed ? ThemeInactive : ThemeCyan;
        readyCaptureInstructions.ForeColor = ready ? ThemeText : ThemeInactive;
    }

    private void RenderManagedFiles(StatusInfo? snapshot)
    {
        managedFiles.Text = "After restore, the game folder will be reverted to its original state.";
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
        openResults.Enabled = !busy;

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

        var validInstalled = snapshot.State is "INSTALLED_CURRENT" or "PREEXISTING_CURRENT";

        install.Enabled = !busy && snapshot.GameRootValid && snapshot.Red4extPresent && snapshot.State == "NOT_INSTALLED";
        collect.Enabled = !busy && snapshot.CompletedCaptureCount > 0;
        // Any manager ownership marker must leave the user an exit path.
        restore.Enabled = !busy && snapshot.GameRootValid && snapshot.Red4extPresent && snapshot.ManagedStatePresent;
        saveCaptureTitle.Enabled = !busy && validInstalled && snapshot.CaptureTitlePresent;

        startCompanion.Enabled = !busy && HasConfiguredCompanion();

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

        }
        catch (Exception ex)
        {
            await SettleProfilerUiBeforeNotificationAsync();
            ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            await SettleProfilerUiBeforeNotificationAsync();
            ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            string? reportError = null;

            if (HasConfiguredCompanion())
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

            // Rebuild the same analysis report after the optional companion
            // copy so CapFrameX can become an evidence layer without changing
            // the native REDscript measurement.
            if (Directory.Exists(destination))
            {
                try
                {
                    await Task.Run(() => ResultReportService.Generate(destination));
                }
                catch (Exception ex)
                {
                    reportError = ex.Message;
                }

                var report = Path.Combine(destination, ResultReportService.ReportFileName);
                Process.Start(new ProcessStartInfo(File.Exists(report) ? report : destination)
                {
                    UseShellExecute = true
                });
            }

            if (companionError is not null || reportError is not null)
            {
                var warnings = new List<string>();
                if (companionError is not null)
                    warnings.Add("Frame-time companion copy failed: " + companionError);
                if (reportError is not null)
                    warnings.Add("Report rebuild failed: " + reportError);

                await SettleProfilerUiBeforeNotificationAsync();

                ThemedDialog.Show(
                    this,
                    "GRSP collection completed, but part of the optional post-processing needs attention.\r\n\r\n" +
                    string.Join("\r\n\r\n", warnings),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            await SettleProfilerUiBeforeNotificationAsync();
            ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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

            _ = await Task.Run(() => ManagerServices.Restore(gameRoot.Text.Trim()));
            var verified = await Task.Run(() => ManagerServices.GetStatus(gameRoot.Text.Trim()));

            if (verified.ManagedStatePresent || verified.DllPresent)
                throw new InvalidOperationException("Restore returned, but managed profiler state or DLL is still present.");

            lastStatus = verified;
            ShowRestoreOutcome(true, "RESTORE SUCCESSFUL — files returned to their original state.");
        }
        catch (Exception ex)
        {
            if (ManagerServices.IsGameRunning())
            {
                ShowRestoreOutcome(false, "RESTORE WAITING — close Cyberpunk 2077 and retry.");
                await SettleProfilerUiBeforeNotificationAsync();
                ThemedDialog.Show(
                    this,
                    "Cyberpunk 2077 is still running.\r\n\r\n" +
                    "Close Cyberpunk 2077, then click RESTORE ORIGINAL STATE again.",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            else
            {
                ShowRestoreOutcome(false, "RESTORE NOT COMPLETED — close anything locking the profiler files and retry.");
                await SettleProfilerUiBeforeNotificationAsync();
                ThemedDialog.Show(
                    this,
                    "Restore could not complete because a required profiler file or folder is unavailable or locked.\r\n\r\n" + ex.Message,
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
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
            ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StartFrameTimeTool()
    {
        var exe = companionExe.Text.Trim();
        if (!File.Exists(exe))
        {
            ThemedDialog.Show(this, "The configured frame-time profiler executable was not found.", Text,
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
            ThemedDialog.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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

    private async Task SettleProfilerUiBeforeNotificationAsync()
    {
        // Finish the dark UI repaint before a post-operation modal takes focus.
        SetBusy(false);
        await RefreshStatusAsync(silent: true);
        Refresh();
        Update();
        profilerPage.Refresh();
        profilerPage.Update();
        await Task.Delay(500);
    }

    private void ShowRestoreProgress(string message)
    {
        restoreOutcome.Text = "… " + message;
        restoreOutcome.ForeColor = ThemeText;
        restoreOutcome.Visible = true;
        restoreOutcome.BringToFront();
    }

    private void ShowRestoreOutcome(bool success, string message)
    {
        restoreOutcome.Text = (success ? "✓ " : "✗ ") + message;
        restoreOutcome.ForeColor = success ? ThemeGreen : ThemeRed;
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

    private void ApplyTheme()
    {
        ApplyThemeRecursive(this);

        setupPage.BackColor = ThemeBg;
        profilerPage.BackColor = ThemeBg;

        AccentButton(install, ThemeCyan);
        AccentButton(collect, ThemeCyan);
        AccentButton(openResults, ThemeCyan);
        AccentButton(startGame, ThemeCyan);
        AccentButton(startCompanion, ThemeCyan);
        AccentButton(saveCaptureTitle, ThemeCyan);
        AccentButton(restore, ThemeMagenta);

        readyGroup.BackColor = ThemePanelAlt;
        readyHeading.ForeColor = ThemeAmber;
        status.ForeColor = ThemeText;
        companionStatus.ForeColor = ThemeText;

        capFrameXLink.LinkColor = ThemeCyan;
        capFrameXLink.ActiveLinkColor = ThemeMagenta;
        capFrameXLink.VisitedLinkColor = ThemeCyan;
    }

    private static void ApplyThemeRecursive(Control root)
    {
        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case Panel panel:
                    panel.BackColor = (panel.Tag as string) switch
                    {
                        "grsp-accent-cyan" => ThemeBorder,
                        "grsp-accent-magenta" => ThemeMagenta,
                        _ => ThemeBg
                    };
                    panel.ForeColor = ThemeText;
                    break;

                case GroupBox group:
                    group.BackColor = ThemePanelAlt;
                    group.ForeColor = ThemeCyan;
                    group.FlatStyle = FlatStyle.Flat;
                    break;

                case RichTextBox richTextBox:
                    richTextBox.BackColor = ThemePanelAlt;
                    richTextBox.ForeColor = ThemeText;
                    richTextBox.BorderStyle = BorderStyle.None;
                    break;

                case TextBox textBox:
                    textBox.BackColor = ThemePanel;
                    textBox.ForeColor = ThemeText;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case Button button:
                    button.UseVisualStyleBackColor = false;
                    button.BackColor = ThemePanel;
                    button.ForeColor = ThemeText;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = 1;
                    button.FlatAppearance.BorderColor = ThemeBorder;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(19, 35, 44);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(23, 43, 53);
                    break;

                case CheckBox checkBox:
                    checkBox.BackColor = Color.Transparent;
                    checkBox.ForeColor = ThemeText;
                    break;

                case LinkLabel link:
                    link.BackColor = Color.Transparent;
                    link.ForeColor = ThemeCyan;
                    link.LinkColor = ThemeCyan;
                    link.ActiveLinkColor = ThemeMagenta;
                    break;

                case Label label:
                    label.BackColor = Color.Transparent;
                    if (label.ForeColor == SystemColors.GrayText)
                        label.ForeColor = ThemeMuted;
                    else if (label.ForeColor == SystemColors.ControlText ||
                             label.ForeColor == SystemColors.WindowText ||
                             label.ForeColor == Color.Black)
                        label.ForeColor = label.Font.Size >= 16F ? ThemeCyan : ThemeText;
                    break;
            }

            if (control.HasChildren)
                ApplyThemeRecursive(control);
        }
    }

    private static void AccentButton(Button button, Color accent)
    {
        button.ForeColor = accent;
        button.FlatAppearance.BorderColor = accent;
        button.Paint += (_, e) =>
        {
            if (button.Enabled)
                return;

            e.Graphics.Clear(ThemeDisabledSurface);
            using var border = new Pen(ThemeDisabledBorder);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, button.Width - 1), Math.Max(0, button.Height - 1));
            TextRenderer.DrawText(
                e.Graphics,
                button.Text,
                button.Font,
                button.ClientRectangle,
                ThemeInactive,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis);
        };
    }

    private static Control CreateHeaderLogo(Point location)
    {
        var logo = new PictureBox
        {
            Location = location,
            Size = new Size(68, 68),
            BackColor = Color.Transparent,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false,
            Image = LoadHeaderBrandImage()
        };
        return logo;
    }

    private static Image? LoadHeaderBrandImage()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("GRedscriptProfiler.GRedIcon.png");
            if (stream is null)
                return null;

            using var source = Image.FromStream(stream);
            using var full = new Bitmap(source);

            // The Windows icon keeps the complete emblem. The installer header uses a
            // mild presentation crop so the outer braces stay visible while the
            // G-RED wordmark remains large enough to read at header size.
            var cropWidth = Math.Max(1, (int)Math.Round(full.Width * 0.90));
            var cropHeight = Math.Max(1, (int)Math.Round(full.Height * 0.90));
            var cropX = Math.Max(0, (full.Width - cropWidth) / 2);
            var cropY = Math.Max(0, (full.Height - cropHeight) / 2);
            var crop = new Rectangle(cropX, cropY, cropWidth, cropHeight);

            return full.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }
        catch
        {
            // Branding must never prevent the profiler UI from starting.
            return null;
        }
    }

    private static void AddHeaderAccent(Control page, int y)
    {
        var cyan = new Panel
        {
            Tag = "grsp-accent-cyan",
            BackColor = ThemeBorder,
            Location = new Point(20, y),
            Size = new Size(820, 1)
        };
        var magenta = new Panel
        {
            Tag = "grsp-accent-magenta",
            BackColor = ThemeMagenta,
            Location = new Point(20, y + 1),
            Size = new Size(92, 1)
        };
        page.Controls.Add(cyan);
        page.Controls.Add(magenta);
        cyan.SendToBack();
        magenta.SendToBack();
    }

    private static bool LooksLikeGameRoot(string root) =>
        !string.IsNullOrWhiteSpace(root) &&
        Directory.Exists(root) &&
        File.Exists(GetGameExe(root));

    private static string GetGameExe(string root) =>
        Path.Combine(root ?? "", "bin", "x64", "Cyberpunk2077.exe");
}
