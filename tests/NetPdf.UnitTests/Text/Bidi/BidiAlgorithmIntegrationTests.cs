// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>
/// Integration tests for <see cref="BidiAlgorithm"/>: drives the public API surface
/// (paragraph-level resolution today; full level resolution after Stage 12.2) and
/// verifies the staging contract — <see cref="BidiAlgorithm.ResolveLevels"/> throws
/// <see cref="NotImplementedException"/> with a clear staging message until 12.2 lands.
/// </summary>
public sealed class BidiAlgorithmIntegrationTests
{
    [Fact]
    public void ResolveParagraphLevel_combines_table_lookup_and_P_rules_for_pure_LTR()
    {
        Assert.Equal((byte)0, BidiAlgorithm.ResolveParagraphLevel("Hello, world!"));
    }

    [Fact]
    public void ResolveParagraphLevel_combines_table_lookup_and_P_rules_for_pure_RTL()
    {
        Assert.Equal((byte)1, BidiAlgorithm.ResolveParagraphLevel("שלום עולם"));
    }

    [Fact]
    public void ResolveParagraphLevel_handles_mixed_text_starting_with_neutrals_and_digits()
    {
        // P2: skip neutrals + digits, find Arabic alef → level 1.
        Assert.Equal((byte)1, BidiAlgorithm.ResolveParagraphLevel("  ١٢٣ ابة hello"));
    }

    [Fact]
    public void ResolveParagraphLevel_default_direction_argument_is_Auto()
    {
        // Default is Auto — same result whether passed explicitly or not.
        var explicitAuto = BidiAlgorithm.ResolveParagraphLevel("שלום", ParagraphDirection.Auto);
        var implicitAuto = BidiAlgorithm.ResolveParagraphLevel("שלום");
        Assert.Equal(explicitAuto, implicitAuto);
    }

    [Fact]
    public void ResolveLevels_throws_NotImplementedException_with_staging_message()
    {
        // Stage 12.1 ships paragraph-level resolution only. Calling ResolveLevels before
        // Stage 12.2 lands must surface a precise diagnostic so consumers know the gap.
        var ex = Assert.Throws<NotImplementedException>(() =>
            BidiAlgorithm.ResolveLevels("hello"));
        Assert.Contains("Stage 12.2", ex.Message);
    }
}
