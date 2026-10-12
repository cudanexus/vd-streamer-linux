using System;
using System.Collections.Generic;
using System.Diagnostics;
using NLog;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using Xenko.Core.Mathematics;

namespace VDStreamer.Linux.Input;

/// <summary>
/// Injects headset keyboard/mouse/laser-pointer input through uinput. Behaviour mirrors the macOS
/// KeyboardHelper/MouseInputHelper/TouchInputHelper; coordinates are global desktop coordinates
/// (the same space as the monitor bounds sent in VideoFormat).
/// </summary>
internal static class InputInjector
{
    private const int AbsMax = 65535;
    private const int WheelUnitsPerStep = 8;
    private static readonly Logger Logger = LogManager.GetLogger("Input");
    private static readonly object _lock = new();

    private static UInputDevice _keyboard;
    private static UInputDevice _absMouse;
    private static UInputDevice _relMouse;
    private static string _error;

    private static Rectangle _desktop = new(0, 0, 1920, 1080);

    // keyboard state
    private static readonly List<ScanCodes> _downKeys = new(8);
    private static readonly List<ScanCodes> _previousDownKeys = new(8);
    private static RawKeyboardState _previousKeyboardState;

    // mouse state
    private static RawMouseState _previousMouseState;
    private static float _relRemainderX, _relRemainderY;
    private static int _wheelRemainder;
    private static int _hwheelRemainder;
    // Device that moved the pointer last. Scrolling must come from the same device: on X11 each
    // switch between devices resets XInput2 smooth scrolling and GTK/Firefox/Chrome drop the next
    // scroll event, so alternating "laser pointer moves (absolute device)" and "thumbstick scrolls
    // (relative device)" every frame lost every single scroll event.
    private static UInputDevice _lastMotionDevice;
    private static readonly HashSet<ushort> _absButtons = new();
    private static readonly HashSet<ushort> _relButtons = new();

    // pointer (laser) state
    private struct PointerInfo
    {
        public RawPointerState PreviousState;
        public TimeSpan LastDownTime;
        public Vector2 LastDownLocation;
    }

    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static readonly TimeSpan DoubleClickTimeSpan = TimeSpan.FromMilliseconds(1200.0);
    private static PointerInfo _leftPointer;
    private static PointerInfo _rightPointer;
    private static int _lastPointerIndex = -1;

    public static string Error => _error;

    /// <summary>Scroll speed multiplier (StreamerSettings.ScrollSpeed); negative inverts.</summary>
    public static double ScrollSpeed { get; set; } = 1.0;

    public static bool IsAvailable => _keyboard != null && _absMouse != null;

    /// <summary>Bounding box of the whole desktop, used to scale absolute pointer coordinates.</summary>
    public static Rectangle DesktopBounds
    {
        get => _desktop;
        set
        {
            if (value.Width > 0 && value.Height > 0)
            {
                _desktop = value;
            }
        }
    }

    public static void Initialize()
    {
        lock (_lock)
        {
            if (_keyboard != null)
            {
                return;
            }
            try
            {
                var keys = new List<ushort>();
                for (ushort k = 1; k < 249; k++)
                {
                    keys.Add(k);
                }
                _keyboard = new UInputDevice(new UInputDevice.Options { Name = "Virtual Desktop Keyboard", Product = 1, Keys = keys });
                _absMouse = new UInputDevice(new UInputDevice.Options
                {
                    Name = "Virtual Desktop Pointer",
                    Product = 2,
                    Keys = new[] { UInputDevice.BTN_LEFT, UInputDevice.BTN_RIGHT, UInputDevice.BTN_MIDDLE },
                    AbsAxes = new[] { (UInputDevice.ABS_X, AbsMax), (UInputDevice.ABS_Y, AbsMax) },
                    RelAxes = new[] { UInputDevice.REL_WHEEL, UInputDevice.REL_HWHEEL, UInputDevice.REL_WHEEL_HI_RES, UInputDevice.REL_HWHEEL_HI_RES },
                    PointerProperty = true,
                });
                _relMouse = new UInputDevice(new UInputDevice.Options
                {
                    Name = "Virtual Desktop Mouse",
                    Product = 3,
                    Keys = new[] { UInputDevice.BTN_LEFT, UInputDevice.BTN_RIGHT, UInputDevice.BTN_MIDDLE },
                    RelAxes = new[] { UInputDevice.REL_X, UInputDevice.REL_Y, UInputDevice.REL_WHEEL, UInputDevice.REL_HWHEEL, UInputDevice.REL_WHEEL_HI_RES, UInputDevice.REL_HWHEEL_HI_RES },
                });
                _error = null;
                Logger.Info("Virtual input devices created");
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                Logger.Error("Input injection unavailable: " + ex.Message);
                Shutdown();
            }
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            ReleaseAll();
            _keyboard?.Dispose();
            _absMouse?.Dispose();
            _relMouse?.Dispose();
            _keyboard = _absMouse = _relMouse = _lastMotionDevice = null;
        }
    }

