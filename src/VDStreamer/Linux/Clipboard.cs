using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using VDStreamer.Streamer;
using VirtualDesktop.Core;
using VirtualDesktop.Mobile;
using VirtualDesktop.Net;
using Xenko.VR;

namespace VDStreamer.Linux;

/// <summary>
/// Text clipboard sharing with the headset (wl-clipboard on Wayland, xclip/xsel on X11).
/// Headset → PC: ClipboardChanged(Text) → ClipboardRequestText → ClipboardText (UTF-16).
/// PC → headset (Apple Vision only, like macOS): ClipboardText when the PC clipboard changes.
/// </summary>
internal static class Clipboard
{
    private const byte ContentTypeText = 0;
    private static readonly Logger Logger = LogManager.GetLogger("Clipboard");
    private static Timer _timer;
    private static string _lastText;
    private static volatile bool _awaitingHeadsetText;

    public static event EventHandler Changed;

    public static string Text => Read();

    public static void Initialize()
    {
        ConnectionManager.MessagingClient.Message += OnMessage;
        _lastText = Read();
        _timer = new Timer(_ => Poll(), null, 1000, 1000);
    }

    public static void Shutdown() => _timer?.Dispose();

    private static void Poll()
    {
        try
        {
            if (!SettingsBase<DynamicSettings>.Default.IsConnected)
            {
                return;
            }
            string text = Read();
            if (text == null || text == _lastText)
            {
                return;
            }
            _lastText = text;
            Changed?.Invoke(null, EventArgs.Empty);
            if (HmdResolutionTypeExtensions.IsMauiDevice(SettingsBase<SharedMobileSettings>.Default.HmdType) && text.Length > 0)
            {
                SendText(text);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Clipboard poll");
        }
    }

    private static void OnMessage(object sender, NetMessage e)
    {
        switch (e.MessageType)
        {
            case MessageType.ClipboardChanged:
                byte type = e.Reader.ReadByte();
                if (type == ContentTypeText)
                {
                    _awaitingHeadsetText = true;
                    NetMessage.CreateOutgoing(MessageType.ClipboardRequestText).SendTo(ConnectionManager.MessagingClient);
                }
                else
                {
                    Logger.Info("Headset shared non-text clipboard content (not supported on Linux yet)");
                }
                break;
            case MessageType.ClipboardText:
                if (_awaitingHeadsetText)
                {
                    _awaitingHeadsetText = false;
                    string text = Encoding.Unicode.GetString(e.GetReadSpan());
                    Task.Run(() =>
                    {
                        if (Write(text))
                        {
                            _lastText = text;
                            NetMessage.CreateOutgoing(MessageType.ClipboardCopied).SendTo(ConnectionManager.MessagingClient);
                        }
                    });
                }
                break;
        }
    }

    private static unsafe void SendText(string text)
    {
        int byteCount = Encoding.Unicode.GetByteCount(text);
        NetMessage message = NetMessage.CreateOutgoing(MessageType.ClipboardText, byteCount);
        Span<byte> span = message.GetWriteSpan();
        int written = Encoding.Unicode.GetBytes(text, span);
        message.Length += written;
        message.SendTo(ConnectionManager.MessagingClient);
    }

    public static string Read()
    {
        if (SystemInfo.IsWayland && SystemInfo.CommandExists("wl-paste"))
        {
            return SystemInfo.RunCaptureArgs("wl-paste", 2000, "--no-newline", "--type", "text/plain");
        }
        if (SystemInfo.HasX11 && SystemInfo.CommandExists("xclip"))
        {
            return SystemInfo.RunCaptureArgs("xclip", 2000, "-selection", "clipboard", "-o");
        }
        if (SystemInfo.HasX11 && SystemInfo.CommandExists("xsel"))
        {
            return SystemInfo.RunCaptureArgs("xsel", 2000, "--clipboard", "--output");
        }
        return null;
    }

    public static bool Write(string text)
    {
        string tool;
        string[] args;
        if (SystemInfo.IsWayland && SystemInfo.CommandExists("wl-copy"))
        {
            tool = "wl-copy";
            args = new[] { "--type", "text/plain;charset=utf-8" };
        }
        else if (SystemInfo.HasX11 && SystemInfo.CommandExists("xclip"))
        {
            tool = "xclip";
            args = new[] { "-selection", "clipboard", "-i" };
        }
        else if (SystemInfo.HasX11 && SystemInfo.CommandExists("xsel"))
        {
            tool = "xsel";
            args = new[] { "--clipboard", "--input" };
        }
        else
        {
            Logger.Warn("No clipboard tool found (install wl-clipboard or xclip)");
            return false;
        }
        try
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (string a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using Process p = Process.Start(psi);
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            // wl-copy/xclip fork to serve the selection; do not wait for the server process.
            p.WaitForExit(2000);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Setting clipboard failed");
            return false;
        }
    }
}
