// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using NetPdf.Pdf.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Post-Task-8 hardening: <see cref="TtfSubsetter.Subset"/> calls
/// <see cref="GlyphSubsetPlan.Validate"/> first so structurally bad plans never reach
/// byte emission, and the emitter rejects malformed source glyph data.
/// </summary>
public sealed class TtfSubsetterHardeningTests
{
    [Fact]
    public void Subset_runs_plan_preflight_before_emission()
    {
        var font = OpenTypeFont.Parse(SyntheticFont.Build());
        // Plan with glyph 0 not at new id 0 — Validate catches this; the subsetter does
        // not need to invent its own check beyond delegating to the plan.
        var bad = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 1, 0 },
            OldToNew = new Dictionary<int, int> { { 1, 0 }, { 0, 1 } },
        };
        Assert.Throws<InvalidOperationException>(() => TtfSubsetter.Subset(font, bad));
    }

    [Fact]
    public void Subset_rejects_cross_font_plan()
    {
        var smallFont = OpenTypeFont.Parse(SyntheticFont.Build());
        var largerFont = OpenTypeFont.Parse(SyntheticFontWithComposite.Build());
        var planFromLargerFont = GlyphSubsetPlan.Build(largerFont, new HashSet<int> { 3 });
        // Plan was built against the larger font — its glyph id 3 is out of range for the
        // smaller (3-glyph) font. Validate catches this via the per-id range check.
        Assert.Throws<InvalidOperationException>(() => TtfSubsetter.Subset(smallFont, planFromLargerFont));
    }

    [Fact]
    public void Subset_rejects_source_font_with_short_non_empty_glyph_data()
    {
        // Build a TTF with glyph 1 carrying 5 bytes (less than the 10-byte header). We
        // construct the plan by hand so we bypass GlyphSubsetPlan.Build's own short-glyph
        // guard — the test then proves the EmitGlyf path also rejects, providing
        // defense-in-depth at the trust boundary.
        var fontBytes = BuildFontWithShortGlyph(payloadLength: 5);
        var font = OpenTypeFont.Parse(fontBytes);
        var plan = new GlyphSubsetPlan
        {
            OrderedOldGlyphIds = new[] { 0, 1 },
            OldToNew = new Dictionary<int, int> { { 0, 0 }, { 1, 1 } },
        };
        Assert.Throws<InvalidDataException>(() => TtfSubsetter.Subset(font, plan));
    }

    [Fact]
    public void Plan_build_also_rejects_short_non_empty_glyph_data()
    {
        // Same source font as above, but going through the planner. EnsureValidHeader is
        // shared between the planner and the emitter, so both call sites enforce the rule.
        var fontBytes = BuildFontWithShortGlyph(payloadLength: 5);
        var font = OpenTypeFont.Parse(fontBytes);
        Assert.Throws<InvalidDataException>(() => GlyphSubsetPlan.Build(font, new HashSet<int> { 1 }));
    }

    /// <summary>
    /// Construct a 3-glyph TTF identical in shape to <see cref="SyntheticFont"/> except
    /// glyph 1's payload is <paramref name="payloadLength"/> bytes (less than the 10-byte
    /// header). Keeping numGlyphs at 3 sidesteps the round-6 cmap → maxp cross-table check.
    /// </summary>
    private static byte[] BuildFontWithShortGlyph(int payloadLength)
    {
        var head = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HeadBytes();
        var hhea = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HheaBytes();
        var maxp = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.MaxpBytes();
        var os2 = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.Os2Bytes();
        var post = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.PostBytes();
        var name = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.NameBytes();
        var cmap = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.CmapBytes();
        var hmtx = NetPdf.UnitTests.Text.Fonts.OpenType.SyntheticFont.HmtxBytes();

        // loca (long): glyph 0 empty (0..0), glyph 1 has `payloadLength` bytes (0..payloadLength), glyph 2 empty.
        var loca = new byte[4 * 4];
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(8, 4), (uint)payloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(12, 4), (uint)payloadLength);

        var glyf = new byte[payloadLength]; // intentionally short — no valid header

        return BuildSfnt(new (uint, byte[])[]
        {
            (0x4F532F32u, os2),
            (0x636D6170u, cmap),
            (0x676C7966u, glyf),
            (0x68656164u, head),
            (0x68686561u, hhea),
            (0x686D7478u, hmtx),
            (0x6C6F6361u, loca),
            (0x6D617870u, maxp),
            (0x6E616D65u, name),
            (0x706F7374u, post),
        });
    }

    private static byte[] BuildSfnt((uint Tag, byte[] Bytes)[] tables)
    {
        Array.Sort(tables, (a, b) => a.Tag.CompareTo(b.Tag));
        const int sfntHeaderSize = 12;
        const int recordSize = 16;
        var firstTableOffset = sfntHeaderSize + (recordSize * tables.Length);

        var offsets = new int[tables.Length];
        var cursor = firstTableOffset;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = cursor;
            cursor += AlignTo4(tables[i].Bytes.Length);
        }
        var output = new byte[cursor];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span[..4], 0x00010000u);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], (ushort)tables.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 128);
        BinaryPrimitives.WriteUInt16BigEndian(span[8..10], 3);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], (ushort)((tables.Length * 16) - 128));

        var directoryCursor = sfntHeaderSize;
        for (var i = 0; i < tables.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[directoryCursor..(directoryCursor + 4)], tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 4)..(directoryCursor + 8)], 0);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 8)..(directoryCursor + 12)], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(directoryCursor + 12)..(directoryCursor + 16)], (uint)tables[i].Bytes.Length);
            directoryCursor += recordSize;
        }
        for (var i = 0; i < tables.Length; i++)
        {
            tables[i].Bytes.CopyTo(span[offsets[i]..]);
        }
        return output;
    }

    private static int AlignTo4(int length) => (length + 3) & ~3;
}
