// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using NetPdf.Text.Hyphenation;

namespace NetPdf.Hyphenation;

/// <summary>
/// The public extension seam for language hyphenation. The core ships American-English patterns
/// (pre-registered as <c>"en"</c>); the optional <c>NetPdf.Languages.*</c> NuGet packages register
/// additional languages here (from a <c>[ModuleInitializer]</c> on load) by handing over TeX-style
/// Liang pattern text + an optional exception list. Layout resolves a hyphenator by the run's language
/// tag; an unregistered language falls back to the core English hyphenator.
/// </summary>
/// <remarks>
/// <para><b>Language keys.</b> Keys are normalized to the primary subtag, lower-cased (BCP-47
/// <c>de-DE</c> → <c>de</c>, <c>en-GB</c> → <c>en</c>), so a pack registering <c>"de"</c> serves all
/// German locales.</para>
/// <para><b>Thread-safety.</b> Registration and lookup are concurrent-safe. Registering a language that
/// is already present replaces it (last registration wins) — a pack can override the built-in English.</para>
/// </remarks>
public static class HyphenationRegistry
{
    private static readonly ConcurrentDictionary<string, Hyphenator> Hyphenators = new(StringComparer.Ordinal);

    static HyphenationRegistry()
    {
        // The core's bundled American-English hyphenator is always available under "en".
        Hyphenators["en"] = EnUsHyphenation.Default;
    }

    /// <summary>
    /// Register (or replace) the hyphenator for <paramref name="language"/> from TeX-style Liang
    /// <paramref name="patternBlock"/> text (whitespace/newline-separated patterns like <c>.aus1</c>,
    /// TeX <c>%</c> comments stripped) and an optional <paramref name="exceptionBlock"/> of explicit
    /// hyphenations (whitespace-separated words like <c>Sil-ben-tren-nung</c>). Called by the
    /// <c>NetPdf.Languages.*</c> packs on load; may also be called directly to supply custom patterns.
    /// </summary>
    public static void Register(string language, string patternBlock, string? exceptionBlock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(patternBlock);

        var patterns = HyphenationPatternSet.ParseBlock(patternBlock);
        var exceptions = string.IsNullOrEmpty(exceptionBlock)
            ? HyphenationDictionary.Empty
            : HyphenationDictionary.Parse(exceptionBlock);
        Hyphenators[Normalize(language)] = new Hyphenator(patterns, exceptions);
    }

    /// <summary>Whether a hyphenator is registered for <paramref name="language"/> (by primary subtag).</summary>
    public static bool IsRegistered(string language) =>
        !string.IsNullOrWhiteSpace(language) && Hyphenators.ContainsKey(Normalize(language));

    /// <summary>
    /// Find the hyphenation break positions for <paramref name="word"/> in <paramref name="language"/>.
    /// A break position <c>k</c> means "a soft break may go between <c>word[k-1]</c> and <c>word[k]</c>".
    /// Returns <see langword="false"/> (and an empty array) when the language isn't registered.
    /// </summary>
    public static bool TryHyphenate(
        string language, ReadOnlySpan<char> word, out int[] breaks, int leftMin = 2, int rightMin = 3)
    {
        breaks = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(language) || !Hyphenators.TryGetValue(Normalize(language), out var h))
        {
            return false;
        }

        breaks = h.FindHyphenationPoints(word, leftMin, rightMin);
        return true;
    }

    /// <summary>Layout seam — resolve the hyphenator for a language, or the core English default when
    /// the language isn't registered (so a document in an unsupported language still hyphenates plausibly
    /// rather than not at all).</summary>
    internal static Hyphenator ResolveOrDefault(string? language) =>
        !string.IsNullOrWhiteSpace(language) && Hyphenators.TryGetValue(Normalize(language), out var h)
            ? h
            : EnUsHyphenation.Default;

    private static string Normalize(string language)
    {
        var s = language.AsSpan().Trim();
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            s = s[..dash];
        }

        return s.ToString().ToLowerInvariant();
    }
}
