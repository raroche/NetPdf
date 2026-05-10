// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Inline;
using Xunit;

namespace NetPdf.UnitTests.Layout.Inline;

/// <summary>Per Phase 3 Task 10 cycle 2 + post-cycle-2 review
/// hardening — tests for <see cref="InlineTextPolicy"/> +
/// <see cref="InlineTextPolicyMaterializer.ReadInlineTextPolicy"/>.
/// Covers each keyword id → enum mapping plus the alias-folding
/// rules per CSS Text L3:
/// <list type="bullet">
///   <item><c>overflow-wrap: break-word</c> → <see cref="OverflowWrap.Anywhere"/>.</item>
///   <item><c>word-break: break-word</c> → <see cref="WordBreak.Normal"/>
///   PLUS bumps <see cref="OverflowWrap"/> to
///   <see cref="OverflowWrap.Anywhere"/> regardless of the
///   <c>overflow-wrap</c> declaration.</item>
///   <item><c>white-space: break-spaces</c> folds to
///   <see cref="WhiteSpace.Normal"/> (cycle 2 simplification).</item>
/// </list>
/// </summary>
public sealed class InlineTextPolicyTests
{
    [Fact]
    public void Default_returns_CSS_initial_values()
    {
        var p = InlineTextPolicy.Default;
        Assert.Equal(WhiteSpace.Normal, p.WhiteSpace);
        Assert.Equal(OverflowWrap.Normal, p.OverflowWrap);
        Assert.Equal(WordBreak.Normal, p.WordBreak);
        Assert.Equal(Hyphens.Manual, p.Hyphens);
    }

