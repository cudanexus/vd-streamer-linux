using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NLog;
using Tmds.DBus.Protocol;
using Xenko.Core.Mathematics;

namespace VDStreamer.Linux.Capture;

/// <summary>
/// xdg-desktop-portal ScreenCast client. Works on every Wayland desktop (GNOME, KDE, wlroots via
/// xdg-desktop-portal-wlr/-hyprland, ...) and also on X11 sessions that run the portal.
/// The restore token is persisted so the user only picks the screens once.
/// </summary>
internal sealed class PortalScreenCast : IAsyncDisposable
{
    private const string Bus = "org.freedesktop.portal.Desktop";
    private const string ObjectPath = "/org/freedesktop/portal/desktop";
    private const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    private const string RequestInterface = "org.freedesktop.portal.Request";

    private const uint SourceMonitor = 1;
    private const uint CursorHidden = 1;
    private const uint CursorEmbedded = 2;
    private const uint PersistUntilRevoked = 2;

    private static readonly Logger Logger = LogManager.GetLogger("Portal");

    private readonly DBusConnection _connection;
    private string _sessionHandle;
    private SafeFileHandle _pipeWireFd;

    private PortalScreenCast(DBusConnection connection)
    {
        _connection = connection;
    }

    public IReadOnlyList<MonitorInfo> Streams { get; private set; } = Array.Empty<MonitorInfo>();

    public string RestoreToken { get; private set; }

