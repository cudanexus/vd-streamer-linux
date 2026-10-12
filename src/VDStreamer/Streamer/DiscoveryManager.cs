using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using NLog;
using VDStreamer.Linux;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Net;

namespace VDStreamer.Streamer;

public sealed record PairingRequest(Guid Id, Platform Platform, string AccountID, DateTime Received);

/// <summary>
/// Local network discovery (the headset finds the PC directly on the LAN) and pairing
/// requests ("Allow Meta user X to access this computer?"). Pairing prompts are shown in the
/// web UI / CLI and as a desktop notification instead of a modal NSAlert.
/// </summary>
internal static class DiscoveryManager
{
    private static readonly Logger Logger = LogManager.GetLogger("Discovery");
    private static readonly ConcurrentDictionary<Guid, PairingRequest> _pairingRequests = new();

    private static readonly object _lock = new();
    private static Aes _aes;
    private static Computer _computer;
    private static ComputerDiscoveryClient _client;
    private static PlatformAccessToken[] _accessTokens;

    public static bool Connecting { get; private set; }

    public static IReadOnlyCollection<PairingRequest> PairingRequests => _pairingRequests.Values.OrderBy(r => r.Received).ToList();

    public static void RegisterComputer(string computerName, string computerDescription, OS os, NetworkAdapter[] privateAdapters, PlatformAccessToken[] accessTokens)
    {
        lock (_lock)
        {
            RegisterComputerLocked(computerName, computerDescription, os, privateAdapters, accessTokens);
        }
    }

    private static void RegisterComputerLocked(string computerName, string computerDescription, OS os, NetworkAdapter[] privateAdapters, PlatformAccessToken[] accessTokens)
    {
        if (_client != null && _computer.PrivateAdapters.SequenceEqual(privateAdapters) && accessTokens.SequenceEqual(_accessTokens))
        {
            return;
        }
        UnregisterComputerLocked();
        _aes?.Dispose();
        _aes = Aes.Create();
        _aes.GenerateKey();
        _aes.GenerateIV();
        _aes.Padding = PaddingMode.None;
        _accessTokens = accessTokens;
        _computer = new Computer(SettingsBase<DynamicSettings>.Default.ComputerID, computerName, computerDescription, os, AssemblyHelper.Version,
            Guid.NewGuid(), privateAdapters, false, SettingsBase<DynamicSettings>.Default.IsLocalTrafficEncrypted, true, _aes.IV, _aes.Key);
        _client = new ComputerDiscoveryClient();
        _client.PairingRequest += OnPairingRequest;
        _client.ConnectionRequest += OnConnectionRequest;
        _client.StartListening(new HashSet<PlatformAccessToken>(accessTokens), _computer);
        Logger.Info($"Listening for headsets on the local network as '{computerName}'");
    }

    public static void UnregisterComputer()
    {
        lock (_lock)
        {
            UnregisterComputerLocked();
        }
    }

    private static void UnregisterComputerLocked()
    {
        ComputerDiscoveryClient client = Interlocked.Exchange(ref _client, null);
        if (client != null)
        {
            client.PairingRequest -= OnPairingRequest;
            client.ConnectionRequest -= OnConnectionRequest;
            client.Dispose();
        }
        Connecting = false;
    }

    public static bool RespondToPairing(Guid id, bool accept, bool dontAskAgain = false)
    {
        if (!_pairingRequests.TryRemove(id, out PairingRequest request))
        {
            return false;
        }
        if (dontAskAgain)
        {
            SettingsBase<StreamerSettings>.Default.ShowPairingRequests = false;
        }
        if (accept && SettingsBase<StreamerSettings>.Default.TryAddAccountID(request.Platform, request.AccountID))
        {
            SettingsBase<StreamerSettings>.Default.Save();
            Logger.Info($"Paired {request.Platform} account '{request.AccountID}'");
            switch (ConnectionManager.Status)
            {
                case ConnectionStatus.ConnectingToServer:
                case ConnectionStatus.ConnectedToServer:
                    ConnectionManager.DisconnectAsync().Forget();
                    break;
                case ConnectionStatus.Disconnected:
                    ConnectionManager.ConnectToServerAsync().Forget();
                    break;
            }
        }
        return true;
    }

    private static void OnPairingRequest(object sender, PairingRequestEventArgs e)
    {
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        if (!settings.ShowPairingRequests || !settings.CanAddAccountID(e.Platform, e.AccountID))
        {
            return;
        }
        if (_pairingRequests.Values.Any(r => r.Platform == e.Platform && r.AccountID == e.AccountID))
        {
            return;
        }
        var request = new PairingRequest(Guid.NewGuid(), e.Platform, e.AccountID, DateTime.UtcNow);
        _pairingRequests[request.Id] = request;
        string platform = e.Platform <= Platform.OculusQuest ? "Meta" : e.Platform.ToString();
        Logger.Info($"Pairing request from {platform} user '{e.AccountID}'");
        SystemInfo.Notify("Virtual Desktop pairing request",
            $"Allow {platform} user '{e.AccountID}' to access this computer? Open http://127.0.0.1:{settings.WebUiPort} or run 'vdstreamer pair accept'.");
    }

    private static void OnConnectionRequest(object sender, ConnectionRequestEventArgs e)
    {
        // The vendor discovery client disposes itself after a connection request; forget it so the
        // next RegisterComputer() starts listening again if this connection attempt fails.
        if (Interlocked.CompareExchange(ref _client, null, (ComputerDiscoveryClient)sender) == sender)
        {
            ((ComputerDiscoveryClient)sender).PairingRequest -= OnPairingRequest;
            ((ComputerDiscoveryClient)sender).ConnectionRequest -= OnConnectionRequest;
        }
        Connecting = true;
        try
        {
            ConnectionManager.ConnectToPeerAsync(NetHelper.Region, null, e.EncryptTraffic, null, _aes).Forget();
        }
        finally
        {
            Connecting = false;
        }
    }
}
