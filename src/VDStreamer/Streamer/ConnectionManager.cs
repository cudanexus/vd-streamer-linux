using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Linux.Input;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.VR;

namespace VDStreamer.Streamer;

/// <summary>
/// Server registration, NAT traversal, local discovery hand-off and the four streaming channels
/// (messaging 38810, data 38820, video 38830, audio 38840). Same flow as the macOS streamer.
/// </summary>
internal static class ConnectionManager
{
    private static readonly Logger Logger = LogManager.GetLogger("ConnectionManager");
    private static readonly PriorityScheduler _networkChangedScheduler = new(ThreadPriority.Highest, 1);
    private static readonly BasicRateLimiter _connectRateLimiter = new(3, 4);
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static TaskCompletionSource<bool> _connectionTcs;
    private static CancellationTokenSource _connectToServerCts;
    private static CancellationTokenSource _networkChangedCts;
    private static bool _isRunning;
    private static string _computerName;
    private static Task<string> _computerDescriptionTask;
    private static RegistrationToken _token;
    private static Task<bool?> _isLatestVersion;
    private static NetworkAdapter[] _adapters;
    private static volatile int _connectingToServer;
    private static volatile bool _networkChanged;
    private static NetworkFingerprint _networkFingerprint;
    private static Timer _networkPollTimer;

    public static NetMessagingClient MessagingClient { get; private set; }

    public static NetDataClient DataClient { get; private set; }

    public static NetVideoClient VideoClient { get; private set; }

    public static NetAudioClient AudioClient { get; private set; }

    public static ConnectionStatus Status => MessagingClient?.Status ?? ConnectionStatus.Disconnected;

    public static string LastRegistrationError { get; private set; }

    public static void Initialize()
    {
        if (_isRunning)
        {
            return;
        }
        _isRunning = true;
        _computerDescriptionTask = SystemInfo.GetComputerDescriptionAsync();
        ServiceHelper<IComputerRegistry>.Initialize(HttpBinding.Default, NetHelper.GetServerUrl());
        NetClient.BindAccessError += OnBindAccessError;
        _isLatestVersion = UpdateHelper.IsLatestVersionAsync();

        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        settings.AllowRemoteConnectionsChanged += (_, _) => ReconnectToServer();
        settings.EncryptLocalTrafficChanged += (_, _) => ReconnectToServer();
        settings.AccountsChanged += (_, _) =>
        {
            RefreshLocalDiscoveryAsync().Forget();
            ReconnectToServer();
        };
        settings.AutoAdjustBitrateChanged += (_, _) => SettingsBase<SharedStreamerSettings>.Default.AutoAdjustBitrate = settings.AutoAdjustBitrate;
        SettingsBase<SharedStreamerSettings>.Default.AutoAdjustBitrate = settings.AutoAdjustBitrate;

        MessagingClient = new NetMessagingClient(new IPEndPoint(NetClient.AnyAddress, 38810));
        MessagingClient.StatusChanged += OnStatusChanged;
        MessagingClient.Message += OnMessage;
        SettingsBase<SharedUserSettings>.Default.InitializeAsClient(MessagingClient);
        SettingsBase<SharedMobileSettings>.Default.InitializeAsClient(MessagingClient);
        SettingsBase<SharedStreamerSettings>.Default.InitializeAsServer(MessagingClient);
        DataClient = new NetDataClient(new IPEndPoint(NetClient.AnyAddress, 38820));
        DataClient.InitializeFrameData<FrameData>(true);
        DataClient.StatusChanged += OnOtherClientStatusChanged;
        VideoClient = new NetVideoClient(new IPEndPoint(NetClient.AnyAddress, 38830));
        VideoClient.StatusChanged += OnOtherClientStatusChanged;
        AudioClient = new NetAudioClient(new IPEndPoint(NetClient.AnyAddress, 38840));
        AudioClient.StatusChanged += OnOtherClientStatusChanged;
        _computerName = SystemInfo.GetComputerName();

        Task.Run(UpdateRouterUrl).Forget();
        UpdateRoutingStatus();
        _networkFingerprint = NetworkFingerprint.Capture();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        // Netlink events can be missed (or only the router changes): also compare every 10 seconds.
        _networkPollTimer = new Timer(_ =>
        {
            if (!_isRunning || _networkChanged)
            {
                return;
            }
            NetworkFingerprint current = NetworkFingerprint.Capture();
            if (current.DiffersFrom(_networkFingerprint))
            {
                OnNetworkChanged(null, EventArgs.Empty);
            }
            else
            {
                _networkFingerprint.LearnMacs(current); // router MACs that were not in the ARP cache at startup
            }
        }, null, 10_000, 10_000);
        if (settings.HasValidAccountID)
        {
            RefreshLocalDiscoveryAsync().Forget();
            ConnectToServerAsync().Forget();
        }
        else
        {
            SettingsBase<DynamicSettings>.Default.StatusMessage = "Add your headset account name (Meta/Pico/...) to start";
            UpdateConnectionTypeAsync().Forget();
        }
    }

