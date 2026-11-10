namespace CoopsWoWLauncher;

/// <summary>
/// Optional HD Environment Pack section. Nothing downloads unless the player presses Download; the metadata check at
/// start-up is read-only. Enable/Disable only moves the already-installed pack (no download, mandatory patch-z untouched).
/// </summary>
public partial class Form1
{
    private Label hdStatus = null!;
    private ProgressBar hdBar = null!;
    private Button checkHdButton = null!;
    private Button downloadHdButton = null!;
    private Button installHdButton = null!;
    private Button toggleHdButton = null!;
    private Button removeHdButton = null!;
    private PackageMetadata? hdPackage;
    private string? hdFile;
    private bool hdBusy;
    private bool hdMutating;
    private bool hdCoreOk;

    private void InitializeHdPackControls()
    {
        ClientSize = new Size(605, 700);
        MinimumSize = Size;
        hdStatus = SectionLabel(535, 76);
        hdBar = SectionBar(613);
        checkHdButton = SectionButton("Check HD pack", 30, 637, 110, CheckHdPackAsync);
        downloadHdButton = SectionButton("Download", 148, 637, 96, DownloadHdPackAsync);
        installHdButton = SectionButton("Install", 252, 637, 82, () => ChangeHdPackAsync("install"));
        toggleHdButton = SectionButton("Disable", 342, 637, 82, () => ChangeHdPackAsync("toggle"));
        removeHdButton = SectionButton("Remove", 432, 637, 82, () => ChangeHdPackAsync("remove"));
        hdStatus.Text = "Optional HD Environment Pack · checking availability";
        UpdateHdButtons();
    }

    private void UpdateHdButtons()
    {
        var installed = string.IsNullOrEmpty(wowPath) ? null : HdPackInstaller.Installed(wowPath);
        checkHdButton.Enabled = !hdBusy;
        downloadHdButton.Enabled = !hdBusy && hdPackage is { InstallationEnabled: true } && hdCoreOk;
        installHdButton.Enabled = !hdBusy && !patchBusy && hdCoreOk && hdFile is not null && hdPackage is { InstallationEnabled: true };
        toggleHdButton.Enabled = !hdBusy && !patchBusy && installed is not null;
        toggleHdButton.Text = installed is { Enabled: false } ? "Enable" : "Disable";
        removeHdButton.Enabled = !hdBusy && !patchBusy && installed is not null;
    }

    private string HdInstalledText()
    {
        var r = string.IsNullOrEmpty(wowPath) ? null : HdPackInstaller.Installed(wowPath);
        return r is null ? "Not installed." : $"Installed {r.Version} · {(r.Enabled ? "enabled" : "disabled (kept, no download needed to re-enable)")}.";
    }

    private async Task CheckHdPackAsync()
    {
        if (hdBusy) return;
        hdBusy = true;
        UpdateHdButtons();
        hdBar.Visible = false;
        try
        {
            string client = wowPath;
            if (IsValidWowPath() && !hdMutating)
                await Task.Run(() => HdPackInstaller.RecoverIfNeededAsync(client));
            var package = await SignedPackageFeed.HdEnvironment(Http).CheckAsync();
            if (hdPackage?.Sha256 != package?.Sha256) hdFile = null;
            hdPackage = package;
            hdCoreOk = package is not null && IsValidWowPath() &&
                       await Task.Run(() => HdPackInstaller.CoreMatchesAsync(client, package.RequiredCoreSha256));
            if (package is null)
                hdStatus.Text = "Optional HD Environment Pack\n" +
                    (SignedPackageFeed.BuildSetting("HdEnvFeedKey").Length == 0 ? "Not available in this launcher build. " : "Currently unavailable. ") + HdInstalledText();
            else
                hdStatus.Text = $"Optional HD Environment Pack {package.Version} · {DownloadStatus.FormatBytes(package.Size)}\n" +
                    (!package.InstallationEnabled ? "Not yet available to install. " : hdCoreOk ? "Matches your game patch. " : "Install the current game patch first (Check / Install Patch). ") + HdInstalledText() +
                    "\nHigher-resolution world textures and props. Uses more memory; turn it off any time.";
        }
        catch (Exception ex)
        {
            hdPackage = null;
            hdFile = null;
            hdStatus.Text = "Optional HD Environment Pack\nCheck failed. You can retry.\n" + ex.Message;
        }
        finally
        {
            hdBusy = false;
            UpdateHdButtons();
        }
    }

