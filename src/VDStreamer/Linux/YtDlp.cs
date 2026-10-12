using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using VDStreamer.Streamer;
using VirtualDesktop.Core;

namespace VDStreamer.Linux;

internal struct DownloadResult
{
    public string FileName;
}

/// <summary>
/// "Paste URL" video downloads through yt-dlp (system install, or a per-user standalone copy
/// downloaded on first use - the macOS app installs it into /usr/local/bin through its daemon).
/// </summary>
internal static class YtDlp
{
    private static readonly Regex DownloadPercentage = new(@"\[download\] \s*([0-9]*\.?[0-9]?)% of", RegexOptions.Compiled);
    private static readonly Regex Downloaded = new(@"\[download\] Destination: (.*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Merged = new("\\[.*\\] Merging formats into \"(.*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AlreadyDownloaded = new(@"\[download\] (.*) has already been downloaded", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Process _process;
    private static TaskCompletionSource<DownloadResult> _tcs;

    private static string LocalPath => Path.Combine(Vendor.Paths.DataDir, "bin", "yt-dlp");

    public static async Task<string> EnsureAsync()
    {
        if (SystemInfo.CommandExists("yt-dlp"))
        {
            return "yt-dlp";
        }
        string local = LocalPath;
        if (!File.Exists(local))
        {
            string asset = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "yt-dlp_linux_aarch64" : "yt-dlp_linux";
            Directory.CreateDirectory(Path.GetDirectoryName(local));
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            byte[] data = await http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/" + asset).ConfigureAwait(false);
            await File.WriteAllBytesAsync(local + ".part", data).ConfigureAwait(false);
            File.SetUnixFileMode(local + ".part", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(local + ".part", local, overwrite: true);
        }
        return local;
    }

    public static async Task<DownloadResult> DownloadVideoAsync(string url, string destinationPath, IProgress<float> progress)
    {
        string tool = await EnsureAsync().ConfigureAwait(false);
        Directory.CreateDirectory(destinationPath);
        _tcs = new TaskCompletionSource<DownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { url, "-f", "bestvideo+bestaudio[ext=m4a]/mp4", "-o", Path.Combine(destinationPath, "%(title)s.%(ext)s"),
                     "--compat-opt", "prefer-vp9-sort", "--restrict-filenames", "--newline" })
        {
            psi.ArgumentList.Add(arg);
        }
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        TaskCompletionSource<DownloadResult> tcs = _tcs;
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data) || tcs.Task.IsCompleted)
            {
                return;
            }
            Match m = DownloadPercentage.Match(e.Data);
            if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out float pct))
            {
                progress.Report(pct);
            }
            lock (stdout)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            lock (stderr)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.Exited += (s, _) =>
        {
            var p = (Process)s;
            if (p.ExitCode != 0)
            {
                tcs.TrySetException(new Exception(stderr.ToString().Trim()));
                return;
            }
            string output = stdout.ToString();
            Match match = Merged.Match(output);
            if (!match.Success)
            {
                match = Downloaded.Match(output);
            }
            if (!match.Success)
            {
                match = AlreadyDownloaded.Match(output);
            }
            if (!match.Success)
            {
                tcs.TrySetException(new Exception("Downloaded, but could not determine the file name. Look in " + SettingsBase<DynamicSettings>.Default.VideosRootPath));
                return;
            }
            tcs.TrySetResult(new DownloadResult { FileName = match.Groups[1].Value.Trim() });
        };
        _process = process;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return await tcs.Task.ConfigureAwait(false);
    }

    public static void CancelDownload()
    {
        _tcs?.TrySetCanceled();
        Process process = Interlocked.Exchange(ref _process, null);
        try
        {
            if (process != null && !process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }
}
