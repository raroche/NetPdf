// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Generates the deterministic 6-uppercase-letter prefix that PDF requires on every
/// embedded subset font's BaseFont (e.g. <c>"AAAAAA+Helvetica"</c>, ISO 32000-2:2020 §9.6.4).
/// </summary>
/// <remarks>
/// <para>
/// The prefix must be unique per (font, used-glyph-set) combination so two subsets of the
/// same source font with different glyph populations do not share a BaseFont — that would
/// confuse cache layers and de-duplication logic in viewers. We derive it from
/// <c>SHA-256(NFC-normalized UTF-8 font name ‖ sorted used glyph ids encoded as 4-byte
/// big-endian)</c>.
/// </para>
/// <para>
/// <b>Algorithm.</b> Take the first 4 bytes of the digest as a big-endian
/// <see cref="uint"/> seed (32 bits of entropy). Repeatedly emit <c>seed % 26</c> as a
/// letter <c>A..Z</c> from the most-significant position, then divide by 26. The 6-letter
/// alphabet has 26⁶ = 308,915,776 ≈ 2²⁸·² distinct prefixes — collisions for distinct
/// inputs are astronomically unlikely.
/// </para>
/// <para>
/// <b>Why UTF-8 + NFC.</b> A previous version hashed the font name as ASCII, which
/// silently replaced non-ASCII characters with <c>?</c> — distinct international names
/// like "Söhne" and "Sühne" would have hashed to the same bytes. NFC normalization
/// ensures decomposed and precomposed forms of the same string also hash identically.
/// </para>
/// </remarks>
internal static class SubsetPrefix
{
    public const int PrefixLength = 6;

    /// <summary>
    /// Produce the 6-letter subset prefix for the given font name and the original glyph
    /// ids included in the subset (typically <c>plan.OrderedOldGlyphIds</c>).
    /// </summary>
    public static string Derive(string sourceFontName, IReadOnlyCollection<int> includedOriginalGlyphIds)
    {
        ArgumentNullException.ThrowIfNull(sourceFontName);
        ArgumentNullException.ThrowIfNull(includedOriginalGlyphIds);

        var sortedGlyphs = new int[includedOriginalGlyphIds.Count];
        var i = 0;
        foreach (var gid in includedOriginalGlyphIds)
        {
            sortedGlyphs[i++] = gid;
        }
        Array.Sort(sortedGlyphs);

        // Stream the inputs through IncrementalHash so we never materialize the full
        // serialized buffer. AOT-friendly; no LINQ in the hash hot path.
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var normalized = sourceFontName.Normalize(NormalizationForm.FormC);
        var nameBytes = Encoding.UTF8.GetBytes(normalized);
        hasher.AppendData(nameBytes);

        Span<byte> gidBytes = stackalloc byte[4];
        for (var k = 0; k < sortedGlyphs.Length; k++)
        {
            BinaryPrimitives.WriteInt32BigEndian(gidBytes, sortedGlyphs[k]);
            hasher.AppendData(gidBytes);
        }

        Span<byte> hash = stackalloc byte[32];
        var written = hasher.GetHashAndReset(hash);
        if (written != 32)
        {
            throw new InvalidOperationException("SHA-256 produced fewer than 32 bytes — should be impossible.");
        }

        // Take the first 32 bits of the digest as the seed; map to 6 base-26 letters via
        // repeated div/mod 26. Effective output entropy is log2(26^6) ≈ 28.2 bits.
        var seed = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
        Span<char> prefix = stackalloc char[PrefixLength];
        for (var pos = PrefixLength - 1; pos >= 0; pos--)
        {
            prefix[pos] = (char)('A' + (seed % 26));
            seed /= 26;
        }
        return new string(prefix);
    }
}
