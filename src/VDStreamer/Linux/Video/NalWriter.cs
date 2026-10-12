using System;
using System.Buffers.Binary;

namespace VDStreamer.Linux.Video;

/// <summary>
/// Rewrites an Annex B access unit into the framing the headset asked for: Annex B start codes or
/// 4-byte big-endian length prefixes (what x264 emits with b_annexb=0 on macOS). Access unit
/// delimiters and filler data are dropped.
/// </summary>
internal static class NalWriter
{
    public static int MaxOutputSize(int inputSize) => inputSize + 64;

    public static int Write(ReadOnlySpan<byte> input, Span<byte> output, bool annexB, bool hevc)
    {
        int written = 0;
        int pos = FindStartCode(input, 0, out int scLen);
        while (pos >= 0)
        {
            int nalStart = pos + scLen;
            int next = FindStartCode(input, nalStart, out int nextScLen);
            int nalEnd = next < 0 ? input.Length : next;
            // Trailing zero bytes before the next start code belong to the start code.
            while (nalEnd > nalStart && input[nalEnd - 1] == 0)
            {
                nalEnd--;
            }
            ReadOnlySpan<byte> nal = input.Slice(nalStart, nalEnd - nalStart);
            if (nal.Length > 0 && !IsDropped(nal[0], hevc))
            {
                if (annexB)
                {
                    output[written] = 0;
                    output[written + 1] = 0;
                    output[written + 2] = 0;
                    output[written + 3] = 1;
                }
                else
                {
                    BinaryPrimitives.WriteInt32BigEndian(output.Slice(written), nal.Length);
                }
                nal.CopyTo(output.Slice(written + 4));
                written += 4 + nal.Length;
            }
            pos = next;
            scLen = nextScLen;
        }
        return written;
    }

    public static bool ContainsKeyFrame(ReadOnlySpan<byte> annexB, bool hevc)
    {
        int pos = FindStartCode(annexB, 0, out int scLen);
        while (pos >= 0 && pos + scLen < annexB.Length)
        {
            byte header = annexB[pos + scLen];
            if (hevc ? ((header >> 1) & 0x3F) is >= 16 and <= 21 : (header & 0x1F) == 5)
            {
                return true;
            }
            pos = FindStartCode(annexB, pos + scLen, out scLen);
        }
        return false;
    }

    private static bool IsDropped(byte header, bool hevc)
    {
        if (hevc)
        {
            int type = (header >> 1) & 0x3F;
            return type == 35 || type == 38; // AUD, filler
        }
        int t = header & 0x1F;
        return t == 9 || t == 12; // AUD, filler
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int from, out int length)
    {
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    length = 3;
                    return i;
                }
                if (data[i + 2] == 0 && i + 3 < data.Length && data[i + 3] == 1)
                {
                    length = 4;
                    return i;
                }
            }
        }
        length = 0;
        return -1;
    }
}
