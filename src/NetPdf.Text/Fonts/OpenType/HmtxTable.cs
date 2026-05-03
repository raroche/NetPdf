// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>hmtx</c> table (OpenType §"hmtx"). Carries per-glyph horizontal metrics:
/// <see cref="AdvanceWidths"/> (one per glyph) and <see cref="LeftSideBearings"/>
/// (one per glyph).
/// </summary>
/// <remarks>
/// Wire format: <c>numberOfHMetrics</c> longHorMetric records (4 bytes each — uint16
/// advance + int16 lsb), then <c>numGlyphs - numberOfHMetrics</c> int16 lsb-only entries
/// for trailing monospaced glyphs that share the last advance width. After parsing we
/// expose flat <see cref="AdvanceWidths"/> and <see cref="LeftSideBearings"/> arrays of
/// length <see cref="MaxpTable.NumGlyphs"/> for O(1) per-glyph lookup downstream.
/// </remarks>
internal sealed class HmtxTable
{
    /// <summary>Advance width in font design units, indexed by glyph id.</summary>
    public required ushort[] AdvanceWidths { get; init; }

    /// <summary>Left side bearing in font design units, indexed by glyph id.</summary>
    public required short[] LeftSideBearings { get; init; }

    public static HmtxTable Parse(ReadOnlySpan<byte> tableBytes, ushort numberOfHMetrics, ushort numGlyphs)
    {
        if (numberOfHMetrics == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberOfHMetrics), numberOfHMetrics,
                "numberOfHMetrics must be at least 1.");
        }
        if (numberOfHMetrics > numGlyphs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberOfHMetrics), numberOfHMetrics,
                $"numberOfHMetrics ({numberOfHMetrics}) cannot exceed numGlyphs ({numGlyphs}).");
        }

        var lsbOnlyCount = numGlyphs - numberOfHMetrics;
        var expectedLength = (numberOfHMetrics * 4) + (lsbOnlyCount * 2);
        if (tableBytes.Length < expectedLength)
        {
            throw new InvalidDataException(
                $"hmtx: expected {expectedLength} bytes for {numberOfHMetrics} long metric(s) " +
                $"and {lsbOnlyCount} lsb-only entr(ies); got {tableBytes.Length}.");
        }

        var advanceWidths = new ushort[numGlyphs];
        var leftSideBearings = new short[numGlyphs];

        var reader = new BigEndianReader(tableBytes);
        ushort lastAdvance = 0;
        for (var i = 0; i < numberOfHMetrics; i++)
        {
            lastAdvance = reader.ReadUInt16();
            advanceWidths[i] = lastAdvance;
            leftSideBearings[i] = reader.ReadInt16();
        }
        for (var i = numberOfHMetrics; i < numGlyphs; i++)
        {
            advanceWidths[i] = lastAdvance;
            leftSideBearings[i] = reader.ReadInt16();
        }

        return new HmtxTable
        {
            AdvanceWidths = advanceWidths,
            LeftSideBearings = leftSideBearings,
        };
    }
}
