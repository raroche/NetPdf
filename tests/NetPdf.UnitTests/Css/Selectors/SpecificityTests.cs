// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Selectors;
using Xunit;

namespace NetPdf.UnitTests.Css.Selectors;

/// <summary>
/// Specificity arithmetic + ordering tests. CSS Selectors L4 §17 specifies a lexicographic
/// (A, B, C) tuple — A dominant. The cascade resolver (Task 7) leans on these for tie-break.
/// </summary>
public sealed class SpecificityTests
{
    [Fact]
    public void Zero_compares_less_than_anything_nonzero()
    {
        Assert.True(Specificity.Zero < new Specificity(0, 0, 1));
        Assert.True(Specificity.Zero < new Specificity(0, 1, 0));
        Assert.True(Specificity.Zero < new Specificity(1, 0, 0));
    }

    [Fact]
    public void A_dominates_B_and_C()
    {
        var withA = new Specificity(1, 0, 0);
        var withBigB = new Specificity(0, 1000, 1000);
        Assert.True(withA > withBigB);
    }

    [Fact]
    public void B_dominates_C_when_A_equal()
    {
        var withB = new Specificity(0, 1, 0);
        var withBigC = new Specificity(0, 0, 1000);
        Assert.True(withB > withBigC);
    }

    [Fact]
    public void Addition_adds_componentwise()
    {
        var sum = new Specificity(1, 2, 3) + new Specificity(0, 0, 1);
        Assert.Equal(new Specificity(1, 2, 4), sum);
    }

    [Fact]
    public void Equal_specificities_compare_equal()
    {
        var a = new Specificity(1, 2, 3);
        var b = new Specificity(1, 2, 3);
        Assert.False(a < b);
        Assert.False(a > b);
        Assert.True(a >= b);
        Assert.True(a <= b);
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void ToString_renders_triple()
    {
        Assert.Equal("(0,0,0)", Specificity.Zero.ToString());
        Assert.Equal("(1,2,3)", new Specificity(1, 2, 3).ToString());
    }
}
