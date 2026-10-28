using Il2Radio.Core;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Input;
using Il2Radio.Core.Osd;
using Il2Radio.Core.Radio;
using Il2Radio.Core.Settings;
using Il2Radio.Windows;

namespace Il2Radio.App;

/// <summary>
/// The player's window: flight status, and every setting in settings.json. The diagnostic console is separate.
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly (string Label, string Action)[] KeyRows =
    [
        ("Friendly positions", "friendlyPositions"),
        ("Flight position", "flightPosition"),
        ("Rejoin", "rejoin"),
        ("Enemy contacts", "enemyContacts"),
        ("Identify", "identifyContact"),
        ("Route position", "routePosition"),
        ("Steer home", "steerHome"),
        ("Steer to target", "steerTarget"),
        ("Fix position", "fixPosition"),
        ("Kneeboard", "kneeboard"),
        ("Menu", "menu"),
        ("Older log", "older"),
        ("Newer log", "newer"),
        ("Quit", "quit"),
    ];

    private readonly string _settingsPath;
    private readonly FlightHost _host = new();
    private readonly PageHost _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _statusPage = Page("Status", 16, scroll: false);
    private readonly TabPage _helpPage = Page("Help", 12, scroll: true);
    private readonly TabPage _settingsPage = Page("Settings", 8, scroll: true);
    private readonly HeaderBanner _header = new();
    private readonly NavStrip _nav;
    private readonly List<(Label Label, HelpBlock Kind)> _helpBlocks = [];
    private readonly Dictionary<string, Label> _helpKeys = new(StringComparer.Ordinal);
    private bool _layingOutHelp;
    private readonly Label _state = new()
    {
        Dock = DockStyle.Top,
        Height = 44,
        Text = "Starting…",
        Padding = new Padding(0, 0, 8, 4),
        Font = Brand.Ui(10.5f),
        ForeColor = Brand.Ink,
    };
    private readonly Label _mission = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _track = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _you = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _chat = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly Label _audio = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox _notices = ReadOnlyBox();
    private readonly TextBox _exchange = ReadOnlyBox();
    private readonly TextBox _notes = ReadOnlyBox();
    private readonly TextBox _problems = new()
    {
        Dock = DockStyle.Bottom,
        Height = 64,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Visible = false,
        BorderStyle = BorderStyle.None,
        BackColor = Brand.ProblemBack,
        ForeColor = Brand.ProblemText,
    };

    private readonly TextBox _il2Path = new();
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 220, Height = 32, BackColor = Brand.Paper };
    private readonly Label _volumeReadout = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly ComboBox _voice = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 280 };
    private readonly ComboBox _realism = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _positions = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _unaskedRoute = new() { Text = "Call a new heading on a cruise leg", AutoSize = true, Checked = true };
    private readonly CheckBox _unaskedContacts = new() { Text = "Call when the contact picture changes", AutoSize = true, Checked = true };
    private readonly CheckBox _unaskedLosses = new() { Text = "Call a loss the game's pilots don't", AutoSize = true, Checked = true };
    private readonly CheckBox _radioWait = new() { Text = "Hold replies while IL-2's own radio is talking", AutoSize = true };
    private readonly NumericUpDown _radioWaitRatio = Number(1.1m, 10m, 1.8m, 0.1m, 1);
    private readonly NumericUpDown _reminder = Number(0, 600, 60, 5);
    private readonly ComboBox _chatOutput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _kneeboardView = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _kneeboardPosition = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _kneeboardX = Number(0, 16384, 0, 10);
    private readonly NumericUpDown _kneeboardY = Number(0, 16384, 0, 10);
    private int[]? _kneeboardScreen; // the resolution custom x, y were set at, kept as loaded unless they change
    private int[]? _kneeboardCustomLoaded;
    private readonly NumericUpDown _screen = Number(0, 8, 0, 1);
    private readonly NumericUpDown _areaL = Fraction(0.015m);
    private readonly NumericUpDown _areaT = Fraction(0.40m);
    private readonly NumericUpDown _areaW = Fraction(0.42m);
    private readonly NumericUpDown _areaH = Fraction(0.30m);
    private readonly NumericUpDown _font = Number(6, 96, 16, 1);
    private readonly CheckBox _keepClear = new() { Text = "Keep the text out of IL-2's own message line and subtitles", AutoSize = true, Checked = true };
    private readonly Dictionary<string, TextBox> _keyBoxes = new(StringComparer.Ordinal);
    private readonly Button _apply = new() { Text = "Save and apply", Width = 120, Height = 28 };
    private readonly Button _reset = new() { Text = "Reset to defaults", Width = 140, Height = 28 };
    private readonly Button _quit = new() { Text = "Quit", Width = 88, Height = 28 };

    private bool _shown;
    private bool _closing;
    private bool _filling;
    private bool _createdDefaults;
    private CancellationTokenSource? _startup;
    private int _settingsBottom;

    public MainForm(string settingsPath)
    {
        _settingsPath = settingsPath;
        _nav = new NavStrip(_tabs);
        Text = Brand.Name;
        Icon = Brand.AppIcon() ?? Icon;
        Font = Brand.Ui(9f);
        BackColor = Brand.Paper;
        ForeColor = Brand.Ink;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(780, 760);
        MinimumSize = new Size(720, 640);
        _host.Ended += OnSessionEnded;
        BuildStatus();
        BuildSettings();
        BuildHelp();
        BuildChrome();
        _volume.ValueChanged += (_, _) =>
        {
            _volumeReadout.Text = $"{_volume.Value} %";
            if (!_filling && _host.Voice is { } voice)
                voice.Volume = _volume.Value / 100.0;
        };
        _kneeboardPosition.SelectedIndexChanged += (_, _) => EnableKneeboardCustom();
        _apply.Click += async (_, _) => await SaveAndStartAsync(writeFile: true);
        _reset.Click += (_, _) => ResetToDefaults();
        _quit.Click += (_, _) => Close();
        _apply.Enabled = false;
        _reset.Enabled = false;
        Brand.Primary(_apply);
        Brand.Secondary(_reset);
        Brand.Secondary(_quit);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Brand.DarkCaption(Handle);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shown)
            return;
        _shown = true;
        await LoadAndStartAsync();
        RelayoutHelp();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closing)
        {
            base.OnFormClosing(e);
            return;
        }

        if (!_host.NeedsRelease)
        {
            _closing = true;
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        _closing = true;
        Text = "IL-2 Tactical Radio — stopping";
        SetState(RadioState.Starting, "Stopping");
        _apply.Enabled = false;
        try { _startup?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { await _host.StopAsync(); }
        catch (Exception) { /* closing still has to finish; the session was given 4 s */ }
        Close();
    }

    private void BuildChrome()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            Padding = new Padding(12, 10, 12, 8),
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Brand.Footer,
        };
        buttons.Paint += (_, e) =>
        {
            using var pen = new Pen(Brand.Rule);
            e.Graphics.DrawLine(pen, 0, 0, buttons.Width, 0);
        };
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_reset);
        buttons.Controls.Add(_quit);
        _tabs.TabPages.Add(_statusPage);
        _tabs.TabPages.Add(_helpPage);
        _tabs.TabPages.Add(_settingsPage);
        Controls.Add(_tabs);
        Controls.Add(_problems);
        Controls.Add(buttons);
        Controls.Add(_nav);
        Controls.Add(_header);
    }

    private void SetState(RadioState state, string text) => _nav.SetState(state, text);

    private static TabPage Page(string title, int padding, bool scroll) => new(title)
    {
        AutoScroll = scroll,
        Padding = new Padding(padding),
        UseVisualStyleBackColor = false,
        BackColor = Brand.Paper,
    };

    private void BuildStatus()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] captions = ["Mission", "Track", "You", "Chat", "Audio"];
        Label[] values = [_mission, _track, _you, _chat, _audio];
        for (int i = 0; i < captions.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label
            {
                Text = captions[i].ToUpperInvariant(),
                AutoSize = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 5, 0, 4),
                Font = Brand.Semibold(8f),
                ForeColor = Brand.Muted,
            }, 0, i);
            values[i].Padding = new Padding(0, 4, 0, 4);
            grid.Controls.Add(values[i], 1, i);
        }

        AddSpan(grid, "Start-up", 5);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        grid.Controls.Add(_notices, 0, 6);
        grid.SetColumnSpan(_notices, 2);
        AddSpan(grid, "Last radio call", 7);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        grid.Controls.Add(_exchange, 0, 8);
        grid.SetColumnSpan(_exchange, 2);
        AddSpan(grid, "Notes", 9);
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        grid.Controls.Add(_notes, 0, 10);
        grid.SetColumnSpan(_notes, 2);
        grid.RowCount = 11;

        _statusPage.Controls.Add(grid);
        _statusPage.Controls.Add(_state);
    }

    /// <summary>
    /// Help explains the radio for any air force, and when each call is the right one.
    /// Key names are the ones currently in the Settings boxes, including edits not yet applied.
    /// </summary>
    private void BuildHelp()
    {
        void Section(string title) => AddHelp(title, HelpBlock.Section);
        void Body(string text) => AddHelp(text, HelpBlock.Body);

        Section("What this is");
        foreach (string paragraph in RadioGuide.Purpose)
            Body(paragraph);

        Section("When to use each call");
        Body("The key shown here is the one on the Settings tab.");
        foreach (var call in RadioGuide.Calls)
            AddHelpKey(call.Action, RadioCommands.Title(call.Command), call.When);

        Section("Other keys");
        Body("These do not call anyone.");
        foreach (var key in RadioGuide.OtherKeys)
            AddHelpKey(key.Action, key.Title, key.When);

        Section("While you fly");
        foreach (string paragraph in RadioGuide.Flying)
            Body(paragraph);

        Section("Version");
        Body($"IL-2 Tactical Radio {AppVersion.Text}. Session reports in the logs folder next to the program record it too; please include one with a bug report.");

        foreach (var box in _keyBoxes.Values)
            box.TextChanged += (_, _) => RefreshHelpKeys();
        _helpPage.Resize += (_, _) => RelayoutHelp();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedTab == _helpPage)
                RelayoutHelp();
        };
        RefreshHelpKeys();
    }

    private void AddHelpKey(string action, string title, string when)
    {
        var heading = AddHelp(title, HelpBlock.Key);
        _helpKeys[action] = heading;
        AddHelp(when, HelpBlock.Body);
    }

    private Label AddHelp(string text, HelpBlock kind)
    {
        var label = kind == HelpBlock.Section
            ? new SectionRule(text)
            : new Label
            {
                Text = text,
                AutoSize = true,
                Font = kind == HelpBlock.Body ? Font : Brand.Semibold(9.5f),
                ForeColor = kind == HelpBlock.Body ? Brand.Ink : Brand.SkyTop,
            };
        _helpPage.Controls.Add(label);
        _helpBlocks.Add((label, kind));
        return label;
    }

    private void RefreshHelpKeys()
    {
        foreach (var call in RadioGuide.Calls)
            SetHelpKey(call.Action, RadioCommands.Title(call.Command));
        foreach (var key in RadioGuide.OtherKeys)
            SetHelpKey(key.Action, key.Title);
        RelayoutHelp();
    }

    private void SetHelpKey(string action, string title)
    {
        if (_helpKeys.TryGetValue(action, out var label))
            label.Text = $"{BoundKey(action)} — {title}";
    }

    private string BoundKey(string action) =>
        _keyBoxes.TryGetValue(action, out var box) && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : "not set";

    private void RelayoutHelp()
    {
        if (_layingOutHelp || _helpBlocks.Count == 0)
            return;
        _layingOutHelp = true;
        try
        {
            int width = Math.Max(280, _helpPage.ClientSize.Width - 36);
            int y = 8;
            foreach (var (label, kind) in _helpBlocks)
            {
                if (kind == HelpBlock.Section)
                    y += 10;
                int height = TextRenderer.MeasureText(label.Text, label.Font, new Size(width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.NoPrefix).Height;
                if (kind == HelpBlock.Section)
                    height += label.Padding.Bottom + 6;
                label.AutoSize = false;
                label.SetBounds(8, y, width, height);
                y += height + (kind == HelpBlock.Key ? 2 : 8);
            }

            _helpPage.AutoScrollMinSize = new Size(0, y + 12);
        }
        finally
        {
            _layingOutHelp = false;
        }
    }

    private enum HelpBlock { Section, Key, Body }

    private static void AddSpan(TableLayoutPanel grid, string text, int row)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new SectionRule(text) { Dock = DockStyle.Fill, Height = 30 };
        grid.Controls.Add(label, 0, row);
        grid.SetColumnSpan(label, 2);
    }

    private void BuildSettings()
    {
        int y = 8;
        void Section(string title)
        {
            y += 8;
            _settingsPage.Controls.Add(new SectionRule(title) { Left = 8, Top = y, Width = 688 });
            y += 32;
        }

        Control Row(string caption, Control control, int width = 440, int height = 24)
        {
            _settingsPage.Controls.Add(new Label { Text = caption, Left = 8, Top = y + 3, Width = 200 });
            control.SetBounds(216, y, width, height);
            _settingsPage.Controls.Add(control);
            y += height + 8;
            return control;
        }

        void Check(CheckBox box)
        {
            box.Left = 216;
            box.Top = y;
            _settingsPage.Controls.Add(box);
            y += 28;
        }

        Section("IL-2");
        var pathRow = new FlowLayoutPanel { Height = 28, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        _il2Path.Width = 340;
        var browseIl2 = new Button { Text = "Browse…", Width = 88, Height = 24 };
        Brand.Secondary(browseIl2);
        browseIl2.Click += (_, _) => BrowseFolder(_il2Path, "Select the IL-2 folder (it contains data\\startup.cfg)", reloadVoices: true);
        pathRow.Controls.Add(_il2Path);
        pathRow.Controls.Add(browseIl2);
        Row("Folder", pathRow, 440, 28);
        Hint("Empty finds it in the usual Steam folders. This is the folder with data\\startup.cfg.", ref y);

        Section("Sound");
        var volumeRow = new FlowLayoutPanel { Height = 36, WrapContents = false };
        volumeRow.Controls.Add(_volume);
        volumeRow.Controls.Add(_volumeReadout);
        Row("Volume", volumeRow, 440, 36);
        Row("Voice", _voice, 280);
        Hint("auto uses the game's pilot voices by nation; what they have no recording for stays on the radio text. off is text only. Or a voice pack's name.", ref y);
        _realism.Items.AddRange(AppSettings.RealismModes);
        Row("Realism", _realism, 160);
        Hint("pilot: he reports a position relative to you only when he can see you or is with you. Otherwise your flight gives its heading and altitude, and a pilot in a fight says the enemy is on him. precise: bearings and ranges from the track.", ref y);
        _positions.Items.AddRange(AppSettings.PositionsModes);
        Row("Positions", _positions, 160);
        Hint("auto: read where everyone is from IL-2's memory (read-only, no track recording needed), and use the track recording when memory can't be read. memory: memory only. track: the track recording only (LCtrl+R), as before.", ref y);
        Check(_unaskedRoute);
        Check(_unaskedContacts);
        Check(_unaskedLosses);
        Hint("Unasked calls. A cruise call names the new course in degrees, once per turn: your AI leader, or a \"Route\" line when you lead. A fight gives one line when a group is gained, lost, or on him. A loss is called by a flight mate who saw it, in the chat, unless the game's own pilots said it (their own kill, or a mate calling that he is hit). Turn all three off to keep the radio answers-only.", ref y);
        Check(_radioWait);
        Row("Talking threshold", _radioWaitRatio, 80);
        Hint("How far above its engine IL-2's audio must peak to count as talking. More than 1.", ref y);
        Row("Recording reminder (s)", _reminder, 80);
        Hint("Remind you to press LCtrl+R this long into a mission with neither memory positions nor a track. 0 never reminds.", ref y);

        Section("Radio text");
        _chatOutput.Items.AddRange(ChatSettings.Outputs);
        Row("Show text", _chatOutput, 160);
        Hint("ingame draws the text, the radio menu and the kneeboard inside IL-2's own picture, fullscreen too, by loading the radio's own overlay into IL-2 (it draws only these, and reads nothing). overlay is a window over a windowed IL-2. window is an ordinary window, e.g. on a second screen. off shows nothing.", ref y);

        Section("Kneeboard");
        _kneeboardView.Items.AddRange(["hidden", "small", "large"]);
        Row("View", _kneeboardView, 160);
        Hint("Where a flight starts. The kneeboard key (and the radio menu) changes it in the game, and the view you leave it in is kept here for the next flight. Needs the ingame text.", ref y);
        _kneeboardPosition.Items.AddRange(KneeboardPlacement.Presets);
        Row("Position", _kneeboardPosition, 160);
        var customRow = new FlowLayoutPanel { Height = 28, WrapContents = false };
        Piece(customRow, "X", _kneeboardX);
        Piece(customRow, "Y", _kneeboardY);
        Row("Custom corner (px)", customRow, 300, 28);
        Hint("A preset pins that corner of the sheet near the screen's edge; the large sheet grows away from it. custom puts its top-left corner at X, Y pixels of IL-2's picture, scaled if IL-2's resolution changes. It is always kept on screen.", ref y);

        Section("Advanced chat layout");
        Row("Screen number", _screen, 80);
        var areaRow = new FlowLayoutPanel { Height = 28, WrapContents = false };
        Piece(areaRow, "Left", _areaL);
        Piece(areaRow, "Top", _areaT);
        Piece(areaRow, "Width", _areaW);
        Piece(areaRow, "Height", _areaH);
        Row("Window area", areaRow, 480, 28);
        Hint("Overlay and window only: left, top, width and height as fractions of the screen.", ref y);
        Row("Font size", _font, 80);
        Hint("Pixels on a 1080-line screen. Other screens are scaled from that.", ref y);
        Check(_keepClear);

        Section("Keys");
        Hint("Click a box and press the key. Backspace clears it (the action is left unbound). IL-2 still receives the key, so pick one it doesn't use.", ref y);
        foreach (var (label, action) in KeyRows)
        {
            var box = new TextBox { ReadOnly = true, ShortcutsEnabled = false, BackColor = Color.White, Font = Brand.Semibold(9) };
            box.KeyDown += CaptureKey;
            box.GotFocus += (_, _) => box.BackColor = KeyFocus;
            box.LostFocus += (_, _) => box.BackColor = Color.White;
            _keyBoxes[action] = box;
            Row(label, box, 120);
        }

        Hint("Saved as settings.json next to the program. Save and apply restarts the radio here; the volume takes effect on the next transmission either way.", ref y);
        _settingsBottom = y;
        _settingsPage.AutoScrollMinSize = new Size(680, _settingsBottom + 12);
    }

    private static void Piece(FlowLayoutPanel row, string caption, NumericUpDown box)
    {
        row.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(8, 4, 4, 0) });
        row.Controls.Add(box);
    }

    private async Task LoadAndStartAsync()
    {
        AppSettings settings;
        IReadOnlyList<string> problems;
        bool created;
        try
        {
            (settings, created, problems) = AppSettings.LoadOrCreate(_settingsPath);
        }
        catch (IOException ex)
        {
            Fill(new AppSettings());
            ShowProblems([$"Can't read or create {_settingsPath}: {ex.Message}"]);
            _tabs.SelectedTab = _settingsPage;
            _state.Text = "Not running.";
            SetState(RadioState.Stopped, "Not running");
            _apply.Enabled = true;
            _reset.Enabled = true;
            return;
        }

        Fill(settings);
        if (problems.Count > 0)
        {
            ShowProblems(problems);
            _tabs.SelectedTab = _settingsPage;
            _state.Text = "Not running.";
            SetState(RadioState.Stopped, "Not running");
            _mission.Text = "Settings need fixing before the radio can start. Fix them, or reset to defaults, then Save and apply.";
            _apply.Enabled = true;
            _reset.Enabled = true;
            return;
        }

        _createdDefaults = created;
        await SaveAndStartAsync(writeFile: false);
    }

    private async Task SaveAndStartAsync(bool writeFile)
    {
        if (_closing)
            return;
        var settings = ReadSettings();
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            ShowProblems(problems);
            _tabs.SelectedTab = _settingsPage;
            _apply.Enabled = true;
            _reset.Enabled = true;
            return;
        }

        ShowProblems([]);
        if (writeFile)
        {
            try
            {
                File.WriteAllText(_settingsPath, settings.ToJson() + Environment.NewLine);
            }
            catch (IOException ex)
            {
                ShowProblems([$"Can't write {_settingsPath}: {ex.Message}"]);
                return;
            }
        }

        _apply.Enabled = false;
        _reset.Enabled = false;
        SetState(RadioState.Starting, "Starting");
        _startup?.Dispose();
        _startup = new CancellationTokenSource();
        try
        {
            string? blocked = await _host.StartAsync(settings, AppContext.BaseDirectory, ShowStatus,
                ConfirmOutputs, RememberKneeboardView, _startup.Token);
            if (_closing || IsDisposed)
                return;
            ShowNotices(StartupChecks.Results);
            if (blocked is null)
            {
                _state.Text = (_createdDefaults
                        ? "Running, with default settings in settings.json beside the program. "
                        : "Running. ")
                    + "Leave this window open while you fly. Help explains each call.";
                SetState(RadioState.Running, "Radio on");
                _createdDefaults = false;
                _tabs.SelectedTab = _statusPage;
            }
            else
            {
                _state.Text = "Not running.";
                SetState(RadioState.Stopped, "Not running");
                _mission.Text = blocked;
                _track.Text = "";
                _you.Text = "";
                _tabs.SelectedTab = _settingsPage;
            }
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex) when (!_closing && !IsDisposed)
        {
            _state.Text = "The radio could not start.";
            SetState(RadioState.Error, "Error");
            _exchange.Text = ex.ToString();
            _tabs.SelectedTab = _statusPage;
        }
        finally
        {
            if (!_closing && !IsDisposed)
            {
                _apply.Enabled = true;
                _reset.Enabled = true;
            }
        }
    }

    private void ResetToDefaults()
    {
        if (MessageBox.Show(this, "Reset every setting to its default? Nothing is saved until you choose Save and apply.",
                "IL-2 Tactical Radio", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;
        Fill(new AppSettings());
        ShowProblems([]);
    }

    private void Fill(AppSettings settings)
    {
        _filling = true;
        try
        {
            var chat = settings.Chat ?? new ChatSettings();
            var keys = settings.Keys ?? new KeySettings();
            _il2Path.Text = settings.Il2Path ?? "";
            int percent = double.IsFinite(settings.Volume) ? (int)Math.Round(settings.Volume * 100) : 80;
            _volume.Value = Math.Clamp(percent, _volume.Minimum, _volume.Maximum);
            _volumeReadout.Text = $"{_volume.Value} %";
            ReloadVoices(settings.Voice);
            Select(_realism, settings.Realism, AppSettings.RealismModes);
            Select(_positions, settings.Positions, AppSettings.PositionsModes);
            var unasked = settings.Unasked ?? new UnaskedSettings();
            _unaskedRoute.Checked = unasked.Route;
            _unaskedContacts.Checked = unasked.Contacts;
            _unaskedLosses.Checked = unasked.Losses;
            _radioWait.Checked = settings.RadioWait;
            SetNumber(_radioWaitRatio, settings.RadioWaitRatio);
            SetNumber(_reminder, settings.RecordingReminderSeconds);
            Select(_chatOutput, chat.Output, ChatSettings.Outputs);
            var kneeboard = settings.Kneeboard ?? new KneeboardSettings();
            Select(_kneeboardView, kneeboard.View?.Trim().ToLowerInvariant(), ["small", "hidden", "large"]);
            Select(_kneeboardPosition, kneeboard.Position?.Trim().ToLowerInvariant(), KneeboardPlacement.Presets);
            _kneeboardCustomLoaded = kneeboard.Custom;
            _kneeboardScreen = kneeboard.CustomScreen;
            if (kneeboard.Custom is [int x, int y])
            {
                SetNumber(_kneeboardX, x);
                SetNumber(_kneeboardY, y);
            }
            EnableKneeboardCustom();
            SetNumber(_screen, chat.Screen);
            double[] area = chat.WindowArea is { Length: 4 } ? chat.WindowArea : [0.015, 0.40, 0.42, 0.30];
            SetNumber(_areaL, area[0]);
            SetNumber(_areaT, area[1]);
            SetNumber(_areaW, area[2]);
            SetNumber(_areaH, area[3]);
            SetNumber(_font, chat.FontPixels);
            _keepClear.Checked = chat.KeepClear;
            foreach (var (action, key) in keys.All())
                if (_keyBoxes.TryGetValue(action, out var box))
                    box.Text = key ?? "";
        }
        finally
        {
            _filling = false;
        }
    }

    private AppSettings ReadSettings() => new()
    {
        Il2Path = Blank(_il2Path.Text),
        Volume = _volume.Value / 100.0,
        Voice = string.IsNullOrWhiteSpace(_voice.Text) ? "auto" : _voice.Text.Trim(),
        Realism = string.IsNullOrWhiteSpace(_realism.Text) ? "pilot" : _realism.Text.Trim().ToLowerInvariant(),
        Positions = string.IsNullOrWhiteSpace(_positions.Text) ? "auto" : _positions.Text.Trim().ToLowerInvariant(),
        Unasked = new UnaskedSettings { Route = _unaskedRoute.Checked, Contacts = _unaskedContacts.Checked, Losses = _unaskedLosses.Checked },
        RadioWait = _radioWait.Checked,
        RadioWaitRatio = (double)_radioWaitRatio.Value,
        RecordingReminderSeconds = (int)_reminder.Value,
        Chat = new ChatSettings
        {
            Output = _chatOutput.Text,
            Screen = (int)_screen.Value,
            WindowArea = [(double)_areaL.Value, (double)_areaT.Value, (double)_areaW.Value, (double)_areaH.Value],
            FontPixels = (double)_font.Value,
            KeepClear = _keepClear.Checked,
        },
        Keys = new KeySettings
        {
            FriendlyPositions = Key("friendlyPositions"),
            FlightPosition = Key("flightPosition"),
            Rejoin = Key("rejoin"),
            EnemyContacts = Key("enemyContacts"),
            IdentifyContact = Key("identifyContact"),
            RoutePosition = Key("routePosition"),
            SteerHome = Key("steerHome"),
            SteerTarget = Key("steerTarget"),
            FixPosition = Key("fixPosition"),
            Kneeboard = Key("kneeboard"),
            Menu = Key("menu"),
            Older = Key("older"),
            Newer = Key("newer"),
            Quit = Key("quit"),
        },
        Kneeboard = ReadKneeboard(),
    };

    /// <summary>
    /// The kneeboard settings from the tab. Custom x, y are noted against the resolution in IL-2's startup.cfg when they
    /// are first set or changed, so they scale if IL-2's resolution changes later.
    /// </summary>
    private KneeboardSettings ReadKneeboard()
    {
        string view = string.IsNullOrWhiteSpace(_kneeboardView.Text) ? "small" : _kneeboardView.Text;
        string position = string.IsNullOrWhiteSpace(_kneeboardPosition.Text) ? KneeboardPlacement.Default : _kneeboardPosition.Text;
        if (position != "custom")
            return new KneeboardSettings { View = view, Position = position, Custom = _kneeboardCustomLoaded, CustomScreen = _kneeboardScreen };
        int[] custom = [(int)_kneeboardX.Value, (int)_kneeboardY.Value];
        bool unchanged = _kneeboardCustomLoaded is [var x, var y] && x == custom[0] && y == custom[1];
        return new KneeboardSettings { View = view, Position = position, Custom = custom, CustomScreen = unchanged ? _kneeboardScreen : GameResolution() };
    }

    /// <summary>IL-2's render size from its startup.cfg, or null when it can't be read.</summary>
    private int[]? GameResolution()
    {
        var install = Il2Install.Locate(Blank(_il2Path.Text));
        if (install is null || !File.Exists(install.StartupCfg))
            return null;
        try
        {
            var cfg = StartupConfig.Load(install.StartupCfg);
            bool fullscreen = cfg.Get("graphics", "fullscreen") == "1";
            return int.TryParse(cfg.Get("graphics", fullscreen ? "full_width" : "win_width"), out int w)
                   && int.TryParse(cfg.Get("graphics", fullscreen ? "full_height" : "win_height"), out int h)
                ? [w, h]
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void EnableKneeboardCustom()
    {
        bool custom = _kneeboardPosition.Text == "custom";
        _kneeboardX.Enabled = custom;
        _kneeboardY.Enabled = custom;
    }

    /// <summary>
    /// The kneeboard key changed the view in the game: remember it in settings.json for the next flight, changing nothing
    /// else in the file (edits not yet applied on this tab stay unsaved), and show it on the tab.
    /// </summary>
    private void RememberKneeboardView(KneeboardView view)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => RememberKneeboardView(view));
            return;
        }
        string name = KneeboardPlacement.ViewName(view);
        _filling = true;
        try { Select(_kneeboardView, name, ["small", "hidden", "large"]); }
        finally { _filling = false; }
        try
        {
            var (saved, problems) = AppSettings.Parse(File.ReadAllText(_settingsPath));
            if (problems.Count > 0 || saved.Kneeboard is null || saved.Kneeboard.View == name)
                return;
            File.WriteAllText(_settingsPath, (saved with { Kneeboard = saved.Kneeboard with { View = name } }).ToJson() + Environment.NewLine);
        }
        catch (IOException)
        {
            // Not kept for the next flight; the view still changed in the game.
        }
    }

    private void ReloadVoices(string? selected)
    {
        string current = selected ?? _voice.Text;
        _voice.Items.Clear();
        _voice.Items.Add("auto");
        _voice.Items.Add("off");
        try
        {
            string? typed = Blank(_il2Path.Text);
            var install = Il2Install.Locate(typed);
            foreach (var voice in VoiceSelection.Create("auto", install?.Root).Voices)
                if (!_voice.Items.Contains(voice.Pack.Name))
                    _voice.Items.Add(voice.Pack.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }

        if (!string.IsNullOrWhiteSpace(current) && !_voice.Items.Contains(current))
            _voice.Items.Add(current);
        _voice.Text = string.IsNullOrWhiteSpace(current) ? "auto" : current;
    }

    private void BrowseFolder(TextBox target, string description, bool reloadVoices)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, SelectedPath = target.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        target.Text = dialog.SelectedPath;
        if (reloadVoices)
            ReloadVoices(null);
    }

    private bool ConfirmOutputs(string question) => AskOnUi(() => PlayerDialogs.YesNo(this, "IL-2 exports", question));

    private T AskOnUi<T>(Func<T> ask)
    {
        if (!InvokeRequired)
            return ask();
        return (T)Invoke(ask)!;
    }

    private void ShowStatus(CompactStatus status)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowStatus(status));
            return;
        }

        _mission.Text = status.Mission;
        _track.Text = status.Track;
        _you.Text = status.You;
        _chat.Text = status.Chat ?? "";
        _audio.Text = status.Audio ?? "";
        ShowNotices(status.Notices);
        _exchange.Text = status.Exchange;
        _notes.Text = string.Join(Environment.NewLine, status.Notes);
    }

    private void ShowNotices(IReadOnlyList<string> lines) => _notices.Text = string.Join(Environment.NewLine, lines);

    private void ShowProblems(IReadOnlyList<string> problems)
    {
        if (problems.Count == 0)
        {
            _problems.Visible = false;
            _problems.Text = "";
            return;
        }

        _problems.Text = string.Join(Environment.NewLine, problems);
        _problems.Visible = true;
    }

    private void OnSessionEnded(Exception? error)
    {
        if (IsDisposed)
            return;
        BeginInvoke(() =>
        {
            if (IsDisposed || _closing)
                return;
            if (error is not null)
            {
                _state.Text = "The radio stopped because of an error.";
                SetState(RadioState.Error, "Error");
                _exchange.Text = error.ToString();
                return;
            }

            Close();
        });
    }

    private static void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (e.KeyCode is Keys.Back or Keys.Delete or Keys.Escape)
        {
            box.Clear();
            return;
        }

        if (e.KeyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.Tab)
            return;
        string label = VirtualKeys.Label((int)e.KeyCode);
        if (!label.StartsWith("key 0x", StringComparison.Ordinal))
            box.Text = label;
    }

    private string? Key(string action) => Blank(_keyBoxes[action].Text);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static void SetNumber(NumericUpDown box, double value)
    {
        if (!double.IsFinite(value))
            return;
        try
        {
            var number = (decimal)value;
            if (number < box.Minimum || number > box.Maximum)
                return;
            box.Value = number;
        }
        catch (OverflowException)
        {
        }
    }

    private static void Select(ComboBox box, string? value, string[] known)
    {
        value = string.IsNullOrWhiteSpace(value) ? known.FirstOrDefault() ?? "" : value;
        if (!box.Items.Contains(value))
            box.Items.Add(value);
        box.SelectedItem = value;
    }

    private void Hint(string text, ref int y)
    {
        int lines = Math.Max(1, (int)Math.Ceiling(text.Length / 78.0));
        var label = new Label
        {
            Text = text,
            Left = 216,
            Top = y,
            Width = 480,
            Height = lines * 16,
            ForeColor = Brand.Muted,
        };
        _settingsPage.Controls.Add(label);
        y += label.Height + 6;
    }

    private static readonly Color KeyFocus = Color.FromArgb(0xFB, 0xEF, 0xD6);

    /// <summary>Start-up checks, the last radio call and notes: white boxes in a monospaced face, like a radio log.</summary>
    private static TextBox ReadOnlyBox() => new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        TabStop = false,
        BackColor = Color.White,
        ForeColor = Brand.Ink,
        Font = Brand.Mono(9f),
    };

    private static NumericUpDown Number(decimal min, decimal max, decimal value, decimal increment, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        DecimalPlaces = decimals,
        Increment = increment,
        Value = value,
        Width = 80,
    };

    private static NumericUpDown Fraction(decimal value) => new()
    {
        Minimum = 0,
        Maximum = 1,
        DecimalPlaces = 3,
        Increment = 0.01m,
        Value = value,
        Width = 70,
    };
}
