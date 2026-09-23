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

            UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (manifest?.Files == null || manifest.Files.Count == 0)
                throw new InvalidDataException("The remote update manifest is invalid.");

            int updated = 0;

            foreach (UpdateFile file in manifest.Files)
            {
                ValidateManifestEntry(file);

                if (file.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    if (await InstallFileAsync(file))
                        updated++;
                }
                else if (file.Type.Equals("addon", StringComparison.OrdinalIgnoreCase))
                {
                    if (await InstallAddonAsync(file))
                        updated++;
                }
                else
                {
                    throw new InvalidDataException(
                        $"Unknown update type '{file.Type}' for {file.Name}.");
                }
            }

            statusLabel.Text = updated == 0
                ? "Game is up to date."
                : $"Update complete.\n{updated} component(s) installed.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
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

    private static void ValidateManifestEntry(UpdateFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Name) ||
            string.IsNullOrWhiteSpace(file.SourceFile) ||
            string.IsNullOrWhiteSpace(file.Sha256) ||
            string.IsNullOrWhiteSpace(file.Type) ||
            string.IsNullOrWhiteSpace(file.Destination))
        {
            throw new InvalidDataException(
                "The remote update manifest contains an invalid entry.");
        }

        if (file.Destination.Contains(".."))
            throw new InvalidDataException(
                $"Unsafe destination in manifest for {file.Name}.");
    }

    private async Task<bool> InstallFileAsync(UpdateFile file)
    {
        string destination = Path.Combine(
            wowPath,
            file.Destination.Replace('/', Path.DirectorySeparatorChar));

        if (File.Exists(destination))
        {
            string currentHash = CalculateSha256(destination);

            if (currentHash.Equals(
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        statusLabel.Text = $"Downloading {file.Name}...";
        Application.DoEvents();

        string tempFile = await DownloadAndVerifyAsync(file);

        try
        {
            string? directory = Path.GetDirectoryName(destination);

            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.Copy(tempFile, destination, true);

            string installedHash = CalculateSha256(destination);

            if (!installedHash.Equals(
                file.Sha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{file.Name} failed verification after installation.");
            }

            return true;
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    private async Task<bool> InstallAddonAsync(UpdateFile file)
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
        Application.DoEvents();

        string zipFile = await DownloadAndVerifyAsync(file);

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

    private async Task<string> DownloadAndVerifyAsync(UpdateFile file)
    {
        string url = PatchBaseUrl + file.SourceFile;

        byte[] data = await Http.GetByteArrayAsync(url);

        string tempFile = Path.Combine(
            Path.GetTempPath(),
            "CoopsWoWLauncher-" + Guid.NewGuid().ToString("N") + ".tmp");

        await File.WriteAllBytesAsync(tempFile, data);

        string downloadedHash = CalculateSha256(tempFile);

        if (!downloadedHash.Equals(
            file.Sha256,
            StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tempFile);

            throw new InvalidDataException(
                $"{file.Name} failed SHA-256 verification.");
        }

        return tempFile;
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

public sealed class UpdateManifest
{
    public System.Collections.Generic.List<UpdateFile> Files { get; set; } = new();
}

public sealed class UpdateFile
{
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Type { get; set; } = "";
    public string Destination { get; set; } = "";
}

