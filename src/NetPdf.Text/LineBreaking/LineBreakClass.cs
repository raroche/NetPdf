// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.LineBreaking;

/// <summary>
/// Line-break property classes per Unicode UAX #14
/// (<c>https://www.unicode.org/reports/tr14/</c>) and UCD <c>LineBreak.txt</c> 16.0.
/// Every Unicode codepoint maps to exactly one of these; the values are the keys the
/// rule engine consults for each pair of adjacent characters.
/// </summary>
/// <remarks>
/// <para>
/// Names match the abbreviations the UCD file uses, which keeps validation against the
/// UCD <c>LineBreakTest.txt</c> conformance suite straightforward. Some classes are
/// "tailorable" (their treatment may vary by locale or context) and some are
/// "non-tailorable" (always behave identically).
/// </para>
/// <para>
/// <b>Brahmic conjunct classes (AK, AP, AS, VF, VI).</b> Added in Unicode 15.x to model
/// the dependent-vowel / virama / pre-base behavior of Brahmic scripts. UAX #14 LB28a
/// uses these classes to forbid breaking inside a conjunct cluster (e.g., a Devanagari
/// consonant cluster joined by a virama).
/// </para>
/// </remarks>
internal enum LineBreakClass : byte
{
    // Non-tailorable
    /// <summary>Mandatory_Break — paragraph separator (U+2029), record separators, etc.</summary>
    BK,

    /// <summary>Carriage_Return — U+000D.</summary>
    CR,

    /// <summary>Line_Feed — U+000A.</summary>
    LF,

    /// <summary>Combining_Mark — combining diacritics; attach to the preceding base via LB9.</summary>
    CM,

    /// <summary>Next_Line — U+0085 NEL.</summary>
    NL,

    /// <summary>Surrogate — UTF-16 surrogates; LB1 maps to AL.</summary>
    SG,

    /// <summary>Word_Joiner — U+2060; prohibits breaks before/after.</summary>
    WJ,

    /// <summary>Zero_Width_Space — U+200B; allows break after.</summary>
    ZW,

    /// <summary>Glue — non-breaking space etc.; prohibits break before/after.</summary>
    GL,

    /// <summary>Space — U+0020 and similar; allows break in many contexts.</summary>
    SP,

    /// <summary>Zero_Width_Joiner — U+200D; prohibits break after (LB8a) and influences LB30b.</summary>
    ZWJ,

    // Tailorable — break opportunities
    /// <summary>Break_Opportunity_Before_And_After — em-dash etc.</summary>
    B2,

    /// <summary>Break_After — break is allowed after this character.</summary>
    BA,

    /// <summary>Break_Before — break is allowed before this character.</summary>
    BB,

    /// <summary>Hyphen — U+002D etc.</summary>
    HY,

    /// <summary>Contingent_Break_Opportunity — object replacement char; behavior depends on caller. LB1 maps to ID.</summary>
    CB,

    // Tailorable — characters prohibiting certain breaks
    /// <summary>Close_Punctuation — closing brackets etc.</summary>
    CL,

    /// <summary>Close_Parenthesis — ASCII close parenthesis ')' specifically (separated from CL for LB30 East-Asian rules).</summary>
    CP,

    /// <summary>Exclamation_or_Interrogation — '!' '?' etc.</summary>
    EX,

    /// <summary>Inseparable — characters that should not be separated, like some Western European punctuation.</summary>
    IN,

    /// <summary>Nonstarter — characters that cannot start a line, e.g., small kana.</summary>
    NS,

    /// <summary>Open_Punctuation — opening brackets etc.</summary>
    OP,

    /// <summary>Quotation — quotation marks.</summary>
    QU,

    /// <summary>Hebrew_Letter — Hebrew letters; treated specially in LB21a.</summary>
    HL,

    // Tailorable — numeric context
    /// <summary>Infix_Numeric_Separator — '.' ',' between digits.</summary>
    IS,

    /// <summary>Numeric — digits.</summary>
    NU,

    /// <summary>Postfix_Numeric — '%' '°' etc. after a number.</summary>
    PO,

    /// <summary>Prefix_Numeric — '$' '€' etc. before a number.</summary>
    PR,

    /// <summary>Symbols_Allowing_Break_After — '/' between numbers etc.</summary>
    SY,

    // Tailorable — other
    /// <summary>Ambiguous (Alphabetic or Ideograph) — context-sensitive; LB1 maps to AL.</summary>
    AI,

    /// <summary>Alphabetic — Latin, Greek, Cyrillic letters; the most common letter class.</summary>
    AL,

    /// <summary>Conditional_Japanese_Starter — small kana; LB1 maps to NS in default context.</summary>
    CJ,

    /// <summary>Emoji_Base — emoji that take a modifier; pairs with EM via LB30b.</summary>
    EB,

    /// <summary>Emoji_Modifier — modifier (skin tone) for an Emoji_Base.</summary>
    EM,

    /// <summary>Hangul_LV_Syllable.</summary>
    H2,

    /// <summary>Hangul_LVT_Syllable.</summary>
    H3,

    /// <summary>Ideographic — Han, Hiragana, Katakana, etc.</summary>
    ID,

    /// <summary>Hangul_L_Jamo.</summary>
    JL,

    /// <summary>Hangul_T_Jamo.</summary>
    JT,

    /// <summary>Hangul_V_Jamo.</summary>
    JV,

    /// <summary>Regional_Indicator — flag emoji components; pair via LB30a.</summary>
    RI,

    /// <summary>Complex_Context_Dependent — Brahmic Asian scripts (Burmese, Khmer, Lao, Tai Tham, Thai, etc.). Default treats as AL.</summary>
    SA,

    /// <summary>Unknown — unassigned codepoints; LB1 maps to AL.</summary>
    XX,

    // Brahmic-script-specific (Unicode 15+)
    /// <summary>Aksara — Brahmic consonant; participates in LB28a conjunct rules.</summary>
    AK,

    /// <summary>Aksara_Pre_Base — pre-base Brahmic; LB28a.</summary>
    AP,

    /// <summary>Aksara_Start — Brahmic dotted-circle / start; LB28a.</summary>
    AS,

    /// <summary>Virama_Final — Brahmic word-final virama; LB28a.</summary>
    VF,

    /// <summary>Virama — Brahmic virama; LB28a.</summary>
    VI,
}
