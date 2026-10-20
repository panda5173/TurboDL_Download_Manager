using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TurboDL.Core;

namespace TurboDL.UI;

public sealed class NavEntry : INotifyPropertyChanged
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Icon { get; init; } = "";
    public bool IsHeader { get; init; }

    int _count;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            OnChanged();
            OnChanged(nameof(HasCount));
        }
    }
    public bool HasCount => _count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public partial class MainWindow : Window
{
    static readonly Regex UrlRegex = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly DownloadManager _mgr = null!;
    readonly ObservableCollection<DownloadItem> _visible = new();
    readonly ObservableCollection<NavEntry> _nav = new();
    string _filter = "all";
    string _search = "";
    bool _refreshQueued;
    NavEntry? _lastNav;

    MenuItem _miOpen = null!, _miFolder = null!, _miResume = null!, _miPause = null!,
             _miRestart = null!, _miCopy = null!, _miDelete = null!;

    public MainWindow()   // designer only
    {
        InitializeComponent();
    }

    public MainWindow(DownloadManager mgr)
    {
        InitializeComponent();
        _mgr = mgr;
        WindowHelper.ApplyChrome(this);
        Opened += (_, _) => WindowHelper.FitToScreen(this);

        BuildNav();
        DownloadList.ItemsSource = _visible;
        BuildContextMenu();

        _mgr.Items.CollectionChanged += (_, _) => QueueRefresh();
        _mgr.Ticked += OnTicked;
        _mgr.StatusChanged += QueueRefresh;

        NavList.SelectionChanged += NavList_SelectionChanged;
        NavList.ContainerPrepared += (_, e) =>
        {
            // Containers can be recycled, so always set the value (header rows are not clickable).
            e.Container.IsEnabled = !(e.Index >= 0 && e.Index < _nav.Count && _nav[e.Index].IsHeader);
        };
        DownloadList.SelectionChanged += (_, _) => UpdateToolbar();
        DownloadList.DoubleTapped += DownloadList_DoubleTapped;
        DownloadList.AddHandler(Button.ClickEvent, ListButton_Click);
        SearchBox.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) OnSearchChanged(); };

        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DropEvent, Window_Drop);

        NavList.SelectedIndex = 0;
        RebuildView();
        UpdateCounts();
        UpdateEmptyState();
        UpdateToolbar();
        OnTicked();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.IsExiting)
        {
            e.Cancel = true;
            App.Instance.RequestClose();
        }
        base.OnClosing(e);
    }

    public void SetBridgeStatus(BrowserBridge bridge)
    {
        BridgeText.Text = bridge.IsRunning
            ? $"Browser bridge ready  •  127.0.0.1:{bridge.Port}"
            : $"Browser bridge off ({bridge.StartError})";
    }

    // ---------------- navigation / filtering ----------------

    void BuildNav()
    {
        _nav.Add(new NavEntry { Key = "all", Title = "All downloads", Icon = "IcList" });
        _nav.Add(new NavEntry { Key = "downloading", Title = "Downloading", Icon = "IcDownload" });
        _nav.Add(new NavEntry { Key = "unfinished", Title = "Unfinished", Icon = "IcPause" });
        _nav.Add(new NavEntry { Key = "completed", Title = "Completed", Icon = "IcCheck" });
        _nav.Add(new NavEntry { Key = "", Title = "CATEGORIES", IsHeader = true });
        foreach (var c in FileHelper.Categories)
            _nav.Add(new NavEntry { Key = "cat:" + c, Title = c, Icon = FileHelper.IconOf(c) });
        NavList.ItemsSource = _nav;
    }

    void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavEntry n) return;
        if (n.IsHeader) { NavList.SelectedItem = _lastNav; return; }
        _lastNav = n;
        _filter = n.Key;
        ViewTitle.Text = n.Title;
        RebuildView();
        UpdateEmptyState();
    }

    bool FilterItem(DownloadItem it)
    {
        if (_search.Length > 0 &&
            it.DisplayName.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0 &&
            it.Url.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) return false;
        return Matches(it, _filter);
    }

    static bool Matches(DownloadItem it, string key) => key switch
    {
        "all" => true,
        "downloading" => it.IsRunning,
        "unfinished" => it.Status != DownloadStatus.Completed,
        "completed" => it.Status == DownloadStatus.Completed,
        _ when key.StartsWith("cat:") => it.Category == key.Substring(4),
        _ => true,
    };

    void OnSearchChanged()
    {
        _search = (SearchBox.Text ?? "").Trim();
        RebuildView();
        UpdateEmptyState();
    }

    /// <summary>Brings the visible list in line with the filter while keeping the selection of rows that stay.</summary>
    void RebuildView()
    {
        if (_mgr == null) return;
        var desired = _mgr.Items.Where(FilterItem).ToList();
        var keep = new HashSet<DownloadItem>(desired);
        for (int i = _visible.Count - 1; i >= 0; i--)
            if (!keep.Contains(_visible[i])) _visible.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < _visible.Count && ReferenceEquals(_visible[i], desired[i])) continue;
            int at = _visible.IndexOf(desired[i]);
            if (at >= 0) _visible.Move(at, i);
            else _visible.Insert(i, desired[i]);
        }
    }

    void QueueRefresh()
    {
        // Status changes come in bursts; coalesce them into one refresh.
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RebuildView();
            UpdateCounts();
            UpdateEmptyState();
            UpdateToolbar();
        }, DispatcherPriority.Background);
    }

    void OnTicked()
    {
        if (_mgr == null) return;
        int running = _mgr.RunningCount;
        TotalSpeedText.Text = FileHelper.FormatSpeed(_mgr.TotalSpeed);
        int queued = _mgr.Items.Count(i => i.Status == DownloadStatus.Queued);
        ActiveText.Text = running == 0 ? (queued > 0 ? $"{queued} queued" : "Idle")
                                       : $"{running} active" + (queued > 0 ? $"  •  {queued} queued" : "");
        LimitText.Text = _mgr.Limiter.Enabled ? $"Speed limit: {FileHelper.FormatSpeed(_mgr.Limiter.BytesPerSecond)}" : "";
        LimitText.IsVisible = _mgr.Limiter.Enabled;
        UpdateCounts();
        FooterText.Text = $"{_mgr.Items.Count} item(s)  •  {_mgr.Settings.ConnectionsPerDownload} connections per file  •  " +
                          $"max {_mgr.Settings.MaxParallelDownloads} at once";
        if (_filter == "downloading") RebuildView();   // a Connecting item may have become Downloading
    }

    void UpdateCounts()
    {
        foreach (var n in _nav)
            if (!n.IsHeader) n.Count = _mgr.Items.Count(i => Matches(i, n.Key));
    }

    void UpdateEmptyState() => EmptyState.IsVisible = _visible.Count == 0;

    void UpdateToolbar()
    {
        var sel = Selected();
        ResumeBtn.IsEnabled = sel.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error);
        PauseBtn.IsEnabled = sel.Any(i => i.IsActive);
        DeleteBtn.IsEnabled = sel.Count > 0;
    }

    List<DownloadItem> Selected() =>
        DownloadList.SelectedItems?.OfType<DownloadItem>().ToList() ?? new List<DownloadItem>();

    // ---------------- adding ----------------

    readonly Queue<(string? Text, BridgeRequest? Req)> _pendingAdds = new();
    bool _addOpen;

    /// <summary>Shows the Add dialog. Requests arriving while it is open (e.g. several browser downloads) are queued.</summary>
    public async void ShowAddDialog(string? text, BridgeRequest? req = null)
    {
        if (_addOpen) { _pendingAdds.Enqueue((text, req)); return; }
        _addOpen = true;
        try
        {
            if (text == null && req == null) text = await ClipboardUrlsAsync();
            var dlg = new AddDownloadWindow(_mgr.Settings, text, req?.FileName);
            await DialogHost.ShowAsync(dlg, this);
            if (dlg.Confirmed)
            {
                _mgr.Settings.Save();
                foreach (var url in dlg.Urls)
                    _mgr.Add(url, dlg.Urls.Count == 1 ? dlg.FileNameOverride : null, dlg.Folder, dlg.Connections, dlg.StartNow,
                             req?.Referrer, req?.Cookies, req?.UserAgent);
            }
        }
        catch (Exception ex) { App.Log("Add dialog: " + ex); }
        finally { _addOpen = false; }

        if (_pendingAdds.Count > 0)
        {
            var next = _pendingAdds.Dequeue();
            Dispatcher.UIThread.Post(() => ShowAddDialog(next.Text, next.Req));
        }
    }

    async Task<string?> ClipboardUrlsAsync()
    {
        try
        {
            if (Clipboard == null) return null;
            var urls = ExtractUrls(await Clipboard.GetTextAsync());
            return urls.Count > 0 ? string.Join(Environment.NewLine, urls) : null;
        }
        catch { return null; }
    }

    internal static List<string> ExtractUrls(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (Match m in UrlRegex.Matches(text))
        {
            string u = m.Value.TrimEnd('.', ',', ';', ')', ']');
            if (Uri.TryCreate(u, UriKind.Absolute, out var uri) && !list.Contains(uri.AbsoluteUri))
                list.Add(uri.AbsoluteUri);
        }
        return list;
    }

    void Add_Click(object? sender, RoutedEventArgs e) => ShowAddDialog(null);

    async void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool inText = FocusManager?.GetFocusedElement() is TextBox;
        if (ctrl && e.Key == Key.N) { e.Handled = true; ShowAddDialog(null); }
        else if (ctrl && e.Key == Key.V && !inText)
        {
            e.Handled = true;
            ShowAddDialog(await ClipboardUrlsAsync() ?? "");
        }
        else if (e.Key == Key.Delete && !inText && Selected().Count > 0) { e.Handled = true; Delete_Click(sender, e); }
        else if (e.Key == Key.Space && !inText && Selected().Count > 0)
        {
            e.Handled = true;
            foreach (var it in Selected())
                if (it.IsActive) _mgr.Pause(it); else _mgr.Resume(it);
        }
        else if (e.Key == Key.Enter && !inText && Selected().FirstOrDefault() is DownloadItem first)
        {
            e.Handled = true;
            OpenItem(first);
        }
    }

    void Window_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Text) || e.Data.Contains("text/uri-list") || e.Data.Contains("text/x-moz-url")
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object? sender, DragEventArgs e)
    {
        var sb = new StringBuilder();
        try { if (e.Data.GetText() is string t) sb.AppendLine(t); } catch { }
        foreach (var fmt in new[] { "text/uri-list", "text/x-moz-url" })
        {
            try
            {
                switch (e.Data.Get(fmt))
                {
                    case string s: sb.AppendLine(s); break;
                    case byte[] b: sb.AppendLine(fmt == "text/x-moz-url" ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b)); break;
                }
            }
            catch { }
        }
        var urls = ExtractUrls(sb.ToString());
        if (urls.Count > 0) ShowAddDialog(string.Join(Environment.NewLine, urls));
    }

    // ---------------- toolbar / menu actions ----------------

    void Resume_Click(object? sender, RoutedEventArgs e) { foreach (var it in Selected()) _mgr.Resume(it); UpdateToolbar(); }
    void Pause_Click(object? sender, RoutedEventArgs e) { foreach (var it in Selected()) _mgr.Pause(it); UpdateToolbar(); }
    void ResumeAll_Click(object? sender, RoutedEventArgs e) => _mgr.ResumeAll();
    void PauseAll_Click(object? sender, RoutedEventArgs e) => _mgr.PauseAll();

    async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var dlg = new ConfirmDeleteWindow(sel.Count, sel.Any(i => i.Status == DownloadStatus.Completed));
        await DialogHost.ShowAsync(dlg, this);
        if (!dlg.Confirmed) return;
        foreach (var it in sel) _mgr.Remove(it, dlg.DeleteFiles);
    }

    void ClearCompleted_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var it in _mgr.Items.Where(i => i.Status == DownloadStatus.Completed).ToList())
            _mgr.Remove(it, false);
    }

    async void Settings_Click(object? sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_mgr.Settings, App.Instance.Bridge);
        await DialogHost.ShowAsync(dlg, this);
        if (dlg.Confirmed)
        {
            _mgr.ApplySettings(dlg.Result);
            App.Instance.RefreshTray();
            OnTicked();
        }
    }

    /// <summary>Round buttons inside each row (pause/resume/open and show-in-folder).</summary>
    void ListButton_Click(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button b || b.DataContext is not DownloadItem it) return;
        e.Handled = true;
        if (Equals(b.Tag, "folder")) { RevealInFileManager(it); return; }
        if (it.Status == DownloadStatus.Completed) OpenItem(it);
        else if (it.IsActive) _mgr.Pause(it);
        else _mgr.Resume(it);
        UpdateToolbar();
    }

    void DownloadList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) != null) return;   // clicks on row buttons
        if (DownloadList.SelectedItem is DownloadItem it) OpenItem(it);
    }

    void BuildContextMenu()
    {
        _miOpen = MakeItem("Open", () => { if (Selected().FirstOrDefault() is DownloadItem it) OpenItem(it); });
        _miFolder = MakeItem("Show in folder", () => { if (Selected().FirstOrDefault() is DownloadItem it) RevealInFileManager(it); });
        _miResume = MakeItem("Resume", () => Resume_Click(null, new RoutedEventArgs()));
        _miPause = MakeItem("Pause", () => Pause_Click(null, new RoutedEventArgs()));
        _miRestart = MakeItem("Restart from beginning", Restart);
        _miCopy = MakeItem("Copy link", CopyLinks);
        _miDelete = MakeItem("Delete…", () => Delete_Click(null, new RoutedEventArgs()));

        var menu = new ContextMenu();
        menu.Items.Add(_miOpen);
        menu.Items.Add(_miFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(_miResume);
        menu.Items.Add(_miPause);
        menu.Items.Add(_miRestart);
        menu.Items.Add(new Separator());
        menu.Items.Add(_miCopy);
        menu.Items.Add(_miDelete);
        menu.Opening += (_, _) =>
        {
            var sel = Selected();
            var one = sel.Count == 1 ? sel[0] : null;
            _miOpen.IsEnabled = one?.Status == DownloadStatus.Completed;
            _miFolder.IsEnabled = one != null;
            _miResume.IsEnabled = sel.Any(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error);
            _miPause.IsEnabled = sel.Any(i => i.IsActive);
            _miRestart.IsEnabled = sel.Count > 0;
            _miCopy.IsEnabled = sel.Count > 0;
            _miDelete.IsEnabled = sel.Count > 0;
        };
        DownloadList.ContextMenu = menu;
    }

    static MenuItem MakeItem(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    async void Restart()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        bool yes = await MessageDialog.ShowAsync(this,
            "Download the selected item(s) again from the beginning?\nAny partial data will be discarded.",
            "Restart download", "Restart", "Cancel");
        if (yes) foreach (var it in sel) _mgr.Restart(it);
    }

    async void CopyLinks()
    {
        try
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(string.Join(Environment.NewLine, Selected().Select(i => i.Url)));
        }
        catch { }
    }

    // ---------------- shell helpers ----------------

    void OpenItem(DownloadItem it)
    {
        if (it.Status != DownloadStatus.Completed) { RevealInFileManager(it); return; }
        string? path = it.FullPath;
        if (path == null || !File.Exists(path))
        {
            _ = MessageDialog.ShowAsync(this, "The file was moved or deleted.", "TurboDL");
            return;
        }
        if (!DesktopShell.OpenFile(path))
            _ = MessageDialog.ShowAsync(this, "No application is set up to open this file type.", "TurboDL");
    }

    public static void RevealInFileManager(DownloadItem it)
    {
        try
        {
            string? target = it.Status == DownloadStatus.Completed ? it.FullPath : it.PartPath;
            if (target != null && File.Exists(target))
            {
                DesktopShell.Reveal(target);
                return;
            }
            string? folder = it.Folder;
            if (folder != null && Directory.Exists(folder)) DesktopShell.OpenFolder(folder);
        }
        catch { }
    }
}
