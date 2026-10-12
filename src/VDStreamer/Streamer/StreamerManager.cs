using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Linux;
using VDStreamer.Linux.Audio;
using VDStreamer.Linux.Input;
using VirtualDesktop.Core;
using VirtualDesktop.Interfaces;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;

namespace VDStreamer.Streamer;

/// <summary>
/// Owns the streaming session while a headset is connected: the update loop (desktop or video
/// file streaming), headset input, audio and microphone. Port of the macOS StreamerManager.
/// </summary>
internal static class StreamerManager
{
    private static readonly Logger Logger = LogManager.GetLogger("StreamerManager");
    private static readonly object _updateLock = new();

    private static VideoStreamer _videoStreamer;
    private static volatile bool _isRunning;
    private static bool _hasReleasedInput;
    private static Thread _updateThread;
    private static DispatcherSlim _dispatcher;
    private static AudioCapture _audioCapture;
    private static Microphone _microphone;
    private static DesktopStreamer _desktopStreamer;
    private static FrameData _frameData;

    public static DesktopStreamer DesktopStreamer => _desktopStreamer;

    public static VideoStreamer VideoStreamer => _videoStreamer;

    /// <summary>Index of the last frame request received from the headset (negative while it is not ready).</summary>
    public static int LastFrameIndex => _frameData.FrameIndex;

    public static bool IsPhysicalMouseActive => false;

    public static bool InvokeRequired
    {
        get
        {
            DispatcherSlim dispatcher = _dispatcher;
            return dispatcher != null && !dispatcher.CheckAccess();
        }
    }

    public static void Initialize()
    {
        ConnectionManager.Initialize();
        _videoStreamer = new VideoStreamer();
        SettingsBase<DynamicSettings>.Default.IsConnectedChanged += OnIsConnectedChanged;
        SettingsBase<StreamerSettings>.Default.PreferredCodecChanged += (_, _) => InvokeAsync(() =>
        {
            if (_isRunning && SettingsBase<SharedStreamerSettings>.Default.StreamingSource == StreamingSource.Desktop)
            {
                _desktopStreamer?.ReleaseStreams();
            }
        });
        SettingsBase<SharedStreamerSettings>.Default.StreamingSourceChanged += OnStreamingSourceChanged;
    }

    public static void Shutdown()
    {
        SettingsBase<DynamicSettings>.Default.IsConnectedChanged -= OnIsConnectedChanged;
        SettingsBase<SharedStreamerSettings>.Default.StreamingSourceChanged -= OnStreamingSourceChanged;
        _isRunning = false;
        _updateThread?.Join(3000);
        ConnectionManager.Shutdown();
        Pulse.RestoreSpeakers();
        Pulse.RestoreMicrophone();
        _videoStreamer?.Dispose();
        InputInjector.Shutdown();
        SystemInfo.ResumeSleep();
    }

    public static Task InvokeAsync(Action action)
    {
        DispatcherSlim dispatcher = _dispatcher;
        if (dispatcher != null)
        {
            return dispatcher.InvokeAsync(action);
        }
        action();
        return Task.CompletedTask;
    }

