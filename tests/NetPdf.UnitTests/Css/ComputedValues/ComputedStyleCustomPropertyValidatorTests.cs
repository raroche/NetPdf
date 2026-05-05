// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Css.ComputedValues;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues;

/// <summary>
/// Round-2 Copilot review regression tests for PR #5 finding #9 — the custom-property
/// name validator must accept non-ASCII characters per CSS Custom Properties L1
/// + Syntax L3 §4.3.11 ident-continue grammar. Earlier validator was ASCII-only and
/// would have rejected valid custom property names like <c>--café</c> / <c>--цвет</c>
/// once Task 8 starts storing real custom properties.
/// </summary>
public sealed class ComputedStyleCustomPropertyValidatorTests
{
    [Theory]
    [InlineData("--café")]
    [InlineData("--цвет")]
    [InlineData("--日本語")]
    [InlineData("--emoji-😀-name")]
    public void Copilot9_NonAscii_custom_property_names_are_accepted(string name)
    {
        using var style = ComputedStyle.Rent();
        // Should not throw — these are all valid <dashed-ident> per CSS Syntax L3 §4.3.11.
        var slot = ComputedSlot.FromKeyword(0);
        style.SetCustomProperty(name, slot);
        Assert.True(style.HasCustomProperty(name));
    }

    [Theory]
    [InlineData("--with-hyphen")]
    [InlineData("--with_underscore")]
    [InlineData("--with-digits-123")]
    [InlineData("--ASCII-letters")]
    public void Copilot9_Ascii_custom_property_names_still_accepted(string name)
    {
        using var style = ComputedStyle.Rent();
        var slot = ComputedSlot.FromKeyword(0);
        style.SetCustomProperty(name, slot);
        Assert.True(style.HasCustomProperty(name));
    }

    [Theory]
    [InlineData("--has space")]      // space is not ident-continue
    [InlineData("--has,comma")]
    [InlineData("--has(paren)")]
    [InlineData("--has@at")]
    public void Copilot9_Names_with_invalid_chars_still_rejected(string name)
    {
        using var style = ComputedStyle.Rent();
        var slot = ComputedSlot.FromKeyword(0);
        Assert.Throws<ArgumentException>(() => style.SetCustomProperty(name, slot));
    }

    [Theory]
    [InlineData("foo")]              // missing -- prefix
    [InlineData("-foo")]             // single - prefix
    [InlineData("--")]               // bare -- (length 2)
    public void Copilot9_Names_violating_dashed_ident_prefix_rejected(string name)
    {
        using var style = ComputedStyle.Rent();
        var slot = ComputedSlot.FromKeyword(0);
        Assert.Throws<ArgumentException>(() => style.SetCustomProperty(name, slot));
    }
}
