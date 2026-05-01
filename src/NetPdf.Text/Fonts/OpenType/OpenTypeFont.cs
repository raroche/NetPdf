// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Fonts.OpenType;

/// <summary>
/// Parsed OpenType / TrueType font. Phase 1 covers all 10 tables required by the PDF
/// emitter (head, hhea, hmtx, maxp, name, OS/2, post, cmap, loca, glyf — TTF outlines).
/// CFF (PostScript outlines) is out of scope for Task 6 and lands in Task 7.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Parse"/> reads the SFNT directory and every required table once; the
/// resulting object is immutable and safe for concurrent reads (each page rendered in
/// parallel can shape against the same instance).
/// </para>
/// <para>
/// <see cref="FontBytes"/> is held as <see cref="ReadOnlyMemory{Byte}"/> so subsequent
/// stages (subsetter, ToUnicode CMap generator) can re-slice without re-parsing.
/// </para>
/// </remarks>
internal sealed class OpenTypeFont
{
    public required ReadOnlyMemory<byte> FontBytes { get; init; }
    public required TableDirectory Directory { get; init; }
    public required HeadTable Head { get; init; }
    public required HheaTable Hhea { get; init; }
    public required MaxpTable Maxp { get; init; }
    public required Os2Table Os2 { get; init; }
    public required PostTable Post { get; init; }
    public required NameTable Name { get; init; }
    public required HmtxTable Hmtx { get; init; }
    public required CmapTable Cmap { get; init; }

    /// <summary>TTF outlines only. Null on CFF fonts (which carry glyphs in the <c>CFF </c> table — Phase 1 Task 7).</summary>
    public LocaTable? Loca { get; init; }
    public GlyfTable? Glyf { get; init; }

    public bool HasTrueTypeOutlines => Loca is not null && Glyf is not null;

    public static OpenTypeFont Parse(ReadOnlyMemory<byte> fontBytes)
    {
        if (fontBytes.IsEmpty)
        {
            throw new ArgumentException("Font bytes must not be empty.", nameof(fontBytes));
        }

        var span = fontBytes.Span;
        var directory = TableDirectory.Parse(span);

        var head = HeadTable.Parse(directory.GetTableBytes(OpenTypeTags.Head, span));
        var hhea = HheaTable.Parse(directory.GetTableBytes(OpenTypeTags.Hhea, span));
        var maxp = MaxpTable.Parse(directory.GetTableBytes(OpenTypeTags.Maxp, span));
        var os2 = Os2Table.Parse(directory.GetTableBytes(OpenTypeTags.Os2, span));
        var post = PostTable.Parse(directory.GetTableBytes(OpenTypeTags.Post, span));
        var name = NameTable.Parse(directory.GetTableBytes(OpenTypeTags.Name, span));
        var hmtx = HmtxTable.Parse(directory.GetTableBytes(OpenTypeTags.Hmtx, span), hhea.NumberOfHMetrics, maxp.NumGlyphs);
        var cmap = CmapTable.Parse(directory.GetTableBytes(OpenTypeTags.Cmap, span));

        LocaTable? loca = null;
        GlyfTable? glyf = null;
        if (directory.IsTrueType)
        {
            var locaBytes = directory.GetTableBytes(OpenTypeTags.Loca, span);
            loca = LocaTable.Parse(locaBytes, maxp.NumGlyphs, head.IndexToLocFormat);

            // Slice glyf via the directory record (NOT via loca offsets — they're relative to glyf).
            if (!directory.TryGetRecord(OpenTypeTags.Glyf, out var glyfRecord))
            {
                throw new InvalidDataException("TTF font is missing required 'glyf' table.");
            }
            if ((long)glyfRecord.Offset + glyfRecord.Length > fontBytes.Length)
            {
                throw new InvalidDataException(
                    $"glyf: table at offset {glyfRecord.Offset} length {glyfRecord.Length} " +
                    $"extends past font end ({fontBytes.Length}).");
            }
            var glyfMemory = fontBytes.Slice((int)glyfRecord.Offset, (int)glyfRecord.Length);
            glyf = new GlyfTable(loca) { RawBytes = glyfMemory };
        }

        return new OpenTypeFont
        {
            FontBytes = fontBytes,
            Directory = directory,
            Head = head,
            Hhea = hhea,
            Maxp = maxp,
            Os2 = os2,
            Post = post,
            Name = name,
            Hmtx = hmtx,
            Cmap = cmap,
            Loca = loca,
            Glyf = glyf,
        };
    }
}
