using System;
using System.Linq;
using NLog;

namespace VDStreamer.Linux.Audio;

/// <summary>
/// PulseAudio / pipewire-pulse device management (pactl) creating the virtual devices
/// the macOS version ships as CoreAudio drivers:
///  * "VirtualDesktopSpeakers" null sink — PC audio is routed there while streaming and its
///    monitor is captured and sent to the headset;
///  * "VirtualDesktopMic" virtual source — headset microphone audio is played into it.
/// </summary>
internal static class Pulse
{
    private static readonly Logger Logger = LogManager.GetLogger("Pulse");

    public const string SpeakerSink = "VirtualDesktopSpeakers";
    public const string MicSink = "VirtualDesktopMicSink";
    public const string MicSource = "VirtualDesktopMic";

    private static readonly object _lock = new();
    private static string StateFile => System.IO.Path.Combine(Vendor.Paths.DataDir, "audio-restore.txt");
    private static string _savedDefaultSink;
    private static string _savedDefaultSource;
    private static string _speakerModule;
    private static string _micSinkModule;
    private static string _micSourceModule;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return Info() != null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Info() => SystemInfo.RunCapture("pactl", "info");

    public static string GetDefaultSink() => SystemInfo.RunCapture("pactl", "get-default-sink")?.Trim() ?? ParseInfo("Default Sink");

    public static string GetDefaultSource() => SystemInfo.RunCapture("pactl", "get-default-source")?.Trim() ?? ParseInfo("Default Source");

    private static string ParseInfo(string key)
    {
        string info = Info();
        string line = info?.Split('\n').FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
        return line?.Substring(key.Length + 1).Trim();
    }

    private static bool Exists(string kind, string name) =>
        SystemInfo.RunCapture("pactl", $"list short {kind}")?.Split('\n').Any(l => l.Split('\t').ElementAtOrDefault(1) == name) == true;

