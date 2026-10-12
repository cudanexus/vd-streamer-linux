using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NLog;
using Xenko.Core.Mathematics;

namespace VDStreamer.Linux.Capture;

/// <summary>Enumerates X11 monitors through XRandR (works on X11 sessions and XWayland-only setups).</summary>
internal static unsafe class X11Displays
{
    private static readonly Logger Logger = LogManager.GetLogger("X11");

    [StructLayout(LayoutKind.Sequential)]
    private struct XRRMonitorInfo
    {
        public IntPtr Name; // Atom
        public int Primary;
        public int Automatic;
        public int NOutput;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int MWidth;
        public int MHeight;
        public IntPtr Outputs;
    }

    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        IntPtr display = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
            {
                return list;
            }
            IntPtr root = XDefaultRootWindow(display);
            int count;
            IntPtr monitors = XRRGetMonitors(display, root, 1, out count);
            if (monitors != IntPtr.Zero)
            {
                try
                {
                    var span = new ReadOnlySpan<XRRMonitorInfo>((void*)monitors, count);
                    for (int i = 0; i < count; i++)
                    {
                        XRRMonitorInfo m = span[i];
                        string name = "Monitor " + (i + 1);
                        IntPtr atomName = XGetAtomName(display, m.Name);
                        if (atomName != IntPtr.Zero)
                        {
                            name = Marshal.PtrToStringAnsi(atomName) ?? name;
                            XFree(atomName);
                        }
                        if (m.Width >= 16 && m.Height >= 16)
                        {
                            list.Add(new MonitorInfo("x11-" + name, name, new Rectangle(m.X, m.Y, m.Width, m.Height), 0, m.Primary != 0, CaptureKind.X11));
                        }
                    }
                }
                finally
                {
                    XRRFreeMonitors(monitors);
                }
            }
            if (list.Count == 0)
            {
                int screen = XDefaultScreen(display);
                list.Add(new MonitorInfo("x11-screen", "Screen", new Rectangle(0, 0, XDisplayWidth(display, screen), XDisplayHeight(display, screen)), 0, true, CaptureKind.X11));
            }
            // Primary first, then left-to-right.
            list.Sort((a, b) => a.IsPrimary != b.IsPrimary ? (a.IsPrimary ? -1 : 1) : a.Bounds.X != b.Bounds.X ? a.Bounds.X.CompareTo(b.Bounds.X) : a.Bounds.Y.CompareTo(b.Bounds.Y));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Warn("libX11/libXrandr not available: " + ex.Message);
        }
        finally
        {
            if (display != IntPtr.Zero)
            {
                XCloseDisplay(display);
            }
        }
        return list;
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport("libX11.so.6")] private static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetAtomName(IntPtr display, IntPtr atom);
    [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    [DllImport("libXrandr.so.2")] private static extern IntPtr XRRGetMonitors(IntPtr display, IntPtr window, int getActive, out int count);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeMonitors(IntPtr monitors);
}
