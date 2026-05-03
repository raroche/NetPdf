// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.InteropServices;

namespace NetPdf.Paint;

/// <summary>
/// 64-byte tagged-union value type. The discriminator <see cref="Kind"/> sits at offset 0;
/// per-kind payloads overlay at offset 8 via <see cref="FieldOffsetAttribute"/>.
/// Heavy or variable-length payloads (shaped glyph buffers, encoded image bytes, future
/// path point arrays) live in side tables on the owning <see cref="DisplayList"/> and are
/// referenced from these payloads by <see cref="int"/> index.
/// </summary>
/// <remarks>
/// <para>
/// Why 64 bytes: matches a typical x86_64 / arm64 cache line so a sequential walk over a
/// <see cref="DisplayList"/> is cache-friendly. <see cref="StructLayoutAttribute.Size"/>
/// pins the size so the layout doesn't drift if a payload struct grows.
/// </para>
/// <para>
/// Why factories instead of public payload fields: a tagged union is only sound if the
/// active payload matches <see cref="Kind"/>. Factories own the invariant by setting both
/// in lock-step on a fresh <c>default</c>; <c>As*</c> accessors verify it on read. The
/// kind discriminator is a private field exposed through a read-only <see cref="Kind"/>
/// property — external callers can observe it but cannot retag a constructed command,
/// which would leave the overlaid payload misinterpreted.
/// </para>
/// <para>
/// Factories also reject non-finite geometry (NaN, ±∞) so layout / paint bugs surface at
/// the IR boundary instead of much later when the PDF emitter tries to serialize garbage.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct DisplayCommand : IEquatable<DisplayCommand>
{
    [FieldOffset(0)] private DisplayCommandKind _kind;

    /// <summary>Active payload discriminator. Read-only — set exclusively by factory methods.</summary>
    public readonly DisplayCommandKind Kind => _kind;

    // All payloads share offset 8. Adding a new kind: define a payload struct that fits
    // in 56 bytes (64 - 8 for Kind/padding), declare it here at FieldOffset(8), add a
    // factory + As* accessor, and append the kind to DisplayCommandKind.

    [FieldOffset(8)] private RectFillPayload _rectFill;
    [FieldOffset(8)] private TextRunPayload _textRun;
    [FieldOffset(8)] private ImageDrawPayload _imageDraw;
    [FieldOffset(8)] private TransformPushPayload _transformPush;
    [FieldOffset(8)] private OpacityPushPayload _opacityPush;

    // ───── Factories ─────────────────────────────────────────────────────────

    public static DisplayCommand RectFill(double x, double y, double width, double height, RgbaColor color)
    {
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        EnsureFinite(width, nameof(width));
        EnsureFinite(height, nameof(height));

        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.RectFill;
        cmd._rectFill = new RectFillPayload
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Color = color,
        };
        return cmd;
    }

    public static DisplayCommand TextRun(int textRunIndex, double x, double y)
    {
        if (textRunIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textRunIndex), textRunIndex,
                "Text run index must be non-negative (it points into DisplayList.TextRuns).");
        }
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));

        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.TextRun;
        cmd._textRun = new TextRunPayload
        {
            TextRunIndex = textRunIndex,
            X = x,
            Y = y,
        };
        return cmd;
    }

    public static DisplayCommand ImageDraw(int imageIndex, double x, double y, double width, double height)
    {
        if (imageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(imageIndex), imageIndex,
                "Image index must be non-negative (it points into DisplayList.Images).");
        }
        EnsureFinite(x, nameof(x));
        EnsureFinite(y, nameof(y));
        EnsureFinite(width, nameof(width));
        EnsureFinite(height, nameof(height));

        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.ImageDraw;
        cmd._imageDraw = new ImageDrawPayload
        {
            ImageIndex = imageIndex,
            X = x,
            Y = y,
            Width = width,
            Height = height,
        };
        return cmd;
    }

    public static DisplayCommand TransformPush(double a, double b, double c, double d, double e, double f)
    {
        EnsureFinite(a, nameof(a));
        EnsureFinite(b, nameof(b));
        EnsureFinite(c, nameof(c));
        EnsureFinite(d, nameof(d));
        EnsureFinite(e, nameof(e));
        EnsureFinite(f, nameof(f));

        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.TransformPush;
        cmd._transformPush = new TransformPushPayload
        {
            A = a, B = b, C = c, D = d, E = e, F = f,
        };
        return cmd;
    }

    public static DisplayCommand TransformPop()
    {
        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.TransformPop;
        return cmd;
    }

    public static DisplayCommand OpacityPush(double alpha)
    {
        if (double.IsNaN(alpha) || alpha < 0 || alpha > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alpha), alpha,
                "Alpha must be a finite number in [0, 1].");
        }
        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.OpacityPush;
        cmd._opacityPush = new OpacityPushPayload { Alpha = alpha };
        return cmd;
    }

    public static DisplayCommand OpacityPop()
    {
        DisplayCommand cmd = default;
        cmd._kind = DisplayCommandKind.OpacityPop;
        return cmd;
    }

    private static void EnsureFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                "Value must be a finite number (no NaN, no ±∞). The IR boundary rejects non-finite " +
                "geometry so producer bugs surface here instead of inside the PDF emitter.");
        }
    }

    // ───── Read accessors with kind verification ─────────────────────────────

    public readonly RectFillPayload AsRectFill()
        => Kind == DisplayCommandKind.RectFill ? _rectFill : throw KindMismatch(nameof(RectFill));

    public readonly TextRunPayload AsTextRun()
        => Kind == DisplayCommandKind.TextRun ? _textRun : throw KindMismatch(nameof(TextRun));

    public readonly ImageDrawPayload AsImageDraw()
        => Kind == DisplayCommandKind.ImageDraw ? _imageDraw : throw KindMismatch(nameof(ImageDraw));

    public readonly TransformPushPayload AsTransformPush()
        => Kind == DisplayCommandKind.TransformPush ? _transformPush : throw KindMismatch(nameof(TransformPush));

    public readonly OpacityPushPayload AsOpacityPush()
        => Kind == DisplayCommandKind.OpacityPush ? _opacityPush : throw KindMismatch(nameof(OpacityPush));

    private readonly InvalidOperationException KindMismatch(string expected)
        => new($"DisplayCommand kind is {Kind}; expected {expected}.");

    // ───── Equality (bitwise — payload bytes outside the active variant are zeroed by default-init) ─────

    public readonly bool Equals(DisplayCommand other)
    {
        var self = MemoryMarshal.AsBytes(new ReadOnlySpan<DisplayCommand>(in this));
        var that = MemoryMarshal.AsBytes(new ReadOnlySpan<DisplayCommand>(in other));
        return self.SequenceEqual(that);
    }

    public override readonly bool Equals(object? obj) => obj is DisplayCommand other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<DisplayCommand>(in this)));
        return hash.ToHashCode();
    }

    public static bool operator ==(DisplayCommand left, DisplayCommand right) => left.Equals(right);
    public static bool operator !=(DisplayCommand left, DisplayCommand right) => !left.Equals(right);

    public override readonly string ToString() => Kind switch
    {
        DisplayCommandKind.None => "None",
        DisplayCommandKind.RectFill => $"RectFill({_rectFill.X}, {_rectFill.Y}, {_rectFill.Width}×{_rectFill.Height}, {_rectFill.Color})",
        DisplayCommandKind.TextRun => $"TextRun(#{_textRun.TextRunIndex} @ {_textRun.X}, {_textRun.Y})",
        DisplayCommandKind.ImageDraw => $"ImageDraw(#{_imageDraw.ImageIndex} @ {_imageDraw.X}, {_imageDraw.Y}, {_imageDraw.Width}×{_imageDraw.Height})",
        DisplayCommandKind.TransformPush => $"TransformPush({_transformPush.A}, {_transformPush.B}, {_transformPush.C}, {_transformPush.D}, {_transformPush.E}, {_transformPush.F})",
        DisplayCommandKind.TransformPop => "TransformPop",
        DisplayCommandKind.OpacityPush => $"OpacityPush({_opacityPush.Alpha})",
        DisplayCommandKind.OpacityPop => "OpacityPop",
        _ => $"Unknown(kind={(byte)Kind})",
    };
}

/// <summary>Phase 1. Solid-color rectangle. 36 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct RectFillPayload
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public RgbaColor Color { get; init; }
}

/// <summary>Phase 1. References a <see cref="TextRun"/> in the side table at <see cref="TextRunIndex"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct TextRunPayload
{
    public int TextRunIndex { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>Phase 1. References a <see cref="RasterImage"/> in the side table at <see cref="ImageIndex"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct ImageDrawPayload
{
    public int ImageIndex { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

/// <summary>Phase 1. Affine matrix concatenation per the PDF <c>cm</c> operator (§8.4.4).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct TransformPushPayload
{
    public double A { get; init; }
    public double B { get; init; }
    public double C { get; init; }
    public double D { get; init; }
    public double E { get; init; }
    public double F { get; init; }
}

/// <summary>Phase 1. Combined fill + stroke alpha (CSS <c>opacity</c>). Validated to [0, 1] in the factory.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal readonly struct OpacityPushPayload
{
    public double Alpha { get; init; }
}
