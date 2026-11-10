using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoopsWoWLauncher;

// ---------------------------------------------------------------------------
// Manifest model. Schema 1 (pre-Release 1) used only Files[] with
// name/sourceFile/sha256/type/destination; every schema-2 field is optional
// so older launchers keep parsing newer manifests.
// ---------------------------------------------------------------------------

public sealed class UpdateManifest
{
    public int Schema { get; set; } = 1;
    public string? ReleaseId { get; set; }
    public string? MinimumLauncherVersion { get; set; }
    public ClientProfile? Client { get; set; }
    public List<UpdateFile> Files { get; set; } = new();
    public List<ObsoleteFile> ObsoleteFiles { get; set; } = new();
    public List<BaselineFile> RequiredBaseline { get; set; } = new();
    // Optional packs the release authorizes (for example the HD Environment Pack, Data/patch-y.mpq). A file at a
    // listed destination is exempt from an obsolete/quarantine rule only when its full SHA-256 is listed here, the
    // core this release installs is one of RequiredCoreSha256, and the player enabled it through the launcher.
    // Launchers that predate this field ignore it, so for them the quarantine rule still applies.
    public List<OptionalPack> OptionalPacks { get; set; } = new();
}

public sealed class OptionalPack
{
    public string Name { get; set; } = "";
    public string Destination { get; set; } = "";
    public List<string> Sha256 { get; set; } = new();
    public List<string> RequiredCoreSha256 { get; set; } = new();
    public string? CoreDestination { get; set; } = "Data/patch-z.mpq";
}

