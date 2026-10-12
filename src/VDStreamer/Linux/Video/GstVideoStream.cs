using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using VDStreamer.Linux.Capture;
using Xenko.Core.Mathematics;

namespace VDStreamer.Linux.Video;

internal delegate void FrameEncodedHandler(long timestampUs, nint data, int dataSize);

/// <summary>
/// Captures one monitor and encodes it with GStreamer (capture → convert/scale → encoder →
/// parser → appsink). Encoded access units are re-framed for the headset and handed to the
/// network layer from a dedicated thread. Linux counterpart of the macOS MonitorStream + encoders.
/// </summary>
internal sealed unsafe class GstVideoStream : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("VideoStream");

    private readonly string _description;
    private readonly bool _annexB;
    private readonly bool _hevc;
    private readonly EncoderChoice _encoder;
    private readonly Stopwatch _stopwatch = new();
    private readonly Func<bool> _canSend;
    private readonly Func<Func<bool>, bool> _waitForNetwork;
    private readonly bool _skipUnchanged;

    private IntPtr _pipeline;
    private IntPtr _bus;
    private IntPtr _sink;
    private IntPtr _encoderElement;
    private IntPtr _sourceElement;
    private IntPtr _rateElement;
    private Thread _thread;
    private volatile bool _stopping;
    private byte[] _output = GC.AllocateArray<byte>(1 << 20, pinned: true);
    private int _bitrate;
    private int _framerate;
    private long _frames;
    private volatile int _forceKeyFrame = 1;

    // Unchanged-frame filter (X11): see FilterFrame.
    private static readonly TimeSpan ChangeTail = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(1);
    private GCHandle _self;
    private IntPtr _lastBuffer;
    private long _lastChangeTicks;
    private long _lastPassTicks;
    private volatile int _passNextFrame = 1;
    private long _skippedFrames;

    public GstVideoStream(CaptureManager capture, MonitorInfo monitor, int streamIndex, Size2 frameSize, int framerate,
        EncoderChoice encoder, int bitrate, bool annexB, bool fullRange, bool showCursor, Func<bool> canSend,
        Func<Func<bool>, bool> waitForNetwork = null, bool skipUnchangedFrames = false)
    {
        Monitor = monitor;
        StreamIndex = streamIndex;
        FrameSize = frameSize;
        _encoder = encoder;
        _hevc = encoder.Hevc;
        _annexB = annexB;
        _bitrate = bitrate;
        _framerate = framerate;
        _canSend = canSend;
        _waitForNetwork = waitForNetwork;
        _skipUnchanged = skipUnchangedFrames && monitor.Kind == CaptureKind.X11;
        string source = capture.BuildSource(monitor, framerate, showCursor);
        _description = EncoderCatalog.BuildPipeline(encoder, source, frameSize.Width, frameSize.Height, framerate, bitrate, fullRange);
    }

    public event FrameEncodedHandler FrameEncoded;

    public MonitorInfo Monitor { get; }

    public int StreamIndex { get; }

    public Size2 FrameSize { get; }

    public EncoderChoice Encoder => _encoder;

    /// <summary>Set when the pipeline reported an error; the owner recreates the stream.</summary>
    public string FailureReason { get; private set; }

    public bool HasFailed => FailureReason != null;

    public long FramesEncoded => Interlocked.Read(ref _frames);

    /// <summary>Captured frames identical to the previous one that were not encoded.</summary>
    public long FramesSkipped => Interlocked.Read(ref _skippedFrames);

    public int Bitrate
    {
        get => _bitrate;
        set
        {
            if (value == _bitrate)
            {
                return;
            }
            _bitrate = value;
            if (_encoderElement != IntPtr.Zero)
            {
                Gst.SetProperty(_encoderElement, "bitrate", _encoder.FormatBitrate(value));
            }
        }
    }

    public void Start()
    {
        Logger.Info($"Stream {StreamIndex}: {Monitor} -> {FrameSize.Width}x{FrameSize.Height}@{_framerate} {_encoder} {_bitrate / 1000} kbps");
        Logger.Debug(_description);
        _pipeline = Gst.ParseLaunch(_description);
        _bus = Gst.gst_element_get_bus(_pipeline);
        _sink = Gst.GetByName(_pipeline, "sink");
        _encoderElement = Gst.GetByName(_pipeline, "enc");
        _sourceElement = Gst.GetByName(_pipeline, "src");
        _rateElement = Gst.GetByName(_pipeline, "rate");
        if (_sink == IntPtr.Zero)
        {
            throw new InvalidOperationException("appsink missing from pipeline");
        }
        if (_skipUnchanged && _sourceElement != IntPtr.Zero)
        {
            _self = GCHandle.Alloc(this);
            if (Gst.AddBufferProbe(_sourceElement, "src", &OnCapturedBuffer, GCHandle.ToIntPtr(_self)) == 0)
            {
                Logger.Debug("Could not attach the unchanged-frame filter");
            }
        }
        if (!Gst.SetState(_pipeline, Gst.StatePlaying))
        {
            string error = Gst.PopError(_bus, 0) ?? "failed to start pipeline";
            throw new InvalidOperationException(error);
        }
        _thread = new Thread(PullLoop)
        {
            Name = "Video Stream " + StreamIndex,
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void ForceKeyFrame()
    {
        _forceKeyFrame = 1;
        _passNextFrame = 1; // a static screen still needs a frame to carry the key frame
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnCapturedBuffer(IntPtr pad, IntPtr info, IntPtr userData)
    {
        try
        {
            return GCHandle.FromIntPtr(userData).Target is GstVideoStream stream ? stream.FilterFrame(Gst.ProbeInfoData(info)) : Gst.PadProbeOk;
        }
        catch
        {
            return Gst.PadProbeOk;
        }
    }

    /// <summary>
    /// Runs on the capture thread for every X11 frame. ximagesrc grabs the whole screen at the
    /// target frame rate even when nothing changes; frames identical to the previous one are
    /// dropped here, before colour conversion and encoding (the most expensive steps). Like the
    /// macOS ScreenCaptureKit stream, which only delivers frames when the screen changes. Frames
    /// keep flowing briefly after a change so the encoder can sharpen the image, plus one per
    /// second as a keep-alive.
    /// </summary>
    private int FilterFrame(IntPtr buffer)
    {
        if (buffer == IntPtr.Zero || _stopping)
        {
            return Gst.PadProbeOk;
        }
        long now = Stopwatch.GetTimestamp();
        bool force = Interlocked.Exchange(ref _passNextFrame, 0) == 1;
        bool changed = force || _lastBuffer == IntPtr.Zero || !Gst.BuffersEqual(_lastBuffer, buffer);
        if (changed)
        {
            _lastChangeTicks = now;
        }
        else if (Stopwatch.GetElapsedTime(_lastChangeTicks, now) > ChangeTail && Stopwatch.GetElapsedTime(_lastPassTicks, now) < KeepAliveInterval)
        {
            Interlocked.Increment(ref _skippedFrames);
            return Gst.PadProbeDrop;
        }
        if (changed)
        {
            IntPtr previous = _lastBuffer;
            _lastBuffer = Gst.gst_mini_object_ref(buffer);
            if (previous != IntPtr.Zero)
            {
                Gst.gst_mini_object_unref(previous);
            }
        }
        _lastPassTicks = now;
        return Gst.PadProbeOk;
    }

    public void SetFramerate(int framerate)
    {
        if (framerate == _framerate)
        {
            return;
        }
        _framerate = framerate;
        if (_rateElement != IntPtr.Zero)
        {
            Gst.SetProperty(_rateElement, "max-rate", framerate.ToString(CultureInfo.InvariantCulture));
        }
    }

    public void SetCursorVisible(bool visible)
    {
        if (Monitor.Kind == CaptureKind.X11 && _sourceElement != IntPtr.Zero)
        {
            Gst.SetProperty(_sourceElement, "show-pointer", visible ? "true" : "false");
        }
    }

    private void PullLoop()
    {
        const ulong pullTimeout = 100_000_000; // 100 ms
        long lastFrameTicks = Stopwatch.GetTimestamp();
        try
        {
            while (!_stopping)
            {
                if (Interlocked.Exchange(ref _forceKeyFrame, 0) == 1)
                {
                    Gst.ForceKeyUnit(_sink);
                }
                IntPtr sample = Gst.gst_app_sink_try_pull_sample(_sink, pullTimeout);
                if (sample == IntPtr.Zero)
                {
                    string error = Gst.PopError(_bus, 0);
                    if (error != null)
                    {
                        Fail(error);
                        return;
                    }
                    if (_frames == 0 && Stopwatch.GetElapsedTime(lastFrameTicks) > TimeSpan.FromSeconds(10))
                    {
                        Fail("no frames produced within 10 seconds");
                        return;
                    }
                    continue;
                }
                try
                {
                    lastFrameTicks = Stopwatch.GetTimestamp();
                    HandleSample(sample);
                }
                finally
                {
                    Gst.gst_mini_object_unref(sample);
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void HandleSample(IntPtr sample)
    {
        IntPtr buffer = Gst.gst_sample_get_buffer(sample);
        if (buffer == IntPtr.Zero || !Gst.gst_buffer_map(buffer, out Gst.MapInfo map, Gst.MapRead))
        {
            return;
        }
        try
        {
            int size = (int)map.Size;
            if (size <= 0)
            {
                return;
            }
            if (!_canSend())
            {
                // Headset not ready (or streaming a video file): drop and restart with a key frame.
                _forceKeyFrame = 1;
                _stopwatch.Reset();
                return;
            }
            if (_waitForNetwork != null && !_waitForNetwork(() => _stopping))
            {
                // The connection stalled: this frame is dropped, so the next one must be a key frame.
                _forceKeyFrame = 1;
                return;
            }
            var input = new ReadOnlySpan<byte>((void*)map.Data, size);
            int needed = NalWriter.MaxOutputSize(size);
            if (_output.Length < needed)
            {
                _output = GC.AllocateArray<byte>(needed * 2, pinned: true);
            }
            int written = NalWriter.Write(input, _output, _annexB, _hevc);
            if (written <= 0)
            {
                return;
            }
            long timestampUs = (long)(_stopwatch.Elapsed.TotalMilliseconds * 1000.0);
            _stopwatch.Start();
            Interlocked.Increment(ref _frames);
            fixed (byte* p = _output)
            {
                FrameEncoded?.Invoke(timestampUs, (nint)p, written);
            }
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, ref map);
        }
    }

    private void Fail(string reason)
    {
        if (_stopping)
        {
            return;
        }
        FailureReason = reason;
        Logger.Error($"Stream {StreamIndex} ({_encoder}) failed: {reason}");
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join(2000);
        _thread = null;
        if (_pipeline != IntPtr.Zero)
        {
            Gst.SetState(_pipeline, Gst.StateNull);
        }
    }

    public void Dispose()
    {
        Stop();
        foreach (IntPtr element in new[] { _sink, _encoderElement, _sourceElement, _rateElement, _bus, _pipeline })
        {
            if (element != IntPtr.Zero)
            {
                Gst.gst_object_unref(element);
            }
        }
        _sink = _encoderElement = _sourceElement = _rateElement = _bus = _pipeline = IntPtr.Zero;
        if (_lastBuffer != IntPtr.Zero)
        {
            Gst.gst_mini_object_unref(_lastBuffer);
            _lastBuffer = IntPtr.Zero;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }
}
