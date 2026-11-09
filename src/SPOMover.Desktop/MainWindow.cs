using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SPOMover.Core.Localization;
using SPOMover.Engine;
using SPOMover.State;
using SPOMover.Ui.Components;
using Forms = System.Windows.Forms;

namespace SPOMover.Desktop;

/// <summary>
/// Main window: the GUI in WebView2 and a tray icon. Closing the window during a run asks whether to keep the run going
/// in the tray or to stop it gracefully (STA-4); a stopped run continues from the same place after the next start.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly WebApplication _host;
    private readonly RunService _runs;
    private readonly BlazorWebView _webView;
    private readonly Forms.NotifyIcon _tray;
    private readonly string? _selftest;
    private readonly string _dataDir;
    private readonly DispatcherTimer _releaseSize;
    private WindowState _shownState = WindowState.Maximized;
    private bool _exiting;
    private bool _trayHintShown;
    private string? _languageScript;

    // The icon of this assembly, also when another program (the demo tool) hosts the window.
    private static readonly Uri IconUri = new("pack://application:,,,/SPOMover;component/app.ico");

    public MainWindow(WebApplication host, IServiceProvider services, string dataDir, string? apiWarning, string? selftest)
    {
        _host = host;
        _runs = host.Services.GetRequiredService<RunService>();
        _selftest = selftest;
        _dataDir = dataDir;
        Title = "SPOMover";
        // Opens maximized: a window taller than the screen put the title bar out of sight. The restored size fits the work area too.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1400, work.Width);
        Height = Math.Min(900, work.Height);
        MinWidth = Math.Min(900, work.Width);
        MinHeight = Math.Min(600, work.Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowState = WindowState.Maximized;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized)
                _shownState = WindowState;
        };
        // A frame of the icon decoder, not BitmapImage: the window then takes the 16/20/24 px frame for the title bar
        // and a large one for Alt+Tab, instead of scaling the first frame.
        Icon = BitmapFrame.Create(IconUri);

        _webView = new BlazorWebView { HostPage = Path.Combine("wwwroot", "index.html"), Services = services };
        _webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        Content = _webView;

        using var iconStream = Application.GetResourceStream(IconUri)!.Stream;
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "SPOMover",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        BuildTrayMenu();
        _tray.DoubleClick += (_, _) => Reveal();
        // The user can switch the language in the pages: the tray menu and the static host page follow.
        L.Changed += OnLanguageChanged;

        // RDP reconnect changes the display: the WebView2 composition control recreates its D3D device, and a WM_SIZE that
        // arrives while it does (the creation pumps messages) resizes the capture with the released device — an access
        // violation that kills the process (WebView2 SDK 1.0.4258/4314). The control keeps its size until the display settles.
        _releaseSize = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _releaseSize.Tick += (_, _) =>
        {
            _releaseSize.Stop();
            _webView.Width = double.NaN;
            _webView.Height = double.NaN;
        };
        SystemEvents.DisplaySettingsChanging += HoldWebViewSize;
        SystemEvents.DisplaySettingsChanged += HoldWebViewSize;

        _webView.BlazorWebViewInitialized += async (_, _) => await SetPageLanguageAsync();
        if (selftest is not null)
            _webView.BlazorWebViewInitialized += async (_, _) => await SelfTestAsync();
        else if (apiWarning is not null)
            Loaded += (_, _) => MessageBox.Show(this, apiWarning, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void BuildTrayMenu()
    {
        var items = _tray.ContextMenuStrip!.Items;
        items.Clear();
        items.Add(L.T("Open"), null, (_, _) => Reveal());
        items.Add(L.T($"Data folder: {_dataDir}"), null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", _dataDir));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(L.T("Exit"), null, (_, _) => RequestExit());
    }

    // L.Changed comes from the thread of the page that switched the language.
    private void OnLanguageChanged() => _ = Dispatcher.InvokeAsync(() =>
    {
        if (_exiting)
            return;
        BuildTrayMenu();
        _ = SetPageLanguageAsync();
    });

    /// <summary>
    /// index.html is static: its "Loading…" and error bar are switched by a script in the page. The language is set
    /// before every load of the page (a reload from the error bar too) and in the page already shown.
    /// </summary>
    private async Task SetPageLanguageAsync()
    {
        var core = _webView.WebView?.CoreWebView2;
        if (core is null)
            return;
        string lang = JsonSerializer.Serialize(L.Language);
        try
        {
            if (_languageScript is not null)
                core.RemoveScriptToExecuteOnDocumentCreated(_languageScript);
            _languageScript = await core.AddScriptToExecuteOnDocumentCreatedAsync($"window.spoLang = {lang};");
            await core.ExecuteScriptAsync($"window.spoLang = {lang}; if (window.spoSetLang) window.spoSetLang({lang});");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // the web view is closing
        }
    }

    private void HoldWebViewSize(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        if (!_releaseSize.IsEnabled && _webView.ActualWidth > 0 && _webView.ActualHeight > 0)
        {
            _webView.Width = _webView.ActualWidth;
            _webView.Height = _webView.ActualHeight;
        }
        _releaseSize.Stop();
        _releaseSize.Start(); // three seconds after the last change
    });

    public void Reveal()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _shownState; // back to maximized if it was
        Activate();
    }

    private bool RunIsActive(out RunService.ActiveRun? active)
    {
        active = _runs.Active;
        return active is not null && !active.Completion.IsCompleted;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exiting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (!RunIsActive(out var active))
        {
            _ = ExitAsync();
            return;
        }
        var answer = MessageBox.Show(this,
            L.T($"Run {active!.RunId} is in progress.\n\nYes — minimize to the tray: the run goes on, the window opens from the icon next to the clock.\nNo — stop the run and exit: current files finish, the rest stays in the queue and continues after the next start.\nCancel — stay in the window."),
            Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes)
            HideToTray();
        else if (answer == MessageBoxResult.No)
            _ = ExitAsync();
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(5000, "SPOMover", L.T("The run goes on in the background. Double-click the icon to open the window."), Forms.ToolTipIcon.Info);
        }
    }

    private void RequestExit()
    {
        if (RunIsActive(out var active)
            && MessageBox.Show(this, L.T($"Stop run {active!.RunId} and exit? Current files finish, the rest continues after the next start."),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _ = ExitAsync();
    }

    /// <summary>Graceful stop of the active run (up to 30 s for current files), then the engine and the window.</summary>
    private async Task ExitAsync(int code = 0)
    {
        if (_exiting)
            return;
        _exiting = true;
        Title = L.T("SPOMover — stopping…");
        IsEnabled = false;
        try
        {
            await _host.StopAsync();
            await _host.DisposeAsync();
        }
        finally
        {
            L.Changed -= OnLanguageChanged;
            SystemEvents.DisplaySettingsChanging -= HoldWebViewSize;
            SystemEvents.DisplaySettingsChanged -= HoldWebViewSize;
            _releaseSize.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            Close();
            Application.Current.Shutdown(code);
        }
    }

    /// <summary>
    /// --selftest: the window starts, the pages render and the main sections open; the result goes to a file.
    /// Lets the build be checked on a machine without anyone looking at the screen.
    /// </summary>
    private async Task SelfTestAsync()
    {
        var log = new List<string>();
        bool ok = true;
        async Task<string> Eval(string js)
        {
            string raw = await _webView.WebView.CoreWebView2.ExecuteScriptAsync(js);
            return JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
        }
        async Task<bool> WaitFor(string what, string js)
        {
            for (int i = 0; i < 120; i++)
            {
                if (await Eval(js) is { Length: > 0 } found)
                {
                    log.Add($"ok   {what}: {found}");
                    await File.WriteAllLinesAsync(_selftest!, log);
                    return true;
                }
                await Task.Delay(250);
            }
            log.Add($"FAIL {what}");
            await File.WriteAllLinesAsync(_selftest!, log);
            return false;
        }

        ok &= await WaitFor("dashboard", "document.querySelector('.mud-appbar') ? document.querySelector('.mud-main-content').innerText.substring(0, 60).replace(/\\s+/g, ' ') : ''");
        ok &= await WaitFor("MudBlazor styles", "getComputedStyle(document.querySelector('.mud-appbar')).position === 'fixed' ? 'applied' : ''");
        // The home page shows the last run, when the data folder has one: its charts (uPlot, through JS) draw and a picture is saved.
        if (await Eval("document.querySelector('.mud-main-content').innerText.trimStart().startsWith('Прогін') ? 'yes' : ''") == "yes") // i18n-ignore: text of the page
        {
            ok &= await WaitFor("run charts", "document.querySelectorAll('.uplot').length >= 2 ? 'drawn' : (document.querySelector('.mud-main-content').innerText.includes('Графіки з\\'являться') ? 'no snapshots yet' : '')"); // i18n-ignore: text of the page
            await CaptureAsync(Path.ChangeExtension(_selftest!, ".dashboard.png"));
            ok &= await WaitFor("breakdown by library", "(() => { const r = document.querySelector('.breakdown-row'); if (r) r.scrollIntoView(); return r ? 'yes' : ''; })()");
            await CaptureAsync(Path.ChangeExtension(_selftest!, ".targets.png"));
        }
        foreach (var (href, heading) in new[] { ("runs", "Прогони"), ("profiles", "Профілі прогону"), ("connections", "Підключення до SharePoint") }) // i18n-ignore: headings of the pages
        {
            await Eval($"(document.querySelector('a[href=\"{href}\"]') || {{ click(){{}} }}).click(), ''");
            ok &= await WaitFor(href, $"document.querySelector('.mud-main-content').innerText.includes('{heading}') ? '{heading}' : ''");
        }

        // Stage 5: the plan of the first profile, its source tree, and the last move, when the data folder has them.
        async Task Go(string href) => await Eval($"(() => {{ const a = document.createElement('a'); a.href = '{href}'; document.body.appendChild(a); a.click(); a.remove(); return ''; }})()");
        await Go("plans");
        ok &= await WaitFor("plan and move", "document.querySelector('.mud-main-content').innerText.includes('План і переміщення') ? 'opened' : ''"); // i18n-ignore: text of the page
        await CaptureAsync(Path.ChangeExtension(_selftest!, ".plans.png"));
        string planHref = await Eval("(() => { const a = [...document.querySelectorAll('a[href$=\"/plan\"]')][0]; return a ? a.getAttribute('href') : ''; })()");
        if (planHref.Length > 0 && await Eval("document.querySelector('.mud-main-content').innerText.includes('план ') ? 'yes' : ''") == "yes") // i18n-ignore: text of the page
        {
            string applyHref = await Eval("(() => { const rows = [...document.querySelectorAll('tr')].filter(r => r.cells.length >= 4); for (const r of rows) { const a = r.cells[3].querySelector('a[href^=\"runs/\"]'); if (a) return a.getAttribute('href'); } return ''; })()");
            await Go(planHref);
            ok &= await WaitFor("plan", "document.querySelector('.mud-main-content').innerText.includes('Найбільший пакет') ? 'built' : ''"); // i18n-ignore: text of the page
            await CaptureAsync(Path.ChangeExtension(_selftest!, ".plan.png"));
            await Eval("([...document.querySelectorAll('.mud-tab')].find(t => t.textContent.includes('Дерево джерела')) || { click(){} }).click(), ''"); // i18n-ignore: text of the page
            ok &= await WaitFor("plan tree", "document.querySelector('.plan-node') ? 'yes' : ''");
            await Eval("(() => { const b = document.querySelector('button[aria-label=\"Розгорнути\"]'); if (b) b.click(); return ''; })()"); // i18n-ignore: text of the page
            await CaptureAsync(Path.ChangeExtension(_selftest!, ".plan-tree.png"));
            if (applyHref.Length > 0)
            {
                await Go(applyHref);
                ok &= await WaitFor("move run", "document.querySelector('.mud-main-content').innerText.includes('Переміщено') ? 'opened' : ''"); // i18n-ignore: text of the page
                await CaptureAsync(Path.ChangeExtension(_selftest!, ".apply.png"));
                await Eval("([...document.querySelectorAll('.mud-tab')].find(t => t.textContent.includes('Пакети')) || { click(){} }).click(), ''"); // i18n-ignore: text of the page
                ok &= await WaitFor("batches of the run", "document.querySelector('.mud-main-content').innerText.includes('Спроб') ? 'yes' : ''"); // i18n-ignore: text of the page
                await CaptureAsync(Path.ChangeExtension(_selftest!, ".apply-batches.png"));
            }
        }

        // Help: the guide opens by sections; a picture of a section is saved next to the result for a look later.
        await Eval("(document.querySelector('a[href=\"help\"]') || { click(){} }).click(), ''");
        ok &= await WaitFor("help", "document.querySelector('.help-content') && document.querySelector('.help-nav') ? 'guide shown' : ''");
        await Eval("(() => { const a = document.createElement('a'); a.href = 'help/' + encodeURIComponent('вкладка-продуктивність'); // i18n-ignore: id of the guide section document.body.appendChild(a); a.click(); a.remove(); return ''; })()");
        ok &= await WaitFor("help: section", "document.querySelector('.mud-main-content').innerText.includes('performance.budgetPercent') ? 'Performance tab' : ''");
        await CaptureAsync(Path.ChangeExtension(_selftest!, ".help.png"));
        await Eval("(document.querySelector('a[href=\"profiles\"]') || { click(){} }).click(), ''");
        await Eval("(() => { const a = document.createElement('a'); a.href = 'profiles/~new'; document.body.appendChild(a); a.click(); a.remove(); return ''; })()");
        ok &= await WaitFor("help button on a tab", "[...document.querySelectorAll('button')].some(b => b.textContent.includes('Що означають ці поля')) ? 'yes' : ''"); // i18n-ignore: text of the page
        await Eval("([...document.querySelectorAll('button')].find(b => b.textContent.includes('Що означають ці поля')) || { click(){} }).click(), ''"); // i18n-ignore: text of the page
        ok &= await WaitFor("help dialog", "document.querySelector('.mud-dialog .help-content') ? 'opened' : ''");
        await CaptureAsync(Path.ChangeExtension(_selftest!, ".help-dialog.png"));
        log.Add(ok ? "RESULT ok" : "RESULT fail");

        async Task CaptureAsync(string path)
        {
            await Task.Delay(500); // let the page settle
            await using var png = File.Create(path);
            await _webView.WebView.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, png);
            log.Add($"ok   picture: {path}");
        }
        await File.WriteAllLinesAsync(_selftest!, log);
        await ExitAsync(ok ? 0 : 1);
    }
}
