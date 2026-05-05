// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;

namespace NetPdf.Css.Cascade;

/// <summary>
/// The host environment values the cascade evaluates media queries and viewport-relative
/// units against. Built by <c>HtmlPdf.ConvertAsync</c> from <c>HtmlPdfOptions</c> at the
/// start of a render and threaded through to <see cref="CascadeResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Media type.</b> NetPdf renders for a <c>print</c> media context by default — that's
/// the entire premise of an HTML-to-PDF tool. Authors who want screen styles can pass
/// <c>HtmlPdfOptions.MediaType = "screen"</c>. Stylesheets attached with a
/// <c>media="screen"</c> attribute (or <c>@media screen { … }</c>) are skipped when this
/// is "print", and vice versa. <c>all</c> matches both.
/// </para>
/// <para>
/// <b>Media query evaluation in v1.</b> The cascade does keyword-only matching on the
/// stylesheet's <c>media</c> attribute (HTML 4 grammar — comma-separated media types).
/// Full Media Queries L4 (range syntax, feature queries like <c>(min-width: …)</c>) is
/// post-v1 work. The viewport size hints below are wired here so the eventual evaluator
/// has them ready.
/// </para>
/// </remarks>
internal sealed record CssMediaContext(
    string MediaType,
    double ViewportWidthPx,
    double ViewportHeightPx,
    double DevicePixelRatio,
    string PreferredColorScheme)
{
    /// <summary>Default print context. Viewport derives from US Letter at 96 DPI per the
    /// HTML2PDF default; consumers can override via <c>HtmlPdfOptions</c>.</summary>
    public static CssMediaContext DefaultPrint => new(
        MediaType: "print",
        ViewportWidthPx: 816,    // 8.5in × 96dpi
        ViewportHeightPx: 1056,  // 11in × 96dpi
        DevicePixelRatio: 1.0,
        PreferredColorScheme: "light");

    /// <summary>Returns <see langword="true"/> when a stylesheet with the given
    /// <paramref name="mediaQuery"/> attribute should contribute to the cascade. Empty /
    /// <see langword="null"/> queries always match (no restriction). Delegates to
    /// <see cref="MediaQueryEvaluator.Evaluate"/> which handles the comma-separated query
    /// list, <c>not</c> / <c>only</c> prefixes, type matching, <c>and</c>-combined feature
    /// queries on common features (<c>min-width</c>, <c>max-width</c>, <c>orientation</c>,
    /// <c>prefers-color-scheme</c>, <c>resolution</c>, …), and conservative false for
    /// unrecognized features.</summary>
    public bool Matches(string? mediaQuery) => MediaQueryEvaluator.Evaluate(mediaQuery, this);
}
