// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;

namespace NetPdf.Text.Fonts.Woff;

/// <summary>
/// Reverses the WOFF 2.0 glyf+loca transform (transform version 0). Spec basis: W3C
/// "WOFF File Format 2.0" §5.1 (Encoding), §5.2 (Triplet decoding). Clean-room
/// implementation directly from the spec; no third-party implementation source consulted.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline: parse the 36-byte transformed-glyf header → slice the 7 (or 8)
/// concatenated substreams → for each glyph reconstruct an SFNT-format glyph entry
/// (empty / simple / composite) → emit the freshly-computed loca offset table at the
/// matching <c>indexFormat</c>. Loca's transform is implicit: it carries no bytes of
/// its own in the compressed stream, and the decoder rebuilds it from the offsets it
/// records while writing glyf entries.
/// </para>
/// <para>
/// Re-encoding choice: the produced glyf table is correct but unoptimized — every
/// simple-glyph point uses a fresh flag byte (no <c>REPEAT_FLAG</c> compaction) and
/// 16-bit signed deltas (no <c>X_SHORT_VECTOR</c> / <c>Y_SHORT_VECTOR</c>). All TTF
/// readers MUST handle this format; downstream subsetters / PDF embedders re-encode
/// the glyph data to their preferred form anyway.
/// </para>
/// </remarks>
internal static class WoffTwoGlyfTransform
{
    private const int TransformedHeaderSize = 36;

