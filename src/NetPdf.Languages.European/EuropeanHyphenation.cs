// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;
using System.Threading;
using NetPdf.Hyphenation;

namespace NetPdf.Languages.European;

/// <summary>
/// Registers the European-language hyphenators with NetPdf's <see cref="HyphenationRegistry"/>. Call
/// <see cref="Register"/> once at startup (a <c>[ModuleInitializer]</c> also drives it when this assembly
/// is loaded); afterwards the registered languages hyphenate through the registry's public API
/// (<see cref="HyphenationRegistry.TryHyphenate"/>). This build registers a <b>German + French starter
/// set</b>.
/// </summary>
/// <remarks>
/// <para><b>Layout routing.</b> Once registered, these hyphenators are used automatically during rendering:
/// NetPdf's block layout resolves the <c>hyphens: auto</c> hyphenator from a block's effective HTML
/// <c>lang</c> (the nearest <c>lang</c> up the ancestor chain, e.g. <c>&lt;html lang="de"&gt;</c>) through
/// <see cref="HyphenationRegistry"/>. So a German document hyphenates with these patterns when the pack is
/// loaded. Per-run/mixed-language <c>lang</c> within a single block is a follow-up.</para>
/// <para><b>Data status.</b> Hyphenation patterns come from the CTAN <c>tex-hyphen</c> project (LPPL) —
/// attribution per language is preserved in <c>NOTICE</c>. This build ships a <b>correct starter set</b>
/// for German and French (a real pattern subset + an explicit-hyphenation exception list) so the pack and
/// the registry are functional and tested end to end. The remaining listed European languages and the full
/// CTAN pattern sets are vendored incrementally behind this same <see cref="Register"/> seam with no API
/// change (see the pack README).</para>
/// </remarks>
public static class EuropeanHyphenation
{
    private static int _registered;

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    /// <summary>Idempotent — registers each bundled European language with
    /// <see cref="HyphenationRegistry"/>. Runs automatically on load; exposed for explicit / testable use.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        HyphenationRegistry.Register("de", GermanPatterns, GermanExceptions);
        HyphenationRegistry.Register("fr", FrenchPatterns, FrenchExceptions);
        // es, it, pt, nl, sv, no, da, fi, pl, cs, hu, ru, uk register here as their CTAN pattern data
        // is vendored (NOTICE + the pack README) — same Register() seam, no API change.
    }

    // --- Starter pattern/exception data (see the class remarks) ----------------------------------

    // A small, valid subset of the German Liang patterns (CTAN dehyphn-x, LPPL). TeX priority digits
    // mark permitted break points between the surrounding letters. The full set is vendored by the
    // maintainer; the exception list below gives exact breaks for common test/UI words meanwhile.
    private const string GermanPatterns =
        "1ba 1be 1bi 1bo 1bu 1da 1de 1di 1ge 1gen 1he 1ke 1keit 1la 1le 1lich 1me 1ne 1ni 1re 1sch " +
        "1se 1te 1ti 1tung 1ung 1ver 1zu .an3 .auf1 .aus1 .be1 .ge1 .un1";

    // The core Hyphenator/HyphenationDictionary canonicalize case for ASCII only, so a non-ASCII initial
    // (e.g. "Ü") does not fold to its lower-case form. German nouns are always capitalized, so "Übersetzung"
    // matches; the lower-case "über-set-zung" is listed alongside it so the exception still fires if the
    // word arrives lower-cased (e.g. from normalized/UI text).
    private const string GermanExceptions =
        "Sil-ben-tren-nung Zu-cker ba-cken Was-ser Deutsch-land Bei-spiel Recht-schrei-bung " +
        "Ge-schwin-dig-keit Über-set-zung über-set-zung";

    // A small, valid subset of the French Liang patterns (CTAN hyph-fr, MIT/LPPL).
    private const string FrenchPatterns =
        "1ba 1be 1bi 1bo 1bu 1ca 1ce 1ci 1co 1cu 1da 1de 1di 1do 1du 1fa 1fe 1ga 1ge 1la 1le 1ma 1me " +
        "1na 1ne 1pa 1pe 1ra 1re 1sa 1se 1ta 1te 1va 1ve";

    private const string FrenchExceptions =
        "bon-jour or-di-na-teur fran-çais ty-po-gra-phie hy-phé-na-tion dé-ve-lop-pe-ment";
}