public sealed class ClientProfile
{
    public string Build { get; set; } = "";
    public string Locale { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class UpdateFile
{
    public string Name { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Type { get; set; } = "";
    public string Destination { get; set; } = "";
    public long? Size { get; set; }
    public List<string> KnownPreviousSha256 { get; set; } = new();
    // Only missing, exactly qualified baseline archives can be supplied this way.
    // Existing mismatched baseline files remain incompatible and are not replaced.
    public bool SuppliesBaseline { get; set; }
    public List<PayloadPart> Parts { get; set; } = new();
    internal int DownloadCount => Math.Max(1, Parts.Count);
}

public sealed class PayloadPart
{
    public string SourceFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

public sealed class ObsoleteFile
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "quarantine";
    public List<string> KnownSha256 { get; set; } = new();
    public string? Reason { get; set; }
}

public sealed class BaselineFile
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public bool Critical { get; set; }
    // Absence is allowed only when the manifest explicitly qualifies a file as
    // optional. Present files still undergo the same size and SHA-256 checks.
    // Critical is descriptive metadata, not a requirement/optionality switch.
    public bool Optional { get; set; }
    public string? Note { get; set; }
}

// ---------------------------------------------------------------------------
// Errors carry a stable code plus user-facing recovery instructions.
// ---------------------------------------------------------------------------

public sealed class ReleaseUpdateException : Exception
{
    public string Code { get; }
    public string Recovery { get; }

    public ReleaseUpdateException(string code, string message, string recovery, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Recovery = recovery;
    }

    public string UserText => Message + "\n\n" + Recovery;
}

/// <summary>Test-only: simulates the process dying at a fault point (no in-process rollback).</summary>
internal sealed class SimulatedProcessTerminationException : Exception
{
    public SimulatedProcessTerminationException(string point) : base("Simulated termination at " + point) { }
}

// ---------------------------------------------------------------------------
// Payload sources
// ---------------------------------------------------------------------------

public sealed class PayloadResponse : IDisposable
{
    public required Stream Stream { get; init; }
    public long StartOffset { get; init; }
    public long? TotalBytes { get; init; }
    public Action? OnDispose { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
        OnDispose?.Invoke();
    }
}

public interface IPayloadSource
{
    /// <summary>Open sourceFile starting at offset. A source that cannot resume returns StartOffset 0.</summary>
    Task<PayloadResponse> OpenAsync(string sourceFile, long offset, CancellationToken ct);
}

public sealed class HttpPayloadSource : IPayloadSource
{
    private readonly HttpClient http;
    private readonly string baseUrl;

    public HttpPayloadSource(HttpClient http, string baseUrl)
    {
        this.http = http;
        this.baseUrl = baseUrl;
    }

    public async Task<PayloadResponse> OpenAsync(string sourceFile, long offset, CancellationToken ct)
    {
        string url = baseUrl + sourceFile;

        if (offset > 0)
        {
            using var ranged = new HttpRequestMessage(HttpMethod.Get, url);
            ranged.Headers.Range = new RangeHeaderValue(offset, null);
            HttpResponseMessage r = await http.SendAsync(ranged, HttpCompletionOption.ResponseHeadersRead, ct);

            if (r.StatusCode == HttpStatusCode.PartialContent &&
                r.Content.Headers.ContentRange?.From == offset)
            {
                Stream s = await r.Content.ReadAsStreamAsync(ct);
                return new PayloadResponse
                {
                    Stream = s, StartOffset = offset,
                    TotalBytes = r.Content.Headers.ContentRange?.Length, OnDispose = r.Dispose
                };
            }

            if (r.StatusCode == HttpStatusCode.OK)
            {
                // Server ignored the Range header: use the full body from the start.
                Stream s = await r.Content.ReadAsStreamAsync(ct);
                return new PayloadResponse
                {
                    Stream = s, TotalBytes = r.Content.Headers.ContentLength, OnDispose = r.Dispose
                };
            }

            r.Dispose();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        Stream body = await response.Content.ReadAsStreamAsync(ct);
        return new PayloadResponse
        {
            Stream = body,
            StartOffset = 0,
            TotalBytes = response.Content.Headers.ContentLength,
            OnDispose = () => { response.Dispose(); request.Dispose(); }
        };
    }
}

// ---------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------

public sealed class BaselineReport
{
    public int Verified { get; set; }
    public List<string> Missing { get; } = new();
    public List<string> Mismatched { get; } = new();
    public bool Ok => Missing.Count == 0 && Mismatched.Count == 0;
}

public sealed class ArchiveScan
{
    public List<string> Blocking { get; } = new();
    public List<string> Warnings { get; } = new();
}

public sealed class ReleaseResult
{
    public bool Changed { get; set; }
    public string? ReleaseId { get; set; }
    public string? TransactionId { get; set; }
    public string? BackupDirectory { get; set; }
    public List<string> Installed { get; } = new();
    public List<string> Quarantined { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? RecoveryNote { get; set; }
}

public sealed class RecoveryResult
{
    public string TransactionId { get; set; } = "";
    public bool RolledBack { get; set; }
    public bool Finalized { get; set; }
    public List<string> Problems { get; } = new();
}

// ---------------------------------------------------------------------------
// Journal (persisted before any client file is moved)
// ---------------------------------------------------------------------------

public sealed class InstallJournal
{
    public string TxId { get; set; } = "";
    public string? ReleaseId { get; set; }
    public string State { get; set; } = "applying";
    public DateTime CreatedUtc { get; set; }
    public List<JournalOp> Ops { get; set; } = new();
}

public sealed class JournalOp
{
    public string Kind { get; set; } = "";          // replace | quarantine
    public string Path { get; set; } = "";          // relative to client root
    public string? NewSha256 { get; set; }
    public long NewSize { get; set; }
    public string? StagedPath { get; set; }         // relative to client root
    public bool HadOriginal { get; set; }
    public string? OriginalSha256 { get; set; }
    public long OriginalSize { get; set; }
    public string? BackupPath { get; set; }         // relative to client root
}

internal sealed class BaselineCacheEntry
{
    public long Size { get; set; }
    public long LastWriteUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
}

// ---------------------------------------------------------------------------
// Updater
// ---------------------------------------------------------------------------

public sealed class ReleaseUpdater
{
    public const string StateDirName = "_CoopsLauncher";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string root;
    private readonly IPayloadSource source;
    private readonly IProgress<string>? progress;
    private readonly IProgress<DownloadStatus>? downloadProgress;
    private readonly Version launcherVersion;

    /// <summary>Test seam: invoked at named fault points during install.</summary>
    internal Action<string>? FaultHook { get; set; }

    /// <summary>Ignore the size/time cache and re-hash every baseline file.</summary>
    public bool ForceBaselineRehash { get; set; }

    public ReleaseUpdater(string wowPath, IPayloadSource source, Version launcherVersion,
        IProgress<string>? progress = null, IProgress<DownloadStatus>? downloadProgress = null)
    {
        root = Path.GetFullPath(wowPath);
        this.source = source;
        this.launcherVersion = launcherVersion;
        this.progress = progress;
        this.downloadProgress = downloadProgress;
    }

    public string StateDir => Path.Combine(root, StateDirName);
    private string StagingDir => Path.Combine(StateDir, "staging");
    private string BackupsDir => Path.Combine(StateDir, "backups");
    private string JournalPath => Path.Combine(StateDir, "transaction.json");
    private string ReceiptPath => Path.Combine(StateDir, "installed-release.json");
    private string BaselineCachePath => Path.Combine(StateDir, "baseline-cache.json");

    private void Report(string s) => progress?.Report(s);
    private void Fault(string point) => FaultHook?.Invoke(point);

    // ---------------- public entry points ----------------

    public static UpdateManifest ParseManifest(string json)
    {
        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions); }
        catch (JsonException ex)
        {
            throw new ReleaseUpdateException("MANIFEST_INVALID", "The update manifest could not be read.",
                "Check your internet connection and try again later. Nothing was changed.", ex);
        }

        if (m?.Files == null || m.Files.Count == 0)
            throw new ReleaseUpdateException("MANIFEST_INVALID", "The remote update manifest is invalid.",
                "Try again later. Nothing was changed.");

        m.ObsoleteFiles ??= new();
        m.RequiredBaseline ??= new();
        return m;
    }

    public void ValidateManifest(UpdateManifest m)
    {
        if (!string.IsNullOrWhiteSpace(m.MinimumLauncherVersion) &&
            Version.TryParse(m.MinimumLauncherVersion, out Version? min) &&
            launcherVersion < min)
        {
            throw new ReleaseUpdateException("LAUNCHER_TOO_OLD",
                $"This release requires launcher version {min} or newer (you have {launcherVersion}).",
                "Download the latest Coop's WoW Launcher, then run it again. Nothing was changed.");
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (UpdateFile f in m.Files)
        {
            if (string.IsNullOrWhiteSpace(f.Name) || string.IsNullOrWhiteSpace(f.SourceFile) ||
                string.IsNullOrWhiteSpace(f.Sha256) || string.IsNullOrWhiteSpace(f.Type) ||
                string.IsNullOrWhiteSpace(f.Destination) || f.Sha256.Length != 64 ||
                f.SourceFile.Contains("..") || f.SourceFile.Contains('/') || f.SourceFile.Contains('\\'))
            {
                throw new ReleaseUpdateException("MANIFEST_INVALID",
                    "The remote update manifest contains an invalid entry.", "Try again later. Nothing was changed.");
            }

            Resolve(f.Destination);
            if (!destinations.Add(Norm(f.Destination)))
                throw InvalidManifest("Duplicate update destination: " + f.Destination);

            if (f.SuppliesBaseline && !SuppliesBaseline(m, f.Destination))
                throw InvalidManifest("A baseline payload does not match its qualified prerequisite: " + f.Destination);

            if (f.Parts == null)
                throw InvalidManifest("Invalid payload parts.");
            if (f.Parts.Count > 0)
            {
                long total = 0;
                foreach (PayloadPart p in f.Parts)
                {
                    if (p == null || !SafeSource(p.SourceFile) || !ValidHash(p.Sha256) || p.Size <= 0 ||
                        p.Size > long.MaxValue - total)
                        throw InvalidManifest("Invalid multipart payload: " + f.Name);
                    total += p.Size;
                }
                if (!f.Type.Equals("file", StringComparison.OrdinalIgnoreCase) || total != f.Size ||
                    !ValidHash(f.Sha256))
                    throw InvalidManifest("Multipart size or hash is invalid: " + f.Name);
            }
        }

        foreach (ObsoleteFile o in m.ObsoleteFiles) Resolve(o.Path);
        foreach (OptionalPack p in m.OptionalPacks)
        {
            Resolve(p.Destination);
            string d = Norm(p.Destination);
            // An optional pack must load BELOW the release patch (patch-z) and must not be a managed or baseline file.
            if (!Regex.IsMatch(d, @"\Adata/patch-[a-y]\.mpq\z") ||
                m.Files.Any(f => Norm(f.Destination) == d) || m.RequiredBaseline.Any(b => Norm(b.Path) == d) ||
                p.Sha256.Count == 0 || !p.Sha256.All(ValidHash) ||
                p.RequiredCoreSha256.Count == 0 || !p.RequiredCoreSha256.All(ValidHash))
                throw InvalidManifest("Optional pack entry is invalid: " + p.Destination);
            if (p.CoreDestination is not null) Resolve(p.CoreDestination);
        }
        foreach (BaselineFile b in m.RequiredBaseline) Resolve(b.Path);
    }

    /// <summary>
    /// True only when every condition holds: the manifest lists this destination as an optional pack; the file's full
    /// SHA-256 is one the manifest authorizes; the core this release leaves installed is in RequiredCoreSha256; and the
    /// launcher's HD-pack receipt says the player installed and enabled exactly this file.
    /// </summary>
    internal async Task<bool> IsAuthorizedOptionalPackAsync(UpdateManifest m, string relativePath, string full, CancellationToken ct = default)
    {
        string rel = Norm(relativePath);
        var entries = m.OptionalPacks.Where(p => Norm(p.Destination) == rel && p.Sha256.Count > 0 && p.RequiredCoreSha256.Count > 0).ToList();
        if (entries.Count == 0) return false;
        HdPackInstaller.Receipt? receipt = HdPackInstaller.Installed(root);
        if (receipt is null || !receipt.Enabled) return false;
        string sha = await Sha256Async(full, ct);
        if (!sha.Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        // A manifest may authorize several pack versions (several entries or several hashes); any one entry that lists
        // this exact file AND the core this release leaves installed is enough.
        foreach (OptionalPack op in entries.Where(p => p.Sha256.Any(h => h.Equals(sha, StringComparison.OrdinalIgnoreCase))))
        {
            string coreRel = Norm(op.CoreDestination ?? "Data/patch-z.mpq");
            // The core after this release: the manifest's file for that destination if it ships one, else what is installed.
            string? core = m.Files.FirstOrDefault(f => f.Type.Equals("file", StringComparison.OrdinalIgnoreCase) && Norm(f.Destination) == coreRel)?.Sha256;
            if (core is null)
            {
                string coreFull = Resolve(op.CoreDestination ?? "Data/patch-z.mpq");
                if (!File.Exists(coreFull)) continue;
                core = await Sha256Async(coreFull, ct);
            }
            if (op.RequiredCoreSha256.Any(h => h.Equals(core, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool ValidHash(string? hash) => hash != null && Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z");
    private static bool SafeSource(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name != "." && !name.Contains("..") && name.IndexOfAny(new[] { '/', '\\', ':', '?', '#' }) < 0;
    private static ReleaseUpdateException InvalidManifest(string message) =>
        new("MANIFEST_INVALID", message, "Nothing was changed. Contact the server admin.");

    private static bool SuppliesBaseline(UpdateManifest m, string path)
    {
        string normalized = Norm(path);
        // Baseline provisioning is limited to custom HD archives. Never supply
        // executables or stock/base archives to turn an incompatible client into a match.
        if (!Regex.IsMatch(normalized, @"\Adata/(patch-[5-9a-z]|enus/patch-enus-[a-z])\.mpq\z"))
            return false;
        BaselineFile? b = m.RequiredBaseline.SingleOrDefault(b => Norm(b.Path) == normalized);
        return b != null && !b.Optional && ValidHash(b.Sha256) && b.Size > 0 &&
            m.Files.Any(f => f.SuppliesBaseline && f.Type.Equals("file", StringComparison.OrdinalIgnoreCase) &&
                Norm(f.Destination) == normalized && f.Size == b.Size &&
                b.Sha256.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finish or undo a transaction left behind by a crash, power loss or forced close.
    /// Safe to call at every start, even offline.
    /// </summary>
    public async Task<RecoveryResult?> RecoverInterruptedInstallAsync(CancellationToken ct = default)
    {
        if (!File.Exists(JournalPath))
            return null;

        InstallJournal? j;
        try { j = JsonSerializer.Deserialize<InstallJournal>(await File.ReadAllTextAsync(JournalPath, ct), JsonOptions); }
        catch (JsonException) { j = null; }

        if (j == null)
        {
            string bad = JournalPath + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(JournalPath, bad);
            throw new ReleaseUpdateException("ROLLBACK_INCOMPLETE",
                "A previous installation was interrupted and its recovery record is unreadable.",
                $"Close WoW and restore the files under {BackupsDir} to their original folders, " +
                "then press Check / Install Patch. The record was kept at " + bad + ".");
        }

        var result = new RecoveryResult { TransactionId = j.TxId };

        if (j.State == "committed")
        {
            File.Delete(JournalPath);
            result.Finalized = true;
            return result;
        }

        Report("Recovering from an interrupted installation...");
        result.Problems.AddRange(await RollbackAsync(j, ct));
        result.RolledBack = true;
        FinishRollback(j, result.Problems);
        return result;
    }

    public async Task<BaselineReport> VerifyBaselineAsync(UpdateManifest m, CancellationToken ct = default)
    {
        var report = new BaselineReport();
        if (m.RequiredBaseline.Count == 0)
            return report;

        Dictionary<string, BaselineCacheEntry> cache = LoadBaselineCache();
        bool cacheChanged = false;
        int n = 0;

        foreach (BaselineFile b in m.RequiredBaseline)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            string full = Resolve(b.Path);
            string label = b.Path + (b.Critical && b.Note != null ? $" ({b.Note})" : "");

            if (!File.Exists(full))
            {
                if (!b.Optional && !SuppliesBaseline(m, b.Path))
                    report.Missing.Add(label);
                continue;
            }

            var info = new FileInfo(full);
            if (info.Length != b.Size)
            {
                report.Mismatched.Add($"{label}: size {info.Length:N0}, expected {b.Size:N0}");
                continue;
            }

            string key = Norm(b.Path);
            string hash;
            if (!ForceBaselineRehash && cache.TryGetValue(key, out BaselineCacheEntry? c) &&
                c.Size == info.Length && c.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                hash = c.Sha256;
            }
            else
            {
                Report($"Verifying client files ({n}/{m.RequiredBaseline.Count}): {b.Path}");
                hash = await Sha256Async(full, ct);
                cache[key] = new BaselineCacheEntry
                {
                    Size = info.Length,
                    LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                    Sha256 = hash
                };
                cacheChanged = true;
            }

            if (!hash.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                report.Mismatched.Add($"{label}: content differs from the supported client");
                cache.Remove(key);
                cacheChanged = true;
            }
            else
            {
                report.Verified++;
            }
        }

        if (cacheChanged)
            SaveJsonAtomic(BaselineCachePath, cache);

        return report;
    }

    public ArchiveScan ScanArchives(UpdateManifest m)
    {
        var scan = new ArchiveScan();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BaselineFile b in m.RequiredBaseline) known.Add(Norm(b.Path));
        foreach (UpdateFile f in m.Files) known.Add(Norm(f.Destination));
        foreach (ObsoleteFile o in m.ObsoleteFiles) known.Add(Norm(o.Path));

        string data = Path.Combine(root, "Data");
        if (!Directory.Exists(data))
            return scan;

        var single = new Regex(@"^patch-(.)\.mpq$", RegexOptions.IgnoreCase);
        foreach (string file in Directory.GetFiles(data, "patch-*.mpq", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            Match mt = single.Match(name);
            if (!mt.Success || known.Contains("data/" + name.ToLowerInvariant()) ||
                (m.OptionalPacks.Any(p => Norm(p.Destination) == "data/" + name.ToLowerInvariant()) && HdPackInstaller.LooksManaged(root, file)))
                continue;

            char c = char.ToLowerInvariant(mt.Groups[1].Value[0]);
            if (c > 'z')
                scan.Blocking.Add($"Data/{name} loads after the release patch and could override it");
            else
                scan.Warnings.Add($"Data/{name} is not part of the supported client (not modified)");
        }

        string? locale = m.Client?.Locale;
        if (!string.IsNullOrWhiteSpace(locale))
        {
            string locDir = Path.Combine(data, locale);
            if (Directory.Exists(locDir))
            {
                var loc = new Regex($@"^patch-{Regex.Escape(locale)}-(.)\.mpq$", RegexOptions.IgnoreCase);
                foreach (string file in Directory.GetFiles(locDir, "patch-*.mpq", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    string rel = $"data/{locale}/{name}".ToLowerInvariant();
                    if (loc.IsMatch(name) && !known.Contains(rel))
                        scan.Blocking.Add($"Data/{locale}/{name} is an unqualified locale patch that could override the release");
                }
            }
        }

        return scan;
    }

    /// <summary>
    /// Validate prerequisites, then make the client match the manifest's "file" entries
    /// and obsolete-file rules in one recoverable transaction. Idempotent.
    /// </summary>
    public async Task<ReleaseResult> ApplyAsync(UpdateManifest m, CancellationToken ct = default)
    {
        ValidateManifest(m);

        var result = new ReleaseResult { ReleaseId = m.ReleaseId };

        RecoveryResult? rec = await RecoverInterruptedInstallAsync(ct);
        if (rec?.RolledBack == true)
            result.RecoveryNote = "An interrupted installation was rolled back to your previous files before updating.";

        Report("Checking client files...");
        BaselineReport baseline = await VerifyBaselineAsync(m, ct);
        if (!baseline.Ok)
        {
            string desc = m.Client?.Description ?? "the supported client";
            var lines = baseline.Missing.Select(x => "Missing: " + x)
                .Concat(baseline.Mismatched.Select(x => "Changed: " + x)).Take(12);
            throw new ReleaseUpdateException("UNSUPPORTED_BASELINE",
                $"Unsupported or incomplete client installation. This update requires {desc}.\n" +
                string.Join("\n", lines),
                "Nothing was changed. Restore the listed files from your original HD client download " +
                "(do not mix in files from other clients), then press Check / Install Patch again.");
        }

        ArchiveScan scan = ScanArchives(m);
        result.Warnings.AddRange(scan.Warnings);
        if (scan.Blocking.Count > 0)
        {
            throw new ReleaseUpdateException("UNEXPECTED_ARCHIVE",
                "Your client contains patch archives that would override this release:\n" +
                string.Join("\n", scan.Blocking),
                "Nothing was changed. Close WoW and move those files out of the Data folder " +
                "(keep them somewhere safe), then press Check / Install Patch again.");
        }

        // Work out what must change.
        var needed = new List<(UpdateFile File, string Dest, bool Exists, string? CurrentSha, long CurrentSize)>();
        foreach (UpdateFile f in m.Files.Where(x => x.Type.Equals("file", StringComparison.OrdinalIgnoreCase)))
        {
            string dest = Resolve(f.Destination);
            if (File.Exists(dest))
            {
                Report($"Verifying {f.Name}...");
                long len = new FileInfo(dest).Length;
                string h = (f.Size is long s && s != len) ? "" : await Sha256Async(dest, ct);
                if (h.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;
                needed.Add((f, dest, true, h.Length == 0 ? await Sha256Async(dest, ct) : h, len));
            }
            else
            {
                needed.Add((f, dest, false, null, 0));
            }
        }

        var obsolete = new List<(ObsoleteFile O, string Full)>();
        foreach (ObsoleteFile o in m.ObsoleteFiles)
        {
            string full = Resolve(o.Path);
            // The HD Environment Pack reuses the old Data\patch-y.mpq name. Only an authorized, enabled, unmodified
            // pack for the installed core is spared; a legacy, unknown or modified patch-y is still quarantined.
            if (File.Exists(full) && !await IsAuthorizedOptionalPackAsync(m, o.Path, full, ct))
                obsolete.Add((o, full));
        }

        if (needed.Count == 0 && obsolete.Count == 0)
        {
            WriteReceiptIfChanged(m, null);
            return result;
        }

        // Stage (download + verify) everything first; the client is untouched until all payloads verify.
        var staged = new Dictionary<UpdateFile, string>();
        var activationPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int queueCount = needed.Sum(n => n.File.DownloadCount);
        int position = 1;
        foreach (var n in needed)
        {
            var item = new DownloadItem(DownloadKind.GamePatch, n.File.Name, position, queueCount);
            string payload = await DownloadAndVerifyAsync(n.File, item, ct);
            position += n.File.DownloadCount;
            if (!activationPaths.Add(payload))
            {
                // Equal content can supply different destinations (global/locale
                // archives). Each journal op needs its own movable staging file.
                string destinationKey = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(Norm(n.File.Destination)))).ToLowerInvariant();
                string activation = Path.Combine(StagingDir, n.File.Sha256.ToLowerInvariant() + "-" + destinationKey + ".install");
                EnsureFreeSpace(new FileInfo(payload).Length);
                File.Copy(payload, activation, true);
                await VerifyFileAsync(activation, n.File.Sha256, new FileInfo(payload).Length, "staged " + n.File.Name, ct);
                payload = activation;
            }
            staged[n.File] = payload;
        }

        // Refuse to start while WoW (or anything else) holds a file we must move.
        foreach (string p in needed.Where(x => x.Exists).Select(x => x.Dest).Concat(obsolete.Select(x => x.Full)))
            EnsureNotLocked(p);

        var j = new InstallJournal
        {
            TxId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + Guid.NewGuid().ToString("N")[..8],
            ReleaseId = m.ReleaseId,
            CreatedUtc = DateTime.UtcNow
        };
        string txBackup = Path.Combine(BackupsDir, j.TxId);

        foreach (var n in needed)
        {
            j.Ops.Add(new JournalOp
            {
                Kind = "replace",
                Path = Norm(n.File.Destination),
                NewSha256 = n.File.Sha256.ToLowerInvariant(),
                NewSize = new FileInfo(staged[n.File]).Length,
                StagedPath = Rel(staged[n.File]),
                HadOriginal = n.Exists,
                OriginalSha256 = n.CurrentSha?.ToLowerInvariant(),
                OriginalSize = n.CurrentSize,
                BackupPath = n.Exists ? Rel(Path.Combine(txBackup, Norm(n.File.Destination).Replace('/', '\\'))) : null
            });
        }

        foreach (var (o, full) in obsolete)
        {
            Report($"Preserving {o.Path}...");
            j.Ops.Add(new JournalOp
            {
                Kind = "quarantine",
                Path = Norm(o.Path),
                HadOriginal = true,
                OriginalSha256 = await Sha256Async(full, ct),
                OriginalSize = new FileInfo(full).Length,
                BackupPath = Rel(Path.Combine(txBackup, Norm(o.Path).Replace('/', '\\')))
            });
        }

        SaveJsonAtomic(JournalPath, j);

        try
        {
            foreach (JournalOp op in j.Ops.Where(o => o.Kind == "replace"))
            {
                string dest = Abs(op.Path);
                string stagedFull = Abs(op.StagedPath!);
                Fault("before-replace:" + op.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                ReportPhase(new(DownloadKind.GamePatch, Path.GetFileName(op.Path)), DownloadPhase.Installing);

                if (op.HadOriginal)
                {
                    string backup = Abs(op.BackupPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Replace(stagedFull, dest, backup, true);
                }
                else
                {
                    File.Move(stagedFull, dest);
                }

                Fault("after-replace:" + op.Path);

                await VerifyFileAsync(dest, op.NewSha256!, op.NewSize, "installed " + op.Path, ct);
                if (op.HadOriginal)
                    await VerifyFileAsync(Abs(op.BackupPath!), op.OriginalSha256!, op.OriginalSize, "backup of " + op.Path, ct);
            }

            Fault("after-verify");

            foreach (JournalOp op in j.Ops.Where(o => o.Kind == "quarantine"))
            {
                string backup = Abs(op.BackupPath!);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Move(Abs(op.Path), backup);
                Fault("after-quarantine:" + op.Path);
            }

            // Final verification of the complete resulting state.
            foreach (JournalOp op in j.Ops)
            {
                if (op.Kind == "replace")
                    await VerifyFileAsync(Abs(op.Path), op.NewSha256!, op.NewSize, "installed " + op.Path, ct);
                else if (File.Exists(Abs(op.Path)) || !File.Exists(Abs(op.BackupPath!)) ||
                         new FileInfo(Abs(op.BackupPath!)).Length != op.OriginalSize)
                    throw new IOException($"Obsolete file {op.Path} was not preserved correctly.");
            }

            Fault("before-commit");

            WriteRestoreInfo(j, txBackup);
            j.State = "committed";
            SaveJsonAtomic(JournalPath, j);
            WriteReceiptIfChanged(m, j);
            File.Delete(JournalPath);
        }
        catch (Exception ex) when (ex is not SimulatedProcessTerminationException)
        {
            Report("Installation failed; restoring previous files...");
            List<string> problems = await RollbackAsync(j, CancellationToken.None);
            FinishRollback(j, problems);

            if (problems.Count > 0)
            {
                throw new ReleaseUpdateException("ROLLBACK_INCOMPLETE",
                    "The update failed and some previous files could not be restored automatically:\n" +
                    string.Join("\n", problems),
                    $"Do not delete anything. Close WoW and copy the files from {txBackup} back to the same " +
                    "folders inside your WoW directory, then press Check / Install Patch.", ex);
            }

            string reason = ex is ReleaseUpdateException rex ? rex.Message : ex.Message;
            bool locked = ex is IOException && !(ex is FileNotFoundException) && IsSharingViolation(ex);
            throw new ReleaseUpdateException(locked ? "FILE_LOCKED" : "INSTALL_FAILED_ROLLED_BACK",
                "The update could not be installed: " + reason,
                "Your previous patch files were restored and verified. " +
                (locked ? "Close World of Warcraft and " : "") +
                "Press Check / Install Patch to try again.", ex);
        }

        result.Changed = true;
        result.TransactionId = j.TxId;
        result.BackupDirectory = j.Ops.Any(o => o.BackupPath != null) ? txBackup : null;
        result.Installed.AddRange(j.Ops.Where(o => o.Kind == "replace").Select(o => o.Path));
        result.Quarantined.AddRange(j.Ops.Where(o => o.Kind == "quarantine").Select(o => o.Path));
        return result;
    }

    /// <summary>
    /// Restore the exact files preserved by a committed transaction (manual rollback/support use).
    /// The current release files are parked in a new backup folder, never deleted.
    /// </summary>
    public async Task RestoreBackupAsync(string txId, CancellationToken ct = default)
    {
        string txBackup = Path.Combine(BackupsDir, txId);
        string info = Path.Combine(txBackup, "restore.json");
        if (!File.Exists(info))
            throw new ReleaseUpdateException("RESTORE_UNAVAILABLE", $"No restore record exists for {txId}.",
                "Choose a folder under " + BackupsDir + " that contains restore.json.");

        InstallJournal j = JsonSerializer.Deserialize<InstallJournal>(await File.ReadAllTextAsync(info, ct), JsonOptions)!;
        string park = Path.Combine(BackupsDir, "displaced-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));

        foreach (JournalOp op in j.Ops)
        {
            string target = Abs(op.Path);
            if (op.BackupPath != null && File.Exists(Abs(op.BackupPath))) EnsureNotLocked(Abs(op.BackupPath));
            if (File.Exists(target)) EnsureNotLocked(target);
        }

        foreach (JournalOp op in j.Ops)
        {
            string target = Abs(op.Path);
            if (File.Exists(target) && op.OriginalSha256 != null &&
                (await Sha256Async(target, ct)).Equals(op.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                continue;

            if (File.Exists(target))
            {
                string parked = Path.Combine(park, op.Path.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(parked)!);
                File.Move(target, parked);
            }

            if (op.BackupPath != null && File.Exists(Abs(op.BackupPath)))
            {
                File.Move(Abs(op.BackupPath), target);
                await VerifyFileAsync(target, op.OriginalSha256!, op.OriginalSize, "restored " + op.Path, ct);
            }
        }

        if (File.Exists(ReceiptPath))
            File.Delete(ReceiptPath);
    }

    /// <summary>Download into the staging folder (resumable) and verify size + SHA-256.</summary>
    public Task<string> DownloadAndVerifyAsync(UpdateFile file, CancellationToken ct = default)
    {
        bool isAddon = file.Type.Equals("addon", StringComparison.OrdinalIgnoreCase);
        var item = new DownloadItem(isAddon ? DownloadKind.Addon : DownloadKind.GamePatch,
            isAddon ? file.SourceFile : file.Name, 1, file.DownloadCount);
        return DownloadAndVerifyAsync(file, item, ct);
    }

    internal async Task<string> DownloadAndVerifyAsync(UpdateFile file, DownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDir);
        string sha = file.Sha256.ToLowerInvariant();
        string final = Path.Combine(StagingDir, sha + ".payload");
        string partial = Path.Combine(StagingDir, sha + ".partial");

        if (File.Exists(final))
        {
            ReportPhase(item, DownloadPhase.Verifying);
            if (await MatchesAsync(final, sha, file.Size, ct))
                return final;
            File.Delete(final);
        }

        if (file.Parts.Count > 0)
        {
            // Use the same resumable SHA-256 downloader for every part. Nothing
            // reaches the client until all parts and the full assembled MPQ verify.
            var parts = new List<string>();
            for (int index = 0; index < file.Parts.Count; index++)
            {
                PayloadPart p = file.Parts[index];
                parts.Add(await DownloadAndVerifyAsync(new UpdateFile
                {
                    Name = p.SourceFile, SourceFile = p.SourceFile,
                    Sha256 = p.Sha256, Size = p.Size
                }, item with { Filename = p.SourceFile, Position = item.Position + index }, ct));
            }
            EnsureFreeSpace(file.Size ?? 0);
            ReportPhase(item, DownloadPhase.Assembling);
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
            {
                foreach (string part in parts)
                {
                    using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
                    await input.CopyToAsync(output, ct);
                }
                await output.FlushAsync(ct);
                output.Flush(true);
            }
            ReportPhase(item, DownloadPhase.Verifying);
            if (!await MatchesAsync(partial, sha, file.Size, ct))
            {
                File.Delete(partial);
                throw new ReleaseUpdateException("HASH_MISMATCH", file.Name + " failed assembled SHA-256 verification.",
                    "Your game files were not changed. Contact the server admin.");
            }
            File.Move(partial, final, true);
            return final;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (file.Size is null || have > file.Size)
            {
                if (have > 0) File.Delete(partial);
                have = 0;
            }

            long received = 0;
            if (file.Size is null || have < file.Size)
            {
                EnsureFreeSpace((file.Size ?? 0) - have);
                if (downloadProgress is null)
                    Report($"Downloading {file.Name}...");
                else
                    downloadProgress.Report(new(item, DownloadPhase.Downloading, have, file.Size));

                try
                {
                    using PayloadResponse resp = await source.OpenAsync(file.SourceFile, have, ct);
                    bool append = have > 0 && resp.StartOffset == have;
                    using var fs = new FileStream(partial, append ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, 1 << 20, true);
                    received = await DownloadTransfer.CopyAsync(resp.Stream, fs, item, append ? have : 0,
                        file.Size ?? resp.TotalBytes, downloadProgress, ct);
                    await fs.FlushAsync(ct);
                    fs.Flush(true);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    throw new ReleaseUpdateException("DOWNLOAD_INTERRUPTED",
                        $"The download of {file.Name} was interrupted ({ex.Message}).",
                        "Your game files were not changed. Downloaded progress was kept; press " +
                        "Check / Install Patch to resume.", ex);
                }
            }

            long got = new FileInfo(partial).Length;
            if (file.Size is long wanted && got < wanted && received == 0)
            {
                // The source has nothing more to give: its file is shorter than the manifest says.
                File.Delete(partial);
                throw new ReleaseUpdateException("DOWNLOAD_SIZE_MISMATCH",
                    $"The server's copy of {file.Name} is smaller than the update manifest says ({got:N0} of {wanted:N0} bytes).",
                    "Your game files were not changed. Try again later; if this persists, contact the server admin.");
            }

            if (file.Size is long expected && got < expected)
            {
                throw new ReleaseUpdateException("DOWNLOAD_INTERRUPTED",
                    $"The download of {file.Name} ended early ({got:N0} of {expected:N0} bytes).",
                    "Your game files were not changed. Press Check / Install Patch to resume the download.");
            }

            ReportPhase(item, DownloadPhase.Verifying);
            if (await MatchesAsync(partial, sha, file.Size, ct))
            {
                File.Move(partial, final, true);
                return final;
            }

            File.Delete(partial);
            if (attempt == 0 && have > 0)
                continue; // resumed bytes may be bad: retry once from scratch
            break;
        }

        throw new ReleaseUpdateException("HASH_MISMATCH",
            $"{file.Name} failed SHA-256 verification; the downloaded file was discarded.",
            "Your game files were not changed. Press Check / Install Patch to try again. " +
            "If this keeps happening, the download server may be serving a damaged file; contact the server admin.");
    }

    private void ReportPhase(DownloadItem item, DownloadPhase phase)
    {
        if (downloadProgress is null)
            Report($"{phase} {item.Filename}...");
        else
            downloadProgress.Report(new(item, phase));
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        return await Task.Run(async () =>
        {
            using var sha = SHA256.Create();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
            byte[] hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }, ct);
    }

    // ---------------- rollback ----------------

    private async Task<List<string>> RollbackAsync(InstallJournal j, CancellationToken ct)
    {
        var problems = new List<string>();

        foreach (JournalOp op in Enumerable.Reverse(j.Ops))
        {
            try
            {
                string target = Abs(op.Path);
                string? backup = op.BackupPath == null ? null : Abs(op.BackupPath);

                if (op.Kind == "quarantine")
                {
                    if (!File.Exists(target) && backup != null && File.Exists(backup))
                        File.Move(backup, target);
                }
                else if (op.Kind == "replace")
                {
                    bool backupExists = backup != null && File.Exists(backup);
                    // Replacement happened if the original was moved to backup, or (no original) dest now exists.
                    bool replaced = op.HadOriginal ? backupExists : File.Exists(target);

                    if (replaced && File.Exists(target))
                        await ParkAsync(target, op, j.TxId, ct);

                    if (op.HadOriginal && backupExists)
                        File.Move(backup!, target);
                }

                if (op.HadOriginal)
                {
                    if (!File.Exists(target))
                        problems.Add($"{op.Path} is missing after rollback (backup: {op.BackupPath})");
                    else if (!(await Sha256Async(target, ct)).Equals(op.OriginalSha256, StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{op.Path} does not match its original after rollback");
                }
                else if (File.Exists(target))
                {
                    problems.Add($"{op.Path} still exists after rollback");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{op.Path}: {ex.Message}");
            }
        }

        return problems;
    }

    /// <summary>Move a file we installed out of the way without deleting anything unexpected.</summary>
    private async Task ParkAsync(string target, JournalOp op, string txId, CancellationToken ct)
    {
        string h = await Sha256Async(target, ct);
        if (h.Equals(op.NewSha256, StringComparison.OrdinalIgnoreCase))
        {
            string staged = Abs(op.StagedPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            if (File.Exists(staged))
                File.Delete(target); // identical verified copy already staged
            else
                File.Move(target, staged); // keep for the retry; avoids re-downloading
        }
        else
        {
            string displaced = Path.Combine(BackupsDir, txId, "rollback-displaced", op.Path.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(displaced)!);
            File.Move(target, displaced, true);
        }
    }

    private void FinishRollback(InstallJournal j, List<string> problems)
    {
        if (problems.Count == 0)
        {
            string txBackup = Path.Combine(BackupsDir, j.TxId);
            foreach (string f in new[] { "restore.json", "restore.json.tmp", "README.txt" })
                if (File.Exists(Path.Combine(txBackup, f))) File.Delete(Path.Combine(txBackup, f));
            File.Delete(JournalPath);
            DeleteEmptyDirs(txBackup);
        }
        else if (File.Exists(JournalPath))
        {
            File.Move(JournalPath, Path.Combine(StateDir, $"transaction-failed-{j.TxId}.json"), true);
        }
    }

    // ---------------- helpers ----------------

    private async Task VerifyFileAsync(string path, string sha, long size, string what, CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size ||
            !(await Sha256Async(path, ct)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Verification failed for {what}.");
    }

    private static async Task<bool> MatchesAsync(string path, string sha, long? size, CancellationToken ct)
    {
        if (size is long s && new FileInfo(path).Length != s) return false;
        return (await Sha256Async(path, ct)).Equals(sha, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureNotLocked(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new ReleaseUpdateException("FILE_LOCKED",
                $"{Path.GetFileName(path)} is in use by another program.",
                "Nothing was changed. Close World of Warcraft (and any MPQ editor), then press Check / Install Patch.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new ReleaseUpdateException("ACCESS_DENIED",
                $"The launcher is not allowed to modify {path}.",
                "Nothing was changed. Make sure the file is not read-only, or run the launcher with access to your WoW folder.", ex);
        }
    }

    private static bool IsSharingViolation(Exception ex)
    {
        const int sharing = unchecked((int)0x80070020), lockViolation = unchecked((int)0x80070021);
        return ex.HResult == sharing || ex.HResult == lockViolation;
    }

    private void EnsureFreeSpace(long bytes)
    {
        if (bytes <= 0) return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(StateDir)!);
            if (drive.AvailableFreeSpace < bytes + (64L << 20))
                throw new ReleaseUpdateException("DISK_SPACE",
                    $"Not enough free disk space to download the update ({(bytes >> 20):N0} MB needed).",
                    "Free some space on the drive that contains World of Warcraft and try again. Nothing was changed.");
        }
        catch (ArgumentException) { /* network or unusual path: let the write fail naturally */ }
    }

    private void WriteRestoreInfo(InstallJournal j, string txBackup)
    {
        if (!j.Ops.Any(o => o.BackupPath != null)) return;
        Directory.CreateDirectory(txBackup);
        SaveJsonAtomic(Path.Combine(txBackup, "restore.json"), j);
        File.WriteAllText(Path.Combine(txBackup, "README.txt"),
            "Coop's WoW Launcher preserved these files exactly as they were before installing " +
            (j.ReleaseId ?? "an update") + ".\r\n" +
            "To return to them: close WoW, move Data\\patch-z.mpq out of your Data folder, then move every file " +
            "under this folder's Data directory back to the same place in your WoW Data folder.\r\n" +
            "SHA-256 hashes of the originals are listed in restore.json (OriginalSha256).\r\n");
    }

    private void WriteReceiptIfChanged(UpdateManifest m, InstallJournal? j)
    {
        if (m.ReleaseId == null) return;
        var receipt = new
        {
            releaseId = m.ReleaseId,
            files = m.Files.Where(f => f.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
                .Select(f => new { path = Norm(f.Destination), sha256 = f.Sha256.ToLowerInvariant(), size = f.Size }),
            lastTransaction = j?.TxId
        };

        if (j == null && File.Exists(ReceiptPath))
        {
            try
            {
                using JsonDocument d = JsonDocument.Parse(File.ReadAllText(ReceiptPath));
                if (d.RootElement.GetProperty("releaseId").GetString() == m.ReleaseId)
                    return;
            }
            catch (Exception) { /* rewrite below */ }
        }

        SaveJsonAtomic(ReceiptPath, receipt);
    }

    private Dictionary<string, BaselineCacheEntry> LoadBaselineCache()
    {
        try
        {
            if (File.Exists(BaselineCachePath))
                return JsonSerializer.Deserialize<Dictionary<string, BaselineCacheEntry>>(
                           File.ReadAllText(BaselineCachePath), JsonOptions)
                       ?? new();
        }
        catch (Exception) { /* corrupt cache: re-hash */ }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveJsonAtomic(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, value, value.GetType(), JsonOptions);
            fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }

    private static void DeleteEmptyDirs(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (string sub in Directory.GetDirectories(dir)) DeleteEmptyDirs(sub);
        if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    private static string Norm(string rel) => rel.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

    private string Abs(string rel) => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Rel(string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    /// <summary>Resolve a manifest-relative path, refusing anything outside the client folder.</summary>
    private string Resolve(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.Contains("..") || rel.Contains(':'))
            throw new ReleaseUpdateException("MANIFEST_INVALID", $"Unsafe path in manifest: {rel}",
                "Nothing was changed. Contact the server admin.");

        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(StateDir, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseUpdateException("MANIFEST_INVALID", $"Unsafe path in manifest: {rel}",
                "Nothing was changed. Contact the server admin.");
        return full;
    }
}