    /// <summary>Reverse the transform; returns the reconstructed (glyf, loca) byte arrays.</summary>
    public static (byte[] GlyfBytes, byte[] LocaBytes, int IndexFormat) Reverse(ReadOnlySpan<byte> transformedGlyf)
    {
        if (transformedGlyf.Length < TransformedHeaderSize)
        {
            throw new InvalidDataException(
                $"WOFF2: transformed glyf header truncated; need {TransformedHeaderSize} bytes, got {transformedGlyf.Length}.");
        }

        // Header (§5.1).
        var reserved = BinaryPrimitives.ReadUInt16BigEndian(transformedGlyf[0..2]);
        if (reserved != 0)
        {
            throw new InvalidDataException($"WOFF2: transformed glyf reserved must be 0; got 0x{reserved:X4}.");
        }
        var optionFlags = BinaryPrimitives.ReadUInt16BigEndian(transformedGlyf[2..4]);
        // Per §5.1: only bit 0 (overlapSimpleBitmap-present) is defined; bits 1..15 must be 0.
        if ((optionFlags & 0xFFFE) != 0)
        {
            throw new InvalidDataException(
                $"WOFF2: transformed glyf optionFlags has reserved bits set: 0x{optionFlags:X4}.");
        }
        var hasOverlapSimpleBitmap = (optionFlags & 0x0001) != 0;
        var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(transformedGlyf[4..6]);
        var indexFormat = BinaryPrimitives.ReadUInt16BigEndian(transformedGlyf[6..8]);
        // Per OpenType spec head.indexToLocFormat: only 0 (short loca) and 1 (long loca)
        // are defined. WOFF 2.0 §5.1 inherits this constraint via the transformed-glyf
        // header. Any other value is malformed.
        if (indexFormat is not (0 or 1))
        {
            throw new InvalidDataException(
                $"WOFF2: transformed glyf indexFormat must be 0 (short) or 1 (long); got {indexFormat}.");
        }

        var nContourStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[8..12]);
        var nPointsStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[12..16]);
        var flagStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[16..20]);
        var glyphStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[20..24]);
        var compositeStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[24..28]);
        var bboxStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[28..32]);
        var instructionStreamSize = BinaryPrimitives.ReadUInt32BigEndian(transformedGlyf[32..36]);

        // The bboxStreamSize spans bboxBitmap + bboxStream concatenated.
        var bboxBitmapSize = ((numGlyphs + 31) / 32) * 4;

        // Layout the substreams.
        var cursor = TransformedHeaderSize;
        var nContour = SliceStream(transformedGlyf, ref cursor, nContourStreamSize, "nContourStream");
        if (nContour.Length != numGlyphs * 2)
        {
            throw new InvalidDataException(
                $"WOFF2: nContourStream length {nContour.Length} != numGlyphs × 2 ({numGlyphs * 2}).");
        }
        var nPoints = SliceStream(transformedGlyf, ref cursor, nPointsStreamSize, "nPointsStream");
        var flags = SliceStream(transformedGlyf, ref cursor, flagStreamSize, "flagStream");
        var glyph = SliceStream(transformedGlyf, ref cursor, glyphStreamSize, "glyphStream");
        var composite = SliceStream(transformedGlyf, ref cursor, compositeStreamSize, "compositeStream");
        var bboxArea = SliceStream(transformedGlyf, ref cursor, bboxStreamSize, "bboxStream");
        var instructions = SliceStream(transformedGlyf, ref cursor, instructionStreamSize, "instructionStream");
        ReadOnlySpan<byte> overlapSimple = ReadOnlySpan<byte>.Empty;
        if (hasOverlapSimpleBitmap)
        {
            overlapSimple = SliceStream(transformedGlyf, ref cursor, (uint)bboxBitmapSize, "overlapSimpleBitmap");
        }
        if (cursor != transformedGlyf.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: transformed glyf has {transformedGlyf.Length - cursor} trailing byte(s) after declared substreams.");
        }
        if (bboxArea.Length < bboxBitmapSize)
        {
            throw new InvalidDataException(
                $"WOFF2: bboxStreamSize {bboxArea.Length} < bboxBitmap size {bboxBitmapSize} for {numGlyphs} glyphs.");
        }
        var bboxBitmap = bboxArea[..bboxBitmapSize];
        var bboxValues = bboxArea[bboxBitmapSize..];

        // Per-substream forward cursors.
        var nContourCursor = 0;
        var nPointsCursor = 0;
        var flagsCursor = 0;
        var glyphCursor = 0;
        var compositeCursor = 0;
        var bboxCursor = 0;
        var instructionCursor = 0;

        // Output glyf bytes — variable per glyph, 4-byte aligned. We write to a
        // MemoryStream and snapshot offsets in `locaOffsets` for the loca rebuild.
        using var glyfOutput = new MemoryStream();
        var locaOffsets = new uint[numGlyphs + 1];

        for (var g = 0; g < numGlyphs; g++)
        {
            locaOffsets[g] = (uint)glyfOutput.Position;

            // Read nContour for this glyph.
            var nContoursRaw = BinaryPrimitives.ReadInt16BigEndian(nContour[nContourCursor..(nContourCursor + 2)]);
            nContourCursor += 2;

            var bboxBitSet = ((bboxBitmap[g / 8] >> (7 - (g % 8))) & 1) != 0;

            if (nContoursRaw == 0)
            {
                // Empty glyph — no bytes written, loca[g+1] stays equal to loca[g] after this iteration.
                if (bboxBitSet)
                {
                    throw new InvalidDataException(
                        $"WOFF2: empty glyph #{g} has bboxBitmap bit set — malformed.");
                }
                continue;
            }

            if (nContoursRaw > 0)
            {
                // Simple glyph.
                EmitSimpleGlyph(
                    glyfOutput,
                    nContoursRaw,
                    bboxBitSet,
                    bboxValues,
                    ref bboxCursor,
                    nPoints,
                    ref nPointsCursor,
                    flags,
                    ref flagsCursor,
                    glyph,
                    ref glyphCursor,
                    instructions,
                    ref instructionCursor,
                    overlapSimple,
                    g);
            }
            else
            {
                // Composite glyph (-1).
                if (!bboxBitSet)
                {
                    throw new InvalidDataException(
                        $"WOFF2: composite glyph #{g} must have bbox bit set — malformed.");
                }
                EmitCompositeGlyph(
                    glyfOutput,
                    bboxValues,
                    ref bboxCursor,
                    composite,
                    ref compositeCursor,
                    instructions,
                    ref instructionCursor);
            }

            // Pad each glyph entry to 4-byte alignment per OpenType spec.
            while ((glyfOutput.Position & 3) != 0) glyfOutput.WriteByte(0);
        }

        locaOffsets[numGlyphs] = (uint)glyfOutput.Position;

        // Per-substream cursor consumption checks. The whole-stream trailing-bytes check
        // earlier rules out unconsumed regions overall, but a per-substream check pinpoints
        // a malformed file where one substream over-claims at the expense of another.
        if (nContourCursor != nContour.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: nContourStream has {nContour.Length - nContourCursor} unconsumed byte(s).");
        }
        if (nPointsCursor != nPoints.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: nPointsStream has {nPoints.Length - nPointsCursor} unconsumed byte(s).");
        }
        if (flagsCursor != flags.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: flagStream has {flags.Length - flagsCursor} unconsumed byte(s).");
        }
        if (glyphCursor != glyph.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: glyphStream has {glyph.Length - glyphCursor} unconsumed byte(s).");
        }
        if (compositeCursor != composite.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: compositeStream has {composite.Length - compositeCursor} unconsumed byte(s).");
        }
        if (bboxCursor != bboxValues.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: bboxStream has {bboxValues.Length - bboxCursor} unconsumed byte(s).");
        }
        if (instructionCursor != instructions.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: instructionStream has {instructions.Length - instructionCursor} unconsumed byte(s).");
        }

        var glyfBytes = glyfOutput.ToArray();
        var locaBytes = BuildLocaTable(locaOffsets, indexFormat);
        return (glyfBytes, locaBytes, indexFormat);
    }

    private static ReadOnlySpan<byte> SliceStream(ReadOnlySpan<byte> all, scoped ref int cursor, uint declaredLength, string name)
    {
        if ((long)cursor + declaredLength > all.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: transformed glyf {name} (length={declaredLength}) extends past total ({all.Length} bytes; cursor={cursor}).");
        }
        var slice = all.Slice(cursor, (int)declaredLength);
        cursor += (int)declaredLength;
        return slice;
    }

    private static void EmitSimpleGlyph(
        MemoryStream output,
        short nContours,
        bool bboxBitSet,
        ReadOnlySpan<byte> bboxValues,
        ref int bboxCursor,
        ReadOnlySpan<byte> nPoints,
        ref int nPointsCursor,
        ReadOnlySpan<byte> flagsStream,
        ref int flagsCursor,
        ReadOnlySpan<byte> glyphStream,
        ref int glyphCursor,
        ReadOnlySpan<byte> instructions,
        ref int instructionCursor,
        ReadOnlySpan<byte> overlapSimple,
        int glyphIndex)
    {
        // Read endpoints — one 255UInt16 per contour. The cumulative sum of nPoints
        // values gives the total point count; per-contour endPtsOfContours[c] is
        // (sum-up-to-and-including-c) - 1.
        Span<int> nPointsPerContour = stackalloc int[nContours];
        var totalPoints = 0;
        for (var c = 0; c < nContours; c++)
        {
            var v = WoffTwoVarInt.Read255UInt16(nPoints, ref nPointsCursor);
            // A zero-point contour would emit endPtsOfContours[c] = (cumulative - 1) which
            // underflows to 0xFFFF when cast to UInt16; the resulting glyph is malformed.
            // Reject explicitly per WOFF 2.0 §5.1 (each contour must have at least 1 point).
            if (v == 0)
            {
                throw new InvalidDataException(
                    $"WOFF2: simple glyph #{glyphIndex} contour #{c} has 0 points.");
            }
            nPointsPerContour[c] = v;
            totalPoints += v;
        }

        // Read per-point on-curve flags + triplet indices.
        if (flagsCursor + totalPoints > flagsStream.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: flagStream truncated for simple glyph #{glyphIndex} (need {totalPoints} bytes).");
        }
        var pointFlags = flagsStream.Slice(flagsCursor, totalPoints);
        flagsCursor += totalPoints;

        // Decode coordinate triplets and accumulate to absolute coords.
        Span<int> xs = totalPoints <= 256 ? stackalloc int[totalPoints] : new int[totalPoints];
        Span<int> ys = totalPoints <= 256 ? stackalloc int[totalPoints] : new int[totalPoints];
        var x = 0;
        var y = 0;
        for (var p = 0; p < totalPoints; p++)
        {
            var flagByte = pointFlags[p];
            var entryIdx = flagByte & 0x7F;
            var entry = WoffTwoTripletTable.Entries[entryIdx];

            int dx = 0, dy = 0;
            DecodeTriplet(glyphStream, ref glyphCursor, entry, out dx, out dy);
            x += dx;
            y += dy;
            xs[p] = x;
            ys[p] = y;
        }

        // Read instructionLength as 255UInt16 from glyphStream; instruction bytes from instructionStream.
        var instructionLength = WoffTwoVarInt.Read255UInt16(glyphStream, ref glyphCursor);
        if (instructionCursor + instructionLength > instructions.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: instructionStream truncated for simple glyph #{glyphIndex} (need {instructionLength} bytes).");
        }
        var instructionBytes = instructions.Slice(instructionCursor, instructionLength);
        instructionCursor += instructionLength;

        // Resolve bbox.
        short xMin, yMin, xMax, yMax;
        if (bboxBitSet)
        {
            // Explicit guard so a truncated bboxStream surfaces as InvalidDataException,
            // not the ArgumentOutOfRangeException Span slicing would otherwise throw.
            if (bboxCursor + 8 > bboxValues.Length)
            {
                throw new InvalidDataException(
                    $"WOFF2: simple glyph #{glyphIndex} bbox truncated in bboxStream (need 8 bytes at cursor {bboxCursor}, have {bboxValues.Length - bboxCursor}).");
            }
            xMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[bboxCursor..(bboxCursor + 2)]);
            yMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 2)..(bboxCursor + 4)]);
            xMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 4)..(bboxCursor + 6)]);
            yMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 6)..(bboxCursor + 8)]);
            bboxCursor += 8;
        }
        else
        {
            // Compute from coordinates.
            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var minY = int.MaxValue;
            var maxY = int.MinValue;
            for (var p = 0; p < totalPoints; p++)
            {
                if (xs[p] < minX) minX = xs[p];
                if (xs[p] > maxX) maxX = xs[p];
                if (ys[p] < minY) minY = ys[p];
                if (ys[p] > maxY) maxY = ys[p];
            }
            if (totalPoints == 0)
            {
                minX = maxX = minY = maxY = 0;
            }
            // Bbox values must fit in Int16 per OpenType. Overflow indicates a malformed
            // glyph — reject so a corrupted glyph never lands in a downstream renderer.
            xMin = ToInt16OrThrow(minX, "xMin", glyphIndex);
            yMin = ToInt16OrThrow(minY, "yMin", glyphIndex);
            xMax = ToInt16OrThrow(maxX, "xMax", glyphIndex);
            yMax = ToInt16OrThrow(maxY, "yMax", glyphIndex);
        }

        // Determine OVERLAP_SIMPLE bit for the FIRST point per OpenType convention.
        bool overlap = false;
        if (overlapSimple.Length > 0)
        {
            overlap = ((overlapSimple[glyphIndex / 8] >> (7 - (glyphIndex % 8))) & 1) != 0;
        }

        // Emit glyph header + body.
        WriteInt16BE(output, nContours);
        WriteInt16BE(output, xMin);
        WriteInt16BE(output, yMin);
        WriteInt16BE(output, xMax);
        WriteInt16BE(output, yMax);

        // endPtsOfContours[]
        var pointIndex = 0;
        for (var c = 0; c < nContours; c++)
        {
            pointIndex += nPointsPerContour[c];
            WriteUInt16BE(output, (ushort)(pointIndex - 1));
        }

        // instructionLength + instructions
        WriteUInt16BE(output, (ushort)instructionLength);
        if (instructionLength > 0)
        {
            output.Write(instructionBytes);
        }

        // Flags — one byte per point, no REPEAT compaction. We always use 16-bit X/Y deltas.
        for (var p = 0; p < totalPoints; p++)
        {
            byte ttfFlag = 0;
            if ((pointFlags[p] & 0x80) != 0) ttfFlag |= 0x01; // ON_CURVE_POINT
            if (p == 0 && overlap) ttfFlag |= 0x40;            // OVERLAP_SIMPLE on first point only
            output.WriteByte(ttfFlag);
        }

        // Compute relative-to-previous deltas from absolute coords and write as Int16 BE.
        // Per OpenType §"glyf", point deltas are signed 16-bit. A delta outside Int16
        // range cannot be represented and indicates malformed input — reject rather than
        // silently clamp, which would corrupt the glyph outline.
        var prevX = 0;
        for (var p = 0; p < totalPoints; p++)
        {
            var dx = xs[p] - prevX;
            prevX = xs[p];
            if (dx is < short.MinValue or > short.MaxValue)
            {
                throw new InvalidDataException(
                    $"WOFF2: simple glyph #{glyphIndex} point #{p} X delta {dx} is out of Int16 range.");
            }
            WriteInt16BE(output, (short)dx);
        }
        var prevY = 0;
        for (var p = 0; p < totalPoints; p++)
        {
            var dy = ys[p] - prevY;
            prevY = ys[p];
            if (dy is < short.MinValue or > short.MaxValue)
            {
                throw new InvalidDataException(
                    $"WOFF2: simple glyph #{glyphIndex} point #{p} Y delta {dy} is out of Int16 range.");
            }
            WriteInt16BE(output, (short)dy);
        }
    }

    private static void DecodeTriplet(ReadOnlySpan<byte> glyphStream, ref int cursor, WoffTwoTripletTable.Entry entry, out int dx, out int dy)
    {
        var bytesAfterFlag = entry.ByteCount - 1;
        if (cursor + bytesAfterFlag > glyphStream.Length)
        {
            throw new InvalidDataException(
                $"WOFF2: glyphStream truncated reading triplet (need {bytesAfterFlag} bytes at cursor {cursor}).");
        }
        var coordBytes = glyphStream.Slice(cursor, bytesAfterFlag);
        cursor += bytesAfterFlag;

        int rawX = 0, rawY = 0;
        switch ((entry.XBits, entry.YBits))
        {
            case (0, 8):
                rawY = coordBytes[0];
                break;
            case (8, 0):
                rawX = coordBytes[0];
                break;
            case (4, 4):
                rawX = (coordBytes[0] >> 4) & 0x0F;
                rawY = coordBytes[0] & 0x0F;
                break;
            case (8, 8):
                rawX = coordBytes[0];
                rawY = coordBytes[1];
                break;
            case (12, 12):
                rawX = (coordBytes[0] << 4) | ((coordBytes[1] >> 4) & 0x0F);
                rawY = ((coordBytes[1] & 0x0F) << 8) | coordBytes[2];
                break;
            case (16, 16):
                rawX = (coordBytes[0] << 8) | coordBytes[1];
                rawY = (coordBytes[2] << 8) | coordBytes[3];
                break;
            default:
                throw new InvalidDataException($"WOFF2: unsupported triplet (xBits={entry.XBits}, yBits={entry.YBits}).");
        }

        dx = entry.XBits == 0 ? 0 : (rawX + entry.DeltaX) * entry.XSign;
        dy = entry.YBits == 0 ? 0 : (rawY + entry.DeltaY) * entry.YSign;
    }

    private static void EmitCompositeGlyph(
        MemoryStream output,
        ReadOnlySpan<byte> bboxValues,
        ref int bboxCursor,
        ReadOnlySpan<byte> compositeStream,
        ref int compositeCursor,
        ReadOnlySpan<byte> instructions,
        ref int instructionCursor)
    {
        // Bbox is mandatory (caller already verified).
        if (bboxCursor + 8 > bboxValues.Length)
        {
            throw new InvalidDataException("WOFF2: composite glyph bbox truncated in bboxStream.");
        }
        var xMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[bboxCursor..(bboxCursor + 2)]);
        var yMin = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 2)..(bboxCursor + 4)]);
        var xMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 4)..(bboxCursor + 6)]);
        var yMax = BinaryPrimitives.ReadInt16BigEndian(bboxValues[(bboxCursor + 6)..(bboxCursor + 8)]);
        bboxCursor += 8;

        WriteInt16BE(output, -1); // numContours
        WriteInt16BE(output, xMin);
        WriteInt16BE(output, yMin);
        WriteInt16BE(output, xMax);
        WriteInt16BE(output, yMax);

        // Walk component records from compositeStream until !FLAG_MORE_COMPONENTS.
        // Components have the same byte format as TTF composite glyph records (per §5.1).
        var componentsStart = compositeCursor;
        bool weHaveInstructions = false;
        ushort flagsField;
        do
        {
            if (compositeCursor + 4 > compositeStream.Length)
            {
                throw new InvalidDataException("WOFF2: compositeStream truncated reading component header.");
            }
            flagsField = BinaryPrimitives.ReadUInt16BigEndian(compositeStream[compositeCursor..(compositeCursor + 2)]);
            compositeCursor += 2;
            // glyphIndex
            compositeCursor += 2;

            // Argument size: 2 (Int8 each) or 4 (Int16 each) per ARG_1_AND_2_ARE_WORDS.
            var argSize = (flagsField & 0x0001) != 0 ? 4 : 2;
            // Transform size.
            var xformSize = 0;
            if ((flagsField & 0x0008) != 0) xformSize = 2;       // WE_HAVE_A_SCALE
            else if ((flagsField & 0x0040) != 0) xformSize = 4;  // WE_HAVE_AN_X_AND_Y_SCALE
            else if ((flagsField & 0x0080) != 0) xformSize = 8;  // WE_HAVE_A_TWO_BY_TWO

            compositeCursor += argSize + xformSize;
            if (compositeCursor > compositeStream.Length)
            {
                throw new InvalidDataException("WOFF2: compositeStream truncated reading component args/transform.");
            }
            if ((flagsField & 0x0100) != 0) weHaveInstructions = true; // WE_HAVE_INSTRUCTIONS
        }
        while ((flagsField & 0x0020) != 0); // FLAG_MORE_COMPONENTS

        // Copy the components verbatim into the output.
        var componentsBytes = compositeStream[componentsStart..compositeCursor];
        output.Write(componentsBytes);

        // If WE_HAVE_INSTRUCTIONS, the instructionLength is inline in compositeStream
        // immediately after the last component, and the instruction bytes are pulled
        // from instructionStream.
        if (weHaveInstructions)
        {
            if (compositeCursor + 2 > compositeStream.Length)
            {
                throw new InvalidDataException("WOFF2: compositeStream truncated reading instructionLength.");
            }
            var instructionLength = BinaryPrimitives.ReadUInt16BigEndian(compositeStream[compositeCursor..(compositeCursor + 2)]);
            compositeCursor += 2;

            if (instructionCursor + instructionLength > instructions.Length)
            {
                throw new InvalidDataException("WOFF2: instructionStream truncated for composite glyph.");
            }
            var instructionBytes = instructions.Slice(instructionCursor, instructionLength);
            instructionCursor += instructionLength;

            // Emit inline in TTF format.
            WriteUInt16BE(output, instructionLength);
            output.Write(instructionBytes);
        }
    }

    private static byte[] BuildLocaTable(uint[] offsets, int indexFormat)
    {
        // indexFormat 0 = short (UInt16, halfword-units); else long (UInt32, byte-units).
        if (indexFormat == 0)
        {
            // Halfword-units: every offset must be even and fit in UInt16 (< 0x20000 bytes).
            var bytes = new byte[offsets.Length * 2];
            var span = bytes.AsSpan();
            for (var i = 0; i < offsets.Length; i++)
            {
                var off = offsets[i];
                if ((off & 1) != 0 || off > 0x1FFFE)
                {
                    throw new InvalidDataException(
                        $"WOFF2: short loca format chosen but glyph #{i} offset {off} does not fit (must be even and ≤ 131070).");
                }
                BinaryPrimitives.WriteUInt16BigEndian(span[(i * 2)..((i + 1) * 2)], (ushort)(off / 2));
            }
            return bytes;
        }
        else
        {
            var bytes = new byte[offsets.Length * 4];
            var span = bytes.AsSpan();
            for (var i = 0; i < offsets.Length; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(span[(i * 4)..((i + 1) * 4)], offsets[i]);
            }
            return bytes;
        }
    }

    private static void WriteInt16BE(Stream s, short v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteUInt16BE(Stream s, ushort v)
    {
        s.WriteByte((byte)((v >> 8) & 0xFF));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static short ToInt16OrThrow(int v, string field, int glyphIndex)
    {
        if (v is < short.MinValue or > short.MaxValue)
        {
            throw new InvalidDataException(
                $"WOFF2: simple glyph #{glyphIndex} {field} = {v} is out of Int16 range.");
        }
        return (short)v;
    }
}
