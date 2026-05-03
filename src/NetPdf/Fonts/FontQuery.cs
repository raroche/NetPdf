// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// A request to find a font face. <see cref="Family"/> is the CSS-resolved family name
/// (after generic-family expansion). The query maps directly onto the three CSS Fonts
/// Module Level 4 §5.2 matching axes: <see cref="StretchCss"/>, <see cref="Style"/>,
/// and <see cref="WeightCss"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Italic vs oblique policy.</b> The default <see cref="SystemFontResolver"/> treats
/// <see cref="FontStyle.Italic"/> and <see cref="FontStyle.Oblique"/> as synonyms when
/// matching against system fonts — CSS Fonts Module Level 4 §5.2 explicitly permits this:
/// "User agents may treat italic and oblique as synonyms when matching." Custom
/// <see cref="IFontResolver"/> implementations are free to distinguish them; the
/// underlying parsed metadata preserves both flags.
/// </para>
/// </remarks>
public readonly record struct FontQuery
{
    /// <summary>The CSS-resolved family name (case-insensitive ASCII match).</summary>
    public required string Family { get; init; }

    /// <summary>
    /// CSS numeric weight in the range <c>1..1000</c>. <c>400</c> = normal; <c>700</c> = bold.
    /// Values outside <c>1..1000</c> are not part of the public contract — resolvers may
    /// clamp or reject such values per their own policy.
    /// </summary>
    public required int WeightCss { get; init; }

    /// <summary>The CSS <c>font-style</c> axis. Defaults to <see cref="FontStyle.Normal"/>.</summary>
    public FontStyle Style { get; init; }

    /// <summary>
    /// CSS stretch (font-stretch) in the range <c>1..9</c>. <c>5</c> = normal width;
    /// <c>1</c> = ultra-condensed; <c>9</c> = ultra-expanded. Null means "unspecified" —
    /// the default <see cref="SystemFontResolver"/> treats null as <c>5</c> (normal width)
    /// per CSS Fonts Level 4 §5.2.3 default. Values outside <c>1..9</c> are clamped to that
    /// range by the default resolver; custom resolvers may apply a different policy.
    /// </summary>
    public int? StretchCss { get; init; }

    /// <summary>Optional script tag (e.g. <c>"Latn"</c>, <c>"Arab"</c>) — reserved for future shaping integration.</summary>
    public string? Script { get; init; }

    /// <summary>Optional BCP 47 language tag — reserved for future locale-aware matching.</summary>
    public string? Language { get; init; }
}

public enum FontStyle
{
    Normal,
    Italic,
    Oblique,
}
