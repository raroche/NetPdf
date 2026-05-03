// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Post-Task-9 hardening: <see cref="ToUnicodeCMap.Emit"/> is the trust boundary for
/// CMap serialization. Direct construction via the <c>init</c> property bypasses
/// <see cref="ToUnicodeCMap.FromSubset"/>'s implicit validation, so the emitter
/// re-validates every entry before any byte production.
/// </summary>
public sealed class ToUnicodeCMapHardeningTests
{
    [Fact]
    public void Emit_throws_on_negative_subset_glyph_id()
    {
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { -1, "A" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("Identity-H", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_glyph_id_above_uint16_max()
    {
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 0x10000, "A" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("65535", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_empty_target_string()
    {
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, string.Empty } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("empty target", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_unpaired_high_surrogate()
    {
        // U+D83D alone — high surrogate without the low surrogate that completes a supplementary codepoint.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "\uD83D" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("unpaired high surrogate", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_high_surrogate_followed_by_non_low_surrogate()
    {
        // High surrogate followed by an unrelated BMP char — also invalid.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "\uD83DA" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("unpaired high surrogate", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_unpaired_low_surrogate()
    {
        // U+DE00 alone — low surrogate not preceded by a high surrogate.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "\uDE00" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("unpaired low surrogate", ex.Message);
    }

    [Fact]
    public void Emit_throws_on_low_surrogate_after_BMP_character()
    {
        // BMP char then orphan low surrogate.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "A\uDE00" } },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => cmap.Emit());
        Assert.Contains("unpaired low surrogate", ex.Message);
    }

    [Fact]
    public void Emit_accepts_valid_supplementary_plane_pair()
    {
        // Sanity: a properly-paired surrogate sequence still emits successfully.
        var cmap = new ToUnicodeCMap
        {
            SubsetGlyphIdToText = new Dictionary<int, string> { { 1, "😀" } }, // U+1F600
        };
        var bytes = cmap.Emit();
        Assert.NotEmpty(bytes);
    }
}
