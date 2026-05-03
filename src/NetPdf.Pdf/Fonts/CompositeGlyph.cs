// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Single source of truth for walking TTF composite glyphs (OpenType §"glyf"). Both the
/// subset planner (which collects referenced component ids) and the subset emitter (which
/// rewrites those ids) use the same walker — keeps validation aligned and prevents the
/// kind of mirrored-gap bug that arises when two callers parse the same wire format
/// independently.
/// </summary>
internal static class CompositeGlyph
{
    private const ushort FlagArg1And2AreWords = 0x0001;
    private const ushort FlagWeHaveAScale = 0x0008;
    private const ushort FlagMoreComponents = 0x0020;
    private const ushort FlagWeHaveAnXAndYScale = 0x0040;
    private const ushort FlagWeHaveATwoByTwo = 0x0080;
    private const ushort FlagWeHaveInstructions = 0x0100;

    /// <summary>Minimum bytes for a valid glyph header (numberOfContours + bounding box).</summary>
    public const int GlyphHeaderSize = 10;

    /// <summary>
    /// Where a composite component lives inside the glyph bytes. <see cref="GlyphIndexByteOffset"/>
    /// is the absolute byte offset of the 2-byte glyphIndex field — consumers either read it
    /// (planner) or overwrite it (emitter).
    /// </summary>
    public readonly record struct ComponentLocation(int GlyphIndexByteOffset, ushort GlyphIndex);

    /// <summary>
    /// True when the glyph header declares a composite (numberOfContours &lt; 0). Returns
    /// false for empty glyphs and glyphs with too few bytes to even read the header — the
    /// caller is responsible for the per-glyph "non-empty must be ≥ 10 bytes" guard.
    /// </summary>
    public static bool IsComposite(ReadOnlySpan<byte> glyphBytes)
    {
        if (glyphBytes.Length < 2)
        {
            return false;
        }
        return BinaryPrimitives.ReadInt16BigEndian(glyphBytes[..2]) < 0;
    }

    /// <summary>
    /// Reject non-empty glyph payloads that are smaller than the 10-byte header. Empty
    /// payloads are legitimate (whitespace glyphs, .notdef in some fonts) and pass through.
    /// </summary>
    public static void EnsureValidHeader(ReadOnlySpan<byte> glyphBytes, int glyphIndex)
    {
        if (glyphBytes.Length is > 0 and < GlyphHeaderSize)
        {
            throw new InvalidDataException(
                $"glyf glyph {glyphIndex}: payload is {glyphBytes.Length} byte(s) — non-empty " +
                $"glyph data must include the {GlyphHeaderSize}-byte header (numberOfContours + bbox).");
        }
    }

    /// <summary>
    /// Walk the composite glyph and return every component's <see cref="ComponentLocation"/>
    /// in record order. Validates argument-pair widths, optional transform widths, the
    /// <c>MORE_COMPONENTS</c> chain, and the <c>WE_HAVE_INSTRUCTIONS</c> trailer
    /// (instructionLength uint16 followed by exactly that many bytes).
    /// </summary>
    public static List<ComponentLocation> EnumerateComponents(ReadOnlySpan<byte> glyphBytes)
    {
        if (glyphBytes.Length < GlyphHeaderSize)
        {
            throw new InvalidDataException(
                $"Composite glyph: header truncated (need {GlyphHeaderSize} bytes; got {glyphBytes.Length}).");
        }
        var numberOfContours = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[..2]);
        if (numberOfContours >= 0)
        {
            throw new InvalidOperationException(
                "Composite walker called on a simple glyph (numberOfContours >= 0).");
        }

        var components = new List<ComponentLocation>(4);
        var pos = GlyphHeaderSize;
        ushort lastFlags;

        while (true)
        {
            if (pos + 4 > glyphBytes.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: component header truncated at byte {pos} (glyph length {glyphBytes.Length}).");
            }
            var flags = BinaryPrimitives.ReadUInt16BigEndian(glyphBytes.Slice(pos, 2));
            var glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(glyphBytes.Slice(pos + 2, 2));
            components.Add(new ComponentLocation(pos + 2, glyphIndex));
            lastFlags = flags;
            pos += 4;

            // Argument pair (1 or 2 bytes each)
            var argBytes = (flags & FlagArg1And2AreWords) != 0 ? 4 : 2;
            if (pos + argBytes > glyphBytes.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: argument pair truncated at byte {pos} (need {argBytes}, have {glyphBytes.Length - pos}).");
            }
            pos += argBytes;

            // Optional transform: at most one of these flags is meaningful at a time, but
            // the spec doesn't actually forbid combinations. We use the largest applicable.
            var transformBytes = 0;
            if ((flags & FlagWeHaveATwoByTwo) != 0)
            {
                transformBytes = 8;
            }
            else if ((flags & FlagWeHaveAnXAndYScale) != 0)
            {
                transformBytes = 4;
            }
            else if ((flags & FlagWeHaveAScale) != 0)
            {
                transformBytes = 2;
            }
            if (transformBytes != 0)
            {
                if (pos + transformBytes > glyphBytes.Length)
                {
                    throw new InvalidDataException(
                        $"Composite glyph: transform field truncated at byte {pos} (need {transformBytes}, have {glyphBytes.Length - pos}).");
                }
                pos += transformBytes;
            }

            if ((flags & FlagMoreComponents) == 0)
            {
                break;
            }
        }

        // Optional instruction trailer (after the LAST component if its flags set the bit).
        if ((lastFlags & FlagWeHaveInstructions) != 0)
        {
            if (pos + 2 > glyphBytes.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: WE_HAVE_INSTRUCTIONS set but instructionLength field truncated at byte {pos}.");
            }
            var instructionLength = BinaryPrimitives.ReadUInt16BigEndian(glyphBytes.Slice(pos, 2));
            pos += 2;
            if (pos + instructionLength > glyphBytes.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: instruction payload truncated — declared {instructionLength} byte(s), " +
                    $"only {glyphBytes.Length - pos} available.");
            }
            pos += instructionLength;
        }

        // Trailing bytes are tolerated by some real-world fonts; we don't reject them, but
        // the position-tracking loop above ensures we never read past the declared end.
        return components;
    }
}
