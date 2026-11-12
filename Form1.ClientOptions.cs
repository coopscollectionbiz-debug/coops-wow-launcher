namespace CoopsWoWLauncher;

/// <summary>
/// "Client options": turn optional patch categories (HD weapons, HD armor, new music, ...) on or off. Same effect as the
/// client's bundled PatchMenu (archives renamed to .disabled), but Check / Install Patch respects the choice.
/// </summary>
public partial class Form1
{
    private Button optionsButton = null!;

    private void InitializeClientOptionsControls()
    {
        // Four buttons share the main row.
        browseButton.SetBounds(30, 156, 130, 40);
        patchButton.SetBounds(168, 156, 150, 40);
        optionsButton = new Button { Text = "Client options", Left = 326, Top = 156, Width = 120, Height = 40 };
        launchButton.SetBounds(454, 156, 116, 40);
        optionsButton.Click += async (_, _) => await ShowClientOptionsAsync();
        Controls.Add(optionsButton);
    }

    private async Task ShowClientOptionsAsync()
    {
        if (patchBusy || graphicsMutating || hdMutating || !IsValidWowPath()) return;
        optionsButton.Enabled = false;
        string client = wowPath;
        UpdateManifest manifest;
        ReleaseUpdater updater = new(client, new HttpPayloadSource(Http, PatchBaseUrl), LauncherVersion);
        List<ClientOptionState> states;
        try
        {
            ShowStatus("Loading client options...");
            string json = await Http.GetStringAsync(ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            manifest = ReleaseUpdater.ParseManifest(json);
            updater.ValidateManifest(manifest);
            states = await Task.Run(() => updater.GetClientOptions(manifest));
            ShowStatus($"WoW detected:\n{client}");
        }
        catch (Exception ex)
        {
            ShowStatus("Client options could not be loaded.");
            MessageBox.Show(ex is ReleaseUpdateException r ? r.UserText : ex.Message + "\n\nCheck your internet connection and try again.",
                "Client options", MessageBoxButtons.OK, MessageBoxIcon.Error);
            optionsButton.Enabled = true;
            return;
        }

        if (states.Count == 0)
        {
            MessageBox.Show("This release has no optional client categories.", "Client options");
            optionsButton.Enabled = true;
            return;
        }

        using var dialog = new ClientOptionsDialog(states);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Changes.Count == 0)
        {
            optionsButton.Enabled = true;
            return;
        }

        patchBusy = true;
        browseButton.Enabled = patchButton.Enabled = launchButton.Enabled = false;
        UpdatePackageButtons(); UpdateHdButtons();
        bool download = false;
        var done = new List<string>();
        try
        {
            // Turn categories off first, then on in prerequisite order.
            foreach (var (id, on) in dialog.Changes.Where(c => !c.On))
            {
                ShowStatus($"Turning off {Name(id)}...");
                await Task.Run(() => updater.SetClientOptionAsync(manifest, id, false));
                done.Add(Name(id) + " off");
            }
            foreach (var (id, on) in dialog.Changes.Where(c => c.On))
            {
                ShowStatus($"Turning on {Name(id)}...");
                download |= await Task.Run(() => updater.SetClientOptionAsync(manifest, id, true));
                done.Add(Name(id) + " on");
            }
            ShowStatus("Client options saved: " + string.Join(", ", done) + ".\nChanges apply the next time WoW starts.");
        }
        catch (ReleaseUpdateException ex)
        {
            ShowStatus("Client options were not all changed." + (done.Count > 0 ? " Done: " + string.Join(", ", done) + "." : ""));
            MessageBox.Show(ex.UserText, "Client options", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            ShowStatus("Client options were not all changed." + (done.Count > 0 ? " Done: " + string.Join(", ", done) + "." : ""));
            MessageBox.Show(ex.Message + "\n\nClose World of Warcraft and try again.", "Client options", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            patchBusy = false;
            browseButton.Enabled = true;
            patchButton.Enabled = launchButton.Enabled = optionsButton.Enabled = IsValidWowPath();
            UpdatePackageButtons(); UpdateHdButtons();
        }

        // Categories turned back on whose files are gone are downloaded by the normal, verified patch install.
        if (download)
            await CheckAndInstallPatchAsync();

        string Name(string id) => states.First(s => s.Option.Id == id).Option.Name;
    }
}

internal sealed class ClientOptionsDialog : Form
{
    private readonly List<ClientOptionState> states;
    private readonly Dictionary<string, CheckBox> boxes = new();
    private bool syncing;

    /// <summary>Changes to apply: off first, then on in prerequisite order.</summary>
    public List<(string Id, bool On)> Changes { get; } = new();

    public ClientOptionsDialog(List<ClientOptionState> states)
    {
        this.states = states;
        Text = "Client options";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(560, 600);

        var intro = new Label
        {
            Left = 16, Top = 12, Width = 528, Height = 48,
            Text = "Choose which optional client patches to use. Turned-off patches stay on disk (renamed to .disabled), " +
                   "so turning them back on needs no download. Check / Install Patch keeps your choices. Changes apply the next time WoW starts."
        };
        var panel = new FlowLayoutPanel
        {
            Left = 16, Top = 64, Width = 528, Height = 476, AutoScroll = true,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, BorderStyle = BorderStyle.FixedSingle
        };
        var tips = new ToolTip { AutoPopDelay = 20000 };

        foreach (var group in states.GroupBy(s => s.Option.Group ?? "Other"))
        {
            panel.Controls.Add(new Label { Text = group.Key, Width = 490, Height = 22, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 6, 0, 0) });
            foreach (ClientOptionState s in group)
            {
                ClientOption o = s.Option;
                var box = new CheckBox
                {
                    Text = $"{o.Name}  ({DownloadStatus.FormatBytes(s.Bytes)})" + (o.Locked ? "  · required" : ""),
                    Width = 490, Height = 22, Tag = o.Id, ThreeState = false,
                    CheckState = s.Mode switch { ClientOptionMode.On => CheckState.Checked, ClientOptionMode.Off => CheckState.Unchecked, _ => CheckState.Indeterminate },
                    Enabled = !o.Locked
                };
                if (o.Locked) box.CheckState = CheckState.Checked;
                box.CheckedChanged += (_, _) => Sync(box);
                boxes[o.Id] = box;
                panel.Controls.Add(box);
                string detail = string.Join(" ", new[]
                {
                    o.Description,
                    o.Locked ? o.LockReason : null,
                    o.Warning is { Length: > 0 } w ? "Note: " + w : null,
                    s.Mode == ClientOptionMode.Partial ? "Partly turned off by another tool; tick to turn all of it on, untick to turn all of it off." : null,
                    s.NeedsDownload ? "Some files are missing and will be downloaded by Check / Install Patch." : null
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (detail.Length > 0)
                {
                    var label = new Label { Text = detail, Width = 470, Margin = new Padding(22, 0, 3, 6), ForeColor = SystemColors.GrayText };
                    label.Height = TextRenderer.MeasureText(detail, label.Font, new Size(470, 0), TextFormatFlags.WordBreak).Height + 4;
                    panel.Controls.Add(label);
                    tips.SetToolTip(box, detail);
                }
            }
        }

        var ok = new Button { Text = "Apply", Left = 344, Top = 552, Width = 96, Height = 32, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 448, Top = 552, Width = 96, Height = 32, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => CollectChanges();
        Controls.AddRange(new Control[] { intro, panel, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // Unticking a category also unticks what requires it; ticking one also ticks its prerequisites.
    private void Sync(CheckBox changed)
    {
        if (syncing) return;
        syncing = true;
        try
        {
            string id = (string)changed.Tag!;
            if (changed.Checked)
            {
                var todo = new Stack<string>(Option(id).Requires);
                while (todo.Count > 0)
                {
                    string r = todo.Pop();
                    if (boxes.TryGetValue(r, out var b) && b.Enabled && !b.Checked) { b.Checked = true; foreach (string x in Option(r).Requires) todo.Push(x); }
                }
            }
            else
            {
                var todo = new Stack<string>(new[] { id });
                while (todo.Count > 0)
                {
                    string cur = todo.Pop();
                    foreach (ClientOptionState d in states.Where(s => s.Option.Requires.Contains(cur, StringComparer.OrdinalIgnoreCase)))
                        if (boxes[d.Option.Id].Enabled && boxes[d.Option.Id].CheckState != CheckState.Unchecked)
                        { boxes[d.Option.Id].Checked = false; todo.Push(d.Option.Id); }
                }
            }
        }
        finally { syncing = false; }
    }

    private ClientOption Option(string id) => states.First(s => s.Option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Option;

    private void CollectChanges()
    {
        Changes.Clear();
        var ons = new List<string>();
        foreach (ClientOptionState s in states.Where(s => !s.Option.Locked))
        {
            CheckState now = boxes[s.Option.Id].CheckState;
            CheckState was = s.Mode switch { ClientOptionMode.On => CheckState.Checked, ClientOptionMode.Off => CheckState.Unchecked, _ => CheckState.Indeterminate };
            if (now == was && !(now == CheckState.Checked && s.NeedsDownload)) continue;
            if (now == CheckState.Unchecked) Changes.Add((s.Option.Id, false));
            else if (now == CheckState.Checked) ons.Add(s.Option.Id);
        }
        // Prerequisites first.
        var ordered = new List<string>();
        void Visit(string id)
        {
            if (ordered.Contains(id) || !ons.Contains(id)) return;
            foreach (string r in Option(id).Requires) Visit(r);
            ordered.Add(id);
        }
        foreach (string id in ons) Visit(id);
        Changes.AddRange(ordered.Select(id => (id, true)));
    }
}
