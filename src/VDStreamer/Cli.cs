using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.ServiceModel;
using System.Text;
using System.Text.Json;
using VDStreamer.Linux;
using VDStreamer.Linux.Audio;
using VDStreamer.Linux.Capture;
using VDStreamer.Linux.Input;
using VDStreamer.Linux.Video;
using VDStreamer.Streamer;
using VDStreamer.Vendor;
using VDStreamer.Web;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;

namespace VDStreamer;

internal static class Cli
{
    public static int Run(string command, string[] args)
    {
        VendorRuntime.Initialize();
        switch (command)
        {
            case "run":
            case "daemon":
                return App.Run();
            case "ui":
                return OpenUi();
            case "start":
                return EnsureStarted() ? 0 : 1;
            case "status":
                return Status();
            case "account":
            case "accounts":
                return Account(args);
            case "pair":
                return Pair(args);
            case "settings":
                return ShowSettings();
            case "set":
                return SetSetting(args);
            case "encoders":
                return Encoders();
            case "diagnose":
            case "doctor":
                return Diagnose();
            case "smoke":
                return Smoke();
            case "test-audio":
                return TestAudio();
            case "test-capture":
                return TestCapture(args);
            default:
                Console.Error.WriteLine("Unknown command: " + command + " (see 'vdstreamer help')");
                return 64;
        }
    }

    // ---- daemon API client -------------------------------------------------------------------

    private static int Port => SettingsBase<StreamerSettings>.Default.WebUiPort;

