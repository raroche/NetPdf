// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// One entry in a WOFF 2.0 table directory. Each entry is variable-length:
/// </summary>
/// <list type="bullet">
///   <item>1 byte flags (bits 0–5 = known-tag index or 63 sentinel; bits 6–7 = transform version)</item>
///   <item>4 bytes tag (only if flags bits 0–5 == 63)</item>
///   <item>UIntBase128 origLength (1–5 bytes)</item>
///   <item>UIntBase128 transformLength (1–5 bytes; only present when transform applied — see §4.1)</item>
/// </list>
/// <remarks>
/// Spec basis: W3C "WOFF File Format 2.0" §4.1. Per spec, transformLength is present
/// (and origLength carries the post-transform length) only for tables with non-null
/// transforms. The two transforms defined are <c>glyf</c>/<c>loca</c> (transform
/// version 0) and <c>hmtx</c> (transform version 1). The "null transform" (version 3)
/// stores the table verbatim and omits transformLength.
/// </remarks>
internal readonly record struct WoffTwoTableEntry
{
    /// <summary>The 4-byte big-endian OpenType table tag (e.g. <c>0x676C7966</c> for <c>glyf</c>).</summary>
    public required uint Tag { get; init; }

    /// <summary>Transform version: 0..3. Per §4.1 only versions 0, 1, and 3 (null) are defined; 2 is reserved.</summary>
    public required byte TransformVersion { get; init; }

    /// <summary>Original (uncompressed, post-transform-reverse) table length in bytes.</summary>
    public required uint OrigLength { get; init; }

    /// <summary>
    /// Transformed length in bytes — what the table consumes in the decompressed stream
    /// before transform reversal. Equal to <see cref="OrigLength"/> when no transform is
    /// applied (null transform).
    /// </summary>
    public required uint TransformLength { get; init; }

    /// <summary>
    /// True when the entry uses the null transform (verbatim copy in the compressed
    /// stream). Per W3C WOFF 2.0 §5:
    /// </summary>
    /// <list type="bullet">
    ///   <item><c>glyf</c> / <c>loca</c>: version 0 = the actual transform; version 3 = null transform.</item>
    ///   <item><c>hmtx</c>: version 0 = null transform; version 1 = the hmtx transform.</item>
    ///   <item>Every other table: version 0 = null transform; versions 1, 2, 3 are not
    ///         currently defined and must be rejected (spec §5: "Predefined non-zero
    ///         transformation versions for tables other than glyf, loca, or hmtx are not
    ///         currently defined").</item>
    /// </list>
    public bool IsNullTransform => TransformVersion switch
    {
        // Version 3 is the null sentinel for glyf/loca only.
        3 when Tag == WoffTwoTags.Glyf || Tag == WoffTwoTags.Loca => true,
        // Version 0 is the null transform for every table EXCEPT glyf/loca (where it is
        // the actual transform).
        0 => Tag != WoffTwoTags.Glyf && Tag != WoffTwoTags.Loca,
        // Everything else is non-null. Version 1 on hmtx is the hmtx transform; all other
        // (table, version) combinations are undefined per spec and rejected upstream.
        _ => false,
    };

    /// <summary>
    /// Parse the variable-length directory beginning at <paramref name="cursor"/> in
    /// <paramref name="span"/>; advances <paramref name="cursor"/> past the directory.
    /// Returns the parsed entries in directory order. Rejects duplicate table tags —
    /// every entry must reference a unique OpenType table.
    /// </summary>
    public static WoffTwoTableEntry[] ParseDirectory(ReadOnlySpan<byte> span, ref int cursor, int numTables)
    {
        var entries = new WoffTwoTableEntry[numTables];
        var seenTags = new HashSet<uint>(numTables);
        for (var i = 0; i < numTables; i++)
        {
            entries[i] = ParseOne(span, ref cursor);
            if (!seenTags.Add(entries[i].Tag))
            {
                throw new InvalidDataException(
                    $"WOFF2: duplicate table tag 0x{entries[i].Tag:X8} at directory index {i}.");
            }
        }
        return entries;
    }

    private static WoffTwoTableEntry ParseOne(ReadOnlySpan<byte> span, ref int cursor)
    {
        if (cursor >= span.Length)
        {
            throw new InvalidDataException("WOFF2: table directory truncated (flags byte).");
        }
        var flags = span[cursor++];
        var tagIndex = (byte)(flags & 0x3F);
        var transformVersion = (byte)((flags >> 6) & 0x03);

        uint tag;
        if (tagIndex == WoffTwoConstants.CustomTagSentinel)
        {
            // Custom tag follows: 4 raw bytes (big-endian).
            if (cursor + 4 > span.Length)
            {
                throw new InvalidDataException("WOFF2: custom tag sentinel set but tag bytes truncated.");
            }
            tag = ((uint)span[cursor] << 24)
                | ((uint)span[cursor + 1] << 16)
                | ((uint)span[cursor + 2] << 8)
                | span[cursor + 3];
            cursor += 4;
        }
        else
        {
            tag = WoffTwoConstants.KnownTags[tagIndex];
        }

        var origLength = WoffTwoVarInt.ReadUIntBase128(span, ref cursor);

        // transformLength is present only when the table uses a NON-null transform.
        // Per §4.1 + §5: glyf/loca with transform version 0 (the actual transform) carry
        // transformLength; the null transform (version 3 OR a non-glyf/loca/hmtx table
        // with version 0) does not. For hmtx with version 1 the spec also requires it.
        bool hasTransformLength = transformVersion switch
        {
            // glyf or loca with version 0: actual transform → transformLength present.
            0 when tag == WoffTwoTags.Glyf || tag == WoffTwoTags.Loca => true,
            // hmtx with version 1: transform present.
            1 when tag == WoffTwoTags.Hmtx => true,
            _ => false,
        };

        uint transformLength;
        if (hasTransformLength)
        {
            transformLength = WoffTwoVarInt.ReadUIntBase128(span, ref cursor);
        }
        else
        {
            // No transform: stored size equals original size.
            transformLength = origLength;
        }

        return new WoffTwoTableEntry
        {
            Tag = tag,
            TransformVersion = transformVersion,
            OrigLength = origLength,
            TransformLength = transformLength,
        };
    }
}

/// <summary>Common WOFF 2.0 table tag constants, kept distinct from the OpenType-side tag set for spec referencing.</summary>
internal static class WoffTwoTags
{
    public const uint Glyf = 0x676C7966u; // 'glyf'
    public const uint Loca = 0x6C6F6361u; // 'loca'
    public const uint Hmtx = 0x686D7478u; // 'hmtx'
    public const uint Head = 0x68656164u; // 'head'
}
