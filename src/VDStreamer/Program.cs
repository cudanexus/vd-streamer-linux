using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using VDStreamer.Vendor;

namespace VDStreamer;

/// <summary>
/// Entry point. Must not touch vendor types directly: vendor assemblies are only resolvable
/// after <see cref="VendorManager.InstallResolver"/> has run, so everything that uses them
/// lives behind [NoInlining] calls into other classes.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";
        string[] rest = args.Skip(1).ToArray();
        try
        {
            switch (command)
            {
                case "setup":
                    return SetupAsync(rest).GetAwaiter().GetResult();
                case "update":
                    return UpdateAsync(rest).GetAwaiter().GetResult();
                case "version":
                case "--version":
                    PrintVersion();
                    return 0;
                case "help":
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }

            if (VendorManager.ActiveVendorDir == null)
            {
                Console.Error.WriteLine("Virtual Desktop vendor libraries are not installed.");
                Console.Error.WriteLine("Run:  vdstreamer setup            (downloads the official macOS streamer package)");
                Console.Error.WriteLine("  or: vdstreamer setup --pkg /path/to/VirtualDesktop.Streamer.Setup.pkg");
                return 2;
            }
            VendorManager.InstallResolver();
            return RunWithVendor(command, rest);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("vdstreamer: " + ex.Message);
            if (Environment.GetEnvironmentVariable("VDSTREAMER_DEBUG") == "1")
            {
                Console.Error.WriteLine(ex);
            }
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunWithVendor(string command, string[] args) => Cli.Run(command, args);

    private static async Task<int> SetupAsync(string[] args)
    {
        string pkg = null;
        string target = Paths.UserVendorDir;
        bool ifMissing = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pkg":
                    pkg = args[++i];
                    break;
                case "--target":
                    target = args[++i];
                    break;
                case "--if-missing":
                    ifMissing = true;
                    break;
            }
        }
        if (ifMissing && VendorManager.ActiveVendorDir != null)
        {
            return 0;
        }
        if (pkg == null)
        {
            (Version version, string url) = await VendorManager.GetLatestAsync().ConfigureAwait(false);
            Console.WriteLine($"Latest official streamer for macOS: {version}");
            pkg = await VendorManager.DownloadPkgAsync(url, Console.WriteLine).ConfigureAwait(false);
        }
        VendorManager.InstallFromPkg(Path.GetFullPath(pkg), Path.GetFullPath(target), Console.WriteLine);
        return 0;
    }

    private static async Task<int> UpdateAsync(string[] args)
    {
        bool check = args.Contains("--check");
        Version installed = VendorManager.ActiveVendorDir != null ? VendorManager.GetVendorVersion() : null;
        (Version latest, string url) = await VendorManager.GetLatestAsync().ConfigureAwait(false);
        Console.WriteLine($"Installed vendor libraries: {installed?.ToString() ?? "none"}; latest: {latest}");
        if (installed != null && latest != null && installed >= latest)
        {
            Console.WriteLine("Up to date.");
            return 0;
        }
        if (check)
        {
            return 3;
        }
        string pkg = await VendorManager.DownloadPkgAsync(url, Console.WriteLine).ConfigureAwait(false);
        VendorManager.InstallFromPkg(pkg, Paths.UserVendorDir, Console.WriteLine);
        return 0;
    }

    private static void PrintVersion()
    {
        Console.WriteLine("vdstreamer (Linux port) " + typeof(Program).Assembly.GetName().Version);
        string dir = VendorManager.ActiveVendorDir;
        Console.WriteLine(dir != null ? $"vendor libraries {VendorManager.GetVendorVersion(dir)} ({dir})" : "vendor libraries: not installed");
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"vdstreamer - Virtual Desktop Streamer for Linux

Usage:
  vdstreamer [run]                 Start the streamer (web UI on http://127.0.0.1:38899)
  vdstreamer setup [--pkg FILE]    Install vendor protocol libraries from the official
                                   macOS installer (downloaded if --pkg is omitted)
  vdstreamer ui                    Open the settings page (starts the streamer if needed)
  vdstreamer status                Show connection status and pairing requests
  vdstreamer pair [list|accept|deny] [ID|all]
                                   Answer pairing requests from new headsets
  vdstreamer update [--check]      Update vendor libraries to the latest official release
  vdstreamer account add PLATFORM USERNAME
                                   Platforms: meta, pico, vive, google, pfd, apple
  vdstreamer account remove PLATFORM
  vdstreamer account list
  vdstreamer set KEY VALUE         Change a setting (see 'vdstreamer settings')
  vdstreamer settings              Show settings
  vdstreamer encoders              List usable video encoders
  vdstreamer diagnose              Check the environment (capture, encoders, audio, input)
  vdstreamer test-capture [SECS]   Capture + encode the screen without a headset
  vdstreamer test-audio            Record PC audio the way a session does and show the level
  vdstreamer version");
    }
}