    private async Task DownloadHdPackAsync()
    {
        if (hdBusy || hdPackage is not { } package) return;
        hdBusy = true;
        UpdateHdButtons();
        bool reporting = true;
        try
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoopsWoWLauncher", "HdEnvironment");
#if STAGING_FEEDS
            if (StagingSmoke is not null)
                cache = Path.Combine(AppContext.BaseDirectory, "staging-hd-cache");
#endif
            Directory.CreateDirectory(cache);
            HdPackInstaller.EnsureSpace(cache, package.Size);
            var progress = new Progress<DownloadStatus>(s => { if (reporting && !IsDisposed) RenderPackageProgress(hdStatus, hdBar, s); });
            hdFile = null;
            // Resumable: an interrupted download continues from its .partial file on the next press.
            hdFile = await Task.Run(() => SignedPackageFeed.HdEnvironment(Http).DownloadAsync(package, cache, DownloadKind.HdEnvironment, progress));
            hdBar.Visible = false;
            hdStatus.Text = $"Optional HD Environment Pack {package.Version}\nDownload verified. Choose Install to add it to this client.";
        }
        catch (Exception ex)
        {
            hdBar.Visible = false;
            hdStatus.Text = "HD Environment Pack download failed. Pressing Download again resumes it.\n" + ex.Message;
        }
        finally
        {
            reporting = false;
            hdBusy = false;
            UpdateHdButtons();
        }
    }

    private async Task ChangeHdPackAsync(string action)
    {
        if (hdBusy || patchBusy || graphicsMutating) return;
        string client = wowPath;
        hdBusy = hdMutating = true;
        browseButton.Enabled = patchButton.Enabled = launchButton.Enabled = false;
        UpdateHdButtons();
        bool reporting = true;
        try
        {
            var progress = new Progress<DownloadStatus>(s => { if (reporting && !IsDisposed) RenderPackageProgress(hdStatus, hdBar, s); });
            switch (action)
            {
                case "install":
                    if (hdPackage is not { InstallationEnabled: true } p || hdFile is null) return;
                    if (!await Task.Run(() => HdPackInstaller.CoreMatchesAsync(client, p.RequiredCoreSha256)))
                        throw new IOException("Your game patch does not match this HD pack. Run Check / Install Patch first.");
                    await Task.Run(() => HdPackInstaller.InstallAsync(client, p.Version, p.Sha256, p.Size, hdFile, progress));
                    hdStatus.Text = $"HD Environment Pack {p.Version} installed and enabled.\nIt applies the next time WoW starts.";
                    break;
                case "toggle":
                    bool enable = HdPackInstaller.Installed(client) is { Enabled: false };
                    await Task.Run(() => HdPackInstaller.SetEnabledAsync(client, enable));
                    hdStatus.Text = $"HD Environment Pack {(enable ? "enabled" : "disabled")}.\nThe change applies the next time WoW starts. Your game patch was not changed.";
                    break;
                case "remove":
                    await Task.Run(() => HdPackInstaller.RemoveAsync(client));
                    hdStatus.Text = "HD Environment Pack removed.\nThe standard world assets are used the next time WoW starts.";
                    break;
            }
        }
        catch (Exception ex)
        {
            hdStatus.Text = "HD Environment Pack change failed. Nothing else was changed.\n" + ex.Message;
        }
        finally
        {
            reporting = false;
            hdBar.Visible = false;
            hdBusy = hdMutating = false;
            browseButton.Enabled = true;
            patchButton.Enabled = launchButton.Enabled = IsValidWowPath();
            UpdateHdButtons();
        }
    }
}
