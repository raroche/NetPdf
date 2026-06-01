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
/// Phase 5 layout→PDF cycle 4 — unit tests for <see cref="FontFamilyListResolver"/>
/// (CSS Fonts 4 §2.1). Parses the comma-separated family list into a side-table
/// <see cref="FontFamilyList"/>; <see cref="FontReaders.ReadFontFamily"/> decodes it.
/// </summary>
public sealed class FontFamilyListResolverTests
{
    private static FontFamilyList Read(string value)
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, value);
        var style = ComputedStyle.RentForExclusiveTesting();
        result.MaterializeInto(style, PropertyId.FontFamily);
        return style.ReadFontFamily();
    }

    private static ResolverResult ResolveLeaf(string value) =>
        FontFamilyListResolver.Resolve(value, PropertyId.FontFamily, "font-family", null, default);

    [Fact]
    public void Parses_a_comma_separated_list_in_order()
    {
        var list = Read("Arial, sans-serif");
        Assert.Equal(new[] { "Arial", "sans-serif" }, list.Families.ToArray());
        Assert.Equal("Arial", list.Primary);
    }

    [Fact]
    public void Strips_quotes_and_preserves_multi_word_names()
    {
        Assert.Equal(new[] { "Times New Roman", "serif" }, Read("\"Times New Roman\", serif").Families.ToArray());
        Assert.Equal(new[] { "Foo Bar" }, Read("'Foo Bar'").Families.ToArray());       // single-quoted
        Assert.Equal(new[] { "Courier New" }, Read("Courier  New").Families.ToArray()); // unquoted, ws-collapsed
    }

    [Fact]
    public void Single_generic_resolves()
    {
        Assert.Equal(new[] { "monospace" }, Read("monospace").Families.ToArray());
    }

    [Fact]
    public void Unset_reads_the_initial_serif_default()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        Assert.Equal(new[] { "serif" }, style.ReadFontFamily().Families.ToArray());
    }

    [Fact]
    public void Resolves_into_the_side_table()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, "Arial, sans-serif");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.SideTableIndex, result.Slot.Tag);
    }

    // --- post-PR-#120 review (P2): strict <family-name> grammar ----------

    [Theory]
    [InlineData("Arial,")]          // trailing comma
    [InlineData(",serif")]          // leading comma
    [InlineData("Arial,,serif")]    // doubled comma
    [InlineData("\"Arial")]         // unclosed double quote
    [InlineData("'Arial, serif")]   // unclosed single quote spanning a comma
    [InlineData("\"Arial\"x")]      // junk after a quoted string
    [InlineData("123abc")]          // digit-leading unquoted ident
    [InlineData("Arial, 9News")]    // digit-leading later entry
    [InlineData("@home")]           // punctuation-leading unquoted ident
    [InlineData("")]                // empty
    [InlineData("   ")]             // whitespace only
    public void Malformed_lists_are_invalid(string value)
    {
        // Malformed family lists are rejected (Invalid), not silently sanitized.
        Assert.True(ResolveLeaf(value).IsInvalid);
    }

    [Fact]
    public void Invalid_list_diagnoses_and_falls_back_to_the_serif_initial()
    {
        var sink = new CapturingSink();
        var result = FontFamilyListResolver.Resolve(
            "Arial,,serif", PropertyId.FontFamily, "font-family", sink, default);

        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);

        // An Invalid result is a no-op on the style → the reader sees the serif default.
        var style = ComputedStyle.RentForExclusiveTesting();
        result.MaterializeInto(style, PropertyId.FontFamily);
        Assert.Equal(new[] { "serif" }, style.ReadFontFamily().Families.ToArray());
    }

    [Fact]
    public void Hyphen_and_underscore_leading_identifiers_are_valid()
    {
        // A leading '-' or '_' is a valid CSS ident start (e.g. -apple-system); the
        // strict parser must accept a real-world system-font stack.
        Assert.Equal(
            new[] { "-apple-system", "BlinkMacSystemFont", "sans-serif" },
            Read("-apple-system, BlinkMacSystemFont, sans-serif").Families.ToArray());
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }
}
