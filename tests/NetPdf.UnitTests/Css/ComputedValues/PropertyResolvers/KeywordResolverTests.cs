// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

public sealed class KeywordResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // Note: PropertyId is internal so [InlineData] cannot reference its members
    // (xUnit needs public test signatures). Each admitted-keyword case is its own
    // [Fact] — verbose but keeps the assertions strongly-typed.

    private static void AssertResolves(string keyword, PropertyId pid)
    {
        var result = KeywordResolver.Resolve(keyword, pid, pid.ToString(), null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.True(result.Slot.AsKeyword() >= 0);
    }

    private static void AssertInvalid(string keyword, PropertyId pid)
    {
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve(keyword, pid, pid.ToString(), sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    [Fact] public void Display_block_resolves()           => AssertResolves("block", PropertyId.Display);
    [Fact] public void Display_BLOCK_resolves_case_insensitive() => AssertResolves("BLOCK", PropertyId.Display);
    [Fact] public void Display_inline_block_resolves()    => AssertResolves("inline-block", PropertyId.Display);
    [Fact] public void Display_flex_resolves()            => AssertResolves("flex", PropertyId.Display);
    [Fact] public void Display_grid_resolves()            => AssertResolves("grid", PropertyId.Display);
    [Fact] public void Display_none_resolves()            => AssertResolves("none", PropertyId.Display);
    [Fact] public void Position_static_resolves()         => AssertResolves("static", PropertyId.Position);
    [Fact] public void Position_absolute_resolves()       => AssertResolves("absolute", PropertyId.Position);
    [Fact] public void Position_sticky_resolves()         => AssertResolves("sticky", PropertyId.Position);
    [Fact] public void BoxSizing_content_box_resolves()   => AssertResolves("content-box", PropertyId.BoxSizing);
    [Fact] public void BoxSizing_border_box_resolves()    => AssertResolves("border-box", PropertyId.BoxSizing);
    [Fact] public void TextAlign_center_resolves()        => AssertResolves("center", PropertyId.TextAlign);
    [Fact] public void TextAlign_justify_resolves()       => AssertResolves("justify", PropertyId.TextAlign);
    [Fact] public void FlexDirection_row_reverse_resolves() => AssertResolves("row-reverse", PropertyId.FlexDirection);
    [Fact] public void BorderTopStyle_solid_resolves()    => AssertResolves("solid", PropertyId.BorderTopStyle);
    [Fact] public void BorderTopStyle_dotted_resolves()   => AssertResolves("dotted", PropertyId.BorderTopStyle);

    [Fact] public void Display_foo_emits_diagnostic()         => AssertInvalid("foo", PropertyId.Display);
    [Fact] public void Display_block_flex_emits_diagnostic()  => AssertInvalid("block-flex", PropertyId.Display);
    [Fact] public void Position_stickys_emits_diagnostic()    => AssertInvalid("stickys", PropertyId.Position);
    [Fact] public void BoxSizing_padding_box_emits_diagnostic() => AssertInvalid("padding-box", PropertyId.BoxSizing);

    // Phase 3 Task 10 cycle 2 — CSS Text L3 §5+§6 keyword tables.
    [Fact] public void OverflowWrap_normal_resolves()     => AssertResolves("normal", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_anywhere_resolves()   => AssertResolves("anywhere", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_break_word_resolves() => AssertResolves("break-word", PropertyId.OverflowWrap);
    [Fact] public void OverflowWrap_invalid_emits_diagnostic() => AssertInvalid("foo", PropertyId.OverflowWrap);
    [Fact] public void WordBreak_normal_resolves()        => AssertResolves("normal", PropertyId.WordBreak);
    [Fact] public void WordBreak_break_all_resolves()     => AssertResolves("break-all", PropertyId.WordBreak);
    [Fact] public void WordBreak_keep_all_resolves()      => AssertResolves("keep-all", PropertyId.WordBreak);
    [Fact] public void WordBreak_break_word_resolves()    => AssertResolves("break-word", PropertyId.WordBreak);
    [Fact] public void WordBreak_invalid_emits_diagnostic() => AssertInvalid("bogus", PropertyId.WordBreak);
    [Fact] public void Hyphens_none_resolves()            => AssertResolves("none", PropertyId.Hyphens);
    [Fact] public void Hyphens_manual_resolves()          => AssertResolves("manual", PropertyId.Hyphens);
    [Fact] public void Hyphens_auto_resolves()            => AssertResolves("auto", PropertyId.Hyphens);
    [Fact] public void Hyphens_invalid_emits_diagnostic() => AssertInvalid("always", PropertyId.Hyphens);
    [Fact] public void Hyphens_AUTO_resolves_case_insensitive() => AssertResolves("AUTO", PropertyId.Hyphens);

    [Fact]
    public void TryGetId_returns_dense_zero_based_ids()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "content-box", out var contentBoxId));
        Assert.Equal(0, contentBoxId);
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "border-box", out var borderBoxId));
        Assert.Equal(1, borderBoxId);
    }

    // Per Phase 3 Task 10 cycle 2 review (User #3): pin EXACT keyword
    // ids for the new tables. The ids are part of the cascade →
    // materializer contract; reordering would silently break any
    // downstream switch. Adding new keywords appends; never reorders.

    [Fact]
    public void OverflowWrap_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "normal", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "anywhere", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.OverflowWrap, "break-word", out var id2));
        Assert.Equal(2, id2);
    }

    [Fact]
    public void WordBreak_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "normal", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "break-all", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "keep-all", out var id2));
        Assert.Equal(2, id2);
        Assert.True(KeywordResolver.TryGetId(PropertyId.WordBreak, "break-word", out var id3));
        Assert.Equal(3, id3);
    }

    [Fact]
    public void Hyphens_keyword_ids_are_pinned()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "none", out var id0));
        Assert.Equal(0, id0);
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "manual", out var id1));
        Assert.Equal(1, id1);
        Assert.True(KeywordResolver.TryGetId(PropertyId.Hyphens, "auto", out var id2));
        Assert.Equal(2, id2);
    }

    [Fact]
    public void Property_with_no_table_returns_UnsupportedUnvalidated_with_raw_text()
    {
        // Per the hardening review: a Keyword-typed property with no table
        // registered yet should be UnsupportedUnvalidated (not Deferred — that
        // implies "validated"). FontWeight isn't a Keyword PropertyType so its
        // table isn't registered.
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve("bold", PropertyId.FontWeight, "font-weight", sink, default);
        Assert.True(result.IsUnsupportedUnvalidated);
        Assert.False(result.IsDeferred);
        Assert.Equal("bold", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }
}
