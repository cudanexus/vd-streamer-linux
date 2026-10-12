using System;
using System.Reflection;
using NLog;
using VDStreamer.Vendor;
using VirtualDesktop.Core;

namespace VDStreamer.Streamer;

/// <summary>
/// Adapts the vendor runtime helpers (written for /Users/Shared on macOS) to Linux paths and
/// makes the vendor libraries report the version of the official streamer they came from,
/// which the Virtual Desktop server and headset app require.
/// </summary>
internal static class VendorRuntime
{
    private static bool _initialized;

    public static Version VendorVersion { get; private set; }

    public static void Initialize(string logFileName = "StreamerLog.txt", LogLevel level = null)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        VendorVersion = VendorManager.GetVendorVersion();

        AssemblyHelper.SetEntryAssembly(typeof(VendorRuntime).Assembly);
        SetField("_productName", "Virtual Desktop Streamer");
        SetField("_version", VendorVersion);
        SetField("_productVersion", VendorVersion.ToString(3));
        SetField("_commonAppDataPath", Paths.DataDir);
        SetField("_userAppDataPath", Paths.ConfigDir);
        SetField("_executableDirectory", PrepareToolDirectory());

        string env = Environment.GetEnvironmentVariable("VDSTREAMER_LOG_LEVEL");
        if (level == null && !string.IsNullOrEmpty(env))
        {
            try
            {
                level = LogLevel.FromString(env);
            }
            catch
            {
            }
        }
        NLogManager.Initialize(System.IO.Path.Combine("logs", logFileName), level ?? LogLevel.Info);
    }

    /// <summary>
    /// The vendor FFmpeg helpers run "ffmpeg"/"ffprobe" from the executable directory; point them at
    /// a private directory that links to the system binaries.
    /// </summary>
    private static string PrepareToolDirectory()
    {
        string bin = Paths.Ensure(System.IO.Path.Combine(Paths.DataDir, "bin"));
        foreach (string tool in new[] { "ffmpeg", "ffprobe" })
        {
            string link = System.IO.Path.Combine(bin, tool);
            try
            {
                string target = FindInPath(tool);
                var existing = new System.IO.FileInfo(link);
                if (target == null || existing.LinkTarget == target)
                {
                    continue;
                }
                if (existing.Exists || existing.LinkTarget != null)
                {
                    existing.Delete();
                }
                System.IO.File.CreateSymbolicLink(link, target);
            }
            catch
            {
            }
        }
        return bin + System.IO.Path.DirectorySeparatorChar;
    }

    private static string FindInPath(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(':'))
        {
            string candidate = System.IO.Path.Combine(dir, name);
            if (!string.IsNullOrEmpty(dir) && System.IO.File.Exists(candidate) && !candidate.StartsWith(Paths.DataDir, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return null;
    }

    private static void SetField(string name, object value)
    {
        FieldInfo field = typeof(AssemblyHelper).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingFieldException(typeof(AssemblyHelper).FullName, name);
        field.SetValue(null, value);
    }
}
