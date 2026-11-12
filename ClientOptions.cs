using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopsWoWLauncher;

// ---------------------------------------------------------------------------
// Client options: categories of optional patch archives (HD weapons, HD armor, new music, ...) that a player may turn
// off. Turning a category off renames its archives to "<name>.mpq.disabled", the same convention the client's bundled
// PatchMenu uses, so choices made in either tool are honoured by both. A turned-off archive is never re-downloaded,
// re-enabled or reported as missing by Check / Install Patch. Locked categories are needed by the release (for
// example the HD Goblin models) and are always kept on. Launchers older than 1.5.0 ignore these manifest fields.
// ---------------------------------------------------------------------------

public sealed class ClientOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public string? Description { get; set; }
    // Baseline archives (client-relative) that make up this category. They are turned on and off together.
    public List<string> Archives { get; set; } = new();
    public bool Locked { get; set; }
    public string? LockReason { get; set; }
    // Ids of categories that must be on for this one to be on (for example dungeon mini maps need dungeon maps).
    public List<string> Requires { get; set; } = new();
    public string? Warning { get; set; }
    // Optional small archive installed only while this category is off. It puts back the stock versions of files that
    // another, still-enabled archive would otherwise keep showing (HD weapon models duplicated in the HD armor archive).
    public RestoreArchive? Restore { get; set; }
}

public sealed class RestoreArchive
{
    public string Destination { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string? Note { get; set; }
}

public enum ClientOptionMode { On, Off, Partial }

public sealed record ClientOptionState(ClientOption Option, ClientOptionMode Mode, long Bytes, bool NeedsDownload);

public sealed partial class ReleaseUpdater
{
    public const string DisabledSuffix = ".disabled";

    private sealed class ClientOptionsFile
    {
        public List<string> Off { get; set; } = new();
    }

    private string ClientOptionsPath => Path.Combine(StateDir, "client-options.json");

    private ClientOptionsFile LoadClientOptions()
    {
        try
        {
            if (File.Exists(ClientOptionsPath))
                return JsonSerializer.Deserialize<ClientOptionsFile>(File.ReadAllText(ClientOptionsPath), JsonOptions) ?? new();
        }
        catch (Exception) { /* unreadable: treat as no saved choices; .disabled files still count */ }
        return new();
    }

    private enum ArchiveState { Active, Disabled, Missing }

    private ArchiveState StateOf(string rel)
    {
        string full = Resolve(rel);
        if (File.Exists(full)) return ArchiveState.Active;
        return File.Exists(full + DisabledSuffix) ? ArchiveState.Disabled : ArchiveState.Missing;
    }

    /// <summary>
    /// Archives the player turned off (normalized client-relative paths): renamed to .disabled (by this launcher or
    /// PatchMenu), or missing while their category is saved as off. Archives of locked categories are never included.
    /// </summary>
    public HashSet<string> DisabledArchives(UpdateManifest m)
    {
        var off = new HashSet<string>(LoadClientOptions().Off, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lockedArchives = new HashSet<string>(m.ClientOptions.Where(o => o.Locked).SelectMany(o => o.Archives).Select(Norm),
            StringComparer.OrdinalIgnoreCase);
        foreach (ClientOption o in m.ClientOptions.Where(o => !o.Locked))
            foreach (string a in o.Archives)
            {
                if (lockedArchives.Contains(Norm(a))) continue;
                ArchiveState s = StateOf(a);
                if (s == ArchiveState.Disabled || (s == ArchiveState.Missing && off.Contains(o.Id)))
                    result.Add(Norm(a));
            }
        return result;
    }

    public List<ClientOptionState> GetClientOptions(UpdateManifest m)
    {
        HashSet<string> disabled = DisabledArchives(m);
        var list = new List<ClientOptionState>();
        foreach (ClientOption o in m.ClientOptions)
        {
            int offCount = o.Archives.Count(a => disabled.Contains(Norm(a)));
            ClientOptionMode mode = offCount == 0 ? ClientOptionMode.On :
                offCount == o.Archives.Count ? ClientOptionMode.Off : ClientOptionMode.Partial;
            long bytes = o.Archives.Sum(a => m.RequiredBaseline.FirstOrDefault(b => Norm(b.Path) == Norm(a))?.Size ?? 0);
            bool download = o.Archives.Any(a => !disabled.Contains(Norm(a)) && StateOf(a) == ArchiveState.Missing &&
                                                !(m.RequiredBaseline.FirstOrDefault(b => Norm(b.Path) == Norm(a))?.Optional ?? false));
            list.Add(new ClientOptionState(o, mode, bytes, download));
        }
        return list;
    }

    private void ValidateClientOptions(UpdateManifest m)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var managed = new HashSet<string>(m.Files.Where(f => !f.SuppliesBaseline).Select(f => Norm(f.Destination)), StringComparer.OrdinalIgnoreCase);
        foreach (ClientOption o in m.ClientOptions)
        {
            if (o == null || string.IsNullOrWhiteSpace(o.Id) || !Regex.IsMatch(o.Id, @"\A[a-z0-9-]{1,40}\z") ||
                string.IsNullOrWhiteSpace(o.Name) || !ids.Add(o.Id) || o.Archives == null || o.Archives.Count == 0)
                throw InvalidManifest("Invalid client option: " + o?.Id);
            o.Requires ??= new();
            foreach (string a in o.Archives)
            {
                Resolve(a);
                string n = Norm(a);
                if (!Regex.IsMatch(n, @"\Adata/(patch-[0-9a-z]|enus/patch-enus-[0-9a-z])\.mpq\z") || managed.Contains(n) ||
                    !m.RequiredBaseline.Any(b => Norm(b.Path) == n) || !archives.Add(n))
                    throw InvalidManifest("Client option archive is invalid: " + a);
            }
            if (o.Restore is { } r)
            {
                Resolve(r.Destination);
                string d = Norm(r.Destination);
                if (!Regex.IsMatch(d, @"\Adata/patch-[0-9a-y]\.mpq\z") || !SafeSource(r.SourceFile) || !ValidHash(r.Sha256) || r.Size <= 0 ||
                    m.Files.Any(f => Norm(f.Destination) == d) || m.RequiredBaseline.Any(b => Norm(b.Path) == d) ||
                    m.OptionalPacks.Any(p => Norm(p.Destination) == d) || m.ObsoleteFiles.Any(x => Norm(x.Path) == d) || !restores.Add(d))
                    throw InvalidManifest("Client option restore archive is invalid: " + o.Id);
            }
        }
        foreach (ClientOption o in m.ClientOptions)
            if (o.Requires.Any(r => !ids.Contains(r) || r.Equals(o.Id, StringComparison.OrdinalIgnoreCase)))
                throw InvalidManifest("Client option prerequisite is invalid: " + o.Id);
    }

