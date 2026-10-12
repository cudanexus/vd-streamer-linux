using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Vendor;
using VirtualDesktop.Core;

namespace VDStreamer.Streamer;

/// <summary>
/// The Virtual Desktop server only accepts the current streamer release. Instead of installing a
/// macOS package, the Linux port refreshes its vendor protocol libraries from the latest official
/// macOS release and restarts itself.
/// </summary>
internal static class UpdateHelper
{
    private static readonly Logger Logger = LogManager.GetLogger("UpdateHelper");
    private static readonly Regex HostsRegex = new(@"^\s*(?<Address>[a-zA-Z0-9.:]+)\s+(?<Host>[\w.-]+)", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static int _updating;

    public static Task<bool?> IsLatestVersionAsync() => Task.Run(async () =>
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            Task<string> versionTask = client.GetStringAsync("https://download.vrdesktop.net/files/version.txt");
            if (File.Exists("/etc/hosts"))
            {
                foreach (Match match in HostsRegex.Matches(File.ReadAllText("/etc/hosts")))
                {
                    if (match.Groups["Host"].Value.Contains("vrdesktop.net", StringComparison.OrdinalIgnoreCase))
                    {
                        SettingsBase<DynamicSettings>.Default.HasModifiedHostFile = true;
                        Logger.Warn("/etc/hosts overrides vrdesktop.net; the official service will reject this streamer");
                        return (bool?)false;
                    }
                }
            }
            if (Version.TryParse((await versionTask.ConfigureAwait(false)).Trim(), out Version latest))
            {
                bool ok = AssemblyHelper.Version >= latest;
                if (!ok)
                {
                    Logger.Warn($"Vendor libraries {AssemblyHelper.Version} are older than the current release {latest}");
                }
                return ok;
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Version check failed");
            return null;
        }
    });

    /// <summary>
    /// Downloads the latest official package and installs its protocol libraries.
    /// Returns true when an update was installed (the process then restarts).
    /// </summary>
    public static async Task<bool?> CheckForUpdatesAsync(Action downloadingAction = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _updating, 1) == 1)
        {
            return null;
        }
        try
        {
            (Version latest, string url) = await VendorManager.GetLatestAsync().ConfigureAwait(false);
            if (latest == null || latest <= AssemblyHelper.Version)
            {
                return false;
            }
            Logger.Info($"Updating vendor libraries {AssemblyHelper.Version} -> {latest}");
            downloadingAction?.Invoke();
            string pkg = await VendorManager.DownloadPkgAsync(url, Logger.Info).ConfigureAwait(false);
            VendorManager.InstallFromPkg(pkg, Paths.UserVendorDir, Logger.Info);
            try
            {
                File.Delete(pkg);
            }
            catch
            {
            }
            SystemInfo.Notify("Virtual Desktop Streamer updated", $"Protocol libraries updated to {latest}. Restarting...");
            App.Restart();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Update failed");
            return null;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _updating, 0);
        }
    }
}
