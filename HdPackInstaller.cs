using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CoopsWoWLauncher;

/// <summary>
/// Optional HD Environment Pack: one MPQ (Data\patch-y.mpq) that loads directly below the mandatory patch-z.mpq.
/// The mandatory patch is never read, replaced or re-downloaded here. Disabling moves the pack out of Data\ into
/// the client's .coops-hdpack folder (same volume, no copy, no download); enabling moves it back. Every change is
/// journalled (pending.json) and finished or undone by RecoverIfNeededAsync after a crash or power loss.
/// The player's choice (enabled / disabled) is the receipt's Enabled flag and survives launcher restarts.
/// </summary>
internal static class HdPackInstaller
{
    internal const string StateName = ".coops-hdpack";
    internal const string PackPath = "Data/patch-y.mpq";
    private const string StoredName = "patch-y.mpq";
    private const long SpaceMargin = 64L * 1024 * 1024;

    internal sealed record Receipt(string Version, string Sha256, long Size, bool Enabled, DateTime InstalledUtc, DateTime PackWriteUtc);
    // Op: install | enable | disable | remove. Previous: where the earlier pack was parked during an install.
    private sealed record Pending(string Op, string Version, string Sha256, long Size, bool HadPrevious, bool PreviousEnabled);

    internal static string ActivePath(string client) => Path.Combine(client, "Data", StoredName);
    private static string State(string client) => Path.Combine(client, StateName);
    private static string StoredPath(string client) => Path.Combine(State(client), StoredName);
    private static string StagingPath(string client) => Path.Combine(client, "Data", StoredName + ".coops-staging");
    private static string PreviousPath(string client) => Path.Combine(State(client), "previous.mpq");

    internal static Receipt? Installed(string client) => Read<Receipt>(client, "installed.json");

    /// <summary>Required core: the pack is only offered when Data\patch-z.mpq is one of these exact builds.</summary>
    internal static async Task<bool> CoreMatchesAsync(string client, IEnumerable<string> requiredCoreSha256)
    {
        string z = Path.Combine(client, "Data", "patch-z.mpq");
        if (!File.Exists(z)) return false;
        var allowed = new HashSet<string>(requiredCoreSha256, StringComparer.OrdinalIgnoreCase);
        return allowed.Count > 0 && allowed.Contains(await ReleaseUpdater.Sha256Async(z));
    }

    /// <summary>Cheap check used while scanning archives: the file at Data\patch-y.mpq is the pack this launcher installed.</summary>
    internal static bool LooksManaged(string client, string fullPath)
    {
        Receipt? r = Installed(client);
        if (r is null || !r.Enabled || !SamePath(fullPath, ActivePath(client))) return false;
        var info = new FileInfo(fullPath);
        return info.Exists && info.Length == r.Size && info.LastWriteTimeUtc == r.PackWriteUtc;
    }

