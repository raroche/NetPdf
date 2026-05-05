// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Cascade;
using NetPdf.Css.Parser;
using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Cascade;

/// <summary>
/// Unit tests for <see cref="MatchedRuleSet"/> — verifies that the per-property winner
/// lookup picks the largest <see cref="CascadeKey"/> regardless of insertion order.
/// </summary>
public sealed class MatchedRuleSetTests
{
    private static MatchedDeclaration Decl(string property, string value, CascadeKey key) =>
        new(new CssDeclaration(property, new CssValue(value), false, CssSourceLocation.Unknown), key);

    private static CascadeKey Key(int specB = 0, int sheet = 0, int rule = 0) =>
        new(CssStylesheetOrigin.Author, false, 0, new Specificity(0, specB, 0), sheet, rule, 0);

    [Fact]
    public void Empty_set_has_no_winner()
    {
        var set = new MatchedRuleSet();
        Assert.Null(set.GetWinner("color"));
        Assert.Empty(set.GetAllForProperty("color"));
        Assert.Equal(0, set.Count);
    }

    [Fact]
    public void Single_declaration_is_the_winner()
    {
        var set = new MatchedRuleSet();
        var d = Decl("color", "red", Key());
        set.Add(d);
        Assert.Equal(d, set.GetWinner("color"));
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void Higher_specificity_wins_regardless_of_insertion_order()
    {
        var set = new MatchedRuleSet();
        var lowSpec = Decl("color", "red", Key(specB: 0));
        var highSpec = Decl("color", "blue", Key(specB: 5));
        // Insert low-specificity LAST to verify GetWinner doesn't just return last-added.
        set.Add(highSpec);
        set.Add(lowSpec);
        Assert.Equal(highSpec, set.GetWinner("color"));
    }

    [Fact]
    public void Source_order_breaks_specificity_tie()
    {
        var set = new MatchedRuleSet();
        var first = Decl("color", "red", Key(rule: 0));
        var last = Decl("color", "blue", Key(rule: 99));
        set.Add(first);
        set.Add(last);
        Assert.Equal(last, set.GetWinner("color"));
    }

    [Fact]
    public void Per_property_buckets_are_independent()
    {
        var set = new MatchedRuleSet();
        set.Add(Decl("color", "red", Key()));
        set.Add(Decl("background", "white", Key()));
        Assert.Equal(2, set.Count);
        Assert.NotNull(set.GetWinner("color"));
        Assert.NotNull(set.GetWinner("background"));
        Assert.Null(set.GetWinner("border"));
    }

    [Fact]
    public void Pseudo_element_target_is_carried_on_set()
    {
        var hostSet = new MatchedRuleSet();
        var beforeSet = new MatchedRuleSet("before");
        Assert.Null(hostSet.PseudoElement);
        Assert.Equal("before", beforeSet.PseudoElement);
    }

    [Fact]
    public void GetAllForProperty_returns_every_match_in_insertion_order()
    {
        var set = new MatchedRuleSet();
        var first = Decl("color", "red", Key(rule: 0));
        var second = Decl("color", "blue", Key(rule: 1));
        var third = Decl("color", "green", Key(rule: 2));
        set.Add(first);
        set.Add(second);
        set.Add(third);
        var all = set.GetAllForProperty("color");
        Assert.Equal(3, all.Count);
        Assert.Equal(first, all[0]);
        Assert.Equal(second, all[1]);
        Assert.Equal(third, all[2]);
    }

    [Fact]
    public void ToSortedArray_returns_ascending_by_cascade_key()
    {
        var set = new MatchedRuleSet();
        var high = Decl("color", "blue", Key(specB: 5));
        var low = Decl("color", "red", Key(specB: 0));
        var mid = Decl("color", "green", Key(specB: 3));
        set.Add(high);
        set.Add(low);
        set.Add(mid);
        var sorted = set.ToSortedArray();
        Assert.Equal(low, sorted[0]);
        Assert.Equal(mid, sorted[1]);
        Assert.Equal(high, sorted[2]);
    }
}
