using System;
using System.IO;

namespace TurboDL.Core;

/// <summary>
/// "Start at login" via ~/.config/autostart/turbodl.desktop (XDG autostart - GNOME, KDE, XFCE...).
/// Per user, no admin rights needed. Starts minimised to the tray (--tray).
/// </summary>
public static class StartupManager
{
    static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    static string EntryPath => Path.Combine(ConfigHome, "autostart", "turbodl.desktop");

    static string? ExecLine => Environment.ProcessPath is string p ? $"Exec={DesktopQuote(p)} --tray" : null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (!File.Exists(EntryPath)) return false;
                string t = File.ReadAllText(EntryPath);
                return !t.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase) &&
                       !t.Contains("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void SetEnabled(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(EntryPath)) File.Delete(EntryPath);
                return;
            }
            if (ExecLine is not string exec) return;
            Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
            string content =
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=TurboDL\n" +
                "Comment=Fast multi-connection download manager\n" +
                exec + "\n" +
                "Icon=" + (DesktopShell.IconPath ?? "turbodl") + "\n" +
                "Terminal=false\n" +
                "StartupNotify=false\n" +
                "X-GNOME-Autostart-enabled=true\n" +
                "X-GNOME-Autostart-Delay=5\n";      // let the top-bar tray area come up first
            string tmp = EntryPath + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, EntryPath, true);
        }
        catch (Exception ex) { App.Log("Startup setting failed: " + ex.Message); }
    }

    /// <summary>If the TurboDL binary was moved, point the autostart entry at the new location.</summary>
    public static void RefreshPath()
    {
        try
        {
            if (!IsEnabled || ExecLine is not string exec) return;
            string t = File.ReadAllText(EntryPath);
            if (!t.Contains(exec, StringComparison.Ordinal)) SetEnabled(true);
        }
        catch { }
    }

    /// <summary>Quotes a path for the Exec= key (Desktop Entry spec: quoting rules, then string escaping, then %%).</summary>
    static string DesktopQuote(string s)
    {
        string q = "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
        return q.Replace("\\", "\\\\").Replace("%", "%%");
    }
}
