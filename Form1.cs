using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;

namespace CoopsWoWLauncher;

public partial class Form1 : Form
{
    private readonly string settingsFile =
        Path.Combine(AppContext.BaseDirectory, "launcher_path.txt");

    private const string ManifestUrl =
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v1.2.0/manifest.json";

    private const string PatchBaseUrl =
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v1.2.0/";

    private static readonly HttpClient Http = new HttpClient();

    private readonly Label statusLabel;
    private readonly ProgressBar downloadBar;
    private readonly Button browseButton;
    private readonly Button patchButton;
    private readonly Button launchButton;

    private string wowPath = "";

    // null = not checked yet; false = last check/repair failed (Launch asks first).
    private bool? releaseVerified;

    private static readonly Version LauncherVersion =
        typeof(Form1).Assembly.GetName().Version ?? new Version(1, 0);

    public Form1()
    {
        InitializeComponent();

        Text = "Coop's WoW Launcher";
        Width = 620;
        Height = 320;
        StartPosition = FormStartPosition.CenterScreen;

        statusLabel = new Label
        {
            Left = 30,
            Top = 25,
            Width = 540,
            Height = 90
        };

        downloadBar = new ProgressBar
        {
            Left = 30,
            Top = 112,
            Width = 540,
            Height = 16,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            MarqueeAnimationSpeed = 30,
            Visible = false
        };

        browseButton = new Button
        {
            Text = "Choose WoW Folder",
            Left = 55,
            Top = 156,
            Width = 160,
            Height = 40
        };

        patchButton = new Button
        {
            Text = "Check / Install Patch",
            Left = 230,
            Top = 156,
            Width = 170,
            Height = 40
        };

        launchButton = new Button
        {
            Text = "Launch WoW",
            Left = 415,
            Top = 156,
            Width = 140,
            Height = 40
        };

        browseButton.Click += BrowseButton_Click;
        patchButton.Click += PatchButton_Click;
        launchButton.Click += LaunchButton_Click;

        Controls.Add(statusLabel);
        Controls.Add(downloadBar);
        Controls.Add(browseButton);
        Controls.Add(patchButton);
        Controls.Add(launchButton);
        InitializePackageControls();
        InitializeHdPackControls();
        InitializeClientOptionsControls();

        LoadSavedPath();
        RefreshStatus();

        Shown += Form1_Shown;
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
#if STAGING_FEEDS
        if (StagingSmoke is { } smoke)
        {
            await RunStagingSmokeAsync(smoke);
            return;
        }
#endif
        Task launcher = CheckLauncherAsync();
        Task graphics = CheckLumenveilAsync();
        Task hd = CheckHdPackAsync();
        Task patch = IsValidWowPath() ? CheckAndInstallPatchAsync() : Task.CompletedTask;
        await Task.WhenAll(launcher, graphics, hd, patch);
    }

    private bool IsValidWowPath()
    {
        if (string.IsNullOrWhiteSpace(wowPath))
            return false;

        return File.Exists(Path.Combine(wowPath, "Wow.exe"));
    }

    private void LoadSavedPath()
    {
        if (File.Exists(settingsFile))
        {
            string saved = File.ReadAllText(settingsFile).Trim();

            if (Directory.Exists(saved))
                wowPath = saved;
        }

        if (string.IsNullOrWhiteSpace(wowPath) &&
            Directory.Exists(@"C:\WoWClient"))
        {
            wowPath = @"C:\WoWClient";
        }
    }

    private void SavePath()
    {
        File.WriteAllText(settingsFile, wowPath);
    }

    private void RefreshStatus()
    {
        string exe = string.IsNullOrWhiteSpace(wowPath)
            ? ""
            : Path.Combine(wowPath, "Wow.exe");

        bool validWow = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);

        launchButton.Enabled = validWow;
        patchButton.Enabled = validWow;
        if (optionsButton is not null) optionsButton.Enabled = validWow;

