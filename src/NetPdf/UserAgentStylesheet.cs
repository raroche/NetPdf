// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading;
using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using NetPdf.Css.Parser;
using NetPdf.Css.Parser.Preprocessing;

namespace NetPdf;

/// <summary>
/// The engine's built-in user-agent stylesheet (RC-UA / WP-8→WP-2). Before this, the only
/// element defaults NetPdf applied were DISPLAY types (<c>HtmlDefaultDisplay</c>); everything the
/// HTML rendering model's UA sheet provides — heading sizes/weights/margins, <c>b</c>/<c>strong</c>/
/// <c>th</c> bold, <c>em</c>/<c>i</c> italic, list indentation, <c>th</c> centering, etc. — was
/// missing unless the author set it, so bare <c>&lt;h1&gt;</c>/<c>&lt;strong&gt;</c> rendered at
/// body size / regular weight.
/// <para>
/// <b>Clean-room provenance.</b> The rules below are transcribed from the public specifications —
/// CSS 2.2 Appendix D "Default style sheet for HTML 4" (https://www.w3.org/TR/CSS22/sample.html)
/// and the WHATWG HTML Standard §"Rendering" — NOT from any browser's shipped UA sheet. DISPLAY
/// declarations are intentionally omitted: box display is owned by
/// <c>NetPdf.Layout.Boxes.HtmlDefaultDisplay</c>, and duplicating it here would create two sources
/// of truth. Only typographic + box-model defaults live here.
/// </para>
/// <para>
/// Registered at <see cref="CssStylesheetOrigin.UserAgent"/> (the lowest cascade origin, id 0), so
/// any author rule — even a low-specificity type selector — overrides it. Parsed + adapted ONCE and
/// cached (the AST is immutable), then prepended to every conversion's stylesheet list.
/// </para>
/// </summary>
internal static class UserAgentStylesheet
{
    // Transcribed from CSS 2.2 Appendix D + WHATWG HTML "Rendering". Physical margins/padding
    // (LTR — the engine's primary writing mode). No `display` (see class doc). Kept small + literal.
    private const string Css = """
        h1 { font-size: 2em; font-weight: bold; margin-top: 0.67em; margin-bottom: 0.67em; }
        h2 { font-size: 1.5em; font-weight: bold; margin-top: 0.83em; margin-bottom: 0.83em; }
        h3 { font-size: 1.17em; font-weight: bold; margin-top: 1em; margin-bottom: 1em; }
        h4 { font-size: 1em; font-weight: bold; margin-top: 1.33em; margin-bottom: 1.33em; }
        h5 { font-size: 0.83em; font-weight: bold; margin-top: 1.67em; margin-bottom: 1.67em; }
        h6 { font-size: 0.67em; font-weight: bold; margin-top: 2.33em; margin-bottom: 2.33em; }
        b, strong { font-weight: bold; }
        i, em, cite, var, dfn, address { font-style: italic; }
        p { margin-top: 1em; margin-bottom: 1em; }
        blockquote { margin-top: 1em; margin-bottom: 1em; margin-left: 40px; margin-right: 40px; }
        figure { margin-top: 1em; margin-bottom: 1em; margin-left: 40px; margin-right: 40px; }
        ul, ol { margin-top: 1em; margin-bottom: 1em; padding-left: 40px; }
        dl { margin-top: 1em; margin-bottom: 1em; }
        dd { margin-left: 40px; }
        pre { font-family: monospace; white-space: pre; margin-top: 1em; margin-bottom: 1em; }
        code, kbd, samp, tt { font-family: monospace; }
        small { font-size: smaller; }
        sub { font-size: smaller; vertical-align: sub; }
        sup { font-size: smaller; vertical-align: super; }
        a { text-decoration: underline; }
        caption { text-align: center; }
        th { font-weight: bold; text-align: center; }
        td, th { padding: 1px; }
        table, thead, tbody, tfoot, tr { vertical-align: middle; }
        hr { border-width: 1px; border-style: solid; margin-top: 0.5em; margin-bottom: 0.5em; }
        """;

    private static readonly CssStylesheet Sheet = Build();

    /// <summary>The cached, adapted UA stylesheet (immutable AST). Prepend to the per-conversion
    /// sheet list; the cascade sorts it below every author sheet by origin.</summary>
    public static CssStylesheet Instance => Sheet;

    private static CssStylesheet Build()
    {
        var context = BrowsingContext.New(Configuration.Default.WithCss());
        var parser = context.GetService<ICssParser>()!;
        ICssStyleSheet raw = parser.ParseStyleSheet(Css);
        // Run the same preprocessor the author path uses so any declaration AngleSharp would
        // otherwise drop is recovered identically (harmless here — the sheet is plain).
        var preprocess = CssPreprocessor.Process(Css);
        return CssParserAdapter.Adapt(
            raw, preprocess,
            href: null,
            origin: CssStylesheetOrigin.UserAgent,
            ownerKind: CssStylesheetOwnerKind.Unknown,
            mediaQuery: null,
            isDisabled: false,
            order: 0);
    }
}
