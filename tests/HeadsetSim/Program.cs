using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.VR;

namespace HeadsetSim;

/// <summary>
/// Simulated headset used to test vdstreamer end to end on a LAN (no real headset needed):
///   1. local discovery broadcast with an RSA key + account name (UDP 38850),
///   2. decrypts the Computer record returned by the streamer,
///   3. sends the connection request and opens messaging/video/audio/data channels,
///   4. publishes headset settings (HMD type, bandwidth...) and records the received video.
/// </summary>
public static class Program
{
    private static readonly byte[] BroadcastAesKey = { 32, 71, 236, 94, 194, 34, 85, 255, 165, 172, 187, 150, 6, 104, 106, 57, 57, 62, 244, 114, 75, 174, 237, 9, 48, 36, 239, 82, 57, 98, 205, 80 };
    private static readonly byte[] BroadcastAesIV = { 82, 200, 129, 118, 144, 104, 249, 4, 62, 20, 120, 110, 20, 180, 63, 31 };

    public static int Main(string[] args)
    {
        string vendor = Environment.GetEnvironmentVariable("VDSTREAMER_VENDOR_DIR") ?? "/opt/vdstreamer/vendor";
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string p = Path.Combine(vendor, name.Name + ".dll");
            return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
        };
        return Run(args).GetAwaiter().GetResult();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> Run(string[] args)
    {
        string account = Arg(args, "--account") ?? "vdtest-user";
        int seconds = int.Parse(Arg(args, "--seconds") ?? "15");
        string output = Arg(args, "--out") ?? "received.h264";
        bool annexB = (Arg(args, "--annexb") ?? "true") == "true";
        var platform = (Platform)int.Parse(Arg(args, "--platform") ?? "1");

        // ---- 1. discovery ----------------------------------------------------------------------
        using RSA rsa = RSA.Create(1024);
        string publicKeyXml = rsa.ToXmlString(false);
        if (publicKeyXml.Length != 243)
        {
            Console.WriteLine($"unexpected RSA XML length {publicKeyXml.Length}");
            return 2;
        }
        byte[] accountBytes = Encoding.UTF8.GetBytes(account);
        byte[] plain = new byte[243 + 2 + accountBytes.Length];
        Encoding.ASCII.GetBytes(publicKeyXml, 0, 243, plain, 0);
        plain[243] = 0;
        plain[244] = (byte)platform;
        accountBytes.CopyTo(plain, 245);

        byte[] request;
        using (Aes broadcastAes = Aes.Create())
        {
            broadcastAes.Key = BroadcastAesKey;
            broadcastAes.IV = BroadcastAesIV;
            broadcastAes.Padding = PaddingMode.None;
            using var ms = new MemoryStream();
            using (var aesStream = new AesStream(ms, broadcastAes))
            {
                aesStream.Write(plain, 0, plain.Length);
            }
            request = ms.ToArray();
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        Console.WriteLine($"[sim] broadcasting discovery for {platform} account '{account}'");
        UdpReceiveResult response = default;
        bool got = false;
        Stopwatch discoveryTime = Stopwatch.StartNew();
        Task<UdpReceiveResult> receive = null;
        int timeoutSeconds = int.Parse(Arg(args, "--discovery-timeout") ?? "30");
        while (!got && discoveryTime.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            foreach (IPAddress target in BroadcastTargets())
            {
                try
                {
                    await udp.SendAsync(request, request.Length, new IPEndPoint(target, 38850));
                }
                catch (SocketException)
                {
                }
            }
            receive ??= udp.ReceiveAsync(); // keep one pending receive across retries
            if (await Task.WhenAny(receive, Task.Delay(1000)) == receive)
            {
                response = receive.Result;
                receive = null;
                got = response.Buffer.Length > 128;
            }
        }
        if (!got)
        {
            Console.WriteLine("[sim] FAIL: no discovery response (is the account configured on the streamer?)");
            return 3;
        }
        IPAddress streamerIp = response.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? response.RemoteEndPoint.Address.MapToIPv4() : response.RemoteEndPoint.Address;
        byte[] keyIv = rsa.Decrypt(response.Buffer.AsSpan(0, 128).ToArray(), RSAEncryptionPadding.OaepSHA1);
        Computer computer;
        using (Aes computerAes = Aes.Create())
        {
            computerAes.Key = keyIv.AsSpan(0, 32).ToArray();
            computerAes.IV = keyIv.AsSpan(32, 16).ToArray();
            computerAes.Padding = PaddingMode.None;
            using var aesStream = new AesStream(new MemoryStream(response.Buffer, 128, response.Buffer.Length - 128), computerAes.CreateDecryptor());
            computer = (Computer)new DataContractSerializer(typeof(Computer)).ReadObject(new BufferedReadStream(aesStream));
        }
        Console.WriteLine($"[sim] discovered '{computer.Name}' ({computer.OS}, streamer {computer.StreamerVersion}) at {streamerIp}: {computer.Description}");

        // ---- 2. connection request -------------------------------------------------------------
        byte[] connect = new byte[17];
        Array.Copy(computer.ConnectionID, connect, 16);
        connect[16] = (byte)platform;
        await udp.SendAsync(connect, connect.Length, new IPEndPoint(streamerIp, 38850));

        using Aes aes = Aes.Create();
        aes.Key = computer.Key;
        aes.IV = computer.IV;
        aes.Padding = PaddingMode.None;
        bool encrypt = computer.EncryptLocalTraffic;

        var messaging = new NetMessagingClient(new IPEndPoint(NetClient.AnyAddress, 38810));
        SettingsBase<SharedMobileSettings>.Default.InitializeAsServer(messaging);
        SettingsBase<SharedUserSettings>.Default.InitializeAsServer(messaging);
        SettingsBase<SharedStreamerSettings>.Default.InitializeAsClient(messaging);
        SharedMobileSettings mobile = SettingsBase<SharedMobileSettings>.Default;
        mobile.HmdType = HmdType.MetaQuest3;
        mobile.UseAnnexB = annexB;
        mobile.DesktopFramerate = 30;
        mobile.MeasuredBandwidth = 0;

        IPAddress[] targets = { streamerIp };
        await messaging.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, targets, 38810, true);
        Console.WriteLine($"[sim] messaging channel: {messaging.Status}");
        var video = new NetVideoClient(new IPEndPoint(NetClient.AnyAddress, 38830));
        var audio = new NetAudioClient(new IPEndPoint(NetClient.AnyAddress, 38840));
        var data = new NetDataClient(new IPEndPoint(NetClient.AnyAddress, 38820));
        await Task.Delay(300);
        await video.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, targets, 38830, encrypt);
        await Task.Delay(300);
        await audio.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, targets, 38840, encrypt);
        await Task.Delay(300);
        await data.ConnectToLocalPeerAsync(aes, NetClient.AnyAddress, targets, 38820, encrypt);
        Console.WriteLine($"[sim] channels: video={video.Status} audio={audio.Status} data={data.Status}");

        // The streamer sends a bandwidth probe (int32 length + random bytes) on the data channel;
        // like the headset app we drain it, measure the throughput and publish MeasuredBandwidth,
        // which completes the streamer's connection handshake.
        Stream dataStream = GetStream(data);
        byte[] lengthBytes = new byte[4];
        await ReadExactAsync(dataStream, lengthBytes, 4, CancellationToken.None);
        int probeLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (probeLength < 0 || probeLength > 64 << 20)
        {
            probeLength = BitConverter.ToInt32(lengthBytes, 0);
        }
        Stopwatch probeTime = Stopwatch.StartNew();
        byte[] probe = new byte[Math.Max(1, probeLength)];
        if (probeLength > 0)
        {
            await ReadExactAsync(dataStream, probe, probeLength, CancellationToken.None);
        }
        double seconds_ = Math.Max(0.001, probeTime.Elapsed.TotalSeconds);
        int measured = (int)Math.Clamp(probeLength * 8 / seconds_, 10_000_000, 150_000_000);
        Console.WriteLine($"[sim] bandwidth probe: {probeLength} bytes in {seconds_ * 1000:F0} ms -> reporting {measured / 1_000_000} Mbps");
        mobile.MeasuredBandwidth = measured;

        // ---- 3. optional: headset microphone + input --------------------------------------------
        bool sendMic = args.Contains("--mic");
        bool sendInput = args.Contains("--input");
        bool sendScroll = args.Contains("--scroll");
        int inputDelayIndex = Array.IndexOf(args, "--input-delay");
        int inputDelayMs = inputDelayIndex >= 0 ? (int)(double.Parse(args[inputDelayIndex + 1], System.Globalization.CultureInfo.InvariantCulture) * 1000) : 0;
        if (sendMic)
        {
            SettingsBase<SharedUserSettings>.Default.MicPassthrough = true;
            _ = Task.Run(async () =>
            {
                // 440 Hz mono tone, 20 ms packets.
                byte[] pcm = GC.AllocateArray<byte>(1920, pinned: true);
                long n = 0;
                Stopwatch clock = Stopwatch.StartNew();
                while (audio.Connected)
                {
                    for (int i = 0; i < 960; i++, n++)
                    {
                        short v = (short)(Math.Sin(2 * Math.PI * 440 * n / 48000.0) * 12000);
                        pcm[2 * i] = (byte)v;
                        pcm[2 * i + 1] = (byte)(v >> 8);
                    }
                    unsafe
                    {
                        fixed (byte* p = pcm)
                        {
                            audio.WriteSample((nint)p, pcm.Length);
                        }
                    }
                    long due = (n / 48) - (long)clock.Elapsed.TotalMilliseconds;
                    if (due > 0)
                    {
                        await Task.Delay((int)due);
                    }
                }
            });
        }
        if (sendScroll)
        {
            // Thumbstick scrolling while a laser pointer moves (mouse inactive), 1 step per frame:
            // 60 frames up, 60 frames down, then 20 frames of a fast flick (4 steps per frame).
            Stream dataOut = GetStream(data);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(inputDelayMs);
                    for (int frame = 1; frame <= 140 && data.Connected; frame++)
                    {
                        var fd = new FrameData();
                        fd.FrameIndex = frame;
                        fd.InputState.MouseState.Active = false;
                        fd.InputState.MouseState.ScrollY = (sbyte)(frame <= 60 ? 1 : frame <= 120 ? -1 : 4);
                        fd.InputState.LeftPointerState.Active = true;
                        fd.InputState.LeftPointerState.X = (short)(400 + frame * 3);
                        fd.InputState.LeftPointerState.Y = (short)(300 + frame % 7);
                        WriteFrameData(dataOut, fd);
                        await Task.Delay(14);
                    }
                    Console.WriteLine("[sim] scroll input sent");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[sim] scroll sender stopped: " + ex.Message);
                }
            });
        }
        if (sendInput)
        {
            Stream dataOut = GetStream(data);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(inputDelayMs);
                    for (int frame = 1; frame < 400 && data.Connected; frame++)
                    {
                        var fd = new FrameData();
                        fd.FrameIndex = frame;
                        unsafe
                        {
                            RawMouseState* mouse = &fd.InputState.MouseState;
                            mouse->Active = true;
                            mouse->X = 5;                 // relative motion
                            mouse->Y = 3;
                            ((byte*)mouse)[1] = (byte)(frame % 20 == 10 ? 1 : 0); // left button
                            byte* kb = (byte*)&fd.InputState.KeyboardState;
                            if (frame % 30 == 0)
                            {
                                *(ushort*)kb = 30;        // ScanCodes.A
                                kb[2] = 3;                // Down | Pressed
                            }
                        }
                        WriteFrameData(dataOut, fd);
                        await Task.Delay(16);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[sim] input sender stopped: " + ex.Message);
                }
            });
        }

        // ---- 4. receive video ------------------------------------------------------------------
        Stream videoStream = GetStream(video);
        string readMbps = Arg(args, "--read-mbps");
        if (readMbps != null)
        {
            // Simulate a slow Wi-Fi link: consume the video channel at a capped rate.
            videoStream = new ThrottledStream(videoStream, double.Parse(readMbps, System.Globalization.CultureInfo.InvariantCulture) * 1_000_000);
            Console.WriteLine($"[sim] reading video at most {readMbps} Mbps");
            // Keep the kernel receive buffer small too, like a headset that reads promptly from a
            // slow link: the backlog then builds up on the PC side, where the streamer can see it.
            var tcp = typeof(NetClient).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(video) as TcpClient;
            if (tcp != null)
            {
                tcp.Client.ReceiveBufferSize = 64 * 1024;
            }
        }
        Stream audioStream = GetStream(audio);
        long audioBytes = 0;
        _ = Task.Run(() =>
        {
            byte[] buf = new byte[8192];
            try
            {
                int n;
                while ((n = audioStream.Read(buf, 0, buf.Length)) > 0)
                {
                    Interlocked.Add(ref audioBytes, n);
                }
            }
            catch
            {
            }
        });

        string playVideo = Arg(args, "--play-video");
        int videoFrames = 0, subtitles = 0;
        var seenTypes = new System.Collections.Concurrent.ConcurrentDictionary<MessageType, int>();
        messaging.Message += (_, m) => seenTypes.AddOrUpdate(m.MessageType, 1, (_, c) => c + 1);
        messaging.Message += (_, m) =>
        {
            if (m.MessageType == MessageType.Videos)
            {
                short count = m.Reader.ReadInt16();
                var names = new System.Collections.Generic.List<string>();
                for (int i = 0; i < count; i++)
                {
                    names.Add(m.Reader.ReadString());
                    m.Reader.ReadInt64();
                    m.Reader.ReadInt64();
                    m.Reader.ReadInt32();
                }
                Console.WriteLine($"[sim] PC video library: {string.Join(", ", names)}");
            }
            else if (m.MessageType == MessageType.AudioTracks)
            {
                Console.WriteLine($"[sim] video audio tracks: {m.Reader.ReadByte()}");
            }
        };
        if (playVideo != null)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                NetMessage list = NetMessage.CreateOutgoing(MessageType.RequestVideos);
                list.Writer.Write(string.Empty);
                list.SendTo(messaging);
                await Task.Delay(1000);
                Console.WriteLine($"[sim] PlayVideo {playVideo}");
                NetMessage play = NetMessage.CreateOutgoing(MessageType.PlayVideo);
                play.Writer.Write(playVideo);
                play.Writer.Write(0L);
                play.Writer.Write((sbyte)-1);
                play.SendTo(messaging);
            });
        }

        int metadataSize = Unsafe.SizeOf<FrameMetadata>();
        int sliceHeaderSize = Unsafe.SizeOf<SliceHeader>();
        int formatSize = Unsafe.SizeOf<VideoFormat>();
        int formatOldSize = Unsafe.SizeOf<VideoFormatOld>();
        using FileStream file = File.Create(output);
        int frames = 0, slices = 0, keyFrames = 0;
        long videoBytes = 0;
        VideoFormat? format = null;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        Stopwatch firstFrame = Stopwatch.StartNew();
        double firstFrameMs = -1;
        // Queueing delay: arrival time minus the PC's send timestamp, relative to the smallest value seen.
        double minOffsetMs = double.MaxValue, maxDelayMs = 0, lastDelayMs = 0;
        var recentDelays = new System.Collections.Generic.Queue<double>();
        try
        {
            byte[] header = new byte[Math.Max(Math.Max(metadataSize, formatSize), Math.Max(formatOldSize, sliceHeaderSize))];
            while (!cts.IsCancellationRequested)
            {
                int type = await ReadByteAsync(videoStream, cts.Token);
                switch ((PacketType)type)
                {
                    case PacketType.Frame:
                    {
                        await ReadExactAsync(videoStream, header, metadataSize, cts.Token);
                        FrameMetadata md = MemoryMarshalRead<FrameMetadata>(header);
                        byte[] payload = new byte[md.DataSize];
                        await ReadExactAsync(videoStream, payload, md.DataSize, cts.Token);
                        if (md.Source == StreamingSource.Video)
                        {
                            videoFrames++;
                            break;
                        }
                        if (frames == 0)
                        {
                            firstFrameMs = firstFrame.Elapsed.TotalMilliseconds;
                        }
                        double offsetMs = firstFrame.Elapsed.TotalMilliseconds - md.TimestampUs / 1000.0;
                        minOffsetMs = Math.Min(minOffsetMs, offsetMs);
                        lastDelayMs = offsetMs - minOffsetMs;
                        if (frames > 30)
                        {
                            maxDelayMs = Math.Max(maxDelayMs, lastDelayMs);
                        }
                        recentDelays.Enqueue(lastDelayMs);
                        if (recentDelays.Count > 30)
                        {
                            recentDelays.Dequeue();
                        }
                        frames++;
                        videoBytes += payload.Length;
                        if (HasIdr(payload, annexB))
                        {
                            keyFrames++;
                        }
                        if (annexB)
                        {
                            file.Write(payload);
                        }
                        else
                        {
                            WriteAvccAsAnnexB(file, payload);
                        }
                        break;
                    }
                    case PacketType.Slice:
                    {
                        await ReadExactAsync(videoStream, header, sliceHeaderSize, cts.Token);
                        SliceHeader sh = MemoryMarshalRead<SliceHeader>(header);
                        byte[] payload = new byte[sh.DataSize];
                        await ReadExactAsync(videoStream, payload, sh.DataSize, cts.Token);
                        slices++;
                        break;
                    }
                    case PacketType.VideoFormat:
                        await ReadExactAsync(videoStream, header, formatSize, cts.Token);
                        format = MemoryMarshalRead<VideoFormat>(header);
                        Console.WriteLine($"[sim] VideoFormat: {format.Value.Codec} {format.Value.FrameSize.Width}x{format.Value.FrameSize.Height}@{format.Value.FrameRate} streams={format.Value.StreamCount} bounds={format.Value.Bounds}");
                        break;
                    case PacketType.VideoFormatOld:
                        await ReadExactAsync(videoStream, header, formatOldSize, cts.Token);
                        VideoFormatOld old = MemoryMarshalRead<VideoFormatOld>(header);
                        format = new VideoFormat { Source = old.Source, Codec = old.Codec, FrameRate = old.FrameRate, FrameSize = old.FrameSize, StreamCount = old.StreamCount, Bounds = old.Bounds };
                        Console.WriteLine($"[sim] VideoFormat(old): {old.Codec} {old.FrameSize.Width}x{old.FrameSize.Height}@{old.FrameRate} streams={old.StreamCount} bounds={old.Bounds}");
                        break;
                    case PacketType.Subtitle:
                    {
                        byte[] len = new byte[4];
                        await ReadExactAsync(videoStream, len, 4, cts.Token);
                        int n = BitConverter.ToInt32(len, 0);
                        await ReadExactAsync(videoStream, new byte[Math.Max(1, n)], n, cts.Token);
                        subtitles++;
                        break;
                    }
                    default:
                        throw new InvalidDataException("unknown video packet type " + type);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine("[sim] video channel ended: " + ex.Message);
        }
        Console.WriteLine($"[sim] streamer settings: ActiveCodec={SettingsBase<SharedStreamerSettings>.Default.ActiveCodec} CanAddMonitor={SettingsBase<SharedStreamerSettings>.Default.CanAddMonitor} Source={SettingsBase<SharedStreamerSettings>.Default.StreamingSource}");
        Console.WriteLine($"[sim] queueing delay: max {maxDelayMs:F0} ms, last 30 frames avg {(recentDelays.Count > 0 ? recentDelays.Average() : 0):F0} ms");
        Console.WriteLine($"[sim] received {frames} frames ({keyFrames} key frames, {slices} slices, {videoBytes / 1024} KiB video), {Interlocked.Read(ref audioBytes) / 1024} KiB audio; first frame after {firstFrameMs:F0} ms");
        NetMessage.CreateOutgoing(MessageType.Disconnect).SendTo(messaging);
        await Task.Delay(300);
        messaging.Dispose();
        video.Dispose();
        audio.Dispose();
        data.Dispose();
        Console.WriteLine("[sim] messages from PC: " + string.Join(", ", seenTypes.Select(kv => $"{kv.Key}x{kv.Value}")));
        if (playVideo != null)
        {
            Console.WriteLine($"[sim] video playback: {videoFrames} video-file frames, {subtitles} subtitle updates");
        }
        bool ok = format.HasValue && frames >= 10 && keyFrames >= 1 && (playVideo == null || videoFrames >= 10);
        Console.WriteLine(ok ? "[sim] PASS" : "[sim] FAIL");
        return ok ? 0 : 1;
    }

    /// <summary>255.255.255.255 plus each interface's directed broadcast (needed without a default route).</summary>
    private static IPAddress[] BroadcastTargets()
    {
        var list = new System.Collections.Generic.List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address) && ua.IPv4Mask != null)
                {
                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    for (int i = 0; i < 4; i++)
                    {
                        ip[i] = (byte)(ip[i] | ~mask[i]);
                    }
                    list.Add(new IPAddress(ip));
                }
            }
        }
        return list.ToArray();
    }

    /// <summary>Data channel framing read by NetDataClient.ReadFrameData: [len][xxhash3][LZ4(FrameData)].</summary>
    private static unsafe void WriteFrameData(Stream stream, FrameData frameData)
    {
        int size = Unsafe.SizeOf<FrameData>();
        byte[] raw = new byte[size];
        fixed (byte* r = raw)
        {
            *(FrameData*)r = frameData;
        }
        byte[] compressed = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(size)];
        int length = K4os.Compression.LZ4.LZ4Codec.Encode(raw, 0, size, compressed, 0, compressed.Length);
        ulong hash = System.IO.Hashing.XxHash3.HashToUInt64(raw);
        byte[] packet = new byte[12 + length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), length);
        BitConverter.TryWriteBytes(packet.AsSpan(4, 8), hash);
        Array.Copy(compressed, 0, packet, 12, length);
        stream.Write(packet, 0, packet.Length);
    }

    private static string Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static Stream GetStream(NetClient client)
    {
        for (Type t = client.GetType(); t != null; t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty("Stream", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null)
            {
                return (Stream)p.GetValue(client);
            }
            FieldInfo f = t.GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null)
            {
                return (Stream)f.GetValue(client);
            }
        }
        throw new MissingMemberException("NetClient stream not found");
    }

    private static unsafe T MemoryMarshalRead<T>(byte[] buffer) where T : unmanaged
    {
        fixed (byte* p = buffer)
        {
            return *(T*)p;
        }
    }

    private static async Task<int> ReadByteAsync(Stream s, CancellationToken ct)
    {
        byte[] b = new byte[1];
        await ReadExactAsync(s, b, 1, ct);
        return b[0];
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }
            read += n;
        }
    }

    private static bool HasIdr(byte[] data, bool annexB)
    {
        if (annexB)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && ((data[i + 3] & 0x1F) == 5 || ((data[i + 3] >> 1) & 0x3F) is >= 16 and <= 21))
                {
                    return true;
                }
            }
            return false;
        }
        int pos = 0;
        while (pos + 4 < data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            byte h = data[pos + 4];
            if ((h & 0x1F) == 5)
            {
                return true;
            }
            pos += 4 + len;
        }
        return false;
    }

    private static void WriteAvccAsAnnexB(Stream file, byte[] data)
    {
        int pos = 0;
        while (pos + 4 <= data.Length)
        {
            int len = (data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3];
            file.Write(new byte[] { 0, 0, 0, 1 });
            file.Write(data, pos + 4, Math.Min(len, data.Length - pos - 4));
            pos += 4 + len;
        }
    }

    /// <summary>DataContractSerializer needs a stream that reads to the end without seeking.</summary>
    private sealed class BufferedReadStream : MemoryStream
    {
        public BufferedReadStream(Stream source)
        {
            byte[] buffer = new byte[4096];
            int n;
            while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                Write(buffer, 0, n);
            }
            // Strip the zero padding added by the AES block framing.
            byte[] all = ToArray();
            int length = all.Length;
            while (length > 0 && all[length - 1] == 0)
            {
                length--;
            }
            SetLength(0);
            Write(all, 0, length);
            Position = 0;
        }
    }
}

/// <summary>Read-only wrapper that limits throughput (token bucket, 50 ms burst).</summary>
internal sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly double _bytesPerSecond;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _allowance;
    private double _last;

    public ThrottledStream(Stream inner, double bitsPerSecond)
    {
        _inner = inner;
        _bytesPerSecond = bitsPerSecond / 8;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _allowance = Math.Min(_bytesPerSecond * 0.05, _allowance + (now - _last) * _bytesPerSecond);
            _last = now;
            if (_allowance >= 1)
            {
                break;
            }
            await Task.Delay(2, cancellationToken);
        }
        int n = await _inner.ReadAsync(buffer.Slice(0, (int)Math.Min(buffer.Length, Math.Max(1, _allowance))), cancellationToken);
        _allowance -= n;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
