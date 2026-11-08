using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopsWoWLauncher;

internal static class LumenveilInstaller
{
    private const string StateName = ".coops-lumenveil";
    private const string BackupName = "backup";
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private sealed record InstalledFile(string Path, string Sha256);
    // Replaced: player files moved to .coops-lumenveil/backup before a package file took their place.
    // Directories: folders the install created, removed again when they are empty.
    private sealed record Receipt(string Transaction, string Version, string PackageSha256, InstalledFile[] Files,
        InstalledFile[]? Replaced = null, string[]? Directories = null);
    private sealed record Transaction(string Id, bool Removing, InstalledFile[] Files,
        InstalledFile[]? Replaced = null, string[]? Directories = null);
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "d3d9.dll", "dxgi.dll", "dinput8.dll", "d3d9.ini", "dxvk.conf", "ReShade.ini",
        "ReShadePreset.ini", "ReShade-Lumenveil.ini", "CoAVolFog.dll", "CoAVolFog.ini",
        "CoAVolFog-regions.ini", "CoAVolFog-spots.txt", "lampdata.bin", "liquiddata.bin", "gameobjectlights.bin",
        LayerDll, LayerManifest
    };

    // ReShade ships as a client-local explicit Vulkan layer. Nothing is registered system-wide: the launcher
    // enables it only for the Wow.exe process it starts, and turns off a system-wide ReShade layer for that
    // process so ReShade is never injected twice.
    private const string LayerDirectory = "ReShade";
    private const string LayerDll = LayerDirectory + "/ReShade32.dll";
    private const string LayerManifest = LayerDirectory + "/ReShade32.json";
    internal const string LayerName = "VK_LAYER_coops_reshade";

    /// <summary>
    /// Environment for starting Wow.exe with the installed client-local ReShade layer, or an empty map when the
    /// package (or its layer) is not installed or the layer files no longer match what was installed.
    /// </summary>
    internal static async Task<Dictionary<string, string>> LaunchEnvironmentAsync(string client)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!HasState(client)) return environment;
            string state = SafePath(client, StateName);
            if (File.Exists(SafePath(state, "pending.json"))) return environment;
            Receipt? receipt = Read<Receipt>(state, "installed.json");
            if (receipt is null) return environment;
            foreach (string path in new[] { LayerDll, LayerManifest })
            {
                InstalledFile? file = receipt.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                string full = SafePath(client, path);
                if (file is null || !File.Exists(full) || !await MatchesAsync(full, file))
                    return environment;
            }
            string layers = SafePath(client, LayerDirectory);
            string? existing = Environment.GetEnvironmentVariable("VK_INSTANCE_LAYERS");
            environment["VK_LAYER_PATH"] = layers;
            environment["VK_ADD_LAYER_PATH"] = layers;
            environment["VK_INSTANCE_LAYERS"] = string.IsNullOrEmpty(existing) ? LayerName : LayerName + ";" + existing;
            environment["DISABLE_VK_LAYER_reshade_1"] = "1";
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            environment.Clear();
        }
        return environment;
    }

    /// <summary>DXVK, and with it Lumenveil and ReShade, needs the 32-bit Vulkan runtime from the graphics driver.</summary>
    private static void EnsureVulkanRuntime()
    {
        string wow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        if (!string.IsNullOrEmpty(wow64) && !File.Exists(Path.Combine(wow64, "vulkan-1.dll")))
            throw new IOException("Lumenveil needs a graphics driver with Vulkan support, and the 32-bit Vulkan runtime " +
                "(vulkan-1.dll) was not found. Update your graphics driver, then install Lumenveil again.");
    }

    internal static bool CanInstall(PackageMetadata package) => package.InstallationEnabled &&
        package.CompatibleWowSha256 is { Length: > 0 } &&
        package.CompatibleWowSha256.All(SignedPackageFeed.IsSha256) &&
        package.SupportedBuilds is { Length: > 0 } &&
        package.Files is { Length: > 0 } && package.Prerequisites is not null;

    internal static async Task<bool> IsCompatibleAsync(string client, PackageMetadata package)
    {
        if (!CanInstall(package) || !Directory.Exists(client))
            return false;
        string wow = SafePath(client, "Wow.exe");
        return File.Exists(wow) && package.CompatibleWowSha256.Contains(
            await ReleaseUpdater.Sha256Async(wow), StringComparer.OrdinalIgnoreCase);
    }

    internal static bool HasState(string client) =>
        Directory.Exists(client) && Directory.Exists(Path.Combine(client, StateName));

    /// <summary>The installed package version, or null when no Lumenveil files are managed in this client.</summary>
    internal static string? InstalledVersion(string client)
    {
        try
        {
            if (!HasState(client)) return null;
            Receipt? receipt = Read<Receipt>(SafePath(client, StateName), "installed.json");
            return receipt?.Files.Length > 0 ? receipt.Version : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static async Task RecoverIfNeededAsync(string client)
    {
        if (!HasState(client)) return;
        string state = SafePath(client, StateName);
        if (!File.Exists(SafePath(state, "pending.json"))) return;
        EnsureGameClosed(client);
        using var gate = Lock(state);
        await RecoverAsync(client, state);
    }

    internal static async Task InstallAsync(string client, PackageMetadata package, string zip,
        IProgress<DownloadStatus> progress)
    {
        if (!await IsCompatibleAsync(client, package))
            throw new InvalidDataException("This Wow.exe is outside the supported Lumenveil builds.");
        EnsureGameClosed(client);
        if (package.Files.Contains(LayerDll, StringComparer.OrdinalIgnoreCase))
            EnsureVulkanRuntime();

        string state = SafePath(client, StateName);
        Directory.CreateDirectory(state);
        using var gate = Lock(state);
        await RecoverAsync(client, state);
        Receipt? receipt = Read<Receipt>(state, "installed.json");
        if (receipt?.Files.Length > 0 && (receipt.Version != package.Version ||
            !receipt.PackageSha256.Equals(package.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Remove the currently installed Lumenveil package before installing another version.");

        var item = new DownloadItem(DownloadKind.Lumenveil, package.Filename);
        progress.Report(new(item, DownloadPhase.Verifying));
        if (new FileInfo(zip).Length != package.Size ||
            !string.Equals(await ReleaseUpdater.Sha256Async(zip), package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cached Lumenveil package failed verification. Download it again.");

        using var archive = ZipFile.OpenRead(zip);
        var entries = Inspect(archive, package.Files);
        var owned = (receipt?.Files ?? []).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var replacedBefore = (receipt?.Replaced ?? []).ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var missing = new List<(string Path, ZipArchiveEntry Entry)>();
        var replace = new List<InstalledFile>();
        var conflicts = new List<string>();
        foreach (var (path, entry) in entries)
        {
            string dest = SafePath(client, path);
            if (Directory.Exists(dest))
                conflicts.Add(path);
            else if (owned.TryGetValue(path, out var installed))
            {
                // Already installed by this package: keep it, unless the player edited it.
                if (!File.Exists(dest))
                    missing.Add((path, entry));
                else if (!await MatchesAsync(dest, installed))
                    conflicts.Add(path + " (edited after installation)");
            }
            else if (File.Exists(dest))
            {
                if (replacedBefore.ContainsKey(path) || File.Exists(SafePath(state, BackupName + "/" + path)))
                    conflicts.Add(path + " (an earlier backup is still kept in " + StateName + "\\" + BackupName + ")");
                else
                {
                    replace.Add(new(path, await ReleaseUpdater.Sha256Async(dest)));
                    missing.Add((path, entry));
                }
            }
            else
                missing.Add((path, entry));
        }
        // A different wrapper can conflict even when its filename is outside the archive.
        foreach (string wrapper in new[] { "d3d9.dll", "dxgi.dll", "opengl32.dll", "ReShade32.dll",
            "ReShade.ini", "dxvk.conf", "version.dll", "CoAVolFog.dll", "CoAVolFog.ini" })
        {
            if (!entries.ContainsKey(wrapper) && !owned.ContainsKey(wrapper) && File.Exists(SafePath(client, wrapper)))
                conflicts.Add(wrapper);
        }
        if (conflicts.Count > 0)
            throw new IOException("Resolve these existing graphics files before installing Lumenveil:\n" + string.Join("\n", conflicts));

        // Folders this install creates, deepest first, so removal can take them away again.
        var created = new List<string>();
        foreach (var (path, _) in missing)
        {
            for (string? dir = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
                 !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                string relative = dir.Replace(Path.DirectorySeparatorChar, '/');
                if (!Directory.Exists(SafePath(client, relative)) &&
                    !created.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    created.Add(relative);
            }
        }
        string[] directories = created.OrderByDescending(d => d.Count(c => c == '/')).ToArray();

        var transaction = new Transaction(Guid.NewGuid().ToString("N"), false, []);
        string staging = SafePath(state, transaction.Id);
        var staged = new List<InstalledFile>();
        try
        {
            Write(state, "pending.json", transaction);
            progress.Report(new(item, DownloadPhase.Extracting));
            foreach (var (path, entry) in missing)
            {
                string target = SafePath(staging, path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
                staged.Add(new(path, await ReleaseUpdater.Sha256Async(target)));
            }
            EnsureGameClosed(client);
            transaction = transaction with { Files = staged.ToArray(), Replaced = replace.ToArray(), Directories = directories };
            Write(state, "pending.json", transaction);
            progress.Report(new(item, DownloadPhase.Installing));
            foreach (var original in replace)
            {
                string dest = SafePath(client, original.Path);
                if (!await MatchesAsync(dest, original))
                    throw new IOException("A graphics file changed during installation: " + original.Path);
                string backup = SafePath(state, BackupName + "/" + original.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(dest, backup);
                replacedBefore[original.Path] = original;
            }
            foreach (var file in staged)
            {
                string dest = SafePath(client, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(SafePath(staging, file.Path), dest);
                owned[file.Path] = file;
            }

            string[] allDirectories = (receipt?.Directories ?? []).Concat(directories)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Count(c => c == '/')).ToArray();
            Write(state, "installed.json", new Receipt(transaction.Id, package.Version, package.Sha256,
                owned.Values.ToArray(), replacedBefore.Values.ToArray(), allDirectories));
            Finish(state, transaction);
        }
        catch
        {
            if (File.Exists(SafePath(state, "pending.json")))
                await RecoverAsync(client, state);
            else if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    internal static async Task<string[]> RemoveAsync(string client)
    {
        EnsureGameClosed(client);
        string state = SafePath(client, StateName);
        if (!Directory.Exists(state))
            return [];
        string[] preserved;
        bool clean;
        using (var gate = Lock(state))
        {
            (preserved, clean) = await RemoveLockedAsync(client, state);
        }
        // Leave no launcher state behind once every file and backup is back in place.
        if (clean)
        {
            try { Directory.Delete(state, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return preserved;
    }

    private static async Task<(string[] Preserved, bool Clean)> RemoveLockedAsync(string client, string state)
    {
        await RecoverAsync(client, state);
        Receipt? receipt = Read<Receipt>(state, "installed.json");
        if (receipt is null)
            return ([], !Directory.Exists(SafePath(state, BackupName)));

        var removable = new List<InstalledFile>();
        var preserved = new List<string>();
        foreach (var file in receipt.Files)
        {
            ValidateGraphicsPath(file.Path);
            string dest = SafePath(client, file.Path);
            if (Directory.Exists(dest))
                preserved.Add(file.Path);
            else if (File.Exists(dest))
            {
                if (await MatchesAsync(dest, file))
                    removable.Add(file);
                else
                    preserved.Add(file.Path);
            }
        }
        // Restore a backup only where the package file is about to be removed or is already gone.
        var restore = new List<InstalledFile>();
        var kept = new List<InstalledFile>();
        foreach (var original in receipt.Replaced ?? [])
        {
            ValidateGraphicsPath(original.Path);
            string backup = SafePath(state, BackupName + "/" + original.Path);
            if (!File.Exists(backup))
                continue;
            bool freed = removable.Any(f => f.Path.Equals(original.Path, StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(SafePath(client, original.Path)) && !Directory.Exists(SafePath(client, original.Path));
            if (freed && await MatchesAsync(backup, original))
                restore.Add(original);
            else
            {
                kept.Add(original);
                preserved.Add(original.Path + " (your original is kept in " + StateName + "\\" + BackupName + ")");
            }
        }
        EnsureGameClosed(client);

        var transaction = new Transaction(Guid.NewGuid().ToString("N"), true, removable.ToArray(), restore.ToArray());
        string staging = SafePath(state, transaction.Id);
        Write(state, "pending.json", transaction);
        try
        {
            foreach (var file in removable)
            {
                string dest = SafePath(client, file.Path);
                if (!await MatchesAsync(dest, file))
                {
                    preserved.Add(file.Path);
                    continue;
                }

                string moved = SafePath(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
                File.Move(dest, moved);
            }
            foreach (var original in restore)
            {
                string dest = SafePath(client, original.Path);
                if (File.Exists(dest) || Directory.Exists(dest))
                    throw new IOException("Cannot restore a graphics backup over an existing file: " + dest);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(SafePath(state, BackupName + "/" + original.Path), dest);
            }
            Write(state, "installed.json", new Receipt(transaction.Id, "", "", [], kept.ToArray(), []));
            Finish(state, transaction);
        }
        catch
        {
            await RecoverAsync(client, state);
            throw;
        }
        RemoveEmptyDirectories(client, receipt.Directories);
        RemoveEmptyDirectories(state, [BackupName], recursive: true);
        return (preserved.ToArray(), kept.Count == 0 && !Directory.Exists(SafePath(state, BackupName)));
    }

    private static void RemoveEmptyDirectories(string root, string[]? relative, bool recursive = false)
    {
        foreach (string dir in relative ?? [])
        {
            string full = SafePath(root, dir);
            if (!Directory.Exists(full)) continue;
            if (recursive)
                foreach (string child in Directory.GetDirectories(full, "*", SearchOption.AllDirectories)
                    .OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(child).Any()) Directory.Delete(child);
            if (!Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
        }
    }

    private static Dictionary<string, ZipArchiveEntry> Inspect(ZipArchive archive, string[] declared)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in declared)
        {
            ValidateGraphicsPath(path);
            if (!allowed.Add(path))
                throw new InvalidDataException("The graphics file list contains duplicate paths.");
        }
        if (allowed.Any(path => allowed.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("A declared graphics path is also a parent directory.");

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            bool directory = entry.FullName.EndsWith('/');
            string path = directory ? entry.FullName[..^1] : entry.FullName;
            ValidateRelative(path);
            int attributes = entry.ExternalAttributes;
            int unixType = (attributes >> 16) & 0xf000;
            if (!seen.Add(path) || (attributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)))
                throw new InvalidDataException("The graphics ZIP contains a link or duplicate path: " + path);

            if (directory && allowed.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (directory || !allowed.TryGetValue(path, out string? expected))
                throw new InvalidDataException("The graphics ZIP contains an undeclared path: " + path);
            if (entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("The expanded graphics package exceeds the size limit.");
            expanded += entry.Length;
            entries.Add(expected, entry);
        }
        if (entries.Count != allowed.Count)
            throw new InvalidDataException("The graphics ZIP is missing declared files.");
        return entries;
    }

    private static void ValidateGraphicsPath(string path)
    {
        ValidateRelative(path);
        if (RootFiles.Contains(path))
            return;
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if ((path.StartsWith("Lumenveil/", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("reshade-shaders/", StringComparison.OrdinalIgnoreCase)) &&
            extension is ".ini" or ".conf" or ".fx" or ".fxh" or ".png" or ".dds" or ".jpg" or ".txt" or ".md")
            return;
        throw new InvalidDataException("The package may not write to " + path);
    }

    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240)
            throw new InvalidDataException("The package contains an invalid path.");
        foreach (string segment in path.Split('/'))
        {
            if (!Regex.IsMatch(segment, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]*\z") ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                Regex.IsMatch(segment.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase))
                throw new InvalidDataException("The package contains an unsafe path: " + path);
        }
    }

    private static string SafePath(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A graphics path escapes the selected folder.");

        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Lumenveil cannot write through a link: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    private static async Task RecoverAsync(string client, string state)
    {
        Transaction? pending = Read<Transaction>(state, "pending.json");
        if (pending is null)
            return;
        if (!Guid.TryParseExact(pending.Id, "N", out _))
            throw new InvalidDataException("The graphics recovery record has an invalid transaction.");
        if (Read<Receipt>(state, "installed.json")?.Transaction != pending.Id)
        {
            string staging = SafePath(state, pending.Id);
            if (pending.Removing)
            {
                // Undo restored backups first so the removed package files can return to their places.
                foreach (InstalledFile original in pending.Replaced ?? [])
                {
                    ValidateGraphicsPath(original.Path);
                    string dest = SafePath(client, original.Path);
                    string backup = SafePath(state, BackupName + "/" + original.Path);
                    if (!File.Exists(backup) && File.Exists(dest) && await MatchesAsync(dest, original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Move(dest, backup);
                    }
                }
            }
            foreach (InstalledFile file in pending.Files)
            {
                ValidateGraphicsPath(file.Path);
                string dest = SafePath(client, file.Path);
                string staged = SafePath(staging, file.Path);
                if (pending.Removing && File.Exists(staged))
                {
                    if (File.Exists(dest) || Directory.Exists(dest))
                        throw new IOException("Cannot restore a graphics backup over an existing file: " + dest);
                    if (!await MatchesAsync(staged, file))
                        throw new IOException("Cannot restore a graphics backup that failed verification: " + staged);
                    File.Move(staged, dest);
                }
                else if (!pending.Removing && !File.Exists(staged) && File.Exists(dest) && await MatchesAsync(dest, file))
                {
                    File.Delete(dest);
                }
            }
            if (!pending.Removing)
            {
                // Put back the player's files that this interrupted install had moved aside.
                foreach (InstalledFile original in pending.Replaced ?? [])
                {
                    ValidateGraphicsPath(original.Path);
                    string dest = SafePath(client, original.Path);
                    string backup = SafePath(state, BackupName + "/" + original.Path);
                    if (!File.Exists(backup))
                        continue;
                    if (File.Exists(dest) || Directory.Exists(dest))
                        throw new IOException("Cannot restore a graphics backup over an existing file: " + dest);
                    File.Move(backup, dest);
                }
                RemoveEmptyDirectories(client, pending.Directories);
                RemoveEmptyDirectories(state, [BackupName], recursive: true);
            }
        }
        Finish(state, pending);
    }

    private static void Finish(string state, Transaction transaction)
    {
        string staging = SafePath(state, transaction.Id);
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        File.Delete(SafePath(state, "pending.json"));
    }

    private static async Task<bool> MatchesAsync(string path, InstalledFile file) =>
        string.Equals(await ReleaseUpdater.Sha256Async(path), file.Sha256, StringComparison.OrdinalIgnoreCase);

    private static FileStream Lock(string state) =>
        new(SafePath(state, "graphics.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static T? Read<T>(string state, string file)
    {
        string path = SafePath(state, file);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), SignedPackageFeed.Json) : default;
    }

    private static void Write<T>(string state, string file, T value)
    {
        string path = SafePath(state, file);
        string temp = SafePath(state, file + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, SignedPackageFeed.Json));
        File.Move(temp, path, true);
    }

    // Only a WoW running from this client folder blocks a change. A process whose path cannot be read still blocks.
    private static void EnsureGameClosed(string client)
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
                    throw new IOException("Close World of Warcraft in this folder before changing graphics files.");
            }
        }
    }
}
