// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed <c>hhea</c> table. 36 bytes, fixed layout (OpenType §"hhea"). Drives PDF-side
/// font descriptor metrics (ascent / descent / line gap) and tells <see cref="HmtxTable"/>
/// how many longHorMetric records to expect via <see cref="NumberOfHMetrics"/>.
/// </summary>
internal sealed class HheaTable
{
    public required ushort MajorVersion { get; init; }
    public required ushort MinorVersion { get; init; }

    public required short Ascender { get; init; }
    public required short Descender { get; init; }
    public required short LineGap { get; init; }
    public required ushort AdvanceWidthMax { get; init; }
    public required short MinLeftSideBearing { get; init; }
    public required short MinRightSideBearing { get; init; }
    public required short XMaxExtent { get; init; }
    public required short CaretSlopeRise { get; init; }
    public required short CaretSlopeRun { get; init; }
    public required short CaretOffset { get; init; }

    public required short MetricDataFormat { get; init; }

    /// <summary>Number of longHorMetric entries in <c>hmtx</c>. Remaining glyphs share the last advance width.</summary>
    public required ushort NumberOfHMetrics { get; init; }

    public static HheaTable Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 36)
        {
            throw new InvalidDataException(
                $"hhea: expected at least 36 bytes; got {tableBytes.Length}.");
        }
        var reader = new BigEndianReader(tableBytes);
        var majorVersion = reader.ReadUInt16();
        var minorVersion = reader.ReadUInt16();
        var ascender = reader.ReadInt16();
        var descender = reader.ReadInt16();
        var lineGap = reader.ReadInt16();
        var advanceWidthMax = reader.ReadUInt16();
        var minLsb = reader.ReadInt16();
        var minRsb = reader.ReadInt16();
        var xMaxExtent = reader.ReadInt16();
        var caretSlopeRise = reader.ReadInt16();
        var caretSlopeRun = reader.ReadInt16();
        var caretOffset = reader.ReadInt16();
        // 4 reserved int16 fields, must be 0 per spec; we don't enforce since real fonts sometimes drift.
        reader.Skip(8);
        var metricDataFormat = reader.ReadInt16();
        var numberOfHMetrics = reader.ReadUInt16();
        if (numberOfHMetrics == 0)
        {
            throw new InvalidDataException("hhea: numberOfHMetrics is 0; at least one glyph metric is required.");
        }

        return new HheaTable
        {
            MajorVersion = majorVersion,
            MinorVersion = minorVersion,
            Ascender = ascender,
            Descender = descender,
            LineGap = lineGap,
            AdvanceWidthMax = advanceWidthMax,
            MinLeftSideBearing = minLsb,
            MinRightSideBearing = minRsb,
            XMaxExtent = xMaxExtent,
            CaretSlopeRise = caretSlopeRise,
            CaretSlopeRun = caretSlopeRun,
            CaretOffset = caretOffset,
            MetricDataFormat = metricDataFormat,
            NumberOfHMetrics = numberOfHMetrics,
        };
    }
}
