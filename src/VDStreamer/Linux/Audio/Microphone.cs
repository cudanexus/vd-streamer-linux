using System;
using System.Threading;
using NLog;
using VDStreamer.Streamer;
using VirtualDesktop.Core;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.Core.Mathematics;

namespace VDStreamer.Linux.Audio;

/// <summary>
/// Headset microphone passthrough: 48 kHz 16-bit mono PCM received on the audio channel is played
/// into the "VirtualDesktopMic" virtual source (macOS: VirtualDesktopMic CoreAudio driver).
/// </summary>
internal sealed class Microphone : IDisposable
{
    private const int ChunkBytes = 1920; // 20 ms
    private static readonly Logger Logger = LogManager.GetLogger("Microphone");

    private readonly NetAudioClient _client;
    private Thread _thread;
    private volatile bool _enabled;
    private volatile bool _disposed;
    private float _volume = 1f;

    public Microphone(NetAudioClient client)
    {
        _client = client;
        SettingsBase<SharedUserSettings>.Default.MicPassthroughChanged += OnMicPassthroughChanged;
        SettingsBase<SharedUserSettings>.Default.MicVolumeChanged += OnMicVolumeChanged;
        SettingsBase<StreamerSettings>.Default.AutoSelectMicrophoneChanged += OnAutoSelectChanged;
        OnMicVolumeChanged(null, EventArgs.Empty);
        _thread = new Thread(PlaybackLoop) { Name = "Mic Passthrough", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        Enabled = SettingsBase<SharedUserSettings>.Default.MicPassthrough;
    }

    public bool Enabled
    {
        get => _enabled;
        private set
        {
            if (value == _enabled)
            {
                return;
            }
            _enabled = value;
            if (value)
            {
                try
                {
                    Pulse.EnableMicrophone(SettingsBase<StreamerSettings>.Default.AutoSelectMicrophone);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Could not create the virtual microphone");
                }
            }
            else
            {
                Pulse.RestoreMicrophone();
            }
        }
    }

    private void PlaybackLoop()
    {
        PulseStream output = null;
        try
        {
            // Drain anything queued before we started.
            while (!_disposed && _client.DataAvailable > 0)
            {
                if (_client.ReadSample(out _) == 0)
                {
                    return;
                }
            }
            while (!_disposed)
            {
                if (_client.DataAvailable > ChunkBytes * 4)
                {
                    // Too much latency accumulated: skip ahead like the macOS implementation.
                    do
                    {
                        if (_client.ReadSample(out _) == 0)
                        {
                            return;
                        }
                    }
                    while (_client.DataAvailable > ChunkBytes);
                }
                int length = _client.ReadSample(out byte[] data);
                if (length == 0)
                {
                    break;
                }
                if (!_enabled)
                {
                    output?.Dispose();
                    output = null;
                    continue;
                }
                if (output == null)
                {
                    output = PulseStream.OpenPlayback(Pulse.MicSink, 1, "Headset microphone", ChunkBytes * 3);
                }
                ApplyVolume(data, length, _volume);
                output.Write(new ReadOnlySpan<byte>(data, 0, length));
            }
        }
        catch (Exception ex)
        {
            // The vendor client throws (NullReference/IO) once the audio socket is closed.
            if (!_disposed && _client.Connected && ex is not System.IO.EndOfStreamException)
            {
                Logger.Error(ex, "Microphone passthrough stopped");
            }
            else
            {
                Logger.Debug("Microphone passthrough ended: " + ex.Message);
            }
        }
        finally
        {
            output?.Dispose();
        }
    }

    private static void ApplyVolume(byte[] data, int length, float volume)
    {
        if (Math.Abs(volume - 1f) < 0.01f)
        {
            return;
        }
        for (int i = 0; i + 1 < length; i += 2)
        {
            int sample = (short)(data[i] | (data[i + 1] << 8));
            sample = (int)(sample * volume);
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            data[i] = (byte)sample;
            data[i + 1] = (byte)(sample >> 8);
        }
    }

    private void OnMicPassthroughChanged(object sender, EventArgs e)
    {
        if (SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            Enabled = SettingsBase<SharedUserSettings>.Default.MicPassthrough;
        }
    }

    private void OnMicVolumeChanged(object sender, EventArgs e) =>
        _volume = MathUtil.Lerp(0.1f, 2f, SettingsBase<SharedUserSettings>.Default.MicVolume);

    private void OnAutoSelectChanged(object sender, EventArgs e)
    {
        if (!_enabled)
        {
            return;
        }
        if (SettingsBase<StreamerSettings>.Default.AutoSelectMicrophone)
        {
            Pulse.EnableMicrophone(true);
        }
        else
        {
            Pulse.RestoreMicrophone();
            Pulse.EnableMicrophone(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SettingsBase<SharedUserSettings>.Default.MicPassthroughChanged -= OnMicPassthroughChanged;
        SettingsBase<SharedUserSettings>.Default.MicVolumeChanged -= OnMicVolumeChanged;
        SettingsBase<StreamerSettings>.Default.AutoSelectMicrophoneChanged -= OnAutoSelectChanged;
        Enabled = false;
    }
}
