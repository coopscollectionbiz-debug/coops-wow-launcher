// End-to-end check of the optional Lumenveil package through the real launcher EXE.
// Builds a staging launcher with an ephemeral signing key, serves a signed loopback feed, and drives the
// same handlers as the WinForms buttons (--staging-smoke) against a disposable WoW client copy.
//
// dotnet run --project tests/graphics-package -- --output <new dir> --client <disposable client>
//     --package <dir with Lumenveil-<v>.zip and package-manifest.json> [--source C:\WoWClient-Worgen-Test]
//     [--skip-self-update] [--no-plant] [--launch]
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var opts = ParseArgs(args);
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
string project = Path.Combine(repo, "CoopsWoWLauncher.csproj");
string root = Path.GetFullPath(opts["output"]);
if (Directory.Exists(root)) throw new Exception("Output must be a new directory: " + root);
string client = Path.GetFullPath(opts["client"]);
string packageDir = Path.GetFullPath(opts["package"]);
string source = Path.GetFullPath(opts.GetValueOrDefault("source", @"C:\WoWClient-Worgen-Test"));
string original = @"C:\WoWClient-Worgen-Test";
Assert(!client.Equals(original, StringComparison.OrdinalIgnoreCase) &&
    !client.StartsWith(original + "\\", StringComparison.OrdinalIgnoreCase), "Choose a separate disposable client");
Assert((File.GetAttributes(client) & FileAttributes.ReparsePoint) == 0, "Disposable client may not be a link");
Assert(!ClientWowRunning(client), "Close WoW in the disposable client first");

var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(packageDir, "package-manifest.json")))!;
string version = (string)manifest["version"]!;
string zipName = (string)manifest["package"]!["filename"]!;
var files = manifest["files"]!.AsArray().Select(f => new PackagedFile(
    (string)f!["path"]!, (string)f["sourceSha256"]!, (string)f["packagedSha256"]!, f["adjustment"] is not null,
    (string?)f["from"] ?? Path.Combine(source, (string)f["path"]!))).ToList();

Directory.CreateDirectory(root);
string feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string key = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
var report = new List<string>();
void Pass(string what) { Console.WriteLine("PASS " + what); report.Add("PASS " + what); }

// 1. Packaged bytes match the manifest and the live Worgen source.
string zipPath = Path.Combine(packageDir, zipName);
Assert(Sha(zipPath) == ((string)manifest["package"]!["sha256"]!).ToUpperInvariant(), "Package hash differs from manifest");
using (var archive = ZipFile.OpenRead(zipPath))
{
    Assert(archive.Entries.Count == files.Count, "ZIP entry count differs from manifest");
    foreach (var f in files)
    {
        var entry = archive.GetEntry(f.Path) ?? throw new Exception("ZIP lacks " + f.Path);
        using var s = entry.Open();
        Assert(Hex(SHA256.HashData(s)) == f.Packaged.ToUpperInvariant(), "ZIP bytes differ for " + f.Path);
        string live = Sha(f.From);
        Assert(live == f.Source.ToUpperInvariant(), "Worgen source changed since packaging: " + f.Path);
        Assert(f.Adjusted || f.Source.Equals(f.Packaged, StringComparison.OrdinalIgnoreCase), "Unexplained difference for " + f.Path);
    }
}
Pass($"{files.Count} packaged files match the manifest and the live Worgen source ({files.Count(f => f.Adjusted)} documented adjustment)");

// 2. Player files that the package replaces: plant distinct ones where the client has none.
var planted = new List<string>();
foreach (string name in opts.ContainsKey("no-plant") ? Array.Empty<string>() : new[] { "d3d9.dll", "dxvk.conf" })
{
    string p = Path.Combine(client, name);
    if (!File.Exists(p)) { File.WriteAllText(p, "player-owned " + name + " " + Guid.NewGuid()); planted.Add(name); }
}
var before = Snapshot(client);
string patchZ = Path.Combine(client, "Data", "patch-z.mpq");
string wow = Path.Combine(client, "Wow.exe");
string patchZHash = File.Exists(patchZ) ? Sha(patchZ) : "";
string wowHash = Sha(wow);
var preexisting = files.Where(f => File.Exists(Path.Combine(client, f.Path)))
    .ToDictionary(f => f.Path, f => Sha(Path.Combine(client, f.Path)));

