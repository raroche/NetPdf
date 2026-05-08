// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Parser;

namespace NetPdf.Css.Resources;

/// <summary>
/// Per Phase D D-3 — one resource URL referenced from CSS, with the
/// metadata Phase 5's resource loader needs to apply per-kind safety
/// policy. Every CSS resource sink — <c>@import url(...)</c>,
/// <c>@font-face src: url(...)</c>, <c>background-image: url(...)</c>,
/// <c>cursor: url(...)</c>, <c>list-style-image: url(...)</c>,
/// <c>content: url(...)</c>, <c>mask-image: url(...)</c>,
/// <c>border-image-source: url(...)</c>, <c>shape-outside: url(...)</c> —
/// extracts to one of these.
///
/// <para>Phase 5's wireup will route every <see cref="CssResourceReference"/>
/// through <c>SafeResourceLoader.FetchAsync</c> with the matching
/// <c>ResourceKind</c>. The extractor lives in
/// <c>NetPdf.Css.Resources</c> (this assembly) so the CSS parser can
/// produce the references during cascade resolution + downstream stages
/// can consume them without re-walking the AST.</para>
/// </summary>
/// <param name="Url">The raw URL text from the <c>url(...)</c> token —
/// may be relative, absolute, or a <c>data:</c> URI. Phase 5's loader
/// resolves relative URLs against the conversion's
/// <c>HtmlPdfOptions.BaseUri</c> + applies <c>UriSafetyValidator</c> +
/// <c>SafeResourceLoader.FetchAsync</c>.</param>
/// <param name="Kind">What the resource is used as. Drives the
/// MIME allowlist + per-kind size cap when Phase 5 lands the loader.</param>
/// <param name="SourceLocation">Where in the CSS the reference appeared.
/// Threads through diagnostics so a security-rejected URL can be
/// reported with line/column.</param>
internal sealed record CssResourceReference(
    string Url,
    CssResourceKind Kind,
    CssSourceLocation SourceLocation);

/// <summary>What the CSS-side url() reference resolves to. The
/// <c>NetPdf.ResourceKind</c> public enum doesn't cover every CSS use
/// (e.g., <c>cursor: url(...)</c> is image-shaped but with subtly
/// different policy needs); this internal enum stays close to CSS-side
/// semantics + maps to <c>ResourceKind</c> at the loader boundary.</summary>
internal enum CssResourceKind
{
    /// <summary><c>@import url(...)</c> — another stylesheet. Maps to
    /// <c>ResourceKind.Stylesheet</c>.</summary>
    Stylesheet = 0,
    /// <summary><c>@font-face src: url(...)</c> — a font face. Maps to
    /// <c>ResourceKind.Font</c>.</summary>
    Font = 1,
    /// <summary><c>background-image</c> / <c>list-style-image</c> /
    /// <c>border-image-source</c> / <c>mask-image</c> / etc. Maps to
    /// <c>ResourceKind.Image</c>.</summary>
    Image = 2,
    /// <summary><c>cursor: url(...)</c> — image-shaped but typically
    /// small (cursor sprites). Maps to <c>ResourceKind.Image</c> with a
    /// tighter per-resource size cap when Phase 5 cares.</summary>
    Cursor = 3,
    /// <summary><c>content: url(...)</c> on <c>::before</c> / <c>::after</c>.
    /// Maps to <c>ResourceKind.Image</c>.</summary>
    Content = 4,
}
