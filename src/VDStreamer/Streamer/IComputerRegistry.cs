using System;
using System.ServiceModel;
using VirtualDesktop.Interfaces;

namespace VDStreamer.Streamer;

/// <summary>
/// SOAP contract of the Virtual Desktop matchmaking service, as declared by the official
/// streamer (contract name "IComputerRegistry", default tempuri namespace). Data contracts
/// come from the vendor VirtualDesktop.Interfaces assembly.
/// </summary>
[ServiceContract(Name = "IComputerRegistry")]
public interface IComputerRegistry
{
    [OperationContract(AsyncPattern = true)]
    IAsyncResult BeginGetComputers2(PlatformAccessToken accessToken, AsyncCallback callback, object state);

    Computer[] EndGetComputers2(IAsyncResult result);

    [OperationContract]
    RegistrationToken RegisterComputer5(PlatformAccessToken[] accessTokens, string id, string name, string description, OS os, Version version, NetworkAdapter[] privateAdapters, bool allowRemoteConnections, bool encryptLocalTraffic, bool encryptRemoteTraffic);

    [OperationContract(IsOneWay = true)]
    void UnregisterComputer3(byte[] connectionID);
}
