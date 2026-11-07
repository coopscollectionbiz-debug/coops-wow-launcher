using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopsWoWLauncher;

internal static class LumenveilInstaller
{
    private const string StateName = ".coops-lumenveil";
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private sealed record InstalledFile(string Path, string Sha256);
    private sealed record Receipt(string Transaction, string Version, string PackageSha256, InstalledFile[] Files);
    private sealed record Transaction(string Id, bool Removing, InstalledFile[] Files);
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "d3d9.dll", "dxgi.dll", "dinput8.dll", "d3d9.ini", "dxvk.conf", "ReShade.ini",
        "ReShadePreset.ini", "ReShade-Lumenveil.ini", "CoAVolFog.dll", "CoAVolFog.ini",
        "CoAVolFog-regions.ini", "CoAVolFog-spots.txt"
    };

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

    internal static async Task RecoverIfNeededAsync(string client)
    {
        if (!HasState(client)) return;
        string state = SafePath(client, StateName);
        if (!File.Exists(SafePath(state, "pending.json"))) return;
        EnsureGameClosed();
        using var gate = Lock(state);
        await RecoverAsync(client, state);
    }

    internal static async Task InstallAsync(string client, PackageMetadata package, string zip,
        IProgress<DownloadStatus> progress)
    {
        if (!await IsCompatibleAsync(client, package))
            throw new InvalidDataException("This Wow.exe is outside the supported Lumenveil builds.");
        EnsureGameClosed();

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
        var missing = new List<(string Path, ZipArchiveEntry Entry)>();
        var conflicts = new List<string>();
        foreach (var (path, entry) in entries)
        {
            string dest = SafePath(client, path);
            if (Directory.Exists(dest) || File.Exists(dest) &&
                (!owned.TryGetValue(path, out var original) || !await MatchesAsync(dest, original)))
                conflicts.Add(path);
            else if (!File.Exists(dest))
                missing.Add((path, entry));
        }
        // A different wrapper can conflict even when its filename is outside the archive.
        foreach (string wrapper in new[] { "d3d9.dll", "dxgi.dll", "dinput8.dll", "opengl32.dll",
            "ReShade32.dll", "ReShade.ini", "dxvk.conf", "version.dll", "CoAVolFog.dll", "CoAVolFog.ini" })
        {
            if (!entries.ContainsKey(wrapper) && !owned.ContainsKey(wrapper) && File.Exists(SafePath(client, wrapper)))
                conflicts.Add(wrapper);
        }
        if (conflicts.Count > 0)
            throw new IOException("Resolve these existing graphics files before installing Lumenveil:\n" + string.Join("\n", conflicts));

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
            EnsureGameClosed();
            transaction = transaction with { Files = staged.ToArray() };
            Write(state, "pending.json", transaction);
            progress.Report(new(item, DownloadPhase.Installing));
            foreach (var file in staged)
            {
                string dest = SafePath(client, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(SafePath(staging, file.Path), dest);
                owned[file.Path] = file;
            }

            Write(state, "installed.json", new Receipt(transaction.Id, package.Version, package.Sha256, owned.Values.ToArray()));
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
        EnsureGameClosed();
        string state = SafePath(client, StateName);
        if (!Directory.Exists(state))
            return [];
        using var gate = Lock(state);
        await RecoverAsync(client, state);
        Receipt? receipt = Read<Receipt>(state, "installed.json");
        if (receipt is null)
            return [];

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
        EnsureGameClosed();

        var transaction = new Transaction(Guid.NewGuid().ToString("N"), true, removable.ToArray());
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

                string backup = SafePath(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(dest, backup);
            }
            Write(state, "installed.json", new Receipt(transaction.Id, "", "", []));
            Finish(state, transaction);
            return preserved.ToArray();
        }
        catch
        {
            await RecoverAsync(client, state);
            throw;
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

    private static void EnsureGameClosed()
    {
        Process[] processes = Process.GetProcessesByName("Wow");
        foreach (Process process in processes) process.Dispose();
        if (processes.Length > 0)
            throw new IOException("Close World of Warcraft before changing graphics files.");
    }
}
