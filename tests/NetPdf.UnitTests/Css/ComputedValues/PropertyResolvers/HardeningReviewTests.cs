// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Regression coverage for the six Task 10 hardening review recommendations:
/// <list type="number">
///   <item>Rec 1 — `UnsupportedUnvalidated` distinct from `Deferred`: cycle-2
///     PropertyTypes must NOT silently inherit "valid CSS" semantics.</item>
///   <item>Rec 2 — strict CSS Color 4 syntax: reject modern 4-arg without slash,
///     reject legacy mixed channel forms.</item>
///   <item>Rec 3 — finite-number guards on every numeric color input.</item>
///   <item>Rec 4 — alignment compound keywords (`first baseline`, `safe center`)
///     accepted; bare `first`/`last` rejected.</item>
///   <item>Rec 5 — materialization API preserves Deferred raw text in
///     <see cref="ComputedStyle"/>.</item>
///   <item>Rec 6 — Phase 2 doc sync (covered separately in the Phase 2 doc).</item>
/// </list>
/// </summary>
public sealed class HardeningReviewTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    // ============================================================
    // Rec 1 — UnsupportedUnvalidated distinct from Deferred
    // ============================================================

    [Fact]
    public void Cycle_2_PropertyType_returns_UnsupportedUnvalidated_not_Deferred()
    {
        // `content`'s PropertyType (Content) is still unwired. Per the hardening
        // review, it must surface as UnsupportedUnvalidated — distinct from Deferred
        // which means "validated but needs context". (line-height, used here
        // pre-line-height-cycle, now resolves via LineHeightResolver.)
        var result = PropertyResolverDispatch.Resolve(PropertyId.Content, "open-quote");
        Assert.True(result.IsUnsupportedUnvalidated);
        Assert.False(result.IsDeferred);
        Assert.Equal("open-quote", result.RawText);
        Assert.True(result.HasRawText);
    }

    [Fact]
    public void Validated_deferred_value_returns_Deferred_not_UnsupportedUnvalidated()
    {
        // `width: 2em` — the LengthResolver validated it (it's well-formed) but
        // can't reduce without font metrics. State is Deferred, NOT UnsupportedUnvalidated.
        var result = PropertyResolverDispatch.Resolve(PropertyId.Width, "2em");
        Assert.True(result.IsDeferred);
        Assert.False(result.IsUnsupportedUnvalidated);
        Assert.Equal("2em", result.RawText);
    }

    [Fact]
    public void HasRawText_returns_true_for_both_Deferred_and_UnsupportedUnvalidated()
    {
        // The HasRawText helper catches the consumer footgun — "I should preserve
        // the raw text" is true for either deferred state.
        var deferred = ResolverResult.Deferred("2em");
        var unvalidated = ResolverResult.UnsupportedUnvalidated("Arial");
        var resolved = ResolverResult.Resolved(ComputedSlot.FromLengthPx(16));
        var invalid = ResolverResult.Invalid();
        Assert.True(deferred.HasRawText);
        Assert.True(unvalidated.HasRawText);
        Assert.False(resolved.HasRawText);
        Assert.False(invalid.HasRawText);
    }

    [Fact]
    public void KeywordResolver_unwired_PropertyType_returns_UnsupportedUnvalidated()
    {
        // FontWeight isn't a Keyword PropertyType so its table isn't registered.
        // The keyword resolver should return UnsupportedUnvalidated, not Deferred.
        var sink = new CapturingSink();
        var result = KeywordResolver.Resolve("bold", PropertyId.FontWeight, "font-weight", sink, default);
        Assert.True(result.IsUnsupportedUnvalidated);
        Assert.Equal("bold", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }

    // ============================================================
    // Rec 2 — Strict modern slash + legacy mixed channels rejected
    // ============================================================

    [Theory]
    [InlineData("rgb(255 0 0 0.5)")]               // modern shape, 4 args, NO slash
    [InlineData("rgba(255 128 64 0.8)")]           // same with rgba
    [InlineData("hsl(0 100% 50% 0.5)")]            // hsl modern 4-arg without slash
    [InlineData("hsla(120 100% 50% 0.3)")]         // hsla same
    public void Modern_syntax_4_args_without_slash_is_invalid(string value)
    {
        // Per CSS Color 4 §4.2.1 + §4.3.1 modern syntax REQUIRES `/` before alpha.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("'/'", sink.Diagnostics[0].Message);
    }

    [Theory]
    [InlineData("rgb(100%, 0, 0)")]                // mixed: pct, num, num
    [InlineData("rgb(100%, 0%, 0)")]               // mixed: pct, pct, num
    [InlineData("rgb(255, 50%, 0)")]               // mixed: num, pct, num
    [InlineData("rgba(255, 50%, 0, 0.5)")]         // mixed in 4-arg form too
    public void Legacy_syntax_mixed_channels_is_invalid(string value)
    {
        // Per CSS Color 4 §4.2.2 legacy syntax requires all 3 RGB to be the same
        // form (all numbers OR all percentages). Modern syntax is permissive.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.NotEmpty(sink.Diagnostics);
        Assert.Contains("legacy", sink.Diagnostics[0].Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rgb(255 50% 0)")]                 // modern allows mixing
    [InlineData("rgb(255 50% 0 / 0.5)")]           // modern with alpha allows mixing
    public void Modern_syntax_mixed_channels_is_accepted(string value)
    {
        // Per spec, only legacy enforces all-same-form; modern allows mixing.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsResolved);
        Assert.Empty(sink.Diagnostics);
    }

    // ============================================================
    // Rec 3 — Finite-number guards in ColorResolver
    // ============================================================

    [Theory]
    [InlineData("rgb(1e500, 0, 0)")]               // overflow channel
    [InlineData("rgb(255, -1e500, 0)")]            // -Infinity
    [InlineData("rgba(255, 0, 0, 1e500)")]         // overflow alpha
    [InlineData("hsl(1e500, 100%, 50%)")]          // overflow hue
    [InlineData("hsl(0, 1e500%, 50%)")]            // overflow saturation
    [InlineData("hsl(0, 100%, 1e500%)")]           // overflow lightness
    public void Color_components_reject_non_finite_numbers(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.NotEmpty(sink.Diagnostics);
    }

    // ============================================================
    // Rec 4 — Alignment compound keywords + safe/unsafe; reject bare first/last
    // ============================================================

    [Theory]
    [InlineData("first baseline")]
    [InlineData("last baseline")]
    [InlineData("first  baseline")]                // whitespace-tolerant
    [InlineData("First Baseline")]                 // case-insensitive
    [InlineData("LAST  BASELINE")]
    [InlineData("safe center")]
    [InlineData("unsafe end")]
    [InlineData("safe self-start")]
    [InlineData("unsafe flex-end")]
    [InlineData("anchor-center")]
    public void Align_items_accepts_compound_keywords(string value)
    {
        var result = KeywordResolver.Resolve(value, PropertyId.AlignItems, "align-items", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
    }

    [Theory]
    [InlineData("auto")]                            // auto only on align-self
    [InlineData("first baseline")]
    [InlineData("last baseline")]
    [InlineData("safe center")]
    [InlineData("unsafe end")]
    public void Align_self_accepts_compound_keywords(string value)
    {
        var result = KeywordResolver.Resolve(value, PropertyId.AlignSelf, "align-self", null, default);
        Assert.True(result.IsResolved);
    }

    [Theory]
    [InlineData("space-between")]
    [InlineData("space-around")]
    [InlineData("space-evenly")]
    [InlineData("safe center")]
    [InlineData("unsafe flex-start")]
    [InlineData("safe left")]
    [InlineData("unsafe right")]
    public void Justify_content_accepts_compound_keywords(string value)
    {
        var result = KeywordResolver.Resolve(value, PropertyId.JustifyContent, "justify-content", null, default);
        Assert.True(result.IsResolved);
    }

    // Per Phase 3 Task 15 L7 post-PR-#67 hardening F#3 — `align-content`
    // uses the same content-alignment grammar as `justify-content`
    // (CSS Box Alignment L3 §4.5 + §6.3): bare distribution +
    // positional values, baseline-position triple (Phase 3 Task 15 L7
    // post-PR-#67 F#6), and safe/unsafe compound forms. All must
    // resolve through the KeywordResolver. Mirrors
    // Justify_content_accepts_compound_keywords above.
    [Theory]
    [InlineData("space-between")]
    [InlineData("space-around")]
    [InlineData("space-evenly")]
    [InlineData("stretch")]
    [InlineData("baseline")]                       // §6.3 baseline triple
    [InlineData("first baseline")]
    [InlineData("last baseline")]
    [InlineData("safe center")]
    [InlineData("unsafe flex-start")]
    [InlineData("safe left")]
    [InlineData("unsafe right")]
    [InlineData("safe flex-end")]
    [InlineData("unsafe center")]
    public void Align_content_accepts_compound_keywords(string value)
    {
        var result = KeywordResolver.Resolve(value, PropertyId.AlignContent, "align-content", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
    }

    private static void AssertBareFirstLastInvalid(PropertyId pid)
    {
        // CSS Box Alignment 3 §4.4: <baseline-position> is `[first | last]? baseline`.
        // Bare `first` / `last` (without `baseline`) is NOT in the grammar.
        var sink = new CapturingSink();
        var bareFirst = KeywordResolver.Resolve("first", pid, pid.ToString(), sink, default);
        var bareLast  = KeywordResolver.Resolve("last", pid, pid.ToString(), sink, default);
        Assert.True(bareFirst.IsInvalid);
        Assert.True(bareLast.IsInvalid);
    }

    [Fact] public void Align_items_rejects_bare_first_and_last() =>
        AssertBareFirstLastInvalid(PropertyId.AlignItems);

    [Fact] public void Align_self_rejects_bare_first_and_last() =>
        AssertBareFirstLastInvalid(PropertyId.AlignSelf);

    [Fact]
    public void Justify_content_rejects_self_position_keywords()
    {
        // self-start / self-end are <self-position> only — NOT in <content-position>
        // for justify-content.
        var sink = new CapturingSink();
        var r1 = KeywordResolver.Resolve("self-start", PropertyId.JustifyContent, "justify-content", sink, default);
        var r2 = KeywordResolver.Resolve("self-end", PropertyId.JustifyContent, "justify-content", sink, default);
        Assert.True(r1.IsInvalid);
        Assert.True(r2.IsInvalid);
    }

    [Fact]
    public void Align_items_rejects_left_and_right_keywords()
    {
        // left / right are content-position-only (justify-content) per §4.5 — they
        // don't appear in self-position (align-items / align-self).
        var sink = new CapturingSink();
        var r1 = KeywordResolver.Resolve("left", PropertyId.AlignItems, "align-items", sink, default);
        var r2 = KeywordResolver.Resolve("right", PropertyId.AlignItems, "align-items", sink, default);
        Assert.True(r1.IsInvalid);
        Assert.True(r2.IsInvalid);
    }

    // ============================================================
    // Rec 5 — Materialization API preserves Deferred raw text
    // ============================================================

    [Fact]
    public void MaterializeInto_writes_resolved_slot()
    {
        using var style = ComputedStyle.Rent();
        var result = ResolverResult.Resolved(ComputedSlot.FromLengthPx(16));
        var written = result.MaterializeInto(style, PropertyId.Width);
        Assert.True(written);
        Assert.True(style.IsSet(PropertyId.Width));
        Assert.False(style.IsDeferred(PropertyId.Width));
        Assert.Equal(16.0, style.Get(PropertyId.Width).AsLengthPx(), 3);
    }

    [Fact]
    public void MaterializeInto_preserves_Deferred_raw_text()
    {
        // The cycle-1 footgun: a consumer who ignored the State and just wrote
        // result.Slot would silently drop the raw text — Deferred slot is Unset.
        // The MaterializeInto API forces preservation through SetDeferred.
        using var style = ComputedStyle.Rent();
        var result = ResolverResult.Deferred("2em");
        var written = result.MaterializeInto(style, PropertyId.Width);
        Assert.True(written);
        Assert.True(style.IsSet(PropertyId.Width));        // bitmap bit set
        Assert.True(style.IsDeferred(PropertyId.Width));   // deferred-text store has it
        Assert.True(style.TryGetDeferred(PropertyId.Width, out var raw));
        Assert.Equal("2em", raw);
        // Slot itself is still Unset — consumer must check IsDeferred.
        Assert.True(style.Get(PropertyId.Width).IsUnset);
    }

    [Fact]
    public void MaterializeInto_preserves_UnsupportedUnvalidated_raw_text()
    {
        using var style = ComputedStyle.Rent();
        var result = ResolverResult.UnsupportedUnvalidated("Arial, sans-serif");
        var written = result.MaterializeInto(style, PropertyId.FontFamily);
        Assert.True(written);
        Assert.True(style.IsDeferred(PropertyId.FontFamily));
        Assert.True(style.TryGetDeferred(PropertyId.FontFamily, out var raw));
        Assert.Equal("Arial, sans-serif", raw);
    }

    [Fact]
    public void MaterializeInto_Invalid_writes_nothing()
    {
        using var style = ComputedStyle.Rent();
        var result = ResolverResult.Invalid();
        var written = result.MaterializeInto(style, PropertyId.Color);
        Assert.False(written);
        Assert.False(style.IsSet(PropertyId.Color));
        Assert.False(style.IsDeferred(PropertyId.Color));
    }

    [Fact]
    public void Set_clears_prior_deferred_text_for_same_property()
    {
        // Materializing a typed slot for a property that previously had deferred
        // text should clear the deferred entry — single source of truth.
        using var style = ComputedStyle.Rent();
        ResolverResult.Deferred("2em").MaterializeInto(style, PropertyId.Width);
        Assert.True(style.IsDeferred(PropertyId.Width));
        ResolverResult.Resolved(ComputedSlot.FromLengthPx(32)).MaterializeInto(style, PropertyId.Width);
        Assert.True(style.IsSet(PropertyId.Width));
        Assert.False(style.IsDeferred(PropertyId.Width));
        Assert.Equal(32.0, style.Get(PropertyId.Width).AsLengthPx(), 3);
    }

    [Fact]
    public void Unset_clears_deferred_text_too()
    {
        using var style = ComputedStyle.Rent();
        ResolverResult.Deferred("2em").MaterializeInto(style, PropertyId.Width);
        style.Unset(PropertyId.Width);
        Assert.False(style.IsSet(PropertyId.Width));
        Assert.False(style.IsDeferred(PropertyId.Width));
        Assert.False(style.TryGetDeferred(PropertyId.Width, out _));
    }

    [Fact]
    public void Reset_via_pool_rent_clears_deferred_text()
    {
        // Pool re-rental contract: the next caller must not see prior deferred state.
        var first = ComputedStyle.Rent();
        ResolverResult.Deferred("2em").MaterializeInto(first, PropertyId.Width);
        first.Dispose();
        var second = ComputedStyle.Rent();
        Assert.False(second.IsDeferred(PropertyId.Width));
        Assert.False(second.TryGetDeferred(PropertyId.Width, out _));
        second.Dispose();
    }
}
