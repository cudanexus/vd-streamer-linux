using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VDStreamer.Streamer;
using VirtualDesktop.Core;

namespace VDStreamer.Web;

/// <summary>Whitelisted, string-typed access to StreamerSettings for the web UI and CLI.</summary>
internal static class SettingsEditor
{
    private sealed record Entry(string Help, Func<StreamerSettings, object> Get, Action<StreamerSettings, string> Set);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["allowRemoteConnections"] = new("Allow connections over the internet (UPnP/NAT traversal)", s => s.AllowRemoteConnections, (s, v) => s.AllowRemoteConnections = Bool(v)),
        ["encryptLocalTraffic"] = new("Encrypt traffic on the local network", s => s.EncryptLocalTraffic, (s, v) => s.EncryptLocalTraffic = Bool(v)),
        ["autoAdjustBitrate"] = new("Automatically adjust bitrate to network conditions", s => s.AutoAdjustBitrate, (s, v) => s.AutoAdjustBitrate = Bool(v)),
        ["streamAudio"] = new("Stream PC audio to the headset", s => s.StreamAudio, (s, v) => s.StreamAudio = Bool(v)),
        ["headsetOnlyAudio"] = new("Play PC audio only on the headset while connected", s => s.HeadsetOnlyAudio, (s, v) => s.HeadsetOnlyAudio = Bool(v)),
        ["autoSelectMicrophone"] = new("Make the headset microphone the default input while connected", s => s.AutoSelectMicrophone, (s, v) => s.AutoSelectMicrophone = Bool(v)),
        ["lockComputer"] = new("Lock the computer when the headset disconnects", s => s.LockComputer, (s, v) => s.LockComputer = Bool(v)),
        ["showPairingRequests"] = new("Show pairing requests from unknown headsets", s => s.ShowPairingRequests, (s, v) => s.ShowPairingRequests = Bool(v)),
        ["preferredCodec"] = new("Video codec: automatic | h264 | hevc", s => s.PreferredCodec.ToString().ToLowerInvariant(), (s, v) => s.PreferredCodec = Enum.Parse<PreferredCodec>(v, true)),
        ["preferredEncoder"] = new("Encoder: auto | nvenc | va | vaapi | x264 | openh264", s => s.PreferredEncoder, (s, v) => s.PreferredEncoder = OneOf(v, "auto", "nvenc", "va", "vaapi", "x264", "openh264", "x265")),
        ["captureBackend"] = new("Screen capture: auto | portal | x11", s => s.CaptureBackend, (s, v) => s.CaptureBackend = OneOf(v, "auto", "portal", "x11")),
        ["headsetName"] = new("Display name for the headset", s => s.HeadsetName, (s, v) => s.HeadsetName = v),
        ["videosRootPath"] = new("Folder shown in the headset's Videos tab", s => s.VideosRootPath, (s, v) => { s.VideosRootPath = v; SettingsBase<DynamicSettings>.Default.RefreshFolders(); }),
        ["screenshotsRootPath"] = new("Folder where headset screenshots are saved", s => s.ScreenshotsRootPath, (s, v) => { s.ScreenshotsRootPath = v; SettingsBase<DynamicSettings>.Default.RefreshFolders(); }),
        ["monitorCount"] = new("Number of monitors to stream", s => s.MonitorCount, (s, v) => s.MonitorCount = int.Parse(v, CultureInfo.InvariantCulture)),
        ["skipUnchangedFrames"] = new("Do not re-encode the screen while it is not changing (X11; saves CPU/GPU)", s => s.SkipUnchangedFrames, (s, v) => s.SkipUnchangedFrames = Bool(v)),
        ["adaptiveBitrate"] = new("Lower the bitrate while the network can't keep up (prevents growing lag)", s => s.AdaptiveBitrate, (s, v) => s.AdaptiveBitrate = Bool(v)),
        ["maxFramerate"] = new("Frame rate limit, e.g. 30 to save CPU (0 = what the headset asks for)", s => s.MaxFramerate, (s, v) => s.MaxFramerate = int.Parse(v, CultureInfo.InvariantCulture)),
        ["scrollSpeed"] = new("Scroll speed multiplier (negative inverts the direction)", s => s.ScrollSpeed, (s, v) => { s.ScrollSpeed = double.Parse(v, CultureInfo.InvariantCulture); VDStreamer.Linux.Input.InputInjector.ScrollSpeed = s.ScrollSpeed; }),
        ["webUiPort"] = new("Port of this settings UI (restart required)", s => s.WebUiPort, (s, v) => s.WebUiPort = int.Parse(v, CultureInfo.InvariantCulture)),
    };

    public static IEnumerable<(string Key, object Value, string Help)> List()
    {
        StreamerSettings s = SettingsBase<StreamerSettings>.Default;
        return Entries.Select(e => (e.Key, e.Value.Get(s), e.Value.Help));
    }

    public static void Set(string key, string value)
    {
        string normalized = key.Replace("-", string.Empty).Replace("_", string.Empty);
        if (!Entries.TryGetValue(normalized, out Entry entry))
        {
            throw new ArgumentException($"Unknown setting '{key}'. Known: {string.Join(", ", Entries.Keys)}");
        }
        entry.Set(SettingsBase<StreamerSettings>.Default, value?.Trim());
    }

    private static bool Bool(string v) => v?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "yes" or "on" => true,
        "0" or "false" or "no" or "off" => false,
        _ => throw new ArgumentException("expected true/false, got " + v),
    };

    private static string OneOf(string v, params string[] allowed)
    {
        string lower = (v ?? string.Empty).Trim().ToLowerInvariant();
        if (!allowed.Contains(lower))
        {
            throw new ArgumentException($"expected one of {string.Join(", ", allowed)}");
        }
        return lower;
    }
}
