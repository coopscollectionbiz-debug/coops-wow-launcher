using System.Diagnostics;
namespace CoopsWoWLauncher;

public partial class Form1
{
    private Label launcherStatus = null!;
    private Label graphicsStatus = null!;
    private ProgressBar launcherBar = null!;
    private ProgressBar graphicsBar = null!;
    private Button checkLauncherButton = null!;
    private Button updateLauncherButton = null!;
    private Button checkGraphicsButton = null!;
    private Button downloadGraphicsButton = null!;
    private Button installGraphicsButton = null!;
    private Button removeGraphicsButton = null!;
    private PackageMetadata? launcherPackage;
    private PackageMetadata? graphicsPackage;
    private string? graphicsZip;
    private bool launcherReady;
    private bool launcherBusy;
    private bool graphicsBusy;
    private bool graphicsMutating;
    private bool graphicsCompatible;
    private bool patchBusy;

    internal Action? StartupCheckpoint { get; init; }

    protected override void OnShown(EventArgs e)
    {
        StartupCheckpoint?.Invoke();
        base.OnShown(e);
    }

    private void InitializePackageControls()
    {
        ClientSize = new Size(605, 575);
        MinimumSize = Size;
        launcherStatus = SectionLabel(222, 68);
        launcherBar = SectionBar(294);
        checkLauncherButton = SectionButton("Check for launcher update", 30, 316, 188, CheckLauncherAsync);
        updateLauncherButton = SectionButton("Download update", 226, 316, 173, UpdateLauncherAsync);
        graphicsStatus = SectionLabel(367, 92);
        graphicsBar = SectionBar(465);
        checkGraphicsButton = SectionButton("Check Lumenveil", 30, 489, 120, CheckLumenveilAsync);
        downloadGraphicsButton = SectionButton("Download Lumenveil", 158, 489, 151, DownloadLumenveilAsync);
        installGraphicsButton = SectionButton("Install", 317, 489, 82, InstallLumenveilAsync);
        removeGraphicsButton = SectionButton("Remove", 407, 489, 82, RemoveLumenveilAsync);
        launcherStatus.Text = "Launcher update";
        graphicsStatus.Text = "Optional Graphics\nLumenveil · checking availability";
        UpdatePackageButtons();
    }

