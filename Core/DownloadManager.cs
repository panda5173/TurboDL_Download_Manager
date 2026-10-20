using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace TurboDL.Core;

/// <summary>Owns the download list, the queue, persistence and the 500 ms UI refresh tick. UI thread only (except SaveCore).</summary>
public sealed class DownloadManager : IEngineHost
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    static string StatePath => Path.Combine(AppSettings.DataDir, "downloads.json");

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public AppSettings Settings { get; private set; }
    public RateLimiter Limiter { get; } = new();
    public double TotalSpeed { get; private set; }
    public int RunningCount => _running.Count;

    public event Action<DownloadItem>? Completed;
    public event Action<DownloadItem>? Failed;
    public event Action? Ticked;
    public event Action? StatusChanged;

    readonly Dictionary<DownloadItem, (CancellationTokenSource Cts, Task Task)> _running = new();
    readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    readonly object _reserveLock = new();
    readonly object _saveLock = new();
    readonly DispatcherTimer _timer;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastTick;
    int _tick, _saving;
    bool _dirty;

    public DownloadManager(AppSettings settings)
    {
        Settings = settings;
        Limiter.SetLimit(settings.SpeedLimitKBps * 1024L);
        Load();
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        Pump();
    }

    // ---------------- public API ----------------

    public DownloadItem Add(string url, string? fileName, string? folder, int? connections, bool start,
                            string? referrer = null, string? cookies = null, string? userAgent = null)
    {
        var it = new DownloadItem(url, fileName, folder, connections ?? Settings.ConnectionsPerDownload, referrer, cookies, userAgent);
        it.Status = start ? DownloadStatus.Queued : DownloadStatus.Paused;
        Items.Insert(0, it);
        MarkDirty();
        StatusChanged?.Invoke();
        if (start) Pump();
        return it;
    }

    public void Resume(DownloadItem it)
    {
        if (it.Status is DownloadStatus.Paused or DownloadStatus.Error)
        {
            it.ErrorMessage = null;
            it.Status = DownloadStatus.Queued;
            MarkDirty();
            StatusChanged?.Invoke();
            Pump();
        }
    }

    public void Pause(DownloadItem it)
    {
        if (_running.TryGetValue(it, out var r)) r.Cts.Cancel();
        else if (it.Status == DownloadStatus.Queued)
        {
            it.Status = DownloadStatus.Paused;
            MarkDirty();
            StatusChanged?.Invoke();
        }
    }

    public void PauseAll()
    {
        foreach (var it in Items.ToArray()) Pause(it);
    }

    public void ResumeAll()
    {
        foreach (var it in Items.Reverse().ToArray())
            if (it.Status is DownloadStatus.Paused or DownloadStatus.Error)
            {
                it.ErrorMessage = null;
                it.Status = DownloadStatus.Queued;
            }
        MarkDirty();
        StatusChanged?.Invoke();
        Pump();
    }

    /// <summary>Discard progress and download again from byte 0.</summary>
    public void Restart(DownloadItem it)
    {
        if (_running.TryGetValue(it, out var r)) { it.PendingRestart = true; r.Cts.Cancel(); return; }
        RestartNow(it);
    }

    public void Remove(DownloadItem it, bool deleteFile)
    {
        if (_running.TryGetValue(it, out var r))
        {
            it.PendingRemove = true;
            it.PendingDeleteFile = deleteFile;
            r.Cts.Cancel();
            return;
        }
        RemoveNow(it, deleteFile);
    }

    public void ApplySettings(AppSettings s)
    {
        s.Normalize();
        Settings = s;
        s.Save();
        Limiter.SetLimit(s.SpeedLimitKBps * 1024L);
        Pump();
    }

    public string ReserveName(string folder, string name)
    {
        lock (_reserveLock)
        {
            string unique = FileHelper.MakeUnique(folder, name, p => _reserved.Contains(p));
            _reserved.Add(Path.Combine(folder, unique));
            return unique;
        }
    }

    /// <summary>Stops everything and writes state. Called once on exit.</summary>
    public void Shutdown()
    {
        _timer.Stop();
        var tasks = new List<Task>();
        foreach (var kv in _running) { kv.Value.Cts.Cancel(); tasks.Add(kv.Value.Task); }
        try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(4)); } catch { }
        SaveCore(Items.ToArray());
    }

    // ---------------- queue ----------------

    public void Pump()
    {
        int max = Math.Clamp(Settings.MaxParallelDownloads, 1, 10);
        if (_running.Count >= max) return;
        // Items are newest-first, so walk from the end to start the oldest queued item first.
        for (int i = Items.Count - 1; i >= 0 && _running.Count < max; i--)
        {
            var it = Items[i];
            if (it.Status == DownloadStatus.Queued && !_running.ContainsKey(it)) Launch(it);
        }
    }

    void Launch(DownloadItem it)
    {
        var cts = new CancellationTokenSource();
        it.ErrorMessage = null;
        it.LastError = null;
        it.Status = DownloadStatus.Connecting;
        var task = Task.Run(() => DownloadEngine.RunAsync(it, this, cts.Token));
        _running[it] = (cts, task);
        StatusChanged?.Invoke();
        _ = ObserveAsync(it, task, cts);
    }

    async Task ObserveAsync(DownloadItem it, Task task, CancellationTokenSource cts)
    {
        bool completed = false, failed = false;
        try
        {
            await task; // resumes on the UI thread
            it.CompletedAt = DateTime.Now;
            it.Status = DownloadStatus.Completed;
            completed = true;
        }
        catch (OperationCanceledException)
        {
            it.Status = DownloadStatus.Paused;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            it.Status = DownloadStatus.Paused;   // a socket error raised by the pause itself
        }
        catch (Exception ex)
        {
            it.ErrorMessage = DownloadEngine.Describe(ex);
            it.Status = DownloadStatus.Error;
            failed = true;
        }
        finally
        {
            _running.Remove(it);
            cts.Dispose();
            it.Refresh(0);
        }

        if (it.PendingRemove) { RemoveNow(it, it.PendingDeleteFile); }
        else if (it.PendingRestart) { it.PendingRestart = false; RestartNow(it); }
        else if (completed) Completed?.Invoke(it);
        else if (failed) Failed?.Invoke(it);

        MarkDirty();
        StatusChanged?.Invoke();
        Pump();
    }

    void RestartNow(DownloadItem it)
    {
        TryDelete(it.PartPath);
        it.ResetProgress();
        it.Status = DownloadStatus.Queued;
        MarkDirty();
        StatusChanged?.Invoke();
        Pump();
    }

    void RemoveNow(DownloadItem it, bool deleteFile)
    {
        Items.Remove(it);
        TryDelete(it.PartPath);                                 // partial data is useless without the list entry
        if (deleteFile && it.Status == DownloadStatus.Completed) TryDelete(it.FullPath);
        MarkDirty();
        StatusChanged?.Invoke();
    }

    static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- tick ----------------

    void OnTick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTick;
        _lastTick = now;

        double total = 0;
        foreach (var it in _running.Keys)
        {
            it.Refresh(dt);
            total += it.Speed;
        }
        foreach (var it in Items)
            if (it.NeedsRefresh) it.Refresh(0);

        TotalSpeed = total;
        Ticked?.Invoke();

        if (++_tick % 6 == 0 && (_dirty || _running.Count > 0)) SaveInBackground();
    }

    void MarkDirty() => _dirty = true;

    // ---------------- persistence ----------------

    void SaveInBackground()
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0) return;
        _dirty = false;
        var snapshot = Items.ToArray();
        Task.Run(() =>
        {
            try { SaveCore(snapshot); }
            finally { Interlocked.Exchange(ref _saving, 0); }
        });
    }

    void SaveCore(DownloadItem[] items)
    {
        lock (_saveLock)
        {
            try
            {
                // 1) snapshot positions, 2) flush file data, 3) write state.
                // Saved positions are therefore never ahead of the data on disk (safe after power loss).
                var records = new List<DownloadRecord>(items.Length);
                foreach (var it in items) records.Add(it.ToRecord());
                foreach (var it in items) it.FlushToDisk?.Invoke();

                Directory.CreateDirectory(AppSettings.DataDir);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, JsonOpts));
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, StatePath + ".bak", true);
                else File.Move(tmp, StatePath);
            }
            catch (Exception ex) { App.Log("Save failed: " + ex.Message); }
        }
    }

    void Load()
    {
        List<DownloadRecord>? records = TryRead(StatePath) ?? TryRead(StatePath + ".bak");
        if (records == null) return;
        foreach (var r in records)
        {
            if (string.IsNullOrWhiteSpace(r.Url)) continue;
            var it = DownloadItem.FromRecord(r);
            if (it.Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Queued)
                it.Status = Settings.ResumeOnStartup ? DownloadStatus.Queued : DownloadStatus.Paused;
            Items.Add(it);
        }
    }

    static List<DownloadRecord>? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            App.Log($"Could not read {path}: {ex.Message}");
            return null;
        }
    }
}