    /// <summary>PipeWire remote file descriptor (valid while this session is alive).</summary>
    public int PipeWireFd => _pipeWireFd == null || _pipeWireFd.IsInvalid ? -1 : (int)_pipeWireFd.DangerousGetHandle();

    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var connection = new DBusConnection(DBusAddress.Session);
            await connection.ConnectAsync().ConfigureAwait(false);
            uint version = await GetUInt32PropertyAsync(connection, "version").ConfigureAwait(false);
            return version >= 1;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "ScreenCast portal unavailable");
            return false;
        }
    }

    /// <summary>
    /// Runs CreateSession → SelectSources → Start → OpenPipeWireRemote. Shows the desktop's
    /// screen picker unless <paramref name="restoreToken"/> is still valid.
    /// </summary>
    public static async Task<PortalScreenCast> StartAsync(string restoreToken, bool embedCursor, CancellationToken cancellationToken)
    {
        var connection = new DBusConnection(DBusAddress.Session);
        await connection.ConnectAsync().ConfigureAwait(false);
        var cast = new PortalScreenCast(connection);
        try
        {
            await cast.RunAsync(restoreToken, embedCursor, cancellationToken).ConfigureAwait(false);
            return cast;
        }
        catch
        {
            await cast.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task RunAsync(string restoreToken, bool embedCursor, CancellationToken ct)
    {
        uint cursorModes = 0;
        uint version = 1;
        try
        {
            cursorModes = await GetUInt32PropertyAsync(_connection, "AvailableCursorModes").ConfigureAwait(false);
            version = await GetUInt32PropertyAsync(_connection, "version").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Could not read portal properties");
        }

        // 1. CreateSession
        var createOptions = new Dictionary<string, VariantValue>
        {
            ["session_handle_token"] = VariantValue.String(NewToken()),
        };
        Dictionary<string, VariantValue> created = await RequestAsync("CreateSession", null, createOptions, ct).ConfigureAwait(false);
        _sessionHandle = created["session_handle"].Type == VariantValueType.ObjectPath
            ? created["session_handle"].GetObjectPathAsString()
            : created["session_handle"].GetString();

        // 2. SelectSources
        var selectOptions = new Dictionary<string, VariantValue>
        {
            ["types"] = VariantValue.UInt32(SourceMonitor),
            ["multiple"] = VariantValue.Bool(true),
        };
        uint wantedCursor = embedCursor ? CursorEmbedded : CursorHidden;
        if ((cursorModes & wantedCursor) != 0)
        {
            selectOptions["cursor_mode"] = VariantValue.UInt32(wantedCursor);
        }
        if (version >= 4)
        {
            selectOptions["persist_mode"] = VariantValue.UInt32(PersistUntilRevoked);
            if (!string.IsNullOrEmpty(restoreToken))
            {
                selectOptions["restore_token"] = VariantValue.String(restoreToken);
            }
        }
        await RequestAsync("SelectSources", _sessionHandle, selectOptions, ct).ConfigureAwait(false);

        // 3. Start (shows the picker when needed)
        Dictionary<string, VariantValue> started = await RequestAsync("Start", _sessionHandle, new Dictionary<string, VariantValue>(), ct, parentWindow: string.Empty).ConfigureAwait(false);
        if (started.TryGetValue("restore_token", out VariantValue token))
        {
            RestoreToken = token.GetString();
        }
        Streams = ParseStreams(started);
        if (Streams.Count == 0)
        {
            throw new InvalidOperationException("The screen sharing portal returned no streams");
        }

        // 4. OpenPipeWireRemote
        MessageBuffer message = BuildCall(_connection, ScreenCastInterface, "OpenPipeWireRemote", _sessionHandle, null, new Dictionary<string, VariantValue>());
        _pipeWireFd = await _connection.CallMethodAsync(message, static (Message m, object _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(), null).ConfigureAwait(false);
        if (_pipeWireFd == null || _pipeWireFd.IsInvalid)
        {
            throw new InvalidOperationException("Portal did not return a PipeWire file descriptor");
        }
        Logger.Info($"Portal screencast started: {string.Join(", ", Streams)}");
    }

    private static List<MonitorInfo> ParseStreams(Dictionary<string, VariantValue> results)
    {
        var list = new List<MonitorInfo>();
        if (!results.TryGetValue("streams", out VariantValue streams))
        {
            return list;
        }
        int x = 0;
        for (int i = 0; i < streams.Count; i++)
        {
            VariantValue stream = streams.GetItem(i);
            uint node = stream.GetItem(0).GetUInt32();
            VariantValue props = stream.GetItem(1);
            int px = x, py = 0, w = 0, h = 0;
            bool hasPosition = false;
            string id = null;
            for (int j = 0; j < props.Count; j++)
            {
                KeyValuePair<VariantValue, VariantValue> entry = props.GetDictionaryEntry(j);
                string key = entry.Key.GetString();
                VariantValue value = entry.Value.Type == VariantValueType.Variant ? entry.Value.GetVariantValue() : entry.Value;
                switch (key)
                {
                    case "position":
                        px = value.GetItem(0).GetInt32();
                        py = value.GetItem(1).GetInt32();
                        hasPosition = true;
                        break;
                    case "size":
                        w = value.GetItem(0).GetInt32();
                        h = value.GetItem(1).GetInt32();
                        break;
                    case "id":
                        id = value.GetString();
                        break;
                }
            }
            if (!hasPosition)
            {
                px = x;
            }
            x = Math.Max(x, px + w);
            list.Add(new MonitorInfo(id ?? ("pw-" + node), "Screen " + (i + 1), new Rectangle(px, py, w, h), 0, i == 0, CaptureKind.PipeWire)
            {
                PipeWireNode = node,
            });
        }
        return list;
    }

    private async Task<Dictionary<string, VariantValue>> RequestAsync(string method, string sessionHandle, Dictionary<string, VariantValue> options, CancellationToken ct, string parentWindow = null)
    {
        string handleToken = NewToken();
        options["handle_token"] = VariantValue.String(handleToken);
        string sender = _connection.UniqueName.TrimStart(':').Replace('.', '_');
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{handleToken}";

        var tcs = new TaskCompletionSource<(uint Code, Dictionary<string, VariantValue> Results)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Interface = RequestInterface,
            Member = "Response",
            Path = requestPath,
        };
        // The (Exception, value, state, state) overload is marked obsolete in Tmds.DBus.Protocol 0.95
        // but is the one verified against the portal; the Notification<T> overload never delivered
        // the Response signal in testing.
#pragma warning disable CS0618
        IDisposable watch = await _connection.AddMatchAsync(
            rule,
            static (Message m, object _) =>
            {
                Reader reader = m.GetBodyReader();
                uint code = reader.ReadUInt32();
                Dictionary<string, VariantValue> results = reader.ReadDictionaryOfStringToVariantValue();
                return (code, results);
            },
            (Exception ex, (uint, Dictionary<string, VariantValue>) value, object readerState, object handlerState) =>
            {
                if (ex != null)
                {
                    tcs.TrySetException(ex);
                }
                else
                {
                    tcs.TrySetResult(value);
                }
            },
            ObserverFlags.None,
            null,
            null,
            true).ConfigureAwait(false);
#pragma warning restore CS0618

        try
        {
            MessageBuffer message = BuildCall(_connection, ScreenCastInterface, method, sessionHandle, parentWindow, options);
            await _connection.CallMethodAsync(message).ConfigureAwait(false);

            using (ct.Register(() => tcs.TrySetCanceled(ct)))
            {
                (uint code, Dictionary<string, VariantValue> results) = await tcs.Task.ConfigureAwait(false);
                if (code != 0)
                {
                    throw new OperationCanceledException(code == 1 ? "Screen sharing was cancelled in the desktop dialog" : $"Portal {method} failed (response {code})");
                }
                return results;
            }
        }
        finally
        {
            watch.Dispose();
        }
    }

    private static async Task<uint> GetUInt32PropertyAsync(DBusConnection connection, string name)
    {
        MessageBuffer message = BuildGetProperty(connection, name);
        return await connection.CallMethodAsync(message, static (Message m, object _) => m.GetBodyReader().ReadVariantValue().GetUInt32(), null).ConfigureAwait(false);
    }

    // MessageWriter is a ref struct and cannot live in async methods; build messages here.
    private static MessageBuffer BuildCall(DBusConnection connection, string iface, string method, string sessionHandle, string parentWindow, Dictionary<string, VariantValue> options)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        string signature = (sessionHandle == null ? string.Empty : "o") + (parentWindow == null ? string.Empty : "s") + "a{sv}";
        writer.WriteMethodCallHeader(Bus, ObjectPath, iface, method, signature, MessageFlags.None);
        if (sessionHandle != null)
        {
            writer.WriteObjectPath(sessionHandle);
        }
        if (parentWindow != null)
        {
            writer.WriteString(parentWindow);
        }
        writer.WriteDictionary(options);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildGetProperty(DBusConnection connection, string name)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        writer.WriteString(ScreenCastInterface);
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildSessionClose(DBusConnection connection, string sessionHandle)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, sessionHandle, "org.freedesktop.portal.Session", "Close", null, MessageFlags.None);
        return writer.CreateMessage();
    }

    private static string NewToken() => "vdstreamer" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_sessionHandle != null)
            {
                MessageBuffer message = BuildSessionClose(_connection, _sessionHandle);
                await _connection.CallMethodAsync(message).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        _pipeWireFd?.Dispose();
        _connection.Dispose();
    }
}
