using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NLog;

namespace VDStreamer.Linux;

/// <summary>Host information and session helpers (replacement for the macOS MacHelper/SleepHelper/PowerHelper).</summary>
internal static class SystemInfo
{
    private static readonly Logger Logger = LogManager.GetLogger("SystemInfo");
    private static readonly object _inhibitLock = new();
    private static Process _inhibitor;

    public static bool IsWayland => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) ||
                                    string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase);

    public static bool HasX11 => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));

    public static string GetComputerName()
    {
        try
        {
            string pretty = RunCapture("hostnamectl", "--pretty")?.Trim();
            if (!string.IsNullOrEmpty(pretty))
            {
                return pretty;
            }
        }
        catch
        {
        }
        return Dns.GetHostName();
    }

    /// <summary>Stable machine identifier (macOS uses IOPlatformUUID; Linux uses /etc/machine-id).</summary>
    public static string GetComputerID()
    {
        foreach (string path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                string id = File.ReadAllText(path).Trim();
                if (id.Length >= 32)
                {
                    // Derive a GUID so the raw machine-id is not sent off the machine.
                    byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes("vdstreamer:" + id));
                    return new Guid(hash.AsSpan(0, 16)).ToString().ToUpperInvariant();
                }
            }
            catch
            {
            }
        }
        string file = Path.Combine(Vendor.Paths.DataDir, "computer-id");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, Guid.NewGuid().ToString().ToUpperInvariant());
        }
        return File.ReadAllText(file).Trim();
    }

    public static Task<string> GetComputerDescriptionAsync() => Task.Run(() =>
    {
        var parts = new System.Collections.Generic.List<string>();
        string os = ReadOsRelease("PRETTY_NAME");
        parts.Add(string.IsNullOrEmpty(os) ? "Linux" : os);
        string cpu = ReadCpuModel();
        if (!string.IsNullOrEmpty(cpu))
        {
            parts.Add(cpu);
        }
        string gpu = ReadGpuModel();
        if (!string.IsNullOrEmpty(gpu))
        {
            parts.Add(gpu);
        }
        return string.Join(" | ", parts);
    });

    public static string ReadOsRelease(string key)
    {
        try
        {
            foreach (string line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith(key + "=", StringComparison.Ordinal))
                {
                    return line.Substring(key.Length + 1).Trim('"');
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string ReadCpuModel()
    {
        try
        {
            string line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            return line?.Split(':', 2)[1].Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string ReadGpuModel()
    {
        try
        {
            string output = RunCapture("lspci", null);
            string gpu = output?.Split('\n').FirstOrDefault(l => l.Contains("VGA compatible controller") || l.Contains("3D controller"));
            if (gpu != null)
            {
                int i = gpu.IndexOf(": ", StringComparison.Ordinal);
                return i > 0 ? gpu.Substring(i + 2).Trim() : gpu.Trim();
            }
        }
        catch
        {
        }
        return null;
    }

    /// <summary>Keeps the machine and display awake while a headset is connected.</summary>
    public static void PreventSleep()
    {
        lock (_inhibitLock)
        {
            if (_inhibitor != null && !_inhibitor.HasExited)
            {
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("systemd-inhibit")
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string arg in new[] { "--what=idle:sleep", "--who=Virtual Desktop Streamer", "--why=Headset connected", "--mode=block", "sleep", "infinity" })
                {
                    psi.ArgumentList.Add(arg);
                }
                _inhibitor = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "systemd-inhibit unavailable; the computer may go to sleep during streaming");
            }
        }
    }

    public static void ResumeSleep()
    {
        lock (_inhibitLock)
        {
            try
            {
                if (_inhibitor != null && !_inhibitor.HasExited)
                {
                    _inhibitor.Kill(true);
                }
            }
            catch
            {
            }
            _inhibitor?.Dispose();
            _inhibitor = null;
        }
    }

    public static void LockComputer()
    {
        if (!TryRun("loginctl", "lock-session"))
        {
            TryRun("xdg-screensaver", "lock");
        }
    }

    /// <summary>Wakes the display (and unblanks the screensaver) when a headset connects.</summary>
    public static void WakeUpDisplay()
    {
        TryRun("xdg-screensaver", "reset");
        if (HasX11 && !IsWayland)
        {
            TryRun("xset", "dpms", "force", "on");
        }
    }

    public static void Notify(string title, string body)
    {
        TryRun("notify-send", "--app-name=Virtual Desktop Streamer", "--icon=vdstreamer", title, body);
    }

    public static bool TryRun(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using Process p = Process.Start(psi);
            if (p == null)
            {
                return false;
            }
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string RunCapture(string file, string args, int timeoutMs = 5000) =>
        RunCaptureArgs(file, timeoutMs, string.IsNullOrEmpty(args) ? Array.Empty<string>() : args.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string RunCaptureArgs(string file, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using Process p = Process.Start(psi);
        if (p == null)
        {
            return null;
        }
        Task<string> output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return null;
        }
        return p.ExitCode == 0 ? output.Result : null;
    }

    /// <summary>
    /// "gateway-ip=mac" for each IPv4 default route. Identifies the router, so switching to another
    /// Wi-Fi/LAN is noticed even when DHCP hands out the same address.
    /// </summary>
    public static string[] GetDefaultGateways()
    {
        try
        {
            string[] arp = File.Exists("/proc/net/arp") ? File.ReadAllLines("/proc/net/arp") : Array.Empty<string>();
            return File.ReadLines("/proc/net/route").Skip(1)
                .Select(line => line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Where(f => f.Length > 2 && f[1] == "00000000" && f[2] != "00000000")
                .Select(f =>
                {
                    string ip = new IPAddress(uint.Parse(f[2], System.Globalization.NumberStyles.HexNumber)).ToString();
                    string mac = arp.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .FirstOrDefault(a => a.Length > 3 && a[0] == ip)?[3];
                    return $"{f[0]}:{ip}={mac}";
                })
                .Distinct()
                .OrderBy(g => g, StringComparer.Ordinal)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static bool CommandExists(string name)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        return path.Split(':').Any(dir => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name)));
    }
}