    private async Task<bool> ExactBaselineAsync(UpdateManifest m, string rel, string full, CancellationToken ct)
    {
        BaselineFile? b = m.RequiredBaseline.FirstOrDefault(x => Norm(x.Path) == Norm(rel));
        return b != null && File.Exists(full) && new FileInfo(full).Length == b.Size &&
               (await Sha256Async(full, ct)).Equals(b.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A locked category's archive renamed to .disabled by another tool is renamed back when it is exactly the expected
    /// file (no download). Anything else is left for the normal missing-file handling.
    /// </summary>
    private async Task ReEnableLockedArchivesAsync(UpdateManifest m, ReleaseResult result, CancellationToken ct)
    {
        foreach (ClientOption o in m.ClientOptions.Where(o => o.Locked))
            foreach (string a in o.Archives)
            {
                if (StateOf(a) != ArchiveState.Disabled) continue;
                string full = Resolve(a);
                if (!await ExactBaselineAsync(m, a, full + DisabledSuffix, ct)) continue;
                File.Move(full + DisabledSuffix, full);
                result.Warnings.Add($"{a} was turned off but is required ({o.Name}); it was turned back on.");
            }
    }

    /// <summary>Install or remove each category's restore archive to match the category's state.</summary>
    private async Task<bool> ReconcileRestoreArchivesAsync(UpdateManifest m, HashSet<string> disabled, ReleaseResult result, CancellationToken ct)
    {
        bool changed = false;
        foreach (ClientOption o in m.ClientOptions.Where(o => o.Restore != null))
        {
            RestoreArchive r = o.Restore!;
            string dest = Resolve(r.Destination);
            bool off = !o.Locked && o.Archives.All(a => disabled.Contains(Norm(a)));
            bool ours = File.Exists(dest) && await MatchesAsync(dest, r.Sha256, r.Size, ct);
            if (off && !ours)
            {
                if (File.Exists(dest))
                {
                    result.Warnings.Add($"{r.Destination} exists and is not the launcher's restore archive for {o.Name}; it was not changed.");
                    continue;
                }
                await InstallRestoreArchiveAsync(r, dest, ct);
                changed = true;
            }
            else if (!off && ours)
            {
                EnsureNotLocked(dest);
                File.Delete(dest);
                changed = true;
            }
        }
        return changed;
    }

    private async Task InstallRestoreArchiveAsync(RestoreArchive r, string dest, CancellationToken ct)
    {
        string payload = await DownloadAndVerifyAsync(new UpdateFile
        {
            Name = Path.GetFileName(r.Destination), SourceFile = r.SourceFile, Sha256 = r.Sha256, Size = r.Size, Type = "file"
        }, ct);
        string staging = dest + ".coops-staging";
        File.Copy(payload, staging, true);
        await VerifyFileAsync(staging, r.Sha256, r.Size, "staged " + r.Destination, ct);
        File.Move(staging, dest);
        await VerifyFileAsync(dest, r.Sha256, r.Size, "installed " + r.Destination, ct);
    }

    /// <summary>
    /// Turn a category on or off. Off: its archives are renamed to .disabled (nothing is deleted or downloaded, except
    /// a small restore archive when the category has one) and categories that require it are turned off too.
    /// On: exact .disabled archives are renamed back; anything missing is downloaded by the next Check / Install Patch.
    /// Returns true when an archive still has to be downloaded.
    /// </summary>
    public async Task<bool> SetClientOptionAsync(UpdateManifest m, string id, bool on, CancellationToken ct = default)
    {
        ValidateManifest(m);
        await RecoverInterruptedInstallAsync(ct);
        ClientOption o = m.ClientOptions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ReleaseUpdateException("OPTION_UNKNOWN", "Unknown client option: " + id, "Nothing was changed.");
        if (o.Locked)
            throw new ReleaseUpdateException("OPTION_LOCKED", $"{o.Name} is required by this release and cannot be turned off.",
                (o.LockReason ?? "") + " Nothing was changed.");

        ClientOptionsFile saved = LoadClientOptions();
        var off = new HashSet<string>(saved.Off, StringComparer.OrdinalIgnoreCase);

        if (!on)
        {
            var targets = new List<ClientOption> { o };
            // Everything that (directly or indirectly) requires this category goes off with it.
            for (int i = 0; i < targets.Count; i++)
                foreach (ClientOption d in m.ClientOptions.Where(x => !x.Locked && x.Requires.Contains(targets[i].Id, StringComparer.OrdinalIgnoreCase)))
                    if (!targets.Contains(d)) targets.Add(d);

            // Fetch any restore archive first, so a failed download leaves everything as it was.
            foreach (ClientOption t in targets.Where(t => t.Restore != null))
                await DownloadAndVerifyAsync(new UpdateFile { Name = Path.GetFileName(t.Restore!.Destination), SourceFile = t.Restore.SourceFile,
                    Sha256 = t.Restore.Sha256, Size = t.Restore.Size, Type = "file" }, ct);
            var actives = targets.SelectMany(t => t.Archives).Where(a => StateOf(a) == ArchiveState.Active).Select(Resolve).ToList();
            foreach (string full in actives) EnsureNotLocked(full);
            string park = Path.Combine(BackupsDir, "replaced-disabled-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"));
            foreach (string full in actives)
            {
                string dis = full + DisabledSuffix;
                if (File.Exists(dis))
                {
                    // An older .disabled copy would block the rename; keep it rather than delete it.
                    string kept = Path.Combine(park, Rel(dis).Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                    File.Move(dis, kept);
                }
                File.Move(full, dis);
            }
            foreach (ClientOption t in targets) off.Add(t.Id);
            SaveJsonAtomic(ClientOptionsPath, new ClientOptionsFile { Off = off.OrderBy(x => x).ToList() });
            var result = new ReleaseResult();
            await ReconcileRestoreArchivesAsync(m, DisabledArchives(m), result, ct);
            return false;
        }

        HashSet<string> disabledNow = DisabledArchives(m);
        foreach (string req in o.Requires)
        {
            ClientOption r = m.ClientOptions.First(x => x.Id.Equals(req, StringComparison.OrdinalIgnoreCase));
            if (!r.Locked && r.Archives.Any(a => disabledNow.Contains(Norm(a))))
                throw new ReleaseUpdateException("OPTION_REQUIRES", $"{o.Name} needs {r.Name}.", $"Turn on {r.Name} first. Nothing was changed.");
        }

        bool needsDownload = false;
        foreach (string a in o.Archives)
        {
            string full = Resolve(a);
            ArchiveState s = StateOf(a);
            if (s == ArchiveState.Disabled)
            {
                if (await ExactBaselineAsync(m, a, full + DisabledSuffix, ct))
                {
                    File.Move(full + DisabledSuffix, full);
                    continue;
                }
                // Not the expected file: keep it aside and let Check / Install Patch download the right one.
                string kept = Path.Combine(BackupsDir, "unexpected-disabled-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"),
                    Rel(full + DisabledSuffix).Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
                File.Move(full + DisabledSuffix, kept);
                s = ArchiveState.Missing;
            }
            if (s == ArchiveState.Missing &&
                !(m.RequiredBaseline.FirstOrDefault(b => Norm(b.Path) == Norm(a))?.Optional ?? false))
                needsDownload = true;
        }
        off.Remove(o.Id);
        SaveJsonAtomic(ClientOptionsPath, new ClientOptionsFile { Off = off.OrderBy(x => x).ToList() });
        await ReconcileRestoreArchivesAsync(m, DisabledArchives(m), new ReleaseResult(), ct);
        return needsDownload;
    }
}
