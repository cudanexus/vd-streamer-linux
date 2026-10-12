using VirtualDesktop.Interfaces;

namespace VDStreamer.Linux.Input;

/// <summary>
/// The headset sends PC set-1 scan codes (extended keys as 0xE0xx). Linux evdev key codes are
/// identical for the base set; extended keys are remapped here.
/// </summary>
internal static class KeyMap
{
    public static ushort ToEvdev(ScanCodes key)
    {
        ushort raw = (ushort)key;
        if (raw is > 0 and <= 88)
        {
            return raw switch
            {
                84 => 99,   // PrintScrn (SysRq) -> KEY_SYSRQ
                _ => raw,   // KEY_ESC .. KEY_F12 (86 = KEY_102ND)
            };
        }
        return key switch
        {
            ScanCodes.F21 => 191,
            ScanCodes.F24 => 194,
            ScanCodes.Again => 129,
            ScanCodes.Cut => 137,
            ScanCodes.Paste => 135,
            ScanCodes.NumpadEnter => 96,
            ScanCodes.RightControl => 97,
            ScanCodes.NumpadSlash => 98,
            ScanCodes.RightAlt => 100,
            ScanCodes.Home => 102,
            ScanCodes.Up => 103,
            ScanCodes.PageUp => 104,
            ScanCodes.Left => 105,
            ScanCodes.Right => 106,
            ScanCodes.End => 107,
            ScanCodes.Down => 108,
            ScanCodes.PageDown => 109,
            ScanCodes.Insert => 110,
            ScanCodes.Delete => 111,
            ScanCodes.Pause => 119,
            ScanCodes.LeftWindows => 125,
            ScanCodes.RightWindows => 126,
            ScanCodes.Menu => 127,
            _ => 0,
        };
    }

    public static bool IsModifier(ScanCodes key) => key is ScanCodes.LeftShift or ScanCodes.RightShift or
        ScanCodes.LeftControl or ScanCodes.RightControl or ScanCodes.LeftAlt or ScanCodes.RightAlt or
        ScanCodes.LeftWindows or ScanCodes.RightWindows;
}
