// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
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
/// <b>Embedding policy.</b> The <c>OS/2</c> table's <c>fsType</c> field declares the font
/// vendor's embedding-permission policy (OpenType §"OS/2 — fsType"). <see cref="Build"/>
/// rejects fonts marked <i>restricted-license embedding</i> (bit 1), <i>no-subsetting</i>
/// (bit 8), or <i>bitmap-only embedding</i> (bit 9). The remaining categories
/// (installable, preview-print, editable) are allowed. Honoring fsType is a legal /
/// licensing requirement; silently embedding a restricted-license font is a compliance
/// violation regardless of how the font reached our hands.
/// </para>
/// <para>
/// <b>Trust boundary.</b> Build runs a <see cref="Validate"/> preflight before any byte
/// production: the plan is validated against the source font, subset table lengths must
/// match the declared glyph count, and every ToUnicode key must fall within the subset
/// CID range. Catches hand-built or out-of-band <see cref="TtfSubsetResult"/> /
/// <see cref="ToUnicodeCMap"/> values before they produce broken PDF font dictionaries.
/// </para>
/// <para>
/// <b>Tables embedded in the SFNT.</b> ISO 32000-2 §9.7.4.2 lets us strip tables that the
/// PDF reader doesn't consume when <c>/CIDToGIDMap /Identity</c> is used:
/// </para>
/// <list type="bullet">
/// <item>Required: <c>head</c>, <c>hhea</c>, <c>hmtx</c>, <c>maxp</c>, <c>glyf</c>, <c>loca</c>.</item>
/// <item>Useful for the FontDescriptor descriptor: <c>OS/2</c>, <c>name</c>, <c>post</c>.</item>
/// <item>Optional hinting tables (<c>cvt</c>, <c>fpgm</c>, <c>prep</c>, <c>gasp</c>) carried through when present — they improve rendering at small sizes in viewers that honor TrueType hinting.</item>
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
/// optimization. Per-glyph advance widths are read as <see cref="ushort"/> so values
/// above 32767 (legitimately used by some fonts for wide glyphs) survive scaling.
/// </para>
/// </remarks>
internal static class EmbeddedTtfFont
{
    private const int GlyphSpaceUnitsPerEm = 1000;

    /// <summary>The OS/2.fsSelection italic bit (bit 0).</summary>
    private const ushort FsSelectionItalic = 0x0001;

    /// <summary>head.macStyle italic bit (bit 1).</summary>
    private const ushort MacStyleItalic = 0x0002;

    // OS/2 fsType embedding-permission bits per OpenType "OS/2" §"fsType".
    private const ushort FsTypeRestrictedLicense = 0x0002;
    private const ushort FsTypeNoSubsetting = 0x0100;
    private const ushort FsTypeBitmapOnly = 0x0200;

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

        EnforceEmbeddingPolicy(sourceFont);
        Validate(sourceFont, subset, toUnicode);

        // 1. Build the SFNT envelope from subset + pass-through tables.
        var sfntBytes = BuildSfntEnvelope(sourceFont, subset);

        // 2. /FontFile2 — uncompressed SFNT in a stream with /Length1.
        var fontFile2 = BuildFontFile2Stream(sfntBytes);

        // 3. /ToUnicode — CMap stream (uncompressed for Phase 1; FlateDecode arrives with the embedder polish pass).
        var toUnicodeBytes = toUnicode.Emit();
        var toUnicodeStream = new PdfStream(toUnicodeBytes);

        // 4. /FontDescriptor.
        var unitsPerEm = sourceFont.Head.UnitsPerEm;
        var descriptor = BuildFontDescriptor(sourceFont, subset, fontFile2, unitsPerEm);

        // 5. CIDFontType2 (the descendant font).
        var cidFont = BuildCidFont(subset, descriptor, unitsPerEm);

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

    private static void EnforceEmbeddingPolicy(OpenTypeFont sourceFont)
    {
        var fsType = sourceFont.Os2.FsType;
        if ((fsType & FsTypeRestrictedLicense) != 0)
        {
            throw new InvalidOperationException(
                "Font is licensed for restricted-license embedding only (OS/2.fsType bit 1). " +
                "Embedding requires permission from the font vendor; not embedding by default.");
        }
        if ((fsType & FsTypeNoSubsetting) != 0)
        {
            throw new InvalidOperationException(
                "Font has the no-subsetting bit set (OS/2.fsType bit 8). " +
                "We always subset; cannot embed this font without violating its license.");
        }
        if ((fsType & FsTypeBitmapOnly) != 0)
        {
            throw new InvalidOperationException(
                "Font has the bitmap-only embedding bit set (OS/2.fsType bit 9). " +
                "Our outline-based path is incompatible with bitmap-only embedding.");
        }
    }

