using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Linux;
using VirtualDesktop.Core;
using VirtualDesktop.FFmpeg;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.VR;

namespace VDStreamer.Streamer;

/// <summary>
/// Plays video files from the user's Videos folder on the headset (the "Videos" tab), using the
/// vendor FFmpeg-based MediaReader. Port of the macOS VideoStreamer.
/// </summary>
internal sealed class VideoStreamer : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger("VideoStreamer");
    private static readonly int DefaultConcurrencyLevel = Math.Max(1, Environment.ProcessorCount / 2);
    private static readonly ISet<string> VideoExtensions = new HashSet<string>(new[]
    {
        ".mp4", ".mpg", ".mpeg", ".mpe", ".mp2", ".m1v", ".m2v", ".m4v", ".aaf", ".avchd", ".avi", ".flv", ".mkv", ".mov",
        ".webm", ".wmv", ".wtv", ".3gp", ".asf", ".mjpeg", ".ts", ".m2ts", ".mxf", ".vob", ".hevc", ".swf", ".rm", ".iso",
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<ParallelOptions> _thumbnailOptions = new(() => new ParallelOptions
    {
        MaxDegreeOfParallelism = DefaultConcurrencyLevel,
        TaskScheduler = new PriorityScheduler(ThreadPriority.Lowest, DefaultConcurrencyLevel),
    });
    private static readonly ConcurrentHashSet<string> _requestedFiles = new();

    private CancellationTokenSource _downloadCts;
    private string _path;
    private MediaReader _reader;
    private string _currentSubtitle = string.Empty;
    private CancellationTokenSource _playCts = new();
    private CancellationToken _playCancellationToken;

    public VideoStreamer()
    {
        SettingsBase<DynamicSettings>.Default.IsConnectedChanged += OnIsConnectedChanged;
        ConnectionManager.MessagingClient.Message += OnMessage;
        SettingsBase<SharedStreamerSettings>.Default.StreamingSourceChanged += OnStreamingSourceChanged;
        SettingsBase<SharedMobileSettings>.Default.IsVideoPausedChanged += OnIsVideoPausedChanged;
        SettingsBase<SharedMobileSettings>.Default.VideoPlaybackSpeedChanged += OnVideoPlaybackSpeedChanged;
        Clipboard.Changed += OnClipboardChanged;
    }

    public static bool IsSupported { get; private set; }

    public static string UnsupportedReason { get; private set; }

    /// <summary>Configures FFmpeg.AutoGen to use the system FFmpeg 7 libraries.</summary>
    public static void ConfigureFFmpeg()
    {
        try
        {
            FFmpeg.AutoGen.ffmpeg.RootPath = string.Empty; // let dlopen search the system library path
            uint version = FFmpeg.AutoGen.ffmpeg.avcodec_version();
            IsSupported = (version >> 16) == 61;
            UnsupportedReason = IsSupported ? null : $"video playback needs FFmpeg 7.x (libavcodec.so.61); found libavcodec {version >> 16}";
        }
        catch (Exception ex)
        {
            IsSupported = false;
            UnsupportedReason = "video playback needs FFmpeg 7.x (libavcodec.so.61): " + ex.GetBaseException().Message;
        }
        if (!IsSupported)
        {
            Logger.Warn(UnsupportedReason);
        }
    }

    public void Update()
    {
        MediaReader reader = _reader;
        if (reader == null)
        {
            Thread.Sleep(5);
            return;
        }
        if (!reader.TryReadSample(true, _playCancellationToken, out long timestamp, out bool isAudio, out bool isKeyFrame, out nint data, out int size, out string subtitle))
        {
            return;
        }
        if (subtitle != _currentSubtitle)
        {
            _currentSubtitle = subtitle;
            ConnectionManager.VideoClient.WriteSubtitle(subtitle ?? string.Empty);
        }
        if (isAudio)
        {
            ConnectionManager.AudioClient.WriteSample(data, size);
            return;
        }
        SharedMobileSettings mobile = SettingsBase<SharedMobileSettings>.Default;
        long ts = (long)(timestamp / mobile.VideoPlaybackSpeed);
        var metadata = new FrameMetadata(ts, StreamingSource.Video, isKeyFrame, reader.IsHDR, size);
        ConnectionManager.VideoClient.WriteFrame(ref metadata, data, !mobile.UseAnnexB);
    }

    public void PasteUrl()
    {
        string text = Clipboard.Text;
        if (!string.IsNullOrEmpty(text))
        {
            DownloadVideoAsync(text).Forget();
        }
    }

    public void UpdateDownloadStateFromClipboard(bool force = false)
    {
        SharedStreamerSettings shared = SettingsBase<SharedStreamerSettings>.Default;
        if (!force && shared.DownloadState != DownloadState.None && shared.DownloadState != DownloadState.CanPaste)
        {
            return;
        }
        try
        {
            string text = Clipboard.Text;
            int length = text?.Length ?? 0;
            shared.DownloadState = length > 0 && length < 2048 && Uri.IsWellFormedUriString(text.Trim(), UriKind.Absolute) ? DownloadState.CanPaste : DownloadState.None;
        }
        catch
        {
            shared.DownloadState = DownloadState.None;
        }
    }

    private async Task DownloadVideoAsync(string url)
    {
        if (SettingsBase<SharedStreamerSettings>.Default.DownloadState != DownloadState.CanPaste)
        {
            return;
        }
        DownloadResult result = default;
        try
        {
            SettingsBase<SharedStreamerSettings>.Default.DownloadProgress = 0f;
            SettingsBase<SharedStreamerSettings>.Default.DownloadState = DownloadState.RetrievingInfo;
            string destination = Path.Combine(SettingsBase<DynamicSettings>.Default.VideosRootPath, SettingsBase<SharedMobileSettings>.Default.VideosFolderPath ?? string.Empty);
            var progress = new Progress<float>(f =>
            {
                SharedStreamerSettings shared = SettingsBase<SharedStreamerSettings>.Default;
                if (shared.DownloadState == DownloadState.RetrievingInfo)
                {
                    shared.DownloadState = DownloadState.Downloading;
                }
                shared.DownloadProgress = f;
            });
            result = await YtDlp.DownloadVideoAsync(url.Trim(), destination, progress).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
        }
        catch (Exception ex)
        {
            NetMessage.CreateOutgoing(MessageType.ShowDesktop).SendTo(ConnectionManager.MessagingClient);
            Logger.Error(ex, "Video download failed");
            SystemInfo.Notify("Failed to download video", ex.Message);
        }
        if (string.IsNullOrEmpty(result.FileName))
        {
            UpdateDownloadStateFromClipboard(force: true);
            return;
        }
        SettingsBase<SharedStreamerSettings>.Default.DownloadState = DownloadState.None;
        if (SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            string fileName = result.FileName;
            string root = SettingsBase<DynamicSettings>.Default.VideosRootPath;
            var info = new FileInfo(fileName);
            FileIDOptions options = IsHidden(fileName, root.Length) ? FileIDOptions.Hidden : FileIDOptions.None;
            NetMessage message = NetMessage.CreateOutgoing(MessageType.VideoAdded);
            NetworkBinaryWriter writer = message.Writer;
            writer.Write(fileName.Substring(root.Length));
            writer.Write(info.Length);
            writer.Write(info.LastWriteTimeUtc.Ticks);
            writer.Write((int)options);
            message.SendTo(ConnectionManager.MessagingClient);
        }
    }

    private void PlayVideo(string path, long startTimeUs, int audioStreamIndex)
    {
        _playCancellationToken = _playCts.Token;
        if (_playCancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            if (!IsSupported)
            {
                throw new NotSupportedException(UnsupportedReason);
            }
            if (!string.IsNullOrEmpty(path) && path != _path)
            {
                ReleaseVideo();
                string full = Path.Combine(SettingsBase<DynamicSettings>.Default.VideosRootPath, path);
                _reader = new MediaReader(full, SettingsBase<SharedMobileSettings>.Default.HmdType, GetMaxBitrate(), true, SettingsBase<SharedMobileSettings>.Default.VideoPlaybackSpeed);
                if (audioStreamIndex >= 0)
                {
                    _reader.SetAudioStreamIndex(audioStreamIndex);
                }
                _path = path;
                SettingsBase<SharedMobileSettings>.Default.IsVideoPaused = false;
                SettingsBase<SharedStreamerSettings>.Default.StreamingSource = StreamingSource.Video;
                VideoFormat format = _reader.Format;
                ConnectionManager.VideoClient.WriteVideoFormat(ref format);

                IReadOnlyList<AudioTrack> audioTracks = _reader.AudioTracks;
                NetMessage audio = NetMessage.CreateOutgoing(MessageType.AudioTracks);
                audio.Writer.Write((byte)audioTracks.Count);
                foreach (AudioTrack track in audioTracks)
                {
                    audio.Writer.Write(track.Language);
                    audio.Writer.Write((byte)track.Channels);
                    audio.Writer.Write((sbyte)track.StreamIndex);
                }
                audio.SendTo(ConnectionManager.MessagingClient);

                IReadOnlyList<SubtitleTrack> subtitleTracks = _reader.SubtitleTracks;
                NetMessage subs = NetMessage.CreateOutgoing(MessageType.SubtitleTracks);
                subs.Writer.Write((byte)subtitleTracks.Count);
                foreach (SubtitleTrack track in subtitleTracks)
                {
                    subs.Writer.Write(track.TrackID);
                    subs.Writer.Write(track.Title);
                    subs.Writer.Write(track.Language);
                    subs.Writer.Write(track.StreamIndex);
                }
                subs.SendTo(ConnectionManager.MessagingClient);
            }
            if (startTimeUs > 0)
            {
                _reader?.Seek(startTimeUs);
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Error trying to play video");
            SystemInfo.Notify("Cannot play video", exception.Message);
            ReleaseVideo();
            SettingsBase<SharedStreamerSettings>.Default.StreamingSource = StreamingSource.Desktop;
            NetMessage.CreateOutgoing(MessageType.ShowDesktop).SendTo(ConnectionManager.MessagingClient);
        }
    }

    private void SeekVideo(long startTimeUs)
    {
        _playCancellationToken = _playCts.Token;
        if (_playCancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            _reader?.Seek(startTimeUs);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Error trying to seek video");
            ReleaseVideo();
            SettingsBase<SharedStreamerSettings>.Default.StreamingSource = StreamingSource.Desktop;
        }
    }

    private void ReleaseVideo()
    {
        Interlocked.Exchange(ref _reader, null)?.Dispose();
        _path = null;
    }

    private void DownloadVideo(string relativePath)
    {
        MediaReader reader;
        try
        {
            if (!IsSupported)
            {
                throw new NotSupportedException(UnsupportedReason);
            }
            reader = new MediaReader(Path.Combine(SettingsBase<DynamicSettings>.Default.VideosRootPath, relativePath), SettingsBase<SharedMobileSettings>.Default.HmdType);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Error creating MediaReader to download video");
            ConnectionManager.DataClient.SendVideoOpenFailure();
            return;
        }
        using (reader)
        {
            try
            {
                using (_downloadCts = new CancellationTokenSource())
                {
                    reader.SendVideo(ConnectionManager.DataClient, _downloadCts.Token);
                }
            }
            finally
            {
                _downloadCts = null;
            }
        }
    }

    private void SendVideos(string relative)
    {
        if (ConnectionManager.Status != ConnectionStatus.ConnectedToPeer)
        {
            return;
        }
        NetMessage message = NetMessage.CreateOutgoing(MessageType.Videos);
        NetworkBinaryWriter writer = message.Writer;
        string root = SettingsBase<DynamicSettings>.Default.VideosRootPath;
        string directory = root != null ? Path.Combine(root, relative ?? string.Empty) : null;
        if (directory == null || !Directory.Exists(directory))
        {
            writer.Write((short)0);
            message.SendTo(ConnectionManager.MessagingClient);
            return;
        }
        bool hiddenParent = IsHidden(directory, root.Length);
        var list = new List<FileID>();
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(directory).OrderBy(d => d, NaturalComparer.Default))
            {
                var info = new DirectoryInfo(dir);
                FileIDOptions options = hiddenParent || info.Name.StartsWith('.') ? FileIDOptions.Hidden : FileIDOptions.None;
                list.Add(new FileID(dir.Substring(root.Length), 0L, info.LastWriteTimeUtc, options | FileIDOptions.Folder));
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Error enumerating directories");
        }
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory).Where(f => VideoExtensions.Contains(Path.GetExtension(f))).OrderBy(f => f, NaturalComparer.Default))
            {
                var info = new FileInfo(file);
                FileIDOptions options = hiddenParent || info.Name.StartsWith('.') ? FileIDOptions.Hidden : FileIDOptions.None;
                list.Add(new FileID(file.Substring(root.Length), info.Length, info.LastWriteTimeUtc, options));
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Error enumerating files");
        }
        int count = Math.Min(list.Count, 500);
        writer.Write((short)count);
        foreach (FileID id in list.Take(count))
        {
            writer.Write(id.Path);
            writer.Write(id.Size);
            writer.Write(id.LastWriteTimeUtc.Ticks);
            writer.Write((int)id.Options);
        }
        message.SendTo(ConnectionManager.MessagingClient);
    }

    private void SendThumbnails(IReadOnlyCollection<string> files)
    {
        Parallel.ForEach(files.Where(_requestedFiles.Add), _thumbnailOptions.Value, file =>
        {
            try
            {
                if (ConnectionManager.Status != ConnectionStatus.ConnectedToPeer)
                {
                    return;
                }
                string full = Path.Combine(SettingsBase<DynamicSettings>.Default.VideosRootPath, file);
                if (!File.Exists(full))
                {
                    return;
                }
                byte[] image = ThumbnailGenerator.Create(full, 220, out int length);
                if (image != null && length != 0 && ConnectionManager.Status == ConnectionStatus.ConnectedToPeer)
                {
                    NetMessage message = NetMessage.CreateOutgoing(MessageType.VideoThumbnail, length + file.Length * 2 + 8 + 5);
                    message.Writer.Write(file);
                    message.Writer.Write(length);
                    message.Writer.Write(image, 0, length);
                    message.SendTo(ConnectionManager.MessagingClient);
                }
            }
            catch
            {
            }
            finally
            {
                _requestedFiles.Remove(file);
            }
        });
    }

    public void Dispose()
    {
        SettingsBase<DynamicSettings>.Default.IsConnectedChanged -= OnIsConnectedChanged;
        ConnectionManager.MessagingClient.Message -= OnMessage;
        SettingsBase<SharedStreamerSettings>.Default.StreamingSourceChanged -= OnStreamingSourceChanged;
        SettingsBase<SharedMobileSettings>.Default.IsVideoPausedChanged -= OnIsVideoPausedChanged;
        SettingsBase<SharedMobileSettings>.Default.VideoPlaybackSpeedChanged -= OnVideoPlaybackSpeedChanged;
        Clipboard.Changed -= OnClipboardChanged;
        ReleaseVideo();
    }

    private void OnClipboardChanged(object sender, EventArgs e) => UpdateDownloadStateFromClipboard();

    private void OnIsConnectedChanged(object sender, EventArgs e)
    {
        if (!SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            _playCts.CancelSafe();
            StreamerManager.InvokeAsync(ReleaseVideo);
        }
    }

    private void OnMessage(object sender, NetMessage e)
    {
        try
        {
            switch (e.MessageType)
            {
                case MessageType.RequestVideos:
                {
                    string folder = e.Reader.ReadString();
                    Task.Run(() => SendVideos(folder)).Forget();
                    break;
                }
                case MessageType.RequestVideoThumbnails:
                {
                    short count = e.Reader.ReadInt16();
                    var list = new List<string>(count);
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(e.Reader.ReadString());
                    }
                    Task.Run(() => SendThumbnails(list)).Forget();
                    break;
                }
                case MessageType.PlayVideo:
                {
                    string path = e.Reader.ReadString();
                    long timestampUs = e.Reader.ReadInt64();
                    sbyte audioStreamIndex = e.Reader.ReadSByte();
                    _playCts.CancelSafe();
                    _playCts = new CancellationTokenSource();
                    CancellationToken token = _playCts.Token;
                    StreamerManager.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            PlayVideo(path, timestampUs, audioStreamIndex);
                        }
                    }).Forget();
                    break;
                }
                case MessageType.StopVideo:
                    _playCts.CancelSafe();
                    StreamerManager.InvokeAsync(() =>
                    {
                        ReleaseVideo();
                        SettingsBase<SharedStreamerSettings>.Default.StreamingSource = StreamingSource.Desktop;
                        SettingsBase<DynamicSettings>.Default.ForceKeyFrame = true;
                    }).Forget();
                    break;
                case MessageType.SeekVideo:
                {
                    long timestampUs = e.Reader.ReadInt64();
                    _playCts.CancelSafe();
                    _playCts = new CancellationTokenSource();
                    CancellationToken token = _playCts.Token;
                    StreamerManager.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            SeekVideo(timestampUs);
                        }
                    }).Forget();
                    break;
                }
                case MessageType.SetAudioStreamIndex:
                {
                    sbyte index = e.Reader.ReadSByte();
                    StreamerManager.InvokeAsync(() => _reader?.SetAudioStreamIndex(index)).Forget();
                    break;
                }
                case MessageType.DownloadVideo:
                {
                    string path = e.Reader.ReadString();
                    Task.Run(() => DownloadVideo(path)).Forget();
                    break;
                }
                case MessageType.CancelDownloadVideo:
                    _downloadCts.CancelSafe();
                    break;
                case MessageType.PasteUrl:
                    Task.Run(PasteUrl).Forget();
                    break;
                case MessageType.CancelPasteUrl:
                    Task.Run(YtDlp.CancelDownload).Forget();
                    break;
                case MessageType.SetSubtitleTrackId:
                {
                    Guid id = e.Reader.ReadGuid();
                    StreamerManager.InvokeAsync(() => _reader?.SetSubtitleTrackID(id)).Forget();
                    break;
                }
                case MessageType.SetSubtitleStreamIndex:
                {
                    int index = e.Reader.ReadInt32();
                    StreamerManager.InvokeAsync(() => _reader?.SetSubtitleStreamIndex(index)).Forget();
                    break;
                }
                case MessageType.SetSubtitleOffsetAmount:
                {
                    long offset = e.Reader.ReadInt64();
                    StreamerManager.InvokeAsync(() => _reader?.SetSubtitleOffset(offset)).Forget();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error handling " + e.MessageType);
        }
    }

    private void OnStreamingSourceChanged(object sender, StreamingSourceChangedEventArgs e)
    {
        if (SettingsBase<SharedStreamerSettings>.Default.StreamingSource != StreamingSource.Video)
        {
            _playCts.CancelSafe();
            StreamerManager.InvokeAsync(ReleaseVideo);
        }
    }

    private void OnIsVideoPausedChanged(object sender, EventArgs e)
    {
        StreamerManager.InvokeAsync(() =>
        {
            if (SettingsBase<SharedMobileSettings>.Default.IsVideoPaused)
            {
                _reader?.Pause();
            }
            else
            {
                _reader?.Resume();
            }
        });
    }

    private void OnVideoPlaybackSpeedChanged(object sender, EventArgs e) =>
        StreamerManager.InvokeAsync(() => _reader?.SetPlaybackSpeed(SettingsBase<SharedMobileSettings>.Default.VideoPlaybackSpeed));

    private static bool IsHidden(string path, int rootLength)
    {
        while (path != null && path.Length > rootLength)
        {
            if (Path.GetFileName(path.TrimEnd('/')).StartsWith('.'))
            {
                return true;
            }
            path = Path.GetDirectoryName(path);
        }
        return false;
    }

    public static int GetMaxBitrate() =>
        Math.Min(SettingsBase<SharedMobileSettings>.Default.MeasuredBandwidth, HmdResolutionTypeExtensions.GetMaxVideoBitrate(SettingsBase<SharedMobileSettings>.Default.HmdType));
}
