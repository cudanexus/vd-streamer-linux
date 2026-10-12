using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VDStreamer.Linux.Video;

/// <summary>A concrete GStreamer encoder configuration.</summary>
internal sealed class EncoderChoice
{
    public string Id { get; init; }                 // nvenc | va | vaapi | x264 | openh264 | x265
    public string DisplayName { get; init; }
    public bool Hevc { get; init; }
    public bool Hardware { get; init; }
    public string Element { get; init; }
    /// <summary>Converter chain producing encoder input: {0}=width {1}=height {2}=colorimetry caps suffix.</summary>
    public string Converter { get; init; }
    /// <summary>Encoder properties: {0}=bitrate (unit per encoder) {1}=keyframe interval in frames.</summary>
    public string Properties { get; init; }
    /// <summary>Divider applied to bits/s to obtain the encoder's bitrate unit (1000 = kbit/s).</summary>
    public int BitrateDivider { get; init; } = 1000;
    public bool SupportsFullRange { get; init; }

    public string FormatBitrate(int bitsPerSecond) => Math.Max(1, bitsPerSecond / BitrateDivider).ToString(CultureInfo.InvariantCulture);

    public override string ToString() => DisplayName;
}

/// <summary>Detects which hardware/software encoders GStreamer provides and builds pipelines.</summary>
internal static class EncoderCatalog
{
    // Encoders that failed at runtime during this process lifetime (e.g. nvh264enc without an NVIDIA GPU).
    private static readonly ConcurrentDictionary<string, string> _broken = new();

    // Scale before converting (cheaper when the headset gets a smaller picture than the monitor) and
    // let both run on several cores; GStreamer versions without n-threads ignore it with a warning.
    private static readonly string Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(CultureInfo.InvariantCulture);
    private static readonly string SoftwareConverter = $"videoscale n-threads={Threads} ! videoconvert n-threads={Threads} dither=none ! video/x-raw,format=I420,width={{0}},height={{1}}{{2}}";

    private static IEnumerable<EncoderChoice> Candidates(bool hevc)
    {
        if (!hevc)
        {
            foreach (string element in new[] { "nvh264enc", "nvcudah264enc" })
            {
                yield return Nvenc(element, false);
            }
            foreach (string element in new[] { "vah264enc", "vah264lpenc" })
            {
                yield return Va(element, false);
            }
            yield return Vaapi("vaapih264enc", false);
            yield return new EncoderChoice
            {
                Id = "x264",
                DisplayName = "x264 (software)",
                Element = "x264enc",
                Converter = SoftwareConverter,
                Properties = "tune=zerolatency speed-preset=ultrafast bitrate={0} vbv-buf-capacity=1000 key-int-max={1} bframes=0 b-adapt=false aud=false byte-stream=true threads=0 sliced-threads=true",
                SupportsFullRange = true,
            };
            yield return new EncoderChoice
            {
                Id = "openh264",
                DisplayName = "OpenH264 (software)",
                Element = "openh264enc",
                Converter = SoftwareConverter,
                Properties = "bitrate={0} rate-control=bitrate complexity=low gop-size={1} usage-type=screen",
                BitrateDivider = 1,
                SupportsFullRange = false,
            };
        }
        else
        {
            foreach (string element in new[] { "nvh265enc", "nvcudah265enc" })
            {
                yield return Nvenc(element, true);
            }
            foreach (string element in new[] { "vah265enc", "vah265lpenc" })
            {
                yield return Va(element, true);
            }
            yield return Vaapi("vaapih265enc", true);
            yield return new EncoderChoice
            {
                Id = "x265",
                DisplayName = "x265 (software HEVC)",
                Hevc = true,
                Element = "x265enc",
                Converter = SoftwareConverter,
                Properties = "tune=zerolatency speed-preset=ultrafast bitrate={0} key-int-max={1}",
                SupportsFullRange = false,
            };
        }
    }

