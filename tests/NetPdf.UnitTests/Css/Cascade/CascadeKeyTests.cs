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
    public void Layer_order_breaks_inside_same_origin_importance()
    {
        // Layered rules: higher layer (later-declared @layer) wins for normal.
        var lowLayer = Make(layer: 0);
        var highLayer = Make(layer: 5);
        Assert.True(highLayer > lowLayer);
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
