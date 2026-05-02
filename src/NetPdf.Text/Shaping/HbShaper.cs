// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using HarfBuzzSharp;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbDirection = HarfBuzzSharp.Direction;

namespace NetPdf.Text.Shaping;

/// <summary>
/// Wraps HarfBuzzSharp into a small, idiomatic .NET shaping API. Holds an
/// <see cref="HarfBuzzSharp.Blob"/> over the source font bytes, an
/// <see cref="HarfBuzzSharp.Face"/>, and a <see cref="HarfBuzzSharp.Font"/> at a fixed
/// size — call <see cref="Shape"/> any number of times against this single shaper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The shaper owns three native handles (Blob/Face/Font); callers must
/// dispose it. Per-shape <see cref="HarfBuzzSharp.Buffer"/>s are constructed and disposed
/// inside <see cref="Shape"/>, so concurrent shape calls against one shaper are safe —
/// HarfBuzz documents Face / Font as thread-safe for read-only use.
/// </para>
/// <para>
/// <b>Scaling.</b> Output positions are returned in pixels at the requested font size.
/// Internally we set HarfBuzz's scale to <c>(unitsPerEm, unitsPerEm)</c> so HarfBuzz
/// returns positions in font units, then convert to pixels with
/// <c>units × fontSizePx / unitsPerEm</c>. This matches CSS px scaling and keeps the
/// HarfBuzz-side math integer-only.
/// </para>
/// <para>
/// <b>Phase 1 scope.</b> Default OpenType features only (the script-default set HarfBuzz
/// applies when no feature list is supplied). Custom features (small-caps, fractions,
/// etc.) land alongside CSS <c>font-feature-settings</c> in Phase 2.
/// </para>
/// </remarks>
internal sealed class HbShaper : IDisposable
{
    private readonly Blob _blob;
    private readonly Face _face;
    private readonly Font _font;
    private readonly ushort _unitsPerEm;
    private readonly double _fontSizePx;
    private bool _disposed;

    public HbShaper(ReadOnlyMemory<byte> fontBytes, double fontSizePx)
    {
        if (fontBytes.IsEmpty)
        {
            throw new ArgumentException("HbShaper: font bytes must not be empty.", nameof(fontBytes));
        }
        if (!double.IsFinite(fontSizePx) || fontSizePx <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fontSizePx), fontSizePx,
                "HbShaper: fontSizePx must be a positive finite number.");
        }

        _fontSizePx = fontSizePx;

        // Materialize a managed byte[] so HarfBuzz can hold a stable pointer through the
        // Blob's release callback. The cost is one extra allocation per shaper instance,
        // typically amortized over many Shape calls.
        var bytes = fontBytes.ToArray();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        _blob = new Blob(
            handle.AddrOfPinnedObject(),
            bytes.Length,
            MemoryMode.ReadOnly,
            () => handle.Free());

        _face = new Face(_blob, 0);
        _unitsPerEm = (ushort)_face.UnitsPerEm;
        _font = new Font(_face);

        // Tell HarfBuzz to return positions in font units. The conversion to pixels
        // happens on the way out of Shape().
        _font.SetScale(_unitsPerEm, _unitsPerEm);
        _font.SetFunctionsOpenType();
    }

    public ShapedGlyph[] Shape(
        ReadOnlySpan<char> utf16Text,
        ShapingDirection direction = ShapingDirection.LeftToRight,
        string scriptIso15924 = "Latn",
        string language = "en")
    {
        ThrowIfDisposed();
        if (utf16Text.IsEmpty)
        {
            return Array.Empty<ShapedGlyph>();
        }

        using var buffer = new HbBuffer();
        buffer.AddUtf16(utf16Text);
        buffer.Direction = ConvertDirection(direction);
        buffer.Script = Script.Parse(scriptIso15924);
        buffer.Language = new Language(language);

        // Default features — HarfBuzz applies the script-default set when we pass no list.
        _font.Shape(buffer);

        var infos = buffer.GetGlyphInfoSpan();
        var positions = buffer.GetGlyphPositionSpan();
        if (infos.Length != positions.Length)
        {
            throw new InvalidOperationException(
                $"HarfBuzz returned mismatched info/position counts ({infos.Length} vs {positions.Length}).");
        }

        var pxPerUnit = _fontSizePx / _unitsPerEm;
        var output = new ShapedGlyph[infos.Length];
        for (var i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            var pos = positions[i];
            output[i] = new ShapedGlyph(
                GlyphId: (ushort)info.Codepoint,
                XAdvance: (float)(pos.XAdvance * pxPerUnit),
                YAdvance: (float)(pos.YAdvance * pxPerUnit),
                XOffset: (float)(pos.XOffset * pxPerUnit),
                YOffset: (float)(pos.YOffset * pxPerUnit),
                Cluster: (int)info.Cluster);
        }
        return output;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _font.Dispose();
        _face.Dispose();
        _blob.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HbShaper));
        }
    }

    private static HbDirection ConvertDirection(ShapingDirection direction) => direction switch
    {
        ShapingDirection.LeftToRight => HbDirection.LeftToRight,
        ShapingDirection.RightToLeft => HbDirection.RightToLeft,
        ShapingDirection.TopToBottom => HbDirection.TopToBottom,
        ShapingDirection.BottomToTop => HbDirection.BottomToTop,
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown ShapingDirection."),
    };
}
