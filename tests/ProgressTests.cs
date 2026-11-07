using CoopsWoWLauncher;
using System.Collections.Concurrent;
using System.Security.Cryptography;

static class ProgressTests
{
    public static async Task TransferCases(string root)
    {
        byte[] content = new byte[4096];
        Random.Shared.NextBytes(content);
        foreach (var scenario in new[] { "fresh", "resume", "restart", "unknown", "bad-resume", "bad-hash" })
        {
            string path = Path.Combine(root, scenario);
            var file = new UpdateFile
            {
                Name = "addon.zip", SourceFile = "addon.zip", Type = "addon",
                Size = scenario == "unknown" ? null : content.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()
            };
            var reports = new Recorder();
            var source = new TestSource(content, scenario);
            var updater = new ReleaseUpdater(path, source, new Version(1, 2), downloadProgress: reports);
            string partial = Path.Combine(updater.StateDir, "staging", file.Sha256 + ".partial");
            bool resumes = scenario is "resume" or "restart" or "bad-resume";
            if (resumes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
                byte[] prefix = content[..2048];
                if (scenario == "bad-resume") prefix[0] ^= 1;
                File.WriteAllBytes(partial, prefix);
            }

            try
            {
                string verified = await updater.DownloadAndVerifyAsync(file, new(DownloadKind.Addon, file.Name, 2, 5), default);
                Check(scenario != "bad-hash" && File.ReadAllBytes(verified).SequenceEqual(content), scenario);
            }
            catch (ReleaseUpdateException ex) when (scenario == "bad-hash" && ex.Code == "HASH_MISMATCH")
            {
                Check(!File.Exists(partial), "Failed verification left corrupt bytes behind.");
            }

            var states = reports.States.ToArray();
            var transfer = states.Where(s => s.Phase == DownloadPhase.Downloading).ToArray();
            Check(states.All(s => s.Item == new DownloadItem(DownloadKind.Addon, file.Name, 2, 5)), "Lost queue context.");
            Check(transfer[0].DownloadedBytes == (resumes ? 2048 : 0) && transfer[0].BytesPerSecond == 0, scenario);
            Check(transfer[^1].DownloadedBytes == content.Length, "Missing final byte count.");
            Check(states[^1].Phase == DownloadPhase.Verifying && states[^1].Percent is null, "Verification retained the progress bar.");
            Check(transfer.All(s => s.TotalBytes == file.Size), "Incorrect total size.");
            if (scenario is "restart" or "bad-resume")
                Check(transfer.Skip(1).Any(s => s.DownloadedBytes == 0 && s.BytesPerSecond == 0), "Retry did not reset.");
            if (scenario == "bad-resume")
                Check(source.Offsets.SequenceEqual(new long[] { 2048, 0 }), "Corrupt partial was not retried from zero.");
        }
    }

    public static async Task StallAndKinds()
    {
        foreach (DownloadKind kind in Enum.GetValues<DownloadKind>())
        {
            var reports = new Recorder();
            var item = new DownloadItem(kind, kind == DownloadKind.LauncherUpdate ? "launcher.exe" : "payload.zip");
            await using var source = new StalledStream();
            await using var output = new MemoryStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            const long existing = 100_000_000;
            Task<long> transfer = DownloadTransfer.CopyAsync(source, output, item, existing, null, reports, timeout.Token);
            try
            {
                while (!reports.States.Any(s => s.BytesPerSecond > 0))
                    await Task.Delay(30, timeout.Token);
                var moving = reports.States.First(s => s.BytesPerSecond > 0);
                Check(moving.DownloadedBytes == existing + 1 && moving.BytesPerSecond < 10_000,
                    "The resumed prefix inflated transfer speed.");
                // Exercise the full rolling window once. All kinds use the same sampler.
                if (kind == DownloadKind.GamePatch)
                {
                    while (reports.States.Last().BytesPerSecond != 0)
                        await Task.Delay(30, timeout.Token);
                    Check(!transfer.IsCompleted && reports.States.Last().DownloadedBytes == existing + 1,
                        "A stalled read should keep the byte count and drop to zero speed.");
                }
                Check(reports.States.All(s => s.Item == item && s.Percent is null), "Wrong category or known-size state.");
            }
            finally
            {
                source.Release.TrySetResult();
                await transfer;
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    internal sealed class Recorder : IProgress<DownloadStatus>
    {
        public ConcurrentQueue<DownloadStatus> States { get; } = new();
        public void Report(DownloadStatus value) => States.Enqueue(value);
    }

    private sealed class TestSource(byte[] content, string scenario) : IPayloadSource
    {
        public List<long> Offsets { get; } = new();

        public Task<PayloadResponse> OpenAsync(string file, long offset, CancellationToken ct)
        {
            Offsets.Add(offset);
            int start = scenario == "restart" ? 0 : (int)offset;
            byte[] bytes = content[start..];
            if (scenario == "bad-hash") bytes[0] ^= 1;
            return Task.FromResult(new PayloadResponse { Stream = new MemoryStream(bytes), StartOffset = start });
        }
    }

    private sealed class StalledStream : MemoryStream
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent)
            {
                sent = true;
                buffer.Span[0] = 1;
                return 1;
            }
            await Release.Task.WaitAsync(ct);
            return 0;
        }
    }
}