    /// <summary>
    /// Cross-component preflight: subset bytes must match the plan's declared glyph count
    /// at structural-length level, and every ToUnicode key must be a valid subset CID.
    /// </summary>
    private static void Validate(OpenTypeFont sourceFont, TtfSubsetResult subset, ToUnicodeCMap toUnicode)
    {
        // Plan-vs-font (defense-in-depth — TtfSubsetter already validated, but a hand-built
        // subset would skip that path).
        subset.Plan.Validate(sourceFont);

        if (subset.HeadBytes.Length < 54)
        {
            throw new InvalidOperationException(
                $"Subset head table is {subset.HeadBytes.Length} bytes; need at least 54.");
        }
        if (subset.HheaBytes.Length < 36)
        {
            throw new InvalidOperationException(
                $"Subset hhea table is {subset.HheaBytes.Length} bytes; need at least 36.");
        }
        if (subset.MaxpBytes.Length < 6)
        {
            throw new InvalidOperationException(
                $"Subset maxp table is {subset.MaxpBytes.Length} bytes; need at least 6.");
        }

        // hmtx subsetter emits one long metric per subset glyph (4 bytes each, no lsb-only trail).
        var expectedHmtx = subset.Plan.NumGlyphs * 4;
        if (subset.HmtxBytes.Length != expectedHmtx)
        {
            throw new InvalidOperationException(
                $"Subset hmtx table is {subset.HmtxBytes.Length} bytes; expected {expectedHmtx} for {subset.Plan.NumGlyphs} glyph(s).");
        }

        // loca: subset.NumGlyphs+1 entries × (2 or 4) bytes per entry depending on head.indexToLocFormat.
        var indexToLocFormat = BinaryPrimitives.ReadInt16BigEndian(subset.HeadBytes.Span.Slice(50, 2));
        var locaEntrySize = indexToLocFormat == 0 ? 2 : 4;
        var expectedLoca = (subset.Plan.NumGlyphs + 1) * locaEntrySize;
        if (subset.LocaBytes.Length != expectedLoca)
        {
            throw new InvalidOperationException(
                $"Subset loca table is {subset.LocaBytes.Length} bytes; expected {expectedLoca} for " +
                $"{subset.Plan.NumGlyphs}+1 entr(ies) × {locaEntrySize} byte(s) (indexToLocFormat = {indexToLocFormat}).");
        }

        // ToUnicode keys must fall within the subset CID range. The cmap-fallback factory
        // already produces valid keys, but a hand-built ToUnicode could carry stale entries.
        foreach (var key in toUnicode.SubsetGlyphIdToText.Keys)
        {
            if (key < 0 || key >= subset.Plan.NumGlyphs)
            {
                throw new InvalidOperationException(
                    $"ToUnicode entry maps subset glyph id {key}, but the subset only covers [0, {subset.Plan.NumGlyphs}).");
            }
        }
    }

    private static byte[] BuildSfntEnvelope(OpenTypeFont sourceFont, TtfSubsetResult subset)
    {
        var tables = new Dictionary<uint, ReadOnlyMemory<byte>>(13)
        {
            { OpenTypeTags.Head, subset.HeadBytes },
            { OpenTypeTags.Hhea, subset.HheaBytes },
            { OpenTypeTags.Hmtx, subset.HmtxBytes },
            { OpenTypeTags.Maxp, subset.MaxpBytes },
            { OpenTypeTags.Loca, subset.LocaBytes },
            { OpenTypeTags.Glyf, subset.GlyfBytes },
        };
        // Pass-through tables: descriptor metrics + diagnostics. cmap intentionally
        // stripped (Identity-H + /ToUnicode supersede its role).
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Os2);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Name);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Post);

        // Hinting tables: optional but improve raster fidelity at small sizes in viewers
        // that honor TrueType hinting. Pass-through when present.
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Cvt);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Fpgm);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Prep);
        AddPassThroughTable(tables, sourceFont, OpenTypeTags.Gasp);

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
        PdfStream fontFile2,
        ushort unitsPerEm)
    {
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

    private static PdfDictionary BuildCidFont(TtfSubsetResult subset, PdfDictionary descriptor, ushort unitsPerEm)
    {
        var cidSystemInfo = new PdfDictionary()
            .Set(PdfNames.Registry, new PdfLiteralString("Adobe"))
            .Set(PdfNames.Ordering, new PdfLiteralString("Identity"))
            .Set(PdfNames.Supplement, new PdfInteger(0));

        var widths = BuildWidthsArray(subset, unitsPerEm);

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

    private static PdfArray BuildWidthsArray(TtfSubsetResult subset, ushort unitsPerEm)
    {
        // Phase 1 emits the simple "[0 [w0 w1 … wN]]" form — one CID range starting at 0
        // with explicit per-glyph widths. Range-collapsed forms (run-length encoding for
        // contiguous equal widths) are a future optimization once we benchmark the size win.
        var widths = new PdfArray();
        widths.Add(new PdfInteger(0));

        var perGlyph = new PdfArray();
        var hmtx = subset.HmtxBytes.Span;
        // Subset hmtx is "1 longHorMetric per glyph" so each entry is 4 bytes:
        // uint16 advance + int16 lsb. Read advance as ushort (NOT short — fonts can have
        // legitimate widths > 32767 that would wrap to negatives under signed cast).
        for (var i = 0; i < subset.Plan.NumGlyphs; i++)
        {
            var advanceFontUnits = (ushort)((hmtx[i * 4] << 8) | hmtx[(i * 4) + 1]);
            var advance = ScaleToGlyphSpace(advanceFontUnits, unitsPerEm);
            perGlyph.Add(new PdfInteger(advance));
        }
        widths.Add(perGlyph);
        return widths;
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
