using System;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using VDStreamer.Streamer;
using VirtualDesktop.Core;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.VR;

namespace VDStreamer.Linux.Audio;

/// <summary>
/// Streams PC audio to the headset: 48 kHz 16-bit stereo PCM in 1152-byte samples, exactly like
/// the macOS AudioCapture (which records the "VirtualDesktopSpeakers" CoreAudio device).
/// </summary>
internal sealed unsafe class AudioCapture : IDisposable
{
    private const int SampleBytes = 1152;
    private static readonly Logger Logger = LogManager.GetLogger("AudioCapture");

    private readonly bool _isMauiDevice;
    private Thread _thread;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private long _bytesSent;

    public long BytesSent => Interlocked.Read(ref _bytesSent);
    // Own pinned buffer: the vendor BeginWriteSample() pointer aliases the client's write buffer,
    // which video-file playback can reallocate.
    private readonly byte[] _sample = GC.AllocateArray<byte>(SampleBytes, pinned: true);

    public AudioCapture()
    {
        _isMauiDevice = HmdResolutionTypeExtensions.IsMauiDevice(SettingsBase<SharedMobileSettings>.Default.HmdType);
        SettingsBase<StreamerSettings>.Default.StreamAudioChanged += OnStreamAudioChanged;
        SetEnabled(SettingsBase<StreamerSettings>.Default.StreamAudio);
    }

    private void SetEnabled(bool value)
    {
        if (value == _enabled)
        {
            return;
        }
        _enabled = value;
        if (value)
        {
            _thread = new Thread(CaptureLoop) { Name = "Audio Capture", IsBackground = true, Priority = ThreadPriority.Highest };
            _thread.Start();
        }
        else
        {
            // The capture thread owns the pa_simple stream (freeing it while pa_simple_read is
            // blocked aborts libpulse); it exits within one 6 ms audio period.
            _thread?.Join(2000);
            _thread = null;
            Pulse.RestoreSpeakers();
        }
    }

    private void CaptureLoop()
    {
        int silenceCount = 0;
        byte[] buffer = new byte[SampleBytes];
        PulseStream stream = null;
        try
        {
            string device = Pulse.EnableSpeakers(SettingsBase<StreamerSettings>.Default.HeadsetOnlyAudio);
            stream = PulseStream.OpenRecord(device, 2, "Headset audio", SampleBytes);
            Logger.Info("Capturing PC audio from " + device);
            NetAudioClient client = ConnectionManager.AudioClient;
            long chunks = 0, skippedNotReady = 0, skippedSilent = 0, droppedBytes = 0;
            var report = System.Diagnostics.Stopwatch.StartNew();
            while (_enabled && !_disposed)
            {
                // Never fall more than ~50 ms behind (resume at ~12 ms) after a stall.
                droppedBytes += stream.DropBacklog(SampleBytes * 8, SampleBytes * 2);
                stream.Read(buffer);
                chunks++;
                if (report.Elapsed.TotalSeconds >= 5)
                {
                    Logger.Debug($"audio: {chunks} chunks, {skippedNotReady} not ready, {skippedSilent} silent, {droppedBytes / 192} ms of backlog dropped, {Interlocked.Read(ref _bytesSent) / 1024} KiB sent");
                    report.Restart();
                }
                if (SettingsBase<VirtualDesktop.Mobile.SharedStreamerSettings>.Default.StreamingSource != StreamingSource.Desktop ||
                    (!_isMauiDevice && StreamerManager.LastFrameIndex < 0))
                {
                    skippedNotReady++;
                    continue;
                }
                if (IsSilence(buffer))
                {
                    if (++silenceCount > 4)
                    {
                        skippedSilent++;
                        continue;
                    }
                }
                else
                {
                    if (silenceCount > 4)
                    {
                        client.WriteSilenceSample(SampleBytes);
                    }
                    silenceCount = 0;
                }
                buffer.CopyTo(_sample, 0);
                fixed (byte* p = _sample)
                {
                    client.WriteSample((nint)p, SampleBytes);
                }
                Interlocked.Add(ref _bytesSent, SampleBytes);
            }
        }
        catch (Exception ex)
        {
            if (_enabled && !_disposed)
            {
                Logger.Error(ex, "Audio capture stopped");
            }
        }
        finally
        {
            stream?.Dispose();
            Logger.Info($"Audio capture finished ({Interlocked.Read(ref _bytesSent) / 1024} KiB sent)");
        }
    }

    private static bool IsSilence(byte[] data)
    {
        fixed (byte* p = data)
        {
            short* s = (short*)p;
            int n = data.Length / 2;
            for (int i = 0; i < n; i++)
            {
                if (s[i] > 1 || s[i] < -1)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private void OnStreamAudioChanged(object sender, EventArgs e) => SetEnabled(SettingsBase<StreamerSettings>.Default.StreamAudio);

    public void Dispose()
    {
        _disposed = true;
        SettingsBase<StreamerSettings>.Default.StreamAudioChanged -= OnStreamAudioChanged;
        SetEnabled(false);
    }
}
