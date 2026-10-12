using Xenko.Core.Mathematics;

namespace VDStreamer.Linux;

public enum CaptureKind
{
    X11,
    PipeWire,
}

/// <summary>One capturable display.</summary>
public sealed class MonitorInfo
{
    public MonitorInfo(string id, string name, Rectangle bounds, int refreshRate, bool isPrimary, CaptureKind kind)
    {
        Id = id;
        Name = name;
        Bounds = bounds;
        DisplayFrequency = refreshRate;
        IsPrimary = isPrimary;
        Kind = kind;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>Position/size in the global desktop coordinate space (same space as input injection).</summary>
    public Rectangle Bounds { get; }

    public int DisplayFrequency { get; }

    public bool IsPrimary { get; }

    public CaptureKind Kind { get; }

    /// <summary>PipeWire node id when captured through the ScreenCast portal.</summary>
    public uint PipeWireNode { get; init; }

    public bool IsPortrait => Bounds.Height > Bounds.Width;

    public override string ToString() => $"{Name} {Bounds.Width}x{Bounds.Height}+{Bounds.X}+{Bounds.Y}" + (DisplayFrequency > 0 ? $"@{DisplayFrequency}" : string.Empty);
}
