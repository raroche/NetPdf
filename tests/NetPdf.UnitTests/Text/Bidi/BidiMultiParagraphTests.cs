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

    // ───── CRLF handling — single break unit, not two ─────────────────────────

    [Fact]
    public void CRLF_treated_as_single_paragraph_break_RTL_then_LTR()
    {
        // "אבג\r\nabc": paragraph 1 = "אבג\r\n" (Hebrew + CRLF, all level 1),
        // paragraph 2 = "abc" (level 0). Without the CRLF fix the LF would split into
        // a spurious empty paragraph between the CR and "abc".
        var input = "אבג\r\nabc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(input.Length, levels.Length);
        Assert.Equal(1, levels[0]);                   // Hebrew at paragraph 1's level 1
        Assert.Equal(1, levels[1]);
        Assert.Equal(1, levels[2]);
        Assert.Equal(1, levels[3]);                   // CR stays with paragraph 1 at level 1
        Assert.Equal(1, levels[4]);                   // LF stays with paragraph 1 at level 1
        Assert.Equal(0, levels[5]);                   // 'a' starts paragraph 2 at level 0
        Assert.Equal(0, levels[^1]);
    }

    [Fact]
    public void CRLF_treated_as_single_paragraph_break_LTR_then_RTL()
    {
        var input = "abc\r\nאבג";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[0]);
        Assert.Equal(0, levels[3]);                   // CR at paragraph 1's level
        Assert.Equal(0, levels[4]);                   // LF at paragraph 1's level
        Assert.Equal(1, levels[5]);                   // first Hebrew letter at paragraph 2's level 1
        Assert.Equal(1, levels[^1]);
    }

    [Fact]
    public void CRLF_does_not_leak_explicit_state_into_next_paragraph()
    {
        // U+202B = RLE; CR + LF = paragraph break. After CRLF, the embedded RLE state
        // must NOT persist into the next paragraph. plain Latin "abc" must end at level 0.
        var input = "A‫R\r\nabc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        // First paragraph: 'A' RLE 'R' CR LF — 'A' at 0; RLE retained at 0; 'R' (Latin L)
        // inside RLE bumps to level 2 via I1 (odd-level seq, L bumps by 1 → 2). CR/LF
        // retained at paragraph level 0.
        Assert.Equal(0, levels[0]);
        // Second paragraph: 'abc' at level 0.
        Assert.Equal(0, levels[5]);                   // 'a' starts paragraph 2
        Assert.Equal(0, levels[6]);
        Assert.Equal(0, levels[7]);
    }

    [Fact]
    public void Lone_CR_is_its_own_paragraph_break()
    {
        // "אבג\rabc" — CR alone (no following LF) is a single class-B break.
        var input = "אבג\rabc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(1, levels[3]);                   // CR at paragraph 1's level 1
        Assert.Equal(0, levels[4]);                   // 'a' starts paragraph 2 at level 0
    }

    [Fact]
    public void Lone_LF_after_non_CR_is_its_own_paragraph_break()
    {
        // "abc\nxyz\rdef" — LF alone, then CR alone — each is its own break.
        var input = "abc\nxyz\rdef";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(0, levels[3]);                   // LF
        Assert.Equal(0, levels[7]);                   // CR
        Assert.Equal(0, levels[^1]);
    }

    // ───── NEL (U+0085) ──────────────────────────────────────────────────────

    [Fact]
    public void NEL_is_a_paragraph_break()
    {
        // U+0085 NEL is class B per UCD. "אבג\u0085abc" → paragraph 1 = Hebrew + NEL,
        // paragraph 2 = Latin.
        var input = "אבג\u0085abc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(1, levels[0]);                   // Hebrew at paragraph 1 level 1
        Assert.Equal(1, levels[3]);                   // NEL stays at paragraph 1 level 1
        Assert.Equal(0, levels[4]);                   // 'a' starts paragraph 2 at level 0
    }

    // ───── PARAGRAPH SEPARATOR (U+2029) ──────────────────────────────────────

    [Fact]
    public void U2029_PARAGRAPH_SEPARATOR_breaks_paragraphs_in_public_API()
    {
        var input = "אבג\u2029abc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(1, levels[0]);
        Assert.Equal(1, levels[3]);                   // U+2029 stays with paragraph 1 at level 1
        Assert.Equal(0, levels[4]);                   // 'a' starts paragraph 2 at level 0
    }

    // ───── FILE / GROUP / RECORD SEPARATORS (U+001C / U+001D / U+001E) ───────

    [Theory]
    [InlineData('\u001C')] // FILE SEPARATOR
    [InlineData('\u001D')] // GROUP SEPARATOR
    [InlineData('\u001E')] // RECORD SEPARATOR
    public void U001C_001D_001E_are_paragraph_breaks(char separator)
    {
        // UCD classifies U+001C, U+001D, U+001E as class B. The public API must split
        // paragraphs on each.
        var input = $"אבג{separator}abc";
        var levels = BidiAlgorithm.ResolveLevels(input);
        Assert.Equal(1, levels[0]);
        Assert.Equal(1, levels[3]);                   // separator at paragraph 1's level
        Assert.Equal(0, levels[4]);                   // 'a' starts paragraph 2 at level 0
    }

    // ───── Forced paragraph direction over CRLF ──────────────────────────────

    [Fact]
    public void Forced_LeftToRight_applies_to_every_paragraph_across_CRLF()
    {
        // ParagraphDirection.LeftToRight forces paragraph level 0 for every paragraph.
        // Hebrew letters bump to level 1 via I1 within each paragraph.
        var levels = BidiAlgorithm.ResolveLevels("אבג\r\nשלום", ParagraphDirection.LeftToRight);
        Assert.Equal(1, levels[0]);                   // Hebrew bumped to level 1 in LTR paragraph 1
        Assert.Equal(0, levels[3]);                   // CR at paragraph 1's level 0
        Assert.Equal(0, levels[4]);                   // LF at paragraph 1's level 0
        Assert.Equal(1, levels[5]);                   // Hebrew bumped to level 1 in LTR paragraph 2
        Assert.Equal(1, levels[^1]);
    }

    [Fact]
    public void Forced_RightToLeft_applies_to_every_paragraph_across_CRLF()
    {
        var levels = BidiAlgorithm.ResolveLevels("abc\r\nxyz", ParagraphDirection.RightToLeft);
        // Both paragraphs forced to level 1; Latin bumps to level 2 via I1 (odd seq, L+1).
        Assert.Equal(2, levels[0]);
        Assert.Equal(2, levels[1]);
        Assert.Equal(2, levels[2]);
        Assert.Equal(1, levels[3]);                   // CR
        Assert.Equal(1, levels[4]);                   // LF
        Assert.Equal(2, levels[5]);
        Assert.Equal(2, levels[^1]);
    }
}
