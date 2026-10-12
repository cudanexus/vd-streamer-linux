using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace VDStreamer.Vendor;

/// <summary>
/// Locates, installs and loads the vendor (Virtual Desktop, Inc.) .NET assemblies taken from
/// the official macOS installer. The protocol/network layer is reused as-is; only the
/// platform layer is reimplemented for Linux.
/// </summary>
internal static class VendorManager
{
    public const string UpdatesUrl = "https://download.vrdesktop.net/files/updatesMacOS.txt";
    public const string DefaultPkgUrl = "https://download.vrdesktop.net/files/VirtualDesktop.Streamer.Setup.pkg";
    private const string ComponentPayload = "VirtualDesktopStreamer.pkg/Payload";
    private const string MonoBundle = "Contents/MonoBundle/";
    private const string ArchBundle = "Contents/MonoBundle/.xamarin/osx-x64/";
    public const string MainAssemblyFile = "Virtual Desktop Streamer.dll";

    // Runtime facades that ship inside the macOS bundle but must come from the Linux runtime.
    private static readonly HashSet<string> SkippedRootAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib.dll", "netstandard.dll",
    };

    private static readonly string[] RequiredAssemblies =
    {
        "VirtualDesktop.Core.dll", "VirtualDesktop.Net.dll", "VirtualDesktop.Interfaces.dll",
        "VirtualDesktop.Mobile.Shared.dll", "VirtualDesktop.WCF.dll", "VirtualDesktop.FFmpeg.dll",
        "Xenko.VR.dll", "Xenko.Core.dll", "Xenko.Core.Mathematics.dll", "NLog.dll", "Newtonsoft.Json.dll",
        "System.Private.ServiceModel.dll", "System.ServiceModel.Primitives.dll", "System.ServiceModel.Http.dll",
        MainAssemblyFile,
    };

    private static string _activeDir;

    public static string ActiveVendorDir => _activeDir ??= FindVendorDir();

    public static bool IsInstalled(string dir) =>
        dir != null && RequiredAssemblies.All(f => File.Exists(Path.Combine(dir, f)));

    /// <summary>
    /// $VDSTREAMER_VENDOR_DIR wins; otherwise the newest of the per-user copy (written by
    /// 'vdstreamer update') and the copy shipped by the distro package.
    /// </summary>
    private static string FindVendorDir()
    {
        string env = Environment.GetEnvironmentVariable("VDSTREAMER_VENDOR_DIR");
        if (!string.IsNullOrEmpty(env) && IsInstalled(env))
        {
            return env;
        }
        return new[] { Paths.UserVendorDir, Paths.PackagedVendorDir }
            .Where(IsInstalled)
            .Select(d => (Dir: d, Version: TryGetVersion(d)))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Dir)
            .FirstOrDefault();
    }

    private static Version TryGetVersion(string dir)
    {
        try
        {
            return GetVendorVersion(dir);
        }
        catch
        {
            return new Version(0, 0);
        }
    }

    /// <summary>Version of the vendor streamer the protocol libraries come from (reported to the server).</summary>
    public static Version GetVendorVersion(string dir = null)
    {
        dir ??= ActiveVendorDir;
        return AssemblyName.GetAssemblyName(Path.Combine(dir, MainAssemblyFile)).Version;
    }

    public static void InstallResolver()
    {
        string dir = ActiveVendorDir ?? throw new InvalidOperationException("Vendor assemblies are not installed");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
    }

    /// <summary>Extracts and patches vendor assemblies from an official .pkg into <paramref name="targetDir"/>.</summary>
    public static Version InstallFromPkg(string pkgPath, string targetDir, Action<string> log)
    {
        string staging = targetDir + ".staging";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, true);
        }
        Directory.CreateDirectory(staging);

        int count = 0;
        using (var pkg = new PkgExtractor(pkgPath))
        using (Stream payload = pkg.OpenEntry(ComponentPayload))
        {
            PkgExtractor.ReadCpioPayload(payload, (name, size, content) =>
            {
                string fileName = null;
                if (name.StartsWith(ArchBundle, StringComparison.Ordinal))
                {
                    string rest = name.Substring(ArchBundle.Length);
                    if (!rest.Contains('/') && rest.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                        (rest.StartsWith("VirtualDesktop.", StringComparison.Ordinal) || rest.StartsWith("Xenko.", StringComparison.Ordinal) ||
                         rest == MainAssemblyFile || rest == "System.IO.Hashing.dll") &&
                        rest != "VirtualDesktop.Streamer.Bindings.dll")
                    {
                        fileName = rest;
                    }
                }
                else if (name.StartsWith(MonoBundle, StringComparison.Ordinal))
                {
                    string rest = name.Substring(MonoBundle.Length);
                    if (!rest.Contains('/') && rest.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !SkippedRootAssemblies.Contains(rest))
                    {
                        fileName = rest;
                    }
                }
                if (fileName != null)
                {
                    using FileStream output = File.Create(Path.Combine(staging, fileName));
                    content.CopyTo(output);
                    count++;
                }
            });
        }
        log?.Invoke($"Extracted {count} assemblies from {Path.GetFileName(pkgPath)}");

        string[] missing = RequiredAssemblies.Where(f => !File.Exists(Path.Combine(staging, f))).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException("Package is missing required files: " + string.Join(", ", missing));
        }

        foreach (string line in VendorPatcher.PatchNetAssembly(Path.Combine(staging, "VirtualDesktop.Net.dll")))
        {
            log?.Invoke(line);
        }
        string frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        foreach (string line in TrimRepair.RepairDirectory(staging, frameworkDir))
        {
            log?.Invoke(line);
        }

        Version version = AssemblyName.GetAssemblyName(Path.Combine(staging, MainAssemblyFile)).Version;
        File.WriteAllText(Path.Combine(staging, "vendor.json"), JsonSerializer.Serialize(new
        {
            version = version.ToString(),
            source = Path.GetFileName(pkgPath),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pkgPath))).ToLowerInvariant(),
            installed = DateTime.UtcNow,
        }));

        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, true);
        }
        Directory.Move(staging, targetDir);
        _activeDir = null;
        log?.Invoke($"Installed vendor libraries {version} into {targetDir}");
        return version;
    }

    /// <summary>Returns the version advertised by the official update feed for macOS, or null when offline.</summary>
    public static async Task<(Version Version, string Url)> GetLatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string json = await http.GetStringAsync(UpdatesUrl).ConfigureAwait(false);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Version version = root.TryGetProperty("version", out JsonElement v) && Version.TryParse(v.GetString(), out Version parsed) ? parsed : null;
        string url = root.TryGetProperty("url", out JsonElement u) ? u.GetString() : null;
        return (version, string.IsNullOrEmpty(url) ? DefaultPkgUrl : url);
    }

    public static async Task<string> DownloadPkgAsync(string url, Action<string> log)
    {
        string file = Path.Combine(Paths.Ensure(Path.Combine(Paths.DataDir, "downloads")), "VirtualDesktop.Streamer.Setup.pkg");
        log?.Invoke("Downloading " + url);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using (Stream src = await http.GetStreamAsync(url).ConfigureAwait(false))
        using (FileStream dst = File.Create(file + ".part"))
        {
            await src.CopyToAsync(dst).ConfigureAwait(false);
        }
        File.Move(file + ".part", file, overwrite: true);
        return file;
    }
}
