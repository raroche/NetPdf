// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;
using System.Threading;
using NetPdf.Hyphenation;

namespace NetPdf.Languages.Indic;

/// <summary>
/// Registers the major Indic-script languages with NetPdf's <see cref="HyphenationRegistry"/> so layout
/// routes hyphenation by a run's <c>lang</c>.
/// </summary>
/// <remarks>
/// <para><b>Data status.</b> Indic scripts <em>do</em> hyphenate (at akshara/syllable boundaries), unlike
/// CJK or Arabic — but this build ships <b>no pattern data yet</b>. The real CTAN <c>hyph-utf8</c> (LPPL)
/// Devanagari / Bengali / Tamil / Telugu / … pattern sets are vendored by the maintainer (see the pack
/// README + <c>NOTICE</c>); they drop in behind these same <see cref="Register"/> calls with no API change.
/// Until then each language is registered with an EMPTY (placeholder) hyphenator: <c>hyphens: auto</c>
/// produces no breaks for them — deliberately conservative, so an Indic-tagged document is never hyphenated
/// with the wrong English rules. This is a placeholder pending data, <b>not</b> an assertion that Indic
/// scripts don't hyphenate.</para>
/// </remarks>
public static class IndicHyphenation
{
    private static int _registered;

    // BCP-47 primary subtags for the Indic-script languages this pack covers.
    private static readonly string[] Languages =
    [
        "hi", // Hindi (Devanagari)
        "sa", // Sanskrit (Devanagari)
        "mr", // Marathi (Devanagari)
        "ne", // Nepali (Devanagari)
        "bn", // Bengali
        "as", // Assamese
        "pa", // Panjabi (Gurmukhi)
        "gu", // Gujarati
        "or", // Odia
        "ta", // Tamil
        "te", // Telugu
        "kn", // Kannada
        "ml", // Malayalam
    ];

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    /// <summary>Idempotent — registers the Indic languages with <see cref="HyphenationRegistry"/> (empty
    /// placeholder hyphenators pending vendored CTAN pattern data). Runs automatically on load; exposed for
    /// explicit / testable use.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        // Register(lang, "") — NOT RegisterNoHyphenation: these languages DO hyphenate; this is a
        // placeholder registration pending the maintainer-vendored pattern data (see the class remarks).
        foreach (var lang in Languages)
        {
            HyphenationRegistry.Register(lang, string.Empty);
        }
    }

    /// <summary>The BCP-47 primary subtags registered by this pack (in registration order).</summary>
    public static System.Collections.Generic.IReadOnlyList<string> RegisteredLanguages => Languages;
}
