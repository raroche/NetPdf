// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Objects;
using NetPdf.Text.Fonts.OpenType;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Wraps a TTF subset (Task 8) plus its ToUnicode CMap (Task 9) into the PDF font-object
/// tree consumers expect: a Type 0 font with one CIDFontType2 descendant, an
/// <c>Identity-H</c> encoding so glyph ids flow through unchanged, and the embedded
/// SFNT bytes in <c>FontFile2</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tables embedded in the SFNT.</b> ISO 32000-2 §9.7.4.2 lets us strip tables that the
/// PDF reader doesn't consume when <c>/CIDToGIDMap /Identity</c> is used:
/// </para>
/// <list type="bullet">
/// <item>Required: <c>head</c>, <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>, <c>glyf</c>, <c>loca</c>.</item>
/// <item>Useful for the FontDescriptor descriptor: <c>OS/2</c>, <c>name</c>, <c>post</c>.</item>
/// <item>Stripped: <c>cmap</c> — Identity-H bypasses it; the PDF's <c>/ToUnicode</c> handles text extraction.</item>
/// </list>
/// <para>
/// <b>FontDescriptor metrics</b> are scaled from font-design-units to PDF glyph space
/// (1/1000 em). <c>/StemV</c> is estimated from <c>OS/2.usWeightClass</c> since OpenType
/// has no direct stem-width field. <c>/Flags</c> is a conservative default
/// (<c>Symbolic</c> bit set, <c>Italic</c> bit derived from <c>head.macStyle</c>) — refining
/// it to mark fonts as Nonsymbolic when the subset is Latin-only is a Phase 1.x detail.
/// </para>
/// <para>
/// <b>/W array</b> emits one width per subset glyph in the compact "[0 [w0 w1 … wN]]"
/// form. Range-collapsed forms ("[a b w]" for runs of equal-width glyphs) are a future
/// optimization.
/// </para>
/// </remarks>
internal static class EmbeddedTtfFont
{
    private const int GlyphSpaceUnitsPerEm = 1000;

    /// <summary>The OS/2.fsSelection italic bit (bit 0).</summary>
    private const ushort FsSelectionItalic = 0x0001;

    /// <summary>head.macStyle italic bit (bit 1).</summary>
    private const ushort MacStyleItalic = 0x0002;

    // FontDescriptor /Flags bits per ISO 32000-2 §9.8.2 Table 117.
    private const int FlagFixedPitch = 1 << 0;
    private const int FlagSymbolic = 1 << 2;
    private const int FlagItalic = 1 << 6;

    public static EmbeddedFont Build(OpenTypeFont sourceFont, TtfSubsetResult subset, ToUnicodeCMap toUnicode)
    {
        ArgumentNullException.ThrowIfNull(sourceFont);
        ArgumentNullException.ThrowIfNull(subset);
        ArgumentNullException.ThrowIfNull(toUnicode);
        if (!sourceFont.HasTrueTypeOutlines)
        {
            throw new InvalidOperationException("EmbeddedTtfFont.Build requires a TTF-flavored source font.");
        }

        // 1. Build the SFNT envelope from subset + pass-through tables.
        var sfntBytes = BuildSfntEnvelope(sourceFont, subset);

        // 2. /FontFile2 — uncompressed SFNT in a stream with /Length1.
        var fontFile2 = BuildFontFile2Stream(sfntBytes);

        // 3. /ToUnicode — CMap stream (uncompressed for Phase 1; FlateDecode arrives with the embedder polish pass).
        var toUnicodeBytes = toUnicode.Emit();
        var toUnicodeStream = new PdfStream(toUnicodeBytes);

        // 4. /FontDescriptor.
        var descriptor = BuildFontDescriptor(sourceFont, subset, fontFile2);

        // 5. CIDFontType2 (the descendant font).
        var cidFont = BuildCidFont(subset, descriptor);

        // 6. Type 0 wrapper.
        var type0 = BuildType0Font(subset, cidFont, toUnicodeStream);

        return new EmbeddedFont
        {
            SubsetBaseFontName = subset.SubsetBaseFontName,
            Type0FontDictionary = type0,
            CidFontDictionary = cidFont,
            FontDescriptorDictionary = descriptor,
            FontFile2Stream = fontFile2,
            ToUnicodeStream = toUnicodeStream,
        };
    }

