using System.Reflection;
using CoopsWoWLauncher;

static class ProgressControlTests
{
    // Avoid startup feed checks while exercising the real controls.
    private sealed class TestForm : Form1
    {
        protected override void OnShown(EventArgs e) { }
    }

    [STAThread]
    public static void Main()
    {
        Application.EnableVisualStyles();
        using var form = new TestForm { Opacity = 0, ShowInTaskbar = false };
        form.Show();

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bar = (ProgressBar)typeof(Form1).GetField("downloadBar", flags)!.GetValue(form)!;
        var label = (Label)typeof(Form1).GetField("statusLabel", flags)!.GetValue(form)!;
        var render = typeof(Form1).GetMethod("ShowDownloadStatus", flags)!;

        void Render(DownloadStatus status) => render.Invoke(form, [status]);

        var item = new DownloadItem(DownloadKind.GamePatch, "patch-z.mpq", 2, 5);
        Render(new(item, DownloadPhase.Downloading, 37, 100, 8));
        Check(bar.Visible && bar.Style == ProgressBarStyle.Continuous && bar.Value == 37 &&
            label.Text.Contains("File 2 of 5") && label.Text.Contains("patch-z.mpq") &&
            label.Text.Contains("37 B / 100 B (37%)") && label.Text.Contains("8 B/s"));

        Render(new(item, DownloadPhase.Downloading, 37));
        Check(bar.Visible && bar.Style == ProgressBarStyle.Marquee && label.Text.Contains("37 B downloaded") &&
            label.Text.Contains("0 B/s") && !label.Text.Contains('%'));

        foreach (var (phase, expected) in new[] { (DownloadPhase.Verifying, "Verifying"),
            (DownloadPhase.Extracting, "Extracting"), (DownloadPhase.Installing, "Installing"),
            (DownloadPhase.ReadyToRestart, "Ready to restart"), (DownloadPhase.Restarting, "Restarting") })
        {
            Render(new(item, phase));
            Check(!bar.Visible && label.Text.Contains(expected) && !label.Text.Contains("B/s"));
        }

        foreach (DownloadKind kind in Enum.GetValues<DownloadKind>())
        {
            var category = item with { Kind = kind };
            Render(new(category, DownloadPhase.Downloading, 0, 100));
            Check(label.Text.StartsWith(category.DisplayName) && bar.Value == 0);
        }
        Console.WriteLine("PASS WinForms progress bar modes, queue text, transfer rates, phase labels and retry reset.");
    }

    private static void Check(bool ok)
    {
        if (!ok) throw new InvalidOperationException("Progress UI state mismatch.");
    }
}
