using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TurboDL.Core;
using TurboDL.UI;

namespace TurboDL;

public partial class App : Application
{
    IClassicDesktopStyleApplicationLifetime? _desktop;
    DownloadManager? _manager;
    BrowserBridge? _bridge;
    TrayService? _tray;
    MainWindow? _main;
    bool _exiting;
    readonly List<PosixSignalRegistration> _signals = new();
    string _lastTooltip = "";
    int _tooltipTick;

    public static App Instance => (App)Current!;
    public static bool IsExiting => Current is App a && a._exiting;
    public BrowserBridge? Bridge => _bridge;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => { if (!_exiting) SaveAndStop(); };
            StartApp(desktop.Args ?? Array.Empty<string>());
        }
        base.OnFrameworkInitializationCompleted();
    }

    void StartApp(string[] args)
    {
        Dispatcher.UIThread.UnhandledException += OnUiException;

        // Logout / shutdown / kill: stop cleanly so progress is saved and downloads resume next time.
        foreach (var sig in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(sig, ctx =>
                {
                    ctx.Cancel = true;
                    Dispatcher.UIThread.Post(ExitApp);
                }));
            }
            catch { }
        }

        var settings = AppSettings.Load();
        DesktopShell.ExtractIcon();
        ThemeManager.Apply(settings.Theme);
        StartupManager.RefreshPath();

        _manager = new DownloadManager(settings);
        _main = new MainWindow(_manager);

        _tray = new TrayService();
        _tray.OpenRequested += ShowMain;
        _tray.ExitRequested += ExitApp;
        _tray.PauseAllRequested += () => _manager.PauseAll();
        _tray.ResumeAllRequested += () => _manager.ResumeAll();
        _tray.AddRequested += () => { ShowMain(); _main.ShowAddDialog(null); };

        _manager.Completed += it =>
        {
            if (_manager.Settings.ShowNotifications)
                DesktopShell.Notify("Download complete", $"{it.FileName}  ({FileHelper.FormatBytes(it.TotalBytes)})",
                    () => MainWindow.RevealInFileManager(it));
        };
        _manager.Failed += it =>
        {
            if (_manager.Settings.ShowNotifications)
                DesktopShell.Notify("Download failed", $"{it.DisplayName}\n{it.ErrorMessage}",
                    () => MainWindow.RevealInFileManager(it));
        };
        _manager.Ticked += () =>
        {
            if (++_tooltipTick % 4 != 0) return;   // tray tooltip every ~2 s (each change is a D-Bus message)
            int n = _manager.RunningCount;
            string tip = n == 0 ? "TurboDL – idle" : $"TurboDL – {n} active • {FileHelper.FormatSpeed(_manager.TotalSpeed)}";
            if (tip != _lastTooltip) { _lastTooltip = tip; _tray?.SetTooltip(tip); }
        };

        _bridge = new BrowserBridge(settings.BridgePort, OnBridgeAdd, () => Dispatcher.UIThread.Post(ShowMain));
        _bridge.Start();
        _main.SetBridgeStatus(_bridge);

        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray) _main.Show();
        else if (!TrayService.IsTrayAvailable())
        {
            // No tray area (AppIndicator extension disabled): start minimised instead of invisible.
            _main.WindowState = WindowState.Minimized;
            _main.Show();
        }

        foreach (var arg in args.Where(IsHttpUrl))
            OnBridgeAdd(new BridgeRequest { Url = arg });
    }

    static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    void OnBridgeAdd(BridgeRequest r)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_manager == null || _main == null || r.Url == null) return;
            var urls = r.Urls is { Count: > 0 } ? r.Urls : new List<string> { r.Url };
            if (r.Silent)
            {
                foreach (var url in urls)
                    _manager.Add(url, urls.Count == 1 ? r.FileName : null, null, null, true, r.Referrer, r.Cookies, r.UserAgent);
                if (_manager.Settings.ShowNotifications)
                    DesktopShell.Notify("Download added",
                        urls.Count == 1 ? (r.FileName ?? FileHelper.GuessNameFromUrl(urls[0]) ?? urls[0]) : $"{urls.Count} downloads added");
            }
            else
                _main.ShowAddDialog(string.Join(Environment.NewLine, urls), r);
        });
    }

    public void ShowMain()
    {
        if (_main == null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;    // reliably bring to front
        _main.Topmost = false;
        _main.Focus();
    }

    public void RefreshTray() => _tray?.RefreshStartup();

    /// <summary>Called when the main window's close button is pressed.</summary>
    public async void RequestClose()
    {
        if (_manager == null || _main == null) { ExitApp(); return; }
        if (_manager.Settings.MinimizeToTray)
        {
            if (TrayService.IsTrayAvailable()) _main.Hide();
            else _main.WindowState = WindowState.Minimized;   // never make the window unreachable
            if (!_manager.Settings.TrayHintShown)
            {
                _manager.Settings.TrayHintShown = true;
                _manager.Settings.Save();
                DesktopShell.Notify("TurboDL is still running",
                    "Downloads continue in the background. Use the TurboDL icon in the top bar to open or exit.");
            }
            return;
        }
        if (_manager.RunningCount > 0)
        {
            bool yes = await MessageDialog.ShowAsync(_main,
                "Downloads are in progress. Exit anyway?\n\nThey will be paused and can be resumed later.",
                "TurboDL", "Exit", "Cancel");
            if (!yes) return;
        }
        ExitApp();
    }

    /// <summary>Session is ending (logout/shutdown): save state, don't block.</summary>
    void SaveAndStop()
    {
        _exiting = true;
        try { _manager?.Shutdown(); } catch (Exception ex) { Log("Shutdown: " + ex.Message); }
        _bridge?.Dispose();
        ThemeManager.Stop();
        Program.ReleaseInstance();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { _manager?.Shutdown(); } catch (Exception ex) { Log("Shutdown: " + ex.Message); }
        _bridge?.Dispose();
        try { _tray?.Dispose(); } catch { }
        ThemeManager.Stop();
        try { _main?.Close(); } catch { }
        Program.ReleaseInstance();
        foreach (var s in _signals) { try { s.Dispose(); } catch { } }
        _desktop?.Shutdown();
    }

    void OnUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI error: " + e.Exception);
        e.Handled = true;
        _ = MessageDialog.ShowAsync(_main != null && _main.IsVisible ? _main : null,
            "Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to the log file.", "TurboDL");
    }

    static readonly object LogLock = new();

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                string path = Path.Combine(AppSettings.DataDir, "log.txt");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
