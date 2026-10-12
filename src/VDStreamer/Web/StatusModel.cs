using System;
using System.Linq;
using VDStreamer.Linux;
using VDStreamer.Linux.Audio;
using VDStreamer.Linux.Input;
using VDStreamer.Streamer;
using VDStreamer.Vendor;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;

namespace VDStreamer.Web;

internal static class StatusModel
{
    public static object Build()
    {
        StreamerSettings s = SettingsBase<StreamerSettings>.Default;
        DynamicSettings d = SettingsBase<DynamicSettings>.Default;
        SharedMobileSettings mobile = SettingsBase<SharedMobileSettings>.Default;
        return new
        {
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            vendorVersion = AssemblyHelper.Version?.ToString(),
            vendorDir = VendorManager.ActiveVendorDir,
            uptimeSeconds = (int)(DateTime.UtcNow - App.StartedAt).TotalSeconds,
            status = d.StatusMessage,
            connection = ConnectionManager.Status.ToString(),
            isConnected = d.IsConnected,
            registrationError = ConnectionManager.LastRegistrationError,
            headset = d.IsConnected ? ConnectionManager.DescribeHeadset() : null,
            hmdType = mobile.HmdType.ToString(),
            desktopFramerate = mobile.DesktopFramerate,
            desktopBitrate = mobile.DesktopBitrate,
            latencyMs = d.Latency,
            encoder = d.ActiveEncoder,
            performance = StreamerManager.DesktopStreamer?.Performance,
            capture = d.CaptureDescription,
            monitors = d.Monitors.Select(m => m.ToString()).ToArray(),
            connectionType = d.ConnectionType.ToString(),
            routing = d.RoutingStatus.ToString(),
            modifiedHostsFile = d.HasModifiedHostFile,
            accounts = new
            {
                meta = s.MetaID,
                pico = s.PicoID,
                vive = s.ViveID,
                google = s.GoogleID,
                pfd = s.PFDID,
                apple = s.AppleID,
            },
            settings = SettingsEditor.List().ToDictionary(e => e.Key, e => e.Value),
            settingsHelp = SettingsEditor.List().ToDictionary(e => e.Key, e => e.Help),
            pairingRequests = DiscoveryManager.PairingRequests.Select(r => new
            {
                id = r.Id,
                platform = r.Platform <= Platform.OculusQuest ? "Meta" : r.Platform.ToString(),
                accountId = r.AccountID,
                received = r.Received,
            }).ToArray(),
            environment = new
            {
                session = SystemInfo.IsWayland ? "wayland" : SystemInfo.HasX11 ? "x11" : "headless",
                gstreamer = Gst.IsAvailable ? Gst.Version() : "missing: " + Gst.InitError,
                inputInjection = InputInjector.IsAvailable ? "ok" : (InputInjector.Error ?? (UInputDevice.IsAccessible() ? "ok (idle)" : "no access to /dev/uinput")),
                videoPlayback = VideoStreamer.IsSupported ? "ok" : VideoStreamer.UnsupportedReason,
            },
        };
    }
}