        if (validWow)
            statusLabel.Text = $"WoW detected:\n{wowPath}";
        else
            statusLabel.Text = "WoW folder not selected or Wow.exe was not found.";
    }

    private void BrowseButton_Click(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new FolderBrowserDialog
        {
            Description = "Select your World of Warcraft folder"
        };

        if (!string.IsNullOrWhiteSpace(wowPath) && Directory.Exists(wowPath))
            dialog.SelectedPath = wowPath;

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            string exe = Path.Combine(dialog.SelectedPath, "Wow.exe");

            if (!File.Exists(exe))
            {
                MessageBox.Show(
                    "Wow.exe was not found in that folder.",
                    "Invalid WoW Folder",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            wowPath = dialog.SelectedPath;
            graphicsCompatible = false;
            UpdatePackageButtons(); UpdateHdButtons();
            SavePath();
            RefreshStatus();
            _ = RefreshGraphicsClientAsync();
            _ = CheckHdPackAsync();
        }
    }

    private async void PatchButton_Click(object? sender, EventArgs e)
    {
        await CheckAndInstallPatchAsync();
    }

    private async Task CheckAndInstallPatchAsync()
    {
        if (patchBusy || graphicsMutating || hdMutating)
            return;

        if (!IsValidWowPath())
        {
            RefreshStatus();
            return;
        }

        patchBusy = true;
        UpdatePackageButtons(); UpdateHdButtons();
        browseButton.Enabled = false;
        patchButton.Enabled = false;
        launchButton.Enabled = false;

        bool showProgress = true;
        var progress = new Progress<string>(s =>
        {
            if (showProgress) ShowStatus(s);
        });
        IProgress<DownloadStatus> downloadProgress = new Progress<DownloadStatus>(s =>
        {
            if (showProgress) ShowDownloadStatus(s);
        });
        var updater = new ReleaseUpdater(
            wowPath,
            new HttpPayloadSource(Http, PatchBaseUrl),
            LauncherVersion,
            progress,
            downloadProgress);

        bool releaseApplied = false;
        string activeCategory = "Game patch";

        try
        {
            // Finish or undo an interrupted install first; works even when offline.
            RecoveryResult? recovery = await updater.RecoverInterruptedInstallAsync();

            ShowStatus("Checking for updates...");

            string manifestUrl =
                ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string json = await Http.GetStringAsync(manifestUrl);

            UpdateManifest manifest = ReleaseUpdater.ParseManifest(json);
            updater.ValidateManifest(manifest);

            foreach (UpdateFile file in manifest.Files)
            {
                if (!file.Type.Equals("file", StringComparison.OrdinalIgnoreCase) &&
                    !file.Type.Equals("addon", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Unknown update type '{file.Type}' for {file.Name}.");
                }
            }

            // Release patch files: baseline-checked, staged, verified, atomically
            // activated and rolled back on failure.
            ReleaseResult release = await Task.Run(() => updater.ApplyAsync(manifest));

            // Patch files are verified; an addon problem below must not block launching.
            releaseApplied = true;
            releaseVerified = true;

            int updated = release.Installed.Count;

            activeCategory = "Addon";
            var addons = manifest.Files.Where(file =>
                file.Type.Equals("addon", StringComparison.OrdinalIgnoreCase) &&
                !IsAddonCurrent(file)).ToList();
            int queueCount = addons.Sum(file => file.DownloadCount);
            int position = 1;
            foreach (UpdateFile file in addons)
            {
                var item = new DownloadItem(DownloadKind.Addon, file.SourceFile, position, queueCount);
                if (await Task.Run(() => InstallAddonAsync(file, updater, item, downloadProgress)))
                    updated++;
                position += file.DownloadCount;
            }

            string text = updated == 0 && !release.Changed
                ? "Game is up to date."
                : $"Update complete.\n{updated} component(s) installed.";

            if (release.Quarantined.Count > 0)
                text += $"\nOld patches were moved to {ReleaseUpdater.StateDirName}\\backups.";

            if (release.RecoveryNote != null || recovery?.RolledBack == true)
                text += "\nRecovered from an interrupted update.";

            if (release.Warnings.Count > 0)
                text += "\nNote: " + string.Join("; ", release.Warnings);

            showProgress = false;
            ShowStatus(text);
        }
        catch (ReleaseUpdateException ex)
        {
            showProgress = false;
            ShowStatus($"{activeCategory} update failed.\n" + ex.Message.Split('\n')[0]);
            if (!releaseApplied)
                releaseVerified = false;

            MessageBox.Show(
                ex.UserText,
                "Update Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            showProgress = false;
            ShowStatus($"{activeCategory} update failed.");
            // Network and addon errors say nothing about the installed patch files.
            if (!releaseApplied && ex is not HttpRequestException and not TaskCanceledException)
                releaseVerified = false;

            MessageBox.Show(
                ex.Message + "\n\nYour game files were not changed by this error. " +
                "Check your internet connection and press Check / Install Patch to try again.",
                "Update Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            patchBusy = false;
            browseButton.Enabled = true;
            patchButton.Enabled = IsValidWowPath();
            launchButton.Enabled = IsValidWowPath();
            UpdatePackageButtons(); UpdateHdButtons();
        }
    }

    private void ShowStatus(string text)
    {
        downloadBar.Visible = false;
        statusLabel.Text = text;
    }

    private void ShowDownloadStatus(DownloadStatus status) => RenderProgress(statusLabel, downloadBar, status);

    private static void RenderProgress(Label label, ProgressBar bar, DownloadStatus status)
    {
        DownloadItem item = status.Item;
        if (status.Phase != DownloadPhase.Downloading)
        {
            bar.Visible = false;
            string phase = status.Phase == DownloadPhase.ReadyToRestart ? "Ready to restart" : status.Phase.ToString();
            label.Text = $"{item.DisplayName}\n{phase} · {item.Filename}";
            return;
        }

        string queue = item.Count > 1 ? $" · File {item.Position} of {item.Count}" : "";
        string text = $"{item.DisplayName}{queue}\nDownloading {item.Filename}";
        label.Text = text + "\n" + status.TransferText;
        if (status.Percent is int percent)
        {
            bar.Style = ProgressBarStyle.Continuous;
            bar.Value = percent;
        }
        else
        {
            bar.Style = ProgressBarStyle.Marquee;
        }
        bar.Visible = true;
    }

    private string AddonDestination(UpdateFile file) =>
        Path.Combine(wowPath, file.Destination.Replace('/', Path.DirectorySeparatorChar));

    private bool IsAddonCurrent(UpdateFile file)
    {
        string marker = Path.Combine(AddonDestination(file), ".coops-launcher-version");
        return File.Exists(marker) &&
            File.ReadAllText(marker).Trim().Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> InstallAddonAsync(UpdateFile file, ReleaseUpdater updater, DownloadItem item,
        IProgress<DownloadStatus> progress)
    {
        if (IsAddonCurrent(file))
            return false;

        string destination = AddonDestination(file);
        string zipFile = await updater.DownloadAndVerifyAsync(file, item, default);

        string extractRoot = Path.Combine(
            Path.GetTempPath(),
            "CoopsWoWLauncher-" + Guid.NewGuid().ToString("N"));

        try
        {
            progress.Report(new(item, DownloadPhase.Extracting));
            Directory.CreateDirectory(extractRoot);

            System.IO.Compression.ZipFile.ExtractToDirectory(
                zipFile,
                extractRoot);

            string extractedAddon = Path.Combine(
                extractRoot,
                Path.GetFileName(destination));

            if (!Directory.Exists(extractedAddon))
            {
                throw new InvalidDataException(
                    $"{file.Name} package does not contain the expected addon folder.");
            }

            string[] tocFiles = Directory.GetFiles(
                extractedAddon,
                "*.toc",
                SearchOption.TopDirectoryOnly);

            if (tocFiles.Length == 0)
            {
                throw new InvalidDataException(
                    $"{file.Name} package does not contain a TOC file.");
            }

            string? addonParent = Path.GetDirectoryName(destination);

            if (string.IsNullOrWhiteSpace(addonParent))
                throw new InvalidDataException(
                    $"Invalid addon destination for {file.Name}.");

            progress.Report(new(item, DownloadPhase.Installing));
            Directory.CreateDirectory(addonParent);

            string backup = destination + ".coops-launcher-old";

            if (Directory.Exists(backup))
                Directory.Delete(backup, true);

            bool hadExisting = Directory.Exists(destination);

            if (hadExisting)
                Directory.Move(destination, backup);

            try
            {
                Directory.Move(extractedAddon, destination);

                File.WriteAllText(
                    Path.Combine(destination, ".coops-launcher-version"),
                    file.Sha256);
            }
            catch
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination, true);

                if (hadExisting && Directory.Exists(backup))
                    Directory.Move(backup, destination);

                throw;
            }

            if (Directory.Exists(backup))
                Directory.Delete(backup, true);

            return true;
        }
        finally
        {
            if (File.Exists(zipFile))
                File.Delete(zipFile);

            if (Directory.Exists(extractRoot))
                Directory.Delete(extractRoot, true);
        }
    }

    private async void LaunchButton_Click(object? sender, EventArgs e)
    {
        string exe = Path.Combine(wowPath, "Wow.exe");

        if (!File.Exists(exe))
        {
            MessageBox.Show("Wow.exe could not be found.");
            RefreshStatus();
            return;
        }

        if (releaseVerified == false)
        {
            DialogResult answer = MessageBox.Show(
                "The last update check did not complete, so your patch files may be missing, " +
                "outdated or incomplete. The game may not work correctly on the server.\n\n" +
                "Launch anyway?",
                "Patch Not Verified",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
                return;
        }

        try
        {
            await StartWowAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show("World of Warcraft could not be started.\n" + ex.Message);
        }
    }

    // Starts Wow.exe. With the optional Lumenveil package installed, the process also gets the environment that
    // enables its client-local ReShade layer; without it, WoW starts exactly as before.
    internal async Task<Process?> StartWowAsync()
    {
        string client = wowPath;
        Dictionary<string, string> environment = await LumenveilInstaller.LaunchEnvironmentAsync(client);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(client, "Wow.exe"),
            WorkingDirectory = client,
            UseShellExecute = environment.Count == 0
        };
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        return Process.Start(start);
    }
}
