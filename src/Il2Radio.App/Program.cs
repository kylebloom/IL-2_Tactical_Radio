namespace Il2Radio.App;

/// <summary>
/// IL-2 Tactical Radio: double-click Il2Radio.exe (it asks for administrator rights), then fly.
/// Everything lives beside the program. An elevated start from Explorer runs in System32, so the
/// working directory is never used.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var single = new Mutex(initiallyOwned: true, @"Local\Il2Radio.App", out bool first);
        if (!first)
        {
            MessageBox.Show("The radio is already running in another window.", "IL-2 Tactical Radio",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new MainForm(Path.Combine(AppContext.BaseDirectory, "settings.json")));
    }
}
