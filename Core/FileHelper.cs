using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Microsoft.Win32.SafeHandles;

namespace TurboDL.Core;

/// <summary>File-name, category, formatting and native helpers.</summary>
public static class FileHelper
{
    public const string PartExt = ".tdl";

    // ---------- Known folders ----------

    /// <summary>XDG download folder (respects localised names such as ~/Téléchargements), else ~/Downloads.</summary>
    public static string DefaultDownloadsFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            string cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
            string file = Path.Combine(cfg, "user-dirs.dirs");
            if (File.Exists(file))
            {
                foreach (var raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal)) continue;
                    string v = line.Substring("XDG_DOWNLOAD_DIR=".Length).Trim().Trim('"');
                    v = v.Replace("$HOME", home);
                    if (Path.IsPathRooted(v) && Path.GetFullPath(v).TrimEnd('/') != home.TrimEnd('/')) return v;
                }
            }
        }
        catch { }
        return Path.Combine(home, "Downloads");
    }

    // ---------- Categories ----------

    static readonly Dictionary<string, string> ExtToCategory = BuildCategoryMap();

    static Dictionary<string, string> BuildCategoryMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string cat, string exts) { foreach (var e in exts.Split(' ')) map["." + e] = cat; }
        Add("Video", "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob");
        Add("Music", "mp3 wav flac aac ogg m4a wma opus amr");
        Add("Compressed", "zip rar 7z tar gz bz2 xz tgz iso cab zst lz img");
        Add("Documents", "pdf doc docx xls xlsx xlsm ppt pptx txt csv rtf odt ods odp xml json md epub vsdx");
        Add("Programs", "exe msi msix msixbundle appx appxbundle apk bat cmd ps1 jar dmg deb rpm appimage flatpak flatpakref snap run sh");
        Add("Images", "jpg jpeg png gif bmp webp svg tif tiff ico heic raw psd");
        return map;
    }

    public static readonly string[] Categories = { "Video", "Music", "Compressed", "Documents", "Programs", "Images", "Other" };

    public static string CategoryOf(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "Other";
        string ext;
        try { ext = Path.GetExtension(fileName); } catch { return "Other"; }
        return ExtToCategory.TryGetValue(ext, out var c) ? c : "Other";
    }

    /// <summary>Icon resource key (Themes/Icons.axaml).</summary>
    public static string IconOf(string category) => category switch
    {
        "Video" => "IcVideo",
        "Music" => "IcMusic",
        "Compressed" => "IcArchive",
        "Documents" => "IcDocument",
        "Programs" => "IcApps",
        "Images" => "IcImage",
        _ => "IcFile",
    };

    public static string ColorOf(string category) => category switch
    {
        "Video" => "#E5486B",
        "Music" => "#B65CFF",
        "Compressed" => "#F2A93B",
        "Documents" => "#3B8BF2",
        "Programs" => "#2FBF8F",
        "Images" => "#22B5C9",
        _ => "#5B6478",
    };

    static readonly Dictionary<string, IBrush> BrushCache = Categories.ToDictionary(
        c => c, c => (IBrush)new ImmutableSolidColorBrush(Color.Parse(ColorOf(c))));

    public static IBrush BrushOf(string category) =>
        BrushCache.TryGetValue(category, out var b) ? b : BrushCache["Other"];

    // ---------- Names ----------

    // Windows-reserved names and characters are still avoided so files can be copied to USB/NTFS drives safely.
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    static readonly char[] Invalid = { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' };

    /// <summary>Linux limits a file name to 255 *bytes*; keep room for " (123)" and ".tdl".</summary>
    const int MaxNameBytes = 200;

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.', ' ');
        s = s.TrimStart('.', ' ');                      // no hidden files ("." prefix) from server-sent names
        if (s.Length == 0) s = "download";
        if (Encoding.UTF8.GetByteCount(s) > MaxNameBytes)
        {
            string ext = Path.GetExtension(s);
            if (ext.Length > 20) ext = "";
            string stem = s.Substring(0, s.Length - ext.Length);
            int budget = MaxNameBytes - Encoding.UTF8.GetByteCount(ext);
            while (stem.Length > 1 && Encoding.UTF8.GetByteCount(stem) > budget)
            {
                stem = stem.Substring(0, stem.Length - 1);
                if (stem.Length > 0 && char.IsHighSurrogate(stem[^1])) stem = stem.Substring(0, stem.Length - 1);
            }
            s = stem.TrimEnd('.', ' ') + ext;
        }
        string bare = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(bare)) s = "_" + s;
        return s;
    }

    /// <summary>Returns a name that does not collide with an existing file, partial file or reserved path.</summary>
    public static string MakeUnique(string folder, string name, Func<string, bool>? isReserved = null)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        string candidate = name;
        for (int i = 1; i < 10000; i++)
        {
            string full = Path.Combine(folder, candidate);
            if (!File.Exists(full) && !File.Exists(full + PartExt) && !Directory.Exists(full) && !(isReserved?.Invoke(full) ?? false))
                return candidate;
            candidate = $"{stem} ({i}){ext}";
        }
        return $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }

    public static string? GuessNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string last = uri.AbsolutePath;
            last = last.Substring(last.LastIndexOf('/') + 1);
            last = Uri.UnescapeDataString(last);
            return string.IsNullOrWhiteSpace(last) ? null : Sanitize(last);
        }
        catch { return null; }
    }

    public static string FileNameFromResponse(HttpResponseMessage resp, string originalUrl)
    {
        string? name = null;
        try
        {
            var cd = resp.Content.Headers.ContentDisposition;
            if (cd != null)
            {
                name = cd.FileNameStar;
                if (string.IsNullOrWhiteSpace(name)) name = cd.FileName;
                name = name?.Trim().Trim('"');
            }
        }
        catch { name = null; }

        if (string.IsNullOrWhiteSpace(name))
        {
            string finalUrl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? originalUrl;
            name = GuessNameFromUrl(finalUrl) ?? GuessNameFromUrl(originalUrl);
        }
        if (string.IsNullOrWhiteSpace(name)) name = "download";

        if (!Path.HasExtension(name))
        {
            string? ext = ExtFromMime(resp.Content.Headers.ContentType?.MediaType);
            if (ext != null) name += ext;
        }
        return Sanitize(name);
    }

    static string? ExtFromMime(string? mime) => mime?.ToLowerInvariant() switch
    {
        "application/pdf" => ".pdf",
        "application/zip" or "application/x-zip-compressed" => ".zip",
        "application/x-7z-compressed" => ".7z",
        "application/vnd.rar" or "application/x-rar-compressed" => ".rar",
        "application/x-msdownload" or "application/vnd.microsoft.portable-executable" => ".exe",
        "application/x-msi" => ".msi",
        "application/vnd.debian.binary-package" or "application/x-debian-package" => ".deb",
        "application/x-rpm" => ".rpm",
        "application/x-iso9660-image" => ".iso",
        "application/gzip" or "application/x-gzip" => ".gz",
        "application/x-xz" => ".xz",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "audio/mpeg" => ".mp3",
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "text/html" => ".html",
        "text/plain" => ".txt",
        "application/json" => ".json",
        _ => null,
    };

    // ---------- Formatting ----------

    static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "?";
        double v = bytes; int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : (v >= 100 ? $"{v:0} {Units[i]}" : v >= 10 ? $"{v:0.0} {Units[i]}" : $"{v:0.00} {Units[i]}");
    }

    public static string FormatSpeed(double bytesPerSecond) => FormatBytes((long)bytesPerSecond) + "/s";

    public static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > 360000 * 24) return "";
        var t = TimeSpan.FromSeconds(Math.Ceiling(seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    /// <summary>Checks free space on the file system that actually holds <paramref name="folder"/> (longest mount-point match).</summary>
    public static void EnsureDiskSpace(string folder, long bytesNeeded)
    {
        try
        {
            string full = Path.GetFullPath(folder).TrimEnd('/') + "/";
            DriveInfo? best = null;
            int bestLen = -1;
            foreach (var d in DriveInfo.GetDrives())
            {
                string root;
                try { root = d.RootDirectory.FullName.TrimEnd('/') + "/"; } catch { continue; }
                if (full.StartsWith(root, StringComparison.Ordinal) && root.Length > bestLen) { best = d; bestLen = root.Length; }
            }
            if (best == null) return;
            if (best.DriveType == DriveType.Network) return;                 // network share: skip check
            long free = best.AvailableFreeSpace;
            if (free < bytesNeeded)
                throw new FatalDownloadException(
                    $"Not enough disk space on {best.Name} (need {FormatBytes(bytesNeeded)}, free {FormatBytes(free)}).");
        }
        catch (FatalDownloadException) { throw; }
        catch { /* best effort */ }
    }
}

/// <summary>Error that should not be retried (bad link, access denied, file changed...).</summary>
public sealed class FatalDownloadException : Exception
{
    public FatalDownloadException(string message) : base(message) { }
}

/// <summary>Linux system calls (glibc).</summary>
internal static class Native
{
    [DllImport("libc.so.6", SetLastError = true)]
    static extern int fsync(int fd);

    [DllImport("libc.so.6", SetLastError = true)]
    static extern int fallocate(int fd, int mode, long offset, long len);

    /// <summary>Forces written data to disk (used before saving progress, so saved positions never run ahead of the data).</summary>
    internal static void Fsync(SafeFileHandle h)
    {
        bool added = false;
        try
        {
            h.DangerousAddRef(ref added);
            fsync((int)h.DangerousGetHandle());
        }
        catch { /* handle already closed */ }
        finally { if (added) h.DangerousRelease(); }
    }

    /// <summary>Reserves disk blocks for the whole file. Returns false if the file system doesn't support it.</summary>
    internal static bool Preallocate(SafeFileHandle h, long length)
    {
        if (length <= 0) return false;
        bool added = false;
        try
        {
            h.DangerousAddRef(ref added);
            return fallocate((int)h.DangerousGetHandle(), 0, 0, length) == 0;
        }
        catch { return false; }
        finally { if (added) h.DangerousRelease(); }
    }
}
