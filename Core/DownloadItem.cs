using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace TurboDL.Core;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Completed, Error }

/// <summary>A byte range of the file. Start..End inclusive; Position = next byte to fetch. End = -1 means unknown size.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long Position { get; set; }
    public long End { get; set; } = -1;

    internal bool Busy;                                   // owned by a connection
    internal long Remaining => End < 0 ? long.MaxValue : End - Position + 1;
    internal bool IsDone => End >= 0 && Position > End;

    public Segment Clone() => new() { Start = Start, Position = Position, End = End };
}

/// <summary>What is saved in downloads.json.</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? UserFileName { get; set; }
    public string? FileName { get; set; }
    public string? Folder { get; set; }
    public bool CustomFolder { get; set; }
    public long TotalBytes { get; set; } = -1;
    public bool Resumable { get; set; }
    public bool NameLocked { get; set; }
    public DownloadStatus Status { get; set; }
    public List<Segment> Segments { get; set; } = new();
    public DateTime AddedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public int Connections { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// One download. The engine (background threads) only touches fields guarded by <see cref="Sync"/>;
/// the UI timer calls <see cref="Refresh"/> on the UI thread to publish values to bindings.
/// </summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    // ----- engine-shared state -----
    internal readonly object Sync = new();
    internal readonly List<Segment> Segments = new();
    internal long BytesCounter;            // bytes received since last UI tick (Interlocked)
    internal int ActiveConnections;        // Interlocked
    internal volatile Action? FlushToDisk; // set by engine while file is open
    internal volatile string? LastError;   // last retry error (informational)
    internal bool PendingRemove, PendingDeleteFile, PendingRestart;
    internal bool NeedsRefresh = true;

    // ----- identity / request info -----
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public string Url { get; private set; }
    public string? UserFileName { get; private set; }
    public bool CustomFolder { get; private set; }
    public string? Referrer { get; private set; }
    public string? Cookies { get; private set; }
    public string? UserAgent { get; private set; }
    public int Connections { get; private set; }
    public DateTime AddedAt { get; private set; } = DateTime.Now;

    // ----- metadata written by the engine (published on next Refresh) -----
    string? _fileName, _folder;
    long _total = -1;
    bool _resumable, _nameLocked;
    volatile bool _metaDirty = true;

    public string? FileName => _fileName;
    public string? Folder => _folder;
    public long TotalBytes => _total;
    public bool Resumable => _resumable;
    internal bool NameLocked => _nameLocked;

    public string? FullPath => _fileName == null || _folder == null ? null : Path.Combine(_folder, _fileName);
    public string? PartPath => FullPath == null ? null : FullPath + FileHelper.PartExt;

    public DownloadItem(string url, string? fileName, string? folder, int connections,
                        string? referrer, string? cookies, string? userAgent)
    {
        Url = url.Trim();
        UserFileName = string.IsNullOrWhiteSpace(fileName) ? null : FileHelper.Sanitize(fileName);
        _fileName = UserFileName;
        CustomFolder = !string.IsNullOrWhiteSpace(folder);
        _folder = CustomFolder ? folder : null;
        Connections = Math.Clamp(connections, 1, 32);
        Referrer = referrer; Cookies = cookies; UserAgent = userAgent;
    }

    internal void SetMeta(string fileName, string folder, long total, bool resumable)
    {
        lock (Sync) { _fileName = fileName; _folder = folder; _total = total; _resumable = resumable; _nameLocked = true; }
        _metaDirty = true;
    }

    internal void SetFileName(string fileName)
    {
        lock (Sync) _fileName = fileName;
        _metaDirty = true;
    }

    internal void SetTotal(long total)
    {
        lock (Sync) _total = total;
        _metaDirty = true;
    }

    /// <summary>Forget all progress (used by Restart).</summary>
    internal void ResetProgress()
    {
        lock (Sync)
        {
            Segments.Clear();
            _total = -1; _resumable = false; _nameLocked = false;
            _fileName = UserFileName;
            if (!CustomFolder) _folder = null;
        }
        Interlocked.Exchange(ref BytesCounter, 0);
        LastError = null;
        ErrorMessage = null;
        CompletedAt = null;
        _metaDirty = true;
        NeedsRefresh = true;
    }

    // ----- persistence -----
    public DownloadRecord ToRecord()
    {
        var r = new DownloadRecord
        {
            Id = Id, Url = Url, UserFileName = UserFileName, CustomFolder = CustomFolder,
            Status = Status, AddedAt = AddedAt, CompletedAt = CompletedAt,
            Referrer = Referrer, UserAgent = UserAgent, Connections = Connections, Error = ErrorMessage,
            // Session cookies are only kept while the download is unfinished.
            Cookies = Status == DownloadStatus.Completed ? null : Cookies,
        };
        lock (Sync)
        {
            r.FileName = _fileName; r.Folder = _folder; r.TotalBytes = _total;
            r.Resumable = _resumable; r.NameLocked = _nameLocked;
            foreach (var s in Segments) r.Segments.Add(s.Clone());
        }
        return r;
    }

    public static DownloadItem FromRecord(DownloadRecord r)
    {
        var it = new DownloadItem(r.Url, r.UserFileName, r.CustomFolder ? r.Folder : null, r.Connections <= 0 ? 8 : r.Connections,
                                  r.Referrer, r.Cookies, r.UserAgent)
        {
            Id = string.IsNullOrEmpty(r.Id) ? Guid.NewGuid().ToString("N") : r.Id,
            AddedAt = r.AddedAt,
        };
        it._fileName = r.FileName;
        it._folder = r.Folder;
        it._total = r.TotalBytes;
        it._resumable = r.Resumable;
        it._nameLocked = r.NameLocked;
        if (r.Segments != null) it.Segments.AddRange(r.Segments);
        it._status = r.Status;
        it._errorMessage = r.Error;
        it._completedAt = r.CompletedAt;
        return it;
    }

    // ----- UI-facing properties -----
    DownloadStatus _status = DownloadStatus.Queued;
    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            NeedsRefresh = true;
            OnChanged(); OnChanged(nameof(StatusText)); OnChanged(nameof(StatusBrush));
            OnChanged(nameof(ActionIcon)); OnChanged(nameof(ActionTip)); OnChanged(nameof(IsActive));
            Refresh(0);
        }
    }

    public bool IsActive => _status is DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading;
    public bool IsRunning => _status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }

    DateTime? _completedAt;
    public DateTime? CompletedAt { get => _completedAt; set => Set(ref _completedAt, value); }

    public string DisplayName => _fileName ?? FileHelper.GuessNameFromUrl(Url) ?? Url;
    public string Category => FileHelper.CategoryOf(_fileName ?? FileHelper.GuessNameFromUrl(Url));
    public string CategoryIcon => FileHelper.IconOf(Category);
    public IBrush CategoryBrush => FileHelper.BrushOf(Category);

    public string StatusText => _status switch
    {
        DownloadStatus.Queued => "Queued",
        DownloadStatus.Connecting => "Connecting…",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Completed",
        _ => "Failed",
    };

    static readonly IBrush QueuedBrush = new ImmutableSolidColorBrush(Color.Parse("#8C95AA"));
    static readonly IBrush WaitBrush = new ImmutableSolidColorBrush(Color.Parse("#FFB547"));
    static readonly IBrush RunBrush = new ImmutableSolidColorBrush(Color.Parse("#4F8CFF"));
    static readonly IBrush DoneBrush = new ImmutableSolidColorBrush(Color.Parse("#3DDC97"));
    static readonly IBrush FailBrush = new ImmutableSolidColorBrush(Color.Parse("#FF5D5D"));

    public IBrush StatusBrush => _status switch
    {
        DownloadStatus.Queued => QueuedBrush,
        DownloadStatus.Connecting => WaitBrush,
        DownloadStatus.Downloading => RunBrush,
        DownloadStatus.Paused => WaitBrush,
        DownloadStatus.Completed => DoneBrush,
        _ => FailBrush,
    };

    /// <summary>Icon resource key (Themes/Icons.axaml).</summary>
    public string ActionIcon => _status switch
    {
        DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading => "IcPause",
        DownloadStatus.Completed => "IcOpen",
        _ => "IcPlay",
    };

    public string ActionTip => _status switch
    {
        DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Downloading => "Pause",
        DownloadStatus.Completed => "Open file",
        _ => "Resume",
    };

    long _downloaded;
    public long Downloaded { get => _downloaded; private set => Set(ref _downloaded, value); }

    double _progress;
    public double ProgressPercent { get => _progress; private set => Set(ref _progress, value); }

    double _speed;
    public double Speed { get => _speed; private set => Set(ref _speed, value); }

    long[]? _snapshot;
    public long[]? SegmentSnapshot { get => _snapshot; private set { _snapshot = value; OnChanged(); } }

    string _sizeText = "", _speedText = "", _etaText = "", _detailText = "";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    double _smoothSpeed;

    /// <summary>UI thread only. Pulls engine counters and updates bindable properties.</summary>
    internal void Refresh(double seconds)
    {
        NeedsRefresh = false;
        if (_metaDirty)
        {
            _metaDirty = false;
            OnChanged(nameof(FileName)); OnChanged(nameof(DisplayName)); OnChanged(nameof(TotalBytes));
            OnChanged(nameof(Category)); OnChanged(nameof(CategoryIcon)); OnChanged(nameof(CategoryBrush));
        }

        long downloaded = 0, total;
        bool resumable;
        long[]? snap = null;
        lock (Sync)
        {
            total = _total; resumable = _resumable;
            foreach (var s in Segments) downloaded += Math.Max(0, s.Position - s.Start);
            if (_status != DownloadStatus.Completed && resumable && total > 0 && Segments.Count > 0)
            {
                snap = new long[Segments.Count * 3];
                int k = 0;
                foreach (var s in Segments) { snap[k++] = s.Start; snap[k++] = s.Position; snap[k++] = s.End; }
            }
        }
        if (_status == DownloadStatus.Completed && total >= 0) downloaded = total;

        bool running = IsRunning;
        long delta = Interlocked.Exchange(ref BytesCounter, 0);
        if (!running) _smoothSpeed = 0;
        else if (seconds > 0.05)
        {
            double inst = delta / seconds;
            _smoothSpeed = _smoothSpeed <= 0 ? inst : _smoothSpeed * 0.65 + inst * 0.35;
        }
        if (_status == DownloadStatus.Connecting && delta > 0)
        {
            _status = DownloadStatus.Downloading;
            OnChanged(nameof(Status)); OnChanged(nameof(StatusText)); OnChanged(nameof(StatusBrush));
        }

        double pct = total > 0 ? Math.Min(100.0, downloaded * 100.0 / total) : (_status == DownloadStatus.Completed ? 100 : 0);
        Downloaded = downloaded;
        ProgressPercent = pct;
        Speed = _smoothSpeed;
        SegmentSnapshot = snap;

        SizeText = total > 0
            ? (_status == DownloadStatus.Completed ? FileHelper.FormatBytes(total) : $"{FileHelper.FormatBytes(downloaded)} of {FileHelper.FormatBytes(total)}")
            : downloaded > 0 ? FileHelper.FormatBytes(downloaded)
            : _status == DownloadStatus.Completed ? "0 B" : "Size unknown";

        SpeedText = running && _smoothSpeed > 0 ? FileHelper.FormatSpeed(_smoothSpeed) : "";
        EtaText = running && _smoothSpeed > 1 && total > 0
            ? FileHelper.FormatEta((total - downloaded) / _smoothSpeed) + " left" : "";

        int conns = Volatile.Read(ref ActiveConnections);
        string? lastErr = LastError;
        DetailText = _status switch
        {
            DownloadStatus.Downloading => $"{pct:0.0}%  •  {conns} connection{(conns == 1 ? "" : "s")}"
                                          + (resumable ? "" : "  •  no resume support"),
            DownloadStatus.Connecting => lastErr != null ? "Retrying – " + lastErr : "Connecting to server…",
            DownloadStatus.Queued => "Waiting in queue",
            DownloadStatus.Paused => (total > 0 ? $"{pct:0.0}%  •  " : "")
                                     + (resumable || downloaded == 0 ? "Paused" : "Paused – will restart (server has no resume)"),
            DownloadStatus.Completed => _completedAt is DateTime d ? $"Completed {d:dd MMM yyyy, HH:mm}" : "Completed",
            _ => _errorMessage ?? "Failed",
        };
    }

    // ----- INotifyPropertyChanged -----
    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnChanged(name);
    }
}
