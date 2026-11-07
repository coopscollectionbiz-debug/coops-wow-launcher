using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CoopsWoWLauncher;

internal static class LauncherHandoff
{
    private const string StateName = ".coops-launcher-update";
    private const string HelperArgument = "--launcher-handoff";
    private const string HealthArgument = "--launcher-health";
    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException("Launcher path is unavailable.");
    private static string StateFor(string executable) => Path.Combine(Path.GetDirectoryName(executable)!, StateName);
    private static string Journal(string state) => Path.Combine(state, "pending.json");
    private static string Previous(string state) => Path.Combine(state, "previous.exe");
    private static string Health(string state) => Path.Combine(state, "healthy.txt");
    private static string Payload(string state, string sha) =>
        Path.Combine(state, ReleaseUpdater.StateDirName, "staging", sha.ToLowerInvariant() + ".payload");

    private sealed record Pending(string Executable, string Sha256, long Size, string Version, string Token);

    internal static Version CurrentVersion
    {
        get
        {
            Version version = typeof(LauncherHandoff).Assembly.GetName().Version!;
            return new Version(version.Major, version.Minor, version.Build);
        }
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "An empty assembly location identifies the portable bundle.")]
    private static bool IsPortableExecutable() => string.IsNullOrEmpty(typeof(LauncherHandoff).Assembly.Location);

    internal static Mutex InstanceMutex()
    {
        string path = Path.GetFullPath(Executable).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        return new Mutex(false, @"Local\CoopsWoWLauncher-" + key);
    }

    internal static string PrepareDownload()
    {
        if (!IsPortableExecutable())
            throw new InvalidOperationException("Self-update requires the portable launcher EXE.");

        string state = StateFor(Executable);
        try
        {
            Directory.CreateDirectory(state);
            if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update staging folder is a link.");
            if ((File.GetAttributes(Executable) & FileAttributes.ReadOnly) != 0)
                throw new IOException("The launcher EXE is read-only.");
            string probePath = Path.Combine(Path.GetDirectoryName(Executable)!, ".coops-write-" + Guid.NewGuid().ToString("N"));
            using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("The launcher folder is not writable. Move the launcher and launcher_path.txt to a folder you own, then try again.", ex);
        }
        return state;
    }

    internal static void StartUpdate(PackageMetadata package)
    {
        string state = PrepareDownload();
        if (File.Exists(Journal(state)) || File.Exists(Previous(state)))
            throw new IOException("An earlier launcher update still needs recovery.");

        var pending = new Pending(Executable, package.Sha256, package.Size, package.Version, Guid.NewGuid().ToString("N"));
        File.Delete(Health(state));
        WriteJournal(state, pending);
        try
        {
            StartHelper(state);
        }
        catch
        {
            File.Delete(Journal(state));
            throw;
        }
    }

    internal static void VerifyVersion(string payload, string expectedVersion)
    {
        FileVersionInfo info = FileVersionInfo.GetVersionInfo(payload);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        if (version.ToString() != expectedVersion || info.FilePrivatePart != 0 || version <= CurrentVersion)
            throw new InvalidDataException("The launcher EXE version does not match the signed update or is not newer.");
    }

    // Ordinary startup recovers an interrupted handoff before opening the form.
    internal static bool RecoverOrContinue()
    {
        string state = StateFor(Executable);
        if (!File.Exists(Journal(state)))
            return true;

        using (FileStream? gate = TryGate(state))
        {
            if (gate is null)
                return false;
            Pending pending = ReadJournal(state);
            if (IsHealthy(state, pending) || !File.Exists(Previous(state)))
            {
                CleanTransaction(state);
                return true;
            }
        }
        StartHelper(state, recover: true);
        return false;
    }

    internal static Action? HealthCheckpoint(string[] args)
    {
        if (args is not [HealthArgument, var token])
            return null;
        string state = StateFor(Executable);
        Pending pending = ReadJournal(state);
        if (pending.Token != token || !SamePath(pending.Executable, Executable) ||
            CurrentVersion.ToString() != pending.Version)
            throw new InvalidDataException("The launcher startup checkpoint does not match the pending update.");
        if (SignedPackageFeed.Staging && SignedPackageFeed.BuildSetting("FailStartup") == "true")
            Environment.Exit(73);

        return () =>
        {
            string temporary = Health(state) + ".tmp";
            File.WriteAllText(temporary, token);
            File.Move(temporary, Health(state), true);
#if STAGING_FEEDS
            File.AppendAllLines(Path.Combine(state, "staging-health.log"), [CurrentVersion.ToString()]);
#endif
        };
    }

    internal static bool IsHelper(string[] args) => args.Length > 0 && args[0] == HelperArgument;

    internal static void RunHelper(string[] args)
    {
        if (args.Length != 4 || !int.TryParse(args[1], out int parentId) ||
            !long.TryParse(args[2], out long parentStarted))
            return;

        string state = Path.GetDirectoryName(Executable)!;
        if (!Path.GetFileName(state).Equals(StateName, StringComparison.OrdinalIgnoreCase))
            return;
        using FileStream? gate = TryGate(state);
        if (gate is null)
            return;

        Pending? pending = null;
        try
        {
            pending = ReadJournal(state);
            WaitForParent(parentId, parentStarted);
            if (args[3] == "recover")
            {
                if (IsHealthy(state, pending))
                    CleanTransaction(state);
                else
                    RestoreAndRestart(state, pending);
                return;
            }

            string payload = Payload(state, pending.Sha256);
            if (new FileInfo(payload).Length != pending.Size ||
                !ReleaseUpdater.Sha256Async(payload).GetAwaiter().GetResult().Equals(pending.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged launcher failed verification.");
            VerifyVersion(payload, pending.Version);

            File.Replace(payload, pending.Executable, Previous(state));
            using Process candidate = StartExecutable(pending.Executable, HealthArgument, pending.Token);
            var wait = Stopwatch.StartNew();
            while (!candidate.HasExited && wait.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (IsHealthy(state, pending))
                {
                    CleanTransaction(state);
                    return;
                }
                Thread.Sleep(100);
            }

            if (!candidate.HasExited)
            {
                candidate.Kill();
                if (!candidate.WaitForExit(5000))
                    throw new IOException("The new launcher did not exit.");
            }
            RestoreAndRestart(state, pending);
        }
        catch (Exception ex)
        {
            if (pending is null)
                return;
            try
            {
                if (File.Exists(Previous(state)))
                    RestoreAndRestart(state, pending);
                else
                {
                    CleanTransaction(state);
                    using Process old = StartExecutable(pending.Executable);
                }
                WriteFailure(state, ex.Message);
            }
            catch (Exception rollbackError)
            {
                WriteFailure(state, rollbackError.Message);
            }
        }
    }

    private static void RestoreAndRestart(string state, Pending pending)
    {
        File.Replace(Previous(state), pending.Executable, null);
        CleanTransaction(state);
        WriteFailure(state, "The updated launcher did not reach its startup check. The previous version was restored.");
        using Process restored = StartExecutable(pending.Executable);
    }

    internal static string? TakeFailure()
    {
        string file = Path.Combine(StateFor(Executable), "last-error.txt");
        try
        {
            if (!File.Exists(file)) return null;
            string error = File.ReadAllText(file);
            File.Delete(file);
            return error;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void WriteFailure(string state, string message)
    {
        try
        {
            File.WriteAllText(Path.Combine(state, "last-error.txt"), message);
#if STAGING_FEEDS
            File.AppendAllLines(Path.Combine(state, "staging-recovery.log"), [message]);
#endif
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void CleanTransaction(string state)
    {
        File.Delete(Previous(state));
        File.Delete(Journal(state));
        File.Delete(Health(state));
    }

    private static bool IsHealthy(string state, Pending pending) =>
        File.Exists(Health(state)) && File.ReadAllText(Health(state)) == pending.Token;

    private static FileStream? TryGate(string state)
    {
        try { return new FileStream(Path.Combine(state, "handoff.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    private static void StartHelper(string state, bool recover = false)
    {
        // Previous helpers are unlocked by now. A currently running helper is left alone.
        foreach (string old in Directory.EnumerateFiles(state, "handoff-*.exe"))
        {
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        string helper = Path.Combine(state, "handoff-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Executable, helper);
        using Process parent = Process.GetCurrentProcess();
        using Process handoff = StartExecutable(helper, HelperArgument, parent.Id.ToString(),
            parent.StartTime.ToUniversalTime().Ticks.ToString(), recover ? "recover" : "activate");
    }

    private static Process StartExecutable(string executable, params string[] args)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new IOException("Could not start the launcher.");
    }

    private static void WaitForParent(int parentId, long parentStarted)
    {
        Process parent;
        try { parent = Process.GetProcessById(parentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks == parentStarted && !parent.WaitForExit(15000))
                throw new IOException("Close the running launcher before applying the update.");
        }
    }

    private static Pending ReadJournal(string state)
    {
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllText(Journal(state)), SignedPackageFeed.Json);
        if (pending is null || !SignedPackageFeed.IsSha256(pending.Sha256) || pending.Size <= 0 ||
            !Path.IsPathFullyQualified(pending.Executable) || !SamePath(StateFor(pending.Executable), state) ||
            !Path.GetExtension(pending.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending launcher update record is invalid.");
        return pending;
    }

    private static void WriteJournal(string state, Pending pending)
    {
        string temporary = Journal(state) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending, SignedPackageFeed.Json));
        File.Move(temporary, Journal(state));
    }

    private static bool SamePath(string a, string b) =>
        Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
