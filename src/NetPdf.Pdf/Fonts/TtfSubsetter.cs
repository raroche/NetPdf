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
/// <b>Trust boundary.</b> <see cref="Subset"/> is the single entry point for any caller
/// that wants subset bytes. It runs <see cref="GlyphSubsetPlan.Validate"/> first so a
/// hand-constructed or out-of-band plan can't slip past — every old→new id correspondence
/// and structural invariant (NumGlyphs in [1, 65535], glyph 0 at new id 0, no duplicates,
/// every old id in font.Maxp range) is checked before any byte emission.
/// </para>
/// <para>
/// <b>Composite glyph rewriting.</b> When a composite glyph appears in the subset, its
/// component records reference other glyphs by original id. The shared
/// <see cref="CompositeGlyph"/> walker locates every glyphIndex byte offset and the
/// emitter overwrites them with the new ids from <see cref="GlyphSubsetPlan.OldToNew"/>
/// while leaving every other byte (flags / args / transform / instructions) byte-identical.
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
        plan.Validate(font);

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

        // 7. Resolve the source font's PostScript / family name for the BaseFont. PostScript
        //    names from the font are already ASCII-clean, but FamilyName fallbacks can carry
        //    arbitrary Unicode (CJK fonts, decorative scripts) that's invalid in PDF names —
        //    sanitize through PostScriptName.Sanitize so the embedded BaseFont stays
        //    spec-clean and viewer-portable.
        var rawName = font.Name.PostScriptName ?? font.Name.FamilyName ?? "Subset";
        var sourceName = PostScriptName.Sanitize(rawName);
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
            CompositeGlyph.EnsureValidHeader(glyphBytes, oldId);

            if (glyphBytes.Length == 0)
            {
                // Empty glyph (e.g. .notdef in some fonts, whitespace). loca offsets just
                // record the same boundary twice — no bytes emitted.
                continue;
            }

            // Composite glyphs need their component glyphIndex fields rewritten; simple
            // glyphs are byte-identical.
            var rewritten = CompositeGlyph.IsComposite(glyphBytes)
                ? RewriteCompositeComponents(glyphBytes, plan)
                : glyphBytes.ToArray();
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

    private static byte[] RewriteCompositeComponents(ReadOnlySpan<byte> glyphBytes, GlyphSubsetPlan plan)
    {
        // Walk via the shared validator + locate every glyphIndex byte offset, then patch
        // those offsets in place. Header / args / transform / instructions stay byte-equal,
        // so the glyph's geometry is preserved bit-for-bit.
        var locations = CompositeGlyph.EnumerateComponents(glyphBytes);
        var output = glyphBytes.ToArray();
        foreach (var location in locations)
        {
            if (!plan.OldToNew.TryGetValue(location.GlyphIndex, out var newComponent))
            {
                throw new InvalidOperationException(
                    $"Composite component glyph {location.GlyphIndex} is missing from the subset plan — " +
                    "GlyphSubsetPlan.Build should have caught this; there is a bug in the composite chase.");
            }
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(location.GlyphIndexByteOffset, 2), (ushort)newComponent);
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
