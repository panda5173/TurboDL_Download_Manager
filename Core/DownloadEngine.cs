using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace TurboDL.Core;

public interface IEngineHost
{
    AppSettings Settings { get; }
    RateLimiter Limiter { get; }
    /// <summary>Returns a unique file name in <paramref name="folder"/> and reserves it for this session.</summary>
    string ReserveName(string folder, string name);
}

/// <summary>
/// Multi-connection HTTP/HTTPS downloader with IDM-style dynamic segmentation:
/// every free connection takes an unowned range, or splits the largest remaining range in half.
/// Data is written straight into one pre-allocated (sparse) file, so no merge step is needed.
/// </summary>
public static class DownloadEngine
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    const long MinSplitBytes = 512 * 1024;   // never split a range smaller than 2 x 512 KB
    const int BufferSize = 128 * 1024;       // per-connection buffer (pooled)

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None, // byte ranges must be raw
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),   // connection reuse between segments
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UseCookies = false,                                       // cookies come from the browser per item
            UseProxy = true,                                          // env proxy or GNOME proxy (see SystemProxy)
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    sealed class Job
    {
        public required DownloadItem Item;
        public required SafeFileHandle Handle;
        public required bool Resumable;
        public required string Url;
        public required int TimeoutMs;
        public required int MaxRetries;
        public required RateLimiter Limiter;
        public volatile string? LastError;
        public volatile string? FatalError;

        /// <summary>Gives a connection work: an unowned range first, otherwise half of the largest active range.</summary>
        public Segment? Acquire()
        {
            lock (Item.Sync)
            {
                var list = Item.Segments;
                foreach (var s in list)
                    if (!s.Busy && !s.IsDone) { s.Busy = true; return s; }

                if (!Resumable) return null;

                int best = -1; long bestRemaining = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var s = list[i];
                    if (s.Busy && s.End >= 0 && s.Remaining > bestRemaining) { bestRemaining = s.Remaining; best = i; }
                }
                if (best < 0 || bestRemaining < 2 * MinSplitBytes) return null;

                var big = list[best];
                long mid = big.Position + bestRemaining / 2;
                var created = new Segment { Start = mid, Position = mid, End = big.End, Busy = true };
                big.End = mid - 1;
                list.Insert(best + 1, created);
                return created;
            }
        }

        public void Release(Segment seg)
        {
            lock (Item.Sync)
            {
                seg.Busy = false;
                // Fold finished ranges into their right-hand neighbour to keep the list short.
                var list = Item.Segments;
                for (int i = list.Count - 2; i >= 0; i--)
                {
                    var a = list[i]; var b = list[i + 1];
                    if (!a.Busy && a.IsDone && a.End + 1 == b.Start)
                    {
                        b.Start = a.Start;
                        list.RemoveAt(i);
                    }
                }
            }
        }
    }

    public static async Task RunAsync(DownloadItem item, IEngineHost host, CancellationToken ct)
    {
        var settings = host.Settings;
        int timeoutMs = Math.Clamp(settings.TimeoutSeconds, 5, 300) * 1000;
        item.LastError = null;

        bool fresh;
        lock (item.Sync) fresh = item.Segments.Count == 0 || !item.Resumable || item.TotalBytes <= 0;

        HttpResponseMessage? probe = null;
        string url = item.Url;
        try
        {
            if (fresh)
            {
                probe = await ProbeAsync(item, timeoutMs, ct).ConfigureAwait(false);
                url = probe.RequestMessage?.RequestUri?.AbsoluteUri ?? item.Url;

                var range = probe.Content.Headers.ContentRange;
                bool resumable = probe.StatusCode == HttpStatusCode.PartialContent && range?.Length is long;
                long total = resumable ? range!.Length!.Value : (probe.Content.Headers.ContentLength ?? -1);

                string name = item.NameLocked && item.FileName != null
                    ? item.FileName
                    : item.FileName ?? FileHelper.FileNameFromResponse(probe, item.Url);
                string folder = (item.CustomFolder || item.NameLocked) && item.Folder != null
                    ? item.Folder
                    : settings.UseCategoryFolders
                        ? Path.Combine(settings.DownloadFolder, FileHelper.CategoryOf(name))
                        : settings.DownloadFolder;
                Directory.CreateDirectory(folder);
                if (!item.NameLocked) name = host.ReserveName(folder, name);

                item.SetMeta(name, folder, total, resumable);
                lock (item.Sync)
                {
                    item.Segments.Clear();
                    if (total != 0)
                        item.Segments.Add(new Segment { Start = 0, Position = 0, End = total > 0 ? total - 1 : -1, Busy = true });
                }
            }

            await DownloadToPartFileAsync(item, probe, url, fresh, timeoutMs, settings, host.Limiter, ct).ConfigureAwait(false);
            probe = null; // ownership passed on
        }
        finally
        {
            probe?.Dispose();
            item.FlushToDisk = null;
        }

        // ----- finalize: rename .tdl -> real name -----
        string folderFinal = item.Folder!;
        string finalName = item.FileName!;
        string partPath = Path.Combine(folderFinal, finalName + FileHelper.PartExt);
        string finalPath = Path.Combine(folderFinal, finalName);
        if (File.Exists(finalPath))
        {
            finalName = FileHelper.MakeUnique(folderFinal, finalName);
            finalPath = Path.Combine(folderFinal, finalName);
        }
        File.Move(partPath, finalPath);
        if (finalName != item.FileName) item.SetFileName(finalName);
    }

    static async Task DownloadToPartFileAsync(DownloadItem item, HttpResponseMessage? probe, string url, bool fresh,
        int timeoutMs, AppSettings settings, RateLimiter limiter, CancellationToken ct)
    {
        string partPath = item.PartPath!;
        long total = item.TotalBytes;
        bool resumable = item.Resumable;

        // Resuming but the partial file vanished or has a wrong size -> start over safely.
        bool resetFile = false;
        if (!fresh)
        {
            // FIX v1.1.1: Pause cancels workers without releasing their ranges, so 'Busy' stayed true in memory.
            // On resume in the same session those ranges were never re-assigned (only split in half), leaving
            // orphaned pieces that nobody downloaded -> "Download could not be completed" at ~99.9%.
            ResetOwnership(item);

            var fi = new FileInfo(partPath);
            if (!fi.Exists || fi.Length != total)
            {
                resetFile = true;
                lock (item.Sync)
                {
                    item.Segments.Clear();
                    item.Segments.Add(new Segment { Start = 0, Position = 0, End = total - 1 });
                }
            }
        }

        bool preallocate = fresh || resetFile;
        using var fs = new FileStream(partPath, preallocate ? FileMode.Create : FileMode.Open,
            FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
        var handle = fs.SafeFileHandle;

        if (preallocate && total > 0)
        {
            FileHelper.EnsureDiskSpace(Path.GetDirectoryName(partPath)!, total);
            // Linux: reserve the blocks up front with fallocate (instant on ext4/btrfs/xfs, no zero-fill, no
            // "disk full" half-way through). File systems without fallocate get a normal sparse file instead.
            if (!Native.Preallocate(handle, total)) fs.SetLength(total);
        }

        var job = new Job
        {
            Item = item, Handle = handle, Resumable = resumable, Url = url,
            TimeoutMs = timeoutMs, MaxRetries = Math.Max(0, settings.MaxRetries), Limiter = limiter,
        };
        item.FlushToDisk = () => { try { Native.Fsync(handle); } catch { } };

        Segment? firstSeg = null;
        if (probe != null)
        {
            lock (item.Sync) firstSeg = item.Segments.Count > 0 ? item.Segments[0] : null;
            if (firstSeg == null) { probe.Dispose(); probe = null; } // zero-byte file
        }

        int connections = resumable ? Math.Clamp(item.Connections, 1, 32) : 1;
        var firstResponse = probe;
        var firstSegment = firstSeg;
        bool done = false;
        long unknownLength = -1;

        // Normally one pass finishes everything. If ranges are somehow left over without an error,
        // TurboDL re-assigns them and runs another pass instead of failing (self-healing).
        for (int pass = 0; pass < 5 && !done; pass++)
        {
            if (pass > 0)
            {
                if (job.FatalError != null || !resumable) break;
                ResetOwnership(item);
                firstResponse = null;
                firstSegment = null;
            }

            long before = DownloadedBytes(item);
            var workers = new List<Task>(connections);
            try
            {
                for (int i = 0; i < connections; i++)
                {
                    if (i == 0 && firstResponse != null)
                    {
                        var r0 = firstResponse; var s0 = firstSegment;
                        workers.Add(Task.Run(() => WorkerAsync(job, s0, r0, ct)));
                    }
                    else
                        workers.Add(Task.Run(() => WorkerAsync(job, null, null, ct)));

                    // Small stagger so the first connection proves the server before opening the rest.
                    if (i < connections - 1) await Task.Delay(i == 0 ? 120 : 30, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (workers.Count == 0) firstResponse?.Dispose();
                try { await Task.WhenAll(workers).ConfigureAwait(false); } catch { }
                throw;
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            done = true;
            lock (item.Sync)
            {
                foreach (var sg in item.Segments) if (!sg.IsDone) { done = false; break; }
                if (total < 0 && item.Segments.Count == 1) unknownLength = item.Segments[0].Position;
            }
            if (!done && pass > 0 && DownloadedBytes(item) == before) break;   // no progress - give up
        }

        if (!done)
            throw new IOException(job.FatalError ?? job.LastError ?? "Download could not be completed.");

        if (total < 0 && unknownLength >= 0)
        {
            fs.SetLength(unknownLength);
            item.SetTotal(unknownLength);
        }
        else if (total == 0) fs.SetLength(0);

        fs.Flush(true);   // fsync before the rename
    }

    static void ResetOwnership(DownloadItem item)
    {
        lock (item.Sync)
            foreach (var sg in item.Segments) sg.Busy = false;
    }

    static long DownloadedBytes(DownloadItem item)
    {
        long sum = 0;
        lock (item.Sync)
            foreach (var sg in item.Segments) sum += Math.Max(0, sg.Position - sg.Start);
        return sum;
    }

    static async Task<HttpResponseMessage> ProbeAsync(DownloadItem item, int timeoutMs, CancellationToken ct)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var req = BuildRequest(item, item.Url);
                req.Headers.Range = new RangeHeaderValue(0, null);
                var resp = await SendAsync(req, timeoutMs, ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // Typically an empty file - ask again without a range.
                    resp.Dispose();
                    resp = await SendAsync(BuildRequest(item, item.Url), timeoutMs, ct).ConfigureAwait(false);
                }
                if (!resp.IsSuccessStatusCode)
                {
                    var code = resp.StatusCode;
                    string reason = resp.ReasonPhrase ?? "";
                    resp.Dispose();
                    var ex = new HttpRequestException($"Server returned HTTP {(int)code} {reason}".Trim(), null, code);
                    if (IsFatalStatus(code)) throw new FatalDownloadException(ex.Message);
                    throw ex;
                }
                return resp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (FatalDownloadException) { throw; }
            catch (Exception ex)
            {
                last = ex;
                item.LastError = Describe(ex);
                if (attempt < 3) await Task.Delay(1000 * attempt, ct).ConfigureAwait(false);
            }
        }
        throw new IOException(Describe(last!), last);
    }

    static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Server did not respond in time");
        }
    }

    static HttpRequestMessage BuildRequest(DownloadItem item, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version11,                       // separate TCP connections = real acceleration
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        req.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(item.UserAgent) ? DefaultUserAgent : item.UserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(item.Referrer)) req.Headers.TryAddWithoutValidation("Referer", item.Referrer);
        if (!string.IsNullOrWhiteSpace(item.Cookies)) req.Headers.TryAddWithoutValidation("Cookie", item.Cookies);
        return req;
    }

    static async Task<HttpResponseMessage> SendRangeAsync(Job job, Segment seg, CancellationToken ct)
    {
        long from, to;
        lock (job.Item.Sync)
        {
            if (!job.Resumable) seg.Position = seg.Start; // no resume support: restart this stream
            from = seg.Position; to = seg.End;
        }

        var req = BuildRequest(job.Item, job.Url);
        if (job.Resumable) req.Headers.Range = new RangeHeaderValue(from, to >= 0 ? to : null);

        var resp = await SendAsync(req, job.TimeoutMs, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var code = resp.StatusCode;
            string reason = resp.ReasonPhrase ?? "";
            resp.Dispose();
            string msg = $"HTTP {(int)code} {reason}".Trim();
            if (IsFatalStatus(code)) throw new FatalDownloadException(msg + (code == HttpStatusCode.Forbidden ? " (link may have expired)" : ""));
            throw new HttpRequestException(msg, null, code);
        }

        if (job.Resumable)
        {
            if (resp.StatusCode != HttpStatusCode.PartialContent)
            {
                resp.Dispose();
                throw new FatalDownloadException("Server stopped accepting resume requests. Use 'Restart' to download from the beginning.");
            }
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.Length is long len && job.Item.TotalBytes > 0 && len != job.Item.TotalBytes)
            {
                resp.Dispose();
                throw new FatalDownloadException("The file changed on the server (size differs). Use 'Restart'.");
            }
            if (cr?.From is long f && f != from)
            {
                resp.Dispose();
                throw new IOException("Server returned an unexpected byte range");
            }
        }
        return resp;
    }

    static async Task WorkerAsync(Job job, Segment? seg, HttpResponseMessage? initial, CancellationToken ct)
    {
        var item = job.Item;
        int failures = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            seg ??= job.Acquire();
            while (seg != null)
            {
                ct.ThrowIfCancellationRequested();
                Segment current = seg;
                long startPos;
                lock (item.Sync) startPos = current.Position;
                try
                {
                    var resp = initial ?? await SendRangeAsync(job, current, ct).ConfigureAwait(false);
                    initial = null;
                    using (resp)
                    {
                        Interlocked.Increment(ref item.ActiveConnections);
                        try { await PumpAsync(job, current, resp, buffer, ct).ConfigureAwait(false); }
                        finally { Interlocked.Decrement(ref item.ActiveConnections); }
                    }

                    bool finished;
                    lock (item.Sync)
                    {
                        if (current.End < 0) current.End = current.Position - 1;   // unknown size: end of stream = end of file
                        finished = current.IsDone;
                    }
                    if (!finished) throw new IOException("Connection closed by server");

                    failures = 0;
                    job.Release(current);
                    seg = job.Acquire();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lock (item.Sync) current.Busy = false;   // give the range back on pause
                    throw;
                }
                catch (Exception ex)
                {
                    initial?.Dispose();
                    initial = null;

                    long nowPos;
                    lock (item.Sync) nowPos = current.Position;
                    if (nowPos > startPos) failures = 0; // made progress -> not a hard failure

                    failures++;
                    string msg = Describe(ex);
                    job.LastError = msg;
                    item.LastError = msg;

                    // Free the range so a healthy connection can take it while this one backs off.
                    job.Release(current);
                    seg = null;

                    if (ex is FatalDownloadException) { job.FatalError ??= msg; return; }
                    if (failures > job.MaxRetries) return;

                    await Task.Delay(Math.Min(500 * failures, 8000), ct).ConfigureAwait(false);
                    seg = job.Acquire();
                }
            }
        }
        finally
        {
            initial?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static async Task PumpAsync(Job job, Segment seg, HttpResponseMessage resp, byte[] buffer, CancellationToken ct)
    {
        var item = job.Item;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        while (true)
        {
            int want = buffer.Length;
            if (job.Limiter.Enabled)
                want = (int)Math.Clamp(job.Limiter.BytesPerSecond / 10, 4096, buffer.Length);

            readCts.CancelAfter(job.TimeoutMs);
            int n;
            try
            {
                n = await stream.ReadAsync(buffer.AsMemory(0, want), readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No data received (timeout)");
            }
            if (n == 0) return;

            long pos; int take;
            lock (item.Sync)
            {
                pos = seg.Position;
                if (seg.End >= 0)
                {
                    long rem = seg.End - pos + 1;
                    take = rem <= 0 ? 0 : (int)Math.Min(n, rem);
                }
                else take = n;
            }
            if (take <= 0) return; // range was shortened by a split: our part is finished

            RandomAccess.Write(job.Handle, new ReadOnlySpan<byte>(buffer, 0, take), pos);

            bool reachedEnd;
            lock (item.Sync)
            {
                long np = pos + take;
                if (seg.End >= 0 && np > seg.End + 1) np = seg.End + 1;
                if (np > seg.Position) seg.Position = np;
                reachedEnd = seg.End >= 0 && seg.Position > seg.End;
            }
            Interlocked.Add(ref item.BytesCounter, take);
            if (reachedEnd) return;

            await job.Limiter.WaitAsync(take, ct).ConfigureAwait(false);
        }
    }

    static bool IsFatalStatus(HttpStatusCode code) => (int)code is 400 or 401 or 403 or 404 or 405 or 410 or 451;

    public static string Describe(Exception ex)
    {
        Exception e = ex;
        if (e is AggregateException ag && ag.InnerException != null) e = ag.InnerException;
        if (e is IOException io && io.InnerException is FatalDownloadException f) return f.Message;
        if (e is HttpRequestException hre && hre.InnerException is SocketException se)
            return se.SocketErrorCode switch
            {
                SocketError.HostNotFound or SocketError.NoData => "Server not found (check the link or internet connection)",
                SocketError.ConnectionRefused => "Connection refused by server",
                SocketError.TimedOut => "Connection timed out",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "Network unreachable",
                _ => "Network error: " + se.Message,
            };
        if (e is HttpRequestException h2 && h2.InnerException is IOException) return "Connection lost";
        if (e is UnauthorizedAccessException) return "Access denied to the save folder";
        return e.Message;
    }
}
