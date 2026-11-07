using CoopsWoWLauncher;
using System.Security.Cryptography;
using System.Text;

static class Tests
{
    static readonly string Sandbox = Path.Combine(Path.GetTempPath(), "CoopsCompatibility-" + Guid.NewGuid().ToString("N"));
    static int passed;
    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Run(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
    static UpdateManifest Manifest() => ReleaseUpdater.ParseManifest(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
    static string Full(string root, string path) => Path.Combine(root, path.Replace('\\','/'));
    static void Write(string root, string path, byte[] b) { string p=Full(root,path); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p,b); }
    static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
    static (string, UpdateManifest, ReleaseUpdater) Fixture()
    {
        string root = Path.Combine(Sandbox, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var m=Manifest(); m.Files.RemoveAll(f=>f.Type!="file"); m.ObsoleteFiles.Clear();
        var payloads=new Dictionary<string, byte[]>();
        foreach (var b in m.RequiredBaseline)
        {
            byte[] bytes=Bytes("fixture " + b.Path); b.Size=bytes.Length; b.Sha256=Hash(bytes); Write(root,b.Path,bytes);
            var supplied=m.Files.SingleOrDefault(f=>f.SuppliesBaseline && f.Destination.Replace('\\','/').Equals(b.Path,StringComparison.OrdinalIgnoreCase));
            if(supplied!=null) { supplied.Size=b.Size; supplied.Sha256=b.Sha256; supplied.Parts.Clear(); supplied.SourceFile="fixture-"+payloads.Count; payloads[supplied.SourceFile]=bytes; }
        }
        byte[] payload=Bytes("new verified patch"); var f=m.Files.Single(f=>!f.SuppliesBaseline); f.Size=payload.Length; f.Sha256=Hash(payload); payloads[f.SourceFile]=payload;
        return (root,m,new ReleaseUpdater(root,new MapSource(payloads),new Version(1,2,0)));
    }
    static async Task Expect(string code, Func<Task> action)
    {
        try { await action(); } catch (ReleaseUpdateException e) when (e.Code==code) { return; }
        throw new Exception("Expected " + code);
    }
    public static async Task Main(string[] args)
    {
        try
        {
            if(args.Length==2 && args[0]=="--assets")
            {
                await Run("real release payloads install and verify in isolated synthetic base client",async()=> {
                    string root=Path.Combine(Sandbox,"real-assets"); Directory.CreateDirectory(root);
                    var m=Manifest(); m.Files.RemoveAll(f=>f.Type!="file"); m.ObsoleteFiles.Clear();
                    var supplied=m.Files.Where(f=>f.SuppliesBaseline).Select(f=>f.Destination.Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach(var b in m.RequiredBaseline.Where(b=>!b.Optional && !supplied.Contains(b.Path))) {
                        byte[] bytes=Bytes("synthetic base "+b.Path); b.Size=bytes.Length; b.Sha256=Hash(bytes); Write(root,b.Path,bytes);
                    }
                    var source=new DirectorySource(Path.GetFullPath(args[1]));
                    var u=new ReleaseUpdater(root,source,new Version(1,2,0));
                    var result=await u.ApplyAsync(m); Check(result.Installed.Count==27,"Not all release patches installed");
                    u.ForceBaselineRehash=true; var baseline=await u.VerifyBaselineAsync(m);
                    Check(baseline.Ok && baseline.Verified==m.RequiredBaseline.Count(b=>!b.Optional),"Real HD baseline verification failed");
                    Check(Hash(File.ReadAllBytes(Full(root,m.Files[0].Destination)))==m.Files[0].Sha256,"Real Z mismatch");
                    int requests=source.Requests;
                    Check(!(await u.ApplyAsync(m)).Changed && source.Requests==requests,"Complete client requested download");
                    Console.WriteLine($"Verified {result.Installed.Count} installed archives; {requests} source requests; no requests on repeat.");
                });
                return;
            }
            await Run("progress across fresh, resumed, restarted, unknown-length and failed transfers",
                () => ProgressTests.TransferCases(Sandbox));
            await Run("rolling speed decays on a stalled read and preserves all download categories",
                ProgressTests.StallAndKinds);
            await Run("only evidence-qualified archives are optional",()=> {
                var m=Manifest(); var expected=new[]{"Data/enus/backup-enus.mpq","Data/enus/base-enus.mpq","Data/enus/patch-enus-s.mpq"};
                Check(m.RequiredBaseline.Where(b=>b.Optional).Select(b=>b.Path).Order().SequenceEqual(expected.Order()),"Unexpected optional policy");
                foreach(string path in new[]{"Wow.exe","Data/patch-a.mpq","Data/patch-s.mpq","Data/patch-7.mpq","Data/patch-8.mpq","Data/patch-b.mpq","Data/patch-i.mpq","Data/patch-j.mpq","Data/patch-t.mpq","Data/patch-u.mpq"})
                    Check(m.RequiredBaseline.Any(b=>b.Path.Equals(path,StringComparison.OrdinalIgnoreCase)&&!b.Optional),"Lost prerequisite: "+path);
                Check(!new BaselineFile().Optional,"Legacy entries must remain mandatory"); return Task.CompletedTask;
            });
            await Run("complete HD fixture verifies and installs",async()=> {
                var (r,m,u)=Fixture(); Check((await u.VerifyBaselineAsync(m)).Verified==m.RequiredBaseline.Count,"Not all files hashed");
                var result=await u.ApplyAsync(m); Check(result.Changed,"Patch not installed");
                Check(Hash(File.ReadAllBytes(Full(r,m.Files[0].Destination)))==m.Files[0].Sha256,"Wrong installed hash");
            });
            await Run("missing qualified optional MPQs install successfully",async()=> {
                var (r,m,u)=Fixture(); foreach(var b in m.RequiredBaseline.Where(b=>b.Optional)) File.Delete(Full(r,b.Path));
                Check((await u.VerifyBaselineAsync(m)).Ok,"Missing optional rejected"); Check((await u.ApplyAsync(m)).Changed,"Install rejected");
                Check(u.ScanArchives(m).Blocking.Count==0,"Optional locale archive treated as unknown");
            });
            await Run("stock baseline and executable cannot be provisioned",async()=> {
                var supplied=Manifest().Files.Where(f=>f.SuppliesBaseline).Select(f=>f.Destination.Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach(var path in Manifest().RequiredBaseline.Where(b=>!b.Optional && !supplied.Contains(b.Path)).Select(b=>b.Path)) {
                    var (r,m,u)=Fixture(); File.Delete(Full(r,path));
                    await Expect("UNSUPPORTED_BASELINE",()=>u.ApplyAsync(m)); Check(!File.Exists(Full(r,m.Files[0].Destination)),"Patch changed on rejection");
                }
            });
            await Run("present optional archives retain SHA-256 and size checks",async()=> {
                foreach(var path in Manifest().RequiredBaseline.Where(b=>b.Optional).Select(b=>b.Path)) {
                    var (r,m,u)=Fixture(); var b=m.RequiredBaseline.Single(b=>b.Path==path); byte[] corrupt=File.ReadAllBytes(Full(r,path)); corrupt[0]^=1; Write(r,path,corrupt);
                    await Expect("UNSUPPORTED_BASELINE",()=>u.ApplyAsync(m));
                    Write(r,path,new byte[b.Size+1]); Check(!(await u.VerifyBaselineAsync(m)).Ok,"Optional size mismatch accepted");
                }
            });
            await Run("incompatible same-size executable rejected",async()=> {
                var (r,m,u)=Fixture(); var b=m.RequiredBaseline.Single(b=>b.Path=="Wow.exe"); Write(r,b.Path,new byte[b.Size]);
                await Expect("UNSUPPORTED_BASELINE",()=>u.ApplyAsync(m));
            });
            await Run("post-Z and unknown locale overrides remain blocked",async()=> {
                foreach(var path in new[]{"Data/patch-~.mpq","Data/enUS/patch-enUS-r.mpq"}) {
                    var (r,m,u)=Fixture(); Write(r,path,Bytes("override")); await Expect("UNEXPECTED_ARCHIVE",()=>u.ApplyAsync(m));
                }
            });
            await Run("corrupt download never replaces existing patch",async()=> {
                var (r,m,u)=Fixture(); m.Files[0].Sha256=new string('0',64); Write(r,m.Files[0].Destination,Bytes("old"));
                await Expect("HASH_MISMATCH",()=>u.ApplyAsync(m)); Check(File.ReadAllText(Full(r,m.Files[0].Destination))=="old","Original changed");
            });
            await Run("transaction failure rolls back verified originals",async()=> {
                var (r,m,u)=Fixture(); Write(r,m.Files[0].Destination,Bytes("old"));
                u.FaultHook=p=> { if(p.StartsWith("after-replace:")) throw new IOException("test fault"); };
                await Expect("INSTALL_FAILED_ROLLED_BACK",()=>u.ApplyAsync(m));
                Check(File.ReadAllText(Full(r,m.Files[0].Destination))=="old","Rollback lost original");
            });
            await Run("interrupted transaction recovers on next startup",async()=> {
                var (r,m,u)=Fixture(); Write(r,m.Files[0].Destination,Bytes("old"));
                u.FaultHook=p=> { if(p.StartsWith("after-replace:")) throw new SimulatedProcessTerminationException(p); };
                try { await u.ApplyAsync(m); throw new Exception("Expected termination"); } catch(SimulatedProcessTerminationException) { }
                var recovery=await new ReleaseUpdater(r,new Source(Bytes("unused")),new Version(1,1,0)).RecoverInterruptedInstallAsync();
                Check(recovery?.RolledBack==true && recovery.Problems.Count==0,"Recovery failed");
                Check(File.ReadAllText(Full(r,m.Files[0].Destination))=="old","Recovery lost original");
            });
            await Run("manifest supplies all 26 required custom HD archives",()=> {
                var m=Manifest(); Check(m.MinimumLauncherVersion=="1.2.0","Old launchers not gated");
                Check(m.Files.Count(f=>f.SuppliesBaseline)==26,"Incomplete distribution");
                foreach(var b in m.RequiredBaseline.Where(b=>!b.Optional && System.Text.RegularExpressions.Regex.IsMatch(b.Path,@"^Data/(patch-[5-9a-z]|enus/patch-enus-[a-z])\.mpq$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
                    Check(m.Files.Any(f=>f.SuppliesBaseline && f.Destination==b.Path && f.Sha256==b.Sha256 && f.Size==b.Size),"Unprovided prerequisite "+b.Path);
                return Task.CompletedTask;
            });
            await Run("missing all managed prerequisites installs atomically then verifies",async()=> {
                var (r,m,u)=Fixture(); foreach(var f in m.Files.Where(f=>f.SuppliesBaseline)) File.Delete(Full(r,f.Destination));
                var result=await u.ApplyAsync(m); Check(result.Installed.Count==27,"Missing patches not installed");
                Check((await u.VerifyBaselineAsync(m)).Verified==m.RequiredBaseline.Count,"Installed baseline not exact");
                Check(!(await u.ApplyAsync(m)).Changed,"Matching client redownloaded patches");
            });
            await Run("each missing managed patch can be supplied individually",async()=> {
                foreach(string path in Manifest().Files.Where(f=>f.SuppliesBaseline).Select(f=>f.Destination)) {
                    var (r,m,u)=Fixture(); File.Delete(Full(r,path)); var result=await u.ApplyAsync(m);
                    Check(result.Installed.Count==2 && File.Exists(Full(r,path)),"Missing managed patch not installed");
                }
            });
            await Run("identical content installs to multiple destinations from one download",async()=> {
                var(r,m,u)=Fixture(); var pair=m.Files.Where(f=>f.SuppliesBaseline).Take(2).ToArray();
                pair[1].SourceFile=pair[0].SourceFile; pair[1].Sha256=pair[0].Sha256; pair[1].Size=pair[0].Size;
                var b=m.RequiredBaseline.Single(b=>b.Path==pair[1].Destination); b.Sha256=pair[0].Sha256; b.Size=pair[0].Size!.Value;
                foreach(var f in pair) File.Delete(Full(r,f.Destination));
                var result=await u.ApplyAsync(m); Check(result.Installed.Count==3,"Shared payload install incomplete");
                foreach(var f in pair) Check(Hash(File.ReadAllBytes(Full(r,f.Destination)))==f.Sha256,"Shared payload mismatch");
            });
            await Run("existing mismatched managed archive remains rejected",async()=> {
                foreach(string path in Manifest().Files.Where(f=>f.SuppliesBaseline).Select(f=>f.Destination)) {
                    var (r,m,u)=Fixture(); byte[] old=File.ReadAllBytes(Full(r,path)); old[0]^=1; Write(r,path,old);
                    await Expect("UNSUPPORTED_BASELINE",()=>u.ApplyAsync(m)); Check(File.ReadAllBytes(Full(r,path)).SequenceEqual(old),"Foreign archive overwritten");
                }
            });
            await Run("later replacement failure removes newly supplied prerequisites",async()=> {
                var (r,m,u)=Fixture(); var files=m.Files.Where(f=>f.SuppliesBaseline).ToList(); foreach(var f in files) File.Delete(Full(r,f.Destination));
                Write(r,m.Files[0].Destination,Bytes("old"));
                u.FaultHook=p=> {if(p=="after-replace:"+files.Last().Destination.Replace('\\','/').ToLowerInvariant()) throw new IOException("late failure");};
                await Expect("INSTALL_FAILED_ROLLED_BACK",()=>u.ApplyAsync(m));
                Check(files.All(f=>!File.Exists(Full(r,f.Destination))),"Partial HD install survived rollback");
                Check(File.ReadAllText(Full(r,m.Files[0].Destination))=="old","Z not restored");
            });
            await Run("crash recovery removes newly supplied prerequisites",async()=> {
                var (r,m,u)=Fixture(); var files=m.Files.Where(f=>f.SuppliesBaseline).ToList(); foreach(var f in files) File.Delete(Full(r,f.Destination));
                u.FaultHook=p=> {if(p=="after-replace:"+files.Last().Destination.Replace('\\','/').ToLowerInvariant()) throw new SimulatedProcessTerminationException(p);};
                try {await u.ApplyAsync(m); throw new Exception("Expected termination");} catch(SimulatedProcessTerminationException) { }
                var result=await new ReleaseUpdater(r,new Source(Bytes("unused")),new Version(1,2,0)).RecoverInterruptedInstallAsync();
                Check(result?.RolledBack==true && result.Problems.Count==0,"Crash recovery failed");
                Check(m.Files.All(f=>!File.Exists(Full(r,f.Destination))),"Crash recovery kept new patches");
            });
            await Run("baseline payload contract rejects unsafe or inconsistent manifest",async()=> {
                foreach(int kind in Enumerable.Range(0,5)) {
                    var (r,m,u)=Fixture(); var f=m.Files.First(f=>f.SuppliesBaseline);
                    if(kind==0) f.Sha256=new string('0',64);
                    if(kind==1) f.Size++;
                    if(kind==2) f.Destination="Wow.exe";
                    if(kind==3) m.Files.Add(f);
                    if(kind==4) f.Parts.Add(new PayloadPart {SourceFile="../escape",Sha256=f.Sha256,Size=f.Size!.Value});
                    await Expect("MANIFEST_INVALID",()=>u.ApplyAsync(m));
                }
            });
            await Run("older launcher refuses provisioning manifest",async()=> {
                var(r,m,u)=Fixture(); await Expect("LAUNCHER_TOO_OLD",()=>new ReleaseUpdater(r,new Source(Bytes("unused")),new Version(1,1,0)).ApplyAsync(m));
            });
            await Run("multipart resumes verified pieces and verifies assembled payload",async()=> {
                var(r,m,unused)=Fixture(); var f=m.Files[0]; var chunks=new[]{Bytes("first-"),Bytes("second")}; var source=new MapSource(new());
                f.Parts=chunks.Select((b,i)=>new PayloadPart {SourceFile="part"+i,Sha256=Hash(b),Size=b.Length}).ToList();
                f.Size=chunks.Sum(b=>b.Length); f.Sha256=Hash(chunks.SelectMany(b=>b).ToArray());
                foreach(var (p,i) in f.Parts.Select((p,i)=>(p,i))) source.Payloads[p.SourceFile]=chunks[i];
                var progress = new ProgressTests.Recorder();
                var u=new ReleaseUpdater(r,source,new Version(1,2,0),downloadProgress:progress);
                Write(r,ReleaseUpdater.StateDirName+"/staging/"+f.Parts[0].Sha256+".partial",chunks[0][..2]);
                await u.ApplyAsync(m); Check(source.Offsets.Contains(("part0",2L)),"Partial download not resumed");
                Check(Hash(File.ReadAllBytes(Full(r,f.Destination)))==f.Sha256,"Wrong assembly");
                var queue = progress.States.Where(s => s.Phase == DownloadPhase.Downloading)
                    .Select(s => s.Item).Distinct().ToArray();
                Check(queue.SequenceEqual(new[] {
                    new DownloadItem(DownloadKind.GamePatch, "part0", 1, 2),
                    new DownloadItem(DownloadKind.GamePatch, "part1", 2, 2)
                }), "Multipart download queue was incorrect.");
                Check(progress.States.Any(s => s.Phase == DownloadPhase.Assembling) &&
                    progress.States.Any(s => s.Phase == DownloadPhase.Installing), "Patch phases were missing.");
            });
            await Run("corrupt multipart data or assembled hash changes no client files",async()=> {
                foreach(bool corruptPart in new[]{true,false}) {
                    var(r,m,unused)=Fixture(); var f=m.Files[0]; var bytes=Bytes("piece");
                    f.Size=bytes.Length; f.Sha256=new string('0',64); f.Parts=new(){new PayloadPart{SourceFile="piece",Size=bytes.Length,Sha256=Hash(bytes)}};
                    var source=new MapSource(new(){["piece"]=corruptPart?Bytes("wrong"):bytes});
                    Write(r,f.Destination,Bytes("old")); await Expect("HASH_MISMATCH",()=>new ReleaseUpdater(r,source,new Version(1,2,0)).ApplyAsync(m));
                    Check(File.ReadAllText(Full(r,f.Destination))=="old","Corrupt parts replaced client");
                }
            });
            await Run("multipart size mismatch rejected before downloading",async()=> {
                var(r,m,u)=Fixture(); var f=m.Files[0]; f.Parts.Add(new(){SourceFile="part",Size=1,Sha256=new string('0',64)});
                await Expect("MANIFEST_INVALID",()=>u.ApplyAsync(m));
            });
            Console.WriteLine($"{passed} tests passed. All client writes used synthetic temporary fixtures.");
        }
        finally { if(Directory.Exists(Sandbox)) Directory.Delete(Sandbox,true); }
    }
    sealed class Source(byte[] payload) : IPayloadSource
    {
        public Task<PayloadResponse> OpenAsync(string sourceFile,long offset,CancellationToken ct) => Task.FromResult(new PayloadResponse { Stream=new MemoryStream(payload[(int)offset..]), StartOffset=offset });
    }
    sealed class MapSource(Dictionary<string, byte[]> payloads) : IPayloadSource
    {
        public Dictionary<string,byte[]> Payloads=payloads;
        public List<(string,long)> Offsets=new();
        public Task<PayloadResponse> OpenAsync(string sourceFile,long offset,CancellationToken ct) {
            Offsets.Add((sourceFile,offset)); return Task.FromResult(new PayloadResponse{Stream=new MemoryStream(Payloads[sourceFile][(int)offset..]),StartOffset=offset});
        }
    }
    sealed class DirectorySource(string folder) : IPayloadSource
    {
        public int Requests;
        public Task<PayloadResponse> OpenAsync(string sourceFile,long offset,CancellationToken ct) {
            Requests++; var stream=new FileStream(Path.Combine(folder,sourceFile),FileMode.Open,FileAccess.Read,FileShare.Read,1<<20,true);
            stream.Position=offset; return Task.FromResult(new PayloadResponse {Stream=stream,StartOffset=offset});
        }
    }
}
