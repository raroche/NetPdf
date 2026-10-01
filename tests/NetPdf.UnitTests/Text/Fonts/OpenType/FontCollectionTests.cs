// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Text;
using NetPdf;
using NetPdf.Text.Fonts;
using NetPdf.Text.Fonts.OpenType;
using NetPdf.Text.Fonts.SystemFonts;
using Xunit;

namespace NetPdf.UnitTests.Text.Fonts.OpenType;

/// <summary>
/// Font collections (<c>.ttc</c> / <c>.otc</c>, OpenType §"Font Collections"). Before this support the
/// system-font indexer skipped every collection file, so families that ship only as a collection —
/// on macOS that includes Helvetica, Helvetica Neue, Avenir, Menlo and Optima — could not be used, and
/// a document asking for them fell back to a different (often wider) font. Driven with a synthetic
/// collection built from <see cref="SyntheticFont"/>'s tables: two usable faces (Regular 400 and Bold
/// 700, sharing every table except <c>name</c> and <c>OS/2</c>), a face with a rejected bitmap table,
/// and a face with no <c>OS/2</c>.
/// </summary>
public sealed class FontCollectionTests : IDisposable
{
    private const string Family = "Synth Coll";
    private const uint Sbix = 0x73626978u; // "sbix"

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "netpdf-ttc-" + Guid.NewGuid().ToString("N"));

    public FontCollectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ── collection builder ────────────────────────────────────────────────────────────────

    private static byte[] NameTableBytes(string family, string subfamily, string postScript)
    {
        var strings = new (ushort Id, byte[] Bytes)[]
        {
            (1, Encoding.BigEndianUnicode.GetBytes(family)),
            (2, Encoding.BigEndianUnicode.GetBytes(subfamily)),
            (6, Encoding.BigEndianUnicode.GetBytes(postScript)),
        };
        var headerSize = 6 + 12 * strings.Length;
        var bytes = new byte[headerSize + strings.Sum(s => s.Bytes.Length)];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)strings.Length);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)headerSize);
        var stringOffset = 0;
        for (var i = 0; i < strings.Length; i++)
        {
            var rec = span.Slice(6 + 12 * i, 12);
            BinaryPrimitives.WriteUInt16BigEndian(rec, 3);            // Windows
            BinaryPrimitives.WriteUInt16BigEndian(rec[2..], 1);       // Unicode BMP
            BinaryPrimitives.WriteUInt16BigEndian(rec[4..], 0x0409);  // en-US
            BinaryPrimitives.WriteUInt16BigEndian(rec[6..], strings[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(rec[8..], (ushort)strings[i].Bytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(rec[10..], (ushort)stringOffset);
            strings[i].Bytes.CopyTo(span[(headerSize + stringOffset)..]);
            stringOffset += strings[i].Bytes.Length;
        }
        return bytes;
    }

    private static byte[] Os2WithWeight(ushort weight)
    {
        var os2 = SyntheticFont.Os2Bytes();
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), weight);
        if (weight >= 700) BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), 0x0020); // fsSelection BOLD
        return os2;
    }

    /// <summary>A version-1.0 collection. Every face references the SAME shared table data (written once,
    /// as real collections do); each face adds its own extra tables.</summary>
    private static byte[] BuildCollection(params (uint Tag, byte[] Bytes)[][] perFaceTables)
    {
        var shared = new (uint Tag, byte[] Bytes)[]
        {
            (OpenTypeTags.Cmap, SyntheticFont.CmapBytes()),
            (OpenTypeTags.Glyf, SyntheticFont.GlyfBytes()),
            (OpenTypeTags.Head, SyntheticFont.HeadBytes()),
            (OpenTypeTags.Hhea, SyntheticFont.HheaBytes()),
            (OpenTypeTags.Hmtx, SyntheticFont.HmtxBytes()),
            (OpenTypeTags.Loca, SyntheticFont.LocaBytes()),
            (OpenTypeTags.Maxp, SyntheticFont.MaxpBytes()),
            (OpenTypeTags.Post, SyntheticFont.PostBytes()),
        };
        var numFaces = perFaceTables.Length;
        var cursor = 12 + 4 * numFaces;
        var faceDirOffsets = new int[numFaces];
        for (var f = 0; f < numFaces; f++)
        {
            faceDirOffsets[f] = cursor;
            cursor += 12 + 16 * (shared.Length + perFaceTables[f].Length);
        }
        var data = new List<byte>();
        int Place(byte[] bytes)
        {
            var at = cursor + data.Count;
            data.AddRange(bytes);
            while (data.Count % 4 != 0) data.Add(0);
            return at;
        }
        var sharedOffsets = shared.Select(t => Place(t.Bytes)).ToArray();
        var faceOffsets = perFaceTables.Select(face => face.Select(t => Place(t.Bytes)).ToArray()).ToArray();

        var output = new byte[cursor + data.Count];
        var span = output.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, FontCollection.CollectionTag);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 0x00010000u);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], (uint)numFaces);
        for (var f = 0; f < numFaces; f++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span[(12 + 4 * f)..], (uint)faceDirOffsets[f]);
            var records = shared.Select((t, i) => (t.Tag, Offset: sharedOffsets[i], t.Bytes.Length))
                .Concat(perFaceTables[f].Select((t, i) => (t.Tag, Offset: faceOffsets[f][i], t.Bytes.Length)))
                .ToArray();
            var dir = span[faceDirOffsets[f]..];
            BinaryPrimitives.WriteUInt32BigEndian(dir, OpenTypeTags.SfntVersionTtf);
            BinaryPrimitives.WriteUInt16BigEndian(dir[4..], (ushort)records.Length);
            for (var r = 0; r < records.Length; r++)
            {
                var rec = dir.Slice(12 + 16 * r, 16);
                BinaryPrimitives.WriteUInt32BigEndian(rec, records[r].Tag);
                BinaryPrimitives.WriteUInt32BigEndian(rec[8..], (uint)records[r].Offset);
                BinaryPrimitives.WriteUInt32BigEndian(rec[12..], (uint)records[r].Length);
            }
        }
        data.ToArray().CopyTo(span[cursor..]);
        return output;
    }

    private static (uint, byte[])[] Face(string sub, string ps, ushort weight) =>
        [(OpenTypeTags.Name, NameTableBytes(Family, sub, ps)), (OpenTypeTags.Os2, Os2WithWeight(weight))];

    /// <summary>Regular, Bold, a face with an <c>sbix</c> bitmap table, a face without <c>OS/2</c>.</summary>
    private static byte[] StandardCollection() => BuildCollection(
        Face("Regular", "SynthColl-Regular", 400),
        Face("Bold", "SynthColl-Bold", 700),
        [.. Face("Color", "SynthColl-Color", 500), (Sbix, new byte[16])],
        [(OpenTypeTags.Name, NameTableBytes(Family, "NoOs2", "SynthColl-NoOs2"))]);

    private string WriteCollection(byte[] bytes, string name = "synth.ttc")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static uint WholeFileChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 4 <= data.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(data[i..]));
        return sum;
    }

    // ── extraction ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "SynthColl-Regular", 400)]
    [InlineData(1, "SynthColl-Bold", 700)]
    public void Extracted_face_is_a_valid_standalone_font(int faceIndex, string postScript, int weight)
    {
        var face = FontCollection.ExtractFace(StandardCollection(), faceIndex);

        Assert.True(FontSafetyValidator.Validate(face).IsSafe);
        var font = OpenTypeFont.Parse(face);
        var meta = FontMetadata.Extract(font);
        Assert.Equal(Family, meta.FamilyName);
        Assert.Equal(postScript, meta.PostScriptName);
        Assert.Equal(weight, meta.WeightCss);
        Assert.Equal(SyntheticFont.NumGlyphs, font.Maxp.NumGlyphs);
        // The shared glyph data came along: 'A' maps to glyph 1, as in the source font.
        Assert.Equal(1, font.Cmap.GetGlyphId('A'));
        // head.checkSumAdjustment is recomputed for the new file (OpenType §"head").
        Assert.Equal(0xB1B0AFBAu, WholeFileChecksum(face));
    }

    [Fact]
    public void Extracted_directory_is_tag_sorted_and_tables_are_four_byte_aligned()
    {
        var face = FontCollection.ExtractFace(StandardCollection(), 1);
        var dir = TableDirectory.Parse(face);
        var tags = dir.Tables.Keys.Order().ToArray();
        for (var i = 0; i < dir.NumTables; i++)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(face.AsSpan(12 + 16 * i));
            Assert.Equal(tags[i], tag);
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(face.AsSpan(12 + 16 * i + 8)) % 4);
        }
        Assert.Equal(0, face.Length % 4);
    }

    [Fact]
    public void Extracting_from_a_file_matches_extracting_from_memory()
    {
        var bytes = StandardCollection();
        var path = WriteCollection(bytes);
        Assert.Equal(FontCollection.ExtractFace(bytes, 1), FontCollection.ExtractFace(path, 1));
    }

    [Fact]
    public void A_face_index_past_the_end_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FontCollection.ExtractFace(StandardCollection(), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => FontCollection.ExtractFace(StandardCollection(), -1));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("zero-faces")]
    [InlineData("too-many-faces")]
    [InlineData("face-offset-past-end")]
    [InlineData("table-past-end")]
    [InlineData("truncated-offsets")]
    public void Malformed_collections_are_rejected(string defect)
    {
        var bytes = StandardCollection();
        var span = bytes.AsSpan();
        switch (defect)
        {
            case "tag": span[0] = (byte)'x'; break;
            case "zero-faces": BinaryPrimitives.WriteUInt32BigEndian(span[8..], 0); break;
            case "too-many-faces": BinaryPrimitives.WriteUInt32BigEndian(span[8..], FontCollection.MaxFaces + 1); break;
            case "face-offset-past-end": BinaryPrimitives.WriteUInt32BigEndian(span[12..], (uint)bytes.Length); break;
            case "table-past-end":
                var dir0 = (int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                BinaryPrimitives.WriteUInt32BigEndian(span[(dir0 + 12 + 12)..], 0x7FFFFFFFu); // first record's length
                break;
            case "truncated-offsets": bytes = bytes[..14]; break;
        }
        Assert.Throws<InvalidDataException>(() => FontCollection.ExtractFace(bytes, 0));
    }

    [Fact]
    public void A_plain_font_is_not_a_collection()
    {
        Assert.False(FontCollection.IsCollection(SyntheticFont.Build()));
        Assert.True(FontCollection.IsCollection(StandardCollection()));
    }

    // ── indexing ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Indexer_lists_every_usable_face_and_skips_the_rest()
    {
        var path = WriteCollection(StandardCollection());
        var entries = SystemFontEnumerator.IndexCollection(path);

        // Faces 2 (sbix bitmap table, rejected by the safety validator) and 3 (no OS/2) are skipped.
        Assert.Equal(new[] { 0, 1 }, entries.Select(e => e.FaceIndex));
        Assert.All(entries, e =>
        {
            Assert.True(e.IsCollectionFace);
            Assert.Equal(path, e.FilePath);
            Assert.Equal(Family, e.FamilyName);
        });
        Assert.Equal(new[] { 400, 700 }, entries.Select(e => e.WeightCss));
        Assert.Equal(new[] { "SynthColl-Regular", "SynthColl-Bold" }, entries.Select(e => e.PostScriptName));
    }

    [Fact]
    public void A_malformed_collection_indexes_to_nothing_instead_of_throwing()
    {
        var bytes = StandardCollection();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0);
        Assert.Empty(SystemFontEnumerator.IndexCollection(WriteCollection(bytes)));
    }

    private sealed class DirectoryEnumerator(string dir) : SystemFontEnumerator
    {
        protected override IEnumerable<string> FontDirectories => [dir];
    }

    [Fact]
    public void Enumerator_finds_collection_faces_by_content_not_extension()
    {
        // A .ttc and a collection misnamed .ttf both index face-by-face; the plain sfnt still indexes once.
        WriteCollection(StandardCollection(), "a.ttc");
        WriteCollection(StandardCollection(), "b.ttf");

        var entries = new DirectoryEnumerator(_dir).Enumerate().ToList();

        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.True(e.IsCollectionFace));
        Assert.Equal(2, entries.Count(e => e.FilePath.EndsWith("a.ttc", StringComparison.Ordinal)));
    }

    // ── resolving ─────────────────────────────────────────────────────────────────────────

    private SystemFontResolver ResolverOver(byte[] collection) =>
        new(SystemFontIndex.BuildFromEntries(SystemFontEnumerator.IndexCollection(WriteCollection(collection))));

    [Theory]
    [InlineData(400, 0)]
    [InlineData(700, 1)]
    public async Task Resolver_returns_the_chosen_face_not_the_whole_file(int weight, int expectedFace)
    {
        var collection = StandardCollection();
        var data = await ResolverOver(collection).ResolveAsync(
            new FontQuery { Family = Family, WeightCss = weight }, CancellationToken.None);

        Assert.NotNull(data);
        Assert.Equal(FontCollection.ExtractFace(collection, expectedFace), data!.Bytes.ToArray());
        Assert.Equal(weight, data.WeightCss);
        Assert.Equal("face=" + expectedFace, data.Source!.Fragment.TrimStart('#'));
    }

    [Fact]
    public async Task A_face_that_fails_the_full_parse_resolves_to_nothing()
    {
        // The indexer reads only name / OS/2 / head; a broken maxp shows up only in the full parse at
        // resolve time. The face is then "not available" (the caller falls through to its next family).
        var collection = StandardCollection();
        var resolver = ResolverOver(collection);
        var maxp = FontCollection.ReadFaceDirectory(
            FontCollection.Source.FromMemory(collection),
            BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(12)));
        Assert.True(maxp.TryGet(OpenTypeTags.Maxp, out var record));
        File.WriteAllBytes(Path.Combine(_dir, "synth.ttc"), Corrupt(collection, (int)record.Offset));

        var data = await resolver.ResolveAsync(new FontQuery { Family = Family, WeightCss = 400 }, CancellationToken.None);
        Assert.Null(data);

        static byte[] Corrupt(byte[] bytes, int maxpOffset)
        {
            var copy = (byte[])bytes.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(maxpOffset), 0x7FFF0000u); // unknown maxp version
            return copy;
        }
    }

    [Theory]
    [InlineData("400", "SynthColl-Regular")]
    [InlineData("700", "SynthColl-Bold")]
    public void Rendering_embeds_the_requested_face_of_the_collection(string weight, string postScript)
    {
        var result = HtmlPdf.ConvertDetailed(
            $"<!doctype html><html><body><p style=\"font-family:'{Family}';font-weight:{weight}\">AB</p></body></html>",
            new HtmlPdfOptions { FontResolver = ResolverOver(StandardCollection()) });

        var pdf = Encoding.Latin1.GetString(result.Pdf);
        Assert.Contains(postScript, pdf, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "FONT-MISSING-GLYPH-001");
    }

    [Fact]
    public void Real_macOS_collections_index_their_families()
    {
        // Host smoke test: Helvetica Neue ships only as /System/Library/Fonts/HelveticaNeue.ttc on macOS.
        const string path = "/System/Library/Fonts/HelveticaNeue.ttc";
        if (!File.Exists(path)) return;

        var entries = SystemFontEnumerator.IndexCollection(path);
        Assert.Contains(entries, e => e.FamilyName == "Helvetica Neue" && e.WeightCss == 400 && !e.IsItalic);
        Assert.Contains(entries, e => e.FamilyName == "Helvetica Neue" && e.WeightCss == 700);
        var regular = entries.First(e => e.FamilyName == "Helvetica Neue" && e.WeightCss == 400 && !e.IsItalic);
        var face = FontCollection.ExtractFace(path, regular.FaceIndex);
        Assert.True(FontSafetyValidator.Validate(face).IsSafe);
        Assert.Equal("Helvetica Neue", FontMetadata.Extract(OpenTypeFont.Parse(face)).FamilyName);
    }
}