    private Label SectionLabel(int top, int height)
    {
        var label = new Label { Left = 30, Top = top, Width = 540, Height = height,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(label);
        return label;
    }

    private ProgressBar SectionBar(int top)
    {
        var bar = new ProgressBar { Left = 30, Top = top, Width = 540, Height = 16,
            Visible = false, MarqueeAnimationSpeed = 30,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(bar);
        return bar;
    }

    private Button SectionButton(string text, int left, int top, int width, Func<Task> action)
    {
        var button = new Button { Left = left, Top = top, Width = width, Height = 34, Text = text };
        button.Click += async (_, _) => await action();
        Controls.Add(button);
        return button;
    }

    private void UpdatePackageButtons()
    {
        checkLauncherButton.Enabled = !launcherBusy;
        updateLauncherButton.Enabled = !launcherBusy && launcherPackage is not null &&
            (!launcherReady || (!patchBusy && !graphicsBusy));
        updateLauncherButton.Text = launcherReady ? "Restart to update" : "Download update";
        checkGraphicsButton.Enabled = !graphicsBusy;
        downloadGraphicsButton.Enabled = !graphicsBusy && graphicsPackage is not null;
        installGraphicsButton.Enabled = !graphicsBusy && !patchBusy && graphicsCompatible && graphicsZip is not null;
        removeGraphicsButton.Enabled = !graphicsBusy && !patchBusy && LumenveilInstaller.HasState(wowPath);
    }

    private async Task CheckLauncherAsync()
    {
        if (launcherBusy) return;
        launcherBusy = true;
        UpdatePackageButtons();
        launcherBar.Visible = false;
        launcherStatus.Text = "Checking for a launcher update…";
        try
        {
            string? recovery = LauncherHandoff.TakeFailure();
            if (recovery is not null)
            {
                launcherStatus.Text = "Launcher update\n" + recovery;
                return;
            }

            var package = await SignedPackageFeed.Launcher(Http).CheckAsync();
            if (package is not null && Version.Parse(package.Version) > LauncherHandoff.CurrentVersion)
            {
                if (launcherPackage?.Sha256 != package.Sha256 || launcherPackage.Version != package.Version)
                    launcherReady = false;
                launcherPackage = package;
                if (launcherReady)
                    RenderProgress(launcherStatus, launcherBar,
                        new(new(DownloadKind.LauncherUpdate, package.Filename), DownloadPhase.ReadyToRestart));
                else
                    launcherStatus.Text = $"Launcher update {package.Version} available\n" +
                        $"{DownloadStatus.FormatBytes(package.Size)} · Download now or continue playing.";
            }
            else
            {
                launcherPackage = null;
                launcherReady = false;
                launcherStatus.Text = SignedPackageFeed.BuildSetting("LauncherFeedKey").Length == 0
                    ? $"Launcher {LauncherHandoff.CurrentVersion} · update feed unavailable"
                    : $"Launcher {LauncherHandoff.CurrentVersion} · no newer update available";
            }
        }
        catch (Exception ex)
        {
            launcherStatus.Text = "Launcher update check failed. You can retry.\n" + ex.Message;
        }
        finally
        {
            launcherBusy = false;
            UpdatePackageButtons();
        }
    }

    private async Task UpdateLauncherAsync()
    {
        if (launcherBusy || launcherPackage is not { } package) return;
        if (launcherReady && (patchBusy || graphicsBusy)) return;
        launcherBusy = true;
        UpdatePackageButtons();
        var item = new DownloadItem(DownloadKind.LauncherUpdate, package.Filename);
        bool reporting = true;
        try
        {
            if (launcherReady)
            {
                RenderPackageProgress(launcherStatus, launcherBar, new(item, DownloadPhase.Restarting));
                LauncherHandoff.StartUpdate(package);
                Close();
                return;
            }

            string state = LauncherHandoff.PrepareDownload();
            var progress = new Progress<DownloadStatus>(s =>
            {
                if (reporting && !IsDisposed) RenderPackageProgress(launcherStatus, launcherBar, s);
            });
            string payload = await Task.Run(() =>
                SignedPackageFeed.Launcher(Http).DownloadAsync(package, state, item.Kind, progress));
            LauncherHandoff.VerifyVersion(payload, package.Version);
            launcherReady = true;
            RenderPackageProgress(launcherStatus, launcherBar, new(item, DownloadPhase.ReadyToRestart));
        }
        catch (Exception ex)
        {
            launcherReady = false;
            launcherBar.Visible = false;
            launcherStatus.Text = "Launcher update failed.\n" + ex.Message;
        }
        finally
        {
            reporting = false;
            launcherBusy = false;
            if (!IsDisposed) UpdatePackageButtons();
        }
    }

    private async Task CheckLumenveilAsync()
    {
        if (graphicsBusy) return;
        graphicsBusy = true;
        UpdatePackageButtons();
        graphicsBar.Visible = false;
        graphicsStatus.Text = "Optional Graphics\nChecking Lumenveil availability…";
        try
        {
            var package = await SignedPackageFeed.Lumenveil(Http).CheckAsync();
            if (graphicsPackage?.Sha256 != package?.Sha256)
                graphicsZip = null;
            graphicsPackage = package;
            await RefreshGraphicsClientAsync();
        }
        catch (Exception ex)
        {
            graphicsPackage = null;
            graphicsZip = null;
            graphicsCompatible = false;
            graphicsStatus.Text = "Optional Graphics\nLumenveil is unavailable.\n" + ex.Message;
        }
        finally
        {
            graphicsBusy = false;
            UpdatePackageButtons();
        }
    }

    private async Task RefreshGraphicsClientAsync()
    {
        string client = wowPath;
        PackageMetadata? package = graphicsPackage;
        try
        {
            if (!graphicsMutating)
                await Task.Run(() => LumenveilInstaller.RecoverIfNeededAsync(client));
            bool compatible = package is not null && await LumenveilInstaller.IsCompatibleAsync(client, package);
            string? installed = LumenveilInstaller.InstalledVersion(client);
            bool vulkan = LumenveilInstaller.VulkanRuntimeAvailable();
            bool withoutVulkanFiles = LumenveilInstaller.InstalledWithoutVulkanFiles(client, package);
            if (IsDisposed || client != wowPath || package != graphicsPackage) return;
            graphicsCompatible = compatible;
            if (package is null)
                graphicsStatus.Text = installed is null ? "Optional Graphics\nLumenveil is currently unavailable."
                    : $"Optional Graphics\nLumenveil {installed} is installed in this client.";
            else
            {
                string prerequisites = package.Prerequisites is { Length: > 0 }
                    ? string.Join(", ", package.Prerequisites) : "No published prerequisites";
                string builds = package.SupportedBuilds is { Length: > 0 } buildsList
                    ? string.Join(", ", buildsList) : "No supported builds published";
                string compatibility = installed is not null
                    ? withoutVulkanFiles
                        ? vulkan ? $"Lumenveil {installed} is installed without DXVK and ReShade. Vulkan is now available: choose Install to add them."
                            : $"Lumenveil {installed} is installed without DXVK and ReShade (no Vulkan driver found)."
                        : $"Lumenveil {installed} is installed."
                    : compatible
                        ? vulkan ? "Selected client is supported."
                            : "Selected client is supported. No Vulkan driver was found, so Lumenveil will be installed without DXVK and ReShade."
                    : "Installation is unavailable for the selected client.";
                graphicsStatus.Text = $"Optional Graphics · Lumenveil {package.Version} · {DownloadStatus.FormatBytes(package.Size)}\n" +
                    $"{builds}. {compatibility}\nPrerequisites · {prerequisites}";
            }
        }
        catch (Exception ex)
        {
            if (IsDisposed || client != wowPath || package != graphicsPackage) return;
            graphicsCompatible = false;
            graphicsStatus.Text = "Optional Graphics\nLumenveil client check failed.\n" + ex.Message;
        }
        UpdatePackageButtons();
    }

    private async Task DownloadLumenveilAsync()
    {
        if (graphicsBusy || graphicsPackage is not { } package) return;
        graphicsBusy = true;
        UpdatePackageButtons();
        bool reporting = true;
        try
        {
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CoopsWoWLauncher", "Lumenveil");
#if STAGING_FEEDS
            if (StagingSmoke is not null)
                cache = Path.Combine(AppContext.BaseDirectory, "staging-graphics-cache");
#endif
            var progress = new Progress<DownloadStatus>(s =>
            {
                if (reporting && !IsDisposed) RenderPackageProgress(graphicsStatus, graphicsBar, s);
            });
            graphicsZip = null;
            graphicsZip = await Task.Run(() => SignedPackageFeed.Lumenveil(Http).DownloadAsync(
                package, cache, DownloadKind.Lumenveil, progress));
            graphicsBar.Visible = false;
            graphicsStatus.Text = $"Optional Graphics · Lumenveil {package.Version}\n" +
                (graphicsCompatible ? "Download verified. Choose Install to apply it." : "Download verified. A supported client is required to install.");
        }
        catch (Exception ex)
        {
            graphicsBar.Visible = false;
            graphicsStatus.Text = "Lumenveil download failed.\n" + ex.Message;
        }
        finally
        {
            reporting = false;
            graphicsBusy = false;
            UpdatePackageButtons();
        }
    }

    private Task InstallLumenveilAsync() => ChangeGraphicsAsync(remove: false);
    private Task RemoveLumenveilAsync() => ChangeGraphicsAsync(remove: true);

    private async Task ChangeGraphicsAsync(bool remove)
    {
        if (graphicsBusy || patchBusy || hdMutating || (!remove && (!graphicsCompatible || graphicsZip is null || graphicsPackage is null)))
            return;
        string client = wowPath;
        graphicsBusy = graphicsMutating = true;
        browseButton.Enabled = patchButton.Enabled = launchButton.Enabled = false;
        UpdatePackageButtons();
        bool reporting = true;
        try
        {
            if (remove)
            {
                string[] preserved = await Task.Run(() => LumenveilInstaller.RemoveAsync(client));
                graphicsStatus.Text = preserved.Length == 0 ? "Lumenveil removed."
                    : "Lumenveil removed. These files were kept:\n" + string.Join(", ", preserved);
            }
            else
            {
                var progress = new Progress<DownloadStatus>(s =>
                {
                    if (reporting && !IsDisposed) RenderPackageProgress(graphicsStatus, graphicsBar, s);
                });
                await Task.Run(() => LumenveilInstaller.InstallAsync(client, graphicsPackage!, graphicsZip!, progress));
                graphicsStatus.Text = LumenveilInstaller.InstalledWithoutVulkanFiles(client, graphicsPackage)
                    ? $"Lumenveil {graphicsPackage!.Version} installed without DXVK and ReShade, because no Vulkan driver was found. " +
                      "Lumenveil runs on the game's own Direct3D 9. After a graphics driver update with Vulkan, choose Install again to add them."
                    : $"Lumenveil {graphicsPackage!.Version} installed.";
            }
        }
        catch (Exception ex)
        {
            graphicsStatus.Text = "Lumenveil change failed.\n" + ex.Message;
        }
        finally
        {
            reporting = false;
            graphicsBar.Visible = false;
            graphicsBusy = graphicsMutating = false;
            browseButton.Enabled = true;
            patchButton.Enabled = launchButton.Enabled = IsValidWowPath();
            UpdatePackageButtons();
        }
    }

    private void RenderPackageProgress(Label label, ProgressBar bar, DownloadStatus status)
    {
        RenderProgress(label, bar, status);
#if STAGING_FEEDS
        if (StagingSmoke is not null)
            LogStagingProgress(label.Text.ReplaceLineEndings(" | "));
#endif
    }

#if STAGING_FEEDS
    internal string? StagingSmoke { get; init; }

    private static void LogStagingProgress(string text) =>
        File.AppendAllLines(Path.Combine(AppContext.BaseDirectory, "staging-progress.log"), [text]);

    private async Task RunStagingSmokeAsync(string mode)
    {
        if (mode == "launch")
        {
            var environment = await LumenveilInstaller.LaunchEnvironmentAsync(wowPath);
            using Process? wow = await StartWowAsync();
            LogStagingProgress($"Launched Wow.exe pid {wow?.Id} with ReShade layer {(environment.Count > 0 ? "enabled" : "absent")}");
        }
        else if (mode == "hd")
        {
            // check -> download -> install -> disable -> enable, logging the visible status after each step
            await CheckHdPackAsync(); LogStagingProgress("hd check | " + hdStatus.Text.ReplaceLineEndings(" | "));
            await DownloadHdPackAsync(); LogStagingProgress("hd download | " + hdStatus.Text.ReplaceLineEndings(" | "));
            await ChangeHdPackAsync("install"); LogStagingProgress("hd install | " + hdStatus.Text.ReplaceLineEndings(" | "));
            await ChangeHdPackAsync("toggle"); LogStagingProgress("hd disable | " + hdStatus.Text.ReplaceLineEndings(" | "));
            await ChangeHdPackAsync("toggle"); LogStagingProgress("hd enable | " + hdStatus.Text.ReplaceLineEndings(" | "));
        }
        else if (mode == "update")
        {
            await CheckLauncherAsync();
            await UpdateLauncherAsync();
            if (!launcherReady) LogStagingProgress(launcherStatus.Text.ReplaceLineEndings(" | "));
            else await UpdateLauncherAsync();
        }
        else
        {
            await CheckLumenveilAsync();
            LogStagingProgress(graphicsStatus.Text.ReplaceLineEndings(" | "));
            await DownloadLumenveilAsync();
            LogStagingProgress(graphicsStatus.Text.ReplaceLineEndings(" | "));
            if (mode == "graphics-install" && installGraphicsButton.Enabled)
            {
                await InstallLumenveilAsync();
                LogStagingProgress(graphicsStatus.Text.ReplaceLineEndings(" | "));
            }
            else if (mode == "graphics-remove" && removeGraphicsButton.Enabled)
            {
                await RemoveLumenveilAsync();
                LogStagingProgress(graphicsStatus.Text.ReplaceLineEndings(" | "));
            }
        }
        if (!IsDisposed) Close();
    }
#endif
}
