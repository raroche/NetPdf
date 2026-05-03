// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO.Compression;
using System.Reflection;

namespace NetPdf.Text.Hyphenation;

/// <summary>
/// Loader for the bundled American-English Liang hyphenation pattern set. Patterns
/// originate from <c>hyph-en-us.tex</c> (Gerard D.C. Kuiken, 1990–2005, derived from
/// D. E. Knuth's <c>hyphen.tex</c>) and are redistributed here under the file's
/// permissive notice — see <c>THIRD-PARTY-NOTICES.md</c> for full attribution.
/// </summary>
/// <remarks>
/// <para>
/// The bundled set carries 4,952 patterns plus 14 exception words, sufficient for
/// production English typesetting. Loading is deferred until first access via
/// <see cref="Default"/>; the parsed pattern set and hyphenator are cached for the
/// process lifetime (frozen-dictionary lookup is the steady-state hot path).
/// </para>
/// </remarks>
internal static class EnUsHyphenation
{
    private const string PatternsResource = "NetPdf.Text.Hyphenation.Resources.en-us-patterns.txt.gz";
    private const string ExceptionsResource = "NetPdf.Text.Hyphenation.Resources.en-us-exceptions.txt";

    private static readonly Lazy<HyphenationPatternSet> LazyPatterns = new(LoadPatterns);
    private static readonly Lazy<HyphenationDictionary> LazyExceptions = new(LoadExceptions);
    private static readonly Lazy<Hyphenator> LazyHyphenator = new(() =>
        new Hyphenator(LazyPatterns.Value, LazyExceptions.Value));

    /// <summary>The bundled en-us pattern set. Lazily loaded on first access.</summary>
    public static HyphenationPatternSet Patterns => LazyPatterns.Value;

    /// <summary>The bundled en-us exception dictionary. Lazily loaded on first access.</summary>
    public static HyphenationDictionary Exceptions => LazyExceptions.Value;

    /// <summary>The default <see cref="Hyphenator"/> for American English.</summary>
    public static Hyphenator Default => LazyHyphenator.Value;

    private static HyphenationPatternSet LoadPatterns()
    {
        var assembly = typeof(EnUsHyphenation).Assembly;
        using var stream = assembly.GetManifestResourceStream(PatternsResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{PatternsResource}' missing from {assembly.GetName().Name}.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var text = reader.ReadToEnd();
        return HyphenationPatternSet.ParseBlock(text);
    }

    private static HyphenationDictionary LoadExceptions()
    {
        var assembly = typeof(EnUsHyphenation).Assembly;
        using var stream = assembly.GetManifestResourceStream(ExceptionsResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ExceptionsResource}' missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return HyphenationDictionary.Parse(text);
    }
}