    private static void ReleaseAll()
    {
        try
        {
            foreach (ScanCodes key in _previousDownKeys)
            {
                ushort code = KeyMap.ToEvdev(key);
                if (code != 0)
                {
                    _keyboard?.EmitFrame(new[] { (UInputDevice.EV_KEY, code, 0) });
                }
            }
            foreach (ushort b in _absButtons)
            {
                _absMouse?.EmitFrame(new[] { (UInputDevice.EV_KEY, b, 0) });
            }
            foreach (ushort b in _relButtons)
            {
                _relMouse?.EmitFrame(new[] { (UInputDevice.EV_KEY, b, 0) });
            }
        }
        catch
        {
        }
        _previousDownKeys.Clear();
        _absButtons.Clear();
        _relButtons.Clear();
        _previousKeyboardState = default;
        _previousMouseState = default;
    }

    // ---- keyboard ---------------------------------------------------------------------------

    public static void UpdateKeyboard(ref RawKeyboardState state, LockStates lockStates)
    {
        UInputDevice kb = _keyboard;
        if (kb == null)
        {
            return;
        }
        state.GetDownKeys(_downKeys);

        // Modifiers follow the real key state; other keys are tapped on press and on every
        // repeat press reported by the headset (same as the macOS streamer).
        foreach (ScanCodes key in _downKeys)
        {
            if (KeyMap.IsModifier(key) && !_previousDownKeys.Contains(key))
            {
                Send(kb, key, 1);
            }
        }
        foreach (ScanCodes key in _previousDownKeys)
        {
            if (KeyMap.IsModifier(key) && !_downKeys.Contains(key))
            {
                Send(kb, key, 0);
            }
        }
        foreach (ScanCodes key in _downKeys)
        {
            if (KeyMap.IsModifier(key))
            {
                continue;
            }
            KeyStateFlags previous = _previousKeyboardState[key];
            if (previous == KeyStateFlags.None || (previous == KeyStateFlags.Down && state.IsKeyPressed(key)))
            {
                Send(kb, key, 1);
                Send(kb, key, 0);
            }
        }
        _previousDownKeys.Clear();
        _previousDownKeys.AddRange(_downKeys);
        state.ResetPressed();
        _previousKeyboardState = state;
    }

    private static void Send(UInputDevice device, ScanCodes key, int value)
    {
        ushort code = KeyMap.ToEvdev(key);
        if (code != 0)
        {
            device.EmitFrame(new[] { (UInputDevice.EV_KEY, code, value) });
        }
    }

    /// <summary>Equivalent of the macOS "change input language" shortcut: Super+Space.</summary>
    public static void ChangeLanguage()
    {
        UInputDevice kb = _keyboard;
        if (kb == null)
        {
            return;
        }
        kb.EmitFrame(new[] { (UInputDevice.EV_KEY, (ushort)125, 1) });
        kb.EmitFrame(new[] { (UInputDevice.EV_KEY, (ushort)57, 1) });
        kb.EmitFrame(new[] { (UInputDevice.EV_KEY, (ushort)57, 0) });
        kb.EmitFrame(new[] { (UInputDevice.EV_KEY, (ushort)125, 0) });
    }

    // ---- mouse ------------------------------------------------------------------------------

