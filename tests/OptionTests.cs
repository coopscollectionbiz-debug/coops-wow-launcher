using CoopsWoWLauncher;
using System.Security.Cryptography;
using System.Text;

/// <summary>Client options (launcher 1.5.0): per-category opt-outs that Check / Install Patch honours.</summary>
static class OptionTests
{
    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    static void Check(bool c, string msg) { if (!c) throw new Exception(msg); }
    static string Full(string root, string rel) => Path.Combine(root, rel.Replace('/', '\\'));

    internal sealed class Src(Dictionary<string, byte[]> p) : IPayloadSource
    {
        public readonly List<string> Requests = new();
        public Task<PayloadResponse> OpenAsync(string f, long o, CancellationToken ct)
        {
            Requests.Add(f);
            return Task.FromResult(new PayloadResponse { Stream = new MemoryStream(p[f][(int)o..]), StartOffset = o });
        }
    }

    // Archives: weapons (patch-8), armor (patch-7), characters (locked, patch-a), dungeon maps (enUS m) and mini maps
    // (enUS n, requires maps). Every archive is a qualified baseline file the release can supply; core = patch-z.
    internal static (string Root, UpdateManifest M, ReleaseUpdater U, Src S, byte[] Restore) Fixture(string sandbox)
    {
        string root = Path.Combine(sandbox, "opt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Data", "enUS"));
        var payloads = new Dictionary<string, byte[]>();
        var m = new UpdateManifest { Schema = 2, ReleaseId = "opt-test", Client = new ClientProfile { Locale = "enUS" } };
        byte[] core = Bytes("core patch-z");
        payloads["core.mpq"] = core;
        m.Files.Add(new UpdateFile { Name = "patch-z.mpq", SourceFile = "core.mpq", Sha256 = Hash(core), Size = core.Length, Type = "file", Destination = "Data\\patch-z.mpq" });
        foreach (string a in new[] { "Data/patch-8.mpq", "Data/patch-7.mpq", "Data/patch-a.mpq", "Data/enus/patch-enus-m.mpq", "Data/enus/patch-enus-n.mpq" })
        {
            byte[] b = Bytes("archive " + a);
            File.WriteAllBytes(Full(root, a), b);
            string src = "src-" + Path.GetFileName(a);
            payloads[src] = b;
            m.RequiredBaseline.Add(new BaselineFile { Path = a, Sha256 = Hash(b), Size = b.Length });
            m.Files.Add(new UpdateFile { Name = Path.GetFileName(a), SourceFile = src, Sha256 = Hash(b), Size = b.Length, Type = "file", Destination = a, SuppliesBaseline = true });
        }
        byte[] restore = Bytes("stock weapon files");
        payloads["restore-weapons.mpq"] = restore;
        m.ClientOptions.Add(new ClientOption { Id = "hd-weapons", Name = "HD weapons", Archives = { "Data/patch-8.mpq" },
            Restore = new RestoreArchive { Destination = "Data/patch-9.mpq", SourceFile = "restore-weapons.mpq", Sha256 = Hash(restore), Size = restore.Length } });
        m.ClientOptions.Add(new ClientOption { Id = "hd-armor", Name = "HD armor", Archives = { "Data/patch-7.mpq" } });
        m.ClientOptions.Add(new ClientOption { Id = "hd-characters", Name = "HD characters", Archives = { "Data/patch-a.mpq" }, Locked = true, LockReason = "Goblin models." });
        m.ClientOptions.Add(new ClientOption { Id = "dungeon-maps", Name = "Dungeon maps", Archives = { "Data/enus/patch-enus-m.mpq" } });
        m.ClientOptions.Add(new ClientOption { Id = "dungeon-minimaps", Name = "Dungeon mini maps", Archives = { "Data/enus/patch-enus-n.mpq" }, Requires = { "dungeon-maps" } });
        var s = new Src(payloads);
        var u = new ReleaseUpdater(root, s, new Version(1, 5, 0));
        return (root, m, u, s, restore);
    }

    static async Task Expect(string code, Func<Task> a)
    {
        try { await a(); } catch (ReleaseUpdateException e) when (e.Code == code) { return; }
        throw new Exception("Expected " + code);
    }

    internal static async Task RunAll(string sandbox, Func<string, Func<Task>, Task> run)
    {
        await run("client options: turning HD weapons off renames to .disabled, adds the restore archive, never re-downloads", async () =>
        {
            var (r, m, u, s, restore) = Fixture(sandbox);
            await u.ApplyAsync(m);
            Check(!await u.SetClientOptionAsync(m, "hd-weapons", false), "Off reported a download");
            Check(!File.Exists(Full(r, "Data/patch-8.mpq")) && File.Exists(Full(r, "Data/patch-8.mpq.disabled")), "Not renamed");
            Check(File.ReadAllBytes(Full(r, "Data/patch-9.mpq")).SequenceEqual(restore), "Restore archive missing");
            s.Requests.Clear();
            var res = await u.ApplyAsync(m);
            Check(!res.Changed && s.Requests.Count == 0, "Apply touched or downloaded something");
            Check(!File.Exists(Full(r, "Data/patch-8.mpq")), "patch-8 came back");
            Check((await u.VerifyBaselineAsync(m)).Ok, "Baseline not ok with option off");
            Check(u.ScanArchives(m).Warnings.Count == 0, "Restore archive reported as unknown");
            Check(u.GetClientOptions(m).Single(o => o.Option.Id == "hd-weapons").Mode == ClientOptionMode.Off, "State not Off");
        });
        await run("client options: turning HD weapons on renames back and removes the restore archive", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            await u.SetClientOptionAsync(m, "hd-weapons", false);
            Check(!await u.SetClientOptionAsync(m, "hd-weapons", true), "On needed a download");
            Check(File.Exists(Full(r, "Data/patch-8.mpq")) && !File.Exists(Full(r, "Data/patch-8.mpq.disabled")), "Not restored");
            Check(!File.Exists(Full(r, "Data/patch-9.mpq")), "Restore archive left behind");
            s.Requests.Clear();
            Check(!(await u.ApplyAsync(m)).Changed && s.Requests.Count == 0, "Apply changed something after re-enable");
        });
        await run("client options: a PatchMenu rename is honoured and the restore archive is added by Check / Install", async () =>
        {
            var (r, m, u, s, restore) = Fixture(sandbox);
            await u.ApplyAsync(m);
            File.Move(Full(r, "Data/patch-8.mpq"), Full(r, "Data/patch-8.mpq.disabled"));
            File.Move(Full(r, "Data/patch-7.mpq"), Full(r, "Data/patch-7.mpq.disabled"));
            s.Requests.Clear();
            var res = await u.ApplyAsync(m);
            Check(res.Changed && s.Requests.SequenceEqual(new[] { "restore-weapons.mpq" }), "Expected only the restore download");
            Check(!File.Exists(Full(r, "Data/patch-8.mpq")) && !File.Exists(Full(r, "Data/patch-7.mpq")), "Re-enabled a PatchMenu choice");
            Check(File.ReadAllBytes(Full(r, "Data/patch-9.mpq")).SequenceEqual(restore), "Restore not installed");
        });
        await run("client options: a deleted archive of an off category is not downloaded until turned back on", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            await u.SetClientOptionAsync(m, "hd-armor", false);
            File.Delete(Full(r, "Data/patch-7.mpq.disabled"));
            s.Requests.Clear();
            Check(!(await u.ApplyAsync(m)).Changed && s.Requests.Count == 0, "Downloaded a turned-off archive");
            Check(await u.SetClientOptionAsync(m, "hd-armor", true), "On did not report the needed download");
            Check(u.GetClientOptions(m).Single(o => o.Option.Id == "hd-armor").NeedsDownload, "State does not show download");
            var res = await u.ApplyAsync(m);
            Check(res.Installed.Contains("data/patch-7.mpq") && File.Exists(Full(r, "Data/patch-7.mpq")), "Not downloaded after on");
        });
        await run("client options: a missing archive of an on category is still supplied", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            File.Delete(Full(r, "Data/patch-7.mpq"));
            var res = await u.ApplyAsync(m);
            Check(res.Installed.Contains("data/patch-7.mpq"), "Missing on-archive not supplied");
        });
        await run("client options: locked categories cannot be turned off and a PatchMenu rename is undone without download", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            await Expect("OPTION_LOCKED", () => u.SetClientOptionAsync(m, "hd-characters", false));
            File.Move(Full(r, "Data/patch-a.mpq"), Full(r, "Data/patch-a.mpq.disabled"));
            s.Requests.Clear();
            var res = await u.ApplyAsync(m);
            Check(File.Exists(Full(r, "Data/patch-a.mpq")) && s.Requests.Count == 0, "Locked archive not renamed back");
            Check(res.Warnings.Any(w => w.Contains("required")), "No warning for locked re-enable");
        });
        await run("client options: prerequisites turn dependents off and block turning them on alone", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            await u.SetClientOptionAsync(m, "dungeon-maps", false);
            Check(File.Exists(Full(r, "Data/enus/patch-enus-n.mpq.disabled")), "Dependent stayed on");
            await Expect("OPTION_REQUIRES", () => u.SetClientOptionAsync(m, "dungeon-minimaps", true));
            await u.SetClientOptionAsync(m, "dungeon-maps", true);
            Check(File.Exists(Full(r, "Data/enus/patch-enus-m.mpq")) && !File.Exists(Full(r, "Data/enus/patch-enus-n.mpq")), "Maps on should not turn minimaps on");
            await u.SetClientOptionAsync(m, "dungeon-minimaps", true);
            Check(File.Exists(Full(r, "Data/enus/patch-enus-n.mpq")), "Minimaps not on");
        });
        await run("client options: a modified .disabled archive is kept aside and the right one downloaded", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            await u.SetClientOptionAsync(m, "hd-armor", false);
            File.WriteAllBytes(Full(r, "Data/patch-7.mpq.disabled"), Bytes("tampered"));
            Check(await u.SetClientOptionAsync(m, "hd-armor", true), "Tampered file accepted");
            Check(!File.Exists(Full(r, "Data/patch-7.mpq.disabled")) &&
                  Directory.GetFiles(Path.Combine(u.StateDir, "backups"), "patch-7.mpq.disabled", SearchOption.AllDirectories).Length == 1, "Tampered file not kept aside");
            await u.ApplyAsync(m);
            Check((await u.VerifyBaselineAsync(m)).Ok, "Baseline not restored");
        });
        await run("client options: a foreign file at the restore path is never overwritten", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            await u.ApplyAsync(m);
            File.WriteAllBytes(Full(r, "Data/patch-9.mpq"), Bytes("players own patch"));
            await u.SetClientOptionAsync(m, "hd-weapons", false);
            Check(File.ReadAllText(Full(r, "Data/patch-9.mpq")) == "players own patch", "Foreign patch-9 overwritten");
            var res = await u.ApplyAsync(m);
            Check(res.Warnings.Any(w => w.Contains("patch-9")), "No warning about foreign restore path");
            await u.SetClientOptionAsync(m, "hd-weapons", true);
            Check(File.Exists(Full(r, "Data/patch-9.mpq")), "Foreign patch-9 deleted");
        });
        await run("client options: invalid option definitions reject the manifest", async () =>
        {
            foreach (Action<UpdateManifest> bad in new Action<UpdateManifest>[]
            {
                m => m.ClientOptions[0].Archives.Add("Data/patch-z.mpq"),
                m => m.ClientOptions[1].Archives[0] = "Data/patch-q.mpq",
                m => m.ClientOptions[0].Restore!.Destination = "Data/patch-8.mpq",
                m => m.ClientOptions[0].Restore!.Destination = "Data/patch-~.mpq",
                m => m.ClientOptions[0].Restore!.Sha256 = "xyz",
                m => m.ClientOptions[4].Requires.Add("nope"),
                m => m.ClientOptions[1].Id = "hd-weapons",
                m => m.ClientOptions[1].Archives.Add("Data/patch-8.mpq"),
            })
            {
                var (r, m, u, s, _) = Fixture(sandbox);
                bad(m);
                await Expect("MANIFEST_INVALID", () => u.ApplyAsync(m));
            }
        });
        await run("client options: a manifest without options behaves exactly as before", async () =>
        {
            var (r, m, u, s, _) = Fixture(sandbox);
            m.ClientOptions.Clear();
            File.Move(Full(r, "Data/patch-8.mpq"), Full(r, "Data/patch-8.mpq.disabled"));
            var res = await u.ApplyAsync(m);
            Check(res.Installed.Contains("data/patch-8.mpq"), "Without options a disabled archive must be supplied as before");
        });
    }
}
