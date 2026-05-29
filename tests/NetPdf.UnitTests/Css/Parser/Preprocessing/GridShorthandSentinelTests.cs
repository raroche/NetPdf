// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Parser.Preprocessing;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Parser.Preprocessing;

/// <summary>
/// Phase 3 Task 18 cycle 8 post-PR-#111 review P1#2 — pins the
/// invariant that <see cref="CssPreprocessor.GridInvalidShorthandSentinel"/>
/// is rejected by EVERY grid longhand resolver. The
/// <c>grid</c> shorthand's invalid-recovery path emits this sentinel
/// for all six longhands; if a future resolver change ever made the
/// sentinel valid for one of them, an invalid `grid` shorthand would
/// partially apply — these tests catch that regression.
/// </summary>
public sealed class GridShorthandSentinelTests
{
    private static string Sentinel => CssPreprocessor.GridInvalidShorthandSentinel;

    [Fact]
    public void Sentinel_is_rejected_by_grid_template_list_resolver()
    {
        // grid-template-rows / grid-template-columns /
        // grid-auto-rows / grid-auto-columns all use GridTemplateList.
        Assert.False(GridTemplateListResolver.TryValidate(Sentinel));
    }

    [Fact]
    public void Sentinel_is_rejected_by_grid_auto_flow_keyword_table()
    {
        // grid-auto-flow uses the Keyword resolver.
        Assert.False(KeywordResolver.TryGetId(PropertyId.GridAutoFlow, Sentinel, out _));
    }

    [Fact]
    public void Sentinel_is_rejected_by_grid_template_areas_resolver()
    {
        Assert.False(GridTemplateAreasResolver.TryValidate(Sentinel));
    }

    [Fact]
    public void Sentinel_is_the_documented_slash_value()
    {
        // The sentinel choice (`/`) is load-bearing: a slash is a
        // structural separator that no grid longhand accepts as a
        // standalone value. Pin it so a careless change to a
        // value that some resolver DOES accept fails loudly here
        // rather than silently allowing partial application.
        Assert.Equal("/", Sentinel);
    }
}
