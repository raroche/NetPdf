// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Hyphenation;
using NetPdf.Languages.European;
using Xunit;

namespace NetPdf.UnitTests.Languages;

/// <summary>
/// Phase-5 T5 — the <c>NetPdf.Languages.European</c> pack registers its hyphenators with the core's
/// <see cref="HyphenationRegistry"/> (on load via a module initializer; also idempotently via
/// <see cref="EuropeanHyphenation.Register"/>), after which those languages hyphenate through the
/// public registry seam.
/// </summary>
public sealed class EuropeanHyphenationTests
{
    [Fact]
    public void Core_english_is_always_registered()
    {
        Assert.True(HyphenationRegistry.IsRegistered("en"));
        Assert.True(HyphenationRegistry.IsRegistered("en-GB")); // primary-subtag normalization
        Assert.True(HyphenationRegistry.TryHyphenate("en", "hyphenation", out var breaks));
        Assert.True(breaks.Length >= 1);
    }

    [Fact]
    public void Pack_registers_german_and_hyphenates_a_word()
    {
        EuropeanHyphenation.Register(); // idempotent; the module initializer also runs it on load

        Assert.True(HyphenationRegistry.IsRegistered("de"));
        Assert.True(HyphenationRegistry.IsRegistered("de-DE")); // locale → primary subtag

        Assert.True(HyphenationRegistry.TryHyphenate("de", "Silbentrennung", out var breaks));
        Assert.True(breaks.Length >= 2, "the German exception word should hyphenate at several points");
    }

    [Fact]
    public void Pack_registers_french()
    {
        EuropeanHyphenation.Register();
        Assert.True(HyphenationRegistry.IsRegistered("fr"));
        Assert.True(HyphenationRegistry.TryHyphenate("fr", "ordinateur", out var breaks));
        Assert.True(breaks.Length >= 1);
    }

    [Fact]
    public void Unregistered_language_returns_false()
    {
        Assert.False(HyphenationRegistry.TryHyphenate("zz", "whatever", out var breaks));
        Assert.Empty(breaks);
    }
}
