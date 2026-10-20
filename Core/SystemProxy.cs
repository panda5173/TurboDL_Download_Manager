using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace TurboDL.Core;

/// <summary>
/// On Windows TurboDL follows the system (corporate) proxy automatically. On Linux .NET only reads the
/// http_proxy / https_proxy environment variables, so this also picks up the GNOME "Network Proxy" setting (Manual mode).
/// </summary>
public static class SystemProxy
{
    public static void Apply()
    {
        try
        {
            foreach (var v in new[] { "https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY", "all_proxy", "ALL_PROXY" })
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(v))) return;   // .NET handles these itself

            if (Get("org.gnome.system.proxy", "mode") != "manual") return;

            string host = Get("org.gnome.system.proxy.https", "host");
            int.TryParse(Get("org.gnome.system.proxy.https", "port"), out int port);
            if (host.Length == 0 || port <= 0)
            {
                host = Get("org.gnome.system.proxy.http", "host");
                int.TryParse(Get("org.gnome.system.proxy.http", "port"), out port);
            }
            if (host.Length == 0 || port <= 0) return;

            var bypass = new List<string>();
            foreach (Match m in Regex.Matches(Get("org.gnome.system.proxy", "ignore-hosts"), "'([^']*)'"))
            {
                string h = m.Groups[1].Value.Trim();
                if (h.Length == 0 || h.Contains('/')) continue;     // CIDR ranges are not supported by WebProxy
                bypass.Add("^[a-z]+://" + Regex.Escape(h).Replace("\\*", ".*") + "(:\\d+)?$");
            }

            HttpClient.DefaultProxy = new WebProxy(host, port)
            {
                BypassProxyOnLocal = true,
                BypassList = bypass.ToArray(),
                UseDefaultCredentials = false,
            };
            App.Log($"Using GNOME proxy {host}:{port}");
        }
        catch (Exception ex) { App.Log("Proxy detection failed: " + ex.Message); }
    }

    static string Get(string schema, string key)
    {
        var (code, output) = DesktopShell.Run("gsettings", 2000, "get", schema, key);
        if (code != 0) return "";
        string v = output.Trim();
        if (v.StartsWith("uint32 ", StringComparison.Ordinal)) v = v.Substring(7);
        return v.Trim('\'');
    }
}