    public static void UpdateMouse(ref RawMouseState state, bool isMouseRelative, bool isTrackpad)
    {
        if (_absMouse == null)
        {
            return;
        }
        bool active = state.Active;
        if (!active && !_previousMouseState.Active)
        {
            if (state.ScrollY != 0 || state.ScrollX != 0)
            {
                Scroll(_lastMotionDevice ?? (isMouseRelative ? _relMouse : _absMouse), state.ScrollX, state.ScrollY);
                state.Reset();
            }
            _previousMouseState = state;
            return;
        }

        UInputDevice device = isMouseRelative ? _relMouse : _absMouse;
        HashSet<ushort> pressed = isMouseRelative ? _relButtons : _absButtons;
        // Switching between relative and absolute mode: release buttons held on the other device.
        HashSet<ushort> other = isMouseRelative ? _absButtons : _relButtons;
        UInputDevice otherDevice = isMouseRelative ? _absMouse : _relMouse;
        foreach (ushort b in other)
        {
            otherDevice.EmitFrame(new[] { (UInputDevice.EV_KEY, b, 0) });
        }
        other.Clear();

        if (isMouseRelative)
        {
            float dx = state.X + _relRemainderX;
            float dy = state.Y + _relRemainderY;
            int ix = (int)dx, iy = (int)dy;
            _relRemainderX = dx - ix;
            _relRemainderY = dy - iy;
            if (ix != 0 || iy != 0)
            {
                device.EmitFrame(new[] { (UInputDevice.EV_REL, UInputDevice.REL_X, ix), (UInputDevice.EV_REL, UInputDevice.REL_Y, iy) });
                _lastMotionDevice = device;
            }
        }
        else
        {
            MoveAbsolute(state.X, state.Y);
        }

        if (_previousMouseState.Active)
        {
            if (_previousMouseState.LeftButton && !state.LeftButton) Button(device, pressed, UInputDevice.BTN_LEFT, false);
            if (_previousMouseState.MiddleButton && !state.MiddleButton) Button(device, pressed, UInputDevice.BTN_MIDDLE, false);
            if (_previousMouseState.RightButton && !state.RightButton) Button(device, pressed, UInputDevice.BTN_RIGHT, false);
        }
        if (active)
        {
            if (state.LeftButton && !pressed.Contains(UInputDevice.BTN_LEFT)) Button(device, pressed, UInputDevice.BTN_LEFT, true);
            if (state.MiddleButton && !pressed.Contains(UInputDevice.BTN_MIDDLE)) Button(device, pressed, UInputDevice.BTN_MIDDLE, true);
            if (state.RightButton && !pressed.Contains(UInputDevice.BTN_RIGHT)) Button(device, pressed, UInputDevice.BTN_RIGHT, true);
            if (state.ScrollY != 0 || state.ScrollX != 0)
            {
                Scroll(device, state.ScrollX, state.ScrollY);
            }
            state.Reset();
        }
        else
        {
            foreach (ushort b in new List<ushort>(pressed))
            {
                Button(device, pressed, b, false);
            }
        }
        _previousMouseState = state;
    }

    private static void MoveAbsolute(float x, float y)
    {
        Rectangle d = _desktop;
        int ax = (int)Math.Clamp((x - d.X) * AbsMax / Math.Max(1, d.Width - 1), 0, AbsMax);
        int ay = (int)Math.Clamp((y - d.Y) * AbsMax / Math.Max(1, d.Height - 1), 0, AbsMax);
        _absMouse.EmitFrame(new[] { (UInputDevice.EV_ABS, UInputDevice.ABS_X, ax), (UInputDevice.EV_ABS, UInputDevice.ABS_Y, ay) });
        _lastMotionDevice = _absMouse;
    }

    private static void Button(UInputDevice device, HashSet<ushort> pressed, ushort button, bool down)
    {
        if (down ? pressed.Add(button) : pressed.Remove(button))
        {
            device.EmitFrame(new[] { (UInputDevice.EV_KEY, button, down ? 1 : 0) });
        }
    }

