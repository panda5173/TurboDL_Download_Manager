using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using TurboDL.Core;

namespace TurboDL.UI;

/// <summary>
/// Top-bar (tray) icon with menu. Ubuntu shows it through the built-in "Ubuntu AppIndicators" GNOME extension
/// (StatusNotifierItem over D-Bus).
/// </summary>
public sealed class TrayService : IDisposable
{
    readonly TrayIcon _icon;
    readonly NativeMenuItem _startupItem;

    public event Action? OpenRequested, ExitRequested, PauseAllRequested, ResumeAllRequested, AddRequested;

    public TrayService()
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Open TurboDL", () => OpenRequested?.Invoke()));
        menu.Items.Add(Item("Add download…", () => AddRequested?.Invoke()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Resume all", () => ResumeAllRequested?.Invoke()));
        menu.Items.Add(Item("Pause all", () => PauseAllRequested?.Invoke()));
        menu.Items.Add(new NativeMenuItemSeparator());

        _startupItem = new NativeMenuItem("Start at login")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = StartupManager.IsEnabled,
        };
        _startupItem.Click += (_, _) =>
        {
            StartupManager.SetEnabled(!StartupManager.IsEnabled);
            RefreshStartup();
        };
        menu.Items.Add(_startupItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Exit", () => ExitRequested?.Invoke()));
        menu.Opening += (_, _) => RefreshStartup();

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://TurboDL/Assets/turbodl.png"))),
            ToolTipText = "TurboDL",
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => OpenRequested?.Invoke();

        if (Application.Current != null)
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
    }

    static NativeMenuItem Item(string header, Action onClick)
    {
        var i = new NativeMenuItem(header);
        i.Click += (_, _) => onClick();
        return i;
    }

    public void RefreshStartup()
    {
        try { _startupItem.IsChecked = StartupManager.IsEnabled; } catch { }
    }

    public void SetTooltip(string text)
    {
        try { _icon.ToolTipText = text; } catch { }
    }

    /// <summary>
    /// True if a tray host is running (Ubuntu AppIndicators / KDE). Without one, hiding the window would make
    /// TurboDL unreachable, so the caller minimises instead.
    /// </summary>
    public static bool IsTrayAvailable()
    {
        var (code, output) = DesktopShell.Run("gdbus", 1500, "call", "--session",
            "--dest", "org.freedesktop.DBus",
            "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner",
            "org.kde.StatusNotifierWatcher");
        if (code != 0) return true;   // can't tell (gdbus missing) - assume the tray works
        return output.Contains("true", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { _icon.IsVisible = false; } catch { }
        try { _icon.Dispose(); } catch { }
    }
}