    private static string Call(string method, string path, object body = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(10) };
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-VDS-Token", ApiToken.Value);
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        HttpResponseMessage response = http.Send(request);
        string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(text);
        }
        return text;
    }

    private static int OpenUi()
    {
        string url = $"http://127.0.0.1:{Port}/#token={ApiToken.Value}";
        if (!App.IsRunning() && EnsureStarted())
        {
            System.Threading.Thread.Sleep(1500);
        }
        Console.WriteLine(url);
        if (!SystemInfo.TryRun("xdg-open", url))
        {
            Console.WriteLine("Open the URL above in your browser.");
        }
        return 0;
    }

    /// <summary>
    /// Starts the streamer inside the current graphical session: exports the session environment
    /// to the systemd user manager and starts the unit, or falls back to a detached process.
    /// Used by the autostart entry and 'vdstreamer ui'.
    /// </summary>
    private static bool EnsureStarted()
    {
        if (App.IsRunning())
        {
            return true;
        }
        string[] vars = { "DISPLAY", "WAYLAND_DISPLAY", "XDG_CURRENT_DESKTOP", "XDG_SESSION_TYPE", "XDG_SESSION_DESKTOP", "DBUS_SESSION_BUS_ADDRESS", "XAUTHORITY" };
        bool started = false;
        if (SystemInfo.CommandExists("systemctl"))
        {
            var present = vars.Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToArray();
            SystemInfo.TryRun("systemctl", new[] { "--user", "import-environment" }.Concat(present).ToArray());
            started = SystemInfo.TryRun("systemctl", "--user", "start", "vdstreamer.service");
        }
        if (!started)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(SystemInfo.CommandExists("setsid") ? "setsid" : Environment.ProcessPath) { UseShellExecute = false };
            if (psi.FileName == "setsid")
            {
                psi.ArgumentList.Add(Environment.ProcessPath);
            }
            psi.ArgumentList.Add("run");
            System.Diagnostics.Process.Start(psi);
        }
        for (int i = 0; i < 40; i++)
        {
            if (App.IsRunning())
            {
                return true;
            }
            System.Threading.Thread.Sleep(250);
        }
        Console.Error.WriteLine("The streamer did not start; see 'journalctl --user -u vdstreamer' or ~/.local/share/vdstreamer/logs/StreamerLog.txt");
        return false;
    }

    private static int Status()
    {
        if (!App.IsRunning())
        {
            Console.WriteLine("Streamer: not running");
            return 3;
        }
        using JsonDocument doc = JsonDocument.Parse(Call("GET", "/api/status"));
        JsonElement s = doc.RootElement;
        Console.WriteLine($"Status:      {s.GetProperty("status").GetString()}");
        Console.WriteLine($"Connection:  {s.GetProperty("connection").GetString()}");
        foreach (string key in new[] { "headset", "encoder", "performance", "capture", "registrationError" })
        {
            if (s.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                Console.WriteLine($"{char.ToUpperInvariant(key[0]) + key.Substring(1) + ":",-13}{v.GetString()}");
            }
        }
        foreach (JsonElement p in s.GetProperty("pairingRequests").EnumerateArray())
        {
            Console.WriteLine($"Pairing request {p.GetProperty("id")}: {p.GetProperty("platform")} user '{p.GetProperty("accountId")}' (vdstreamer pair accept)");
        }
        return 0;
    }

    private static int Account(string[] args)
    {
        StreamerSettings settings = SettingsBase<StreamerSettings>.Default;
        string sub = args.ElementAtOrDefault(0) ?? "list";
        if (sub == "list")
        {
            foreach (Platform platform in new[] { Platform.OculusQuest, Platform.Pico, Platform.Vive, Platform.Google, Platform.PlayForDream, Platform.Apple })
            {
                string name = platform switch
                {
                    Platform.OculusQuest => "meta",
                    Platform.PlayForDream => "pfd",
                    _ => platform.ToString().ToLowerInvariant(),
                };
                Console.WriteLine($"{name,-10}{settings.GetAccountID(platform) ?? "-"}");
            }
            return 0;
        }
        if ((sub != "add" && sub != "remove") || args.Length < 2 || !StreamerSettings.TryParsePlatform(args[1], out Platform p))
        {
            Console.Error.WriteLine("usage: vdstreamer account add <meta|pico|vive|google|pfd|apple> <username>\n       vdstreamer account remove <platform>");
            return 64;
        }
        string account = sub == "add" ? string.Join(' ', args.Skip(2)).Trim() : null;
        if (sub == "add" && string.IsNullOrEmpty(account))
        {
            Console.Error.WriteLine("Missing user name");
            return 64;
        }
        if (App.IsRunning())
        {
            Call("POST", "/api/accounts", new { platform = args[1], accountId = account });
        }
        else
        {
            settings.SetAccountID(p, account);
            settings.Save();
        }
        Console.WriteLine(sub == "add" ? $"Saved {args[1]} account '{account}'." : $"Removed {args[1]} account.");
        return 0;
    }

    private static int Pair(string[] args)
    {
        if (!App.IsRunning())
        {
            Console.Error.WriteLine("The streamer is not running.");
            return 1;
        }
        string sub = args.ElementAtOrDefault(0) ?? "list";
        if (sub == "list")
        {
            using JsonDocument doc = JsonDocument.Parse(Call("GET", "/api/status"));
            JsonElement[] requests = doc.RootElement.GetProperty("pairingRequests").EnumerateArray().ToArray();
            if (requests.Length == 0)
            {
                Console.WriteLine("No pending pairing requests.");
            }
            foreach (JsonElement r in requests)
            {
                Console.WriteLine($"{r.GetProperty("id")}  {r.GetProperty("platform")}  {r.GetProperty("accountId")}");
            }
            return 0;
        }
        if (sub != "accept" && sub != "deny")
        {
            Console.Error.WriteLine("usage: vdstreamer pair [list|accept|deny] [id|all]");
            return 64;
        }
        string id = args.ElementAtOrDefault(1) ?? "all";
        Console.WriteLine(Call("POST", $"/api/pairing/{id}/{sub}"));
        return 0;
    }

    private static int ShowSettings()
    {
        foreach ((string key, object value, string help) in SettingsEditor.List())
        {
            Console.WriteLine($"{key,-24}{value,-14}{help}");
        }
        return 0;
    }

    private static int SetSetting(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: vdstreamer set KEY VALUE   (see 'vdstreamer settings')");
            return 64;
        }
        string value = string.Join(' ', args.Skip(1));
        if (App.IsRunning())
        {
            Call("POST", "/api/settings", new Dictionary<string, string> { [args[0]] = value });
        }
        else
        {
            SettingsEditor.Set(args[0], value);
            SettingsBase<StreamerSettings>.Default.Save();
        }
        Console.WriteLine($"{args[0]} = {value}");
        return 0;
    }

    private static int Encoders()
    {
        if (!Gst.EnsureInitialized())
        {
            Console.Error.WriteLine("GStreamer unavailable: " + Gst.InitError);
            return 1;
        }
        IReadOnlyList<EncoderChoice> all = EncoderCatalog.All();
        if (all.Count == 0)
        {
            Console.WriteLine("No supported GStreamer encoders found (install gstreamer1.0-plugins-ugly for x264, or the VA-API/NVENC plugins).");
            return 1;
        }
        foreach (EncoderChoice e in all)
        {
            Console.WriteLine($"{e.Id,-10}{(e.Hevc ? "HEVC" : "H.264"),-7}{(e.Hardware ? "hardware" : "software"),-10}{e.Element}");
        }
        return 0;
    }

    private static int Diagnose()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail, bool optional = false)
        {
            Console.WriteLine($"[{(ok ? " OK " : optional ? "WARN" : "FAIL")}] {name}{(string.IsNullOrEmpty(detail) ? string.Empty : ": " + detail)}");
            if (!ok && !optional)
            {
                failures++;
            }
        }

        Check("Vendor protocol libraries", true, $"{AssemblyHelper.Version} in {VendorManager.ActiveVendorDir}");
        int typeFailures = CheckVendorTypes(quiet: true);
        Check("Vendor libraries load on this runtime", typeFailures == 0, typeFailures == 0 ? null : typeFailures + " type load errors (run 'vdstreamer smoke')");
        try
        {
            (Version latest, _) = VendorManager.GetLatestAsync().GetAwaiter().GetResult();
            Check("Vendor libraries up to date", latest == null || AssemblyHelper.Version >= latest, $"latest official release {latest} (run 'vdstreamer update')");
        }
        catch (Exception ex)
        {
            Check("Update server reachable", false, ex.Message, optional: true);
        }
        Check("Headset account configured", SettingsBase<StreamerSettings>.Default.HasValidAccountID, "vdstreamer account add meta <username>");
        NetworkAdapter[] adapters = NetworkInterfaceHelper.GetPrivateAdapters(false, true, true, false).ToArray();
        Check("Private network adapter", adapters.Length > 0, string.Join(", ", adapters.Select(a => a.IPAddresString)));
        Check("Graphical session", SystemInfo.IsWayland || SystemInfo.HasX11, SystemInfo.IsWayland ? "Wayland" : SystemInfo.HasX11 ? "X11 " + Environment.GetEnvironmentVariable("DISPLAY") : "no DISPLAY/WAYLAND_DISPLAY");
        bool gst = Gst.EnsureInitialized();
        Check("GStreamer", gst, gst ? Gst.Version() : Gst.InitError);
        if (gst)
        {
            CaptureKind kind = CaptureManager.ResolveBackend();
            bool captureElement = kind == CaptureKind.X11 ? Gst.HasElement("ximagesrc") : Gst.HasElement("pipewiresrc");
            Check("Screen capture", captureElement, kind == CaptureKind.X11 ? "X11 (ximagesrc, gstreamer1.0-plugins-good)" : "PipeWire portal (pipewiresrc, gstreamer1.0-pipewire)");
            if (kind == CaptureKind.PipeWire)
            {
                bool portal = PortalScreenCast.IsAvailableAsync().GetAwaiter().GetResult();
                Check("xdg-desktop-portal ScreenCast", portal, portal ? null : "install xdg-desktop-portal and the backend for your desktop (gnome/kde/wlr/hyprland)");
            }
            IReadOnlyList<EncoderChoice> h264 = EncoderCatalog.Available(false);
            Check("H.264 encoder", h264.Count > 0, h264.Count > 0 ? string.Join(", ", h264.Select(e => e.Element)) : "install gstreamer1.0-plugins-ugly (x264enc) or VA-API/NVENC plugins");
            bool hardware = h264.Any(e => e.Hardware);
            Check("Hardware video encoder", hardware, hardware
                ? string.Join(", ", h264.Where(e => e.Hardware).Select(e => e.Element))
                : HardwareEncoderHint(), optional: true);
            string hevc = string.Join(", ", EncoderCatalog.Available(true).Select(e => e.Element));
            Check("HEVC encoder", hevc.Length > 0, hevc.Length > 0 ? hevc : "none", optional: true);
            Check("H.264 parser", Gst.HasElement("h264parse"), "h264parse (gstreamer1.0-plugins-bad)");
        }
        Check("Audio (PulseAudio/PipeWire)", Pulse.IsAvailable, Pulse.IsAvailable ? "pactl OK" : "needs a running PulseAudio or pipewire-pulse server and pactl (pulseaudio-utils)");
        bool uinput = UInputDevice.IsAccessible();
        Check("Input injection (/dev/uinput)", uinput, uinput ? null : "install the udev rule (60-vdstreamer-uinput.rules), run 'sudo modprobe uinput' and log in again");
        VideoStreamer.ConfigureFFmpeg();
        Check("Video file playback", VideoStreamer.IsSupported, VideoStreamer.UnsupportedReason, optional: true);
        Check("Clipboard sharing", SystemInfo.CommandExists("wl-copy") || SystemInfo.CommandExists("xclip") || SystemInfo.CommandExists("xsel"), "wl-clipboard or xclip", optional: true);
        Check("Sleep inhibition", SystemInfo.CommandExists("systemd-inhibit"), "systemd-inhibit", optional: true);
        Console.WriteLine(failures == 0 ? "\nEverything needed for streaming is available." : $"\n{failures} problem(s) found.");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>What to install for GPU encoding, based on the GPUs present.</summary>
    private static string HardwareEncoderHint()
    {
        var vendors = new HashSet<string>();
        try
        {
            foreach (string f in System.IO.Directory.GetDirectories("/sys/class/drm", "card*").Select(d => System.IO.Path.Combine(d, "device", "vendor")).Where(System.IO.File.Exists))
            {
                vendors.Add(System.IO.File.ReadAllText(f).Trim());
            }
        }
        catch
        {
        }
        var hints = new List<string> { "only software encoding is available, which uses a lot of CPU" };
        if (vendors.Contains("0x8086") || vendors.Contains("0x1002"))
        {
            hints.Add("Intel/AMD: install gstreamer1.0-vaapi (Debian/Ubuntu) or gstreamer1-vaapi (Fedora) plus the VA-API driver (intel-media-va-driver / mesa-va-drivers)");
        }
        if (vendors.Contains("0x10de"))
        {
            hints.Add("NVIDIA: NVENC needs the proprietary driver and GStreamer 1.18 or newer with the nvcodec plugin (gstreamer1.0-plugins-bad on Ubuntu 22.04+; not available on Ubuntu 20.04)");
        }
        hints.Add("to save CPU meanwhile: vdstreamer set maxFramerate 30");
        return string.Join("; ", hints);
    }

    public static int CheckVendorTypes(bool quiet = false)
    {
        int failures = 0;
        foreach (string name in new[] { "VirtualDesktop.Core", "VirtualDesktop.Net", "VirtualDesktop.Interfaces", "VirtualDesktop.Mobile.Shared", "VirtualDesktop.WCF", "VirtualDesktop.FFmpeg", "Xenko.VR", "Xenko.Core", "Xenko.Core.Mathematics", "System.Private.ServiceModel", "System.ServiceModel.Primitives", "System.ServiceModel.Http" })
        {
            try
            {
                Type[] types = Assembly.Load(name).GetTypes();
                if (!quiet)
                {
                    Console.WriteLine($"  {name}: {types.Length} types OK");
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (Exception le in ex.LoaderExceptions.Where(e => e != null).Distinct())
                {
                    if (!quiet)
                    {
                        Console.WriteLine($"  {name}: {le.Message}");
                    }
                    failures++;
                }
            }
            catch (Exception ex)
            {
                if (!quiet)
                {
                    Console.WriteLine($"  {name}: {ex.Message}");
                }
                failures++;
            }
        }
        return failures;
    }

    /// <summary>Runs the real capture + encode pipeline for a few seconds without a headset.</summary>
    private static int TestCapture(string[] args)
    {
        if (!Gst.EnsureInitialized())
        {
            Console.Error.WriteLine("GStreamer unavailable: " + Gst.InitError);
            return 1;
        }
        int seconds = int.TryParse(args.ElementAtOrDefault(0), out int s) ? s : 5;
        using var capture = new CaptureManager();
        MonitorInfo[] monitors = capture.GetMonitors(embedCursor: true);
        Console.WriteLine($"Capture backend: {capture.Kind}; monitors: {string.Join(", ", monitors.Select(m => m.ToString()))}");
        if (monitors.Length == 0)
        {
            return 1;
        }
        EncoderChoice encoder = EncoderCatalog.Select(SettingsBase<StreamerSettings>.Default.PreferredEncoder, SettingsBase<StreamerSettings>.Default.PreferredCodec == PreferredCodec.HEVC);
        if (encoder == null)
        {
            Console.Error.WriteLine("No usable encoder");
            return 1;
        }
        MonitorInfo monitor = monitors[0];
        var size = new Xenko.Core.Mathematics.Size2(monitor.Bounds.Width & ~1, monitor.Bounds.Height & ~1);
        long bytes = 0, frames = 0, keyFrames = 0;
        using var stream = new GstVideoStream(capture, monitor, 0, size, 60, encoder, 20_000_000, true, true, true, () => true);
        stream.FrameEncoded += (ts, data, len) =>
        {
            frames++;
            bytes += len;
            unsafe
            {
                if (NalWriter.ContainsKeyFrame(new ReadOnlySpan<byte>((void*)data, len), encoder.Hevc))
                {
                    keyFrames++;
                }
            }
        };
        Console.WriteLine($"Encoding {monitor} with {encoder} for {seconds} s...");
        stream.Start();
        System.Threading.Thread.Sleep(seconds * 1000);
        stream.Stop();
        Console.WriteLine($"{frames} frames ({frames / (double)seconds:F1} fps), {keyFrames} key frames, {bytes * 8 / 1000 / seconds} kbit/s" +
                          (stream.HasFailed ? ", failed: " + stream.FailureReason : string.Empty));
        return frames > 0 && !stream.HasFailed ? 0 : 1;
    }

    /// <summary>Captures PC audio the way a streaming session does and reports the signal level.</summary>
    private static int TestAudio()
    {
        string device = Pulse.EnableSpeakers(SettingsBase<StreamerSettings>.Default.HeadsetOnlyAudio);
        Console.WriteLine("Recording 3 s from " + device + " (play something on the PC)...");
        int peak = 0;
        long silentChunks = 0, chunks = 0;
        try
        {
            using PulseStream stream = PulseStream.OpenRecord(device, 2, "Audio test", 1152);
            byte[] buffer = new byte[1152];
            for (int i = 0; i < 125; i++)
            {
                stream.Read(buffer);
                int chunkPeak = 0;
                for (int j = 0; j + 1 < buffer.Length; j += 2)
                {
                    chunkPeak = Math.Max(chunkPeak, Math.Abs((int)(short)(buffer[j] | (buffer[j + 1] << 8))));
                }
                peak = Math.Max(peak, chunkPeak);
                chunks++;
                if (chunkPeak <= 1)
                {
                    silentChunks++;
                }
            }
        }
        finally
        {
            Pulse.RestoreSpeakers();
        }
        Console.WriteLine($"{chunks} chunks, {silentChunks} silent, peak level {peak} ({20 * Math.Log10(Math.Max(1, peak) / 32768.0):F1} dBFS)");
        return peak > 1 ? 0 : 1;
    }

    private static int Smoke()
    {
        Console.WriteLine("Type load check:");
        int failures = CheckVendorTypes();
        Console.WriteLine($"Vendor version: {AssemblyHelper.Version}  data: {AssemblyHelper.CommonAppDataPath}");
        foreach (NetworkAdapter adapter in NetworkInterfaceHelper.GetPrivateAdapters(false, true, true, false))
        {
            Console.WriteLine($"Adapter: {adapter.IPAddresString} mac={adapter.MACAddresString} wifi={adapter.IsWireless} gigabit={adapter.IsGigabit}");
        }
        Console.WriteLine($"Connection type: {NetworkInterfaceHelper.GetConnectionType()}");
        Console.WriteLine($"Server: {NetHelper.Region} {NetHelper.GetServerUrl()}");
        Console.WriteLine($"Shared settings: HMD={SettingsBase<SharedMobileSettings>.Default.HmdType} bitrate={SettingsBase<SharedMobileSettings>.Default.DesktopBitrate}");
        using (var client = new NetMessagingClient(new IPEndPoint(NetClient.AnyAddress, 38810)))
        {
            Console.WriteLine($"Messaging client status: {client.Status}");
        }
        ServiceHelper<IComputerRegistry>.Initialize(HttpBinding.Default, NetHelper.GetServerUrl());
        Console.WriteLine("WCF channel factory initialized");
        Console.WriteLine($"GStreamer: {(Gst.EnsureInitialized() ? Gst.Version() : Gst.InitError)}");
        return failures == 0 ? 0 : 1;
    }
}
