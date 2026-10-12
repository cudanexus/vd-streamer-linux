using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using VirtualDesktop.Net;

namespace VDStreamer.Streamer;

/// <summary>
/// Access to the TCP sockets inside the vendor <see cref="NetClient"/>s. The vendor writes video
/// frames synchronously into the socket; on Linux the kernel grows a TCP send buffer up to 4 MB, so
/// when Wi-Fi throughput drops below the video bitrate, frames queue up in the kernel and the lag
/// grows to seconds. We read the queue size (SIOCOUTQ) to hold frames back instead, and set
/// keepalive/user timeouts so a dead connection (network change, Wi-Fi drop) is noticed in seconds.
/// </summary>
internal static class SocketTuning
{
    private static readonly Logger Logger = LogManager.GetLogger("Network");
    private static readonly FieldInfo ClientField = typeof(NetClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);

    private const int IPPROTO_TCP = 6;
    private const int TCP_USER_TIMEOUT = 18;
    private const ulong SIOCOUTQ = 0x5411; // == TIOCOUTQ: unsent + unacknowledged bytes

    public static Socket GetSocket(NetClient client)
    {
        try
        {
            return client == null ? null : (ClientField?.GetValue(client) as TcpClient)?.Client;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Bytes written to the socket that the headset has not acknowledged yet, or -1.</summary>
    public static int GetQueuedBytes(Socket socket)
    {
        try
        {
            int fd = (int)socket.SafeHandle.DangerousGetHandle();
            return fd >= 0 && ioctl(fd, SIOCOUTQ, out int queued) == 0 ? queued : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Makes a stalled connection fail after <paramref name="timeoutMs"/> instead of the kernel
    /// default of ~15 minutes of retransmissions, and probes idle connections.
    /// </summary>
    public static void Configure(NetClient client, int timeoutMs, int keepAliveIdleSeconds)
    {
        Socket socket = GetSocket(client);
        if (socket == null)
        {
            return;
        }
        try
        {
            socket.NoDelay = true;
            socket.SetRawSocketOption(IPPROTO_TCP, TCP_USER_TIMEOUT, BitConverter.GetBytes(timeoutMs));
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, keepAliveIdleSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Socket tuning");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, ulong request, out int value);
}

/// <summary>
/// Keeps the video latency bounded. Before a frame is written, waits until the socket queue holds
/// less than ~100 ms of video; while it waits, GStreamer's leaky queue drops captured frames, so the
/// next frame sent is a fresh one (lower frame rate instead of growing lag). If the network keeps
/// falling behind, the encoder bitrate is lowered, and raised again once it has recovered.
/// </summary>
internal sealed class VideoPacer
{
    private static readonly Logger Logger = LogManager.GetLogger("Network");

    private const int MinQueueBytes = 128 * 1024;
    private const double QueueSeconds = 0.1;
    private const int StallTimeoutMs = 3000;
    private const int MinBitrate = 2_000_000;

    private readonly object _lock = new();
    private Socket _socket;
    private int _targetBitrate;
    private int _bitrate;
    private long _windowStart = Stopwatch.GetTimestamp();
    private double _waitedMs;
    private int _calmWindows;
    private long _delayedFrames;

    /// <summary>Raised (on a streaming thread) with the new total bitrate for all monitors.</summary>
    public event Action<int> BitrateChanged;

    public bool Adaptive { get; set; } = true;

    public int Bitrate => _bitrate;

    public int TargetBitrate => _targetBitrate;

    public long DelayedFrames => Interlocked.Read(ref _delayedFrames);

    public int QueuedBytes
    {
        get
        {
            Socket socket = _socket;
            return socket == null ? 0 : Math.Max(0, SocketTuning.GetQueuedBytes(socket));
        }
    }

    public void Reset(Socket socket, int targetBitrate)
    {
        lock (_lock)
        {
            _socket = socket;
            _targetBitrate = _bitrate = targetBitrate;
            _waitedMs = 0;
            _calmWindows = 0;
            _windowStart = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>The headset changed the bitrate limit: start from the new value.</summary>
    public void SetTarget(int targetBitrate)
    {
        lock (_lock)
        {
            _targetBitrate = _bitrate = targetBitrate;
            _calmWindows = 0;
        }
    }

    /// <summary>
    /// Blocks until the network can take another frame. Returns false if the stream is stopping or
    /// the connection made no progress for a few seconds (the caller drops the frame).
    /// </summary>
    public bool WaitForRoom(Func<bool> stopping)
    {
        Socket socket = _socket;
        if (socket == null)
        {
            return true;
        }
        int limit = Math.Max(MinQueueBytes, (int)(_bitrate / 8 * QueueSeconds));
        int queued = SocketTuning.GetQueuedBytes(socket);
        if (queued <= limit)
        {
            Account(0);
            return true;
        }
        Interlocked.Increment(ref _delayedFrames);
        long start = Stopwatch.GetTimestamp();
        bool ok = true;
        while (true)
        {
            Thread.Sleep(1);
            if (stopping())
            {
                ok = false;
                break;
            }
            queued = SocketTuning.GetQueuedBytes(socket);
            if (queued < 0 || queued <= limit)
            {
                break;
            }
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds > StallTimeoutMs)
            {
                ok = false;
                break;
            }
        }
        Account(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return ok;
    }

    private void Account(double waitedMs)
    {
        int newBitrate = 0;
        lock (_lock)
        {
            _waitedMs += waitedMs;
            double windowMs = Stopwatch.GetElapsedTime(_windowStart).TotalMilliseconds;
            if (windowMs < 1000)
            {
                return;
            }
            double busy = _waitedMs / windowMs;
            _waitedMs = 0;
            _windowStart = Stopwatch.GetTimestamp();
            if (!Adaptive || _targetBitrate <= 0)
            {
                return;
            }
            int floor = Math.Min(_targetBitrate, Math.Max(MinBitrate, _targetBitrate / 6));
            if (busy > 0.15 && _bitrate > floor)
            {
                // Spent more than 15% of the last second waiting for the network: back off.
                _bitrate = Math.Max(floor, (int)(_bitrate * 0.7));
                _calmWindows = 0;
                newBitrate = _bitrate;
                Logger.Info($"Network is not keeping up ({busy:P0} of the time waiting): lowering video bitrate to {_bitrate / 1_000_000.0:F1} Mbps");
            }
            else if (busy < 0.01)
            {
                if (++_calmWindows >= 5 && _bitrate < _targetBitrate)
                {
                    _bitrate = Math.Min(_targetBitrate, (int)(_bitrate * 1.15) + 250_000);
                    _calmWindows = 0;
                    newBitrate = _bitrate;
                    Logger.Info($"Network recovered: raising video bitrate to {_bitrate / 1_000_000.0:F1} Mbps");
                }
            }
            else
            {
                _calmWindows = 0;
            }
        }
        if (newBitrate > 0)
        {
            BitrateChanged?.Invoke(newBitrate);
        }
    }
}