    /// <summary>
    /// The headset reports small scroll steps every frame: the Windows streamer injects 8 wheel units
    /// per step (1/15 of a 120-unit notch), the macOS one 5 pixels. Every frame gets a
    /// high-resolution event with the same 8 units per step (smooth scrolling with libinput 1.19+,
    /// e.g. Ubuntu 22.04 and later); classic REL_WHEEL notches are emitted whenever a full notch has
    /// accumulated, which is all that older libinput (Ubuntu 20.04) uses.
    /// </summary>
    private static void Scroll(UInputDevice device, int scrollX, int scrollY)
    {
        if (device == null)
        {
            return;
        }
        double speed = ScrollSpeed;
        var events = new List<(ushort, ushort, int)>(4);
        if (scrollY != 0)
        {
            int hiRes = (int)Math.Round(scrollY * WheelUnitsPerStep * speed);
            if (hiRes == 0)
            {
                hiRes = Math.Sign(scrollY * speed);
            }
            if (Math.Sign(hiRes) != Math.Sign(_wheelRemainder))
            {
                _wheelRemainder = 0; // direction changed: don't let the old remainder eat the new scroll
            }
            _wheelRemainder += hiRes;
            int notches = _wheelRemainder / 120;
            _wheelRemainder -= notches * 120;
            events.Add((UInputDevice.EV_REL, UInputDevice.REL_WHEEL_HI_RES, hiRes));
            if (notches != 0)
            {
                events.Add((UInputDevice.EV_REL, UInputDevice.REL_WHEEL, notches));
            }
        }
        if (scrollX != 0)
        {
            int hiRes = (int)Math.Round(scrollX * WheelUnitsPerStep * Math.Abs(speed));
            if (hiRes == 0)
            {
                hiRes = Math.Sign(scrollX);
            }
            if (Math.Sign(hiRes) != Math.Sign(_hwheelRemainder))
            {
                _hwheelRemainder = 0;
            }
            _hwheelRemainder += hiRes;
            int notches = _hwheelRemainder / 120;
            _hwheelRemainder -= notches * 120;
            events.Add((UInputDevice.EV_REL, UInputDevice.REL_HWHEEL_HI_RES, hiRes));
            if (notches != 0)
            {
                events.Add((UInputDevice.EV_REL, UInputDevice.REL_HWHEEL, notches));
            }
        }
        device.EmitFrame(events.ToArray());
    }

    // ---- laser pointers ----------------------------------------------------------------------

    public static void UpdatePointers(ref RawPointerState left, ref RawPointerState right)
    {
        if (_absMouse == null)
        {
            return;
        }
        bool leftDown = left.LeftDown || left.MiddleDown || left.RightDown;
        bool rightDown = right.LeftDown || right.MiddleDown || right.RightDown;
        if (leftDown && !rightDown)
        {
            _lastPointerIndex = 0;
        }
        else if (rightDown && !leftDown)
        {
            _lastPointerIndex = 1;
        }
        else if ((_lastPointerIndex == 0 && !left.Active) || (_lastPointerIndex == 1 && !right.Active))
        {
            _lastPointerIndex = -1;
        }
        if (left.Active && _lastPointerIndex != 1)
        {
            UpdatePointer(ref left, ref _leftPointer, leftDown);
        }
        else if (right.Active && _lastPointerIndex != 0)
        {
            UpdatePointer(ref right, ref _rightPointer, rightDown);
        }
    }

    private static void UpdatePointer(ref RawPointerState state, ref PointerInfo info, bool isDown)
    {
        ref RawPointerState prev = ref info.PreviousState;
        var location = new Vector2(state.X, state.Y);
        bool newPress = isDown && !prev.LeftDown && !prev.MiddleDown && !prev.RightDown;
        // Keep the cursor still around a click so double clicks register despite hand jitter.
        if (_stopwatch.Elapsed - info.LastDownTime < DoubleClickTimeSpan && Vector2.DistanceSquared(location, info.LastDownLocation) < 1024f)
        {
            location = info.LastDownLocation;
        }
        else if (newPress)
        {
            info.LastDownTime = _stopwatch.Elapsed;
            info.LastDownLocation = location;
        }
        if (state.Active || prev.Active)
        {
            MoveAbsolute(location.X, location.Y);
        }
        if (prev.Active && !isDown)
        {
            if (prev.LeftDown && !state.LeftDown) Button(_absMouse, _absButtons, UInputDevice.BTN_LEFT, false);
            if (prev.MiddleDown && !state.MiddleDown) Button(_absMouse, _absButtons, UInputDevice.BTN_MIDDLE, false);
            if (prev.RightDown && !state.RightDown) Button(_absMouse, _absButtons, UInputDevice.BTN_RIGHT, false);
        }
        if (state.Active && isDown)
        {
            if (state.LeftDown && !prev.LeftDown) Button(_absMouse, _absButtons, UInputDevice.BTN_LEFT, true);
            if (state.MiddleDown && !prev.MiddleDown) Button(_absMouse, _absButtons, UInputDevice.BTN_MIDDLE, true);
            if (state.RightDown && !prev.RightDown) Button(_absMouse, _absButtons, UInputDevice.BTN_RIGHT, true);
        }
        prev = state;
    }
}
