using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TurboDL.Core;

namespace TurboDL.UI;

public partial class SettingsWindow : Window
{
    readonly AppSettings _current = null!;
    readonly string _originalTheme = "Dark";
    bool _ready, _saved;

    public AppSettings Result { get; private set; } = null!;
    public bool Confirmed => _saved;

    public SettingsWindow()   // designer only
    {
        InitializeComponent();
    }

    public SettingsWindow(AppSettings current, BrowserBridge? bridge)
    {
        InitializeComponent();
        WindowHelper.ApplyChrome(this);
        Opened += (_, _) => WindowHelper.FitToScreen(this);
        _current = current;
        _originalTheme = current.Theme;
        Result = current;

        (current.Theme switch { "System" => ThemeSystem, "Light" => ThemeLight, _ => ThemeDark }).IsChecked = true;
        FolderBox.Text = current.DownloadFolder;
        CategoryBox.IsChecked = current.UseCategoryFolders;
        ConnSlider.Value = current.ConnectionsPerDownload;
        ParSlider.Value = current.MaxParallelDownloads;
        LimitBox.Text = current.SpeedLimitKBps.ToString();
        RetryBox.Text = current.MaxRetries.ToString();
        TimeoutBox.Text = current.TimeoutSeconds.ToString();
        TrayBox.IsChecked = current.MinimizeToTray;
        NotifyBox.IsChecked = current.ShowNotifications;
        ResumeBox.IsChecked = current.ResumeOnStartup;
        StartupBox.IsChecked = StartupManager.IsEnabled;
        PortBox.Text = current.BridgePort.ToString();

        BridgeText.Text = bridge == null ? "Not started"
            : bridge.IsRunning ? $"● Ready – listening on 127.0.0.1:{bridge.Port}"
            : $"● Not running – {bridge.StartError}";

        foreach (var rb in new[] { ThemeSystem, ThemeDark, ThemeLight })
            rb.PropertyChanged += (s, e) =>
            {
                if (e.Property == ToggleButton.IsCheckedProperty && ((RadioButton)s!).IsChecked == true) Theme_Checked();
            };
        ConnSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateLabels(); };
        ParSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) UpdateLabels(); };

        UpdateLabels();
        _ready = true;

        // Cancel / close without saving -> undo the live theme preview.
        Closed += (_, _) => { if (!_saved && SelectedTheme() != _originalTheme) ThemeManager.Apply(_originalTheme); };
    }

    string SelectedTheme() => ThemeSystem.IsChecked == true ? "System" : ThemeLight.IsChecked == true ? "Light" : "Dark";

    void Theme_Checked()
    {
        if (_ready) ThemeManager.Apply(SelectedTheme());   // live preview
    }

    void UpdateLabels()
    {
        ConnText.Text = ((int)ConnSlider.Value).ToString();
        ParText.Text = ((int)ParSlider.Value).ToString();
    }

    async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string start = Directory.Exists(FolderBox.Text) ? FolderBox.Text! : FileHelper.DefaultDownloadsFolder();
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Default download folder",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start),
            });
            if (result.Count > 0 && result[0].TryGetLocalPath() is string path) FolderBox.Text = path;
        }
        catch (Exception ex) { App.Log("Folder picker: " + ex.Message); }
    }

    // ---------------- browser extension setup ----------------

    void SetupChrome_Click(object? sender, RoutedEventArgs e) =>
        SetupBrowser("Chrome", "chrome://extensions", new[] { "google-chrome-stable", "google-chrome" }, false);

    void SetupChromium_Click(object? sender, RoutedEventArgs e) =>
        SetupBrowser("Chromium", "chrome://extensions", new[] { "chromium", "chromium-browser" }, true);

    void SetupEdge_Click(object? sender, RoutedEventArgs e) =>
        SetupBrowser("Edge", "edge://extensions", new[] { "microsoft-edge-stable", "microsoft-edge" }, false);

    async void SetupBrowser(string name, string page, string[] executables, bool mayBeSnap)
    {
        string src = Path.Combine(AppContext.BaseDirectory, "ChromeExtension");
        if (!File.Exists(Path.Combine(src, "manifest.json")))
        {
            Warn($"Extension files were not found in:\n{src}\n\nKeep the 'ChromeExtension' folder next to the TurboDL program.");
            return;
        }

        string? exe = executables.Select(DesktopShell.FindExecutable).FirstOrDefault(p => p != null);

        // Snap browsers (Ubuntu's Chromium) cannot read hidden folders in your home, so use the snap's own folder.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool snap = mayBeSnap && (exe?.StartsWith("/snap/", StringComparison.Ordinal) == true ||
                                  File.Exists("/snap/bin/chromium"));
        string dst = snap
            ? Path.Combine(home, "snap", "chromium", "common", "TurboDL-Extension")
            : Path.Combine(AppSettings.DataDir, "BrowserExtension");

        try { CopyDirectory(src, dst); }
        catch (Exception ex) { Warn("Could not prepare the extension folder:\n" + ex.Message); return; }

        try { if (Clipboard != null) await Clipboard.SetTextAsync(dst); } catch { }

        bool opened = exe != null && DesktopShell.Start(exe, page);

        await MessageDialog.ShowAsync(this,
            (opened ? $"{name} extensions page is opening.\n\n"
                    : exe == null ? $"{name} was not found on this PC. Install it, or open {page} in your browser manually.\n\n"
                                  : $"{name} could not be opened automatically. Open {page} manually.\n\n") +
            "1.  Turn on 'Developer mode' (top-right).\n" +
            "2.  Click 'Load unpacked'.\n" +
            "3.  Press Ctrl+L, paste this folder path (already copied) and click Select:\n" +
            $"      {dst}\n" +
            "4.  Pin the TurboDL icon on the toolbar.\n\n" +
            "If you update TurboDL later, click this button again and press the reload icon on the extension.",
            $"Set up {name}");
    }

    static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), true);
    }

    // ---------------- save ----------------

    void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    void Save_Click(object? sender, RoutedEventArgs e)
    {
        string folder = (FolderBox.Text ?? "").Trim();
        if (folder.StartsWith("~/", StringComparison.Ordinal))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.Substring(2));
        try
        {
            if (folder.Length == 0 || !Path.IsPathRooted(folder)) throw new IOException("Please enter a full folder path.");
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Warn("Download folder is not usable:\n" + ex.Message);
            return;
        }
        if (!int.TryParse(LimitBox.Text?.Trim(), out int limit) || limit < 0) { Warn("Speed limit must be 0 or a positive number."); return; }
        if (!int.TryParse(RetryBox.Text?.Trim(), out int retries) || retries < 0) { Warn("Retries must be 0 or more."); return; }
        if (!int.TryParse(TimeoutBox.Text?.Trim(), out int timeout) || timeout < 5) { Warn("Timeout must be at least 5 seconds."); return; }
        if (!int.TryParse(PortBox.Text?.Trim(), out int port) || port < 1024 || port > 65535) { Warn("Port must be between 1024 and 65535."); return; }

        var s = _current;
        s.Theme = SelectedTheme();
        s.DownloadFolder = folder;
        s.UseCategoryFolders = CategoryBox.IsChecked == true;
        s.ConnectionsPerDownload = (int)ConnSlider.Value;
        s.MaxParallelDownloads = (int)ParSlider.Value;
        s.SpeedLimitKBps = limit;
        s.MaxRetries = retries;
        s.TimeoutSeconds = timeout;
        s.MinimizeToTray = TrayBox.IsChecked == true;
        s.ShowNotifications = NotifyBox.IsChecked == true;
        s.ResumeOnStartup = ResumeBox.IsChecked == true;
        s.BridgePort = port;
        StartupManager.SetEnabled(StartupBox.IsChecked == true);

        Result = s;
        _saved = true;
        Close();
    }

    void Warn(string msg) => _ = MessageDialog.ShowAsync(this, msg, "Settings");
}
