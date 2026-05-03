// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Text.Bidi;

/// <summary>
/// Bidirectional character types per Unicode UAX #9 §"Bidirectional Character Types"
/// (https://www.unicode.org/reports/tr9/#Bidirectional_Character_Types). Every Unicode
/// codepoint maps to exactly one of these; the values are the keys the rest of the
/// algorithm walks through.
/// </summary>
/// <remarks>
/// The enum values match the abbreviations the UCD <c>DerivedBidiClass.txt</c> file
/// uses, which keeps validation against UCD test data (<c>BidiTest.txt</c>,
/// <c>BidiCharacterTest.txt</c>) straightforward in Stage 12.3.
/// </remarks>
internal enum BidiClass : byte
{
    // Strong types
    /// <summary>Left_To_Right — most ASCII letters, Latin, Greek, Cyrillic, Han, etc.</summary>
    L,

    /// <summary>Right_To_Left — Hebrew, Cypriot, etc.</summary>
    R,

    /// <summary>Arabic_Letter — Arabic and similar scripts (joining).</summary>
    AL,

    // Weak types
    /// <summary>European_Number — ASCII digits 0–9 and similar.</summary>
    EN,

    /// <summary>European_Separator — '+' and '-' between digits.</summary>
    ES,

    /// <summary>European_Terminator — '$', '%', '°' adjacent to numbers.</summary>
    ET,

    /// <summary>Arabic_Number — Arabic-Indic digits.</summary>
    AN,

    /// <summary>Common_Separator — comma, period, slash, colon between numbers.</summary>
    CS,

    /// <summary>Nonspacing_Mark — combining diacritics.</summary>
    NSM,

    /// <summary>Boundary_Neutral — control characters, default-ignorable.</summary>
    BN,

    // Neutral types
    /// <summary>Paragraph_Separator — newline, paragraph separators.</summary>
    B,

    /// <summary>Segment_Separator — tab.</summary>
    S,

    /// <summary>White_Space — space and similar.</summary>
    WS,

    /// <summary>Other_Neutral — punctuation, symbols.</summary>
    ON,

    // Explicit formatting (legacy embedding/override)
    /// <summary>Left_To_Right_Embedding (U+202A).</summary>
    LRE,

    /// <summary>Left_To_Right_Override (U+202D).</summary>
    LRO,

    /// <summary>Right_To_Left_Embedding (U+202B).</summary>
    RLE,

    /// <summary>Right_To_Left_Override (U+202E).</summary>
    RLO,

    /// <summary>Pop_Directional_Format (U+202C) — closes the most recent LRE/RLE/LRO/RLO.</summary>
    PDF,

    // Explicit isolates (Unicode 6.3+)
    /// <summary>Left_To_Right_Isolate (U+2066).</summary>
    LRI,

    /// <summary>Right_To_Left_Isolate (U+2067).</summary>
    RLI,

    /// <summary>First_Strong_Isolate (U+2068) — direction inferred from contained content.</summary>
    FSI,

    /// <summary>Pop_Directional_Isolate (U+2069) — closes the most recent isolate.</summary>
    PDI,
}
