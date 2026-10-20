using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using TurboDL.Core;

namespace TurboDL;

internal static class Program
{
    static FileStream? _instanceLock;

    [STAThread]
    public static int Main(string[] args)
    {
        TaskScheduler.UnobservedTaskException += (_, a) => { App.Log("Unobserved: " + a.Exception.GetBaseException().Message); a.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => App.Log("Fatal: " + a.ExceptionObject);

        var settings = AppSettings.Load();

        // ---- single instance: hand the URL(s) to the running copy and quit ----
        if (!TryAcquireInstance())
        {
            ForwardToRunningInstance(settings.BridgePort, args);
            return 0;
        }

        SystemProxy.Apply();   // GNOME proxy settings -> HttpClient (like Windows system proxy)

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            App.Log("Fatal startup error: " + ex);
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            ReleaseInstance();
        }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Exclusive lock on a file in the data folder (flock). Released automatically by the kernel if TurboDL crashes,
    /// so a stale lock can never block the next start.
    /// </summary>
    static bool TryAcquireInstance()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            _instanceLock = new FileStream(Path.Combine(AppSettings.DataDir, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
        catch (Exception ex)
        {
            App.Log("Instance lock unavailable: " + ex.Message);
            return true;   // never refuse to start because of a lock problem
        }
    }

    public static void ReleaseInstance()
    {
        try { _instanceLock?.Dispose(); } catch { }
        _instanceLock = null;
    }

    static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    static void ForwardToRunningInstance(int port, string[] args)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var urls = args.Where(IsHttpUrl).ToList();
            Task.Run(async () =>
            {
                if (urls.Count == 0)
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/show");
                    req.Headers.Add(BrowserBridge.ClientHeader, "instance");
                    await http.SendAsync(req);
                }
                foreach (var url in urls)
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/add")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new BridgeRequest { Url = url }), Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Add(BrowserBridge.ClientHeader, "instance");
                    await http.SendAsync(req);
                }
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
    }
}
