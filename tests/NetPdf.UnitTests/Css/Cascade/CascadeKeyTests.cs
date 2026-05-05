// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="CascadeKey"/> — verifies the per-spec ordering of CSS Cascade
/// L4 §6.4: origin → importance → layer → specificity → source order.
/// </summary>
public sealed class CascadeKeyTests
{
    private static CascadeKey Make(
        CssStylesheetOrigin origin = CssStylesheetOrigin.Author,
        bool important = false,
        int layer = 0,
        int a = 0, int b = 0, int c = 0,
        int sheet = 0, int rule = 0, int decl = 0) =>
        new(origin, important, layer, new Specificity(a, b, c), sheet, rule, decl);

    [Fact]
    public void Author_normal_beats_user_normal_beats_ua_normal()
    {
        var ua = Make(origin: CssStylesheetOrigin.UserAgent);
        var user = Make(origin: CssStylesheetOrigin.User);
        var author = Make(origin: CssStylesheetOrigin.Author);
        Assert.True(ua < user);
        Assert.True(user < author);
    }

    [Fact]
    public void Important_inverts_origin_order_so_UA_important_wins()
    {
        // Per CSS Cascade L4 §6.4.1: author-important < user-important < UA-important.
        var authorImp = Make(origin: CssStylesheetOrigin.Author, important: true);
        var userImp = Make(origin: CssStylesheetOrigin.User, important: true);
        var uaImp = Make(origin: CssStylesheetOrigin.UserAgent, important: true);
        Assert.True(authorImp < userImp);
        Assert.True(userImp < uaImp);
    }

    [Fact]
    public void Important_always_beats_normal_within_same_origin()
    {
        var authorNormal = Make(origin: CssStylesheetOrigin.Author);
        var authorImp = Make(origin: CssStylesheetOrigin.Author, important: true);
        Assert.True(authorImp > authorNormal);
    }

    [Fact]
    public void Author_important_beats_UA_normal()
    {
        // Important rules at any origin beat normal rules at any origin.
        var uaNormal = Make(origin: CssStylesheetOrigin.UserAgent);
        var authorImp = Make(origin: CssStylesheetOrigin.Author, important: true);
        Assert.True(authorImp > uaNormal);
    }

    [Fact]
    public void Higher_specificity_wins_within_same_origin_layer()
    {
        var lowSpec = Make(c: 1);                 // type selector — (0,0,1)
        var highSpec = Make(b: 1);                // class selector — (0,1,0)
        Assert.True(highSpec > lowSpec);
    }

    [Fact]
    public void Source_order_breaks_specificity_tie()
    {
        // Same origin, same specificity — last-declared wins via packed source order.
        var first = Make(c: 1, sheet: 0, rule: 0, decl: 0);
        var last = Make(c: 1, sheet: 0, rule: 1, decl: 0);
        Assert.True(last > first);
    }

    [Fact]
    public void Stylesheet_order_dominates_rule_and_declaration_order()
    {
        // sheet:1 / rule:0 / decl:0 should beat sheet:0 / rule:99 / decl:99.
        var earlierSheet = Make(c: 1, sheet: 0, rule: 99, decl: 99);
        var laterSheet = Make(c: 1, sheet: 1, rule: 0, decl: 0);
        Assert.True(laterSheet > earlierSheet);
    }

    [Fact]
    public void Specificity_dominates_source_order()
    {
        var laterButLessSpecific = Make(c: 1, sheet: 99, rule: 99, decl: 99);
        var earlierButMoreSpecific = Make(b: 1, sheet: 0, rule: 0, decl: 0);
        Assert.True(earlierButMoreSpecific > laterButLessSpecific);
    }

    [Fact]
    public void Layer_order_among_named_layers_for_normal_later_wins()
    {
        // Per CSS Cascade L4 §6.4.4: among named layers for NORMAL declarations, the
        // later-declared layer (higher LayerOrder) wins.
        var earlier = Make(layer: 1);
        var later = Make(layer: 5);
        Assert.True(later > earlier);
    }

    [Fact]
    public void Unlayered_normal_beats_any_named_layer_normal()
    {
        // Per L4 §6.4.4: unlayered NORMAL declarations sit in an implicit final layer that
        // comes AFTER all named layers — so unlayered beats layered for normal.
        var unlayered = Make(layer: 0);
        var layered = Make(layer: 99);
        Assert.True(unlayered > layered);
    }

    [Fact]
    public void Layer_order_among_named_layers_for_important_earlier_wins()
    {
        // Per L4 §6.4.4: for IMPORTANT declarations, layer order is REVERSED — the
        // earlier-declared layer wins.
        var earlier = Make(important: true, layer: 1);
        var later = Make(important: true, layer: 5);
        Assert.True(earlier > later);
    }

    [Fact]
    public void Unlayered_important_loses_to_any_named_layer_important()
    {
        // Per L4 §6.4.4: for IMPORTANT, unlayered sits BEFORE all named layers (lowest
        // precedence among importants of the same origin) — so any named layer beats
        // unlayered for important.
        var unlayered = Make(important: true, layer: 0);
        var layered = Make(important: true, layer: 99);
        Assert.True(layered > unlayered);
    }

    [Fact]
    public void Inline_style_beats_selector_of_any_specificity_within_same_tier()
    {
        // Per L4 §6.4.3: element-attached (inline) styles beat selector-driven rules
        // within the same origin+importance tier — regardless of selector specificity.
        var inline = new CascadeKey(CssStylesheetOrigin.Author, false, 0,
            new Specificity(0, 0, 0), 0, 0, 0, isInlineStyle: true);
        var highSpecSelector = new CascadeKey(CssStylesheetOrigin.Author, false, 0,
            new Specificity(99, 99, 99), 999, 999, 999, isInlineStyle: false);
        Assert.True(inline > highSpecSelector);
    }

    [Fact]
    public void Author_important_beats_inline_style_normal()
    {
        // !important always beats normal — inline-style flag doesn't override that.
        var inlineNormal = new CascadeKey(CssStylesheetOrigin.Author, false, 0,
            new Specificity(0, 0, 0), 0, 0, 0, isInlineStyle: true);
        var authorImp = new CascadeKey(CssStylesheetOrigin.Author, true, 0,
            new Specificity(0, 0, 1), 0, 0, 0, isInlineStyle: false);
        Assert.True(authorImp > inlineNormal);
    }

    [Fact]
    public void Source_order_works_past_2M_components_no_packing_overflow()
    {
        // The earlier bit-packed source order silently corrupted past 2^21 = 2,097,152.
        // Tuple comparison handles any int values cleanly.
        var early = Make(c: 1, sheet: 0, rule: 5_000_000, decl: 5_000_000);
        var late = Make(c: 1, sheet: 0, rule: 5_000_000, decl: 5_000_001);
        Assert.True(late > early);
        var biggerSheet = Make(c: 1, sheet: 1, rule: 0, decl: 0);
        Assert.True(biggerSheet > late);
    }

    [Fact]
    public void Equal_keys_compare_equal()
    {
        var k1 = Make(c: 1);
        var k2 = Make(c: 1);
        Assert.Equal(k1, k2);
        Assert.False(k1 < k2);
        Assert.False(k1 > k2);
        Assert.True(k1 <= k2);
        Assert.True(k1 >= k2);
        Assert.Equal(0, k1.CompareTo(k2));
    }
}
