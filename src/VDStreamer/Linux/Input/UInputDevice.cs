using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace VDStreamer.Linux.Input;

/// <summary>Thin wrapper over /dev/uinput (kernel virtual input devices).</summary>
internal sealed unsafe class UInputDevice : IDisposable
{
    public const ushort EV_SYN = 0x00;
    public const ushort EV_KEY = 0x01;
    public const ushort EV_REL = 0x02;
    public const ushort EV_ABS = 0x03;
    public const ushort EV_MSC = 0x04;
    public const ushort SYN_REPORT = 0;
    public const ushort REL_X = 0x00;
    public const ushort REL_Y = 0x01;
    public const ushort REL_HWHEEL = 0x06;
    public const ushort REL_WHEEL = 0x08;
    public const ushort REL_WHEEL_HI_RES = 0x0b;
    public const ushort REL_HWHEEL_HI_RES = 0x0c;
    public const ushort ABS_X = 0x00;
    public const ushort ABS_Y = 0x01;
    public const ushort BTN_LEFT = 0x110;
    public const ushort BTN_RIGHT = 0x111;
    public const ushort BTN_MIDDLE = 0x112;
    public const ushort INPUT_PROP_POINTER = 0x00;

    private const int O_WRONLY = 0x1;
    private const int O_NONBLOCK = 0x800;
    private const ulong UI_DEV_CREATE = 0x5501;
    private const ulong UI_DEV_DESTROY = 0x5502;
    private const ulong UI_DEV_SETUP = 0x405C5503;
    private const ulong UI_ABS_SETUP = 0x401C5504;
    private const ulong UI_SET_EVBIT = 0x40045564;
    private const ulong UI_SET_KEYBIT = 0x40045565;
    private const ulong UI_SET_RELBIT = 0x40045566;
    private const ulong UI_SET_ABSBIT = 0x40045567;
    private const ulong UI_SET_PROPBIT = 0x4004556E;
    private const ushort BUS_VIRTUAL = 0x06;

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public long Sec;
        public long USec;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UInputSetup
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
        public fixed byte Name[80];
        public uint FfEffectsMax;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UInputAbsSetup
    {
        public ushort Code;
        public ushort Padding;
        public int Value;
        public int Minimum;
        public int Maximum;
        public int Fuzz;
        public int Flat;
        public int Resolution;
    }

    private readonly object _lock = new();
    private int _fd = -1;

    public sealed class Options
    {
        public string Name { get; init; }
        public ushort Product { get; init; }
        public IEnumerable<ushort> Keys { get; init; } = Array.Empty<ushort>();
        public IEnumerable<ushort> RelAxes { get; init; } = Array.Empty<ushort>();
        public IEnumerable<(ushort Code, int Max)> AbsAxes { get; init; } = Array.Empty<(ushort, int)>();
        public bool PointerProperty { get; init; }
    }

    public UInputDevice(Options options)
    {
        _fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (_fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException($"Cannot open /dev/uinput (errno {errno}). Install the udev rule shipped with vdstreamer (or add your user to the 'input' group) and make sure the 'uinput' module is loaded.");
        }
        try
        {
            var keys = new List<ushort>(options.Keys);
            if (keys.Count > 0)
            {
                Ioctl(UI_SET_EVBIT, EV_KEY);
                foreach (ushort key in keys)
                {
                    Ioctl(UI_SET_KEYBIT, key);
                }
            }
            bool hasRel = false;
            foreach (ushort rel in options.RelAxes)
            {
                if (!hasRel)
                {
                    Ioctl(UI_SET_EVBIT, EV_REL);
                    hasRel = true;
                }
                Ioctl(UI_SET_RELBIT, rel);
            }
            bool hasAbs = false;
            foreach ((ushort code, int max) in options.AbsAxes)
            {
                if (!hasAbs)
                {
                    Ioctl(UI_SET_EVBIT, EV_ABS);
                    hasAbs = true;
                }
                Ioctl(UI_SET_ABSBIT, code);
                var abs = new UInputAbsSetup { Code = code, Minimum = 0, Maximum = max };
                if (ioctl(_fd, UI_ABS_SETUP, ref abs) < 0)
                {
                    throw new InvalidOperationException("UI_ABS_SETUP failed (errno " + Marshal.GetLastPInvokeError() + ")");
                }
            }
            if (options.PointerProperty)
            {
                Ioctl(UI_SET_PROPBIT, INPUT_PROP_POINTER);
            }
            Ioctl(UI_SET_EVBIT, EV_SYN);

            var setup = new UInputSetup { BusType = BUS_VIRTUAL, Vendor = 0x5644, Product = options.Product, Version = 1 };
            byte[] name = Encoding.ASCII.GetBytes(options.Name);
            for (int i = 0; i < Math.Min(79, name.Length); i++)
            {
                setup.Name[i] = name[i];
            }
            if (ioctl(_fd, UI_DEV_SETUP, ref setup) < 0)
            {
                throw new InvalidOperationException("UI_DEV_SETUP failed (errno " + Marshal.GetLastPInvokeError() + ")");
            }
            if (ioctl(_fd, UI_DEV_CREATE, 0) < 0)
            {
                throw new InvalidOperationException("UI_DEV_CREATE failed (errno " + Marshal.GetLastPInvokeError() + ")");
            }
        }
        catch
        {
            close(_fd);
            _fd = -1;
            throw;
        }
    }

    public static bool IsAccessible()
    {
        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0)
        {
            return false;
        }
        close(fd);
        return true;
    }

    private void Ioctl(ulong request, int value)
    {
        if (ioctl(_fd, request, value) < 0)
        {
            throw new InvalidOperationException($"uinput ioctl 0x{request:x} failed (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    public void Emit(ushort type, ushort code, int value)
    {
        lock (_lock)
        {
            EmitUnlocked(type, code, value);
        }
    }

    /// <summary>Emits several events followed by SYN_REPORT atomically.</summary>
    public void EmitFrame(ReadOnlySpan<(ushort Type, ushort Code, int Value)> events)
    {
        lock (_lock)
        {
            foreach ((ushort type, ushort code, int value) in events)
            {
                EmitUnlocked(type, code, value);
            }
            EmitUnlocked(EV_SYN, SYN_REPORT, 0);
        }
    }

    public void Sync() => Emit(EV_SYN, SYN_REPORT, 0);

    private void EmitUnlocked(ushort type, ushort code, int value)
    {
        if (_fd < 0)
        {
            return;
        }
        var ev = new InputEvent { Type = type, Code = code, Value = value };
        write(_fd, &ev, sizeof(InputEvent));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_fd >= 0)
            {
                ioctl(_fd, UI_DEV_DESTROY, 0);
                close(_fd);
                _fd = -1;
            }
        }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, void* buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, int value);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ref UInputSetup value);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, ulong request, ref UInputAbsSetup value);
}
