// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Shaping;
using NetPdf.UnitTests.Text.Fonts.OpenType;
using Xunit;

namespace NetPdf.UnitTests.Text.Shaping;

/// <summary>
/// Post-Task-11 hardening tests covering the trust-boundary behavior added per the
/// review: required script/language inputs, UTF-16 cluster semantics for the
/// supplementary plane, lone-surrogate handling, and concurrent shape-call safety.
/// </summary>
public sealed class HbShaperHardeningTests
{
    private const string Latin = "Latn";
    private const string English = "en";

    // ───── Trust-boundary input validation ────────────────────────────────────

    [Fact]
    public void Shape_throws_when_script_is_null()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var ex = Assert.Throws<ArgumentException>(() =>
            shaper.Shape("A", ShapingDirection.LeftToRight, scriptIso15924: null!, language: English));
        Assert.Contains("scriptIso15924", ex.Message);
    }

    [Fact]
    public void Shape_throws_when_script_is_empty()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        Assert.Throws<ArgumentException>(() =>
            shaper.Shape("A", ShapingDirection.LeftToRight, scriptIso15924: string.Empty, language: English));
    }

    [Fact]
    public void Shape_throws_when_language_is_null()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var ex = Assert.Throws<ArgumentException>(() =>
            shaper.Shape("A", ShapingDirection.LeftToRight, scriptIso15924: Latin, language: null!));
        Assert.Contains("language", ex.Message);
    }

    [Fact]
    public void Shape_throws_when_language_is_empty()
    {
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        Assert.Throws<ArgumentException>(() =>
            shaper.Shape("A", ShapingDirection.LeftToRight, scriptIso15924: Latin, language: string.Empty));
    }

    // ───── UTF-16 cluster semantics for supplementary plane ───────────────────

    [Fact]
    public void Cluster_for_supplementary_plane_codepoint_jumps_by_2_after_surrogate_pair()
    {
        // U+1F600 (😀) encodes as surrogate pair (D83D DE00) — 2 UTF-16 code units.
        // The next ASCII char 'A' should have Cluster == 2 (not 1) per the
        // hb_buffer_add_utf16 contract: cluster is a UTF-16 code-unit index.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("😀A", ShapingDirection.LeftToRight, Latin, English);

        // Two glyphs: emoji (likely glyph 0 since synthetic font has no emoji), then 'A'.
        Assert.Equal(2, result.Length);
        Assert.Equal(0, result[0].Cluster);  // emoji starts at code-unit 0
        Assert.Equal(2, result[1].Cluster);  // 'A' starts at code-unit 2 (after the 2-unit surrogate pair)
    }

    // ───── Lone surrogate behavior ────────────────────────────────────────────

    [Fact]
    public void Shape_lone_high_surrogate_produces_replacement_glyph_path()
    {
        // U+D83D alone — invalid UTF-16 (orphan high surrogate).
        // HarfBuzz's policy: treat invalid sequences as the replacement character (U+FFFD)
        // and look up via cmap. With a synthetic font that has no U+FFFD entry, this
        // resolves to .notdef (glyph 0). Test pins the behavior — if HarfBuzz changes
        // policy in a future release, this test catches it.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);
        var result = shaper.Shape("\uD83D", ShapingDirection.LeftToRight, Latin, English);

        Assert.Single(result);
        Assert.Equal((ushort)0, result[0].GlyphId); // .notdef
    }

    // ───── Concurrent shape-call safety ───────────────────────────────────────

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_shape_calls_against_one_shaper_produce_consistent_results()
    {
        // Each Shape call constructs its own Buffer; Face/Font are shared but read-only.
        // HarfBuzz docs state that's safe; this test validates the wrapper preserves it.
        using var shaper = new HbShaper(SyntheticFont.Build(), fontSizePx: 12);

        const int taskCount = 16;
        const int iterationsPerTask = 50;
        var tasks = new System.Threading.Tasks.Task[taskCount];
        var failures = new List<Exception>();
        var failuresLock = new object();

        for (var t = 0; t < taskCount; t++)
        {
            tasks[t] = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < iterationsPerTask; i++)
                    {
                        var result = shaper.Shape("AB", ShapingDirection.LeftToRight, Latin, English);
                        if (result.Length != 2 || result[0].GlyphId != 1 || result[1].GlyphId != 2)
                        {
                            throw new InvalidOperationException(
                                $"Concurrent shaping returned unexpected result: " +
                                $"{result.Length} glyph(s), [0].GlyphId = {(result.Length > 0 ? result[0].GlyphId : -1)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (failuresLock)
                    {
                        failures.Add(ex);
                    }
                }
            });
        }
        await System.Threading.Tasks.Task.WhenAll(tasks);

        Assert.Empty(failures);
    }
}
