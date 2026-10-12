using System;
using System.Runtime.InteropServices;

namespace VDStreamer.Linux;

/// <summary>
/// Minimal GStreamer bindings. Only non-variadic, ABI-stable entry points are used so the same
/// binary works with every GStreamer 1.x release shipped by current distributions.
/// </summary>
internal static unsafe class Gst
{
    private const string Core = "libgstreamer-1.0.so.0";
    private const string App = "libgstapp-1.0.so.0";
    private const string Video = "libgstvideo-1.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    public const int StateNull = 1;
    public const int StateReady = 2;
    public const int StatePaused = 3;
    public const int StatePlaying = 4;

    public const int StateChangeFailure = 0;

    public const int MessageEos = 1 << 0;
    public const int MessageError = 1 << 1;
    public const int MessageWarning = 1 << 2;

    public const int MapRead = 1;

    public const int PadProbeTypeBuffer = 1 << 4;
    public const int PadProbeDrop = 0;
    public const int PadProbeOk = 1;

    public const ulong ClockTimeNone = ulong.MaxValue;

    /// <summary>Offset of GstMessage.type (after the 64-byte GstMiniObject header on 64-bit).</summary>
    private const int MessageTypeOffset = 64;

    [StructLayout(LayoutKind.Sequential)]
    public struct MapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public UIntPtr Size;
        public UIntPtr MaxSize;
        public IntPtr UserData0, UserData1, UserData2, UserData3;
        public IntPtr Reserved0, Reserved1, Reserved2, Reserved3;
    }

    private static readonly object _initLock = new();
    private static bool? _available;
    private static string _initError;

    public static bool IsAvailable => EnsureInitialized();

    public static string InitError => _initError;

    public static bool EnsureInitialized()
    {
        lock (_initLock)
        {
            if (_available.HasValue)
            {
                return _available.Value;
            }
            try
            {
                IntPtr error;
                bool ok = gst_init_check(IntPtr.Zero, IntPtr.Zero, out error);
                if (!ok)
                {
                    _initError = TakeError(error) ?? "gst_init_check failed";
                }
                _available = ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _initError = "GStreamer 1.x is not installed (" + ex.Message + ")";
                _available = false;
            }
            return _available.Value;
        }
    }

    public static bool HasElement(string factoryName)
    {
        if (!EnsureInitialized())
        {
            return false;
        }
        IntPtr factory = gst_element_factory_find(factoryName);
        if (factory == IntPtr.Zero)
        {
            return false;
        }
        gst_object_unref(factory);
        return true;
    }

    public static IntPtr ParseLaunch(string description)
    {
        IntPtr error;
        IntPtr pipeline = gst_parse_launch(description, out error);
        string message = TakeError(error);
        if (pipeline == IntPtr.Zero)
        {
            throw new InvalidOperationException("GStreamer pipeline error: " + (message ?? "unknown") + "\n  " + description);
        }
        if (message != null)
        {
            // Recoverable (e.g. a property this GStreamer version does not know): keep going.
            NLog.LogManager.GetLogger("Gst").Warn("Pipeline warning: " + message);
        }
        return pipeline;
    }

    public static IntPtr GetByName(IntPtr bin, string name) => gst_bin_get_by_name(bin, name);

    public static bool SetState(IntPtr element, int state) => gst_element_set_state(element, state) != StateChangeFailure;

    /// <summary>Returns the error text of a pending ERROR message on the bus, or null.</summary>
    public static string PopError(IntPtr bus, ulong timeoutNs)
    {
        IntPtr msg = gst_bus_timed_pop_filtered(bus, timeoutNs, MessageError | MessageEos);
        if (msg == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            int type = Marshal.ReadInt32(msg, MessageTypeOffset);
            if (type == MessageEos)
            {
                return "end of stream";
            }
            IntPtr err, debug;
            gst_message_parse_error(msg, out err, out debug);
            string text = TakeError(err);
            string dbg = debug != IntPtr.Zero ? Marshal.PtrToStringUTF8(debug) : null;
            if (debug != IntPtr.Zero)
            {
                g_free(debug);
            }
            return text + (string.IsNullOrEmpty(dbg) ? string.Empty : " (" + dbg + ")");
        }
        finally
        {
            gst_mini_object_unref(msg);
        }
    }

    public static string TakeError(IntPtr gerror)
    {
        if (gerror == IntPtr.Zero)
        {
            return null;
        }
        // struct GError { GQuark domain; gint code; gchar *message; }
        string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(gerror, 8));
        g_error_free(gerror);
        return message;
    }

    public static void SetProperty(IntPtr obj, string name, string value) => gst_util_set_object_arg(obj, name, value);

    public static bool ForceKeyUnit(IntPtr element)
    {
        IntPtr ev = gst_video_event_new_upstream_force_key_unit(ClockTimeNone, true, 0);
        return ev != IntPtr.Zero && gst_element_send_event(element, ev);
    }

    /// <summary>GstPadProbeInfo.data (after the type enum and the gulong id on 64-bit).</summary>
    public static IntPtr ProbeInfoData(IntPtr info) => Marshal.ReadIntPtr(info, 16);

    /// <summary>Adds a buffer probe on a static pad; returns the probe id or 0.</summary>
    public static ulong AddBufferProbe(IntPtr element, string padName, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> callback, IntPtr userData)
    {
        IntPtr pad = gst_element_get_static_pad(element, padName);
        if (pad == IntPtr.Zero)
        {
            return 0;
        }
        try
        {
            return (ulong)gst_pad_add_probe(pad, PadProbeTypeBuffer, callback, userData, IntPtr.Zero);
        }
        finally
        {
            gst_object_unref(pad);
        }
    }

    /// <summary>True when both buffers hold exactly the same bytes (or are the same buffer).</summary>
    public static bool BuffersEqual(IntPtr a, IntPtr b)
    {
        if (a == b)
        {
            return true;
        }
        if (!gst_buffer_map(a, out MapInfo ma, MapRead))
        {
            return false;
        }
        try
        {
            if (!gst_buffer_map(b, out MapInfo mb, MapRead))
            {
                return false;
            }
            try
            {
                if (ma.Size != mb.Size)
                {
                    return false;
                }
                if (ma.Data == mb.Data)
                {
                    return true;
                }
                // Compare in chunks: Span lengths are int-sized and 4K frames are ~33 MB.
                long remaining = (long)ma.Size;
                byte* pa = (byte*)ma.Data, pb = (byte*)mb.Data;
                while (remaining > 0)
                {
                    int n = (int)Math.Min(remaining, 1 << 24);
                    if (!new ReadOnlySpan<byte>(pa, n).SequenceEqual(new ReadOnlySpan<byte>(pb, n)))
                    {
                        return false;
                    }
                    pa += n;
                    pb += n;
                    remaining -= n;
                }
                return true;
            }
            finally
            {
                gst_buffer_unmap(b, ref mb);
            }
        }
        finally
        {
            gst_buffer_unmap(a, ref ma);
        }
    }

    public static string Version()
    {
        if (!EnsureInitialized())
        {
            return null;
        }
        IntPtr s = gst_version_string();
        string v = Marshal.PtrToStringUTF8(s);
        g_free(s);
        return v;
    }

    // ---- native ----------------------------------------------------------------------------

    [DllImport(Core)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
    [DllImport(Core)] private static extern IntPtr gst_version_string();
    [DllImport(Core)] private static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] private static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
    [DllImport(Core)] private static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] private static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(Core)] public static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(Core)] private static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, ulong timeout, int types);
    [DllImport(Core)] private static extern void gst_message_parse_error(IntPtr message, out IntPtr gerror, out IntPtr debug);
    [DllImport(Core)] public static extern void gst_object_unref(IntPtr obj);
    [DllImport(Core)] public static extern void gst_mini_object_unref(IntPtr obj);
    [DllImport(Core)] private static extern void gst_util_set_object_arg(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gst_element_send_event(IntPtr element, IntPtr ev);
    [DllImport(Core)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
    [DllImport(Core)] public static extern IntPtr gst_mini_object_ref(IntPtr obj);
    [DllImport(Core)] private static extern IntPtr gst_element_get_static_pad(IntPtr element, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] private static extern nuint gst_pad_add_probe(IntPtr pad, int mask, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> callback, IntPtr userData, IntPtr destroy);
    [DllImport(Core)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);
    [DllImport(Core)] public static extern void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);
    [DllImport(App)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr appsink, ulong timeout);
    [DllImport(Video)] private static extern IntPtr gst_video_event_new_upstream_force_key_unit(ulong runningTime, [MarshalAs(UnmanagedType.Bool)] bool allHeaders, uint count);
    [DllImport(GLib)] private static extern void g_error_free(IntPtr error);
    [DllImport(GLib)] private static extern void g_free(IntPtr mem);
}
