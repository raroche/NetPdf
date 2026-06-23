// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Text.Bidi;
using Xunit;
using Script = NetPdf.Text.Bidi.UnicodeScripts.UnicodeScript;

namespace NetPdf.UnitTests.Text.Bidi;

/// <summary>UAX #24 script-property table (<see cref="UnicodeScripts"/>) used for script-change
/// itemization (uax-24-script-detection). The internal <see cref="Script"/> enum is used only in
/// method bodies (not public signatures) to keep the public test methods accessible.</summary>
public class UnicodeScriptsTests
{
    [Fact]
    public void GetScript_classifies_representative_codepoints()
    {
        Assert.Equal(Script.Latin, UnicodeScripts.GetScript('A'));
        Assert.Equal(Script.Latin, UnicodeScripts.GetScript('z'));
        Assert.Equal(Script.Latin, UnicodeScripts.GetScript('é'));      // U+00E9 Latin-1 Supplement
        Assert.Equal(Script.Greek, UnicodeScripts.GetScript('Ω'));      // U+03A9
        Assert.Equal(Script.Cyrillic, UnicodeScripts.GetScript('Я'));   // U+042F
        Assert.Equal(Script.Hebrew, UnicodeScripts.GetScript('א'));     // U+05D0
        Assert.Equal(Script.Arabic, UnicodeScripts.GetScript('م'));     // U+0645
        Assert.Equal(Script.Thai, UnicodeScripts.GetScript('ก'));       // U+0E01
        Assert.Equal(Script.Devanagari, UnicodeScripts.GetScript('अ')); // U+0905
        Assert.Equal(Script.Han, UnicodeScripts.GetScript('中'));        // U+4E2D
        Assert.Equal(Script.Hiragana, UnicodeScripts.GetScript('あ'));  // U+3042
        Assert.Equal(Script.Katakana, UnicodeScripts.GetScript('ア'));  // U+30A2
        Assert.Equal(Script.Hangul, UnicodeScripts.GetScript('한'));    // U+D55C Hangul Syllables
        Assert.Equal(Script.Han, UnicodeScripts.GetScript(0x20000));    // CJK Ext B (supplementary)
    }

    [Fact]
    public void GetScript_returns_common_for_digits_punctuation_and_spaces()
    {
        Assert.Equal(Script.Common, UnicodeScripts.GetScript(' '));
        Assert.Equal(Script.Common, UnicodeScripts.GetScript('1'));
        Assert.Equal(Script.Common, UnicodeScripts.GetScript('.'));
        Assert.Equal(Script.Common, UnicodeScripts.GetScript('!'));
    }

    [Fact]
    public void GetScript_returns_common_for_uncovered_assigned_ranges_documenting_the_block_approximation()
    {
        // uax-24-script-detection (narrowed) — the table is a block-based APPROXIMATION, so assigned
        // codepoints outside the enumerated blocks resolve to Common (the surrounding / caller-uniform
        // script) rather than their exact UAX #24 Script. These lock that known behavior until an exact
        // UCD Scripts.txt table replaces the blocks; if a future range gets covered, flip the expectation.
        Assert.Equal(Script.Common, UnicodeScripts.GetScript(0x2A700)); // CJK Ext C (Han, past Ext B 0x2A6DF)
        Assert.Equal(Script.Common, UnicodeScripts.GetScript(0x08A0));  // Arabic Extended-A
        Assert.Equal(Script.Common, UnicodeScripts.GetScript(0x03E2));  // Coptic (carved out of the Greek block)
        Assert.Equal(Script.Common, UnicodeScripts.GetScript(0x13A0));  // Cherokee (rare, not enumerated)
    }

    [Fact]
    public void ToIso15924_maps_scripts_to_tags()
    {
        Assert.Equal("Latn", UnicodeScripts.ToIso15924(Script.Latin));
        Assert.Equal("Arab", UnicodeScripts.ToIso15924(Script.Arabic));
        Assert.Equal("Hebr", UnicodeScripts.ToIso15924(Script.Hebrew));
        Assert.Equal("Hani", UnicodeScripts.ToIso15924(Script.Han));
        Assert.Equal("Hang", UnicodeScripts.ToIso15924(Script.Hangul));
        // ISO 15924 Common — the caller substitutes the paragraph's uniform script.
        Assert.Equal("Zyyy", UnicodeScripts.ToIso15924(Script.Common));
    }
}
