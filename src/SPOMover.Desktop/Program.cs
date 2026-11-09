namespace SPOMover.Desktop;

public static class Program
{
    /// <summary>
    /// Arguments: --data &lt;folder&gt; (state, logs, temp; default %LOCALAPPDATA%\SPOMover),
    /// --urls &lt;url&gt; (local API for the CLI; default http://localhost:5090), --selftest &lt;file&gt; (start, check the window, exit).
    /// </summary>
    [STAThread]
    public static int Main(string[] args) => DesktopApp.Run(args);
}