    private static EncoderChoice Nvenc(string element, bool hevc) => new()
    {
        Id = "nvenc",
        DisplayName = (hevc ? "NVIDIA NVENC HEVC" : "NVIDIA NVENC H.264") + $" ({element})",
        Hevc = hevc,
        Hardware = true,
        Element = element,
        Converter = Gst.HasElement("cudaconvertscale") && Gst.HasElement("cudaupload")
            ? "cudaupload ! cudaconvertscale ! video/x-raw(memory:CUDAMemory),format=NV12,width={0},height={1}"
            : $"videoscale n-threads={Threads} ! videoconvert n-threads={Threads} dither=none ! video/x-raw,format=NV12,width={{0}},height={{1}}",
        Properties = element.StartsWith("nvcuda", StringComparison.Ordinal)
            ? "preset=p1 tune=ultra-low-latency rate-control=cbr bitrate={0} gop-size={1} b-frames=0 zero-reorder-delay=true repeat-sequence-header=true"
            : "preset=low-latency-hp rc-mode=cbr bitrate={0} gop-size={1} zerolatency=true bframes=0 aud=false",
    };

    private static EncoderChoice Va(string element, bool hevc) => new()
    {
        Id = "va",
        DisplayName = (hevc ? "VA-API HEVC" : "VA-API H.264") + $" ({element})",
        Hevc = hevc,
        Hardware = true,
        Element = element,
        Converter = "vapostproc ! video/x-raw(memory:VAMemory),format=NV12,width={0},height={1}",
        Properties = "rate-control=cbr bitrate={0} key-int-max={1} b-frames=0 target-usage=7",
    };

    private static EncoderChoice Vaapi(string element, bool hevc) => new()
    {
        Id = "vaapi",
        DisplayName = (hevc ? "VA-API HEVC" : "VA-API H.264") + $" ({element}, legacy)",
        Hevc = hevc,
        Hardware = true,
        Element = element,
        Converter = "vaapipostproc ! video/x-raw(memory:VASurface),format=NV12,width={0},height={1}",
        Properties = "rate-control=cbr bitrate={0} keyframe-period={1} max-bframes=0 quality-level=7",
    };

    public static IReadOnlyList<EncoderChoice> Available(bool hevc) =>
        Candidates(hevc).Where(c => Gst.HasElement(c.Element) && (c.Converter.StartsWith("video", StringComparison.Ordinal) || Gst.HasElement(c.Converter.Split(' ')[0]))).ToList();

    public static IReadOnlyList<EncoderChoice> All() => Available(false).Concat(Available(true)).ToList();

    /// <summary>
    /// Picks an encoder. <paramref name="preferred"/> is "auto" or an encoder id; when HEVC is
    /// requested but unavailable we fall back to H.264.
    /// </summary>
    public static EncoderChoice Select(string preferred, bool wantHevc)
    {
        IEnumerable<EncoderChoice> pool = (wantHevc ? Available(true) : Enumerable.Empty<EncoderChoice>()).Concat(Available(false));
        List<EncoderChoice> usable = pool.Where(c => !_broken.ContainsKey(c.Element)).ToList();
        if (!string.IsNullOrEmpty(preferred) && preferred != "auto")
        {
            EncoderChoice match = usable.FirstOrDefault(c => c.Id == preferred || c.Element == preferred);
            if (match != null)
            {
                return match;
            }
        }
        return usable.FirstOrDefault();
    }

    public static void MarkBroken(EncoderChoice choice, string reason) => _broken[choice.Element] = reason;

    public static IReadOnlyDictionary<string, string> Broken => _broken;

    public static string BuildPipeline(EncoderChoice e, string source, int width, int height, int framerate, int bitsPerSecond, bool fullRange)
    {
        string colorimetry = e.SupportsFullRange ? (fullRange ? ",colorimetry=1:3:5:1" : ",colorimetry=bt709") : string.Empty;
        string converter = string.Format(CultureInfo.InvariantCulture, e.Converter, width, height, colorimetry);
        int keyInterval = Math.Max(30, framerate * 4);
        string props = string.Format(CultureInfo.InvariantCulture, e.Properties, e.FormatBitrate(bitsPerSecond), keyInterval);
        string parser = e.Hevc
            ? "h265parse config-interval=-1 ! video/x-h265,stream-format=byte-stream,alignment=au"
            : "h264parse config-interval=-1 ! video/x-h264,stream-format=byte-stream,alignment=au";
        return $"{source} ! queue name=q max-size-buffers=2 max-size-bytes=0 max-size-time=0 leaky=downstream ! {converter} ! {e.Element} name=enc {props} ! {parser} ! appsink name=sink sync=false max-buffers=1 drop=false enable-last-sample=false";
    }
}
