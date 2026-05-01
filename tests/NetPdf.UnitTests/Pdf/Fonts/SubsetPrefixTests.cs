// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

public sealed class SubsetPrefixTests
{
    [Fact]
    public void Prefix_is_six_uppercase_letters()
    {
        var prefix = SubsetPrefix.Derive("TestFont", new[] { 0, 1, 2 });
        Assert.Equal(6, prefix.Length);
        foreach (var c in prefix)
        {
            Assert.InRange(c, 'A', 'Z');
        }
    }

    [Fact]
    public void Same_inputs_produce_same_prefix()
    {
        var a = SubsetPrefix.Derive("TestFont", new[] { 0, 1, 5, 10 });
        var b = SubsetPrefix.Derive("TestFont", new[] { 0, 1, 5, 10 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Different_glyph_sets_typically_produce_different_prefixes()
    {
        var a = SubsetPrefix.Derive("TestFont", new[] { 0, 1, 2 });
        var b = SubsetPrefix.Derive("TestFont", new[] { 0, 1, 3 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_font_names_typically_produce_different_prefixes()
    {
        var a = SubsetPrefix.Derive("FontA", new[] { 0, 1, 2 });
        var b = SubsetPrefix.Derive("FontB", new[] { 0, 1, 2 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Glyph_id_order_does_not_affect_prefix()
    {
        // Internally the helper sorts; passing in a different order must not change output.
        var a = SubsetPrefix.Derive("TestFont", new[] { 1, 5, 10, 2 });
        var b = SubsetPrefix.Derive("TestFont", new[] { 10, 1, 2, 5 });
        Assert.Equal(a, b);
    }

    [Fact]
    public void Empty_glyph_set_still_produces_six_letter_prefix()
    {
        var prefix = SubsetPrefix.Derive("TestFont", Array.Empty<int>());
        Assert.Equal(6, prefix.Length);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => SubsetPrefix.Derive(null!, new[] { 0 }));
        Assert.Throws<ArgumentNullException>(() => SubsetPrefix.Derive("Font", null!));
    }
}
