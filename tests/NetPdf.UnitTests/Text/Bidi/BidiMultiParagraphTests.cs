// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Multi-paragraph behavior tests for the public <see cref="BidiAlgorithm.ResolveLevels"/>
/// API. UAX #9 §3.3.1 P1 requires the algorithm to run independently per paragraph, with
/// each paragraph getting its own P2/P3-resolved base level. This file pins down that
/// behavior — no inheritance of paragraph levels or explicit-formatting state across
/// paragraph separators.
/// </summary>
public sealed class BidiMultiParagraphTests
{
    [Fact]
    public void RTL_then_LTR_paragraph_each_uses_own_resolved_level()
    {
        // Hebrew paragraph (auto → RTL, level 1), then LF (B), then Latin paragraph (auto → LTR, level 0).
        // The LF stays with the first paragraph; positions: 0,1,2 = Hebrew @ 1; 3 = LF @ 1; 4,5,6 = Latin @ 0.
        var levels = BidiAlgorithm.ResolveLevels("אבג\nabc");
        Assert.Equal(1, levels[0]);
        Assert.Equal(1, levels[1]);
        Assert.Equal(1, levels[2]);
        Assert.Equal(1, levels[3]);                   // LF at first paragraph's level
        Assert.Equal(0, levels[4]);                   // 'a' second paragraph LTR
        Assert.Equal(0, levels[5]);
        Assert.Equal(0, levels[6]);
    }

    [Fact]
    public void LTR_then_RTL_paragraph_each_uses_own_resolved_level()
    {
        var levels = BidiAlgorithm.ResolveLevels("abc\nאבג");
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[1]);
        Assert.Equal(0, levels[2]);
        Assert.Equal(0, levels[3]);                   // LF at first paragraph's level
        Assert.Equal(1, levels[4]);                   // Hebrew at second paragraph RTL level 1
        Assert.Equal(1, levels[5]);
        Assert.Equal(1, levels[6]);
    }

    [Fact]
    public void Explicit_controls_in_first_paragraph_do_not_leak_into_second()
    {
        // Open RLE in the first paragraph (no PDF before B) — would leave the directional
        // state pushed if the algorithm didn't reset per paragraph. After the LF, the second
        // paragraph's plain Latin must still be at level 0, not at the first paragraph's
        // residual embedded level.
        var input = "A‫R\nabc";              // 'A' RLE 'R' LF 'a' 'b' 'c'
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);                   // 'A' (LTR para)
        // levels[1] = RLE retained at enclosing level 0, levels[2] = 'R' inside RLE at level 1+I1.
        Assert.Equal(0, levels[3]);                   // LF at first paragraph's level (0 — paragraph LTR)
        // Second paragraph: starts fresh; first strong char is 'a' (L) → level 0.
        Assert.Equal(0, levels[4]);                   // 'a' second paragraph LTR
        Assert.Equal(0, levels[5]);
        Assert.Equal(0, levels[6]);
    }

    [Fact]
    public void Three_paragraphs_each_resolve_independently()
    {
        // First: LTR (Latin), Second: RTL (Hebrew), Third: LTR (Latin).
        var input = "abc\nאבג\nxyz";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[3]);                   // first LF
        Assert.Equal(1, levels[4]);                   // first Hebrew letter (second paragraph)
        Assert.Equal(1, levels[7]);                   // second LF (in RTL paragraph → at level 1)
        Assert.Equal(0, levels[8]);                   // 'x' third paragraph LTR
        Assert.Equal(0, levels[10]);
    }

    [Fact]
    public void Multiple_consecutive_paragraph_separators_each_form_own_paragraph()
    {
        // "A\n\nB" — three implicit paragraphs:
        //   1. "A\n" (single L + LF)
        //   2. "\n" (single LF — empty content + paragraph separator → P3 default LTR)
        //   3. "B" (single L)
        var levels = BidiAlgorithm.ResolveLevels("A\n\nB");
        Assert.Equal(4, levels.Length);
        Assert.All(levels, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Explicit_paragraph_direction_applies_to_every_paragraph()
    {
        // ParagraphDirection.RightToLeft forces level 1 for EVERY paragraph regardless of content.
        var levels = BidiAlgorithm.ResolveLevels("abc\nxyz", ParagraphDirection.RightToLeft);
        // Paragraph 1: forced RTL, Latin gets I1 bumped to level 2.
        Assert.Equal(2, levels[0]);
        Assert.Equal(2, levels[1]);
        Assert.Equal(2, levels[2]);
        Assert.Equal(1, levels[3]);                   // LF at paragraph level 1
        Assert.Equal(2, levels[4]);                   // 'x' second paragraph forced RTL → I1 bumps L by 1
        Assert.Equal(2, levels[6]);
    }

    [Fact]
    public void Output_length_equals_input_length_for_multi_paragraph_input()
    {
        var input = "Hello\nאבג\nWorld";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(input.Length, levels.Length);
    }

    [Fact]
    public void Multi_paragraph_resolution_is_deterministic()
    {
        var input = "Hello\nאבג\nWorld";
        var first = BidiAlgorithm.ResolveLevels(input);
        var second = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(first, second);
    }
}
