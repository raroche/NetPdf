// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// One entry in the SFNT table directory (16 bytes per OpenType §"Table directory").
/// </summary>
internal readonly struct TableRecord
{
    /// <summary>4-byte ASCII tag, big-endian-encoded. Compare with <see cref="OpenTypeTags"/>.</summary>
    public required uint Tag { get; init; }

    /// <summary>Table checksum. Not validated by Phase 1 — only the actual byte payloads matter for our use cases.</summary>
    public required uint Checksum { get; init; }

    /// <summary>Byte offset of the table from the start of the font file.</summary>
    public required uint Offset { get; init; }

    /// <summary>Actual table length (before optional 4-byte padding to align the next table).</summary>
    public required uint Length { get; init; }
}
