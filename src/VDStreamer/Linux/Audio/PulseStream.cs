using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VDStreamer.Linux.Audio;

/// <summary>
/// 48 kHz S16LE record/playback stream on libpulse's threaded main loop, connected with
/// PA_STREAM_DONT_MOVE so it stays on its device when the default sink/source changes (which both
/// PulseAudio and pipewire-pulse otherwise do - e.g. PC audio capture would follow the virtual
/// microphone as soon as it becomes the default input).
/// </summary>
internal sealed unsafe class PulseStream : IDisposable
{
    private const string Lib = "libpulse.so.0";

    private const int PA_SAMPLE_S16LE = 3;
    private const int PA_CONTEXT_READY = 4;
    private const int PA_CONTEXT_FAILED = 5;
    private const int PA_CONTEXT_TERMINATED = 6;
    private const int PA_STREAM_READY = 2;
    private const int PA_STREAM_FAILED = 3;
    private const int PA_STREAM_TERMINATED = 4;
    private const int PA_STREAM_INTERPOLATE_TIMING = 0x0002;
    private const int PA_STREAM_AUTO_TIMING_UPDATE = 0x0008;
    private const int PA_STREAM_DONT_MOVE = 0x0200;
    private const int PA_STREAM_ADJUST_LATENCY = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    private IntPtr _mainloop;
    private IntPtr _context;
    private IntPtr _stream;
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;
    private int _pendingLength;
    private bool _disposed;

    private PulseStream()
    {
    }

    public static PulseStream OpenRecord(string device, int channels, string name, int fragmentBytes) =>
        Open(device, channels, name, record: true, new BufferAttr { MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = (uint)fragmentBytes });

    public static PulseStream OpenPlayback(string device, int channels, string name, int targetBytes) =>
        Open(device, channels, name, record: false, new BufferAttr { MaxLength = uint.MaxValue, TLength = (uint)targetBytes, PreBuf = uint.MaxValue, MinReq = uint.MaxValue, FragSize = uint.MaxValue });

    private static PulseStream Open(string device, int channels, string name, bool record, BufferAttr attr)
    {
        var ps = new PulseStream();
        try
        {
            ps._mainloop = pa_threaded_mainloop_new();
            if (ps._mainloop == IntPtr.Zero)
            {
                throw new InvalidOperationException("pa_threaded_mainloop_new failed");
            }
            ps._context = pa_context_new(pa_threaded_mainloop_get_api(ps._mainloop), "Virtual Desktop Streamer");
            pa_context_set_state_callback(ps._context, &OnStateChanged, ps._mainloop);
            if (pa_context_connect(ps._context, null, 0, IntPtr.Zero) < 0)
            {
                throw new InvalidOperationException("Cannot connect to the sound server: " + ps.LastError());
            }
            pa_threaded_mainloop_lock(ps._mainloop);
            try
            {
                if (pa_threaded_mainloop_start(ps._mainloop) < 0)
                {
                    throw new InvalidOperationException("pa_threaded_mainloop_start failed");
                }
                while (true)
                {
                    int state = pa_context_get_state(ps._context);
                    if (state == PA_CONTEXT_READY)
                    {
                        break;
                    }
                    if (state == PA_CONTEXT_FAILED || state == PA_CONTEXT_TERMINATED)
                    {
                        throw new InvalidOperationException("Cannot connect to the sound server: " + ps.LastError());
                    }
                    pa_threaded_mainloop_wait(ps._mainloop);
                }

                var spec = new SampleSpec { Format = PA_SAMPLE_S16LE, Rate = 48000, Channels = (byte)channels };
                ps._stream = pa_stream_new(ps._context, name, &spec, IntPtr.Zero);
                if (ps._stream == IntPtr.Zero)
                {
                    throw new InvalidOperationException("pa_stream_new failed: " + ps.LastError());
                }
                pa_stream_set_state_callback(ps._stream, &OnStateChanged, ps._mainloop);
                pa_stream_set_read_callback(ps._stream, &OnData, ps._mainloop);
                pa_stream_set_write_callback(ps._stream, &OnData, ps._mainloop);
                int flags = PA_STREAM_DONT_MOVE | PA_STREAM_ADJUST_LATENCY | PA_STREAM_INTERPOLATE_TIMING | PA_STREAM_AUTO_TIMING_UPDATE;
                int rc = record
                    ? pa_stream_connect_record(ps._stream, device, &attr, flags)
                    : pa_stream_connect_playback(ps._stream, device, &attr, flags, IntPtr.Zero, IntPtr.Zero);
                if (rc < 0)
                {
                    throw new InvalidOperationException($"Cannot open {(record ? "record" : "playback")} stream on {device}: {ps.LastError()}");
                }
                while (true)
                {
                    int state = pa_stream_get_state(ps._stream);
                    if (state == PA_STREAM_READY)
                    {
                        break;
                    }
                    if (state == PA_STREAM_FAILED || state == PA_STREAM_TERMINATED)
                    {
                        throw new InvalidOperationException($"Cannot open {(record ? "record" : "playback")} stream on {device}: {ps.LastError()}");
                    }
                    pa_threaded_mainloop_wait(ps._mainloop);
                }
            }
            finally
            {
                pa_threaded_mainloop_unlock(ps._mainloop);
            }
            return ps;
        }
        catch
        {
            ps.Dispose();
            throw;
        }
    }

