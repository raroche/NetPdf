// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using NetPdf.Pdf.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Per-class unit tests for the <see cref="ToUnicodeCMap"/> emitter. The integration
/// layer — pulling from a real font through <see cref="ToUnicodeCMap.FromSubset"/> — is
/// in <see cref="ToUnicodeCMapIntegrationTests"/>.
/// </summary>
public sealed class ToUnicodeCMapTests
{
    [Fact]
    public void Emit_includes_required_header_and_footer_sections()
    {
        var cmap = new ToUnicodeCMap { SubsetGlyphIdToText = new Dictionary<int, string>() };
        var text = AsciiText(cmap.Emit());

        Assert.Contains("/CIDInit /ProcSet findresource begin", text);
        Assert.Contains("begincmap", text);
        Assert.Contains("/CMapName /Adobe-Identity-UCS def", text);
        Assert.Contains("/CMapType 2 def", text);
        Assert.Contains("1 begincodespacerange", text);
        Assert.Contains("<0000> <FFFF>", text);
        Assert.Contains("endcodespacerange", text);
        Assert.Contains("endcmap", text);
        Assert.Contains("CMapName currentdict /CMap defineresource pop", text);
    }

    [Fact]
    public void Emit_produces_ascii_only_bytes()
    {
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string>
            {
                { 1, "A" },
                { 2, "→" }, // arrow — non-ASCII source must still hex-encode to ASCII output
            },
        };
        var bytes = cmap.Emit();
        foreach (var b in bytes)
        {
            Assert.True(b < 0x80, $"non-ASCII byte 0x{b:X2} in emitted CMap");
        }
    }

    [Fact]
    public void Emit_with_no_entries_omits_bfchar_block()
    {
        var cmap = new ToUnicodeCMap { SubsetGlyphIdToText = new Dictionary<int, string>() };
        var text = AsciiText(cmap.Emit());
        Assert.DoesNotContain("beginbfchar", text);
        Assert.DoesNotContain("endbfchar", text);
    }

    [Fact]
    public void Emit_single_BMP_codepoint_uses_4_hex_digits()
    {
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "A" } },
        };
        var text = AsciiText(cmap.Emit());
        Assert.Contains("1 beginbfchar", text);
        Assert.Contains("<0001> <0041>", text);
        Assert.Contains("endbfchar", text);
    }

    [Fact]
    public void Emit_supplementary_plane_codepoint_uses_surrogate_pair()
    {
        // U+1F600 (😀) — supplementary plane, encodes as surrogate pair D83D DE00.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 7, char.ConvertFromUtf32(0x1F600) } },
        };
        var text = AsciiText(cmap.Emit());
        Assert.Contains("<0007> <D83DDE00>", text);
    }

    [Fact]
    public void Emit_multi_codepoint_target_concatenates_hex_pairs()
    {
        // Ligature glyph mapping to "fi" → 0066 0069 in UTF-16BE.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 5, "fi" } },
        };
        var text = AsciiText(cmap.Emit());
        Assert.Contains("<0005> <00660069>", text);
    }

    [Fact]
    public void Emit_sorts_entries_by_subset_glyph_id_for_determinism()
    {
        // Insertion order is [3, 1, 2]; expected emission order is 1, 2, 3.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string>
            {
                { 3, "C" },
                { 1, "A" },
                { 2, "B" },
            },
        };
        var text = AsciiText(cmap.Emit());
        var pos1 = text.IndexOf("<0001>", StringComparison.Ordinal);
        var pos2 = text.IndexOf("<0002>", StringComparison.Ordinal);
        var pos3 = text.IndexOf("<0003>", StringComparison.Ordinal);
        Assert.True(pos1 > 0);
        Assert.True(pos1 < pos2);
        Assert.True(pos2 < pos3);
    }

    [Fact]
    public void Emit_chunks_more_than_100_entries_into_multiple_bfchar_blocks()
    {
        var dict = new Dictionary<int, string>();
        for (var i = 1; i <= 250; i++)
        {
            dict[i] = ((char)('A' + (i % 26))).ToString();
        }
        var cmap = new ToUnicodeCMap { SubsetGlyphIdToText = dict };
        var text = AsciiText(cmap.Emit());

        // 250 entries → blocks of 100, 100, 50.
        Assert.Equal(3, CountOccurrences(text, "beginbfchar"));
        Assert.Equal(3, CountOccurrences(text, "endbfchar"));
        Assert.Contains("100 beginbfchar", text);
        Assert.Contains("50 beginbfchar", text);
    }

    [Fact]
    public void Emit_is_deterministic_for_byte_equal_inputs()
    {
        var dictA = new Dictionary<int, string> { { 1, "A" }, { 2, "B" }, { 5, "X" } };
        var dictB = new Dictionary<int, string> { { 5, "X" }, { 1, "A" }, { 2, "B" } }; // same data, different insertion order
        var a = new ToUnicodeCMap { SubsetGlyphIdToText = dictA }.Emit();
        var b = new ToUnicodeCMap { SubsetGlyphIdToText = dictB }.Emit();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Emit_uses_uppercase_hex()
    {
        // 0xFEFF and similar uppercase letters in hex must come out uppercase.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 0xFEFF, "﻿" } },
        };
        var text = AsciiText(cmap.Emit());
        Assert.Contains("<FEFF> <FEFF>", text);
    }

    private static string AsciiText(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