    private static byte[] BuildSfntEnvelope(OpenTypeFont sourceFont, TtfSubsetResult subset)
    {
        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(9)
        {
            { OpenTypeTags.Head, subset.HeadBytes },
            { OpenTypeTags.Hhea, subset.HheaBytes },
            { OpenTypeTags.Hmtx, subset.HmtxBytes },
            { OpenTypeTags.Maxp, subset.MaxpBytes },
            { OpenTypeTags.Loca, subset.LocaBytes },
            { OpenTypeTags.Glyf, subset.GlyfBytes },
        };
        // Pass-through tables — copied verbatim from source. cmap intentionally stripped
        // (Identity-H + /ToUnicode supersedes its role).
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Os2);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Name);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Post);

        return SfntEnvelopeBuilder.BuildTtf(tables);
    }

    private static void AddPassThroughTable(
        Dictionary<uint, ReadOnlyMemory<byte>> tables,
        OpenTypeFont sourceFont,
        uint tag)
    {
        if (!sourceFont.Directory.TryGetRecord(tag, out var record))
        {
            return; // OpenType makes some of these required, but we tolerate missing entries
        }
        var bytes = sourceFont.FontBytes.Slice((int)record.Offset, (int)record.Length);
        tables[tag] = bytes;
    }

    private static PdfStream BuildFontFile2Stream(byte[] sfntBytes)
    {
        // /Length1 is the uncompressed length of the SFNT (PDF spec for FontFile2).
        // We don't compress here in Phase 1 — leaves room for the embedder polish pass to
        // wrap with FlateDecode if benchmarks show the file-size win is worth it.
        var dict = new PdfDictionary().Set(PdfNames.Length1, new PdfInteger(sfntBytes.Length));
        return new PdfStream(sfntBytes, dict);
    }

    private static PdfDictionary BuildFontDescriptor(
        OpenTypeFont sourceFont,
        TtfSubsetResult subset,
        PdfStream fontFile2)
    {
        var unitsPerEm = sourceFont.Head.UnitsPerEm;
        var bbox = new PdfArray()
            .Add(new PdfInteger(ScaleToGlyphSpace(sourceFont.Head.XMin, unitsPerEm)))
            .Add(new PdfInteger(ScaleToGlyphSpace(sourceFont.Head.YMin, unitsPerEm)))
            .Add(new PdfInteger(ScaleToGlyphSpace(sourceFont.Head.XMax, unitsPerEm)))
            .Add(new PdfInteger(ScaleToGlyphSpace(sourceFont.Head.YMax, unitsPerEm)));

        var italicAngle = ConvertItalicAngle(sourceFont.Post.ItalicAngle);
        var ascent = ScaleToGlyphSpace(sourceFont.Os2.STypoAscender, unitsPerEm);
        var descent = ScaleToGlyphSpace(sourceFont.Os2.STypoDescender, unitsPerEm);
        var capHeight = sourceFont.Os2.SCapHeight is short cap
            ? ScaleToGlyphSpace(cap, unitsPerEm)
            : ascent;
        var xHeight = sourceFont.Os2.SxHeight is short xh
            ? ScaleToGlyphSpace(xh, unitsPerEm)
            : ascent / 2;
        var stemV = EstimateStemV(sourceFont.Os2.UsWeightClass);
        var flags = ComputeFlags(sourceFont);

        var descriptor = new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.FontDescriptor)
            .Set(PdfNames.FontName, new PdfName(subset.SubsetBaseFontName))
            .Set(PdfNames.Flags, new PdfInteger(flags))
            .Set(PdfNames.FontBBox, bbox)
            .Set(PdfNames.ItalicAngle, new PdfReal(italicAngle))
            .Set(PdfNames.Ascent, new PdfInteger(ascent))
            .Set(PdfNames.Descent, new PdfInteger(descent))
            .Set(PdfNames.CapHeight, new PdfInteger(capHeight))
            .Set(PdfNames.XHeight, new PdfInteger(xHeight))
            .Set(PdfNames.StemV, new PdfInteger(stemV))
            .Set(PdfNames.FontFile2, fontFile2);

        return descriptor;
    }

    private static PdfDictionary BuildCidFont(TtfSubsetResult subset, PdfDictionary descriptor)
    {
        var cidSystemInfo = new PdfDictionary()
            .Set(PdfNames.Registry, new PdfLiteralString("Adobe"))
            .Set(PdfNames.Ordering, new PdfLiteralString("Identity"))
            .Set(PdfNames.Supplement, new PdfInteger(0));

        var widths = BuildWidthsArray(subset);

        return new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Font)
            .Set(PdfNames.Subtype, PdfNames.CIDFontType2)
            .Set(PdfNames.BaseFont, new PdfName(subset.SubsetBaseFontName))
            .Set(PdfNames.CIDSystemInfo, cidSystemInfo)
            .Set(PdfNames.FontDescriptor, descriptor)
            .Set(PdfNames.W, widths)
            .Set(PdfNames.CIDToGIDMap, PdfNames.Identity);
    }

    private static PdfDictionary BuildType0Font(
        TtfSubsetResult subset,
        PdfDictionary cidFont,
        PdfStream toUnicodeStream)
    {
        var descendants = new PdfArray().Add(cidFont);

        return new PdfDictionary()
            .Set(PdfNames.Type, PdfNames.Font)
            .Set(PdfNames.Subtype, PdfNames.Type0)
            .Set(PdfNames.BaseFont, new PdfName(subset.SubsetBaseFontName))
            .Set(PdfNames.Encoding, PdfNames.IdentityH)
            .Set(PdfNames.DescendantFonts, descendants)
            .Set(PdfNames.ToUnicode, toUnicodeStream);
    }

    private static PdfArray BuildWidthsArray(TtfSubsetResult subset)
    {
        // Phase 1 emits the simple "[0 [w0 w1 … wN]]" form — one CID range starting at 0
        // with explicit per-glyph widths. Range-collapsed forms (run-length encoding for
        // contiguous equal widths) are a future optimization once we benchmark the size win.
        var widths = new PdfArray();
        widths.Add(new PdfInteger(0));

        var perGlyph = new PdfArray();
        var hmtx = subset.HmtxBytes.Span;
        // Subset hmtx is "1 longHorMetric per glyph" so each entry is 4 bytes:
        // uint16 advance + int16 lsb.
        for (var i = 0; i < subset.Plan.NumGlyphs; i++)
        {
            var advanceFontUnits = (hmtx[i * 4] << 8) | hmtx[(i * 4) + 1];
            var advance = ScaleToGlyphSpace((short)advanceFontUnits, GetUnitsPerEmFromHead(subset.HeadBytes.Span));
            perGlyph.Add(new PdfInteger(advance));
        }
        widths.Add(perGlyph);
        return widths;
    }

    private static ushort GetUnitsPerEmFromHead(ReadOnlySpan<byte> headBytes)
    {
        // unitsPerEm sits at offset 18 of head (uint16).
        return (ushort)((headBytes[18] << 8) | headBytes[19]);
    }

    /// <summary>Scale a font-design-units value to PDF glyph space (1000 units / em).</summary>
    private static int ScaleToGlyphSpace(int fontUnits, ushort unitsPerEm)
    {
        if (unitsPerEm == 0)
        {
            return 0;
        }
        return (int)Math.Round(fontUnits * (double)GlyphSpaceUnitsPerEm / unitsPerEm);
    }

    private static double ConvertItalicAngle(uint rawFixed)
    {
        // post.italicAngle is Fixed (signed 16.16). Reinterpret as int32 then divide.
        return unchecked((int)rawFixed) / 65536.0;
    }

    private static int EstimateStemV(ushort usWeightClass)
    {
        // No direct StemV in OpenType; estimate from weight class. Per PDF spec § Table 122,
        // typical values: Light ~ 50, Normal ~ 80–100, Bold ~ 150–180. The exact number
        // affects only on-screen rendering hints; viewers tolerate a wide range.
        return usWeightClass switch
        {
            < 350 => 50,
            < 450 => 70,
            < 550 => 80,
            < 650 => 100,
            < 750 => 120,
            _ => 140,
        };
    }

    private static int ComputeFlags(OpenTypeFont sourceFont)
    {
        // Phase 1 baseline: Symbolic by default (safe for arbitrary scripts), with FixedPitch
        // and Italic derived from font tables. Phase 1.x can refine to Nonsymbolic when the
        // subset is provably Latin-only (every cmap entry < U+0080).
        var flags = FlagSymbolic;
        if (sourceFont.Post.IsMonospaced)
        {
            flags |= FlagFixedPitch;
        }
        var italic = (sourceFont.Head.MacStyle & MacStyleItalic) != 0
            || (sourceFont.Os2.FsSelection & FsSelectionItalic) != 0;
        if (italic)
        {
            flags |= FlagItalic;
        }
        return flags;
    }
}