    /// <summary>Blocks until <paramref name="buffer"/> is completely filled with captured audio.</summary>
    public void Read(Span<byte> buffer)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            if (_pendingLength > 0)
            {
                int n = Math.Min(_pendingLength, buffer.Length - filled);
                _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Slice(filled));
                _pendingOffset += n;
                _pendingLength -= n;
                filled += n;
                continue;
            }
            pa_threaded_mainloop_lock(_mainloop);
            try
            {
                while (true)
                {
                    EnsureReady();
                    void* data;
                    UIntPtr length;
                    if (pa_stream_peek(_stream, &data, &length) < 0)
                    {
                        throw new InvalidOperationException("pa_stream_peek failed: " + LastError());
                    }
                    int len = (int)length;
                    if (len == 0)
                    {
                        pa_threaded_mainloop_wait(_mainloop);
                        continue;
                    }
                    if (_pending.Length < len)
                    {
                        _pending = new byte[len * 2];
                    }
                    if (data == null)
                    {
                        _pending.AsSpan(0, len).Clear(); // hole in the stream
                    }
                    else
                    {
                        new ReadOnlySpan<byte>(data, len).CopyTo(_pending);
                    }
                    pa_stream_drop(_stream);
                    _pendingOffset = 0;
                    _pendingLength = len;
                    break;
                }
            }
            finally
            {
                pa_threaded_mainloop_unlock(_mainloop);
            }
        }
    }

    /// <summary>
    /// Discards the oldest captured audio once more than <paramref name="maxBytes"/> are waiting,
    /// keeping about <paramref name="keepBytes"/>. Without this, any stall (network, CPU) turns into
    /// a permanent audio delay, because afterwards the backlog is only consumed in real time.
    /// Returns the number of bytes dropped.
    /// </summary>
    public int DropBacklog(int maxBytes, int keepBytes)
    {
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            EnsureReady();
            long readable = (long)(ulong)pa_stream_readable_size(_stream);
            if (readable < 0 || readable == uint.MaxValue || readable + _pendingLength <= maxBytes)
            {
                return 0;
            }
            int dropped = _pendingLength;
            _pendingLength = 0;
            while (readable > keepBytes)
            {
                void* data;
                UIntPtr length;
                if (pa_stream_peek(_stream, &data, &length) < 0 || (int)length == 0)
                {
                    break;
                }
                if (readable - (long)length < keepBytes && data != null)
                {
                    // Keep the newest part of this fragment (frame-aligned).
                    int skip = (int)(readable - keepBytes) & ~3;
                    int keep = (int)length - skip;
                    if (_pending.Length < keep)
                    {
                        _pending = new byte[keep * 2];
                    }
                    new ReadOnlySpan<byte>((byte*)data + skip, keep).CopyTo(_pending);
                    _pendingOffset = 0;
                    _pendingLength = keep;
                    pa_stream_drop(_stream);
                    dropped += skip;
                    break;
                }
                pa_stream_drop(_stream);
                dropped += (int)length;
                readable -= (long)length;
            }
            return dropped;
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    /// <summary>Queues <paramref name="buffer"/> for playback, waiting for space when the server buffer is full.</summary>
    public void Write(ReadOnlySpan<byte> buffer)
    {
        pa_threaded_mainloop_lock(_mainloop);
        try
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                EnsureReady();
                int writable = (int)pa_stream_writable_size(_stream);
                if (writable <= 0)
                {
                    pa_threaded_mainloop_wait(_mainloop);
                    continue;
                }
                int n = Math.Min(writable, buffer.Length - offset);
                fixed (byte* p = buffer.Slice(offset))
                {
                    if (pa_stream_write(_stream, p, (UIntPtr)n, IntPtr.Zero, 0, 0) < 0)
                    {
                        throw new InvalidOperationException("pa_stream_write failed: " + LastError());
                    }
                }
                offset += n;
            }
        }
        finally
        {
            pa_threaded_mainloop_unlock(_mainloop);
        }
    }

    private void EnsureReady()
    {
        if (_disposed || pa_stream_get_state(_stream) != PA_STREAM_READY)
        {
            throw new InvalidOperationException("audio stream closed");
        }
    }

    private string LastError() => _context == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUTF8(pa_strerror(pa_context_errno(_context)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_mainloop != IntPtr.Zero)
        {
            pa_threaded_mainloop_stop(_mainloop);
        }
        if (_stream != IntPtr.Zero)
        {
            pa_stream_disconnect(_stream);
            pa_stream_unref(_stream);
            _stream = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            pa_context_disconnect(_context);
            pa_context_unref(_context);
            _context = IntPtr.Zero;
        }
        if (_mainloop != IntPtr.Zero)
        {
            pa_threaded_mainloop_free(_mainloop);
            _mainloop = IntPtr.Zero;
        }
    }

    // Callbacks run on the main loop thread with the lock held; they just wake waiters.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnStateChanged(IntPtr obj, IntPtr mainloop) => pa_threaded_mainloop_signal(mainloop, 0);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnData(IntPtr stream, UIntPtr bytes, IntPtr mainloop) => pa_threaded_mainloop_signal(mainloop, 0);

    [DllImport(Lib)] private static extern IntPtr pa_threaded_mainloop_new();
    [DllImport(Lib)] private static extern int pa_threaded_mainloop_start(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_stop(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_free(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_lock(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_unlock(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_wait(IntPtr m);
    [DllImport(Lib)] private static extern void pa_threaded_mainloop_signal(IntPtr m, int waitForAccept);
    [DllImport(Lib)] private static extern IntPtr pa_threaded_mainloop_get_api(IntPtr m);
    [DllImport(Lib)] private static extern IntPtr pa_context_new(IntPtr api, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] private static extern void pa_context_set_state_callback(IntPtr c, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> cb, IntPtr userdata);
    [DllImport(Lib)] private static extern int pa_context_connect(IntPtr c, [MarshalAs(UnmanagedType.LPUTF8Str)] string server, int flags, IntPtr spawnApi);
    [DllImport(Lib)] private static extern int pa_context_get_state(IntPtr c);
    [DllImport(Lib)] private static extern int pa_context_errno(IntPtr c);
    [DllImport(Lib)] private static extern void pa_context_disconnect(IntPtr c);
    [DllImport(Lib)] private static extern void pa_context_unref(IntPtr c);
    [DllImport(Lib)] private static extern IntPtr pa_strerror(int error);
    [DllImport(Lib)] private static extern IntPtr pa_stream_new(IntPtr c, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, SampleSpec* ss, IntPtr map);
    [DllImport(Lib)] private static extern void pa_stream_set_state_callback(IntPtr s, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> cb, IntPtr userdata);
    [DllImport(Lib)] private static extern void pa_stream_set_read_callback(IntPtr s, delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, IntPtr, void> cb, IntPtr userdata);
    [DllImport(Lib)] private static extern void pa_stream_set_write_callback(IntPtr s, delegate* unmanaged[Cdecl]<IntPtr, UIntPtr, IntPtr, void> cb, IntPtr userdata);
    [DllImport(Lib)] private static extern int pa_stream_connect_record(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string dev, BufferAttr* attr, int flags);
    [DllImport(Lib)] private static extern int pa_stream_connect_playback(IntPtr s, [MarshalAs(UnmanagedType.LPUTF8Str)] string dev, BufferAttr* attr, int flags, IntPtr volume, IntPtr syncStream);
    [DllImport(Lib)] private static extern int pa_stream_get_state(IntPtr s);
    [DllImport(Lib)] private static extern int pa_stream_peek(IntPtr s, void** data, UIntPtr* nbytes);
    [DllImport(Lib)] private static extern int pa_stream_drop(IntPtr s);
    [DllImport(Lib)] private static extern UIntPtr pa_stream_writable_size(IntPtr s);
    [DllImport(Lib)] private static extern UIntPtr pa_stream_readable_size(IntPtr s);
    [DllImport(Lib)] private static extern int pa_stream_write(IntPtr s, void* data, UIntPtr nbytes, IntPtr freeCb, long offset, int seek);
    [DllImport(Lib)] private static extern int pa_stream_disconnect(IntPtr s);
    [DllImport(Lib)] private static extern void pa_stream_unref(IntPtr s);
}
