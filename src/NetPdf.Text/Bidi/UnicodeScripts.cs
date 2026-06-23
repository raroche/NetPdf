// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Text.Bidi;

/// <summary>
/// UAX #24 (Unicode Script Property) — maps a codepoint to its script, for itemization into
/// script-homogeneous shaping runs. Each script has different OpenType feature requirements
/// (Arabic joining, Indic reordering, Han vertical forms…), so a Latin run can't share a HarfBuzz
/// shaping pass with a Hebrew or Devanagari run even at the same bidi level + font.
/// </summary>
/// <remarks>
/// <para>The table is a sorted, non-overlapping range array covering the MAJOR scripts that appear
/// in real documents and need distinct shaping (the ~30 scripts below). It is derived from the
/// Unicode Character Database (UAX #24 + the block assignments), per the clean-room policy — DATA,
/// not another engine's code. Codepoints outside the table — ASCII digits / punctuation, spaces,
/// the <c>Common</c> and <c>Inherited</c> script values (combining marks), and the long tail of
/// rare scripts — resolve to <see cref="UnicodeScript.Common"/>; per UAX #24 §5.1 they take the
/// script of the surrounding text, so the itemizer does NOT start a new run at a Common codepoint
/// (it extends the run in progress, and a leading Common prefix falls back to the caller's uniform
/// script). This keeps mixed-script paragraphs shaping correctly for the covered scripts while a
/// rare uncovered script degrades to the surrounding/uniform script (no worse than the pre-UAX-24
/// single-script approximation).</para>
/// </remarks>
internal static class UnicodeScripts
{
    /// <summary>The covered scripts. <see cref="Common"/> is the catch-all for Common/Inherited and
    /// any codepoint outside the table (resolved to the surrounding script by the itemizer).</summary>
    internal enum UnicodeScript : byte
    {
        Common = 0,
        Latin, Greek, Cyrillic, Armenian, Hebrew, Arabic, Syriac, Thaana,
        Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Sinhala,
        Thai, Lao, Tibetan, Myanmar, Georgian, Ethiopic, Khmer, Mongolian,
        Hangul, Hiragana, Katakana, Bopomofo, Han,
    }

    private readonly record struct ScriptRange(int Start, int End, UnicodeScript Script);

    // Sorted by Start, non-overlapping. Primary block ranges for each covered script (UAX #24).
    private static readonly ScriptRange[] Ranges =
    [
        new(0x0041, 0x005A, UnicodeScript.Latin),   // A-Z
        new(0x0061, 0x007A, UnicodeScript.Latin),   // a-z
        new(0x00C0, 0x024F, UnicodeScript.Latin),   // Latin-1 Suppl letters + Latin Extended-A/B
        new(0x0250, 0x02AF, UnicodeScript.Latin),   // IPA Extensions
        new(0x0370, 0x03FF, UnicodeScript.Greek),   // Greek and Coptic
        new(0x0400, 0x052F, UnicodeScript.Cyrillic),// Cyrillic + Cyrillic Suppl
        new(0x0531, 0x058F, UnicodeScript.Armenian),// Armenian
        new(0x0591, 0x05FF, UnicodeScript.Hebrew),  // Hebrew
        new(0x0600, 0x06FF, UnicodeScript.Arabic),  // Arabic
        new(0x0700, 0x074F, UnicodeScript.Syriac),  // Syriac
        new(0x0750, 0x077F, UnicodeScript.Arabic),  // Arabic Supplement
        new(0x0780, 0x07BF, UnicodeScript.Thaana),  // Thaana
        new(0x0900, 0x097F, UnicodeScript.Devanagari),
        new(0x0980, 0x09FF, UnicodeScript.Bengali),
        new(0x0A00, 0x0A7F, UnicodeScript.Gurmukhi),
        new(0x0A80, 0x0AFF, UnicodeScript.Gujarati),
        new(0x0B00, 0x0B7F, UnicodeScript.Oriya),
        new(0x0B80, 0x0BFF, UnicodeScript.Tamil),
        new(0x0C00, 0x0C7F, UnicodeScript.Telugu),
        new(0x0C80, 0x0CFF, UnicodeScript.Kannada),
        new(0x0D00, 0x0D7F, UnicodeScript.Malayalam),
        new(0x0D80, 0x0DFF, UnicodeScript.Sinhala),
        new(0x0E00, 0x0E7F, UnicodeScript.Thai),
        new(0x0E80, 0x0EFF, UnicodeScript.Lao),
        new(0x0F00, 0x0FFF, UnicodeScript.Tibetan),
        new(0x1000, 0x109F, UnicodeScript.Myanmar),
        new(0x10A0, 0x10FF, UnicodeScript.Georgian),
        new(0x1100, 0x11FF, UnicodeScript.Hangul),  // Hangul Jamo
        new(0x1200, 0x139F, UnicodeScript.Ethiopic),
        new(0x1780, 0x17FF, UnicodeScript.Khmer),
        new(0x1800, 0x18AF, UnicodeScript.Mongolian),
        new(0x1E00, 0x1EFF, UnicodeScript.Latin),   // Latin Extended Additional
        new(0x1F00, 0x1FFF, UnicodeScript.Greek),   // Greek Extended
        new(0x2E80, 0x2EFF, UnicodeScript.Han),     // CJK Radicals Supplement
        new(0x3040, 0x309F, UnicodeScript.Hiragana),
        new(0x30A0, 0x30FF, UnicodeScript.Katakana),
        new(0x3100, 0x312F, UnicodeScript.Bopomofo),
        new(0x3130, 0x318F, UnicodeScript.Hangul),  // Hangul Compatibility Jamo
        new(0x31F0, 0x31FF, UnicodeScript.Katakana),// Katakana Phonetic Extensions
        new(0x3400, 0x4DBF, UnicodeScript.Han),     // CJK Unified Ideographs Extension A
        new(0x4E00, 0x9FFF, UnicodeScript.Han),     // CJK Unified Ideographs
        new(0xAC00, 0xD7A3, UnicodeScript.Hangul),  // Hangul Syllables
        new(0xF900, 0xFAFF, UnicodeScript.Han),     // CJK Compatibility Ideographs
        new(0xFB1D, 0xFB4F, UnicodeScript.Hebrew),  // Hebrew presentation forms
        new(0xFB50, 0xFDFF, UnicodeScript.Arabic),  // Arabic Presentation Forms-A
        new(0xFE70, 0xFEFF, UnicodeScript.Arabic),  // Arabic Presentation Forms-B
        new(0x20000, 0x2A6DF, UnicodeScript.Han),   // CJK Unified Ideographs Extension B
    ];

