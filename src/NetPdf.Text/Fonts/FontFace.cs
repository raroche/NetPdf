// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Text.Fonts;

/// <summary>
/// A parsed font face usable for shaping, metrics, embedding, and subsetting. Wraps an
/// <see cref="OpenTypeFont"/> with extracted <see cref="FontMetadata"/>, a per-face
/// "used-glyph" bitmap that the subsetter consumes, and a stable <see cref="Source"/>
/// identifier (file path or URI).
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> The wrapped <see cref="OpenTypeFont"/> is immutable and safe for
/// concurrent reads (per its own contract). The used-glyph bitmap is mutated by
/// <see cref="MarkGlyphUsed(int)"/> under a lock so multiple page renderers running in
/// parallel can mark glyphs without races. Subset emission is single-threaded.
/// </para>
/// <para>
/// <b>Disposal contract.</b> <see cref="Dispose"/> is real, not advisory. Once disposed,
/// every mutating or enumerating method (<see cref="MarkGlyphUsed(int)"/>,
/// <see cref="IsGlyphUsed(int)"/>, <see cref="GetUsedGlyphIds"/>) throws
/// <see cref="ObjectDisposedException"/>. The pure metadata properties
/// (<see cref="Font"/>, <see cref="Metadata"/>, <see cref="Source"/>,
/// <see cref="GlyphCount"/>) remain readable so logging / diagnostics paths can still
/// describe the face after disposal. Calls are idempotent: <c>Dispose</c> may be
/// invoked any number of times. A future HarfBuzz handle (Task 11 integration) plugs
/// into the same <see cref="Dispose"/> path; the contract here is locked in now so
/// callers cannot accidentally rely on post-dispose mutation working.
/// </para>
/// </remarks>
internal sealed class FontFace : IDisposable
{
    private readonly object _glyphLock = new();
    private readonly BitArray _usedGlyphs;
    private bool _disposed;

    /// <summary>The parsed underlying OpenType / TrueType font.</summary>
    public OpenTypeFont Font { get; }

    /// <summary>Identifying metadata extracted at construction (family, weight, italic, …).</summary>
    public FontMetadata Metadata { get; }

    /// <summary>
    /// Stable identifier for the face — typically a file path for system fonts or a URI for
    /// remote-resolved fonts. Used as the cache key for <c>FontCache</c>.
    /// </summary>
    public string Source { get; }

    /// <summary>Total number of glyphs in the underlying font (from <c>maxp.numGlyphs</c>).</summary>
    public int GlyphCount => Font.Maxp.NumGlyphs;

    public FontFace(OpenTypeFont font, FontMetadata metadata, string source)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(source);
        Font = font;
        Metadata = metadata;
        Source = source;
        _usedGlyphs = new BitArray(font.Maxp.NumGlyphs);
        // The notdef glyph (id 0) is implicitly always used in a subset per OpenType spec.
        if (font.Maxp.NumGlyphs > 0) _usedGlyphs[0] = true;
    }

    /// <summary>
    /// Build a face by parsing <paramref name="fontBytes"/> and extracting metadata.
    /// Convenience for callers that have raw bytes (system-font enumeration, custom resolvers).
    /// </summary>
    public static FontFace Load(ReadOnlyMemory<byte> fontBytes, string source)
    {
        // Per Phase C C-2 — pre-decode safety gate. Catches non-fonts (e.g.,
        // a binary blob renamed to .ttf), oversized inputs, and sfnt headers
        // that declare millions of tables before OpenTypeFont.Parse + HarfBuzz
        // run on the bytes.
        var verdict = FontSafetyValidator.Validate(fontBytes.Span);
        if (!verdict.IsSafe)
        {
            throw new InvalidOperationException(
                $"Font '{source}' rejected by pre-decode safety validator: {verdict.Reason}");
        }
        // Per PR #17 review user-recommendation #5 + Copilot #3 — WOFF /
        // WOFF2 carry a wrapped sfnt that needs format-specific decoding
        // (Brotli for WOFF2, zlib for WOFF) before OpenTypeFont.Parse can
        // walk the table directory. The validator currently only sniffs
        // the wrapped magic; OpenTypeFont.Parse on wrapped bytes would
        // misread the directory. Phase 5's @font-face loader will own
        // WOFF/WOFF2 decoding; until then, FontFace.Load rejects wrapped
        // formats explicitly so the contract matches reality.
        if (verdict.DetectedFormat is FontSafetyValidator.FontFormat.Woff
            or FontSafetyValidator.FontFormat.Woff2)
        {
            throw new InvalidOperationException(
                $"Font '{source}' is in {verdict.DetectedFormat} format; NetPdf v1 cannot decode the wrapped sfnt — pass the unwrapped TTF/OTF.");
        }
        var font = OpenTypeFont.Parse(fontBytes);
        var metadata = FontMetadata.Extract(font);
        return new FontFace(font, metadata, source);
    }

    /// <summary>Mark <paramref name="glyphId"/> as used so the subsetter retains it.</summary>
    /// <exception cref="ObjectDisposedException">Thrown when called after <see cref="Dispose"/>.</exception>
    public void MarkGlyphUsed(int glyphId)
    {
        if ((uint)glyphId >= (uint)_usedGlyphs.Length) return;
        lock (_glyphLock)
        {
            // Re-check disposal INSIDE the lock to close the race against a concurrent
            // Dispose. Without this, a thread could pass an outside-the-lock check, then
            // a parallel Dispose runs, then this thread mutates the bitmap of a disposed
            // face — silently corrupting state that may have backing native resources.
            ObjectDisposedException.ThrowIf(_disposed, this);
            _usedGlyphs[glyphId] = true;
        }
    }

    /// <summary>True when <paramref name="glyphId"/> has been marked as used (or is the .notdef glyph).</summary>
    /// <exception cref="ObjectDisposedException">Thrown when called after <see cref="Dispose"/>.</exception>
    public bool IsGlyphUsed(int glyphId)
    {
        if ((uint)glyphId >= (uint)_usedGlyphs.Length) return false;
        lock (_glyphLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _usedGlyphs[glyphId];
        }
    }

    /// <summary>
    /// Snapshot of the used-glyph bitmap as a sorted ascending array of glyph IDs. Allocates;
    /// intended for the subsetter, not the per-character marking hot path.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when called after <see cref="Dispose"/>.</exception>
    public int[] GetUsedGlyphIds()
    {
        lock (_glyphLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var ids = new List<int>();
            for (var i = 0; i < _usedGlyphs.Length; i++)
            {
                if (_usedGlyphs[i]) ids.Add(i);
            }
            return [.. ids];
        }
    }

    /// <summary>
    /// Release the face. Idempotent — multiple calls are no-ops. After disposal, mutating
    /// methods throw <see cref="ObjectDisposedException"/> while pure metadata properties
    /// remain readable for diagnostics. Disposal acquires the same lock as
    /// <see cref="MarkGlyphUsed(int)"/> / <see cref="IsGlyphUsed(int)"/> /
    /// <see cref="GetUsedGlyphIds"/> so a concurrent operation cannot mutate state across
    /// the disposal boundary; this guarantee will matter once a native HarfBuzz handle
    /// (Task 11 integration) is released here.
    /// </summary>
    public void Dispose()
    {
        lock (_glyphLock)
        {
            if (_disposed) return;
            _disposed = true;
            // Forward-compat: release HarfBuzz handle here when Task 11 wires one in.
        }
    }
}
