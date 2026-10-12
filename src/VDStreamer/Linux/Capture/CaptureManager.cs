using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Streamer;
using VirtualDesktop.Core;

namespace VDStreamer.Linux.Capture;

/// <summary>
/// Chooses the capture backend (ScreenCast portal + PipeWire, or X11) and produces the GStreamer
/// source fragment for each monitor.
/// </summary>
internal sealed class CaptureManager : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("Capture");

    private PortalScreenCast _portal;

    public CaptureKind Kind { get; private set; }

    public static CaptureKind ResolveBackend()
    {
        string preferred = SettingsBase<StreamerSettings>.Default.CaptureBackend;
        bool hasPipeWireSrc = Gst.HasElement("pipewiresrc");
        bool hasXImageSrc = Gst.HasElement("ximagesrc");
        if (preferred == "x11" && SystemInfo.HasX11 && hasXImageSrc)
        {
            return CaptureKind.X11;
        }
        if (preferred == "portal" && hasPipeWireSrc)
        {
            return CaptureKind.PipeWire;
        }
        if (SystemInfo.IsWayland && hasPipeWireSrc)
        {
            return CaptureKind.PipeWire;
        }
        if (SystemInfo.HasX11 && hasXImageSrc)
        {
            return CaptureKind.X11;
        }
        return CaptureKind.PipeWire;
    }

    /// <summary>Enumerates the monitors to stream. For the portal this may show the screen picker.</summary>
    public MonitorInfo[] GetMonitors(bool embedCursor)
    {
        Kind = ResolveBackend();
        if (Kind == CaptureKind.X11)
        {
            ClosePortal();
            List<MonitorInfo> monitors = X11Displays.GetMonitors();
            SettingsBase<DynamicSettings>.Default.CaptureDescription = $"X11 ({Environment.GetEnvironmentVariable("DISPLAY")}), {monitors.Count} monitor(s)";
            return monitors.ToArray();
        }

        if (_portal == null)
        {
            if (!Gst.HasElement("pipewiresrc"))
            {
                throw new InvalidOperationException("Wayland capture needs the GStreamer PipeWire plugin (gstreamer1.0-pipewire / gst-plugin-pipewire)");
            }
            SettingsBase<DynamicSettings>.Default.StatusMessage = "Waiting for screen sharing permission on the PC...";
            StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            _portal = Task.Run(() => PortalScreenCast.StartAsync(settings.PortalRestoreToken, embedCursor, cts.Token)).GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(_portal.RestoreToken) && _portal.RestoreToken != settings.PortalRestoreToken)
            {
                settings.PortalRestoreToken = _portal.RestoreToken;
                settings.Save();
            }
        }
        SettingsBase<DynamicSettings>.Default.CaptureDescription = $"PipeWire screencast portal, {_portal.Streams.Count} screen(s)";
        return _portal.Streams.ToArray();
    }

    /// <summary>GStreamer source (raw video) for one monitor.</summary>
    public string BuildSource(MonitorInfo monitor, int framerate, bool showCursor)
    {
        if (monitor.Kind == CaptureKind.X11)
        {
            var b = monitor.Bounds;
            string display = Environment.GetEnvironmentVariable("DISPLAY") ?? ":0";
            return string.Format(CultureInfo.InvariantCulture,
                "ximagesrc name=src display-name={0} use-damage=false show-pointer={1} startx={2} starty={3} endx={4} endy={5} ! " +
                "video/x-raw,framerate={6}/1",
                display, showCursor ? "true" : "false", b.X, b.Y, b.X + b.Width - 1, b.Y + b.Height - 1, framerate);
        }
        if (_portal == null)
        {
            throw new InvalidOperationException("Portal session is not active");
        }
        return string.Format(CultureInfo.InvariantCulture,
            "pipewiresrc name=src fd={0} path={1} do-timestamp=true always-copy=false keepalive-time=500 ! " +
            "videorate name=rate drop-only=true max-rate={2}",
            _portal.PipeWireFd, monitor.PipeWireNode, framerate);
    }

    /// <summary>Forgets the stored portal permission and asks the user again on the next stream.</summary>
    public static void ResetPortalPermission()
    {
        SettingsBase<StreamerSettings>.Default.PortalRestoreToken = null;
        SettingsBase<StreamerSettings>.Default.Save();
    }

    public void ClosePortal()
    {
        PortalScreenCast portal = Interlocked.Exchange(ref _portal, null);
        if (portal != null)
        {
            try
            {
                portal.DisposeAsync().AsTask().Wait(2000);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Closing portal session");
            }
        }
    }

    public void Dispose() => ClosePortal();
}
