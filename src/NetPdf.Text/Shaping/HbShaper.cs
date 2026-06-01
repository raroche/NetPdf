// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using HarfBuzzSharp;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbDirection = HarfBuzzSharp.Direction;

namespace NetPdf.Text.Shaping;

/// <summary>
/// Wraps HarfBuzzSharp into a small, idiomatic .NET shaping API. Holds an
/// <see cref="HarfBuzzSharp.Blob"/> over the source font bytes, an
/// <see cref="HarfBuzzSharp.Face"/>, and a <see cref="HarfBuzzSharp.Font"/> at a fixed
/// size — call <see cref="Shape(System.ReadOnlySpan{char}, ShapingDirection, string, string)"/>
/// any number of times against this single shaper.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle.</b> The shaper owns three native handles (Blob/Face/Font) and one
/// pinned <see cref="GCHandle"/>. Constructor uses a partial-construction cleanup
/// pattern so a malformed font that fails after the byte array is pinned does not leak
/// the handle or any native resources. Callers must dispose the shaper. Per-shape
/// <see cref="HarfBuzzSharp.Buffer"/>s are constructed and disposed inside
/// <see cref="Shape(System.ReadOnlySpan{char}, ShapingDirection, string, string)"/>,
/// so concurrent shape calls against one shaper are safe — HarfBuzz
/// documents Face / Font as thread-safe for read-only use.
/// </para>
/// <para>
/// <b>Cluster semantics.</b> The buffer is configured with
/// <c>ClusterLevel.MonotoneCharacters</c> (HarfBuzz level 1) — the recommended setting
/// for new code per the official HarfBuzz "Working with HarfBuzz clusters" guide.
/// Cluster values in <see cref="ShapedGlyph"/> are UTF-16 code-unit indices into the
/// input text (HarfBuzz's native semantics for <c>hb_buffer_add_utf16</c>); see
/// <see cref="ShapedGlyph"/> remarks for the full contract.
/// </para>
/// <para>
/// <b>Scaling.</b> Output positions are returned in pixels at the requested font size.
/// Internally we set HarfBuzz's scale to <c>(unitsPerEm, unitsPerEm)</c> so HarfBuzz
/// returns positions in font units, then convert to pixels with
/// <c>units × fontSizePx / unitsPerEm</c>. This matches CSS px scaling and keeps the
/// HarfBuzz-side math integer-only. Optical sizing — <c>ppem</c>/<c>ptem</c> — comes in
/// Phase 4 when size-sensitive features land.
/// </para>
/// <para>
/// <b>Zero size.</b> A <c>fontSizePx</c> of <c>0</c> is permitted — CSS
/// Fonts 4 §3.4 allows <c>font-size</c> in <c>[0, ∞]</c>. Every advance / offset then
/// converts to <c>0</c> (zero-advance shaping), so a <c>font-size: 0</c> run shapes to
/// invisible, zero-width glyphs instead of snapping up to a default size. Only a
/// negative or non-finite size is rejected.
/// </para>
/// <para>
/// <b>Phase 1 scope</b> covers a single isolated text span with default OpenType
/// features. Three things are explicitly out of scope:
/// </para>
/// <list type="bullet">
///   <item>Custom features (small-caps, fractions, contextual alternates) — wait for Phase 2 CSS <c>font-feature-settings</c>.</item>
///   <item>Context-aware shaping (<c>item_offset</c> / <c>item_length</c> within a larger buffer) — required for Arabic joining and combining-mark behavior across run boundaries; Phase 3 itemizer adds the API.</item>
///   <item>Script itemization, bidi run segmentation, and fallback-font shaping — all Tasks 12+.</item>
/// </list>
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
        if (!double.IsFinite(fontSizePx) || fontSizePx < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fontSizePx), fontSizePx,
                "HbShaper: fontSizePx must be a non-negative finite number.");
        }

        _fontSizePx = fontSizePx;

        // Materialize a managed byte[] so HarfBuzz can hold a stable pointer through the
        // Blob's release callback. Cost: one extra allocation per shaper instance,
        // typically amortized over many Shape calls.
        var bytes = fontBytes.ToArray();
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

        Blob? blob = null;
        Face? face = null;
        Font? font = null;
        try
        {
            blob = new Blob(
                handle.AddrOfPinnedObject(),
                bytes.Length,
                MemoryMode.ReadOnly,
                () => handle.Free());

            face = new Face(blob, 0);
            var unitsPerEm = face.UnitsPerEm;
            if (unitsPerEm <= 0)
            {
                throw new InvalidDataException(
                    $"HbShaper: font face reports unitsPerEm = {unitsPerEm}; cannot shape against a degenerate em-square.");
            }
            _unitsPerEm = (ushort)unitsPerEm;

            font = new Font(face);
            font.SetScale(_unitsPerEm, _unitsPerEm);
            font.SetFunctionsOpenType();

            // Commit references — past this point the cleanup catch will not run.
            _blob = blob;
            _face = face;
            _font = font;
        }
        catch
        {
            // Reverse-order cleanup. Disposing Blob runs the release callback that frees
            // the pinned handle, but that only happens when the Blob exists; otherwise we
            // free the handle ourselves.
            font?.Dispose();
            face?.Dispose();
            if (blob is not null)
            {
                blob.Dispose();
            }
            else if (handle.IsAllocated)
            {
                handle.Free();
            }
            throw;
        }
    }

    /// <summary>
    /// Shape <paramref name="utf16Text"/> against the loaded font and return one
    /// <see cref="ShapedGlyph"/> per emitted glyph.
    /// </summary>
    /// <param name="utf16Text">The input text, as UTF-16 code units. Empty input returns an empty array.</param>
    /// <param name="direction">Writing direction of the run.</param>
    /// <param name="scriptIso15924">
    /// 4-letter ISO 15924 script tag (e.g. <c>"Latn"</c>, <c>"Arab"</c>, <c>"Hani"</c>).
    /// Required — the wrapper deliberately does not auto-guess because silent
    /// Latin-bias defaults break browser-fidelity shaping for non-Latin scripts.
    /// </param>
    /// <param name="language">
    /// BCP 47 language tag (e.g. <c>"en"</c>, <c>"ar"</c>, <c>"ja"</c>). Required for
    /// the same reason as <paramref name="scriptIso15924"/> — language-specific
    /// shaping (Turkish dotted/dotless i, Marathi vs Hindi conjuncts, etc.) needs an
    /// explicit declaration.
    /// </param>
    /// <remarks>
    /// Malformed UTF-16 (e.g. lone surrogates) is handled by HarfBuzz's
    /// replacement-character policy — invalid sequences shape as the font's
    /// <c>.notdef</c> glyph (or the glyph mapped from U+FFFD if cmap covers it).
    /// </remarks>
    public ShapedGlyph[] Shape(
        ReadOnlySpan<char> utf16Text,
        ShapingDirection direction,
        string scriptIso15924,
        string language)
        => Shape(utf16Text, itemOffset: 0, itemLength: utf16Text.Length,
            direction, scriptIso15924, language, cancellationToken: default);

    /// <summary>
    /// Per Phase 3 Task 9 cycle 2 review — full-buffer shaping with
    /// item-offset / item-length so cluster indices stay concat-buffer
    /// relative + cross-source-boundary contextual shaping (Arabic
    /// joining, complex-script reordering across <c>TextRun</c>
    /// boundaries) can use surrounding context.
    /// </summary>
    /// <param name="utf16Text">The full UTF-16 text. Cluster indices
    /// returned by HarfBuzz are offsets into this buffer (per
    /// <c>hb_buffer_add_utf16</c>'s contract).</param>
    /// <param name="itemOffset">Start of the shaping item in
    /// <paramref name="utf16Text"/>. HarfBuzz uses chars before
    /// <paramref name="itemOffset"/> as left context (for joining
    /// scripts that look back), without producing glyphs for them.</param>
    /// <param name="itemLength">Length of the shaping item in code
    /// units. HarfBuzz uses chars after <c>itemOffset+itemLength</c>
    /// as right context.</param>
    /// <param name="direction">Writing direction of the run.</param>
    /// <param name="scriptIso15924">4-letter ISO 15924 script tag
    /// (e.g. <c>"Latn"</c>, <c>"Arab"</c>, <c>"Hani"</c>). Required
    /// — the wrapper deliberately does not auto-guess because silent
    /// Latin-bias defaults break browser-fidelity shaping for non-Latin
    /// scripts.</param>
    /// <param name="language">BCP 47 language tag. Required for the
    /// same reason as <paramref name="scriptIso15924"/>.</param>
    /// <param name="cancellationToken">Honored before the HarfBuzz
    /// shape call (the native call itself is microseconds; cancellation
    /// is most useful when callers loop over many ItemizedRuns).</param>
    public ShapedGlyph[] Shape(
        ReadOnlySpan<char> utf16Text,
        int itemOffset,
        int itemLength,
        ShapingDirection direction,
        string scriptIso15924,
        string language,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(scriptIso15924))
        {
            throw new ArgumentException(
                "HbShaper: scriptIso15924 must be a non-empty 4-letter ISO 15924 tag (e.g. \"Latn\", \"Arab\").",
                nameof(scriptIso15924));
        }
        if (string.IsNullOrEmpty(language))
        {
            throw new ArgumentException(
                "HbShaper: language must be a non-empty BCP 47 tag (e.g. \"en\", \"ar\").",
                nameof(language));
        }
        if (itemOffset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemOffset),
                itemOffset, "HbShaper: itemOffset must be ≥ 0.");
        }
        if (itemLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemLength),
                itemLength, "HbShaper: itemLength must be ≥ 0.");
        }
        if ((long)itemOffset + itemLength > utf16Text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(itemLength),
                $"HbShaper: itemOffset({itemOffset}) + itemLength({itemLength}) exceeds buffer length({utf16Text.Length}).");
        }
        if (itemLength == 0 || utf16Text.IsEmpty)
        {
            return Array.Empty<ShapedGlyph>();
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var buffer = new HbBuffer
        {
            // HarfBuzz "Working with clusters" recommends MonotoneCharacters (level 1)
            // for new code without legacy compatibility constraints. Level 0 is the
            // historical Pango-compatible default.
            ClusterLevel = ClusterLevel.MonotoneCharacters,
        };
        // Pass the FULL buffer + (itemOffset, itemLength) so HarfBuzz
        // sees left/right context — required for cross-source-boundary
        // contextual shaping (Arabic joining across TextRuns, etc.) +
        // returned cluster indices are concat-buffer relative.
        buffer.AddUtf16(utf16Text, itemOffset, itemLength);
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
