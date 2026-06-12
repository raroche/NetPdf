// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Parser;
using NetPdf.Layout.Inline;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Task 14 cycle 1 — parses a CSS <c>content</c> property value (per CSS
/// Generated Content L3 §1.3) into a single concatenated text string. Extends
/// <see cref="CssStringParser"/>'s single-string acceptance to handle the
/// shapes that compose without external state:
/// <list type="bullet">
///   <item>One or more <c>&lt;string&gt;</c> tokens, separated by whitespace —
///     concatenated in source order. Example: <c>"prefix " "suffix"</c>.</item>
///   <item><c>attr(<i>name</i>)</c> — substituted with the host element's
///     attribute value (case-insensitive name match per HTML5; missing
///     attribute → empty string). Per Task 14 review Rec 4, the modern
///     multi-arg form <c>attr(<i>name</i> <i>type</i>?, <i>fallback</i>?)</c>
///     (CSS Values L4 §10) is REJECTED — the parse fails cleanly rather
///     than silently dropping the type / fallback args and treating as
///     bare <c>attr(name)</c>. Cycle 2 will deliver the typed-value
///     pipeline + fallback handling.</item>
///   <item>Mixtures of strings + <c>attr()</c>: <c>"Item " attr(data-name)</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Returns <see langword="false"/> when the value contains tokens this parser
/// doesn't yet handle:
/// <c>counter(<i>name</i>)</c> for a non-page name + <c>counters()</c> (need the
/// counter-reset / counter-increment machinery) — though <c>counter(page)</c> /
/// <c>counter(pages)</c> ARE resolved when a <see cref="PageCounters"/> context is
/// supplied (Task 21 cycle 9 — page-margin-box content), <c>url()</c> / <c>image()</c> / <c>image-set()</c>
/// (needs the resource pipeline), <c>open-quote</c> / <c>close-quote</c> /
/// <c>no-open-quote</c> / <c>no-close-quote</c> (needs the quotation stack with
/// depth tracking + the <c>quotes</c> property).
/// </para>
/// <para>
/// <b>Task 16 cycle 1 — diagnostic emission.</b> The
/// <see cref="TryParse(string, IElement, ICssDiagnosticsSink?, CssSourceLocation, out string, PageCounters?, MarginContentContext?, bool, Func{string, string}?)"/>
/// overload emits one of two diagnostic codes when the parse fails:
/// <see cref="CssDiagnosticCodes.CssContentFunctionUnsupported001"/> for
/// unsupported functions (<c>counter()</c> / <c>url()</c> / etc.) or unsupported
/// keywords (<c>open-quote</c> / <c>close-quote</c>); and
/// <see cref="CssDiagnosticCodes.CssAttrMultiArgUnsupported001"/> when the
/// modern multi-arg <c>attr()</c> form is detected. The sink-less overload
/// remains for callers that don't have a sink in scope.
/// </para>
/// </remarks>
internal static class CssContentList
{
    /// <summary>The page counters available to <c>counter(page)</c> / <c>counter(pages)</c> when
    /// resolving a CSS Paged Media §6.4 margin box's <c>content</c> (Task 21 cycle 9). Supplied only on
    /// the page-margin-box path — body / pseudo-element content has no page context, so
    /// <c>counter(page)</c> stays unsupported there.</summary>
    public readonly record struct PageCounters
    {
        /// <summary>The 1-based number of the page being painted.</summary>
        public int Page { get; }

        /// <summary>The total page count — the document total per CSS Page 3 §6.1.</summary>
        public int Pages { get; }

        /// <summary>Construct with the current page number + the total. Both are 1-based and
        /// <c>Page ≤ Pages</c> — a contract guard before the multi-page driver starts passing dynamic
        /// values (the single-page caller always passes <c>(1, 1)</c>).</summary>
        public PageCounters(int page, int pages)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(pages, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(page, pages);
            Page = page;
            Pages = pages;
        }
    }

    /// <summary>Generated-content context for page-margin boxes: the named strings set by
    /// <c>string-set: name …</c> (resolved for <c>content: string(name)</c> — Task 22) and the running
    /// elements' text from <c>position: running(name)</c> (resolved for <c>content: element(name)</c> —
    /// Task 23), collected from the document during the render. A SINGLE-PAGE first cut — cross-page
    /// "running" persistence (the value carried to the next page until re-set) needs the multi-page
    /// driver (deferred). Both lookups resolve a missing name to the empty string (CSS GCPM L3).
    /// <para>Per CSS GCPM L3 §7.3/§7.4, both <c>string()</c> and <c>element()</c> default to the FIRST
    /// occurrence on the page. <see cref="NamedStringsFirst"/> / <see cref="RunningElementsFirst"/> hold the
    /// FIRST assignment / running element (<c>string(name)</c> default + <c>string(name, first)</c>;
    /// <c>element(name)</c> default + <c>element(name, first)</c>); <see cref="NamedStrings"/> /
    /// <see cref="RunningElements"/> hold the LAST (the exit value — <c>string(name, last)</c> /
    /// <c>element(name, last)</c>). The <c>start</c> / <c>first-except</c> keywords need cross-page context
    /// and stay deferred.</para></summary>
    public readonly record struct MarginContentContext(
        IReadOnlyDictionary<string, string>? NamedStrings,
        IReadOnlyDictionary<string, string>? RunningElements,
        IReadOnlyDictionary<string, string>? NamedStringsFirst = null,
        IReadOnlyDictionary<string, string>? RunningElementsFirst = null,
        // element()'s first-cut OWN-STYLE rendering (Task 23): the running element's winning font/color
        // (property, value) pairs, by name — first occurrence + last, mirroring the text dictionaries.
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>>? RunningElementStyles = null,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, string>>>? RunningElementStylesFirst = null,
        // element()'s nested-block SEGMENTS (Task 23, segment-style cycle): one record per stacked
        // line of the running element's content, each with the OWN style of the leaf block that
        // produced it (ancestor-walked, so a record is self-contained) — first + last occurrence,
        // lockstep with the text/style dictionaries above. A null/absent name means the joined-text
        // single-style path (the pre-cycle behavior).
        IReadOnlyDictionary<string, IReadOnlyList<RunningSegment>>? RunningElementSegments = null,
        IReadOnlyDictionary<string, IReadOnlyList<RunningSegment>>? RunningElementSegmentsFirst = null,
        // element()'s nested CONTAINER bands (container-bands cycle): one record per DECORATED
        // intermediate block (a div whose children are the leaf lines) spanning its descendants'
        // segment range — PRE-order (outer before inner), so paint order nests correctly. First +
        // last occurrence, lockstep like the segments.
        IReadOnlyDictionary<string, IReadOnlyList<RunningContainer>>? RunningElementContainers = null,
        IReadOnlyDictionary<string, IReadOnlyList<RunningContainer>>? RunningElementContainersFirst = null);

    /// <summary>One nested CONTAINER of a running element's content (container-bands cycle): an
    /// intermediate block-level element (between the running root and the leaf lines) carrying its
    /// OWN self-only decoration — its band spans its descendant leaf lines
    /// [<paramref name="FirstSegment"/>..<paramref name="LastSegment"/>]. Its own horizontal
    /// margins inset ITS band (children's line geometry is untouched — the documented first cut);
    /// its VERTICAL margins were folded into the boundary segments' gap margins at capture
    /// (max-collapse, CSS 2.2 §8.3.1's simple case). <paramref name="OwnStyle"/> carries the
    /// container's inherited <c>color</c> (its band's currentcolor owner).</summary>
    public readonly record struct RunningContainer(
        IReadOnlyList<KeyValuePair<string, string>> Decoration,
        IReadOnlyList<KeyValuePair<string, string>> OwnStyle,
        int FirstSegment, int LastSegment,
        double MarginLeftPx = 0, double MarginRightPx = 0);

    /// <summary>One stacked LINE of a running element's content (Task 23, segment-style cycle):
    /// the line's text plus the OWN font/color (property, value) pairs of the element that produced
    /// it — a leaf block child, a flattened deep nest, or an inline run (its parent element's
    /// style). The page-margin painter shapes each segment as its own <c>TextRun</c>, so a
    /// heterogeneous running header (an <c>h1</c> title line over a styled subtitle line) renders
    /// each line in its own font + colour.</summary>
    public readonly record struct RunningSegment(
        string Text, IReadOnlyList<KeyValuePair<string, string>> OwnStyle,
        // The leaf block's OWN (self-only) decoration — background-color / border-* longhands —
        // painted as a PER-LINE band behind the segment's line (segment-decor cycle). Empty for
        // an undecorated leaf. Per-line padding stays deferred (deferrals.md).
        IReadOnlyList<KeyValuePair<string, string>>? Decoration = null,
        // The leaf block's OWN vertical margins in used px (segment-margins cycle) — inter-line
        // GAPS between segment lines (adjacent gaps collapse via max, CSS 2.2 §8.3.1's simple
        // case). Absolute lengths only; %/relative/auto read 0.
        double MarginTopPx = 0, double MarginBottomPx = 0,
        // The leaf block's OWN vertical padding in used px (segment-padding cycle) — grows the
        // line's band/pitch (the background covers the padding box per CSS B&B §4.2); the glyphs
        // centre within the padded pitch (exact for symmetric padding — a documented
        // approximation for asymmetric).
        double PaddingTopPx = 0, double PaddingBottomPx = 0,
        // The leaf block's OWN horizontal padding in used px (hpadding cycle) — insets ITS line's
        // glyphs + alignment extent within the content box (the band keeps the full width: a
        // block's background spans its border box). The wrap width is NOT narrowed per segment
        // (one shared inline pass — a padded long line clips via the existing clip-path safety
        // net; documented approximation).
        double PaddingLeftPx = 0, double PaddingRightPx = 0,
        // The leaf block's OWN horizontal margins in used px (segment-hmargins cycle) — margins
        // sit OUTSIDE the border box, so they inset the line's per-line DECORATION band AND the
        // glyphs/alignment extent (padding insets only the glyphs — the band covers the padding
        // box). Absolute lengths only, clamped ≥ 0 at capture (a negative horizontal margin
        // would pull the line outside its box — deferred, like the vertical gaps' overlap
        // clamp); `auto` reads 0 (no per-line centering distribution — deferrals.md).
        double MarginLeftPx = 0, double MarginRightPx = 0);

    /// <summary>Sink-less convenience overload — see the four-argument form
    /// for the diagnostic-emitting path. Returns <see langword="true"/> + the
    /// concatenated text on success; <see langword="false"/> when the value
    /// uses unsupported tokens (no diagnostic emitted).</summary>
    public static bool TryParse(string raw, IElement host, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result);

    /// <summary>Sink-less overload that also resolves <c>counter(page)</c> / <c>counter(pages)</c>
    /// against <paramref name="pageCounters"/> (Task 21 cycle 9 — page-margin-box content).</summary>
    public static bool TryParse(string raw, IElement host, PageCounters pageCounters, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result, pageCounters);

    /// <summary>Sink-less page-margin-box overload that also resolves <c>string(name)</c> /
    /// <c>element(name)</c> against <paramref name="marginContext"/> (Task 22 / 23).</summary>
    public static bool TryParse(
        string raw, IElement host, PageCounters pageCounters, MarginContentContext marginContext, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result, pageCounters, marginContext);

    /// <summary><c>string-set</c> overload (Task 22 — the <c>content()</c> form) — resolves a
    /// <c>string-set</c> value's content-list, ALSO accepting the GCPM <c>content()</c> function (the
    /// <paramref name="host"/> element's own text content). <c>content()</c> is valid ONLY inside a
    /// <c>string-set</c> value, so it's gated to this entry point — the margin-box / pseudo-element
    /// <c>content</c> paths never enable it. Used by <see cref="MarginContentCollector"/>.</summary>
    public static bool TryParseStringSet(string raw, IElement host, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result,
            pageCounters: null, marginContext: null, allowContentFunction: true);

    /// <summary><c>string-set</c> overload with a PSEUDO-content resolver (content-pseudo cycle):
    /// <paramref name="pseudoContentRaw"/> maps a pseudo name (<c>"before"</c> / <c>"after"</c>) to
    /// that pseudo-element's raw <c>content</c> declaration on the host (or <see langword="null"/>
    /// when the pseudo doesn't exist / declares no content — <c>content(before)</c> then resolves
    /// to the empty string per GCPM). The raw is parsed as a plain content-list (literals +
    /// <c>attr()</c>; a pseudo's content can't itself use <c>content()</c>).</summary>
    public static bool TryParseStringSet(
        string raw, IElement host, Func<string, string?>? pseudoContentRaw, out string result)
        => TryParse(raw, host, sink: null, location: CssSourceLocation.Unknown, out result,
            pageCounters: null, marginContext: null, allowContentFunction: true,
            pseudoContentRaw: pseudoContentRaw);

    // Per Phase A security hardening A-5 — cap on the concatenated output of
    // one ::before / ::after / ::marker content-list parse. A pseudo-element's
    // content is rendered into the output PDF; without a cap, an attacker can
    // craft a content value with a multi-megabyte attribute (e.g., a span
    // with data-x set to 10 MiB + ::before { content: attr(data-x) ... }) that
    // bloats the box tree + downstream PDF without bound. 64 KiB per pseudo is
    // generous for any sane label / numeration string while keeping
    // adversarial input bounded.
    private const int MaxContentOutputChars = 64 * 1024;

    /// <summary>Parse <paramref name="raw"/> as a CSS content-list value
    /// against <paramref name="host"/> for <c>attr()</c> resolution; emit a
    /// diagnostic to <paramref name="sink"/> when the parse fails on an
    /// unsupported token. Returns <see langword="true"/> + the concatenated
    /// text on success; <see langword="false"/> on rejection.</summary>
    public static bool TryParse(
        string raw,
        IElement host,
        ICssDiagnosticsSink? sink,
        CssSourceLocation location,
        out string result,
        PageCounters? pageCounters = null,
        MarginContentContext? marginContext = null,
        bool allowContentFunction = false,
        Func<string, string?>? pseudoContentRaw = null)
    {
        result = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var sb = new StringBuilder(raw.Length);
        var i = 0;
        var span = raw.AsSpan();

        while (i < span.Length)
        {
            i = SkipWhitespace(span, i);
            if (i >= span.Length) break;

            var c = span[i];

            if (c == '"' || c == '\'')
            {
                if (!ReadString(span, ref i, out var stringPart)) return false;
                if (!CssStringParser.TryParseSingleString(stringPart, out var decoded)) return false;
                if (!TryAppendBounded(sb, decoded, sink, raw, location)) return false;
                continue;
            }

            // attr() — case-insensitive function name per CSS Syntax §4.
            if (StartsWithCaseInsensitive(span, i, "attr("))
            {
                i += "attr(".Length;
                var attrAccepted = ReadAttrArgs(span, ref i, host, out var attrValue,
                    out var rejectedReason);
                if (!attrAccepted)
                {
                    EmitAttrRejection(sink, raw, location, rejectedReason);
                    return false;
                }
                if (!TryAppendBounded(sb, attrValue, sink, raw, location)) return false;
                continue;
            }

            // counter(page) / counter(pages) — only in a page context (Task 21 cycle 9). Other
            // counter names / styles, or no page context, fall through to the unsupported path.
            if (StartsWithCaseInsensitive(span, i, "counter("))
            {
                var counterTokenStart = i;
                i += "counter(".Length;
                if (pageCounters is { } pc && TryReadPageCounter(span, ref i, pc, out var counterText))
                {
                    if (!TryAppendBounded(sb, counterText, sink, raw, location)) return false;
                    continue;
                }
                EmitContentFunctionUnsupported(sink, span, counterTokenStart, raw, location);
                return false;
            }

            // string(name [, first|last]) — Task 22 (+ the position keyword). Pulls the named string set
            // by `string-set` (collected during the document walk). Only valid with a margin context
            // (page-margin boxes); a missing/undefined name resolves to the empty string per CSS GCPM L3
            // (no diagnostic). `first` (AND the no-keyword DEFAULT, per GCPM §7.3) → the FIRST assignment on
            // the page; `last` → the exit value. The cross-page `start` / `first-except` keywords bail to
            // unsupported (need the multi-page driver).
            if (StartsWithCaseInsensitive(span, i, "string("))
            {
                var tokenStart = i;
                i += "string(".Length;
                if (marginContext is { } mcStr && TryReadPositionedFunction(span, ref i, out var stringName, out var first))
                {
                    var dict = first ? mcStr.NamedStringsFirst : mcStr.NamedStrings;
                    var value = dict is { } ns && ns.TryGetValue(stringName, out var v) ? v : string.Empty;
                    if (!TryAppendBounded(sb, value, sink, raw, location)) return false;
                    continue;
                }
                EmitContentFunctionUnsupported(sink, span, tokenStart, raw, location);
                return false;
            }

            // element(name [, first|last]) — Task 23 (+ the position keyword). Pulls the text of the running
            // element (`position: running(name)`, collected during the document walk, GCPM-normalized as if
            // white-space: normal). Only valid with a margin context; a missing name → the empty string.
            // `first` (AND the no-keyword DEFAULT, per GCPM §7.4) → the FIRST such element on the page;
            // `last` → the last; `start` / `first-except` bail. First cut: the running element's TEXT (its
            // own block styling/box is a documented deferral — deferrals.md). APPROXIMATION: CSS GCPM L3
            // restricts `element()` to a standalone margin-box `content` value (not combinable with other
            // tokens); this parser treats it as a concatenable content-list token (so `"Prefix " element(rh)`
            // works) — a lenient superset, intentional + documented (deferrals.md).
            if (StartsWithCaseInsensitive(span, i, "element("))
            {
                var tokenStart = i;
                i += "element(".Length;
                if (marginContext is { } mcEl && TryReadPositionedFunction(span, ref i, out var elementName, out var elFirst))
                {
                    var dict = elFirst ? mcEl.RunningElementsFirst : mcEl.RunningElements;
                    var value = dict is { } re && re.TryGetValue(elementName, out var v) ? v : string.Empty;
                    if (!TryAppendBounded(sb, value, sink, raw, location)) return false;
                    continue;
                }
                EmitContentFunctionUnsupported(sink, span, tokenStart, raw, location);
                return false;
            }

            // content() — GCPM string-set ONLY (allowContentFunction): the host element's own text
            // content, so `h1 { string-set: title content() }` captures each h1's text. Bare content()
            // or content(text) → the text; the typographic targets content(before|after|first-letter|
            // marker) are deferred → bail to unsupported. Gated so margin-box / pseudo content can't use it.
            if (allowContentFunction && StartsWithCaseInsensitive(span, i, "content("))
            {
                var contentTokenStart = i;
                i += "content(".Length;
                // content(before|after) (content-pseudo cycle, GCPM §2.4): the host's ::before /
                // ::after pseudo content — resolved through pseudoContentRaw to the pseudo's raw
                // `content` declaration, parsed as a plain content-list (literals + attr(); a
                // pseudo's content can't nest content()). A missing pseudo / no declaration /
                // none/normal / a null resolver yields the EMPTY string (the assignment still
                // succeeds — GCPM treats an absent pseudo as empty). An unparsable pseudo
                // content-list also resolves empty (its own unsupported tokens are the PSEUDO's
                // problem, surfaced when the pseudo renders). first-letter / marker stay
                // unsupported (the existing bail path).
                if (TryReadPseudoTarget(span, ref i, out var pseudoName))
                {
                    var pseudoRaw = pseudoContentRaw?.Invoke(pseudoName);
                    if (!string.IsNullOrWhiteSpace(pseudoRaw)
                        && !pseudoRaw.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)
                        && !pseudoRaw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase)
                        && TryParse(pseudoRaw, host, sink, location, out var pseudoText))
                    {
                        if (!TryAppendBounded(sb, pseudoText, sink, raw, location)) return false;
                    }
                    continue;
                }
                if (TryReadContentText(span, ref i, host, out var contentText))
                {
                    if (!TryAppendBounded(sb, contentText, sink, raw, location)) return false;
                    continue;
                }
                EmitContentFunctionUnsupported(sink, span, contentTokenStart, raw, location);
                return false;
            }

            // Anything else: counters()/url()/image()/image-set()/
            // linear-gradient()/open-quote/close-quote/identifier — bail.
            EmitContentFunctionUnsupported(sink, span, i, raw, location);
            return false;
        }

        result = sb.ToString();
        return true;
    }

    /// <summary>Per Phase A A-5 — append <paramref name="text"/> to
    /// <paramref name="sb"/> only if doing so stays under the per-pseudo
    /// output cap. Returns false on overflow + emits one diagnostic so the
    /// author sees why the pseudo-element generated no box. The cap is hit
    /// LAZILY (we don't refuse partial output beforehand) so a benign label
    /// near the boundary still works.</summary>
    private static bool TryAppendBounded(
        StringBuilder sb, string text, ICssDiagnosticsSink? sink, string raw, CssSourceLocation location)
    {
        if (sb.Length + text.Length > MaxContentOutputChars)
        {
            sink?.Emit(new CssDiagnostic(
                CssDiagnosticCodes.CssContentFunctionUnsupported001,
                $"Generated content for this pseudo-element exceeded the {MaxContentOutputChars / 1024} KiB output cap. The pseudo generates no box. Raw value: '{Truncate(raw)}'.",
                CssDiagnosticSeverity.Warning,
                location));
            return false;
        }
        sb.Append(text);
        return true;
    }

    private static void EmitAttrRejection(
        ICssDiagnosticsSink? sink, string raw, CssSourceLocation location, AttrRejectReason reason)
    {
        if (sink is null) return;
        switch (reason)
        {
            case AttrRejectReason.MultiArg:
                sink.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssAttrMultiArgUnsupported001,
                    $"Modern attr() multi-arg form rejected — cycle 1 supports the bare attr(name) form only. Value: '{Truncate(raw)}'.",
                    CssDiagnosticSeverity.Warning,
                    location));
                return;
            case AttrRejectReason.MalformedSyntax:
            default:
                // Fall through to the generic content-function diagnostic for
                // truly malformed attr() (no name, unterminated, etc.).
                sink.Emit(new CssDiagnostic(
                    CssDiagnosticCodes.CssContentFunctionUnsupported001,
                    $"Malformed attr() in content value: '{Truncate(raw)}'.",
                    CssDiagnosticSeverity.Warning,
                    location));
                return;
        }
    }

    private static void EmitContentFunctionUnsupported(
        ICssDiagnosticsSink? sink, ReadOnlySpan<char> span, int i, string raw, CssSourceLocation location)
    {
        if (sink is null) return;
        // Try to identify the unsupported token kind so the message names what
        // the author wrote — `counter(items)` is more useful than `unrecognized
        // content token`.
        var kind = IdentifyUnsupportedToken(span, i);
        // Per Phase A A-6 — the token name is sliced from raw author CSS, so it
        // can carry C0/C1 control chars or extreme length. Sanitize before
        // interpolation; cap at 40 chars (the function-name + paren shape stays
        // well under that for any sane input).
        var safeKind = NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(kind, maxLength: 40);
        sink.Emit(new CssDiagnostic(
            CssDiagnosticCodes.CssContentFunctionUnsupported001,
            $"Unsupported content token '{safeKind}' in '{Truncate(raw)}'. Cycle 1 supports string + attr(name) only.",
            CssDiagnosticSeverity.Warning,
            location));
    }

    /// <summary>Best-effort identification of the unsupported token at
    /// <paramref name="i"/> so the diagnostic message can name it. Returns the
    /// function name (with trailing <c>(</c>) when followed by a paren, or the
    /// identifier text otherwise. Falls back to a single-char snippet when the
    /// token shape isn't recognizable.</summary>
    private static string IdentifyUnsupportedToken(ReadOnlySpan<char> span, int i)
    {
        var start = i;
        var end = i;
        while (end < span.Length)
        {
            var c = span[end];
            if (c == '(' || IsCssWhitespace(c) || c == ',' || c == ')') break;
            end++;
        }
        if (start == end) return "?";
        var name = span[start..end].ToString();
        // If followed by '(' it's a function call.
        if (end < span.Length && span[end] == '(') return name + "()";
        return name;
    }

    /// <summary>Per Phase A A-6 — uses the central diagnostic-text sanitizer
    /// so this helper applies BOTH length truncation + control-char stripping
    /// (ANSI / NUL injection defense) when the raw content-value is
    /// interpolated into a diagnostic message.</summary>
    private static string Truncate(string s) =>
        NetPdf.Css.Diagnostics.DiagnosticTextSanitizer.Sanitize(s, maxLength: 80);

    private enum AttrRejectReason
    {
        MalformedSyntax = 0,
        MultiArg = 1,
    }

    /// <summary>Advance <paramref name="i"/> past the leading-whitespace run.</summary>
    private static int SkipWhitespace(ReadOnlySpan<char> span, int i)
    {
        while (i < span.Length && IsCssWhitespace(span[i])) i++;
        return i;
    }

    /// <summary>Scan one quoted string token starting at <paramref name="i"/>
    /// (which must point at <c>'"'</c> or <c>'\''</c>). Advances
    /// <paramref name="i"/> past the closing quote and returns the substring
    /// inclusive of both quotes (which <see cref="CssStringParser"/> needs).
    /// CSS Syntax §4.3.4 escapes are honored — backslash-quote is not the
    /// terminator.</summary>
    private static bool ReadString(ReadOnlySpan<char> span, ref int i, out string token)
    {
        token = string.Empty;
        var start = i;
        var quote = span[i];
        i++;
        while (i < span.Length)
        {
            if (span[i] == '\\' && i + 1 < span.Length)
            {
                // Skip the backslash + next char (CssStringParser handles the
                // actual escape decoding when we hand it the substring).
                i += 2;
                continue;
            }
            if (span[i] == quote)
            {
                i++; // consume closing quote
                token = span[start..i].ToString();
                return true;
            }
            i++;
        }
        return false; // unterminated
    }

    /// <summary>Parse the argument list of an <c>attr(...)</c> call. Cycle 1
    /// supports ONLY the bare <c>attr(name)</c> form — per Task 14 review
    /// Rec 4, multi-arg forms (<c>attr(name type)</c>,
    /// <c>attr(name, fallback)</c>, <c>attr(name type, fallback)</c>) cause
    /// the entire content-list parse to fail rather than silently treating
    /// them as bare <c>attr(name)</c>. The full modern syntax (CSS Values L4
    /// §10) needs a typed-value pipeline + fallback handling that cycle 2
    /// will deliver. Resolves the attribute case-insensitively against
    /// <paramref name="host"/>; missing attribute → empty string per
    /// Generated Content L3 §1.3.</summary>
    /// <remarks>
    /// Per Task 16 review Rec 5 — the attribute name is validated as a CSS
    /// <i>ident-token</i> per CSS Syntax §4.3.11 (start: letter / <c>_</c> /
    /// non-ASCII / escape; continuation: start chars + digits + <c>-</c>;
    /// optional leading <c>-</c>). Accepting arbitrary punctuation (e.g.,
    /// <c>attr(.foo)</c>) and reporting a missing attribute would have masked
    /// real authoring errors. Cycle-1 simplification: ASCII-only validation
    /// — escape decoding + non-ASCII identifier ranges defer to cycle 2
    /// alongside the typed-value pipeline.
    /// </remarks>
    private static bool ReadAttrArgs(
        ReadOnlySpan<char> span, ref int i, IElement host,
        out string value, out AttrRejectReason reason)
    {
        value = string.Empty;
        reason = AttrRejectReason.MalformedSyntax;
        i = SkipWhitespace(span, i);

        // Per Rec 5: ident-token shape per CSS Syntax §4.3.11. Read ASCII
        // ident characters (start + continuation rules); reject if the run
        // is empty OR starts with a non-ident char.
        var nameStart = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        if (i == nameStart) return false; // empty name
        if (!IsValidCssIdentStart(span[nameStart..i])) return false;

        var attrName = span[nameStart..i].ToString();

        // Reject extra args per Rec 4 — anything between the name and the
        // close-paren that isn't pure whitespace is an unsupported form.
        i = SkipWhitespace(span, i);
        if (i >= span.Length) return false; // no close-paren at all
        if (span[i] != ')')
        {
            // Multi-arg form (type and/or fallback) — explicit Rec 4 rejection.
            reason = AttrRejectReason.MultiArg;
            return false;
        }
        i++; // consume the close-paren

        var attr = host.GetAttribute(attrName);
        value = attr ?? string.Empty;
        return true;
    }

    /// <summary>Parse a <c>counter(...)</c> call (the <c>counter(</c> already consumed) as a page
    /// counter (Task 21 cycle 9). Supports <c>counter(page)</c> / <c>counter(pages)</c> with an optional
    /// <c>&lt;counter-style&gt;</c> (default <c>decimal</c>) — the predefined numeric / alphabetic styles
    /// shared with list markers via <see cref="CounterStyleFormatter"/> (<c>decimal</c>,
    /// <c>decimal-leading-zero</c>, <c>lower-roman</c> / <c>upper-roman</c>, <c>lower-alpha</c> /
    /// <c>upper-alpha</c> / <c>-latin</c>, <c>lower-greek</c>). Resolves <c>page</c> → the current page
    /// number, <c>pages</c> → the total from <paramref name="pc"/>. An UNKNOWN / unimplemented style (e.g.
    /// <c>hebrew</c>, <c>cjk-ideographic</c>, an undefined name) FALLS BACK TO <c>decimal</c> per CSS
    /// Counter Styles L3 §7.1.4 — the page number must never silently vanish (post-PR-#149 review P2).
    /// Returns <see langword="false"/> (→ the caller bails to the unsupported diagnostic) only for a
    /// non-page counter name, an empty style, or malformed syntax. The <c>counters()</c> form is a tracked
    /// follow-up.</summary>
    private static bool TryReadPageCounter(ReadOnlySpan<char> span, ref int i, PageCounters pc, out string text)
    {
        text = string.Empty;
        i = SkipWhitespace(span, i);

        var nameStart = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        if (i == nameStart) return false; // empty name
        var name = span[nameStart..i];
        int value;
        if (name.Equals("page", StringComparison.OrdinalIgnoreCase)) value = pc.Page;
        else if (name.Equals("pages", StringComparison.OrdinalIgnoreCase)) value = pc.Pages;
        else return false; // a non-page counter — not supported here.

        // Optional `, <counter-style>` (default `decimal`); the style is resolved by CounterStyleFormatter.
        var style = "decimal".AsSpan();
        i = SkipWhitespace(span, i);
        if (i < span.Length && span[i] == ',')
        {
            i++;
            i = SkipWhitespace(span, i);
            var styleStart = i;
            while (i < span.Length)
            {
                var c = span[i];
                if (IsCssWhitespace(c) || c == ')') break;
                if (!IsCssIdentChar(c)) return false;
                i++;
            }
            if (styleStart == i) return false; // empty style after the comma
            style = span[styleStart..i];
            i = SkipWhitespace(span, i);
        }

        if (i >= span.Length || span[i] != ')') return false; // missing close-paren.
        i++; // consume ')'
        // CSS Counter Styles §7.1.4: an unknown / unimplemented counter style falls back to `decimal`
        // (the page number must never vanish — review P2). CounterStyleFormatter.TryFormat returns null
        // for a style it doesn't implement; we then render decimal. (List markers keep their own disc
        // fallback — the formatter stays null-returning so each caller chooses.)
        text = CounterStyleFormatter.TryFormat(value, style)
            ?? value.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Parse a <c>string(…)</c> / <c>element(…)</c> argument list (the opening <c>(</c> already
    /// consumed): a <c>&lt;custom-ident&gt;</c> + an optional GCPM position keyword. <paramref name="first"/>
    /// is set <see langword="true"/> for <c>first</c> AND for NO keyword — per CSS GCPM L3 §7.3/§7.4
    /// <c>first</c> is the DEFAULT (the first occurrence on the page) for both functions — and
    /// <see langword="false"/> for an explicit <c>last</c> (the exit value). Returns <see langword="false"/>
    /// (→ the caller bails to unsupported) for an empty/invalid name, the cross-page <c>start</c> /
    /// <c>first-except</c> keywords (deferred), an unknown keyword, or malformed syntax.</summary>
    private static bool TryReadPositionedFunction(ReadOnlySpan<char> span, ref int i, out string name, out bool first)
    {
        name = string.Empty;
        first = true; // GCPM default position keyword is `first`.
        i = SkipWhitespace(span, i);
        var start = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        if (i == start) return false; // empty name
        if (!IsValidCssIdentStart(span[start..i])) return false;
        name = span[start..i].ToString();

        i = SkipWhitespace(span, i);
        if (i < span.Length && span[i] == ',') // optional position keyword
        {
            i++;
            i = SkipWhitespace(span, i);
            var kwStart = i;
            while (i < span.Length)
            {
                var c = span[i];
                if (IsCssWhitespace(c) || c == ')') break;
                if (!IsCssIdentChar(c)) return false;
                i++;
            }
            var kw = span[kwStart..i];
            if (kw.Equals("first", StringComparison.OrdinalIgnoreCase)) first = true;
            else if (kw.Equals("last", StringComparison.OrdinalIgnoreCase)) first = false;
            else return false; // start / first-except (cross-page) or unknown → bail
            i = SkipWhitespace(span, i);
        }

        if (i >= span.Length || span[i] != ')') return false;
        i++; // consume ')'
        return true;
    }

    /// <summary>When <paramref name="raw"/> is a STANDALONE <c>element(&lt;name&gt; [, first | last])</c>
    /// content value (the GCPM form — no other tokens), returns its <paramref name="name"/> +
    /// <paramref name="first"/> (the first occurrence is selected: <c>first</c> / no keyword) so the
    /// page-margin painter can render the running element in its OWN style (Task 23 — first cut). Returns
    /// <see langword="false"/> for empty input, a position keyword that bails (<c>start</c> /
    /// <c>first-except</c>), or any MIXED / non-element() content (where the box's own style is used).</summary>
    public static bool TryGetStandaloneElement(string raw, out string name, out bool first)
    {
        name = string.Empty;
        first = true;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var span = raw.AsSpan().Trim();
        if (!StartsWithCaseInsensitive(span, 0, "element(")) return false;
        var i = "element(".Length;
        if (!TryReadPositionedFunction(span, ref i, out name, out first)) return false;
        // STANDALONE — only whitespace may follow the close-paren (a mixed list keeps the box's style).
        while (i < span.Length)
        {
            if (!IsCssWhitespace(span[i])) return false;
            i++;
        }
        return true;
    }

    /// <summary>Match <c>before)</c> / <c>after)</c> (the content-pseudo cycle targets) at
    /// <paramref name="i"/> inside a <c>content(</c> call — case-insensitive, whitespace-tolerant.
    /// Advances past the closing paren on success; leaves <paramref name="i"/> untouched otherwise
    /// (the bare/text forms parse next). <c>first-letter</c>/<c>marker</c> deliberately do NOT
    /// match (they stay on the unsupported-bail path).</summary>
    private static bool TryReadPseudoTarget(ReadOnlySpan<char> span, ref int i, out string pseudoName)
    {
        pseudoName = string.Empty;
        var probe = i;
        while (probe < span.Length && char.IsWhiteSpace(span[probe])) probe++;
        foreach (var candidate in (ReadOnlySpan<string>)["before", "after"])
        {
            if (probe + candidate.Length <= span.Length
                && span.Slice(probe, candidate.Length).Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                var end = probe + candidate.Length;
                while (end < span.Length && char.IsWhiteSpace(span[end])) end++;
                if (end < span.Length && span[end] == ')')
                {
                    pseudoName = candidate;
                    i = end + 1;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Parse a GCPM <c>content()</c> call in a <c>string-set</c> value (the <c>content(</c>
    /// already consumed): bare <c>content()</c> or <c>content(text)</c> → <paramref name="host"/>'s text
    /// content. <c>content(before|after)</c> never reaches here (TryReadPseudoTarget consumed it —
    /// content-pseudo cycle); the remaining typographic targets <c>content(first-letter|marker)</c> +
    /// any multi-arg form are deferred → <see langword="false"/> (the caller bails to the unsupported
    /// diagnostic).</summary>
    /// <remarks>Per CSS GCPM L3 §3.1, the element's string is taken "as if <c>white-space: normal</c> were
    /// in effect" — so the raw <see cref="INode.TextContent"/> (which keeps the source indentation /
    /// newlines / runs of spaces of formatted HTML) is collapsed with the SAME normalizer the body inline
    /// pass uses (<see cref="LineBuilder.PreprocessWhitespace"/> in <see cref="WhiteSpace.Normal"/> mode:
    /// collapse every SP/TAB/LF/CR/FF run to a single space + strip leading/trailing) so a heading like
    /// <c>&lt;h1&gt;\n  Chapter   &lt;span&gt;One&lt;/span&gt;\n&lt;/h1&gt;</c> yields <c>Chapter One</c>,
    /// not the indented source text, in the running header.</remarks>
    private static bool TryReadContentText(ReadOnlySpan<char> span, ref int i, IElement host, out string text)
    {
        text = string.Empty;
        i = SkipWhitespace(span, i);
        var start = i;
        while (i < span.Length)
        {
            var c = span[i];
            if (IsCssWhitespace(c) || c == ',' || c == ')') break;
            if (!IsCssIdentChar(c)) return false; // malformed punctuation
            i++;
        }
        var arg = span[start..i];
        i = SkipWhitespace(span, i);
        if (i >= span.Length || span[i] != ')') return false; // a second arg / malformed → bail (first cut)
        i++; // consume ')'
        // Only bare content() / content(text) is the element's text; the other GCPM targets are deferred.
        if (!arg.IsEmpty && !arg.Equals("text", StringComparison.OrdinalIgnoreCase)) return false;
        // GCPM: the element string is determined as if white-space: normal — collapse source whitespace.
        text = LineBuilder.PreprocessWhitespace(host.TextContent ?? string.Empty, WhiteSpace.Normal);
        return true;
    }

    /// <summary><see langword="true"/> when <paramref name="c"/> is a valid
    /// continuation char of a CSS ident-token (ASCII subset per cycle-1
    /// scope). Rec 5: letters / digits / hyphen / underscore.</summary>
    private static bool IsCssIdentChar(char c) =>
        (c >= 'a' && c <= 'z')
        || (c >= 'A' && c <= 'Z')
        || (c >= '0' && c <= '9')
        || c == '-' || c == '_';

    /// <summary><see langword="true"/> when <paramref name="ident"/> is a
    /// valid CSS ident-token start per §4.3.11. ASCII-only check: must start
    /// with letter / <c>_</c>, OR <c>-</c> followed by another valid start
    /// char (so <c>--custom</c> is OK). Pure-numeric / leading-digit are
    /// rejected per the spec.</summary>
    private static bool IsValidCssIdentStart(ReadOnlySpan<char> ident)
    {
        if (ident.IsEmpty) return false;
        var first = ident[0];
        if ((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || first == '_')
            return true;
        if (first == '-')
        {
            if (ident.Length < 2) return false;
            var second = ident[1];
            return (second >= 'a' && second <= 'z')
                || (second >= 'A' && second <= 'Z')
                || second == '_' || second == '-';
        }
        return false;
    }

    /// <summary><see langword="true"/> when <paramref name="ident"/> is a valid CSS
    /// <c>&lt;custom-ident&gt;</c> per CSS Syntax §4.3.11 (ASCII subset, the same rules
    /// <see cref="ReadAttrArgs"/> applies): a non-empty ident-token whose START is a letter / <c>_</c> /
    /// <c>-</c> (a leading <c>-</c> needs a letter/<c>_</c>/<c>-</c> after it — so leading digits are
    /// rejected) and whose remaining chars are letters / digits / <c>-</c> / <c>_</c>. Shared with
    /// <see cref="MarginContentCollector"/> to validate <c>string-set</c> + <c>running()</c> names so an
    /// invalid ident (e.g. <c>running(123)</c>) is not mistaken for a real name.</summary>
    internal static bool IsValidCustomIdent(ReadOnlySpan<char> ident)
    {
        if (!IsValidCssIdentStart(ident)) return false;
        foreach (var c in ident)
            if (!IsCssIdentChar(c)) return false;
        return true;
    }

    private static bool StartsWithCaseInsensitive(ReadOnlySpan<char> span, int i, string ascii)
    {
        if (i + ascii.Length > span.Length) return false;
        for (var k = 0; k < ascii.Length; k++)
        {
            var a = span[i + k];
            var b = ascii[k];
            if (a == b) continue;
            // ASCII case fold.
            if (a is >= 'A' and <= 'Z') a = (char)(a + 32);
            if (b is >= 'A' and <= 'Z') b = (char)(b + 32);
            if (a != b) return false;
        }
        return true;
    }

    private static bool IsCssWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n' or '\f';
}
