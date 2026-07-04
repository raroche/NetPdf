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
/// public registry seam. Exact break positions for the exception words are pinned so a regression in
/// the exception-list parse or the registry wiring can't pass silently.
/// </summary>
public sealed class EuropeanHyphenationTests
{
    // The pack registers into the process-global HyphenationRegistry. Registration is idempotent, so
    // calling it here is safe whether or not the module initializer already ran for this test host.
    public EuropeanHyphenationTests() => EuropeanHyphenation.Register();

    [Fact]
    public void Core_english_is_always_registered()
    {
        Assert.True(HyphenationRegistry.IsRegistered("en"));
        Assert.True(HyphenationRegistry.IsRegistered("en-GB")); // primary-subtag normalization
        Assert.True(HyphenationRegistry.TryHyphenate("en", "hyphenation", out var breaks));
        Assert.NotEmpty(breaks);
    }

    [Theory]
    // Exact break positions for the German exception words (these bypass the pattern algorithm). A
    // position k means "a soft break may go between word[k-1] and word[k]", after the default
    // leftMin=2 / rightMin=3 trim.
    [InlineData("Silbentrennung", new[] { 3, 6, 10 })] // Sil-ben-tren-nung
    [InlineData("Übersetzung", new[] { 4, 7 })]        // Über-set-zung — accented, capitalized German noun
    [InlineData("übersetzung", new[] { 4, 7 })]        // lower-cased variant matches too (ASCII-only case fold)
    public void German_exception_words_hyphenate_at_exact_positions(string word, int[] expected)
    {
        Assert.True(HyphenationRegistry.IsRegistered("de"));
        Assert.True(HyphenationRegistry.IsRegistered("de-DE")); // locale → primary subtag
        Assert.True(HyphenationRegistry.TryHyphenate("de", word, out var breaks));
        Assert.Equal(expected, breaks);
    }

    [Theory]
    [InlineData("ordinateur", new[] { 2, 4, 6 })] // or-di-na-teur
    [InlineData("français", new[] { 4 })]         // fran-çais — accented
    public void French_exception_words_hyphenate_at_exact_positions(string word, int[] expected)
    {
        Assert.True(HyphenationRegistry.IsRegistered("fr"));
        Assert.True(HyphenationRegistry.TryHyphenate("fr", word, out var breaks));
        Assert.Equal(expected, breaks);
    }

    [Fact]
    public void Register_is_idempotent()
    {
        // Repeated registration is a no-op guarded by an Interlocked flag; the registered data is stable.
        EuropeanHyphenation.Register();
        EuropeanHyphenation.Register();

        Assert.True(HyphenationRegistry.TryHyphenate("de", "Silbentrennung", out var a));
        Assert.True(HyphenationRegistry.TryHyphenate("de", "Silbentrennung", out var b));
        Assert.Equal(new[] { 3, 6, 10 }, a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Unregistered_language_returns_false()
    {
        Assert.False(HyphenationRegistry.TryHyphenate("zz", "whatever", out var breaks));
        Assert.Empty(breaks);
    }
}