    /// <summary>Full check (SHA-256) before an obsolete-file rule may skip Data\patch-y.mpq.</summary>
    internal static async Task<bool> IsManagedAsync(string client, string fullPath, CancellationToken ct = default)
    {
        Receipt? r = Installed(client);
        if (r is null || !r.Enabled || !SamePath(fullPath, ActivePath(client)) || !File.Exists(fullPath)) return false;
        return new FileInfo(fullPath).Length == r.Size &&
               string.Equals(await ReleaseUpdater.Sha256Async(fullPath, ct), r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal static void EnsureSpace(string folder, long bytes)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        if (root is null) return;
        long free = new DriveInfo(root).AvailableFreeSpace;
        if (free < bytes + SpaceMargin)
            throw new IOException($"Not enough disk space on {root}: {DownloadStatus.FormatBytes(bytes + SpaceMargin)} needed, " +
                                  $"{DownloadStatus.FormatBytes(free)} free. Nothing was changed.");
    }

    /// <summary>Install (or update) the verified pack file. The previous pack is kept until the new one is in place.</summary>
    internal static async Task InstallAsync(string client, string version, string sha256, long size, string downloaded,
        IProgress<DownloadStatus>? progress = null, CancellationToken ct = default)
    {
        EnsureGameClosed(client);
        await RecoverIfNeededAsync(client);
        var item = new DownloadItem(DownloadKind.HdEnvironment, StoredName);
        progress?.Report(new(item, DownloadPhase.Verifying));
        if (new FileInfo(downloaded).Length != size ||
            !string.Equals(await ReleaseUpdater.Sha256Async(downloaded, ct), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded HD Environment Pack failed verification. Download it again.");

        Receipt? current = Installed(client);
        string active = ActivePath(client);
        if (current is null && File.Exists(active))
            throw new IOException("Data\\patch-y.mpq already exists and was not installed by this launcher. " +
                                  "Close WoW, move that file somewhere safe, then install the HD Environment Pack again.");
        if (current is not null && current.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (!current.Enabled) await SetEnabledAsync(client, true);
            return;
        }
        EnsureSpace(Path.Combine(client, "Data"), size);
        Directory.CreateDirectory(State(client));
        bool hadPrevious = current is not null;
        Write(client, "pending.json", new Pending("install", version, sha256, size, hadPrevious, current?.Enabled ?? false));
        try
        {
            progress?.Report(new(item, DownloadPhase.Installing));
            string staging = StagingPath(client);
            File.Copy(downloaded, staging, overwrite: true);
            if (!string.Equals(await ReleaseUpdater.Sha256Async(staging, ct), sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The staged HD Environment Pack copy failed verification.");
            EnsureGameClosed(client);
            if (hadPrevious)
            {
                string from = current!.Enabled ? active : StoredPath(client);
                if (File.Exists(from)) File.Move(from, PreviousPath(client), overwrite: true);
            }
            File.Move(staging, active);
            var written = new FileInfo(active).LastWriteTimeUtc;
            Write(client, "installed.json", new Receipt(version, sha256.ToLowerInvariant(), size, true, DateTime.UtcNow, written));
            if (File.Exists(PreviousPath(client))) File.Delete(PreviousPath(client));
            Delete(client, "pending.json");
        }
        catch
        {
            await RecoverIfNeededAsync(client);
            throw;
        }
    }

    /// <summary>Turn the installed pack on or off without downloading anything or touching patch-z.mpq.</summary>
    internal static async Task SetEnabledAsync(string client, bool enabled)
    {
        EnsureGameClosed(client);
        await RecoverIfNeededAsync(client);
        Receipt r = Installed(client) ?? throw new IOException("The HD Environment Pack is not installed.");
        if (r.Enabled == enabled) return;
        string from = enabled ? StoredPath(client) : ActivePath(client);
        string to = enabled ? ActivePath(client) : StoredPath(client);
        if (!File.Exists(from))
            throw new IOException("The HD Environment Pack file is missing. Remove it and install it again.");
        if (enabled && File.Exists(to))
            throw new IOException("Data\\patch-y.mpq exists and is not the HD Environment Pack. Move it out of the Data folder first.");
        if (new FileInfo(from).Length != r.Size)
            throw new IOException("The HD Environment Pack file was changed. Remove it and install it again.");
        Directory.CreateDirectory(State(client));
        Write(client, "pending.json", new Pending(enabled ? "enable" : "disable", r.Version, r.Sha256, r.Size, false, r.Enabled));
        File.Move(from, to);
        Write(client, "installed.json", r with { Enabled = enabled, PackWriteUtc = new FileInfo(to).LastWriteTimeUtc });
        Delete(client, "pending.json");
    }

    /// <summary>Remove the pack completely (from Data\ or from storage). Never touches any other archive.</summary>
    internal static async Task RemoveAsync(string client)
    {
        EnsureGameClosed(client);
        await RecoverIfNeededAsync(client);
        Receipt? r = Installed(client);
        if (r is null) return;
        Write(client, "pending.json", new Pending("remove", r.Version, r.Sha256, r.Size, false, r.Enabled));
        foreach (string? p in new[] { StoredPath(client), r.Enabled ? ActivePath(client) : null })
        {
            if (p is null || !File.Exists(p)) continue;
            if (new FileInfo(p).Length != r.Size)
                throw new IOException(p + " does not match the installed pack; it was kept.");
            File.Delete(p);
        }
        Delete(client, "installed.json");
        Delete(client, "pending.json");
        if (Directory.Exists(State(client)) && !Directory.EnumerateFileSystemEntries(State(client)).Any())
            Directory.Delete(State(client));
    }

    /// <summary>Finish or undo an interrupted change. Safe to call at every start.</summary>
    internal static Task RecoverIfNeededAsync(string client)
    {
        Pending? p = Read<Pending>(client, "pending.json");
        if (p is null) return Task.CompletedTask;
        Receipt? r = Installed(client);
        string active = ActivePath(client), stored = StoredPath(client), previous = PreviousPath(client), staging = StagingPath(client);
        switch (p.Op)
        {
            case "install":
                bool done = r is not null && r.Sha256.Equals(p.Sha256, StringComparison.OrdinalIgnoreCase) && File.Exists(active);
                if (!done)
                {
                    // Undo: the new pack never became current. Put the previous pack back where it was.
                    if (File.Exists(active) && (r is null || !r.Sha256.Equals(p.Sha256, StringComparison.OrdinalIgnoreCase)) &&
                        new FileInfo(active).Length == p.Size && File.Exists(previous))
                        File.Delete(active);
                    else if (File.Exists(active) && r is null && !p.HadPrevious && new FileInfo(active).Length == p.Size)
                        File.Delete(active);
                    if (File.Exists(previous))
                        File.Move(previous, p.PreviousEnabled ? active : stored, overwrite: false);
                }
                else if (File.Exists(previous)) File.Delete(previous);
                if (File.Exists(staging)) File.Delete(staging);
                break;
            case "enable":
            case "disable":
                bool enable = p.Op == "enable";
                string from = enable ? stored : active, to = enable ? active : stored;
                if (r is not null)
                {
                    if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);   // finish the move
                    if (File.Exists(to))
                        Write(client, "installed.json", r with { Enabled = enable, PackWriteUtc = new FileInfo(to).LastWriteTimeUtc });
                }
                break;
            case "remove":
                foreach (string f in new[] { stored, active })
                    if (File.Exists(f) && new FileInfo(f).Length == p.Size && (f == stored || p.PreviousEnabled)) File.Delete(f);
                Delete(client, "installed.json");
                break;
        }
        Delete(client, "pending.json");
        return Task.CompletedTask;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static T? Read<T>(string client, string file)
    {
        string path = Path.Combine(State(client), file);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : default; }
        catch (JsonException) { return default; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static void Write<T>(string client, string file, T value)
    {
        Directory.CreateDirectory(State(client));
        string path = Path.Combine(State(client), file);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }

    private static void Delete(string client, string file)
    {
        string path = Path.Combine(State(client), file);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>WoW only reads its archives at start-up, so a change while it runs would not apply and could not move the file.</summary>
    internal static void EnsureGameClosed(string client)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(client)) + Path.DirectorySeparatorChar;
        foreach (Process process in Process.GetProcessesByName("Wow"))
        {
            using (process)
            {
                string? path = null;
                try { path = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                if (path is null || path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Close World of Warcraft in this folder first. HD Environment Pack changes apply the next time WoW starts.");
            }
        }
    }
}
