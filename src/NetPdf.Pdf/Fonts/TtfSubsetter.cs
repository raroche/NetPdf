// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Emits the subset bytes for the TTF tables that change shape under glyph compaction:
/// <c>glyf</c>, <c>loca</c>, <c>hmtx</c>, <c>maxp</c>, plus updated <c>head</c> and
/// <c>hhea</c>. SFNT envelope assembly + table-pass-through (<c>cmap</c>, <c>name</c>,
/// <c>OS/2</c>, <c>post</c>) is the embedder's job in Phase 1 Task 10.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite glyph rewriting.</b> When a composite glyph appears in the subset, its
/// component records reference other glyphs by original id. The emitter rewrites every
/// component's <c>glyphIndex</c> field to the new id from <see cref="GlyphSubsetPlan.OldToNew"/>
/// while leaving everything else (flags / args / transform) byte-identical. The
/// composite-chase in <see cref="GlyphSubsetPlan.Build"/> guarantees every referenced
/// component is in the subset.
/// </para>
/// <para>
/// <b>Glyph alignment.</b> TTF requires per-glyph data in <c>glyf</c> to start on a
/// 2-byte boundary so <c>loca</c>'s short format (which stores offsets divided by 2)
/// stays usable. We pad each glyph's bytes with a single zero byte if its length is odd.
/// </para>
/// <para>
/// <b>Determinism.</b> Output is byte-equal for byte-equal inputs — no PRNG, no timing,
/// no map iteration that depends on hash codes (<see cref="GlyphSubsetPlan.OrderedOldGlyphIds"/>
/// drives every loop).
/// </para>
/// </remarks>
internal static class TtfSubsetter
{
    public static TtfSubsetResult Subset(OpenTypeFont font, GlyphSubsetPlan plan)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(plan);
        if (!font.HasTrueTypeOutlines)
        {
            throw new InvalidOperationException("TtfSubsetter requires a TTF-flavored font.");
        }

        // 1. Re-emit glyf — copy each glyph's bytes (with composite components rewritten),
        //    pad to 2-byte alignment, record the new offsets for loca.
        var (glyfBytes, newOffsets) = EmitGlyf(font, plan);

        // 2. Choose loca format. Short format (uint16 of offset/2) requires every offset
        //    to fit in (uint16 × 2). If glyf grew past that, switch to long.
        var maxOffset = newOffsets[plan.NumGlyphs];
        var useLongLoca = maxOffset > 0x1FFFE; // 0xFFFF × 2 = highest short-format offset
        var locaBytes = EmitLoca(newOffsets, useLongLoca);

        // 3. Subset hmtx — every glyph emits a long metric (no lsb-only trail in subset).
        var hmtxBytes = EmitHmtx(font, plan);

        // 4. Update head bytes (indexToLocFormat may change).
        var headBytes = EmitHead(font, useLongLoca);

        // 5. Update hhea (numberOfHMetrics = subset glyph count).
        var hheaBytes = EmitHhea(font, (ushort)plan.NumGlyphs);

        // 6. Update maxp (numGlyphs = subset glyph count). v1.0 fields stay as the source's
        //    upper bounds — they're maxima and remain valid for any subset.
        var maxpBytes = EmitMaxp(font, (ushort)plan.NumGlyphs);

        // 7. Resolve the source font's PostScript / family name for the BaseFont prefix.
        var sourceName = font.Name.PostScriptName ?? font.Name.FamilyName ?? "Subset";
        var prefix = SubsetPrefix.Derive(sourceName, plan.OrderedOldGlyphIds);

