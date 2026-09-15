using System;
using System.Diagnostics;
using System.IO;
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
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v0.1.0/manifest.json";

    private const string PatchBaseUrl =
        "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/v0.1.0/";

    private static readonly HttpClient Http = new HttpClient();

    private readonly Label statusLabel;
    private readonly Button browseButton;
    private readonly Button patchButton;
    private readonly Button launchButton;

    private string wowPath = "";

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

        try
        {
            statusLabel.Text = "Checking for updates...";

            string manifestUrl =
                ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            string json = await Http.GetStringAsync(manifestUrl);

            PatchManifest? manifest = JsonSerializer.Deserialize<PatchManifest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest == null ||
                string.IsNullOrWhiteSpace(manifest.FileName) ||
                string.IsNullOrWhiteSpace(manifest.SourceFile) ||
                string.IsNullOrWhiteSpace(manifest.Sha256))
            {
                MessageBox.Show("The remote patch manifest is invalid.");
                statusLabel.Text = "Update check failed.";
                return;
            }

            string destination = Path.Combine(
                wowPath,
                "Data",
                manifest.FileName);

            if (File.Exists(destination))
            {
                string currentHash = CalculateSha256(destination);

                if (currentHash.Equals(
                    manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    statusLabel.Text =
                        $"Game is up to date.\n{manifest.FileName}";
                    return;
                }
            }

            statusLabel.Text = $"Downloading {manifest.FileName}...";

            string patchUrl = PatchBaseUrl + manifest.SourceFile;
            byte[] patchData = await Http.GetByteArrayAsync(patchUrl);

            string tempFile = Path.Combine(
                Path.GetTempPath(),
                "CoopsWoWLauncher-" + Guid.NewGuid().ToString("N") + ".tmp");

            await File.WriteAllBytesAsync(tempFile, patchData);

            try
            {
                string downloadedHash = CalculateSha256(tempFile);

                if (!downloadedHash.Equals(
                    manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Downloaded patch failed SHA-256 verification.",
                        "Patch Verification Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    statusLabel.Text = "Patch verification failed.";
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(tempFile, destination, true);

                string installedHash = CalculateSha256(destination);

                if (!installedHash.Equals(
                    manifest.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "Patch installation verification failed.",
                        "Patch Verification Failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    statusLabel.Text = "Patch verification failed.";
                    return;
                }

                statusLabel.Text =
                    $"Patch installed successfully.\n{manifest.FileName}";
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Patch Error",
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

    private static string CalculateSha256(string path)
    {
        using SHA256 sha = SHA256.Create();
        using FileStream stream = File.OpenRead(path);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash);
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

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = wowPath,
            UseShellExecute = true
        });
    }
}

public sealed class PatchManifest
{
    public string FileName { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