    [Fact]
    public void Empty_ComputedStyle_returns_default_policy()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        var p = style.ReadInlineTextPolicy();
        Assert.Equal(WhiteSpace.Normal, p.WhiteSpace);
        Assert.Equal(OverflowWrap.Normal, p.OverflowWrap);
        Assert.Equal(WordBreak.Normal, p.WordBreak);
        Assert.Equal(Hyphens.Manual, p.Hyphens);
    }

    // --- WhiteSpace mapping ---

    private static InlineTextPolicy ResolveWith(PropertyId pid, int keywordId)
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(pid, ComputedSlot.FromKeyword(keywordId));
        return style.ReadInlineTextPolicy();
    }

    [Fact] public void WhiteSpace_normal()       => Assert.Equal(WhiteSpace.Normal, ResolveWith(PropertyId.WhiteSpace, 0).WhiteSpace);
    [Fact] public void WhiteSpace_pre()          => Assert.Equal(WhiteSpace.Pre, ResolveWith(PropertyId.WhiteSpace, 1).WhiteSpace);
    [Fact] public void WhiteSpace_nowrap()       => Assert.Equal(WhiteSpace.NoWrap, ResolveWith(PropertyId.WhiteSpace, 2).WhiteSpace);
    [Fact] public void WhiteSpace_pre_wrap()     => Assert.Equal(WhiteSpace.PreWrap, ResolveWith(PropertyId.WhiteSpace, 3).WhiteSpace);
    [Fact] public void WhiteSpace_break_spaces_folds_to_Normal() => Assert.Equal(WhiteSpace.Normal, ResolveWith(PropertyId.WhiteSpace, 4).WhiteSpace);
    [Fact] public void WhiteSpace_pre_line()     => Assert.Equal(WhiteSpace.PreLine, ResolveWith(PropertyId.WhiteSpace, 5).WhiteSpace);

    // --- OverflowWrap mapping ---

    [Fact] public void OverflowWrap_normal()                   => Assert.Equal(OverflowWrap.Normal, ResolveWith(PropertyId.OverflowWrap, 0).OverflowWrap);
    [Fact] public void OverflowWrap_anywhere()                 => Assert.Equal(OverflowWrap.Anywhere, ResolveWith(PropertyId.OverflowWrap, 1).OverflowWrap);
    [Fact] public void OverflowWrap_break_word_folds_to_Anywhere() => Assert.Equal(OverflowWrap.Anywhere, ResolveWith(PropertyId.OverflowWrap, 2).OverflowWrap);

    // --- WordBreak mapping ---

    [Fact] public void WordBreak_normal()                       => Assert.Equal(WordBreak.Normal, ResolveWith(PropertyId.WordBreak, 0).WordBreak);
    [Fact] public void WordBreak_break_all()                    => Assert.Equal(WordBreak.BreakAll, ResolveWith(PropertyId.WordBreak, 1).WordBreak);
    [Fact] public void WordBreak_keep_all()                     => Assert.Equal(WordBreak.KeepAll, ResolveWith(PropertyId.WordBreak, 2).WordBreak);
    [Fact] public void WordBreak_break_word_stays_Normal()      => Assert.Equal(WordBreak.Normal, ResolveWith(PropertyId.WordBreak, 3).WordBreak);

    [Fact]
    public void WordBreak_break_word_bumps_OverflowWrap_to_Anywhere()
    {
        // word-break: break-word folds to (word-break:normal +
        // overflow-wrap:anywhere) per CSS Text L3 §5.2.
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(3)); // break-word
        var p = style.ReadInlineTextPolicy();
        Assert.Equal(WordBreak.Normal, p.WordBreak);
        Assert.Equal(OverflowWrap.Anywhere, p.OverflowWrap);
    }

    [Fact]
    public void WordBreak_break_word_does_not_override_explicit_OverflowWrap_Anywhere()
    {
        // Explicit overflow-wrap: anywhere + word-break: break-word —
        // both want anywhere; result is anywhere.
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(3)); // break-word
        style.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        var p = style.ReadInlineTextPolicy();
        Assert.Equal(WordBreak.Normal, p.WordBreak);
        Assert.Equal(OverflowWrap.Anywhere, p.OverflowWrap);
    }

    [Fact]
    public void Explicit_OverflowWrap_anywhere_with_WordBreak_normal_resolves_correctly()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        style.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(0));    // normal
        var p = style.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Anywhere, p.OverflowWrap);
        Assert.Equal(WordBreak.Normal, p.WordBreak);
    }

    [Fact]
    public void OverflowWrap_break_word_with_WordBreak_break_all_resolves_correctly()
    {
        // overflow-wrap: break-word folds to anywhere; word-break:
        // break-all stands.
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(2)); // break-word
        style.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1));    // break-all
        var p = style.ReadInlineTextPolicy();
        Assert.Equal(OverflowWrap.Anywhere, p.OverflowWrap);
        Assert.Equal(WordBreak.BreakAll, p.WordBreak);
    }

    // --- Hyphens mapping ---

    [Fact] public void Hyphens_none()    => Assert.Equal(Hyphens.None, ResolveWith(PropertyId.Hyphens, 0).Hyphens);
    [Fact] public void Hyphens_manual()  => Assert.Equal(Hyphens.Manual, ResolveWith(PropertyId.Hyphens, 1).Hyphens);
    [Fact] public void Hyphens_auto()    => Assert.Equal(Hyphens.Auto, ResolveWith(PropertyId.Hyphens, 2).Hyphens);

    // --- Combination test ---

    [Fact]
    public void Full_combination_resolves_all_four_properties()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Set(PropertyId.WhiteSpace, ComputedSlot.FromKeyword(2)); // nowrap
        style.Set(PropertyId.OverflowWrap, ComputedSlot.FromKeyword(1)); // anywhere
        style.Set(PropertyId.WordBreak, ComputedSlot.FromKeyword(1));   // break-all
        style.Set(PropertyId.Hyphens, ComputedSlot.FromKeyword(2));     // auto

        var p = style.ReadInlineTextPolicy();
        Assert.Equal(WhiteSpace.NoWrap, p.WhiteSpace);
        Assert.Equal(OverflowWrap.Anywhere, p.OverflowWrap);
        Assert.Equal(WordBreak.BreakAll, p.WordBreak);
        Assert.Equal(Hyphens.Auto, p.Hyphens);
    }
}