// 3. Loopback feed and staging bootstrap.
string portFile = Path.Combine(root, "port.txt");
using var server = Process.Start(new ProcessStartInfo("python",
    $"-I \"{Path.Combine(repo, "tests", "staging", "feed_server.py")}\" \"{feed}\" \"{portFile}\"")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
try
{
    WaitUntil(() => File.Exists(portFile) && File.ReadAllText(portFile).Length > 0, "loopback feed");
    string baseUrl = "http://127.0.0.1:" + File.ReadAllText(portFile).Trim();
    string bootstrap = Build("1.3.0", Path.Combine(root, "bootstrap"));

    File.Copy(zipPath, Path.Combine(feed, zipName));
    WriteFeed("lumenveil", version, Path.Combine(feed, zipName), new JsonObject
    {
        ["installationEnabled"] = true,
        ["compatibleWowSha256"] = new JsonArray(wowHash.ToLowerInvariant()),
        ["supportedBuilds"] = new JsonArray("Disposable test fingerprint"),
        ["prerequisites"] = new JsonArray("Vulkan graphics driver", "Start WoW with this launcher's Launch button for ReShade"),
        ["files"] = new JsonArray(files.Select(f => (JsonNode)f.Path).ToArray()),
    });

    // Download only: nothing is written into WoW.
    var dl = NewRun("download only");
    Smoke(dl, "graphics");
    CheckProgress(dl.Log, "Downloading", "Verifying", "Download verified");
    CompareSnapshots(before, Snapshot(client), "download-only run");
    Pass("opt-in download verified the package with live progress and changed nothing in the client");

    for (int round = 1; round <= 2; round++)
    {
        var install = NewRun($"install {round}");
        Smoke(install, "graphics-install");
        CheckProgress(install.Log, "Downloading", "Verifying", "Download verified", "Extracting", "Installing", $"Lumenveil {version} installed");
        VerifyInstalled();
        Pass($"round {round}: installed {files.Count} files; each matches the package and the Worgen source");

        var detect = NewRun($"detect {round}");
        Smoke(detect, "graphics");
        Assert(File.ReadLines(detect.Log).First().Contains($"Lumenveil {version} is installed"), "Launcher did not report the installed package");
        Pass($"round {round}: launcher reports Lumenveil {version} as installed");

        var remove = NewRun($"remove {round}");
        Smoke(remove, "graphics-remove");
        Assert(File.ReadAllText(remove.Log).Contains("Lumenveil removed."), "Remove did not report success");
        CompareSnapshots(before, Snapshot(client), $"removal round {round}");
        Pass($"round {round}: removal restored the exact pre-installation file tree (planted player files restored, created folders and launcher state gone)");
    }

    // Interrupted installation: originals moved aside and some package files placed, receipt never written.
    {
        string state = Directory.CreateDirectory(Path.Combine(client, ".coops-lumenveil")).FullName;
        string id = Guid.NewGuid().ToString("N");
        var placed = new[] { "d3d9.dll", "CoAVolFog.dll" };
        var replaced = new JsonArray();
        foreach (string name in placed.Where(n => File.Exists(Path.Combine(client, n))))
        {
            replaced.Add(new JsonObject { ["path"] = name, ["sha256"] = Sha(Path.Combine(client, name)).ToLowerInvariant() });
            Directory.CreateDirectory(Path.Combine(state, "backup"));
            File.Move(Path.Combine(client, name), Path.Combine(state, "backup", name));
        }
        var placedFiles = new JsonArray();
        using (var archive = ZipFile.OpenRead(zipPath))
            foreach (string name in placed)
            {
                archive.GetEntry(name)!.ExtractToFile(Path.Combine(client, name));
                placedFiles.Add(new JsonObject { ["path"] = name, ["sha256"] = Sha(Path.Combine(client, name)).ToLowerInvariant() });
            }
        // One file still waits in staging; its destination is untouched.
        Directory.CreateDirectory(Path.Combine(state, id));
        using (var archive = ZipFile.OpenRead(zipPath))
            archive.GetEntry("dxvk.conf")!.ExtractToFile(Path.Combine(state, id, "dxvk.conf"));
        placedFiles.Add(new JsonObject { ["path"] = "dxvk.conf", ["sha256"] = Sha(Path.Combine(state, id, "dxvk.conf")).ToLowerInvariant() });
        File.WriteAllText(Path.Combine(state, "pending.json"), new JsonObject
        {
            ["id"] = id, ["removing"] = false, ["files"] = placedFiles, ["replaced"] = replaced, ["directories"] = new JsonArray()
        }.ToJsonString());
        var recover = NewRun("interrupted install");
        Smoke(recover, "graphics");
        Assert(!File.Exists(Path.Combine(state, "pending.json")), "Interrupted install was not recovered");
        CompareSnapshots(before, Snapshot(client), "interrupted-install recovery", ignoreState: true);
        Smoke(NewRun("interrupted install cleanup"), "graphics-remove");
        CompareSnapshots(before, Snapshot(client), "cleanup after interrupted install");
        Pass("interrupted installation rolled back on next launch: package files gone, player originals restored");
    }

    // Interrupted removal: package file moved to staging and the player original already restored.
    {
        Smoke(NewRun("install before interrupted removal"), "graphics-install");
        VerifyInstalled();
        var installed = Snapshot(client);
        string state = Path.Combine(client, ".coops-lumenveil");
        string id = Guid.NewGuid().ToString("N");
        var receipt = JsonNode.Parse(File.ReadAllText(Path.Combine(state, "installed.json")))!;
        // With a player original for d3d9.dll it is already restored; on a clean client only the package file moved.
        var origin = (receipt["replaced"]?.AsArray() ?? []).FirstOrDefault(r => (string)r!["path"]! == "d3d9.dll");
        var pkg = receipt["files"]!.AsArray().First(r => (string)r!["path"]! == "d3d9.dll")!;
        var pkg2 = receipt["files"]!.AsArray().First(r => (string)r!["path"]! == "ReShade/ReShade32.dll")!;
        Directory.CreateDirectory(Path.Combine(state, id, "ReShade"));
        File.Move(Path.Combine(client, "d3d9.dll"), Path.Combine(state, id, "d3d9.dll"));
        File.Move(Path.Combine(client, "ReShade", "ReShade32.dll"), Path.Combine(state, id, "ReShade", "ReShade32.dll"));
        if (origin is not null)
            File.Move(Path.Combine(state, "backup", "d3d9.dll"), Path.Combine(client, "d3d9.dll"));
        File.WriteAllText(Path.Combine(state, "pending.json"), new JsonObject
        {
            ["id"] = id, ["removing"] = true, ["files"] = new JsonArray(pkg.DeepClone(), pkg2.DeepClone()),
            ["replaced"] = origin is null ? new JsonArray() : new JsonArray(origin.DeepClone())
        }.ToJsonString());
        Smoke(NewRun("interrupted removal"), "graphics");
        Assert(!File.Exists(Path.Combine(state, "pending.json")), "Interrupted removal was not recovered");
        CompareSnapshots(installed, Snapshot(client), "interrupted-removal recovery", ignoreState: true);
        VerifyInstalled();
        Smoke(NewRun("remove after interrupted removal"), "graphics-remove");
        CompareSnapshots(before, Snapshot(client), "removal after interrupted removal");
        Pass("interrupted removal rolled back to the installed state on next launch; a later removal completed cleanly");
    }

    if (opts.ContainsKey("launch"))
        LaunchCheck();

    Assert(Sha(wow) == wowHash, "Wow.exe changed");
    Assert(!File.Exists(patchZ) || Sha(patchZ) == patchZHash, "Data/patch-z.mpq changed");
    Pass($"Wow.exe and Data/patch-z.mpq unchanged ({(patchZHash.Length > 0 ? patchZHash[..16] : "no patch-z")}…)");

    if (!opts.ContainsKey("skip-self-update"))
    {
        string good = Path.Combine(feed, "CoopLauncher-1.3.1.exe");
        File.Copy(Build("1.3.1", Path.Combine(root, "good")), good);
        WriteFeed("launcher", "1.3.1", good, []);
        var update = NewRun("self update");
        Smoke(update, "update");
        WaitUntil(() => File.Exists(Path.Combine(update.State, "staging-health.log")) &&
            !File.Exists(Path.Combine(update.State, "pending.json")) && Running(update.Exe), "healthy candidate restart");
        Assert(Sha(update.Exe) == Sha(good), "Candidate did not replace the bootstrap");
        Assert(File.ReadAllText(Path.Combine(update.State, "staging-health.log")).Trim() == "1.3.1", "Unexpected checkpoint version");
        Assert(File.ReadAllText(update.Settings) == update.SettingsText, "Launcher settings changed");
        Stop(update.Exe);
        Pass("signed self-update replaced the launcher, restarted at the same path and kept launcher_path.txt");

        string broken = Path.Combine(feed, "CoopLauncher-1.3.2.exe");
        File.Copy(Build("1.3.2", Path.Combine(root, "broken"), failStartup: true), broken);
        WriteFeed("launcher", "1.3.2", broken, []);
        var rollback = NewRun("self update rollback");
        Smoke(rollback, "update");
        WaitUntil(() => File.Exists(Path.Combine(rollback.State, "staging-recovery.log")) &&
            !File.Exists(Path.Combine(rollback.State, "pending.json")) && Running(rollback.Exe), "rollback restart");
        Assert(Sha(rollback.Exe) == Sha(bootstrap), "Bootstrap was not restored after failed startup");
        Stop(rollback.Exe);
        Pass("failed-startup candidate rolled back to the previous launcher");
    }
}
finally
{
    try { if (!server.HasExited) server.Kill(true); } catch { }
    foreach (string p in planted)
    {
        string path = Path.Combine(client, p);
        if (File.Exists(path) && File.ReadAllText(path).StartsWith("player-owned ")) File.Delete(path);
    }
    File.WriteAllLines(Path.Combine(root, "graphics-package-smoke.txt"), report);
}
Console.WriteLine("ALL PASS");

// Installs through the launcher, starts WoW through the launcher's Launch path, and checks what actually loaded.
void LaunchCheck()
{
    Smoke(NewRun("install before launch"), "graphics-install");
    VerifyInstalled();
    var preLaunch = Snapshot(client);
    var launch = NewRun("launch");
    var started = DateTime.Now.AddSeconds(-2);
    Smoke(launch, "launch");
    string line = File.ReadAllText(launch.Log);
    var m = Regex.Match(line, @"pid (\d+) with ReShade layer (\w+)");
    Assert(m.Success && m.Groups[2].Value == "enabled", "Launcher did not start WoW with the ReShade layer: " + line);
    int pid = int.Parse(m.Groups[1].Value);
    var evidence = new List<string>();
    try
    {
        string reshadeLog = Path.Combine(client, "ReShade.log");
        string fogLog = Path.Combine(client, "CoAVolFog.log");
        string dxvkLog = Path.Combine(client, "wow_d3d9.log");
        string localReshade = Path.Combine(client, "ReShade", "ReShade32.dll");
        WaitUntil(() => Fresh(reshadeLog, started) && ReadShared(reshadeLog).Contains("vkCreateSwapchainKHR"), "ReShade swapchain in ReShade.log");
        WaitUntil(() => Fresh(fogLog, started) && ReadShared(fogLog).Contains("CoAVolFog loaded from"), "CoAVolFog.log start line");
        WaitUntil(() => Fresh(dxvkLog, started) && ReadShared(dxvkLog).Contains("D3D9DeviceEx"), "DXVK device in wow_d3d9.log");
        Thread.Sleep(8000); // let effects compile and a few frames present
        Assert(Process.GetProcessesByName("Wow").Any(p => p.Id == pid), "WoW exited during the launch check");
        string rs = ReadShared(reshadeLog);
        string first = rs.Split('\n').First(l => l.Contains("Initializing crosire's ReShade"));
        Assert(first.Contains($"loaded from '{localReshade}'"), "ReShade did not load from the client folder: " + first);
        Assert(!rs.Contains(@"C:\ProgramData\ReShade"), "A system-wide ReShade was also injected");
        Assert(Regex.Matches(rs, "Initializing crosire's ReShade").Count == 1, "ReShade initialised more than once");
        string shaders = Path.Combine(client, "reshade-shaders", "Shaders");
        foreach (string fx in new[] { "MartysMods_LAUNCHPAD.fx", "MartysMods_SMAA.fx", "FakeHDR.fx", "Curves.fx", "MartysMods_SHARPEN.fx" })
            Assert(rs.Split('\n').Any(l => l.Contains("Successfully compiled '" + shaders) && l.Contains(fx + "'")),
                "Preset effect not compiled: " + fx);
        var fog = ReadShared(fogLog).Split('\n').Where(l => l.Contains("loaded from") || l.Contains("lamp data") || l.Contains("liquid data"))
            .Select(l => l.Trim()).ToList();
        Assert(fog.Any(l => l.Contains("CoAVolFog loaded from " + client)), "CoAVolFog did not load from the client");
        Assert(fog.Any(l => l.Contains("lamp data")) && fog.Any(l => l.Contains("liquid data")), "Lumenveil data files were not loaded");
        var modules = Modules(pid);
        foreach (string expect in new[] { @"\version.dll", @"\CoAVolFog.dll", @"\d3d9.dll", @"\ReShade\ReShade32.dll" })
            Assert(modules.Any(x => x.EndsWith(expect, StringComparison.OrdinalIgnoreCase) && x.StartsWith(client, StringComparison.OrdinalIgnoreCase)),
                "Module not loaded from the client: " + expect + "\n" + string.Join("\n", modules));
        Assert(!modules.Any(x => x.Contains(@"\ProgramData\ReShade\", StringComparison.OrdinalIgnoreCase)), "System ReShade module loaded");
        evidence.Add(first.Trim());
        evidence.AddRange(fog.Take(4));
        evidence.Add("DXVK: " + ReadShared(dxvkLog).Split('\n').First().Trim());
        evidence.AddRange(modules.Where(x => x.StartsWith(client, StringComparison.OrdinalIgnoreCase)).Select(x => "module " + x));
        evidence.Add($"ReShade compiled {Regex.Matches(rs, "Successfully compiled").Count} effects; ERROR lines: {Regex.Matches(rs, @"\| ERROR \|").Count}");
    }
    finally
    {
        try { using var wowProcess = Process.GetProcessById(pid); wowProcess.Kill(); wowProcess.WaitForExit(15000); } catch { }
        WaitUntil(() => !ClientWowRunning(client), "the test WoW to exit");
        File.WriteAllLines(Path.Combine(root, "launch-evidence.txt"), evidence);
    }
    foreach (string e in evidence) Console.WriteLine("  " + e);
    Pass("launcher started WoW: version.dll, CoAVolFog.dll, DXVK d3d9.dll and client-local ReShade32.dll loaded; preset effects compiled; no system ReShade");

    // What the game itself writes while running belongs to the player (logs, cache, config). Everything else must match.
    var afterRun = Snapshot(client);
    var runtime = new Regex(@"^(Logs|Cache|WTF|Errors|Screenshots)\\|^(ReShade\.log\d*|CoAVolFog\.log|wow_d3d9\.log|LuxVelum\.log|Lexara\.log)$", RegexOptions.IgnoreCase);
    var changed = preLaunch.Keys.Union(afterRun.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(k => preLaunch.GetValueOrDefault(k) != afterRun.GetValueOrDefault(k)).OrderBy(k => k).ToList();
    File.WriteAllLines(Path.Combine(root, "launch-runtime-changes.txt"), changed);
    var unexpected = changed.Where(k => !runtime.IsMatch(k)).ToList();
    Assert(unexpected.Count == 0, "Game launch changed managed or unrelated files:\n" + string.Join("\n", unexpected));

    Smoke(NewRun("remove after launch"), "graphics-remove");
    var afterRemove = Snapshot(client);
    var remaining = before.Keys.Union(afterRemove.Keys, StringComparer.OrdinalIgnoreCase)
        .Where(k => before.GetValueOrDefault(k) != afterRemove.GetValueOrDefault(k) && !runtime.IsMatch(k)).ToList();
    Assert(remaining.Count == 0, "Removal after a game launch left differences:\n" + string.Join("\n", remaining));
    Pass($"after the game ran, removal restored every package path and player file ({changed.Count} game-written log/cache/config entries kept)");
}

static string ReadShared(string path)
{
    using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var r = new StreamReader(s);
    return r.ReadToEnd();
}

// Only WoW started from the disposable client counts; a game the user is playing elsewhere is left alone.
static bool ClientWowRunning(string dir) => Process.GetProcessesByName("Wow").Any(p =>
{
    try { return p.MainModule?.FileName.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) ?? true; }
    catch { return true; }
});

static bool Fresh(string path, DateTime since) => File.Exists(path) && File.GetLastWriteTime(path) >= since;

// Module paths of the 32-bit WoW process, read by 32-bit Windows PowerShell.
static List<string> Modules(int pid)
{
    var ps = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
    foreach (string a in new[] { "-NoProfile", "-Command", $"(Get-Process -Id {pid}).Modules | ForEach-Object FileName" }) ps.ArgumentList.Add(a);
    using var q = Process.Start(ps)!;
    var paths = q.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    q.WaitForExit();
    return paths;
}

void VerifyInstalled()
{
    foreach (var f in files)
        Assert(Sha(Path.Combine(client, f.Path)) == f.Packaged.ToUpperInvariant(), "Installed bytes differ for " + f.Path);
    string state = Path.Combine(client, ".coops-lumenveil", "backup");
    foreach (var (path, hash) in preexisting)
        Assert(File.Exists(Path.Combine(state, path)) && Sha(Path.Combine(state, path)) == hash,
            "Backup missing or wrong for " + path);
    Assert(Sha(wow) == wowHash, "Install changed Wow.exe");
    Assert(!File.Exists(patchZ) || Sha(patchZ) == patchZHash, "Install changed patch-z");
}

string Build(string v, string dest, bool failStartup = false)
{
    var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
    foreach (string a in new[] { "publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "-o", dest,
        "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-p:DebugType=None", $"-p:Version={v}",
        "-p:StagingFeeds=true", $"-p:FailStartup={failStartup.ToString().ToLowerInvariant()}", $"-p:LauncherFeedKey={key}",
        $"-p:LumenveilFeedKey={key}", $"-p:LauncherFeedUrl={BaseUrl()}/launcher.json",
        $"-p:LumenveilFeedUrl={BaseUrl()}/lumenveil.json", "-p:NuGetAudit=false" })
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
    Assert(p.ExitCode == 0, "Publish failed for " + v);
    var published = Directory.GetFiles(dest);
    Assert(published.Length == 1 && Path.GetFileName(published[0]) == "CoopsWoWLauncher.exe", "Publish did not produce one EXE");
    return published[0];
}

string BaseUrl() => "http://127.0.0.1:" + File.ReadAllText(portFile).Trim();

void WriteFeed(string channel, string v, string asset, JsonObject extra)
{
    string filename = Path.GetFileName(asset);
    var meta = new JsonObject
    {
        ["channel"] = channel, ["version"] = v, ["filename"] = filename, ["url"] = $"{BaseUrl()}/{filename}",
        ["size"] = new FileInfo(asset).Length, ["sha256"] = Sha(asset)
    };
    foreach (var kv in extra) meta[kv.Key] = kv.Value?.DeepClone();
    byte[] bytes = Encoding.UTF8.GetBytes(meta.ToJsonString());
    byte[] sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    File.WriteAllText(Path.Combine(feed, channel + ".json"),
        new JsonObject { ["payload"] = Convert.ToBase64String(bytes), ["signature"] = Convert.ToBase64String(sig) }.ToJsonString());
}

Run NewRun(string name)
{
    string folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    string exe = Path.Combine(folder, "Coop Launcher.exe");
    File.Copy(Path.Combine(root, "bootstrap", "CoopsWoWLauncher.exe"), exe);
    string settings = Path.Combine(folder, "launcher_path.txt");
    string text = client + Environment.NewLine;
    File.WriteAllText(settings, text);
    return new Run(folder, exe, settings, text, Path.Combine(folder, "staging-progress.log"),
        Path.Combine(folder, ".coops-launcher-update"));
}

void Smoke(Run run, string mode)
{
    using var p = Process.Start(new ProcessStartInfo(run.Exe) { UseShellExecute = false, CreateNoWindow = true,
        ArgumentList = { "--staging-smoke", mode } })!;
    Assert(p.WaitForExit(180_000), $"Launcher smoke did not exit ({mode})");
    Assert(p.ExitCode == 0, $"Launcher smoke returned {p.ExitCode} ({mode})");
    if (mode.StartsWith("graphics-") && File.Exists(run.Log))
    {
        string log = File.ReadAllText(run.Log);
        Assert(!log.Contains("change failed"), $"{mode} failed:\n{log}");
    }
}

static void CheckProgress(string log, params string[] phases)
{
    string text = File.ReadAllText(log);
    int last = -1;
    foreach (string phase in phases)
    {
        int i = text.IndexOf(phase, last + 1, StringComparison.Ordinal);
        Assert(i > last, $"Missing or out-of-order phase '{phase}' in {log}:\n{text}");
        last = i;
    }
    Assert(File.ReadLines(log).Any(l => Regex.IsMatch(l, @"/ .*\([0-9]+%\).* [1-9][0-9.,]* [KMG]?i?B/s")), "No live byte/rate sample in " + log);
}

static Dictionary<string, string> Snapshot(string dir)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (string d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories))
        result[Path.GetRelativePath(dir, d) + "\\"] = "dir";
    foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
    {
        var info = new FileInfo(f);
        result[Path.GetRelativePath(dir, f)] = info.Length < 64L * 1024 * 1024 ? Sha(f)
            : $"{info.Length}@{info.LastWriteTimeUtc.Ticks}";
    }
    return result;
}

static void CompareSnapshots(Dictionary<string, string> expected, Dictionary<string, string> actual, string what, bool ignoreState = false)
{
    bool Keep(string k) => !ignoreState || !k.StartsWith(".coops-lumenveil", StringComparison.OrdinalIgnoreCase);
    var diffs = expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase).Where(Keep)
        .Where(k => expected.GetValueOrDefault(k) != actual.GetValueOrDefault(k))
        .Select(k => $"  {k}: {expected.GetValueOrDefault(k) ?? "(absent)"} -> {actual.GetValueOrDefault(k) ?? "(absent)"}").ToList();
    Assert(diffs.Count == 0, $"Client tree differs after {what}:\n" + string.Join("\n", diffs.Take(40)));
}

static bool Running(string exe) => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))
    .Any(p => { try { return string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase); } catch { return false; } });

static void Stop(string exe)
{
    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
}

static void WaitUntil(Func<bool> predicate, string what)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < 65) { if (predicate()) return; Thread.Sleep(250); }
    throw new Exception("Timed out waiting for " + what);
}

static string Sha(string path) { using var s = File.OpenRead(path); return Hex(SHA256.HashData(s)); }
static string Hex(byte[] b) => Convert.ToHexString(b);
static void Assert(bool ok, string what) { if (!ok) throw new Exception(what); }

static Dictionary<string, string> ParseArgs(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < a.Length; i++)
    {
        if (!a[i].StartsWith("--")) throw new Exception("Bad argument " + a[i]);
        string k = a[i][2..];
        d[k] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
    }
    foreach (string req in new[] { "output", "client", "package" })
        if (!d.ContainsKey(req)) throw new Exception("Missing --" + req);
    return d;
}

record PackagedFile(string Path, string Source, string Packaged, bool Adjusted, string From);
record Run(string Folder, string Exe, string Settings, string SettingsText, string Log, string State);
