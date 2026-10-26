using Il2Radio.Core;
using Il2Radio.Core.Audio;
using Il2Radio.Core.Input;
using Il2Radio.Core.Osd;
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
        ("Menu", "menu"),
        ("Older log", "older"),
        ("Newer log", "newer"),
        ("Quit", "quit"),
    ];

    private readonly string _settingsPath;
    private readonly FlightHost _host = new();
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage _statusPage = new("Status") { Padding = new Padding(12) };
    private readonly TabPage _settingsPage = new("Settings") { AutoScroll = true, Padding = new Padding(8) };
    private readonly Label _state = new() { Dock = DockStyle.Top, Height = 52, Text = "Starting…", Padding = new Padding(0, 0, 8, 8) };
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
        ForeColor = Color.DarkRed,
    };

    private readonly TextBox _il2Path = new();
    private readonly TrackBar _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 10, Width = 220, Height = 32 };
    private readonly Label _volumeReadout = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly ComboBox _voice = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 280 };
    private readonly ComboBox _realism = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly CheckBox _radioWait = new() { Text = "Hold replies while IL-2's own radio is talking", AutoSize = true };
    private readonly NumericUpDown _radioWaitRatio = Number(1.1m, 10m, 1.8m, 0.1m, 1);
    private readonly NumericUpDown _reminder = Number(0, 600, 60, 5);
    private readonly ComboBox _chatOutput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly ComboBox _rtssPosition = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 200 };
    private readonly NumericUpDown _rtssMargin = Number(0, 500, 32, 1);
    private readonly NumericUpDown _rtssColumns = Number(RtssPlacement.MinColumns, 200, 72, 1);
    private readonly CheckBox _zoomFromRtss = new() { Text = "Use the zoom from IL-2's RTSS profile", AutoSize = true, Checked = true };
    private readonly NumericUpDown _rtssZoom = Number(1, 20, 1, 1);
    private readonly CheckBox _cellEstimate = new() { Text = "Use the estimated character size (9×16)", AutoSize = true, Checked = true };
    private readonly NumericUpDown _cellW = Number(1, 64, 9, 1);
    private readonly NumericUpDown _cellH = Number(1, 64, 16, 1);
    private readonly NumericUpDown _screen = Number(0, 8, 0, 1);
    private readonly NumericUpDown _areaL = Fraction(0.015m);
    private readonly NumericUpDown _areaT = Fraction(0.40m);
    private readonly NumericUpDown _areaW = Fraction(0.42m);
    private readonly NumericUpDown _areaH = Fraction(0.30m);
    private readonly NumericUpDown _font = Number(6, 96, 16, 1);
    private readonly CheckBox _keepClear = new() { Text = "Keep the text out of IL-2's own message line and subtitles", AutoSize = true, Checked = true };
    private readonly Dictionary<string, TextBox> _keyBoxes = new(StringComparer.Ordinal);
    private readonly CheckBox _piperEnabled = new() { Text = "Use it for words the game's pilots can't say", AutoSize = true };
    private readonly ComboBox _piperVoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    private readonly TextBox _piperFolder = new();
    private readonly NumericUpDown _piperPace = Number(0.5m, 2m, 0.9m, 0.05m, 2);

    private readonly Button _apply = new() { Text = "Save and apply", Width = 120, Height = 28 };
    private readonly Button _reset = new() { Text = "Reset to defaults", Width = 140, Height = 28 };
    private readonly Button _quit = new() { Text = "Quit", Width = 88, Height = 28 };

    private bool _shown;
    private bool _closing;
    private bool _filling;
    private bool _showDownload;
    private bool _createdDefaults;
    private CancellationTokenSource? _startup;
    private int _settingsBottom;
    private DownloadForm? _download;

    public MainForm(string settingsPath)
    {
        _settingsPath = settingsPath;
        Text = "IL-2 Tactical Radio";
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 640);
        MinimumSize = new Size(700, 520);
        _host.Ended += OnSessionEnded;
        BuildStatus();
        BuildSettings();
        BuildChrome();
        _volume.ValueChanged += (_, _) =>
        {
            _volumeReadout.Text = $"{_volume.Value} %";
            if (!_filling && _host.Voice is { } voice)
                voice.Volume = _volume.Value / 100.0;
        };
        _zoomFromRtss.CheckedChanged += (_, _) => _rtssZoom.Enabled = !_zoomFromRtss.Checked;
        _cellEstimate.CheckedChanged += (_, _) =>
        {
            _cellW.Enabled = !_cellEstimate.Checked;
            _cellH.Enabled = !_cellEstimate.Checked;
        };
        _apply.Click += async (_, _) => await SaveAndStartAsync(writeFile: true);
        _reset.Click += (_, _) => ResetToDefaults();
        _quit.Click += (_, _) => Close();
        _apply.Enabled = false;
        _reset.Enabled = false;
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_shown)
            return;
        _shown = true;
        await LoadAndStartAsync();
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
            Height = 48,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.Add(_apply);
        buttons.Controls.Add(_reset);
        buttons.Controls.Add(_quit);
        _tabs.TabPages.Add(_statusPage);
        _tabs.TabPages.Add(_settingsPage);
        Controls.Add(_tabs);
        Controls.Add(_problems);
        Controls.Add(buttons);
    }

    private void BuildStatus()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        string[] captions = ["Mission", "Track", "You", "Chat", "Audio"];
        Label[] values = [_mission, _track, _you, _chat, _audio];
        for (int i = 0; i < captions.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = captions[i], AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 4) }, 0, i);
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

    private static void AddSpan(TableLayoutPanel grid, string text, int row)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Padding = new Padding(0, 8, 0, 2) };
        grid.Controls.Add(label, 0, row);
        grid.SetColumnSpan(label, 2);
    }

    private void BuildSettings()
    {
        int y = 8;
        void Section(string title)
        {
            y += 8;
            var label = new Label { Text = title, AutoSize = true, Left = 8, Top = y, Font = new Font(Font, FontStyle.Bold) };
            _settingsPage.Controls.Add(label);
            y += 24;
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
        Hint("auto uses the game's pilot voices by nation. tts is always Windows text-to-speech. Or a voice pack's name.", ref y);
        _realism.Items.AddRange(AppSettings.RealismModes);
        Row("Realism", _realism, 160);
        Hint("pilot: he reports a position relative to you only when he can see you or is with you. Otherwise your flight gives its heading and altitude, and a pilot in a fight says the enemy is on him. precise: bearings and ranges from the track.", ref y);
        Check(_radioWait);
        Row("Talking threshold", _radioWaitRatio, 80);
        Hint("How far above its engine IL-2's audio must peak to count as talking. More than 1.", ref y);
        Row("Recording reminder (s)", _reminder, 80);
        Hint("Remind you to press LCtrl+R this long into a mission with no track. 0 never reminds.", ref y);

        Section("Radio text");
        _chatOutput.Items.AddRange(ChatSettings.Outputs);
        Row("Show text", _chatOutput, 160);
        Hint("rtss is inside the game, including fullscreen. overlay needs windowed IL-2. window is an ordinary window. off shows nothing.", ref y);
        foreach (string preset in new[] { "left-middle", "top-left", "top-right", "bottom-left", "bottom-right", "default" })
            _rtssPosition.Items.Add(preset);
        Row("RTSS position", _rtssPosition, 200);
        Hint("A preset, or X,Y in RTSS pixels. default leaves the position to RTSS.", ref y);
        Row("RTSS margin (px)", _rtssMargin, 80);

        Section("Advanced chat layout");
        Row("RTSS line width", _rtssColumns, 80);
        Check(_zoomFromRtss);
        Row("RTSS zoom", _rtssZoom, 80);
        Check(_cellEstimate);
        var cellRow = new FlowLayoutPanel { Height = 28, WrapContents = false };
        cellRow.Controls.Add(_cellW);
        cellRow.Controls.Add(new Label { Text = "×", AutoSize = true, Padding = new Padding(4, 4, 4, 0) });
        cellRow.Controls.Add(_cellH);
        Row("Character size (px)", cellRow, 220, 28);
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
            var box = new TextBox { ReadOnly = true, ShortcutsEnabled = false, BackColor = SystemColors.Window };
            box.KeyDown += CaptureKey;
            box.GotFocus += (_, _) => box.BackColor = SystemColors.Info;
            box.LostFocus += (_, _) => box.BackColor = SystemColors.Window;
            _keyBoxes[action] = box;
            Row(label, box, 120);
        }

        Section("German phrase voice");
        foreach (var voice in PiperCatalog.Voices)
            _piperVoice.Items.Add(voice.Name);
        Check(_piperEnabled);
        Hint("Off unless you turn it on. The first time, you are asked what would be downloaded, from where and how big. Nothing is downloaded on No.", ref y);
        Row("Voice", _piperVoice, 280);
        var piperPath = new FlowLayoutPanel { Height = 28, WrapContents = false };
        _piperFolder.Width = 340;
        var browsePiper = new Button { Text = "Browse…", Width = 88, Height = 24 };
        browsePiper.Click += (_, _) => BrowseFolder(_piperFolder, "Where to store the German voice (not inside the IL-2 folder)", reloadVoices: false);
        piperPath.Controls.Add(_piperFolder);
        piperPath.Controls.Add(browsePiper);
        Row("Folder", piperPath, 440, 28);
        Hint("Empty stores it in a piper folder beside the program, never in the IL-2 folder.", ref y);
        Row("Speaking pace", _piperPace, 80);
        Hint("Below 1 is faster. Radio calls are brisk.", ref y);

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
        _showDownload = true;
        _startup?.Dispose();
        _startup = new CancellationTokenSource();
        try
        {
            string? blocked = await _host.StartAsync(settings, AppContext.BaseDirectory, ShowStatus,
                ConfirmOutputs, ConfirmPiper, OnPiperProgress, OnPiperLog, _startup.Token);
            if (_closing || IsDisposed)
                return;
            ShowNotices(StartupChecks.Results);
            if (blocked is null)
            {
                _state.Text = (_createdDefaults
                        ? "Running, with default settings in settings.json beside the program. "
                        : "Running. ")
                    + "Leave this window open while you fly. Numpad . quits.";
                _createdDefaults = false;
                _tabs.SelectedTab = _statusPage;
            }
            else
            {
                _state.Text = "Not running.";
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
            _exchange.Text = ex.ToString();
            _tabs.SelectedTab = _statusPage;
        }
        finally
        {
            _showDownload = false;
            if (!IsDisposed)
                CloseDownload();
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
            var piper = settings.Piper ?? new PiperSettings();
            _il2Path.Text = settings.Il2Path ?? "";
            int percent = double.IsFinite(settings.Volume) ? (int)Math.Round(settings.Volume * 100) : 80;
            _volume.Value = Math.Clamp(percent, _volume.Minimum, _volume.Maximum);
            _volumeReadout.Text = $"{_volume.Value} %";
            ReloadVoices(settings.Voice);
            Select(_realism, settings.Realism, AppSettings.RealismModes);
            _radioWait.Checked = settings.RadioWait;
            SetNumber(_radioWaitRatio, settings.RadioWaitRatio);
            SetNumber(_reminder, settings.RecordingReminderSeconds);
            Select(_chatOutput, chat.Output, ChatSettings.Outputs);
            if (!_rtssPosition.Items.Contains(chat.RtssPosition))
                _rtssPosition.Items.Add(chat.RtssPosition);
            _rtssPosition.Text = chat.RtssPosition;
            SetNumber(_rtssMargin, chat.RtssMargin);
            SetNumber(_rtssColumns, chat.RtssColumns);
            _zoomFromRtss.Checked = chat.RtssZoom is null;
            if (chat.RtssZoom is int zoom)
                SetNumber(_rtssZoom, zoom);
            _rtssZoom.Enabled = !_zoomFromRtss.Checked;
            _cellEstimate.Checked = chat.RtssCell is not { Length: 2 };
            if (chat.RtssCell is [int w, int h])
            {
                SetNumber(_cellW, w);
                SetNumber(_cellH, h);
            }
            _cellW.Enabled = !_cellEstimate.Checked;
            _cellH.Enabled = !_cellEstimate.Checked;
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
            _piperEnabled.Checked = piper.Enabled;
            Select(_piperVoice, piper.Voice, PiperCatalog.Voices.Select(v => v.Name).ToArray());
            _piperFolder.Text = piper.Folder ?? "";
            SetNumber(_piperPace, piper.LengthScale);
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
        RadioWait = _radioWait.Checked,
        RadioWaitRatio = (double)_radioWaitRatio.Value,
        RecordingReminderSeconds = (int)_reminder.Value,
        Chat = new ChatSettings
        {
            Output = _chatOutput.Text,
            RtssPosition = string.IsNullOrWhiteSpace(_rtssPosition.Text) ? "left-middle" : _rtssPosition.Text.Trim(),
            RtssMargin = (int)_rtssMargin.Value,
            RtssColumns = (int)_rtssColumns.Value,
            RtssZoom = _zoomFromRtss.Checked ? null : (int)_rtssZoom.Value,
            RtssCell = _cellEstimate.Checked ? null : [(int)_cellW.Value, (int)_cellH.Value],
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
            Menu = Key("menu"),
            Older = Key("older"),
            Newer = Key("newer"),
            Quit = Key("quit"),
        },
        Piper = new PiperSettings
        {
            Enabled = _piperEnabled.Checked,
            Voice = _piperVoice.Text,
            Folder = Blank(_piperFolder.Text),
            LengthScale = (double)_piperPace.Value,
        },
    };

    private void ReloadVoices(string? selected)
    {
        string current = selected ?? _voice.Text;
        _voice.Items.Clear();
        _voice.Items.Add("auto");
        _voice.Items.Add("tts");
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

    private bool ConfirmPiper(IReadOnlyList<string> lines) =>
        AskOnUi(() => PlayerDialogs.YesNo(this, "German phrase voice", string.Join(Environment.NewLine, lines)));

    private bool AskOnUi(Func<bool> ask)
    {
        if (!InvokeRequired)
            return ask();
        return (bool)Invoke(ask)!;
    }

    private void OnPiperProgress(PiperProgress progress)
    {
        if (IsDisposed || !_showDownload)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(() => OnPiperProgress(progress));
            return;
        }

        _download ??= new DownloadForm();
        if (!_download.Visible)
            _download.Show(this);
        _download.Report(progress);
    }

    private void OnPiperLog(string line)
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            Invoke(() => OnPiperLog(line));
            return;
        }

        StartupChecks.Info(line);
        ShowNotices(StartupChecks.Results);
    }

    private void CloseDownload()
    {
        if (_download is null)
            return;
        _download.Close();
        _download.Dispose();
        _download = null;
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
            ForeColor = SystemColors.GrayText,
        };
        _settingsPage.Controls.Add(label);
        y += label.Height + 6;
    }

    private static TextBox ReadOnlyBox() => new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        TabStop = false,
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
