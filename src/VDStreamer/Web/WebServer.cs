using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Linux.Capture;
using VDStreamer.Linux.Input;
using VDStreamer.Linux.Video;
using VDStreamer.Streamer;
using VDStreamer.Vendor;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;

namespace VDStreamer.Web;

/// <summary>
/// Local settings UI + JSON control API on 127.0.0.1 (replaces the macOS menu bar app/window).
/// Every /api call requires the per-user token (header X-VDS-Token).
/// </summary>
internal sealed class WebServer : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("WebServer");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    private readonly int _port;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _indexHtml;

    public WebServer(int port)
    {
        _port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html");
        using var ms = new MemoryStream();
        s?.CopyTo(ms);
        _indexHtml = ms.ToArray();
    }

    public void Start()
    {
        _listener.Start();
        Task.Run(LoopAsync).Forget();
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                if (_cts.IsCancellationRequested)
                {
                    return;
                }
                continue;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            string host = request.Headers["Host"] ?? string.Empty;
            if (host != $"127.0.0.1:{_port}" && host != $"localhost:{_port}")
            {
                await WriteAsync(response, 421, "text/plain", "Misdirected request").ConfigureAwait(false);
                return;
            }
            string path = request.Url?.AbsolutePath ?? "/";
            if (path == "/" || path == "/index.html")
            {
                response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'";
                await WriteAsync(response, 200, "text/html; charset=utf-8", _indexHtml).ConfigureAwait(false);
                return;
            }
            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await WriteAsync(response, 404, "text/plain", "Not found").ConfigureAwait(false);
                return;
            }
            if (request.Headers["X-VDS-Token"] != ApiToken.Value)
            {
                await WriteJsonAsync(response, 401, new { error = "missing or invalid token - open the UI with 'vdstreamer ui'" }).ConfigureAwait(false);
                return;
            }
            JsonElement body = default;
            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                string text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    body = JsonDocument.Parse(text).RootElement.Clone();
                }
            }
            (int status, object result) = Route(request.HttpMethod, path, body);
            await WriteJsonAsync(response, status, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Web request failed");
            try
            {
                await WriteJsonAsync(response, 500, new { error = ex.Message }).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static (int, object) Route(string method, string path, JsonElement body)
    {
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        switch (method, path)
        {
            case ("GET", "/api/status"):
                return (200, StatusModel.Build());
            case ("GET", "/api/encoders"):
                return (200, EncoderCatalog.All().Select(e => new { e.Id, e.DisplayName, e.Element, e.Hevc, e.Hardware, broken = EncoderCatalog.Broken.TryGetValue(e.Element, out string why) ? why : null }));
            case ("GET", "/api/logs"):
                return (200, new { lines = ReadLogTail(200) });
            case ("POST", "/api/accounts"):
            {
                string platformText = body.GetProperty("platform").GetString();
                string account = body.TryGetProperty("accountId", out JsonElement a) ? a.GetString() : null;
                if (!StreamerSettings.TryParsePlatform(platformText, out Platform platform))
                {
                    return (400, new { error = "unknown platform " + platformText });
                }
                settings.SetAccountID(platform, account);
                settings.Save();
                return (200, new { ok = true });
            }
            case ("POST", "/api/settings"):
            {
                var changed = new List<string>();
                foreach (JsonProperty property in body.EnumerateObject().ToList())
                {
                    SettingsEditor.Set(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText());
                    changed.Add(property.Name);
                }
                settings.Save();
                return (200, new { ok = true, changed });
            }
            case ("POST", "/api/disconnect"):
                if (ConnectionManager.Status == ConnectionStatus.ConnectedToPeer)
                {
                    NetMessage.CreateOutgoing(MessageType.Disconnect).SendTo(ConnectionManager.MessagingClient);
                }
                return (200, new { ok = true });
            case ("POST", "/api/capture/reset"):
                CaptureManager.ResetPortalPermission();
                return (200, new { ok = true });
            case ("POST", "/api/exit"):
                Task.Delay(200).ContinueWith(_ => App.RequestExit());
                return (200, new { ok = true });
            case ("POST", "/api/restart"):
                Task.Delay(200).ContinueWith(_ => App.Restart());
                return (200, new { ok = true });
        }
        if (method == "POST" && path.StartsWith("/api/pairing/", StringComparison.Ordinal))
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); // api pairing {id|all} {accept|deny}
            if (parts.Length == 4)
            {
                bool accept = parts[3] == "accept";
                bool never = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("dontAskAgain", out JsonElement d) && d.GetBoolean();
                IEnumerable<Guid> ids = parts[2] == "all" ? DiscoveryManager.PairingRequests.Select(r => r.Id).ToList() : new[] { Guid.Parse(parts[2]) };
                int handled = ids.Count(id => DiscoveryManager.RespondToPairing(id, accept, never));
                return (200, new { ok = handled > 0, handled });
            }
        }
        return (404, new { error = "unknown endpoint" });
    }

    public static string[] ReadLogTail(int lines)
    {
        try
        {
            string file = Path.Combine(Paths.DataDir, "logs", "StreamerLog.txt");
            if (!File.Exists(file))
            {
                return Array.Empty<string>();
            }
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - 64 * 1024);
            fs.Position = start;
            using var reader = new StreamReader(fs);
            string[] all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.Skip(Math.Max(0, all.Length - lines)).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, int status, object value) =>
        WriteAsync(response, status, "application/json", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static Task WriteAsync(HttpListenerResponse response, int status, string contentType, string text) =>
        WriteAsync(response, status, contentType, Encoding.UTF8.GetBytes(text));

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, byte[] data)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data).ConfigureAwait(false);
        response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }
}
