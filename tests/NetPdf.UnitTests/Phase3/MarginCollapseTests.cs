// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 3 Task 7 cycle 2 — <see cref="MarginCollapse.Collapse"/>
/// formula tests. CSS 2.1 §8.3.1 collapse:
/// <c>max(positives) - max(absolute values of negatives)</c>.
/// </summary>
public sealed class MarginCollapseTests
{
    // --- Both positive: max wins ---------------------------------------

    [Theory]
    [InlineData(0, 0, 0)]            // both zero
    [InlineData(10, 0, 10)]          // one zero, one positive
    [InlineData(0, 15, 15)]          // (commutative)
    [InlineData(10, 20, 20)]         // m2 larger
    [InlineData(20, 10, 20)]         // m1 larger (commutative)
    [InlineData(15, 15, 15)]         // equal
    public void Collapse_both_positive_returns_max(double m1, double m2, double expected)
    {
        Assert.Equal(expected, MarginCollapse.Collapse(m1, m2));
    }

    // --- Mixed sign: positive minus absolute negative ----------------

    [Theory]
    [InlineData(20, -5, 15)]          // +20 - 5 = 15
    [InlineData(10, -5, 5)]           // +10 - 5 = 5
    [InlineData(-5, 20, 15)]          // commutative
    [InlineData(5, -10, -5)]          // negative magnitude wins (-10 > 5)
    [InlineData(0, -7, -7)]           // zero positive, only negative
    [InlineData(-7, 0, -7)]           // (commutative)
    public void Collapse_mixed_sign_returns_pos_minus_abs_neg(double m1, double m2, double expected)
    {
        Assert.Equal(expected, MarginCollapse.Collapse(m1, m2));
    }

    // --- Both negative: most-negative wins ---------------------------

    [Theory]
    [InlineData(-10, -20, -20)]       // -20 more negative
    [InlineData(-20, -10, -20)]       // commutative
    [InlineData(-5, -5, -5)]          // equal negatives
    [InlineData(-1, -100, -100)]
    public void Collapse_both_negative_returns_most_negative(double m1, double m2, double expected)
    {
        Assert.Equal(expected, MarginCollapse.Collapse(m1, m2));
    }

    // --- Properties --------------------------------------------------

    [Theory]
    [InlineData(10, 20)]
    [InlineData(-5, 30)]
    [InlineData(-10, -20)]
    [InlineData(0, 15)]
    public void Collapse_is_commutative(double m1, double m2)
    {
        Assert.Equal(MarginCollapse.Collapse(m1, m2), MarginCollapse.Collapse(m2, m1));
    }

    [Fact]
    public void Collapse_with_self_returns_self()
    {
        // Collapse(m, m) = m for any m (idempotent on identical margins).
        Assert.Equal(15, MarginCollapse.Collapse(15, 15));
        Assert.Equal(-15, MarginCollapse.Collapse(-15, -15));
        Assert.Equal(0, MarginCollapse.Collapse(0, 0));
    }
}
