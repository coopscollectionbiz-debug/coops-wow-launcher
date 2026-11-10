// Feed signing for Coop's WoW Launcher (ECDSA P-256, SHA-256, IEEE P1363 signatures).
//
//   genkey  --private <file outside the repo> --public <file>
//   sign    --private <file> --payload <payload.json> --out <metadata.json>
//   verify  --public <file> --metadata <metadata.json> --channel launcher|lumenveil|hdenv
//   check-live   (build with -p:LauncherFeedKey=... -p:LumenveilFeedKey=... -p:HdEnvFeedKey=...; checks the published feeds)
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoopsWoWLauncher;

var opts = new Dictionary<string, string>();
for (int i = 1; i + 1 < args.Length; i += 2) opts[args[i].TrimStart('-')] = args[i + 1];
string command = args.Length > 0 ? args[0] : "";

switch (command)
{
    case "genkey":
    {
        string priv = Path.GetFullPath(opts["private"]);
        if (File.Exists(priv)) throw new IOException("Refusing to overwrite an existing private key: " + priv);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(priv)!);
        File.WriteAllText(priv, key.ExportPkcs8PrivateKeyPem() + "\n");
        string pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        File.WriteAllText(opts["public"], pub + "\n");
        Console.WriteLine(pub);
        break;
    }
    case "sign":
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(opts["private"]));
        if (key.KeySize != 256) throw new InvalidDataException("Expected a P-256 key.");
        // Sign the compact form of the payload; the launcher verifies these exact bytes.
        byte[] payload = Encoding.UTF8.GetBytes(JsonNode.Parse(File.ReadAllText(opts["payload"]))!.ToJsonString());
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllText(opts["out"], new JsonObject
        {
            ["payload"] = Convert.ToBase64String(payload),
            ["signature"] = Convert.ToBase64String(signature),
        }.ToJsonString() + "\n");
        Console.WriteLine($"signed {payload.Length} bytes -> {opts["out"]}");
        break;
    }
    case "verify":
    {
        var envelope = JsonNode.Parse(File.ReadAllText(opts["metadata"]))!;
        byte[] payload = Convert.FromBase64String((string)envelope["payload"]!);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(File.ReadAllText(opts["public"]).Trim()), out _);
        bool ok = key.VerifyData(payload, Convert.FromBase64String((string)envelope["signature"]!),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var meta = JsonNode.Parse(payload)!;
        if (!ok || (string?)meta["channel"] != opts["channel"]) throw new InvalidDataException("Signature or channel check failed.");
        Console.WriteLine($"valid {opts["channel"]} {meta["version"]} {meta["url"]} {meta["sha256"]}");
        break;
    }
    case "check-live":
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CoopsWoWLauncher-FeedCheck/1.0");
        foreach (var (name, feed) in new[] { ("launcher", SignedPackageFeed.Launcher(http)), ("lumenveil", SignedPackageFeed.Lumenveil(http)), ("hdenv", SignedPackageFeed.HdEnvironment(http)) })
        {
            PackageMetadata? package = await feed.CheckAsync();
            if (package is null) throw new InvalidDataException($"{name}: feed unavailable (no key embedded or 404).");
            Console.WriteLine($"{name}: accepted {package.Version} {package.Url} size {package.Size} sha256 {package.Sha256}" +
                (name == "lumenveil" ? $" installable={package.InstallationEnabled} wow={string.Join(",", package.CompatibleWowSha256)} files={package.Files.Length}" :
                 name == "hdenv" ? $" installable={package.InstallationEnabled} core={string.Join(",", package.RequiredCoreSha256)}" : ""));
        }
        break;
    }
    default:
        Console.Error.WriteLine("Commands: genkey | sign | verify | check-live");
        Environment.ExitCode = 2;
        break;
}
