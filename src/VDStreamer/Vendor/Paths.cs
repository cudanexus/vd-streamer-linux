using System;
using System.IO;

namespace VDStreamer.Vendor;

/// <summary>
/// Filesystem layout used by the Linux port. Everything is per-user (XDG) because the
/// streamer runs inside the user's graphical session.
/// </summary>
internal static class Paths
{
    public const string AppId = "vdstreamer";

    public static string Home => Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataHome => Xdg("XDG_DATA_HOME", Path.Combine(Home, ".local", "share"));

    public static string ConfigHome => Xdg("XDG_CONFIG_HOME", Path.Combine(Home, ".config"));

    public static string RuntimeDir => Xdg("XDG_RUNTIME_DIR", Path.GetTempPath());

    /// <summary>Replacement for the vendor "CommonAppDataPath" (/Users/Shared/... on macOS).</summary>
    public static string DataDir => Ensure(Path.Combine(DataHome, AppId));

    /// <summary>Replacement for the vendor "UserAppDataPath".</summary>
    public static string ConfigDir => Ensure(Path.Combine(ConfigHome, AppId));

    public static string LogDir => Ensure(Path.Combine(DataDir, "logs"));

    /// <summary>Vendor assemblies downloaded/updated by the user (preferred when present).</summary>
    public static string UserVendorDir => Path.Combine(DataDir, "vendor");

    /// <summary>Vendor assemblies shipped next to the executable by the distro package.</summary>
    public static string PackagedVendorDir => Path.Combine(AppContext.BaseDirectory, "vendor");

    public static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Xdg(string variable, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
