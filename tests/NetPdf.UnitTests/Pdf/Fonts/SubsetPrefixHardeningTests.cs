// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Pdf.Fonts;
using Xunit;

namespace NetPdf.UnitTests.Pdf.Fonts;

/// <summary>
/// Post-Task-8 hardening: <see cref="SubsetPrefix"/> uses NFC-normalized UTF-8 so distinct
/// international font names produce distinct prefixes (the previous ASCII path silently
/// collided non-ASCII names by replacing characters with <c>?</c>).
/// </summary>
public sealed class SubsetPrefixHardeningTests
{
    [Fact]
    public void Distinct_non_ascii_names_produce_distinct_prefixes()
    {
        // Under the previous ASCII-fallback path both names hashed the same input
        // ("S?hne") and produced the same prefix. UTF-8 + NFC distinguishes them.
        var a = SubsetPrefix.Derive("Söhne", new[] { 0, 1 });
        var b = SubsetPrefix.Derive("Sühne", new[] { 0, 1 });
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Decomposed_and_precomposed_forms_of_the_same_name_match_after_NFC()
    {
        // U+00F6 (precomposed ö) vs U+006F U+0308 (o + combining diaeresis) — equal under NFC.
        var precomposed = "Söhne";
        var decomposed = "Söhne";
        Assert.Equal(SubsetPrefix.Derive(precomposed, new[] { 0 }),
                     SubsetPrefix.Derive(decomposed, new[] { 0 }));
    }

    [Fact]
    public void CJK_family_name_produces_six_letter_prefix()
    {
        var prefix = SubsetPrefix.Derive("源ノ角ゴシック", new[] { 0, 1, 2 });
        Assert.Equal(6, prefix.Length);
        foreach (var c in prefix)
        {
            Assert.InRange(c, 'A', 'Z');
        }
    }
}
