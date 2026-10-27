namespace Il2Radio.App;

/// <summary>Yes/no questions that used to wait for a typed "y" on the console. No is the default.</summary>
internal static class PlayerDialogs
{
    public static bool YesNo(IWin32Window owner, string title, string text)
    {
        using var dialog = new ConsentDialog(title, text);
        return dialog.ShowDialog(owner) == DialogResult.Yes;
    }
}

/// <summary>
/// The startup.cfg question. Yes is a button, not Enter, so a stray key does not edit the game's config.
/// </summary>
internal sealed class ConsentDialog : Form
{
    public ConsentDialog(string title, string text)
    {
        Text = title;
        Icon = Brand.AppIcon() ?? Icon;
        Font = Brand.Ui(9f);
        BackColor = Brand.Paper;
        ForeColor = Brand.Ink;
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
            BackColor = Brand.Paper,
            ForeColor = Brand.Ink,
        };
        var no = new Button { Text = "No", DialogResult = DialogResult.No, AutoSize = true, MinimumSize = new Size(88, 30) };
        var yes = new Button { Text = "Yes", DialogResult = DialogResult.Yes, AutoSize = true, MinimumSize = new Size(88, 30) };
        // Both plain: the dialog asks for consent, so neither answer is pushed.
        Brand.Secondary(no);
        Brand.Secondary(yes);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 50,
            Padding = new Padding(10),
            BackColor = Brand.Footer,
        };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 12, 8) };
        body.Controls.Add(box);
        buttons.Controls.Add(no);
        buttons.Controls.Add(yes);
        Controls.Add(body);
        Controls.Add(buttons);
        CancelButton = no;
        Shown += (_, _) => box.Select(0, 0);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Brand.DarkCaption(Handle);
    }
}
