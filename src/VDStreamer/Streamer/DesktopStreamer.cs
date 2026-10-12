using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Linux.Capture;
using VDStreamer.Linux.Input;
using VDStreamer.Linux.Video;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.Core.Mathematics;
using Xenko.VR;

namespace VDStreamer.Streamer;

/// <summary>
/// Desktop streaming: enumerates monitors, creates one capture+encode stream per streamed monitor,
/// announces the VideoFormat to the headset and forwards encoded frames. Port of the macOS
/// DesktopStreamer (without display-mode switching / virtual displays, which are macOS specific).
/// </summary>
internal sealed class DesktopStreamer : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("DesktopStreamer");

    private readonly CaptureManager _capture = new();
    private readonly VideoPacer _pacer = new();

    // Troubleshooting switch: VDSTREAMER_NO_PACING=1 writes frames without waiting for the network.
    private static readonly bool NoPacing = Environment.GetEnvironmentVariable("VDSTREAMER_NO_PACING") == "1";
    private GstVideoStream[] _streams;
    private bool _shouldRefreshMonitors = true;
    private int _exceptionCount;
    private DateTime _nextRetry = DateTime.MinValue;

    public DesktopStreamer()
    {
        SettingsBase<DynamicSettings>.Default.CursorVisibleChanged += OnCursorVisibleChanged;
        SettingsBase<DynamicSettings>.Default.MonitorStartIndexChanged += OnMonitorStartIndexChanged;
        SettingsBase<SharedMobileSettings>.Default.DesktopFramerateChanged += OnDesktopFrameRateChanged;
        SettingsBase<SharedUserSettings>.Default.DesktopBitrateLimitChanged += OnDesktopBitrateLimitChanged;
        SettingsBase<StreamerSettings>.Default.MonitorCountChanged += OnMonitorCountChanged;
        _pacer.BitrateChanged += OnPacerBitrateChanged;
        UpdateCanAddRemoveMonitors();
    }

    /// <summary>One-line performance summary for the status page / CLI.</summary>
    public string Performance
    {
        get
        {
            GstVideoStream[] streams = _streams;
            if (streams == null)
            {
                return null;
            }
            long encoded = streams.Sum(s => s.FramesEncoded);
            long skipped = streams.Sum(s => s.FramesSkipped);
            return $"{_pacer.Bitrate / 1_000_000.0:F1}/{_pacer.TargetBitrate / 1_000_000.0:F1} Mbps, network queue {_pacer.QueuedBytes / 1024} KiB, " +
                   $"{encoded} frames sent, {skipped} unchanged frames skipped, {_pacer.DelayedFrames} frames held back by the network";
        }
    }

    public void Dispose()
    {
        SettingsBase<DynamicSettings>.Default.CursorVisibleChanged -= OnCursorVisibleChanged;
        SettingsBase<DynamicSettings>.Default.MonitorStartIndexChanged -= OnMonitorStartIndexChanged;
        SettingsBase<SharedMobileSettings>.Default.DesktopFramerateChanged -= OnDesktopFrameRateChanged;
        SettingsBase<SharedUserSettings>.Default.DesktopBitrateLimitChanged -= OnDesktopBitrateLimitChanged;
        SettingsBase<StreamerSettings>.Default.MonitorCountChanged -= OnMonitorCountChanged;
        _pacer.BitrateChanged -= OnPacerBitrateChanged;
        ReleaseStreams();
        _capture.Dispose();
    }

    public void Update()
    {
        try
        {
            if (_shouldRefreshMonitors)
            {
                if (DateTime.UtcNow < _nextRetry)
                {
                    return;
                }
                ReleaseStreams();
                _shouldRefreshMonitors = false;
                MonitorInfo[] monitors = _capture.GetMonitors(embedCursor: true);
                SettingsBase<DynamicSettings>.Default.Monitors = monitors;
                if (SettingsBase<DynamicSettings>.Default.MonitorStartIndex >= monitors.Length)
                {
                    SettingsBase<DynamicSettings>.Default.MonitorStartIndex = 0;
                }
                InputInjector.DesktopBounds = Union(_capture.Kind == CaptureKind.X11 ? monitors : monitors);
                UpdateCanAddRemoveMonitors();
            }

            if (_streams != null && _streams.Any(s => s.HasFailed))
            {
                HandleFailedStreams();
                return;
            }

            if (_streams != null || RecreateStreams())
            {
                int lastFrameIndex = StreamerManager.LastFrameIndex;
                if (lastFrameIndex < 0)
                {
                    ConnectionManager.VideoClient.IsConnected(true);
                    SettingsBase<DynamicSettings>.Default.ForceKeyFrame = true;
                }
                else if (SettingsBase<DynamicSettings>.Default.ForceKeyFrame)
                {
                    SettingsBase<DynamicSettings>.Default.ForceKeyFrame = false;
                    ForceKeyFrame();
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            Logger.Warn(ex.Message);
            SettingsBase<DynamicSettings>.Default.StatusMessage = ex.Message;
            _shouldRefreshMonitors = true;
            _nextRetry = DateTime.UtcNow.AddSeconds(5);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Exception during Update()");
            SettingsBase<DynamicSettings>.Default.StatusMessage = "Capture error: " + exception.Message;
            if (++_exceptionCount > 5)
            {
                throw;
            }
            _shouldRefreshMonitors = true;
            _nextRetry = DateTime.UtcNow.AddSeconds(2);
        }
        finally
        {
            Thread.Sleep(1);
        }
    }

    private void HandleFailedStreams()
    {
        foreach (GstVideoStream stream in _streams.Where(s => s.HasFailed))
        {
            if (stream.FramesEncoded == 0)
            {
                // The encoder never produced a frame (e.g. NVENC element without an NVIDIA GPU): try the next one.
                EncoderCatalog.MarkBroken(stream.Encoder, stream.FailureReason);
                Logger.Warn($"Encoder {stream.Encoder} unusable: {stream.FailureReason}");
            }
            else if (stream.Monitor.Kind == CaptureKind.PipeWire)
            {
                // The screencast was most likely stopped from the desktop: renegotiate with the portal.
                _capture.ClosePortal();
            }
        }
        ReleaseStreams();
        _shouldRefreshMonitors = true;
        _nextRetry = DateTime.UtcNow.AddMilliseconds(500);
    }

    public void Restart()
    {
        GstVideoStream[] streams = _streams;
        if (streams != null)
        {
            foreach (GstVideoStream s in streams)
            {
                s.ForceKeyFrame();
            }
        }
    }

    public void ReleaseStreams()
    {
        string performance = Performance;
        GstVideoStream[] streams = Interlocked.Exchange(ref _streams, null);
        if (streams == null)
        {
            return;
        }
        Logger.Info("Stream statistics: " + performance);
        foreach (GstVideoStream s in streams)
        {
            s.FrameEncoded -= OnFrameEncoded0;
            s.Dispose();
        }
    }

    public void AddMonitor()
    {
        if (SettingsBase<SharedStreamerSettings>.Default.CanAddMonitor)
        {
            SettingsBase<StreamerSettings>.Default.MonitorCount++;
        }
    }

    public void RemoveMonitor()
    {
        if (SettingsBase<SharedStreamerSettings>.Default.CanRemoveMonitor)
        {
            SettingsBase<StreamerSettings>.Default.MonitorCount--;
        }
    }

    private void ForceKeyFrame()
    {
        GstVideoStream[] streams = _streams;
        if (streams != null)
        {
            foreach (GstVideoStream s in streams)
            {
                s.ForceKeyFrame();
            }
        }
    }

    private void UpdateCanAddRemoveMonitors()
    {
        int monitorCount = SettingsBase<StreamerSettings>.Default.MonitorCount;
        int available = Math.Max(1, SettingsBase<DynamicSettings>.Default.Monitors.Length);
        int max = Math.Min(available, HmdResolutionTypeExtensions.GetMaxMonitorCount(SettingsBase<SharedMobileSettings>.Default.HmdType));
        SettingsBase<SharedStreamerSettings>.Default.CanAddMonitor = monitorCount < max;
        SettingsBase<SharedStreamerSettings>.Default.CanRemoveMonitor = monitorCount > 1;
    }

    private bool RecreateStreams()
    {
        ReleaseStreams();
        if (!SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            return false;
        }
        MonitorInfo[] monitors = SettingsBase<DynamicSettings>.Default.Monitors;
        if (monitors == null || monitors.Length == 0)
        {
            _shouldRefreshMonitors = true;
            _nextRetry = DateTime.UtcNow.AddSeconds(2);
            SettingsBase<DynamicSettings>.Default.StatusMessage = "No monitor available to capture";
            return false;
        }

        SharedMobileSettings mobile = SettingsBase<SharedMobileSettings>.Default;
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        HmdType hmdType = mobile.HmdType;
        bool isMaui = HmdResolutionTypeExtensions.IsMauiDevice(hmdType);
        int startIndex = SettingsBase<DynamicSettings>.Default.MonitorStartIndex % monitors.Length;

        int streamCount = 1;
        if (!isMaui)
        {
            streamCount = Math.Min(Math.Min(monitors.Length, HmdResolutionTypeExtensions.GetMaxMonitorCount(hmdType)), settings.MonitorCount);
            streamCount = Math.Max(1, streamCount);
            if (settings.MonitorCount != streamCount)
            {
                settings.MonitorCount = streamCount;
            }
        }
        mobile.GetMaxDesktopResolution(streamCount, out int maxWidth, out int maxHeight);
        int totalBitrate = mobile.DesktopBitrate > 0 ? mobile.DesktopBitrate : 30_000_000;
        int bitrate = totalBitrate / Math.Max(1, streamCount);
        _pacer.Adaptive = settings.AdaptiveBitrate;
        _pacer.Reset(SocketTuning.GetSocket(ConnectionManager.VideoClient), totalBitrate);

        bool headsetSupportsHevc = hmdType >= HmdType.OculusQuest || (mobile.Features & Features.Hevc) > Features.None;
        bool wantHevc = headsetSupportsHevc && settings.PreferredCodec == PreferredCodec.HEVC;
        EncoderChoice encoder = EncoderCatalog.Select(settings.PreferredEncoder, wantHevc);
        if (encoder == null)
        {
            throw new InvalidOperationException("No usable video encoder found. Install gstreamer1.0-plugins-ugly (x264enc) or a VA-API/NVENC GStreamer plugin.");
        }

        var videoFormat = default(VideoFormat);
        videoFormat.Source = StreamingSource.Desktop;
        videoFormat.StreamCount = streamCount;
        videoFormat.Codec = encoder.Hevc ? VideoCodec.HEVC : VideoCodec.H264;
        bool useAnnexB = mobile.UseAnnexB;
        bool useFullRange = HmdResolutionTypeExtensions.SupportsFullRange(hmdType);

        var streams = new GstVideoStream[streamCount];
        int index = startIndex;
        try
        {
            for (int i = 0; i < streamCount; i++)
            {
                MonitorInfo monitor = monitors[index];
                index = (index + 1) % monitors.Length;
                Size2 frameSize = Fit(new Size2(monitor.Bounds.Width, monitor.Bounds.Height), maxWidth * maxHeight);
                int framerate = mobile.DesktopFramerate > 0 ? mobile.DesktopFramerate : 60;
                if (monitor.DisplayFrequency > 0)
                {
                    framerate = Math.Min(framerate, monitor.DisplayFrequency);
                }
                if (settings.MaxFramerate > 0)
                {
                    framerate = Math.Min(framerate, settings.MaxFramerate);
                }
                switch (i)
                {
                    case 0:
                        videoFormat.FrameSize = frameSize;
                        videoFormat.Bounds = monitor.Bounds;
                        videoFormat.FrameRate = framerate;
                        break;
                    case 1:
                        videoFormat.SecondaryFrameSize = frameSize;
                        videoFormat.SecondaryBounds = monitor.Bounds;
                        break;
                    case 2:
                        videoFormat.TertiaryFrameSize = frameSize;
                        videoFormat.TertiaryBounds = monitor.Bounds;
                        break;
                    case 3:
                        videoFormat.QuaternaryFrameSize = frameSize;
                        videoFormat.QuaternaryBounds = monitor.Bounds;
                        break;
                }
                bool showCursor = SettingsBase<DynamicSettings>.Default.CursorVisible;
                var stream = new GstVideoStream(_capture, monitor, i, frameSize, framerate, encoder, bitrate, useAnnexB, useFullRange, showCursor,
                    () => StreamerManager.LastFrameIndex >= 0 && SettingsBase<SharedStreamerSettings>.Default.StreamingSource == StreamingSource.Desktop,
                    NoPacing ? null : _pacer.WaitForRoom, settings.SkipUnchangedFrames);
                stream.FrameEncoded += i == 0 ? (FrameEncodedHandler)OnFrameEncoded0 : MakeSliceHandler(i);
                streams[i] = stream;
            }

            _streams = streams;
            ConnectionManager.VideoClient.WriteVideoFormat(ref videoFormat, hmdType < HmdType.PicoSwan);
            foreach (GstVideoStream s in streams)
            {
                s.Start();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, $"Error creating streams with {encoder}");
            _streams = null;
            foreach (GstVideoStream s in streams.Where(s => s != null))
            {
                s.Dispose();
            }
            EncoderCatalog.MarkBroken(encoder, ex.Message);
            if (++_exceptionCount > 8)
            {
                throw;
            }
            _shouldRefreshMonitors = true;
            _nextRetry = DateTime.UtcNow.AddMilliseconds(500);
            return false;
        }

        SettingsBase<SharedStreamerSettings>.Default.ActiveCodec = videoFormat.Codec;
        SettingsBase<SharedStreamerSettings>.Default.AdapterIsAMD = encoder.Hardware && encoder.Id.StartsWith("va", StringComparison.Ordinal) && IsAmdGpu();
        settings.CodecName = encoder.DisplayName;
        SettingsBase<DynamicSettings>.Default.ActiveEncoder = encoder.DisplayName;
        SettingsBase<DynamicSettings>.Default.StatusMessage = $"Streaming {streamCount} monitor(s) to {ConnectionManager.DescribeHeadset()} - {encoder.DisplayName}";
        return true;
    }

    private static void OnFrameEncoded0(long timestampUs, nint data, int dataSize)
    {
        var metadata = new FrameMetadata(timestampUs, StreamerManager.LastFrameIndex, StreamingSource.Desktop)
        {
            DataSize = dataSize,
        };
        ConnectionManager.VideoClient.WriteFrame(ref metadata, data, false);
    }

    private static FrameEncodedHandler MakeSliceHandler(int streamIndex) => (timestampUs, data, dataSize) =>
    {
        var header = new SliceHeader(timestampUs, streamIndex, dataSize);
        ConnectionManager.VideoClient.WriteSlice(ref header, data);
    };

    private static Size2 Fit(Size2 source, int maxPixelCount)
    {
        if (maxPixelCount > 0 && source.Width * source.Height > maxPixelCount)
        {
            double aspect = (double)source.Width / source.Height;
            return new Size2(RoundEven((int)Math.Sqrt(maxPixelCount * aspect)), RoundEven((int)Math.Sqrt(maxPixelCount / aspect)));
        }
        return new Size2(RoundEven(source.Width), RoundEven(source.Height));
    }

    private static int RoundEven(int v) => Math.Max(2, (v + 1) & ~1);

    private static Rectangle Union(MonitorInfo[] monitors)
    {
        if (monitors.Length == 0)
        {
            return new Rectangle(0, 0, 1920, 1080);
        }
        int left = monitors.Min(m => m.Bounds.X);
        int top = monitors.Min(m => m.Bounds.Y);
        int right = monitors.Max(m => m.Bounds.X + m.Bounds.Width);
        int bottom = monitors.Max(m => m.Bounds.Y + m.Bounds.Height);
        return new Rectangle(left, top, right - left, bottom - top);
    }

    private static bool IsAmdGpu()
    {
        try
        {
            return Directory.GetDirectories("/sys/class/drm", "card*")
                .Select(d => Path.Combine(d, "device", "vendor"))
                .Where(File.Exists)
                .Any(f => File.ReadAllText(f).Trim() == "0x1002");
        }
        catch
        {
            return false;
        }
    }

    private void OnMonitorStartIndexChanged(object sender, EventArgs e) => StreamerManager.InvokeAsync(ReleaseStreams);

    private void OnCursorVisibleChanged(object sender, EventArgs e)
    {
        GstVideoStream[] streams = _streams;
        if (streams != null)
        {
            bool visible = SettingsBase<DynamicSettings>.Default.CursorVisible;
            foreach (GstVideoStream s in streams)
            {
                s.SetCursorVisible(visible);
            }
        }
    }

    private void OnDesktopFrameRateChanged(object sender, EventArgs e)
    {
        StreamerManager.InvokeAsync(() =>
        {
            GstVideoStream[] streams = _streams;
            if (streams == null)
            {
                return;
            }
            int fps = SettingsBase<SharedMobileSettings>.Default.DesktopFramerate;
            int maxFps = SettingsBase<StreamerSettings>.Default.MaxFramerate;
            if (maxFps > 0)
            {
                fps = Math.Min(fps, maxFps);
            }
            if (streams.Any(s => s.Monitor.Kind == CaptureKind.X11))
            {
                ReleaseStreams(); // capture caps carry the frame rate: rebuild
            }
            else
            {
                foreach (GstVideoStream s in streams)
                {
                    s.SetFramerate(fps);
                }
            }
        });
    }

    private void OnDesktopBitrateLimitChanged(object sender, EventArgs e)
    {
        GstVideoStream[] streams = _streams;
        if (streams == null || !SettingsBase<DynamicSettings>.Default.IsConnected || SettingsBase<SharedStreamerSettings>.Default.StreamingSource != StreamingSource.Desktop)
        {
            return;
        }
        int total = SettingsBase<SharedMobileSettings>.Default.DesktopBitrate;
        _pacer.SetTarget(total);
        foreach (GstVideoStream s in streams)
        {
            s.Bitrate = total / streams.Length;
        }
    }

    private void OnPacerBitrateChanged(int total)
    {
        GstVideoStream[] streams = _streams;
        if (streams == null || streams.Length == 0)
        {
            return;
        }
        foreach (GstVideoStream s in streams)
        {
            s.Bitrate = total / streams.Length;
        }
    }

    private void OnMonitorCountChanged(object sender, EventArgs e)
    {
        UpdateCanAddRemoveMonitors();
        StreamerManager.InvokeAsync(ReleaseStreams);
    }
}
