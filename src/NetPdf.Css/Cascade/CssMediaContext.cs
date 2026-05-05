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
/// <b>Media query evaluation.</b> <see cref="Matches"/> delegates to
/// <see cref="MediaQueryEvaluator"/> which implements the subset of CSS Media Queries L4
/// real-world documents use: type matching, <c>not</c> / <c>only</c> prefixes,
/// comma-separated alternative lists, <c>and</c>-combined feature queries, and the
/// common features evaluated against this context — <c>min-width</c> / <c>max-width</c> /
/// <c>min-height</c> / <c>max-height</c> against <see cref="ViewportWidthPx"/> /
/// <see cref="ViewportHeightPx"/>, <c>orientation</c> (landscape if w &gt; h),
/// <c>prefers-color-scheme</c> against <see cref="PreferredColorScheme"/>,
/// <c>min-resolution</c> / <c>max-resolution</c> in <c>dppx</c> / <c>x</c> / <c>dpi</c> /
/// <c>dpcm</c> against <see cref="DevicePixelRatio"/>, plus length parsing in <c>px</c> /
/// <c>em</c> / <c>rem</c> / <c>cm</c> / <c>mm</c> / <c>in</c> / <c>pt</c> / <c>pc</c> /
/// <c>vw</c> / <c>vh</c> / <c>vmin</c> / <c>vmax</c>. Unknown features evaluate to false
/// (conservative — rules guarded by features we can't validate don't silently apply).
/// Full Media Queries L4 (range syntax <c>(width &gt;= 800px)</c>, advanced media features)
/// is a Phase 3 follow-up.
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