    private static void UpdateLoop()
    {
        lock (_updateLock)
        {
            StreamingSource streamingSource = SettingsBase<SharedStreamerSettings>.Default.StreamingSource;
            try
            {
                InputInjector.ScrollSpeed = SettingsBase<StreamerSettings>.Default.ScrollSpeed;
                InputInjector.Initialize();
                var receiveThread = new Thread(ReceiveFrameData) { Name = "ReceiveFrameData", Priority = ThreadPriority.AboveNormal, IsBackground = true };
                receiveThread.Start();
                _dispatcher = new DispatcherSlim();
                _audioCapture = new AudioCapture();
                _microphone = new Microphone(ConnectionManager.AudioClient);
                _desktopStreamer = new DesktopStreamer();
                while (_isRunning)
                {
                    _dispatcher.RunCallbacks();
                    streamingSource = SettingsBase<SharedStreamerSettings>.Default.StreamingSource;
                    switch (streamingSource)
                    {
                        case StreamingSource.Desktop:
                            _desktopStreamer.Update();
                            if (IsPhysicalMouseActive)
                            {
                                if (!_hasReleasedInput)
                                {
                                    _hasReleasedInput = true;
                                    ref RawInputState inputState = ref _frameData.InputState;
                                    inputState.LeftPointerState.Active = false;
                                    inputState.RightPointerState.Active = false;
                                    inputState.MouseState.Active = false;
                                    SettingsBase<DynamicSettings>.Default.CursorVisible = true;
                                }
                            }
                            else
                            {
                                _hasReleasedInput = false;
                            }
                            continue;
                        case StreamingSource.Video:
                            if (!SettingsBase<SharedMobileSettings>.Default.IsVideoPaused)
                            {
                                _videoStreamer.Update();
                                continue;
                            }
                            break;
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "Error streaming " + streamingSource);
                SettingsBase<DynamicSettings>.Default.StatusMessage = "Streaming error: " + exception.Message;
                ConnectionManager.MessagingClient.Disconnect();
            }
            finally
            {
                ReleaseResources();
            }
        }
    }

    private static void ReleaseResources()
    {
        DispatcherSlim dispatcher = Interlocked.Exchange(ref _dispatcher, null);
        try
        {
            dispatcher?.RunCallbacks();
        }
        catch
        {
        }
        Interlocked.Exchange(ref _desktopStreamer, null)?.Dispose();
        Interlocked.Exchange(ref _audioCapture, null)?.Dispose();
        Interlocked.Exchange(ref _microphone, null)?.Dispose();
        InputInjector.Shutdown();
    }

    private static void ReceiveFrameData()
    {
        try
        {
            NetDataClient dataClient = ConnectionManager.DataClient;
            DynamicSettings dynamicSettings = SettingsBase<DynamicSettings>.Default;
            SharedStreamerSettings sharedStreamer = SettingsBase<SharedStreamerSettings>.Default;
            ref RawInputState inputState = ref _frameData.InputState;
            while (_isRunning)
            {
                try
                {
                    dataClient.ReadFrameData(ref _frameData);
                    if (inputState.KeyboardState.IsActive)
                    {
                        InputInjector.UpdateKeyboard(ref inputState.KeyboardState, _frameData.LockStates);
                    }
                    if (!IsPhysicalMouseActive)
                    {
                        bool isMouseRelative = !inputState.ControllerState.TrackpadStatus;
                        bool isTrackpad = inputState.ControllerState.LeftTrigger > 0;
                        if (sharedStreamer.StreamingSource == StreamingSource.Desktop)
                        {
                            InputInjector.UpdatePointers(ref inputState.LeftPointerState, ref inputState.RightPointerState);
                        }
                        InputInjector.UpdateMouse(ref inputState.MouseState, isMouseRelative, isTrackpad);
                        dynamicSettings.CursorVisible = !inputState.IsPointerActive && !inputState.MouseState.Active && isMouseRelative;
                    }
                    dynamicSettings.Latency = (int)(_frameData.Latency * 1000f);
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        Logger.Debug(ex, "ReadFrameData");
                    }
                    dataClient.Disconnect();
                    break;
                }
            }
        }
        finally
        {
            _frameData.FrameIndex = 0;
        }
    }

    private static void OnIsConnectedChanged(object sender, EventArgs e)
    {
        if (SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            Logger.Info("Headset connected: " + ConnectionManager.DescribeHeadset());
            SystemInfo.WakeUpDisplay();
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch
            {
            }
            _isRunning = true;
            _updateThread = new Thread(UpdateLoop) { Name = "Update Thread", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _updateThread.Start();
            SystemInfo.PreventSleep();
            SettingsBase<StreamerSettings>.Default.LastConnectDate = DateTime.UtcNow.Date;
        }
        else
        {
            Logger.Info("Headset disconnected");
            _isRunning = false;
            SystemInfo.ResumeSleep();
            try
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal;
            }
            catch
            {
            }
            if (SettingsBase<StreamerSettings>.Default.LockComputer)
            {
                SystemInfo.LockComputer();
            }
        }
    }

    private static void OnStreamingSourceChanged(object sender, StreamingSourceChangedEventArgs e)
    {
        if (InvokeRequired)
        {
            InvokeAsync(() => OnStreamingSourceChanged(e.OldValue));
        }
        else
        {
            OnStreamingSourceChanged(e.OldValue);
        }
    }

    private static void OnStreamingSourceChanged(StreamingSource old)
    {
        if (SettingsBase<DynamicSettings>.Default.IsConnected)
        {
            _desktopStreamer?.ReleaseStreams();
        }
    }
}
