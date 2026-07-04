// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;
using System.Threading;
using NetPdf.Hyphenation;

namespace NetPdf.Languages.Arabic;

/// <summary>
/// Registers the Arabic-script languages — Arabic (<c>ar</c>), Persian/Farsi (<c>fa</c>), Urdu (<c>ur</c>)
/// — with NetPdf's <see cref="HyphenationRegistry"/> as explicit <b>no-hyphenation</b> languages. Arabic
/// script is not soft-hyphenated: justification is achieved with kashida/tatweel elongation, not by
/// inserting hyphens. Registering them makes <c>hyphens: auto</c> insert no hyphens for those languages
/// instead of falling back to the bundled English hyphenator (which would otherwise hyphenate embedded
/// Latin-script runs). Call <see cref="Register"/> once at startup; a <c>[ModuleInitializer]</c> also
/// drives it on assembly load.
/// </summary>
/// <remarks>
/// Kashida justification itself is a separate typographic feature (a future layout/justification seam), not
/// part of this hyphenation registry.
/// </remarks>
public static class ArabicHyphenation
{
    private static int _registered;

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    /// <summary>Idempotent — registers ar/fa/ur with <see cref="HyphenationRegistry"/> as no-hyphenation
    /// languages. Runs automatically on load; exposed for explicit / testable use.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        HyphenationRegistry.RegisterNoHyphenation("ar"); // Arabic
        HyphenationRegistry.RegisterNoHyphenation("fa"); // Persian / Farsi
        HyphenationRegistry.RegisterNoHyphenation("ur"); // Urdu
    }
}