    private static string LoadModule(params string[] args)
    {
        string id = SystemInfo.RunCaptureArgs("pactl", 5000, new[] { "load-module" }.Concat(args).ToArray())?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            Logger.Warn("pactl load-module failed: " + string.Join(' ', args));
        }
        return id;
    }

    /// <summary>Creates the virtual speaker sink; when <paramref name="makeDefault"/> PC audio is redirected to it.</summary>
    public static string EnableSpeakers(bool makeDefault)
    {
        lock (_lock)
        {
            // Read the user's default first: loading a sink can make the server switch to it
            // (module-switch-on-connect, or replacing the auto_null placeholder).
            string previous = makeDefault ? GetDefaultSink() : null;
            if (!Exists("sinks", SpeakerSink))
            {
                _speakerModule = LoadModule("module-null-sink", "sink_name=" + SpeakerSink, "rate=48000", "channels=2", "sink_properties=\"device.description='Virtual Desktop Speakers'\"");
            }
            if (makeDefault)
            {
                if (!string.IsNullOrEmpty(previous) && previous != SpeakerSink && _savedDefaultSink == null)
                {
                    _savedDefaultSink = previous;
                    SaveState();
                }
                SystemInfo.TryRun("pactl", "set-default-sink", SpeakerSink);
                MoveSinkInputs(SpeakerSink);
                return SpeakerSink + ".monitor";
            }
            // Mirror whatever the user listens to.
            return "@DEFAULT_MONITOR@";
        }
    }

    public static void RestoreSpeakers()
    {
        lock (_lock)
        {
            if (_savedDefaultSink != null)
            {
                SystemInfo.TryRun("pactl", "set-default-sink", _savedDefaultSink);
                MoveSinkInputs(_savedDefaultSink);
                _savedDefaultSink = null;
            }
            if (_speakerModule != null)
            {
                SystemInfo.TryRun("pactl", "unload-module", _speakerModule);
                _speakerModule = null;
            }
            ClearStateIfIdle();
        }
    }

    private static void ClearStateIfIdle()
    {
        if (_savedDefaultSink == null && _savedDefaultSource == null)
        {
            try
            {
                System.IO.File.Delete(StateFile);
            }
            catch
            {
            }
        }
        else
        {
            SaveState();
        }
    }

    public static void EnableMicrophone(bool makeDefault)
    {
        lock (_lock)
        {
            string previous = makeDefault ? GetDefaultSource() : null;
            if (!Exists("sinks", MicSink))
            {
                _micSinkModule = LoadModule("module-null-sink", "sink_name=" + MicSink, "rate=48000", "channels=1", "sink_properties=\"device.description='Virtual Desktop Mic (internal)'\"");
            }
            if (!Exists("sources", MicSource))
            {
                _micSourceModule = LoadModule("module-remap-source", "master=" + MicSink + ".monitor", "source_name=" + MicSource, "channels=1", "source_properties=\"device.description='Virtual Desktop Microphone'\"");
            }
            if (makeDefault)
            {
                if (!string.IsNullOrEmpty(previous) && previous != MicSource && !previous.StartsWith("VirtualDesktop", StringComparison.Ordinal) && _savedDefaultSource == null)
                {
                    _savedDefaultSource = previous;
                    SaveState();
                }
                SystemInfo.TryRun("pactl", "set-default-source", MicSource);
            }
        }
    }

    public static void RestoreMicrophone()
    {
        lock (_lock)
        {
            if (_savedDefaultSource != null)
            {
                SystemInfo.TryRun("pactl", "set-default-source", _savedDefaultSource);
                _savedDefaultSource = null;
            }
            if (_micSourceModule != null)
            {
                SystemInfo.TryRun("pactl", "unload-module", _micSourceModule);
                _micSourceModule = null;
            }
            if (_micSinkModule != null)
            {
                SystemInfo.TryRun("pactl", "unload-module", _micSinkModule);
                _micSinkModule = null;
            }
            ClearStateIfIdle();
        }
    }

    private static void SaveState()
    {
        try
        {
            System.IO.File.WriteAllText(StateFile, (_savedDefaultSink ?? string.Empty) + "\n" + (_savedDefaultSource ?? string.Empty) + "\n");
        }
        catch
        {
        }
    }

    /// <summary>
    /// Undoes audio routing left behind by a crashed session (default sink/source pointing at the
    /// virtual devices) so the user does not lose sound.
    /// </summary>
    public static void RecoverFromPreviousSession()
    {
        try
        {
            if (!System.IO.File.Exists(StateFile))
            {
                return;
            }
            string[] lines = System.IO.File.ReadAllLines(StateFile);
            string sink = lines.ElementAtOrDefault(0);
            string source = lines.ElementAtOrDefault(1);
            if (!string.IsNullOrEmpty(sink) && GetDefaultSink() == SpeakerSink)
            {
                SystemInfo.TryRun("pactl", "set-default-sink", sink);
                MoveSinkInputs(sink);
            }
            if (!string.IsNullOrEmpty(source) && GetDefaultSource() == MicSource)
            {
                SystemInfo.TryRun("pactl", "set-default-source", source);
            }
            string modules = SystemInfo.RunCapture("pactl", "list short modules") ?? string.Empty;
            foreach (string line in modules.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains("sink_name=" + SpeakerSink) || line.Contains("sink_name=" + MicSink) || line.Contains("source_name=" + MicSource))
                {
                    SystemInfo.TryRun("pactl", "unload-module", line.Split('\t')[0]);
                }
            }
            System.IO.File.Delete(StateFile);
            Logger.Info("Restored audio devices left over from a previous session");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Audio recovery");
        }
    }

    private static void MoveSinkInputs(string sink)
    {
        string inputs = SystemInfo.RunCapture("pactl", "list short sink-inputs");
        if (inputs == null)
        {
            return;
        }
        foreach (string line in inputs.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string id = line.Split('\t')[0];
            SystemInfo.TryRun("pactl", "move-sink-input", id, sink);
        }
    }
}
