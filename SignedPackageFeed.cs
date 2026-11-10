using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoopsWoWLauncher;

internal sealed class PackageMetadata
{
    public string Channel { get; init; } = "";
    public string Version { get; init; } = "";
    public string Filename { get; init; } = "";
    public string Url { get; init; } = "";
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public string[] CompatibleWowSha256 { get; init; } = [];
    public string[] SupportedBuilds { get; init; } = [];
    public string[] Prerequisites { get; init; } = [];
    public string[] Files { get; init; } = [];
    /// <summary>HD Environment Pack only: the mandatory Data\patch-z.mpq builds the pack was validated against.</summary>
    public string[] RequiredCoreSha256 { get; init; } = [];
    public bool InstallationEnabled { get; init; }
}

internal sealed class SignedPackageFeed(HttpClient http, string channel, string endpoint, string publicKey)
{
    private sealed record Envelope(string Payload, string Signature);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MetadataLimit = 256 * 1024;
    private const string Releases = "https://github.com/coopscollectionbiz-debug/coops-wow-launcher/releases/download/";

    internal static bool Staging => BuildSetting("StagingFeeds") == "true";
    internal static string BuildSetting(string name) => typeof(SignedPackageFeed).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value ?? "";

    internal static SignedPackageFeed Launcher(HttpClient http) => new(http, "launcher",
        Endpoint("LauncherFeedUrl", "launcher-feed"), BuildSetting("LauncherFeedKey"));

    internal static SignedPackageFeed Lumenveil(HttpClient http) => new(http, "lumenveil",
        Endpoint("LumenveilFeedUrl", "lumenveil-feed"), BuildSetting("LumenveilFeedKey"));

    internal static SignedPackageFeed HdEnvironment(HttpClient http) => new(http, "hdenv",
        Endpoint("HdEnvFeedUrl", "hdenv-feed"), BuildSetting("HdEnvFeedKey"));

    private static string Endpoint(string setting, string tag) =>
        Staging && BuildSetting(setting) is { Length: > 0 } url ? url : Releases + tag + "/metadata.json";

    internal async Task<PackageMetadata?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, TrustedUri(endpoint));
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var content = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (content.Length + count > MetadataLimit)
                throw new InvalidDataException("Package metadata is too large.");
            content.Write(buffer, 0, count);
        }

        var envelope = JsonSerializer.Deserialize<Envelope>(content.ToArray(), Json)
            ?? throw new InvalidDataException("Package metadata is missing.");
        if (envelope.Payload is null || envelope.Signature is null)
            throw new InvalidDataException("Package metadata has no signature.");

        byte[] payload = Convert.FromBase64String(envelope.Payload);
        using var verifier = ECDsa.Create();
        byte[] key = Convert.FromBase64String(publicKey);
        verifier.ImportSubjectPublicKeyInfo(key, out int keyLength);
        if (keyLength != key.Length || verifier.KeySize != 256 ||
            !verifier.VerifyData(payload, Convert.FromBase64String(envelope.Signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Package metadata signature is invalid.");

        var package = JsonSerializer.Deserialize<PackageMetadata>(payload, Json)
            ?? throw new InvalidDataException("Signed package metadata is empty.");
        string extension = channel switch { "launcher" => ".exe", "hdenv" => ".mpq", _ => ".zip" };
        if (package.Channel != channel || !Version.TryParse(package.Version, out var version) ||
            version.Build < 0 || version.Revision >= 0 || version.ToString() != package.Version || package.Size <= 0 ||
            !IsSha256(package.Sha256) || package.Filename is null ||
            !Regex.IsMatch(package.Filename, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z") ||
            !package.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            (channel == "hdenv" && (package.RequiredCoreSha256.Length == 0 || !package.RequiredCoreSha256.All(IsSha256))))
            throw new InvalidDataException("Signed package metadata has invalid fields.");

        Uri asset = TrustedUri(package.Url);
        if (asset.Query.Length > 0 || asset.Fragment.Length > 0 || asset.Segments[^1] != package.Filename ||
            (!Staging && asset.AbsoluteUri != $"{Releases}{channel}-v{package.Version}/{package.Filename}"))
            throw new InvalidDataException("Package URL must name an immutable versioned release asset.");

        return package;
    }

    internal async Task<string> DownloadAsync(PackageMetadata package, string stagingRoot,
        DownloadKind kind, IProgress<DownloadStatus> progress, CancellationToken ct = default)
    {
        Uri asset = TrustedUri(package.Url);
        var source = new HttpPayloadSource(http, new Uri(asset, ".").AbsoluteUri);
        var updater = new ReleaseUpdater(stagingRoot, source, new Version(1, 2), downloadProgress: progress);
        var file = new UpdateFile
        {
            Name = package.Filename, SourceFile = package.Filename,
            Size = package.Size, Sha256 = package.Sha256
        };
        return await updater.DownloadAndVerifyAsync(file, new DownloadItem(kind, package.Filename), ct);
    }

    internal static bool IsSha256(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static Uri TrustedUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && !(Staging && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidDataException("Package URL is not trusted.");
        return uri;
    }
}
