using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Streamer;
using VDStreamer.Vendor;
using VDStreamer.Web;
using VirtualDesktop.Core;

namespace VDStreamer;

/// <summary>Hosts the streamer daemon: single instance lock, lifetime, signals and restarts.</summary>
internal static class App
{
    private static readonly Logger Logger = LogManager.GetLogger("App");
    private static readonly ManualResetEventSlim _exit = new();
    private static volatile bool _restart;

    public static DateTime StartedAt { get; } = DateTime.UtcNow;

    public static void RequestExit() => _exit.Set();

    public static void Restart()
    {
        _restart = true;
        _exit.Set();
    }

    public static int Run()
    {
        FileStream instanceLock = AcquireInstanceLock(TimeSpan.FromSeconds(10));
        if (instanceLock == null)
        {
            Console.Error.WriteLine("Virtual Desktop Streamer is already running. Use 'vdstreamer ui' to open its settings.");
            return 1;
        }
        using (instanceLock)
        {
            Logger.Info($"Starting vdstreamer {typeof(App).Assembly.GetName().Version} with vendor libraries {VendorRuntime.VendorVersion} on {SystemInfo.ReadOsRelease("PRETTY_NAME") ?? "Linux"} ({RuntimeInformation.ProcessArchitecture})");
            Console.WriteLine($"Virtual Desktop Streamer for Linux (protocol {VendorRuntime.VendorVersion})");

            if (Gst.EnsureInitialized())
            {
                Logger.Info("GStreamer " + Gst.Version());
            }
            else
            {
                Logger.Error("GStreamer unavailable: " + Gst.InitError);
                Console.Error.WriteLine("warning: GStreamer unavailable - desktop streaming will not work: " + Gst.InitError);
            }
            VideoStreamer.ConfigureFFmpeg();
            VDStreamer.Linux.Audio.Pulse.RecoverFromPreviousSession();

            StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
            WebServer web = null;
            try
            {
                StreamerManager.Initialize();
                Clipboard.Initialize();
                web = new WebServer(settings.WebUiPort);
                web.Start();
                Console.WriteLine($"Settings UI: http://127.0.0.1:{settings.WebUiPort}/  (or run 'vdstreamer ui')");
                if (!settings.HasValidAccountID)
                {
                    Console.WriteLine("No headset account configured yet: run 'vdstreamer account add meta <your Meta username>' or use the web UI.");
                }
            }
            catch (Exception ex)
            {
                Logger.Fatal(ex, "Startup failed");
                Console.Error.WriteLine("Startup failed: " + ex.Message);
                return 1;
            }

            using PosixSignalRegistration sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; RequestExit(); });
            using PosixSignalRegistration sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; RequestExit(); });
            using PosixSignalRegistration sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; RequestExit(); });

            _exit.Wait();
            Logger.Info(_restart ? "Restarting" : "Shutting down");
            try
            {
                web?.Dispose();
                Clipboard.Shutdown();
                StreamerManager.Shutdown();
                settings.Save();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error during shutdown");
            }
            LogManager.Shutdown();
        }

        if (_restart)
        {
            // Under systemd let the unit's Restart= policy start us again with the new libraries.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            {
                return 75;
            }
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath, "run") { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Restart failed: " + ex.Message);
                return 1;
            }
        }
        return 0;
    }

    public static bool IsRunning()
    {
        FileStream probe = AcquireInstanceLock(TimeSpan.Zero);
        probe?.Dispose();
        return probe == null;
    }

    private static FileStream AcquireInstanceLock(TimeSpan wait)
    {
        string path = Path.Combine(Paths.RuntimeDir, "vdstreamer.lock");
        Stopwatch sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (sw.Elapsed >= wait)
                {
                    return null;
                }
                Thread.Sleep(250);
            }
        }
    }
}
