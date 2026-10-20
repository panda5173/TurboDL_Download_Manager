using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using TurboDL.Core;

namespace TurboDL.UI;

/// <summary>
/// Switches the Dark / Light palettes (Themes/Palette.axaml) at runtime. Mode: "System", "Dark" or "Light".
/// "System" follows GNOME's Appearance setting (Settings → Appearance → Style) live.
/// </summary>
public static class ThemeManager
{
    static string _mode = "Dark";
    static bool _hooked;
    static Process? _monitor;

    public static bool IsDark { get; private set; } = true;
    public static event Action? Changed;

    public static void Apply(string mode)
    {
        _mode = mode;
        var app = Application.Current;
        if (app == null) return;

        if (!_hooked)
        {
            _hooked = true;
            app.ActualThemeVariantChanged += (_, _) => Publish(app);
        }

        ThemeVariant variant = mode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => SystemVariant(),
        };
        if (mode == "System") StartMonitor();

        app.RequestedThemeVariant = variant;
        Publish(app);
    }

    static void Publish(Application app)
    {
        IsDark = app.ActualThemeVariant == ThemeVariant.Dark;
        WindowHelper.SetChromeAll();
        Changed?.Invoke();
    }

    /// <summary>GNOME color-scheme (prefer-dark / default), falling back to the GTK theme name (Yaru-dark...).</summary>
    public static bool? SystemPrefersDark()
    {
        var (code, scheme) = DesktopShell.Run("gsettings", 1500, "get", "org.gnome.desktop.interface", "color-scheme");
        if (code != 0) return null;
        if (scheme.Contains("prefer-dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (scheme.Contains("prefer-light", StringComparison.OrdinalIgnoreCase)) return false;
        var (code2, gtk) = DesktopShell.Run("gsettings", 1500, "get", "org.gnome.desktop.interface", "gtk-theme");
        return code2 == 0 && gtk.Contains("dark", StringComparison.OrdinalIgnoreCase);
    }

    static ThemeVariant SystemVariant()
    {
        bool? dark = SystemPrefersDark();
        return dark == null ? ThemeVariant.Default : dark.Value ? ThemeVariant.Dark : ThemeVariant.Light;
    }

    /// <summary>Runs "gsettings monitor" once so a change in GNOME Settings is applied immediately.</summary>
    static void StartMonitor()
    {
        if (_monitor != null) return;
        try
        {
            var psi = new ProcessStartInfo("gsettings")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("monitor");
            psi.ArgumentList.Add("org.gnome.desktop.interface");
            psi.ArgumentList.Add("color-scheme");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Dispatcher.UIThread.Post(() => { if (_mode == "System") Apply("System"); });
            };
            p.ErrorDataReceived += (_, _) => { };
            if (!p.Start()) return;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _monitor = p;
        }
        catch { /* gsettings not available - "System" still uses Avalonia's own detection */ }
    }

    public static void Stop()
    {
        try { if (_monitor is { HasExited: false }) _monitor.Kill(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        _monitor = null;
    }
}
