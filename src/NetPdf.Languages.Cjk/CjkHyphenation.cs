// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;
using System.Threading;
using NetPdf.Hyphenation;

namespace NetPdf.Languages.Cjk;

/// <summary>
/// Registers the CJK languages — Chinese (<c>zh</c>), Japanese (<c>ja</c>), Korean (<c>ko</c>) — with
/// NetPdf's <see cref="HyphenationRegistry"/> as explicit <b>no-hyphenation</b> languages. CJK scripts do
/// not use Liang/soft hyphenation: line breaking is per-character (handled by NetPdf's UAX #14 breaker).
/// Registering them makes <c>hyphens: auto</c> insert no hyphens for zh/ja/ko instead of falling back to
/// the bundled English hyphenator (which would otherwise hyphenate embedded Latin-script runs). Call
/// <see cref="Register"/> once at startup; a <c>[ModuleInitializer]</c> also drives it on assembly load.
/// </summary>
/// <remarks>
/// Richer CJK line-break tailoring (<i>kinsoku shori</i> — prohibited line-start/-end characters) is a
/// documented follow-up; it would attach to a future layout line-break seam, not this hyphenation registry.
/// </remarks>
public static class CjkHyphenation
{
    private static int _registered;

    [ModuleInitializer]
    internal static void AutoRegister() => Register();

    /// <summary>Idempotent — registers zh/ja/ko with <see cref="HyphenationRegistry"/> as no-hyphenation
    /// languages. Runs automatically on load; exposed for explicit / testable use.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        HyphenationRegistry.RegisterNoHyphenation("zh"); // Chinese
        HyphenationRegistry.RegisterNoHyphenation("ja"); // Japanese
        HyphenationRegistry.RegisterNoHyphenation("ko"); // Korean
    }
}
