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
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v0.2.0/manifest.json";

    private const string PatchBaseUrl =
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v0.2.0/";

    private static readonly HttpClient Http = new HttpClient();

    private readonly Label statusLabel;
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
        Height = 290;
        StartPosition = FormStartPosition.CenterScreen;

        statusLabel = new Label
        {
            Left = 30,
            Top = 25,
            Width = 540,
            Height = 90
        };

        browseButton = new Button
        {
            Text = "Choose WoW Folder",
            Left = 55,
            Top = 140,
            Width = 160,
            Height = 40
        };

        patchButton = new Button
        {
            Text = "Check / Install Patch",
            Left = 230,
            Top = 140,
            Width = 170,
            Height = 40
        };

        launchButton = new Button
        {
            Text = "Launch WoW",
            Left = 415,
            Top = 140,
            Width = 140,
            Height = 40
        };

        browseButton.Click += BrowseButton_Click;
        patchButton.Click += PatchButton_Click;
        launchButton.Click += LaunchButton_Click;

        Controls.Add(statusLabel);
        Controls.Add(browseButton);
        Controls.Add(patchButton);
        Controls.Add(launchButton);

        LoadSavedPath();
        RefreshStatus();

        Shown += Form1_Shown;
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
        if (!IsValidWowPath())
            return;

        await CheckAndInstallPatchAsync();
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
            SavePath();
            RefreshStatus();
        }
    }

    private async void PatchButton_Click(object? sender, EventArgs e)
    {
        await CheckAndInstallPatchAsync();
    }

    private async Task CheckAndInstallPatchAsync()
    {
        if (!IsValidWowPath())
        {
            RefreshStatus();
            return;
        }

        patchButton.Enabled = false;
        launchButton.Enabled = false;

        var progress = new Progress<string>(s => statusLabel.Text = s);
        var updater = new ReleaseUpdater(
            wowPath,
            new HttpPayloadSource(Http, PatchBaseUrl),
            LauncherVersion,
            progress);

        bool releaseApplied = false;

        try
        {
            // Finish or undo an interrupted install first; works even when offline.
            RecoveryResult? recovery = await updater.RecoverInterruptedInstallAsync();

            statusLabel.Text = "Checking for updates...";

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

            foreach (UpdateFile file in manifest.Files)
            {
                if (file.Type.Equals("addon", StringComparison.OrdinalIgnoreCase) &&
                    await InstallAddonAsync(file, updater))
                {
                    updated++;
                }
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

            statusLabel.Text = text;
        }
        catch (ReleaseUpdateException ex)
        {
            if (!releaseApplied)
                releaseVerified = false;

            MessageBox.Show(
                ex.UserText,
                "Update Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            statusLabel.Text = "Update check failed.\n" + ex.Message.Split('\n')[0];
        }
        catch (Exception ex)
        {
            // Network and addon errors say nothing about the installed patch files.
            if (!releaseApplied && ex is not HttpRequestException and not TaskCanceledException)
                releaseVerified = false;

            MessageBox.Show(
                ex.Message + "\n\nYour game files were not changed by this error. " +
                "Check your internet connection and press Check / Install Patch to try again.",
                "Update Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            statusLabel.Text = "Update check failed.";
        }
        finally
        {
            patchButton.Enabled = IsValidWowPath();
            launchButton.Enabled = IsValidWowPath();
        }
    }

    private async Task<bool> InstallAddonAsync(UpdateFile file, ReleaseUpdater updater)
    {
        string destination = Path.Combine(
            wowPath,
            file.Destination.Replace('/', Path.DirectorySeparatorChar));

        string marker = Path.Combine(
            destination,
            ".coops-launcher-version");

        if (Directory.Exists(destination) && File.Exists(marker))
        {
            string installedVersion = File.ReadAllText(marker).Trim();

            if (installedVersion.Equals(
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        statusLabel.Text = $"Downloading {file.Name}...";

        string zipFile = await updater.DownloadAndVerifyAsync(file);

        string extractRoot = Path.Combine(
            Path.GetTempPath(),
            "CoopsWoWLauncher-" + Guid.NewGuid().ToString("N"));

        try
        {
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

    private void LaunchButton_Click(object? sender, EventArgs e)
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

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = wowPath,
            UseShellExecute = true
        });
    }
}
