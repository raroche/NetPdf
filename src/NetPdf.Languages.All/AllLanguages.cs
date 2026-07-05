// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;
using System.Threading;
using NetPdf.Languages.Arabic;
using NetPdf.Languages.Cjk;
using NetPdf.Languages.European;
using NetPdf.Languages.Indic;

namespace NetPdf.Languages.All;

/// <summary>
/// Convenience aggregator for every <c>NetPdf.Languages.*</c> pack. Install the <c>NetPdf.Languages.All</c>
/// meta-package (which depends on the European, CJK, Arabic, and Indic packs) and call <see cref="Register"/>
/// once at startup to register all of them with NetPdf's <c>HyphenationRegistry</c> in one call — that
/// explicit call is the documented path. A <c>[ModuleInitializer]</c> also drives it as a best-effort
/// convenience, but only once this assembly is actually loaded (a package reference alone does not guarantee
/// that), so prefer the explicit call.
/// </summary>
public static class AllLanguages
{
    private static int _registered;

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    /// <summary>Idempotent — registers every bundled language pack (European, CJK, Arabic, Indic). Each
    /// pack's own <c>Register</c> is itself idempotent, so this is safe to call alongside them.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        EuropeanHyphenation.Register();
        CjkHyphenation.Register();
        ArabicHyphenation.Register();
        IndicHyphenation.Register();
    }
}
