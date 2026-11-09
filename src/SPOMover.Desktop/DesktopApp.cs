using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SPOMover.Core.Localization;
using SPOMover.Engine;
using SPOMover.Host;
using SPOMover.State;
using SPOMover.Ui.Services;

namespace SPOMover.Desktop;

/// <summary>
/// The desktop application: the engine, its state and the local API for the CLI run inside this process,
/// the GUI is the same set of pages shown in a WebView2 window. One instance per data folder.
/// </summary>
public static class DesktopApp
{
    /// <param name="configureServices">Lets tools replace engine services (the demo with the fake Graph).</param>
    public static int Run(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        string? selftest = Option(args, "--selftest");
        string dataDir = HostApp.DataDirectory(Option(args, "--data"));
        string key = "SPOMover-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDir.ToUpperInvariant())))[..16];
        // A crash outside the window's dispatcher (start of WebView2, a background thread) leaves only "has stopped working":
        // the exception goes to logs\crash-*.txt and a message names that file.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(dataDir, e.ExceptionObject as Exception, fatal: true);

        using var mutex = new Mutex(true, @"Local\" + key, out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "-show");
        if (!first)
        {
            // Already running on these data: bring that window forward instead of a second engine on the same database.
            showSignal.Set();
            return 0;
        }

        string[] hostArgs = [.. Without(args, "--selftest"), .. Option(args, "--data") is null ? new[] { "--data", dataDir } : []];
        var (host, apiWarning) = StartHost(hostArgs, configureServices);
        // The self-test finds the pages by their Ukrainian text; the choice saved in the data folder is not changed.
        if (selftest is not null)
            L.Set(L.Ukrainian);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow? window = null;
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddMudServices();
        // The pages use the same engine objects as the local API: one state, one active run.
        services.AddSingleton(_ => host.Services.GetRequiredService<StateStore>());
        services.AddSingleton(_ => host.Services.GetRequiredService<RunService>());
        services.AddSingleton(_ => host.Services.GetRequiredService<SpoBrowser>());
        services.AddSingleton(_ => host.Services.GetRequiredService<SettingsService>());
        services.AddSingleton(_ => host.Services.GetRequiredService<StatusService>());
        services.AddSingleton<IFileSaver>(new WpfFileSaver(() => window));

        window = new MainWindow(host, services.BuildServiceProvider(), dataDir, apiWarning, selftest);
        app.DispatcherUnhandledException += (_, e) =>
        {
            ReportCrash(dataDir, e.Exception, fatal: false);
            e.Handled = true;
        };
        ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => app.Dispatcher.BeginInvoke(window.Reveal), null, Timeout.Infinite, false);
        window.Show();
        return app.Run();
    }

    /// <summary>
    /// Starts the engine and the local API. If the port is taken (another user of this machine runs the app, or another
    /// data folder), the API takes a free port: the CLI finds it in api.json of the data folder.
    /// </summary>
    private static (WebApplication Host, string? Warning) StartHost(string[] args, Action<IServiceCollection>? configure)
    {
        var host = HostApp.Build(args, configure);
        try
        {
            host.StartAsync().GetAwaiter().GetResult();
            return (host, null);
        }
        catch (IOException ex)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            string? requested = Option(args, "--urls");
            var fallback = HostApp.Build([.. Without(args, "--urls"), "--urls", "http://127.0.0.1:0"], configure);
            fallback.StartAsync().GetAwaiter().GetResult();
            // The default port is just taken by someone else: nothing to tell, the CLI finds the new port by itself.
            if (requested is null)
                return (fallback, null);
            string url = fallback.Services.GetRequiredService<ApiAccess>().Url ?? "?";
            return (fallback, L.T($"The address {requested} is taken ({ex.Message}). The local API listens on {url}: the CLI of this user finds it by itself; in scheduled tasks with an explicit --host, give the new address or remove --host."));
        }
    }

    private static void ReportCrash(string dataDir, Exception? ex, bool fatal)
    {
        string? file = null;
        try
        {
            string logs = Path.Combine(dataDir, "logs");
            Directory.CreateDirectory(logs);
            file = Path.Combine(logs, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, $"{DateTime.Now:O}\r\n{Environment.OSVersion} {(Environment.Is64BitProcess ? "x64" : "x86")}\r\n"
                + $"SPOMover {typeof(DesktopApp).Assembly.GetName().Version}\r\n\r\n{ex}");
        }
        catch
        {
            file = null; // the message still says what happened
        }
        string message = ex?.Message ?? L.T("unknown error");
        string text = fatal ? L.T($"The application is shutting down after an error: {message}") : L.T($"Unexpected error: {message}");
        if (file is not null)
            text += "\n\n" + L.T($"Details: {file}");
        try
        {
            MessageBox.Show(text, "SPOMover", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // no UI on this thread or the window is gone: the file stays
        }
    }

    internal static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static IEnumerable<string> Without(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == name)
            {
                i++;
                continue;
            }
            yield return args[i];
        }
    }
}
