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

    [Fact]
    public void TryGetId_returns_dense_zero_based_ids()
    {
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "content-box", out var contentBoxId));
        Assert.Equal(0, contentBoxId);
        Assert.True(KeywordResolver.TryGetId(PropertyId.BoxSizing, "border-box", out var borderBoxId));
        Assert.Equal(1, borderBoxId);
    }

    [Fact]
    public void Property_with_no_table_defers_with_raw_text()
    {
        // Per Rec 1: a Keyword-typed property with no table registered yet should
        // defer (carry raw text for cycle-2 re-resolution), NOT return Invalid.
        // FontWeight isn't a Keyword PropertyType so its table isn't registered.
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve("bold", PropertyId.FontWeight, "font-weight", sink, default);
        Assert.True(result.IsDeferred);
        Assert.Equal("bold", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }
}
