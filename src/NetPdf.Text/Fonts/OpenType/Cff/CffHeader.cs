// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType.Cff;

/// <summary>
/// Parsed CFF v1 header (Adobe Technical Note #5176 §"6 Header"). Four bytes:
/// major / minor version, declared header size, and the size in bytes of absolute
/// offsets used elsewhere in the table.
/// </summary>
/// <remarks>
/// Phase 1 supports CFF v1 only. CFF2 (OpenType §"CFF2") uses a different header shape
/// (uint16 length, no Top DICT INDEX) and is gated to Phase 1.x — variable fonts are
/// post-v1.0 territory.
/// </remarks>
internal sealed class CffHeader
{
    public required byte Major { get; init; }
    public required byte Minor { get; init; }

    /// <summary>Header size in bytes — usually 4, but the spec allows it to grow for future versions.</summary>
    public required byte HdrSize { get; init; }

    /// <summary>Absolute offset element size (1..4 bytes). Drives the offset-array width inside <see cref="CffIndex"/>.</summary>
    public required byte OffSize { get; init; }

    public static CffHeader Parse(ReadOnlySpan<byte> tableBytes)
    {
        if (tableBytes.Length < 4)
        {
            throw new InvalidDataException(
                $"CFF: expected at least 4 bytes for header; got {tableBytes.Length}.");
        }
        var major = tableBytes[0];
        var minor = tableBytes[1];
        var hdrSize = tableBytes[2];
        var offSize = tableBytes[3];

        if (major != 1)
        {
            throw new InvalidDataException(
                $"CFF: major version {major}.{minor} not supported. Phase 1 supports CFF v1 only " +
                "(CFF2 / variable fonts are gated to Phase 1.x).");
        }
        if (hdrSize < 4)
        {
            throw new InvalidDataException(
                $"CFF: hdrSize {hdrSize} is below the minimum of 4 for v1.");
        }
        if (offSize is < 1 or > 4)
        {
            throw new InvalidDataException(
                $"CFF: offSize {offSize} is outside the spec-permitted range [1, 4].");
        }
        if (hdrSize > tableBytes.Length)
        {
            throw new InvalidDataException(
                $"CFF: declared hdrSize {hdrSize} exceeds table length {tableBytes.Length}.");
        }

        return new CffHeader
        {
            Major = major,
            Minor = minor,
            HdrSize = hdrSize,
            OffSize = offSize,
        };
    }
}