    /// <summary>The UAX #24 script of <paramref name="codepoint"/>, or <see cref="UnicodeScript.Common"/>
    /// for Common/Inherited and any codepoint outside the covered table.</summary>
    public static UnicodeScript GetScript(int codepoint)
    {
        var ranges = Ranges;
        var lo = 0;
        var hi = ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var r = ranges[mid];
            if (codepoint < r.Start) hi = mid - 1;
            else if (codepoint > r.End) lo = mid + 1;
            else return r.Script;
        }
        return UnicodeScript.Common;
    }

    /// <summary>The ISO 15924 four-letter tag HarfBuzz consumes for <paramref name="script"/>.
    /// <see cref="UnicodeScript.Common"/> maps to <c>"Zyyy"</c> (the ISO 15924 Common code), which the
    /// caller replaces with the paragraph's uniform script.</summary>
    public static string ToIso15924(UnicodeScript script) => script switch
    {
        UnicodeScript.Latin => "Latn",
        UnicodeScript.Greek => "Grek",
        UnicodeScript.Cyrillic => "Cyrl",
        UnicodeScript.Armenian => "Armn",
        UnicodeScript.Hebrew => "Hebr",
        UnicodeScript.Arabic => "Arab",
        UnicodeScript.Syriac => "Syrc",
        UnicodeScript.Thaana => "Thaa",
        UnicodeScript.Devanagari => "Deva",
        UnicodeScript.Bengali => "Beng",
        UnicodeScript.Gurmukhi => "Guru",
        UnicodeScript.Gujarati => "Gujr",
        UnicodeScript.Oriya => "Orya",
        UnicodeScript.Tamil => "Taml",
        UnicodeScript.Telugu => "Telu",
        UnicodeScript.Kannada => "Knda",
        UnicodeScript.Malayalam => "Mlym",
        UnicodeScript.Sinhala => "Sinh",
        UnicodeScript.Thai => "Thai",
        UnicodeScript.Lao => "Laoo",
        UnicodeScript.Tibetan => "Tibt",
        UnicodeScript.Myanmar => "Mymr",
        UnicodeScript.Georgian => "Geor",
        UnicodeScript.Ethiopic => "Ethi",
        UnicodeScript.Khmer => "Khmr",
        UnicodeScript.Mongolian => "Mong",
        UnicodeScript.Hangul => "Hang",
        UnicodeScript.Hiragana => "Hira",
        UnicodeScript.Katakana => "Kana",
        UnicodeScript.Bopomofo => "Bopo",
        UnicodeScript.Han => "Hani",
        _ => "Zyyy",
    };
}
