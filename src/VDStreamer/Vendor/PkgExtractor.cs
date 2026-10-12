using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace VDStreamer.Vendor;

/// <summary>
/// Minimal reader for the official macOS installer (a XAR "flat package" whose component
/// Payload is a gzip-compressed cpio archive). Lets the streamer extract its vendor
/// assemblies on Linux without xar/pkgutil/bsdtar.
/// </summary>
internal sealed class PkgExtractor : IDisposable
{
    private sealed record XarEntry(string Path, string Type, long Offset, long Length, long Size, string Encoding);

    private readonly FileStream _stream;
    private readonly long _heapOffset;
    private readonly List<XarEntry> _entries = new();

    public PkgExtractor(string pkgPath)
    {
        _stream = File.OpenRead(pkgPath);
        Span<byte> header = stackalloc byte[28];
        _stream.ReadExactly(header);
        if (header[0] != 'x' || header[1] != 'a' || header[2] != 'r' || header[3] != '!')
        {
            throw new InvalidDataException("Not a XAR/.pkg file: " + pkgPath);
        }
        ushort headerSize = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4));
        long tocCompressed = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8));
        _stream.Position = headerSize;
        byte[] tocBytes = new byte[tocCompressed];
        _stream.ReadExactly(tocBytes);
        _heapOffset = headerSize + tocCompressed;

        string tocXml;
        using (var z = new ZLibStream(new MemoryStream(tocBytes), CompressionMode.Decompress))
        using (var reader = new StreamReader(z, Encoding.UTF8))
        {
            tocXml = reader.ReadToEnd();
        }
        XElement toc = XDocument.Parse(tocXml).Root?.Element("toc") ?? throw new InvalidDataException("XAR TOC missing");
        foreach (XElement file in toc.Elements("file"))
        {
            Walk(file, string.Empty);
        }
    }

    public IReadOnlyList<string> EntryPaths => _entries.Select(e => e.Path).ToList();

    private void Walk(XElement file, string parent)
    {
        string name = (string)file.Element("name") ?? string.Empty;
        string path = parent.Length == 0 ? name : parent + "/" + name;
        string type = (string)file.Element("type") ?? "file";
        XElement data = file.Element("data");
        if (data != null)
        {
            _entries.Add(new XarEntry(
                path,
                type,
                long.Parse((string)data.Element("offset") ?? "0"),
                long.Parse((string)data.Element("length") ?? "0"),
                long.Parse((string)data.Element("size") ?? "0"),
                (string)data.Element("encoding")?.Attribute("style") ?? "application/octet-stream"));
        }
        foreach (XElement child in file.Elements("file"))
        {
            Walk(child, path);
        }
    }

    /// <summary>Opens a decoded stream for one XAR member (e.g. "VirtualDesktopStreamer.pkg/Payload").</summary>
    public Stream OpenEntry(string path)
    {
        XarEntry entry = _entries.FirstOrDefault(e => e.Path == path) ?? throw new FileNotFoundException("Package member not found: " + path);
        var slice = new SubStream(_stream, _heapOffset + entry.Offset, entry.Length);
        return entry.Encoding switch
        {
            "application/x-gzip" => new ZLibStream(slice, CompressionMode.Decompress),
            "application/octet-stream" => slice,
            _ => throw new NotSupportedException("Unsupported XAR encoding " + entry.Encoding),
        };
    }

    /// <summary>
    /// Streams a gzip-compressed cpio payload and calls <paramref name="onFile"/> for each
    /// regular file. The callback receives the archive-relative path and a stream positioned on
    /// the file contents; it must consume or ignore the stream before returning.
    /// </summary>
    public static void ReadCpioPayload(Stream payload, Action<string, long, Stream> onFile)
    {
        Stream input = payload;
        var buffered = new BufferedStream(payload, 1 << 16);
        int b0 = buffered.ReadByte();
        int b1 = buffered.ReadByte();
        var prefixed = new PrefixStream(new byte[] { (byte)b0, (byte)b1 }, buffered);
        input = (b0 == 0x1f && b1 == 0x8b) ? new GZipStream(prefixed, CompressionMode.Decompress) : prefixed;

        byte[] magic = new byte[6];
        while (true)
        {
            if (!TryReadExactly(input, magic))
            {
                return;
            }
            string m = Encoding.ASCII.GetString(magic);
            long nameSize, fileSize;
            int mode;
            bool newc;
            if (m == "070707")
            {
                byte[] h = new byte[70];
                input.ReadExactly(h);
                string s = Encoding.ASCII.GetString(h);
                mode = Convert.ToInt32(s.Substring(12, 6), 8);
                nameSize = Convert.ToInt64(s.Substring(53, 6), 8);
                fileSize = Convert.ToInt64(s.Substring(59, 11), 8);
                newc = false;
            }
            else if (m == "070701" || m == "070702")
            {
                byte[] h = new byte[104];
                input.ReadExactly(h);
                string s = Encoding.ASCII.GetString(h);
                mode = Convert.ToInt32(s.Substring(8, 8), 16);
                fileSize = Convert.ToInt64(s.Substring(48, 8), 16);
                nameSize = Convert.ToInt64(s.Substring(88, 8), 16);
                newc = true;
            }
            else
            {
                throw new InvalidDataException("Unsupported cpio header '" + m + "'");
            }

            byte[] nameBytes = new byte[nameSize];
            input.ReadExactly(nameBytes);
            if (newc)
            {
                Skip(input, Pad4(110 + nameSize));
            }
            string name = Encoding.UTF8.GetString(nameBytes, 0, (int)Math.Max(0, nameSize - 1));
            if (name == "TRAILER!!!")
            {
                return;
            }
            if (name.StartsWith("./", StringComparison.Ordinal))
            {
                name = name.Substring(2);
            }

            bool isRegular = (mode & 0xF000) == 0x8000;
            var content = new SubStream(input, -1, fileSize);
            if (isRegular)
            {
                onFile(name, fileSize, content);
            }
            content.SkipRemaining();
            if (newc)
            {
                Skip(input, Pad4(fileSize));
            }
        }
    }

    private static long Pad4(long n) => (4 - (n % 4)) % 4;

    private static bool TryReadExactly(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }
                throw new EndOfStreamException();
            }
            read += n;
        }
        return true;
    }

    private static void Skip(Stream s, long count)
    {
        byte[] tmp = new byte[8192];
        while (count > 0)
        {
            int n = s.Read(tmp, 0, (int)Math.Min(tmp.Length, count));
            if (n == 0)
            {
                throw new EndOfStreamException();
            }
            count -= n;
        }
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>Read-only window over another stream (seekable base or forward-only).</summary>
    private sealed class SubStream : Stream
    {
        private readonly Stream _base;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubStream(Stream baseStream, long start, long length)
        {
            _base = baseStream;
            _start = start;
            _length = length;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }
            if (_start >= 0)
            {
                _base.Position = _start + _position;
            }
            int n = _base.Read(buffer, offset, (int)Math.Min(count, remaining));
            _position += n;
            return n;
        }

        public void SkipRemaining()
        {
            byte[] tmp = new byte[8192];
            while (Read(tmp, 0, tmp.Length) > 0)
            {
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Re-inserts already consumed bytes in front of a stream (used for magic sniffing).</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _prefixPos;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixPos < _prefix.Length)
            {
                int n = Math.Min(count, _prefix.Length - _prefixPos);
                Array.Copy(_prefix, _prefixPos, buffer, offset, n);
                _prefixPos += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