    /// <summary>
    /// (Re)starts local network discovery with the current accounts. Independent from the
    /// matchmaking server so LAN connections work offline and new accounts apply immediately.
    /// </summary>
    public static Task RefreshLocalDiscoveryAsync() => Task.Run(async () =>
    {
        try
        {
            StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
            if (!_isRunning || SettingsBase<DynamicSettings>.Default.IsConnected || !settings.GetAccessTokens(out PlatformAccessToken[] tokens))
            {
                return;
            }
            NetworkAdapter[] adapters = NetworkInterfaceHelper.GetPrivateAdapters(false, true, true, false).ToArray();
            if (adapters.Length == 0)
            {
                SettingsBase<DynamicSettings>.Default.StatusMessage = "No private network (LAN) connection found";
                return;
            }
            SettingsBase<DynamicSettings>.Default.IsLocalTrafficEncrypted = settings.EncryptLocalTraffic;
            string description = await _computerDescriptionTask.ConfigureAwait(false);
            DiscoveryManager.RegisterComputer(_computerName, description, OS.MacOS, adapters, tokens);
            if (Status != ConnectionStatus.ConnectedToServer && !SettingsBase<DynamicSettings>.Default.IsConnected)
            {
                SettingsBase<DynamicSettings>.Default.StatusMessage = "Ready - waiting for the headset (local network)";
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Local discovery");
        }
    });

    public static Task DisconnectAsync() => Task.Run(() => MessagingClient?.Disconnect());

    public static void Shutdown()
    {
        if (!_isRunning)
        {
            return;
        }
        _isRunning = false;
        _networkPollTimer?.Dispose();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetClient.BindAccessError -= OnBindAccessError;
        MessagingClient.Dispose();
        DataClient.Dispose();
        VideoClient.Dispose();
        AudioClient.Dispose();
    }

    public static Task ConnectToServerAsync()
    {
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        if (_networkChanged || MessagingClient.Status != ConnectionStatus.Disconnected || !settings.HasValidAccountID ||
            Interlocked.CompareExchange(ref _connectingToServer, 1, 0) == 1)
        {
            return Task.CompletedTask;
        }
        return Task.Run(async () =>
        {
            try
            {
                NetworkAdapter[] privateAdapters = NetworkInterfaceHelper.GetPrivateAdapters(false, true, true, false).ToArray();
                SettingsBase<DynamicSettings>.Default.ConnectionType = NetworkInterfaceHelper.GetConnectionType(privateAdapters);
                if (privateAdapters.Length == 0)
                {
                    SettingsBase<DynamicSettings>.Default.StatusMessage = "No private network (LAN) connection found";
                    return;
                }
                if (!settings.GetAccessTokens(out PlatformAccessToken[] accessTokens))
                {
                    return;
                }
                _adapters = privateAdapters;
                SettingsBase<DynamicSettings>.Default.IsLocalTrafficEncrypted = settings.EncryptLocalTraffic;
                string computerDescription = await _computerDescriptionTask.ConfigureAwait(false);
                var cts = new CancellationTokenSource();
                Interlocked.Exchange(ref _connectToServerCts, cts)?.Dispose();

                bool isLatest = (await _isLatestVersion.ConfigureAwait(false)).GetValueOrDefault(true);
                if (isLatest)
                {
                    DiscoveryManager.RegisterComputer(_computerName, computerDescription, OS.MacOS, privateAdapters, accessTokens);
                }
                else
                {
                    SettingsBase<DynamicSettings>.Default.StatusMessage = "A newer Virtual Desktop release is required - updating vendor libraries...";
                    UpdateHelper.CheckForUpdatesAsync().Forget();
                }

                _connectionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (cts.IsCancellationRequested || MessagingClient.Status != ConnectionStatus.Disconnected)
                {
                    return;
                }
                await _connectRateLimiter.LimitAsync().ConfigureAwait(false);
                bool allowRemoteConnections = settings.AllowRemoteConnections;
                NetworkAdapter[] adapters = NetworkInterfaceHelper.GetPrivateAdapters(false, true, false, false).ToArray();
                if (adapters.Length == 0)
                {
                    return;
                }
                try
                {
                    RegistrationToken registrationToken = ServiceHelper<IComputerRegistry>.ExecuteWithRateLimit(s =>
                        cts.IsCancellationRequested || MessagingClient.Status != ConnectionStatus.Disconnected
                            ? null
                            : s.RegisterComputer5(accessTokens, SettingsBase<DynamicSettings>.Default.ComputerID, _computerName, computerDescription,
                                OS.MacOS, AssemblyHelper.Version, adapters, allowRemoteConnections, SettingsBase<DynamicSettings>.Default.IsLocalTrafficEncrypted, true));
                    if (registrationToken == null)
                    {
                        LastRegistrationError = "Could not reach the Virtual Desktop server";
                        SettingsBase<DynamicSettings>.Default.StatusMessage = LastRegistrationError + " (local discovery still works)";
                        return;
                    }
                    if (!registrationToken.IsValid)
                    {
                        LastRegistrationError = "Server rejected streamer version " + AssemblyHelper.Version;
                        _isLatestVersion = Task.FromResult((bool?)false);
                        DiscoveryManager.UnregisterComputer();
                        SettingsBase<DynamicSettings>.Default.StatusMessage = "Streamer needs to be updated - downloading the latest vendor libraries...";
                        if (!(await UpdateHelper.CheckForUpdatesAsync().ConfigureAwait(false)).GetValueOrDefault())
                        {
                            SettingsBase<DynamicSettings>.Default.StatusMessage = "Streamer needs to be updated. Run 'vdstreamer update'.";
                        }
                        return;
                    }
                    LastRegistrationError = null;
                    _token = registrationToken;
                    if (MessagingClient.Status != ConnectionStatus.Disconnected)
                    {
                        UnregisterComputerAsync().Forget();
                        return;
                    }
                    settings.IncrementServerRotation();
                    await MessagingClient.ConnectToServerAsync(NetHelper.Region, registrationToken.ConnectionID, registrationToken.Aes, 38811 + settings.ServerRotation).ConfigureAwait(false);
                    // Notice a dead server connection (e.g. after switching networks) within ~30 s.
                    SocketTuning.Configure(MessagingClient, 30_000, 15);
                    MessagingClient.Start();
                    if (MessagingClient.Status == ConnectionStatus.ConnectedToServer || MessagingClient.Status == ConnectionStatus.ConnectingToServer)
                    {
                        SettingsBase<DynamicSettings>.Default.StatusMessage = "Ready - waiting for the headset to connect";
                    }
                    UpdateConnectionTypeAsync().Forget();
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "Registering with the Virtual Desktop server failed");
                    LastRegistrationError = ex.Message;
                    SocketException socketException = (ex as SocketException) ?? ((ex as AggregateException)?.GetBaseException() as SocketException);
                    if (socketException != null && (socketException.SocketErrorCode == SocketError.AddressAlreadyInUse || socketException.ErrorCode == 10048))
                    {
                        SettingsBase<DynamicSettings>.Default.StatusMessage = "Ports 38810-38850 are in use (is another streamer running?)";
                        await Task.Delay(15000).ConfigureAwait(false);
                    }
                }
                finally
                {
                    SettingsBase<DynamicSettings>.Default.ConnectionType = NetworkInterfaceHelper.GetConnectionType();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ConnectToServerAsync");
            }
            finally
            {
                Interlocked.Exchange(ref _connectingToServer, 0);
                switch (Status)
                {
                    case ConnectionStatus.ConnectingToServer:
                        MessagingClient.Disconnect();
                        break;
                    case ConnectionStatus.Disconnected:
                        if (_isRunning)
                        {
                            Task.Delay(5000).ContinueWith(_ => ConnectToServerAsync()).Forget();
                        }
                        break;
                }
            }
        });
    }

    public static async Task ConnectToPeerAsync(ServerRegion region, byte[] connectionID, bool encryptTraffic, IPEndPoint publicEP, Aes aes)
    {
        try
        {
            SettingsBase<DynamicSettings>.Default.StatusMessage = "Headset connecting...";
            if (connectionID != null)
            {
                await MessagingClient.ConnectToPeerAsServerAsync(NetClient.AnyAddress, publicEP, aes, true).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                IPAddress localAddress = MessagingClient.LocalEndPoint?.Address;
                if (localAddress == null)
                {
                    MessagingClient.Disconnect();
                    return;
                }
                int serverRotation = SettingsBase<StreamerSettings>.Default.ServerRotation;
                bool allowRemoteConnections = SettingsBase<StreamerSettings>.Default.AllowRemoteConnections;
                await VideoClient.ConnectToPeerAsServerAsync(region, connectionID, aes, localAddress, 38831 + serverRotation, allowRemoteConnections, encryptTraffic).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                await AudioClient.ConnectToPeerAsServerAsync(region, connectionID, aes, localAddress, 38841 + serverRotation, allowRemoteConnections, encryptTraffic).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                await DataClient.ConnectToPeerAsServerAsync(region, connectionID, aes, localAddress, 38821 + serverRotation, allowRemoteConnections, encryptTraffic).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
            }
            else
            {
                await MessagingClient.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, null, 38810, true).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                await VideoClient.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, null, 38830, encryptTraffic).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                await AudioClient.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, null, 38840, encryptTraffic).ConfigureAwait(false);
                if (MessagingClient.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                await DataClient.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, null, 38820, encryptTraffic).ConfigureAwait(false);
            }
            if (MessagingClient.Status == ConnectionStatus.ConnectedToPeer)
            {
                DataClient.SendBitrateData(SettingsBase<SharedStreamerSettings>.Default.AutoAdjustBitrate ? 5000000 : 0);
                await Task.WhenAny(_connectionTcs.Task, SettingsBase<SharedMobileSettings>.Default.GetMeasuredBandwidthAsync()).ConfigureAwait(false);
                if (MessagingClient.IsConnected(true) && VideoClient.IsConnected(true) && AudioClient.IsConnected(true) && DataClient.IsConnected(true))
                {
                    // A stalled session (Wi-Fi drop, network change) fails after 10 s instead of ~15 min of
                    // TCP retransmissions, so the PC is discoverable again and the headset can reconnect.
                    foreach (NetClient client in new NetClient[] { MessagingClient, VideoClient, AudioClient, DataClient })
                    {
                        SocketTuning.Configure(client, 10_000, 5);
                    }
                    SettingsBase<DynamicSettings>.Default.StatusMessage = "Connected to " + DescribeHeadset();
                    SettingsBase<DynamicSettings>.Default.IsConnected = true;
                    DiscoveryManager.UnregisterComputer();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "ConnectToPeerAsync");
            if (!SettingsBase<DynamicSettings>.Default.IsConnected)
            {
                MessagingClient.Disconnect();
            }
        }
        finally
        {
            UnregisterComputerAsync().Forget();
        }
    }

    public static string DescribeHeadset()
    {
        HmdType hmd = SettingsBase<SharedMobileSettings>.Default.HmdType;
        string name = SettingsBase<StreamerSettings>.Default.HeadsetName;
        return string.IsNullOrEmpty(name) ? hmd.ToString() : $"{name} ({hmd})";
    }

    private static Task UnregisterComputerAsync()
    {
        RegistrationToken token = Interlocked.Exchange(ref _token, null);
        if (token != null)
        {
            return ServiceHelper<IComputerRegistry>.ExecuteAsync(s => s.UnregisterComputer3(token.ConnectionID));
        }
        return Task.CompletedTask;
    }

    private static void OnStatusChanged(object sender, EventArgs e)
    {
        ConnectionStatus status = MessagingClient.Status;
        Logger.Info("Messaging status: " + status);
        switch (status)
        {
            case ConnectionStatus.ConnectingToPeer:
                if (!DiscoveryManager.Connecting && SettingsBase<StreamerSettings>.Default.AllowRemoteConnections)
                {
                    IPAddress address = MessagingClient.LocalEndPoint?.Address;
                    if (address != null && (address.AddressFamily == AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6))
                    {
                        UPnPManager.AddPortMappingsAsync(address).Forget();
                    }
                }
                break;
            case ConnectionStatus.ConnectedToServer:
                SettingsBase<DynamicSettings>.Default.StatusMessage = "Ready - waiting for the headset to connect";
                break;
            case ConnectionStatus.Disconnected:
            {
                _connectionTcs?.TrySetCanceled();
                Task unregister = UnregisterComputerAsync();
                bool wasConnected = SettingsBase<DynamicSettings>.Default.IsConnected;
                SettingsBase<DynamicSettings>.Default.IsConnected = false;
                DataClient.Disconnect();
                VideoClient.Disconnect();
                AudioClient.Disconnect();
                SettingsBase<SharedUserSettings>.Default.ExtraDisplay = false;
                if (StreamingSourceExtensions.IsVideo(SettingsBase<SharedStreamerSettings>.Default.StreamingSource))
                {
                    SettingsBase<SharedStreamerSettings>.Default.StreamingSource = StreamingSource.Desktop;
                }
                if (wasConnected)
                {
                    SettingsBase<DynamicSettings>.Default.StatusMessage = "Headset disconnected";
                }
                if (_isRunning)
                {
                    if (!SettingsBase<StreamerSettings>.Default.AllowRemoteConnections)
                    {
                        UPnPManager.ClearPortMappingsAsync().Forget();
                    }
                    Task.Delay(10).ContinueWith(_ => RefreshLocalDiscoveryAsync()).Forget();
                    Task.Delay(10).ContinueWith(_ => ConnectToServerAsync()).Forget();
                }
                else
                {
                    Task.WhenAll(unregister, UPnPManager.ClearPortMappingsAsync()).Wait(4000);
                }
                break;
            }
        }
    }

    private static void ReconnectToServer()
    {
        ConnectionStatus status = Status;
        if (status is ConnectionStatus.ConnectingToServer or ConnectionStatus.ConnectedToServer)
        {
            Task.Run(() => MessagingClient.Disconnect());
        }
        else if (status == ConnectionStatus.Disconnected && SettingsBase<StreamerSettings>.Default.HasValidAccountID)
        {
            ConnectToServerAsync().Forget();
        }
        if (!SettingsBase<DynamicSettings>.Default.IsConnected && !SettingsBase<StreamerSettings>.Default.AllowRemoteConnections)
        {
            UPnPManager.ClearPortMappingsAsync().Forget();
        }
    }

    private static void UpdateRouterUrl()
    {
        try
        {
            string routerUrl = string.Empty;
            foreach (string gateway in NetworkInterfaceHelper.GetDefaultGateways().Select(a => a.ToString()))
            {
                if (ValidateAddress("https://" + gateway))
                {
                    routerUrl = "https://" + gateway;
                    break;
                }
                if (ValidateAddress("http://" + gateway))
                {
                    routerUrl = "http://" + gateway;
                    break;
                }
            }
            SettingsBase<DynamicSettings>.Default.RouterUrl = routerUrl;
        }
        catch
        {
            SettingsBase<DynamicSettings>.Default.RouterUrl = string.Empty;
        }
    }

    private static bool ValidateAddress(string address)
    {
        try
        {
            return _httpClient.Send(new HttpRequestMessage(HttpMethod.Head, address)).StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    private static Task UpdateConnectionTypeAsync() =>
        Task.Run(() => SettingsBase<DynamicSettings>.Default.ConnectionType = NetworkInterfaceHelper.GetConnectionType());

    private static void UpdateRoutingStatus()
    {
        TraceRoute.GetRoutingStatusAsync().ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
            {
                SettingsBase<DynamicSettings>.Default.RoutingStatus = t.Result;
            }
        }).Forget();
    }

    private static void OnNetworkChanged(object sender, EventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }
        _networkChanged = true;
        _connectToServerCts?.CancelSafe();
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _networkChangedCts, cts)?.CancelSafe();
        Task.Delay(2000).ContinueWith(_ => OnNetworkChangedCallback(), cts.Token, TaskContinuationOptions.None, _networkChangedScheduler);
    }

    private static async void OnNetworkChangedCallback()
    {
        if (!_isRunning)
        {
            return;
        }
        _networkChanged = false;
        NetworkFingerprint fingerprint = NetworkFingerprint.Capture();
        bool changed = fingerprint.DiffersFrom(_networkFingerprint);
        _networkFingerprint = fingerprint.MergeKnownMacs(_networkFingerprint);
        if (changed)
        {
            Logger.Info("Network changed: " + _networkFingerprint);
        }
        if (SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            // A headset is streaming. If the address its connection uses is gone, the session is dead:
            // end it now so the PC becomes discoverable on the new network right away.
            if (changed && !IsLocalAddressPresent(MessagingClient.LocalEndPoint?.Address))
            {
                Logger.Info("The network used by the headset session went away; disconnecting");
                MessagingClient.Disconnect();
            }
        }
        else if (changed)
        {
            // New addresses/router: restart LAN discovery (its multicast memberships and the address
            // list announced to the headset are fixed at start) and register again with the server
            // (the old server connection may be dead and the public address is different).
            DiscoveryManager.UnregisterComputer();
            switch (Status)
            {
                case ConnectionStatus.ConnectingToServer:
                case ConnectionStatus.ConnectedToServer:
                    MessagingClient.Disconnect(); // OnStatusChanged(Disconnected) refreshes discovery and reconnects
                    break;
                case ConnectionStatus.Disconnected:
                    RefreshLocalDiscoveryAsync().Forget();
                    if (SettingsBase<StreamerSettings>.Default.HasValidAccountID)
                    {
                        ConnectToServerAsync().Forget();
                    }
                    else
                    {
                        UpdateConnectionTypeAsync().Forget();
                    }
                    break;
            }
        }
        else if (Status == ConnectionStatus.Disconnected && SettingsBase<StreamerSettings>.Default.HasValidAccountID)
        {
            ConnectToServerAsync().Forget();
        }
        if (changed)
        {
            Task.Run(UpdateRouterUrl).Forget();
            UpdateRoutingStatus();
        }
        if (!(await _isLatestVersion.ConfigureAwait(false)).HasValue)
        {
            _isLatestVersion = UpdateHelper.IsLatestVersionAsync();
        }
    }

    private static bool IsLocalAddressPresent(IPAddress address)
    {
        if (address == null)
        {
            return true;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => u.Address.Equals(address));
        }
        catch
        {
            return true;
        }
    }

    private static async void OnMessage(object sender, NetMessage e)
    {
        try
        {
            switch (e.MessageType)
            {
                case MessageType.PeerInfo:
                {
                    NetworkBinaryReader reader = e.Reader;
                    IPEndPoint publicEP = reader.ReadIPEndPoint();
                    bool encryptTraffic = !reader.ReadBoolean();
                    RegistrationToken token = _token;
                    if (token != null)
                    {
                        ConnectToPeerAsync(NetHelper.Region, token.ConnectionID, encryptTraffic, SettingsBase<StreamerSettings>.Default.AllowRemoteConnections ? publicEP : null, token.Aes).Forget();
                    }
                    break;
                }
                case MessageType.CrashCallstack:
                    Logger.Warn("Headset crash report: " + e.Reader.ReadString() + "\n" + e.Reader.ReadString());
                    break;
                case MessageType.UpdateRequired:
                    if (!(await UpdateHelper.CheckForUpdatesAsync(() => NetMessage.CreateOutgoing(MessageType.UpdateInProgress).SendTo(MessagingClient)).ConfigureAwait(false)).GetValueOrDefault())
                    {
                        if (MessagingClient.Status == ConnectionStatus.ConnectedToPeer)
                        {
                            NetMessage.CreateOutgoing(MessageType.UpdateUnavailable).SendTo(MessagingClient);
                        }
                    }
                    break;
                case MessageType.ForceKeyFrame:
                    StreamerManager.InvokeAsync(() =>
                    {
                        SettingsBase<DynamicSettings>.Default.ForceKeyFrame = true;
                        if (HmdResolutionTypeExtensions.IsMauiDevice(SettingsBase<SharedMobileSettings>.Default.HmdType) &&
                            SettingsBase<SharedStreamerSettings>.Default.StreamingSource == StreamingSource.Desktop)
                        {
                            StreamerManager.DesktopStreamer?.Restart();
                        }
                    }).Forget();
                    break;
                case MessageType.SwitchMonitor:
                    SettingsBase<DynamicSettings>.Default.SwitchMonitor();
                    break;
                case MessageType.AddMonitor:
                    StreamerManager.InvokeAsync(() => StreamerManager.DesktopStreamer?.AddMonitor()).Forget();
                    break;
                case MessageType.RemoveMonitor:
                    StreamerManager.InvokeAsync(() => StreamerManager.DesktopStreamer?.RemoveMonitor()).Forget();
                    break;
                case MessageType.SetMonitorPortrait:
                    e.Reader.ReadInt32();
                    e.Reader.ReadBoolean();
                    break;
                case MessageType.ChangeLanguage:
                    InputInjector.ChangeLanguage();
                    break;
                case MessageType.ScreenshotToDesktop:
                {
                    string fileName = Path.GetFileName(e.Reader.ReadString());
                    int length = e.Reader.ReadInt32();
                    byte[] data = e.Reader.ReadBytes(length);
                    Task.Run(() =>
                    {
                        try
                        {
                            string root = SettingsBase<DynamicSettings>.Default.ScreenshotsRootPath;
                            if (root != null)
                            {
                                File.WriteAllBytes(Path.Combine(root, fileName), data);
                                SystemInfo.Notify("Screenshot saved", Path.Combine(root, fileName));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to save screenshot");
                            SystemInfo.Notify("Failed to save screenshot", ex.Message);
                        }
                    }).Forget();
                    break;
                }
                case MessageType.Exit:
                    Logger.Info("Headset requested the streamer to exit");
                    App.RequestExit();
                    break;
                case MessageType.WakeUpComputer:
                {
                    int count = e.Reader.ReadInt32();
                    var macs = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        macs.Add(e.Reader.ReadString());
                    }
                    if (await WOLHelper.WakeUpComputerAsync(macs).ConfigureAwait(false) && Status == ConnectionStatus.ConnectedToPeer)
                    {
                        NetMessage.CreateOutgoing(MessageType.ComputerAwaken).SendTo(MessagingClient);
                    }
                    break;
                }
                case MessageType.RequestCursor:
                    e.Reader.ReadInt64();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error handling message " + e.MessageType);
        }
    }

    private static void OnBindAccessError(object sender, EventArgs e)
    {
        Logger.Error("Could not bind the streaming ports (38810-38850)");
        SettingsBase<DynamicSettings>.Default.StatusMessage = "Could not bind the streaming ports 38810-38850";
    }

    private static void OnOtherClientStatusChanged(object sender, EventArgs e)
    {
        if (((NetClient)sender).Status == ConnectionStatus.Disconnected)
        {
            MessagingClient?.Disconnect();
        }
    }
}

