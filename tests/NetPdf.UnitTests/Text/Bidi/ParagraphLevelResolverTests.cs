// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Tests for UAX #9 P2/P3 paragraph-level resolution. Covers the explicit-direction
/// short-circuits, first-strong-character detection, and the isolate-skipping behavior
/// the algorithm requires.
/// </summary>
public sealed class ParagraphLevelResolverTests
{
    [Fact]
    public void Explicit_LeftToRight_short_circuits_to_level_zero()
    {
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("hello", ParagraphDirection.LeftToRight));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("אבג", ParagraphDirection.LeftToRight)); // Hebrew text but explicit LTR
    }

    [Fact]
    public void Explicit_RightToLeft_short_circuits_to_level_one()
    {
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("hello", ParagraphDirection.RightToLeft));
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve(string.Empty, ParagraphDirection.RightToLeft));
    }

    [Fact]
    public void Auto_with_first_strong_L_returns_level_zero()
    {
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("Hello World", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_first_strong_R_returns_level_one()
    {
        // א is Hebrew letter Alef → R.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("אבג", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_first_strong_AL_returns_level_one()
    {
        // ا is Arabic letter Alef → AL.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("ابة", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_skips_neutrals_until_first_strong_character()
    {
        // Spaces, digits, punctuation are not "strong" — Hebrew alef wins.
        Assert.Equal((byte)1, ParagraphLevelResolver.Resolve("   123 אbc", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_with_no_strong_character_defaults_to_level_zero()
    {
        // Per P3 — no L/R/AL found, default LTR.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("   ", ParagraphDirection.Auto));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("123 456", ParagraphDirection.Auto));
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve(string.Empty, ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_skips_strong_characters_inside_isolate_pair()
    {
        // ⁧ = RLI (isolate); ⁩ = PDI (close). Strong characters inside a balanced
        // isolate pair are NOT considered for paragraph-level resolution per P2.
        // Sequence: RLI א PDI L → first strong outside any isolate is the L.
        const string text = "⁧א⁩M";
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve(text, ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_treats_unmatched_isolate_initiator_as_skipping_to_end()
    {
        // ⁧ (RLI) with no closing PDI → everything after is "inside" an unmatched
        // isolate and skipped. Since no L/R/AL is seen outside an isolate, P3 → level 0.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("⁧אM", ParagraphDirection.Auto));
    }

    [Fact]
    public void Auto_handles_supplementary_plane_codepoint_correctly()
    {
        // U+1F600 (😀) defaults to L in our table. Surrogate pair must be read as one
        // codepoint; otherwise the lone high surrogate would be classified separately.
        Assert.Equal((byte)0, ParagraphLevelResolver.Resolve("😀", ParagraphDirection.Auto));
    }

    [Fact]
    public void Resolve_throws_on_unknown_direction_value()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ParagraphLevelResolver.Resolve("test", (ParagraphDirection)99));
    }
}
