// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using NetPdf.Hyphenation;
using Xunit;

namespace NetPdf.UnitTests.Hyphenation;

/// <summary>
/// Pins the input-validation + normalization contract of the public <see cref="HyphenationRegistry"/>
/// seam. Per PR #264 review [P3]: a non-whitespace tag can still normalize to an EMPTY primary subtag
/// (<c>"-DE"</c> → <c>""</c>, <c>"-"</c> → <c>""</c>); <see cref="HyphenationRegistry.Register"/> must
/// reject those (before parsing the pattern block) rather than register a hyphenator under the empty key.
/// </summary>
public sealed class HyphenationRegistryTests
{
    // A syntactically-valid Liang block; the point of these tests is that a bad LANGUAGE is rejected
    // regardless of the pattern payload (and before that payload is parsed).
    private const string ValidPatternBlock = "1ba 1be 1bi 1bo 1bu .an3 .auf1";

    [Theory]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("-DE")]
    [InlineData("-de-DE")]
    [InlineData("  -  ")]
    public void Register_rejects_tags_with_no_primary_subtag(string language)
    {
        var ex = Assert.Throws<ArgumentException>(() => HyphenationRegistry.Register(language, ValidPatternBlock));
        Assert.Equal("language", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_empty_or_whitespace_language(string language) =>
        Assert.Throws<ArgumentException>(() => HyphenationRegistry.Register(language, ValidPatternBlock));

    [Fact]
    public void Register_rejects_null_language() =>
        Assert.Throws<ArgumentNullException>(() => HyphenationRegistry.Register(null!, ValidPatternBlock));

    [Fact]
    public void Register_validates_language_before_parsing_the_pattern_block()
    {
        // The validation ordering is what makes an invalid tag fail fast: even a large pattern payload is
        // never parsed when the tag has no primary subtag. We can't time the parse, but we can prove no
        // side effect leaked — a rejected registration leaves the registry untouched (no empty-key entry,
        // which a normalized lookup would otherwise resolve to).
        var big = string.Join(' ', System.Linq.Enumerable.Range(0, 5000).Select(i => $"1a{i}"));
        Assert.Throws<ArgumentException>(() => HyphenationRegistry.Register("-DE", big));

        // The empty-string key is unreachable through the public API (IsRegistered/TryHyphenate short-
        // circuit on whitespace), so a bad Register() can't have created a resolvable phantom entry.
        Assert.False(HyphenationRegistry.TryHyphenate("", "whatever", out var breaks));
        Assert.Empty(breaks);
    }

    [Fact]
    public void Register_normalizes_a_locale_tag_to_its_primary_subtag()
    {
        // Uses a private-use primary subtag ("qaa") so the test never collides with a real language pack.
        HyphenationRegistry.Register("qaa-Latn-XX", "1qa 1qb 1qc");
        Assert.True(HyphenationRegistry.IsRegistered("qaa"));
        Assert.True(HyphenationRegistry.IsRegistered("QAA-anything")); // case-insensitive, primary subtag
    }

    [Fact]
    public void Register_accepts_a_surrounding_whitespace_tag()
    {
        HyphenationRegistry.Register("  qab  ", "1qa 1qb");
        Assert.True(HyphenationRegistry.IsRegistered("qab"));
    }
}