/// <summary>
/// Identity of the current network: private addresses plus each default gateway and its MAC
/// address (so moving to another router is noticed even if DHCP hands out the same address).
/// </summary>
internal sealed class NetworkFingerprint
{
    private string _addresses = string.Empty;
    private Dictionary<string, string> _gateways = new();

    public static NetworkFingerprint Capture()
    {
        var f = new NetworkFingerprint();
        try
        {
            f._addresses = string.Join(",", NetworkInterfaceHelper.GetPrivateAdapters(false, true, true, false)
                .Select(a => a.IPAddresString)
                .OrderBy(a => a, StringComparer.Ordinal));
        }
        catch
        {
        }
        foreach (string gateway in SystemInfo.GetDefaultGateways())
        {
            int eq = gateway.IndexOf('=');
            string mac = gateway.Substring(eq + 1);
            f._gateways[gateway.Substring(0, eq)] = mac.Length == 0 || mac == "00:00:00:00:00:00" ? null : mac;
        }
        return f;
    }

    public bool DiffersFrom(NetworkFingerprint other)
    {
        if (other == null || _addresses != other._addresses || !_gateways.Keys.OrderBy(k => k).SequenceEqual(other._gateways.Keys.OrderBy(k => k)))
        {
            return true;
        }
        // A MAC that is missing from the ARP cache right now is not a change.
        return _gateways.Any(g => g.Value != null && other._gateways[g.Key] != null && g.Value != other._gateways[g.Key]);
    }

    public void LearnMacs(NetworkFingerprint current)
    {
        foreach (KeyValuePair<string, string> g in current._gateways)
        {
            if (g.Value != null && _gateways.TryGetValue(g.Key, out string mac) && mac == null)
            {
                _gateways[g.Key] = g.Value;
            }
        }
    }

    public NetworkFingerprint MergeKnownMacs(NetworkFingerprint previous)
    {
        if (previous != null)
        {
            foreach (string key in _gateways.Keys.ToList())
            {
                if (_gateways[key] == null && previous._gateways.TryGetValue(key, out string mac))
                {
                    _gateways[key] = mac;
                }
            }
        }
        return this;
    }

    public override string ToString() => _addresses.Length == 0 && _gateways.Count == 0
        ? "no connection"
        : $"{_addresses} via {string.Join(", ", _gateways.Select(g => g.Key + (g.Value != null ? " (" + g.Value + ")" : string.Empty)))}";
}
