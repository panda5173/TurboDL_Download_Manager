using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TurboDL.Core;

/// <summary>Payload sent by the browser extension (or a second TurboDL instance).</summary>
public sealed class BridgeRequest
{
    public string? Url { get; set; }
    public List<string>? Urls { get; set; }   // several links at once (e.g. right-click on a selection)
    public string? FileName { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public bool Silent { get; set; }      // true = start immediately without showing the Add dialog
}

/// <summary>
/// Tiny local HTTP endpoint on 127.0.0.1 used for Chrome/Edge integration and single-instance hand-off.
///   GET  /ping            -> {"app":"TurboDL","version":"1.0.0"}
///   POST /add   (JSON)    -> queue a download     (header X-TurboDL-Client required)
///   POST /show            -> bring window to front (header X-TurboDL-Client required)
/// The custom header blocks ordinary web pages (they cannot send it without a CORS pre-flight, which we never approve).
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    public const string ClientHeader = "X-TurboDL-Client";
    public const string Version = "1.1.1";
    const int MaxHeaderBytes = 32 * 1024;
    const int MaxBodyBytes = 256 * 1024;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Action<BridgeRequest> _onAdd;
    readonly Action _onShow;

    public int Port { get; }
    public bool IsRunning { get; private set; }
    public string? StartError { get; private set; }

    public BrowserBridge(int port, Action<BridgeRequest> onAdd, Action onShow)
    {
        Port = port;
        _onAdd = onAdd;
        _onShow = onShow;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            IsRunning = true;
            _ = AcceptLoopAsync();
        }
        catch (Exception ex)
        {
            StartError = ex.Message;
            App.Log("Browser bridge failed to start: " + ex.Message);
        }
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var (method, path, headers, body) = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);

                // Only browser extensions (chrome-extension://, extension://) and local tools may talk to us.
                headers.TryGetValue("origin", out var origin);
                bool extOrigin = origin != null &&
                    (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                     origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase) ||
                     origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase));
                bool webOrigin = origin != null && !extOrigin;
                string? cors = extOrigin ? origin : null;

                if (method == "OPTIONS")
                {
                    await RespondAsync(stream, extOrigin ? 204 : 403, "", cors).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/ping")
                {
                    await RespondAsync(stream, 200, "{\"app\":\"TurboDL\",\"version\":\"" + Version + "\"}", cors).ConfigureAwait(false);
                    return;
                }
                if (webOrigin || !headers.ContainsKey(ClientHeader.ToLowerInvariant()))
                {
                    await RespondAsync(stream, 403, "{\"ok\":false,\"error\":\"forbidden\"}", cors).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path == "/add")
                {
                    BridgeRequest? req = null;
                    try { req = JsonSerializer.Deserialize<BridgeRequest>(body, JsonOpts); } catch { }
                    var valid = new List<string>();
                    if (req != null)
                    {
                        var all = new List<string>();
                        if (req.Url != null) all.Add(req.Url);
                        if (req.Urls != null) all.AddRange(req.Urls);
                        foreach (var raw in all)
                            if (raw != null && Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) &&
                                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
                                !valid.Contains(u.AbsoluteUri) && valid.Count < 500)
                                valid.Add(u.AbsoluteUri);
                    }
                    if (req == null || valid.Count == 0)
                    {
                        await RespondAsync(stream, 400, "{\"ok\":false,\"error\":\"invalid url\"}", cors).ConfigureAwait(false);
                        return;
                    }
                    req.Urls = valid;
                    req.Url = valid[0];
                    _onAdd(req);
                    await RespondAsync(stream, 200, "{\"ok\":true,\"count\":" + valid.Count + "}", cors).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path == "/show")
                {
                    _onShow();
                    await RespondAsync(stream, 200, "{\"ok\":true}", cors).ConfigureAwait(false);
                    return;
                }
                await RespondAsync(stream, 404, "{\"ok\":false,\"error\":\"not found\"}", cors).ConfigureAwait(false);
            }
            catch { /* bad or aborted request - ignore */ }
        }
    }

    static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("closed");
            ms.Write(buffer, 0, n);
            if (ms.Length > MaxHeaderBytes) throw new IOException("header too large");
            headerEnd = IndexOfHeaderEnd(ms.GetBuffer(), (int)ms.Length);
        }

        byte[] all = ms.GetBuffer();
        int total = (int)ms.Length;
        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = headerText.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) throw new IOException("bad request line");
        string method = first[0].ToUpperInvariant();
        string path = first[1];
        int q = path.IndexOf('?');
        if (q >= 0) path = path.Substring(0, q);

        var headers = new Dictionary<string, string>();
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) headers[lines[i].Substring(0, c).Trim().ToLowerInvariant()] = lines[i].Substring(c + 1).Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("content-length", out var cl)) int.TryParse(cl, out contentLength);
        if (contentLength < 0 || contentLength > MaxBodyBytes) throw new IOException("body too large");

        int bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        body.Write(all, bodyStart, total - bodyStart);
        while (body.Length < contentLength)
        {
            int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) break;
            body.Write(buffer, 0, n);
        }
        string bodyText = Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)Math.Min(body.Length, contentLength));
        return (method, path, headers, bodyText);
    }

    static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (int i = 3; i < length; i++)
            if (data[i - 3] == '\r' && data[i - 2] == '\n' && data[i - 1] == '\r' && data[i] == '\n') return i - 3;
        return -1;
    }

    static async Task RespondAsync(NetworkStream stream, int code, string json, string? corsOrigin = null)
    {
        string reason = code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        byte[] body = Encoding.UTF8.GetBytes(json);
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {code} {reason}\r\n");
        if (body.Length > 0) head.Append("Content-Type: application/json; charset=utf-8\r\n");
        head.Append($"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n");
        if (corsOrigin != null)
            head.Append($"Access-Control-Allow-Origin: {corsOrigin}\r\nVary: Origin\r\n" +
                        "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                        "Access-Control-Allow-Headers: Content-Type, X-TurboDL-Client\r\nAccess-Control-Max-Age: 600\r\n");
        head.Append("\r\n");
        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        IsRunning = false;
    }
}
