// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues.PropertyResolvers;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Unit tests for <see cref="RelativeLengthResolver"/> — resolving deferred font-/viewport-relative
/// lengths to used px (relative-units cycle; first consumer: the margin-box explicit
/// <c>width</c>/<c>height</c>).
/// </summary>
public sealed class RelativeLengthResolverTests
{
    // em base 20, root em 10, viewport 800 × 600.
    private const double Em = 20.0, RootEm = 10.0, Vw = 800.0, Vh = 600.0;

    [Theory]
    [InlineData("10em", 200.0)]    // 10 × 20
    [InlineData("4ex", 40.0)]      // 4 × 20 × 0.5 (CSS Values 4 §6.1.2 fallback)
    [InlineData("4ch", 40.0)]      // 4 × 20 × 0.5
    [InlineData("10rem", 100.0)]   // 10 × 10 (root, NOT the em base)
    [InlineData("50vw", 400.0)]    // 50% of 800
    [InlineData("50vh", 300.0)]    // 50% of 600
    [InlineData("10vmin", 60.0)]   // 10% of min(800, 600)
    [InlineData("10vmax", 80.0)]   // 10% of max(800, 600)
    [InlineData("1.5REM", 15.0)]   // unit match is case-insensitive
    [InlineData("0em", 0.0)]       // zero resolves to zero (not rejected)
    public void TryResolve_resolves_each_supported_unit(string raw, double expectedPx)
    {
        Assert.True(RelativeLengthResolver.TryResolve(raw, Em, RootEm, Vw, Vh, out var px));
        Assert.Equal(expectedPx, px, 3);
    }

    [Theory]
    [InlineData("calc(100% - 10px)")]   // calc — needs calc machinery
    [InlineData("-2em")]                // negative — sizes are non-negative
    [InlineData("10cqw")]               // container units — unsupported
    [InlineData("em")]                  // unit with no number
    [InlineData("tenem")]               // malformed number
    [InlineData("10")]                  // bare number — not a relative length
    [InlineData("50%")]                 // percentage — handled by the slot path, not here
    [InlineData("10px")]                // absolute — never deferred
    [InlineData("")]
    public void TryResolve_rejects_unsupported_values(string raw)
    {
        Assert.False(RelativeLengthResolver.TryResolve(raw, Em, RootEm, Vw, Vh, out _));
        Assert.False(RelativeLengthResolver.IsSupported(raw));
    }

    [Fact]
    public void IsSupported_matches_TryResolve_for_supported_units()
    {
        // The MarginBoxStyle keep-vs-drop gate (IsSupported) and the painter resolve (TryResolve)
        // must agree, or a kept raw would silently shrink-to-fit.
        foreach (var raw in new[] { "10em", "4ex", "4ch", "10rem", "50vw", "50vh", "10vmin", "10vmax" })
            Assert.True(RelativeLengthResolver.IsSupported(raw), raw);
    }

    [Fact]
    public void TryResolve_rem_does_not_strip_as_em()
    {
        // "1.5rem" must parse as 1.5 rem (15px against root 10), not 1.5 em — the longest unit
        // suffix wins.
        Assert.True(RelativeLengthResolver.TryResolve("1.5rem", Em, RootEm, Vw, Vh, out var px));
        Assert.Equal(15.0, px, 3);
    }
}
