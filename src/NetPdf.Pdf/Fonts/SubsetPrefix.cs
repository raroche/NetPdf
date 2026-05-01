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
/// The prefix must be unique per (font, used-glyph-set) combination so two subsets of the
/// same source font with different glyph populations do not share a BaseFont — that would
/// confuse cache layers and de-duplication logic in viewers. We derive it from the
/// SHA-256 of (font name bytes || sorted used glyph ids encoded as 4-byte big-endian),
/// then map the first 28 bits of the digest to six base-26 letters. 26⁶ ≈ 309 M
/// combinations, and SHA-256 collisions in the prefix space are astronomically unlikely.
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
        var nameBytes = Encoding.ASCII.GetBytes(sourceFontName);
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

        // Take the first 32 bits of the digest as the seed; map to 6 base-26 letters.
        // Repeated div/mod keeps the most-significant letters from the high-order bits.
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
