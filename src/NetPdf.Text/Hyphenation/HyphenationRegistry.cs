// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using NetPdf.Text.Hyphenation;

namespace NetPdf.Hyphenation;

/// <summary>
/// The public extension seam for language hyphenation. The core ships American-English patterns
/// (pre-registered as <c>"en"</c>); the optional <c>NetPdf.Languages.*</c> NuGet packages register
/// additional languages here (via an explicit <c>Register(…)</c> call, which the packs also drive from a
/// <c>[ModuleInitializer]</c>) by handing over TeX-style Liang pattern text + an optional exception list.
/// Patterns registered through this seam are reachable via <see cref="TryHyphenate"/> and
/// <see cref="IsRegistered"/>.
/// </summary>
/// <remarks>
/// <para><b>Language keys.</b> Keys are normalized to the primary subtag, lower-cased (BCP-47
/// <c>de-DE</c> → <c>de</c>, <c>en-GB</c> → <c>en</c>), so a pack registering <c>"de"</c> serves all
/// German locales. A tag with no primary subtag after normalization (e.g. <c>"-DE"</c> or <c>"-"</c>) is
/// rejected by <see cref="Register"/>.</para>
/// <para><b>Thread-safety.</b> Registration and lookup are concurrent-safe. Registering a language that
/// is already present replaces it (last registration wins) — a pack can override the built-in English.</para>
/// <para><b>Layout routing (follow-up).</b> Wiring the block/inline layout pass to resolve a hyphenator
/// from this registry by the run's <c>lang</c> is a documented follow-up. Until it lands, <c>hyphens:
/// auto</c> layout hyphenates with the bundled English patterns regardless of the run language, and packs
/// are exercised through this registry's public API. The internal <see cref="ResolveOrDefault"/> seam is
/// where that routing will attach.</para>
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

        // Normalize + validate the key BEFORE parsing the (potentially large) pattern block, so a
        // malformed tag fails fast without paying for pattern parsing. A non-whitespace tag can still
        // normalize to an empty primary subtag ("-DE" → "", "-" → ""); reject it rather than register
        // a hyphenator under the empty-string key (which later lookups would never resolve intentionally).
        var key = Normalize(language);
        if (key.Length == 0)
        {
            throw new ArgumentException(
                $"Language tag '{language}' has no primary subtag after BCP-47 normalization.",
                nameof(language));
        }

        var patterns = HyphenationPatternSet.ParseBlock(patternBlock);
        var exceptions = string.IsNullOrEmpty(exceptionBlock)
            ? HyphenationDictionary.Empty
            : HyphenationDictionary.Parse(exceptionBlock);
        Hyphenators[key] = new Hyphenator(patterns, exceptions);
    }

    /// <summary>
    /// Register <paramref name="language"/> as one that does NOT soft-hyphenate — a script whose line
    /// breaking is handled elsewhere rather than by inserting hyphens (CJK: per-character breaking, UAX #14;
    /// Arabic: kashida/tatweel justification). This registers a no-op hyphenator, so <c>hyphens: auto</c>
    /// resolves to zero break points for the language (via <see cref="ResolveOrDefault"/>) instead of
    /// falling back to the bundled English hyphenator — which would otherwise hyphenate any embedded
    /// Latin-script runs in a document tagged for such a language. Same primary-subtag normalization +
    /// validation as <see cref="Register"/>.
    /// </summary>
    public static void RegisterNoHyphenation(string language) => Register(language, string.Empty);

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
