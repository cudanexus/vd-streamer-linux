using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Newtonsoft.Json;
using VDStreamer.Vendor;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;

namespace VDStreamer.Streamer;

public enum PreferredCodec
{
    Automatic,
    H264,
    HEVC,
}

/// <summary>
/// Persistent streamer options (equivalent of the macOS app's StreamerSettings; account IDs
/// are kept in this file with 0600 permissions instead of the macOS keychain).
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public class StreamerSettings : SerializedSettingsBase<StreamerSettings>
{
    private static string _fileName;

    private string _metaID;
    private string _picoID;
    private string _viveID;
    private string _googleID;
    private string _pfdID;
    private string _appleID;
    private int _serverRotation;
    private bool _allowRemoteConnections = true;
    private bool _encryptLocalTraffic;
    private bool _autoAdjustBitrate = true;
    private bool _streamAudio = true;
    private bool _headsetOnlyAudio = true;
    private bool _autoSelectMicrophone = true;
    private bool _lockComputer;
    private bool _showPairingRequests = true;
    private PreferredCodec _preferredCodec;
    private string _preferredEncoder = "auto";
    private string _captureBackend = "auto";
    private string _portalRestoreToken;
    private string _headsetName;
    private string _codecName;
    private string _videosRootPath;
    private string _screenshotsRootPath;
    private DateTime _lastConnectDate;
    private int _monitorCount = 1;
    private int _webUiPort = 38899;
    private bool _skipUnchangedFrames = true;
    private bool _adaptiveBitrate = true;
    private int _maxFramerate;
    private double _scrollSpeed = 1.0;

    public event EventHandler AccountsChanged;
    public event EventHandler AllowRemoteConnectionsChanged;
    public event EventHandler EncryptLocalTrafficChanged;
    public event EventHandler AutoAdjustBitrateChanged;
    public event EventHandler StreamAudioChanged;
    public event EventHandler AutoSelectMicrophoneChanged;
    public event EventHandler PreferredCodecChanged;
    public event EventHandler MonitorCountChanged;

    [JsonProperty] public string MetaID { get => _metaID; set => SetAccount(ref _metaID, value); }
    [JsonProperty] public string PicoID { get => _picoID; set => SetAccount(ref _picoID, value); }
    [JsonProperty] public string ViveID { get => _viveID; set => SetAccount(ref _viveID, value); }
    [JsonProperty] public string GoogleID { get => _googleID; set => SetAccount(ref _googleID, value); }
    [JsonProperty] public string PFDID { get => _pfdID; set => SetAccount(ref _pfdID, value); }
    [JsonProperty] public string AppleID { get => _appleID; set => SetAccount(ref _appleID, value); }

    [JsonProperty] public int ServerRotation { get => _serverRotation; set => Set(ref _serverRotation, value); }

    [JsonProperty, DefaultValue(true)]
    public bool AllowRemoteConnections { get => _allowRemoteConnections; set => Set(ref _allowRemoteConnections, value, AllowRemoteConnectionsChanged); }

    [JsonProperty]
    public bool EncryptLocalTraffic { get => _encryptLocalTraffic; set => Set(ref _encryptLocalTraffic, value, EncryptLocalTrafficChanged); }

    [JsonProperty, DefaultValue(true)]
    public bool AutoAdjustBitrate { get => _autoAdjustBitrate; set => Set(ref _autoAdjustBitrate, value, AutoAdjustBitrateChanged); }

    [JsonProperty, DefaultValue(true)]
    public bool StreamAudio { get => _streamAudio; set => Set(ref _streamAudio, value, StreamAudioChanged); }

    /// <summary>Route PC audio exclusively to the headset while streaming (macOS uses a virtual speaker device).</summary>
    [JsonProperty, DefaultValue(true)]
    public bool HeadsetOnlyAudio { get => _headsetOnlyAudio; set => Set(ref _headsetOnlyAudio, value); }

    [JsonProperty, DefaultValue(true)]
    public bool AutoSelectMicrophone { get => _autoSelectMicrophone; set => Set(ref _autoSelectMicrophone, value, AutoSelectMicrophoneChanged); }

    [JsonProperty]
    public bool LockComputer { get => _lockComputer; set => Set(ref _lockComputer, value); }

    [JsonProperty, DefaultValue(true)]
    public bool ShowPairingRequests { get => _showPairingRequests; set => Set(ref _showPairingRequests, value); }

    [JsonProperty]
    public PreferredCodec PreferredCodec { get => _preferredCodec; set => Set(ref _preferredCodec, value, PreferredCodecChanged); }

    /// <summary>auto | nvenc | vaapi | x264 | openh264</summary>
    [JsonProperty, DefaultValue("auto")]
    public string PreferredEncoder { get => _preferredEncoder; set => Set(ref _preferredEncoder, string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant(), PreferredCodecChanged); }

    /// <summary>auto | portal | x11</summary>
    [JsonProperty, DefaultValue("auto")]
    public string CaptureBackend { get => _captureBackend; set => Set(ref _captureBackend, string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim().ToLowerInvariant()); }

    /// <summary>xdg-desktop-portal restore token so the screen picker is only shown once.</summary>
    [JsonProperty]
    public string PortalRestoreToken { get => _portalRestoreToken; set => Set(ref _portalRestoreToken, value); }

    [JsonProperty] public string HeadsetName { get => _headsetName; set => Set(ref _headsetName, value); }

    [JsonProperty] public string CodecName { get => _codecName; set => Set(ref _codecName, value); }

    [JsonProperty] public string VideosRootPath { get => _videosRootPath; set => Set(ref _videosRootPath, value); }

    [JsonProperty] public string ScreenshotsRootPath { get => _screenshotsRootPath; set => Set(ref _screenshotsRootPath, value); }

    [JsonProperty] public DateTime LastConnectDate { get => _lastConnectDate; set => Set(ref _lastConnectDate, value); }

    [JsonProperty, DefaultValue(1)]
    public int MonitorCount { get => _monitorCount; set => Set(ref _monitorCount, Math.Max(1, value), MonitorCountChanged); }

    [JsonProperty, DefaultValue(38899)]
    public int WebUiPort { get => _webUiPort; set => Set(ref _webUiPort, value); }

    /// <summary>Do not encode X11 frames that are identical to the previous one (saves CPU/GPU on a static screen).</summary>
    [JsonProperty, DefaultValue(true)]
    public bool SkipUnchangedFrames { get => _skipUnchangedFrames; set => Set(ref _skipUnchangedFrames, value, PreferredCodecChanged); }

    /// <summary>Lower the video bitrate while the network cannot keep up, so lag does not build up.</summary>
    [JsonProperty, DefaultValue(true)]
    public bool AdaptiveBitrate { get => _adaptiveBitrate; set => Set(ref _adaptiveBitrate, value, PreferredCodecChanged); }

    /// <summary>Upper limit for the desktop frame rate (0 = what the headset asks for).</summary>
    [JsonProperty]
    public int MaxFramerate { get => _maxFramerate; set => Set(ref _maxFramerate, Math.Max(0, value), PreferredCodecChanged); }

    /// <summary>Multiplier for scrolling from the headset (negative values invert the direction).</summary>
    [JsonProperty, DefaultValue(1.0)]
    public double ScrollSpeed { get => _scrollSpeed; set => Set(ref _scrollSpeed, Math.Clamp(value, -10, 10)); }

    public bool HasValidAccountID =>
        !string.IsNullOrEmpty(_metaID) || !string.IsNullOrEmpty(_picoID) || !string.IsNullOrEmpty(_viveID) ||
        !string.IsNullOrEmpty(_googleID) || !string.IsNullOrEmpty(_pfdID) || !string.IsNullOrEmpty(_appleID);

    public void IncrementServerRotation()
    {
        ServerRotation = (_serverRotation + 1) % 6;
        Task.Run(Save);
    }

    public bool GetAccessTokens(out PlatformAccessToken[] accessTokens)
    {
        var list = new List<PlatformAccessToken>(2);
        if (!string.IsNullOrEmpty(_metaID)) list.Add(new PlatformAccessToken(Platform.OculusQuest, _metaID));
        if (!string.IsNullOrEmpty(_picoID)) list.Add(new PlatformAccessToken(Platform.Pico, _picoID));
        if (!string.IsNullOrEmpty(_googleID)) list.Add(new PlatformAccessToken(Platform.Google, _googleID));
        if (!string.IsNullOrEmpty(_pfdID)) list.Add(new PlatformAccessToken(Platform.PlayForDream, _pfdID));
        if (!string.IsNullOrEmpty(_appleID)) list.Add(new PlatformAccessToken(Platform.Apple, _appleID));
        if (!string.IsNullOrEmpty(_viveID)) list.Add(new PlatformAccessToken(Platform.Vive, _viveID));
        accessTokens = list.ToArray();
        return accessTokens.Length != 0;
    }

    public string GetAccountID(Platform platform) => Normalize(platform) switch
    {
        Platform.OculusQuest => _metaID,
        Platform.Pico => _picoID,
        Platform.Google => _googleID,
        Platform.PlayForDream => _pfdID,
        Platform.Apple => _appleID,
        Platform.Vive => _viveID,
        _ => null,
    };

    public bool CanAddAccountID(Platform platform, string accountID)
    {
        string existing = GetAccountID(platform);
        if (string.IsNullOrEmpty(existing))
        {
            return true;
        }
        StringComparer cmp = Normalize(platform) == Platform.Pico ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        return !cmp.Equals(accountID, existing);
    }

    public bool TryAddAccountID(Platform platform, string accountID)
    {
        if (!CanAddAccountID(platform, accountID))
        {
            return false;
        }
        SetAccountID(platform, accountID);
        return true;
    }

    public void SetAccountID(Platform platform, string accountID)
    {
        accountID = string.IsNullOrWhiteSpace(accountID) ? null : accountID.Trim();
        switch (Normalize(platform))
        {
            case Platform.OculusQuest: MetaID = accountID; break;
            case Platform.Pico: PicoID = accountID; break;
            case Platform.Google: GoogleID = accountID; break;
            case Platform.PlayForDream: PFDID = accountID; break;
            case Platform.Apple: AppleID = accountID; break;
            case Platform.Vive: ViveID = accountID; break;
            default: throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported platform");
        }
    }

    public static Platform Normalize(Platform platform) => platform <= Platform.OculusQuest ? Platform.OculusQuest : platform;

    public static bool TryParsePlatform(string text, out Platform platform)
    {
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "meta": case "oculus": case "quest": platform = Platform.OculusQuest; return true;
            case "pico": platform = Platform.Pico; return true;
            case "vive": case "viveport": case "htc": platform = Platform.Vive; return true;
            case "google": case "android": case "androidxr": platform = Platform.Google; return true;
            case "pfd": case "playfordream": platform = Platform.PlayForDream; return true;
            case "apple": case "visionpro": platform = Platform.Apple; return true;
            default: platform = default; return false;
        }
    }

    public override void Save()
    {
        base.Save();
        try
        {
            File.SetUnixFileMode(GetFileName(), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
        }
    }

    protected override string GetFileName() => _fileName ??= Path.Combine(Paths.ConfigDir, "StreamerSettings.json");

    protected override void Initialize()
    {
        base.Initialize();
        if (_lastConnectDate == default)
        {
            _lastConnectDate = DateTime.UtcNow.Date - TimeSpan.FromDays(55.0);
        }
    }

    private void SetAccount(ref string field, string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (field == value)
        {
            return;
        }
        field = value;
        OnPropertyChanged(nameof(HasValidAccountID));
        StartSaveTimer();
        if (!IsReloading)
        {
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Set<T>(ref T field, T value, EventHandler changed = null, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(name);
        if (!IsReloading)
        {
            changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