        return new TtfSubsetResult
        {
            Plan = plan,
            SubsetBaseFontName = $"{prefix}+{sourceName}",
            HeadBytes = headBytes,
            HheaBytes = hheaBytes,
            MaxpBytes = maxpBytes,
            HmtxBytes = hmtxBytes,
            LocaBytes = locaBytes,
            GlyfBytes = glyfBytes,
        };
    }

    private static (byte[] GlyfBytes, uint[] Offsets) EmitGlyf(OpenTypeFont font, GlyphSubsetPlan plan)
    {
        var glyf = font.Glyf!;
        var offsets = new uint[plan.NumGlyphs + 1];
        var output = new MemoryStream();
        var cursor = 0u;

        for (var newId = 0; newId < plan.NumGlyphs; newId++)
        {
            offsets[newId] = cursor;
            var oldId = plan.OrderedOldGlyphIds[newId];
            var glyphBytes = glyf.GetGlyphBytes(oldId);

            if (glyphBytes.Length == 0)
            {
                // Empty glyph (e.g. .notdef in some fonts, whitespace). loca offsets just
                // record the same boundary twice — no bytes emitted.
                continue;
            }

            // For composite glyphs, copy the bytes and rewrite component glyphIndex fields
            // in place. For simple glyphs, we can copy verbatim.
            var rewritten = MaybeRewriteComposite(glyphBytes, plan);
            output.Write(rewritten);
            cursor += (uint)rewritten.Length;

            // Pad to 2-byte alignment so loca's short format remains usable.
            if ((cursor & 1) != 0)
            {
                output.WriteByte(0);
                cursor++;
            }
        }
        offsets[plan.NumGlyphs] = cursor;
        return (output.ToArray(), offsets);
    }

    private static byte[] MaybeRewriteComposite(ReadOnlySpan<byte> glyphBytes, GlyphSubsetPlan plan)
    {
        if (glyphBytes.Length < 10)
        {
            return glyphBytes.ToArray();
        }
        var numberOfContours = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[..2]);
        if (numberOfContours >= 0)
        {
            return glyphBytes.ToArray();
        }

        // Composite. Walk the component records and rewrite glyphIndex in place. The header
        // (10 bytes: numberOfContours + bbox) and every byte we don't explicitly rewrite is
        // preserved verbatim, so the glyph's geometry is byte-identical.
        var output = glyphBytes.ToArray();
        var pos = 10;
        while (true)
        {
            if (pos + 4 > output.Length)
            {
                throw new InvalidDataException(
                    $"Composite glyph: component header truncated at offset {pos} of {output.Length}.");
            }
            var flags = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(pos, 2));
            var oldComponent = BinaryPrimitives.ReadUInt16BigEndian(output.AsSpan(pos + 2, 2));
            if (!plan.OldToNew.TryGetValue(oldComponent, out var newComponent))
            {
                throw new InvalidOperationException(
                    $"Composite component glyph {oldComponent} is missing from the subset plan — " +
                    "GlyphSubsetPlan.Build should have caught this; there is a bug in the composite chase.");
            }
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(pos + 2, 2), (ushort)newComponent);
            pos += 4;

            // Skip the rest of this component record. Argument width:
            pos += (flags & 0x0001) != 0 ? 4 : 2;

            if ((flags & 0x0008) != 0)
            {
                pos += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                pos += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                pos += 8;
            }

            if ((flags & 0x0020) == 0)
            {
                break; // last component
            }
        }
        return output;
    }

    private static byte[] EmitLoca(uint[] offsets, bool longFormat)
    {
        var entries = offsets.Length;
        if (longFormat)
        {
            var bytes = new byte[entries * 4];
            for (var i = 0; i < entries; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), offsets[i]);
            }
            return bytes;
        }
        else
        {
            var bytes = new byte[entries * 2];
            for (var i = 0; i < entries; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), (ushort)(offsets[i] / 2));
            }
            return bytes;
        }
    }

    private static byte[] EmitHmtx(OpenTypeFont font, GlyphSubsetPlan plan)
    {
        // One longHorMetric per subset glyph: 4 bytes each (advance uint16 + lsb int16).
        var bytes = new byte[plan.NumGlyphs * 4];
        for (var newId = 0; newId < plan.NumGlyphs; newId++)
        {
            var oldId = plan.OrderedOldGlyphIds[newId];
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(newId * 4, 2), font.Hmtx.AdvanceWidths[oldId]);
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan((newId * 4) + 2, 2), font.Hmtx.LeftSideBearings[oldId]);
        }
        return bytes;
    }

    private static byte[] EmitHead(OpenTypeFont font, bool useLongLoca)
    {
        // Copy the source head bytes and overwrite indexToLocFormat. checkSumAdjustment is
        // zeroed — the embedder recomputes it after the SFNT envelope is finalized in
        // Task 10.
        var headSpan = font.Directory.GetTableBytes(OpenTypeTags.Head, font.FontBytes.Span);
        var bytes = headSpan.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 0); // checkSumAdjustment
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(50, 2), useLongLoca ? (short)1 : (short)0);
        return bytes;
    }

    private static byte[] EmitHhea(OpenTypeFont font, ushort numberOfHMetrics)
    {
        var hheaSpan = font.Directory.GetTableBytes(OpenTypeTags.Hhea, font.FontBytes.Span);
        var bytes = hheaSpan.ToArray();
        // numberOfHMetrics is the last uint16 of the 36-byte hhea table.
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(34, 2), numberOfHMetrics);
        return bytes;
    }

    private static byte[] EmitMaxp(OpenTypeFont font, ushort numGlyphs)
    {
        var maxpSpan = font.Directory.GetTableBytes(OpenTypeTags.Maxp, font.FontBytes.Span);
        var bytes = maxpSpan.ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4, 2), numGlyphs);
        return bytes;
    }
}
