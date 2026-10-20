using System;
using System.IO;
using System.Text.Json;

namespace TurboDL.Core;

/// <summary>User preferences, stored in ~/.local/share/TurboDL/settings.json.</summary>
public sealed class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TurboDL");

    static string FilePath => Path.Combine(DataDir, "settings.json");

    public string DownloadFolder { get; set; } = FileHelper.DefaultDownloadsFolder();
    public bool UseCategoryFolders { get; set; } = true;
    public int ConnectionsPerDownload { get; set; } = 8;
    public int MaxParallelDownloads { get; set; } = 3;
    public int SpeedLimitKBps { get; set; } = 0;          // 0 = unlimited
    public int MaxRetries { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 30;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool ResumeOnStartup { get; set; } = false;
    public int BridgePort { get; set; } = 9614;
    public string? LastCustomFolder { get; set; }
    public bool TrayHintShown { get; set; }
    public string Theme { get; set; } = "Dark";          // System | Dark | Light

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch { s = new AppSettings(); }
        s.Normalize();
        return s;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DownloadFolder)) DownloadFolder = FileHelper.DefaultDownloadsFolder();
        ConnectionsPerDownload = Math.Clamp(ConnectionsPerDownload, 1, 32);
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 10);
        SpeedLimitKBps = Math.Max(0, SpeedLimitKBps);
        MaxRetries = Math.Clamp(MaxRetries, 0, 100);
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 5, 300);
        if (Theme is not ("System" or "Dark" or "Light")) Theme = "Dark";
        if (BridgePort < 1024 || BridgePort > 65535) BridgePort = 9614;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}
