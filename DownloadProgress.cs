using System.Diagnostics;

namespace CoopsWoWLauncher;

public enum DownloadKind
{
    LauncherUpdate,
    GamePatch,
    Addon,
    Lumenveil,
    HdEnvironment
}

public enum DownloadPhase
{
    Downloading,
    Verifying,
    Assembling,
    Extracting,
    Installing,
    ReadyToRestart,
    Restarting
}

public sealed record DownloadItem(DownloadKind Kind, string Filename, int Position = 1, int Count = 1)
{
    public string DisplayName => Kind switch
    {
        DownloadKind.LauncherUpdate => "Launcher update",
        DownloadKind.GamePatch => "Game patch",
        DownloadKind.Addon => "Addon",
        DownloadKind.Lumenveil => "Lumenveil",
        DownloadKind.HdEnvironment => "HD Environment Pack",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

public sealed record DownloadStatus(
    DownloadItem Item, DownloadPhase Phase, long DownloadedBytes = 0,
    long? TotalBytes = null, double BytesPerSecond = 0)
{
    public int? Percent => TotalBytes is long total
        ? total == 0 ? 100 : (int)Math.Clamp(Math.Floor(100.0 * DownloadedBytes / total), 0, 100)
        : null;

    public string TransferText => TotalBytes is long total
        ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(total)} ({Percent}%) · {FormatBytes(BytesPerSecond)}/s"
        : $"{FormatBytes(DownloadedBytes)} downloaded · {FormatBytes(BytesPerSecond)}/s";

    internal static string FormatBytes(double bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (1L << 30):N1} GiB";
        if (bytes >= 1 << 20) return $"{bytes / (1 << 20):N1} MiB";
        if (bytes >= 1 << 10) return $"{bytes / (1 << 10):N1} KiB";
        return $"{bytes:N0} B";
    }
}

public static class DownloadTransfer
{
    // An independent timer keeps reporting during a pending read. Samples include
    // only bytes written by this call, so resumed bytes never inflate the rate.
    public static async Task<long> CopyAsync(
        Stream source, Stream destination, DownloadItem item, long existingBytes,
        long? totalBytes, IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        long received = 0;
        double speed = 0;
        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        progress?.Report(new(item, DownloadPhase.Downloading, existingBytes, totalBytes));
        Task sampling = progress is null ? Task.CompletedTask : SampleAsync();
        try
        {
            byte[] buffer = new byte[1 << 20];
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                Interlocked.Add(ref received, read);
            }
        }
        finally
        {
            timer.Dispose();
            await sampling.ConfigureAwait(false);
        }
        if (clock.Elapsed < TimeSpan.FromMilliseconds(250))
            speed = received / clock.Elapsed.TotalSeconds;
        progress?.Report(new(item, DownloadPhase.Downloading, existingBytes + received, totalBytes, speed));
        return received;

        async Task SampleAsync()
        {
            var samples = new Queue<(TimeSpan Time, long Bytes)>();
            samples.Enqueue((TimeSpan.Zero, 0));
            while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                TimeSpan now = clock.Elapsed;
                long bytes = Interlocked.Read(ref received);
                while (samples.Count > 1 && now - samples.Peek().Time > TimeSpan.FromSeconds(2))
                    samples.Dequeue();
                var oldest = samples.Peek();
                speed = (bytes - oldest.Bytes) / (now - oldest.Time).TotalSeconds;
                progress!.Report(new(item, DownloadPhase.Downloading, existingBytes + bytes, totalBytes, speed));
                samples.Enqueue((now, bytes));
            }
        }
    }
}
