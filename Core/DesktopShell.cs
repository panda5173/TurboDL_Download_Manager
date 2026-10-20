using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Avalonia.Threading;

namespace TurboDL.Core;

/// <summary>
/// Freedesktop integration: open files (xdg-open), show a file selected in the file manager (Files/Nautilus via D-Bus),
/// desktop notifications (notify-send, click to show in folder) and small process helpers.
/// </summary>
public static class DesktopShell
{
    public static string? IconPath { get; private set; }

    /// <summary>Writes the app icon to the data folder so notifications and the autostart entry can use it.</summary>
    public static void ExtractIcon()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string path = Path.Combine(AppSettings.DataDir, "turbodl.png");
            using (var src = AssetLoader.Open(new Uri("avares://TurboDL/Assets/turbodl.png")))
            using (var dst = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write))
                src.CopyTo(dst);
            File.Move(path + ".tmp", path, true);
            IconPath = path;
        }
        catch (Exception ex) { App.Log("Icon extract failed: " + ex.Message); }
    }

    // ---------------- processes ----------------

    public static string? FindExecutable(string name)
    {
        if (name.Contains('/')) return File.Exists(name) ? name : null;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries).Append("/snap/bin"))
        {
            try
            {
                string full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Starts a program without waiting. Returns false if it could not be started.</summary>
    public static bool Start(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch { return false; }
    }

    /// <summary>Runs a program and returns (exit code, stdout). Exit code -1 = not found / timed out.</summary>
    public static (int Code, string Output) Run(string file, int timeoutMs, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (-1, "");
            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return (-1, "");
            }
            return (p.ExitCode, stdout.Wait(1000) ? stdout.Result : "");
        }
        catch { return (-1, ""); }
    }

    // ---------------- files ----------------

    public static bool OpenFile(string path) => Start("xdg-open", path);

    public static void OpenFolder(string folder) => Start("xdg-open", folder);

    /// <summary>Opens the file manager with the file selected (GNOME Files, Dolphin, Nemo... via org.freedesktop.FileManager1).</summary>
    public static void Reveal(string filePath)
    {
        Task.Run(() =>
        {
            string uri = ToFileUri(filePath);
            var (code, _) = Run("gdbus", 4000, "call", "--session",
                "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems",
                $"['{uri}']", "");
            if (code != 0)
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (dir != null) OpenFolder(dir);
            }
        });
    }

    /// <summary>file:// URI with every path segment percent-encoded (safe inside a GVariant string, handles # ? ' and spaces).</summary>
    static string ToFileUri(string path) =>
        "file://" + string.Join("/", Path.GetFullPath(path).Split('/').Select(Uri.EscapeDataString));

    // ---------------- notifications ----------------

    static int _actionSupport = -1;   // -1 unknown, 0 no, 1 yes

    static bool SupportsActions()
    {
        if (_actionSupport >= 0) return _actionSupport == 1;
        var (code, output) = Run("notify-send", 2000, "--version");
        bool ok = false;
        if (code == 0)
        {
            // "notify-send 0.8.3" - actions need libnotify 0.7.10 or newer
            var ver = output.Trim().Split(' ').LastOrDefault();
            if (Version.TryParse(ver, out var v)) ok = v >= new Version(0, 7, 10);
        }
        _actionSupport = ok ? 1 : 0;
        return ok;
    }

    /// <summary>Shows a desktop notification. If <paramref name="onClick"/> is given, clicking the notification runs it.</summary>
    public static void Notify(string title, string body, Action? onClick = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                bool withAction = onClick != null && SupportsActions();
                var psi = new ProcessStartInfo("notify-send")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("--app-name=TurboDL");
                if (IconPath != null) psi.ArgumentList.Add("--icon=" + IconPath);
                if (withAction) psi.ArgumentList.Add("--action=default=Show in folder");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add(string.IsNullOrEmpty(body) ? " " : body);

                using var p = Process.Start(psi);
                if (p == null) return;
                var stdout = p.StandardOutput.ReadToEndAsync();
                _ = p.StandardError.ReadToEndAsync();

                // With an action, notify-send waits until the notification is clicked or dismissed.
                using var cts = new CancellationTokenSource(withAction ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(10));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { p.Kill(true); } catch { } return; }

                if (withAction && (await stdout.ConfigureAwait(false)).Trim() == "default")
                    Dispatcher.UIThread.Post(() => { try { onClick!(); } catch { } });
            }
            catch { /* notify-send missing: notifications are optional */ }
        });
    }
}
