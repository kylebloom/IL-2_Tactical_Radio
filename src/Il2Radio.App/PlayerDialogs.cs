using Il2Radio.Core.Audio;

namespace Il2Radio.App;

/// <summary>Yes/no questions that used to wait for a typed "y" on the console. No is the default.</summary>
internal static class PlayerDialogs
{
    public static bool YesNo(IWin32Window owner, string title, string text)
    {
        using var dialog = new ConsentDialog(title, text);
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }

    /// <summary>
    /// The German-voice question: Yes (download now), No (don't ask again), Later (ask next start).
    /// Closing the window or pressing Esc is Later, so nothing is downloaded and nothing is decided.
    /// </summary>
    public static PiperAnswer YesNoLater(IWin32Window owner, string title, string text)
    {
        using var dialog = new ConsentDialog(title, text, offerLater: true);
        return dialog.ShowDialog(owner) switch
        {
            DialogResult.Yes => PiperAnswer.Yes,
            DialogResult.No => PiperAnswer.No,
            _ => PiperAnswer.Later,
        };
    }
}

/// <summary>
/// The startup.cfg and German-voice questions. Yes is a button, not Enter, so a stray key
/// neither edits the game's config nor starts a download.
/// </summary>
internal sealed class ConsentDialog : Form
{
    public ConsentDialog(string title, string text, bool offerLater = false)
    {
        Text = title;
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 320);

        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = text,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
        };
        var no = new Button { Text = offerLater ? "No, don't ask again" : "No", DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new Size(88, 28) };
        var yes = new Button { Text = offerLater ? "Yes, download" : "Yes", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(88, 28) };
        var later = new Button { Text = "Ask me later", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(88, 28) };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        if (offerLater)
        {
            buttons.Controls.Add(later);
            ClientSize = new Size(600, 260);
        }
        buttons.Controls.Add(no);
        buttons.Controls.Add(yes);
        Controls.Add(box);
        Controls.Add(buttons);
        CancelButton = offerLater ? later : no;
    }
}

/// <summary>Shown while the German phrase voice downloads. It does not offer a way to treat a partial file as installed.</summary>
internal sealed class DownloadForm : Form
{
    private readonly Label _label;
    private readonly ProgressBar _bar;

    public DownloadForm()
    {
        Text = "German phrase voice";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 108);
        _label = new Label { Left = 16, Top = 16, Width = 428, Height = 40 };
        _bar = new ProgressBar { Left = 16, Top = 64, Width = 428, Height = 22, Minimum = 0, Maximum = 100 };
        Controls.Add(_label);
        Controls.Add(_bar);
    }

    public void Report(PiperProgress progress)
    {
        if (IsDisposed)
            return;
        _label.Text = progress.ToString();
        _bar.Value = Math.Clamp(progress.Percent, 0, 100);
    }
}
