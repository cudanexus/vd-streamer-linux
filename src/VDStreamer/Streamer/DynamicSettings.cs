using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using VDStreamer.Linux;
using VirtualDesktop.Core;
using VirtualDesktop.Net;

namespace VDStreamer.Streamer;

/// <summary>Runtime (non persisted) streamer state, mirrors the macOS app's DynamicSettings.</summary>
public class DynamicSettings : SettingsBase<DynamicSettings>
{
    private bool _isConnected;
    private bool _isLocalTrafficEncrypted;
    private MonitorInfo[] _monitors = Array.Empty<MonitorInfo>();
    private int _monitorStartIndex;
    private bool _cursorVisible = true;
    private int _latency;
    private string _routerUrl = string.Empty;
    private NetworkConnectionType _connectionType;
    private RoutingStatus _routingStatus;
    private bool _hasModifiedHostFile;
    private string _videosRootPath;
    private string _screenshotsRootPath;
    private string _statusMessage = "Starting";
    private string _activeEncoder;
    private string _captureDescription;

    public event EventHandler IsConnectedChanged;
    public event EventHandler MonitorStartIndexChanged;
    public event EventHandler CursorVisibleChanged;

    public bool IsConnected { get => _isConnected; set => Set(ref _isConnected, value, IsConnectedChanged); }

    public bool IsLocalTrafficEncrypted { get => _isLocalTrafficEncrypted; set => Set(ref _isLocalTrafficEncrypted, value); }

    public MonitorInfo[] Monitors { get => _monitors; set => Set(ref _monitors, value ?? Array.Empty<MonitorInfo>()); }

    public int MonitorStartIndex { get => _monitorStartIndex; set => Set(ref _monitorStartIndex, value, MonitorStartIndexChanged); }

    public bool CursorVisible { get => _cursorVisible; set => Set(ref _cursorVisible, value, CursorVisibleChanged); }

    public int Latency { get => _latency; set => Set(ref _latency, value); }

    public string RouterUrl { get => _routerUrl; set => Set(ref _routerUrl, value); }

    public NetworkConnectionType ConnectionType { get => _connectionType; set => Set(ref _connectionType, value); }

    public RoutingStatus RoutingStatus { get => _routingStatus; set => Set(ref _routingStatus, value); }

    public bool HasModifiedHostFile { get => _hasModifiedHostFile; set => Set(ref _hasModifiedHostFile, value); }

    public string VideosRootPath { get => _videosRootPath; set => Set(ref _videosRootPath, value); }

    public string ScreenshotsRootPath { get => _screenshotsRootPath; set => Set(ref _screenshotsRootPath, value); }

    /// <summary>Human readable status for the web UI / CLI.</summary>
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    public string ActiveEncoder { get => _activeEncoder; set => Set(ref _activeEncoder, value); }

    public string CaptureDescription { get => _captureDescription; set => Set(ref _captureDescription, value); }

    public bool ForceKeyFrame { get; set; }

    public bool HasSwitchedMonitor { get; set; }

    public string ComputerID { get; private set; }

    public void SwitchMonitor()
    {
        if (_monitors.Length > 1)
        {
            MonitorStartIndex = (_monitorStartIndex + 1) % _monitors.Length;
            HasSwitchedMonitor = true;
        }
    }

    public void RefreshFolders()
    {
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        string screenshots = settings.ScreenshotsRootPath;
        if (string.IsNullOrEmpty(screenshots) || !Directory.Exists(screenshots))
        {
            screenshots = XdgUserDir("XDG_PICTURES_DIR", "Pictures");
        }
        ScreenshotsRootPath = screenshots;

        string videos = settings.VideosRootPath;
        if (string.IsNullOrEmpty(videos) || !Directory.Exists(videos))
        {
            videos = XdgUserDir("XDG_VIDEOS_DIR", "Videos");
            if (videos == Vendor.Paths.Home)
            {
                videos = Path.Combine(Vendor.Paths.Home, "Videos"); // created below, like ~/Movies on macOS
            }
        }
        if (!videos.EndsWith(Path.DirectorySeparatorChar))
        {
            videos += Path.DirectorySeparatorChar;
        }
        try
        {
            Directory.CreateDirectory(videos);
        }
        catch
        {
        }
        VideosRootPath = videos;
    }

    /// <summary>Resolves a well-known user directory from ~/.config/user-dirs.dirs.</summary>
    private static string XdgUserDir(string key, string fallback)
    {
        string home = Vendor.Paths.Home;
        try
        {
            string file = Path.Combine(Vendor.Paths.ConfigHome, "user-dirs.dirs");
            if (File.Exists(file))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (line.StartsWith(key + "=", StringComparison.Ordinal))
                    {
                        string value = line.Substring(key.Length + 1).Trim().Trim('"').Replace("$HOME", home);
                        if (Directory.Exists(value))
                        {
                            return value;
                        }
                    }
                }
            }
        }
        catch
        {
        }
        string dir = Path.Combine(home, fallback);
        return Directory.Exists(dir) ? dir : home;
    }

    protected override void Initialize()
    {
        ComputerID = SystemInfo.GetComputerID();
        RefreshFolders();
    }

    private void Set<T>(ref T field, T value, EventHandler changed = null, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(name);
        changed?.Invoke(this, EventArgs.Empty);
    }
}
