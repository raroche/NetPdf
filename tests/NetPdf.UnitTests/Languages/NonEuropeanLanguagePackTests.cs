// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Hyphenation;
using NetPdf.Languages.All;
using NetPdf.Languages.Arabic;
using NetPdf.Languages.Cjk;
using NetPdf.Languages.Indic;
using Xunit;

namespace NetPdf.UnitTests.Languages;

/// <summary>
/// Phase-5 — the CJK / Arabic / Indic packs + the All meta-package register with the core's
/// <see cref="HyphenationRegistry"/>. CJK and Arabic register their languages as explicit
/// <b>no-hyphenation</b> (those scripts don't soft-hyphenate), which suppresses the English fallback; Indic
/// registers routing-aware placeholders (pattern data is maintainer-vendored).
/// </summary>
public sealed class CjkHyphenationTests
{
    public CjkHyphenationTests() => CjkHyphenation.Register(); // idempotent; module initializer also runs

    [Theory]
    [InlineData("zh")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("zh-Hant")] // primary-subtag normalization
    public void Cjk_languages_are_registered(string lang) => Assert.True(HyphenationRegistry.IsRegistered(lang));

    [Theory]
    [InlineData("zh")]
    [InlineData("ja")]
    [InlineData("ko")]
    public void Cjk_suppresses_hyphenation_even_for_a_latin_word(string lang)
    {
        // Registered as no-hyphenation → resolves to zero break points, so hyphens:auto does NOT fall back
        // to the English hyphenator (which WOULD hyphenate this Latin word — asserted for contrast).
        Assert.True(HyphenationRegistry.TryHyphenate(lang, "hyphenation", out var breaks));
        Assert.Empty(breaks);
        Assert.True(HyphenationRegistry.TryHyphenate("en", "hyphenation", out var enBreaks));
        Assert.NotEmpty(enBreaks);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        CjkHyphenation.Register();
        CjkHyphenation.Register();
        Assert.True(HyphenationRegistry.IsRegistered("ja"));
    }
}

public sealed class ArabicHyphenationTests
{
    public ArabicHyphenationTests() => ArabicHyphenation.Register();

    [Theory]
    [InlineData("ar")]
    [InlineData("fa")]
    [InlineData("ur")]
    [InlineData("ar-EG")] // primary-subtag normalization
    public void Arabic_languages_are_registered(string lang) => Assert.True(HyphenationRegistry.IsRegistered(lang));

    [Theory]
    [InlineData("ar")]
    [InlineData("fa")]
    [InlineData("ur")]
    public void Arabic_suppresses_hyphenation_even_for_a_latin_word(string lang)
    {
        Assert.True(HyphenationRegistry.TryHyphenate(lang, "hyphenation", out var breaks));
        Assert.Empty(breaks);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        ArabicHyphenation.Register();
        ArabicHyphenation.Register();
        Assert.True(HyphenationRegistry.IsRegistered("fa"));
    }
}

public sealed class IndicHyphenationTests
{
    public IndicHyphenationTests() => IndicHyphenation.Register();

    [Theory]
    [InlineData("hi")]
    [InlineData("sa")]
    [InlineData("ta")]
    [InlineData("te")]
    [InlineData("bn")]
    [InlineData("ml")]
    [InlineData("hi-IN")] // primary-subtag normalization
    public void Indic_languages_are_registered(string lang) => Assert.True(HyphenationRegistry.IsRegistered(lang));

    [Fact]
    public void Indic_ships_a_placeholder_no_breaks_pending_vendored_patterns()
    {
        // Placeholder registration: no pattern data yet → no breaks (conservative — never the wrong
        // English hyphenation for an Indic-tagged document).
        Assert.True(HyphenationRegistry.TryHyphenate("hi", "hyphenation", out var breaks));
        Assert.Empty(breaks);
    }

    [Fact]
    public void RegisteredLanguages_lists_the_covered_subtags()
    {
        Assert.Contains("hi", IndicHyphenation.RegisteredLanguages);
        Assert.Contains("ta", IndicHyphenation.RegisteredLanguages);
        Assert.Contains("ml", IndicHyphenation.RegisteredLanguages);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        IndicHyphenation.Register();
        IndicHyphenation.Register();
        Assert.True(HyphenationRegistry.IsRegistered("sa"));
    }
}

public sealed class AllLanguagesTests
{
    public AllLanguagesTests() => AllLanguages.Register();

    [Theory]
    [InlineData("de")] // European
    [InlineData("fr")] // European
    [InlineData("zh")] // CJK
    [InlineData("ko")] // CJK
    [InlineData("ar")] // Arabic
    [InlineData("hi")] // Indic
    public void All_registers_every_pack(string lang) => Assert.True(HyphenationRegistry.IsRegistered(lang));

    [Fact]
    public void All_still_hyphenates_german_via_the_european_pack()
    {
        Assert.True(HyphenationRegistry.TryHyphenate("de", "Silbentrennung", out var breaks));
        Assert.Equal(new[] { 3, 6, 10 }, breaks);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        AllLanguages.Register();
        AllLanguages.Register();
        Assert.True(HyphenationRegistry.IsRegistered("ko"));
    }
}
