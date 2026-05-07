// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Walks a styled DOM (an <see cref="IDocument"/> + the
/// <see cref="ResolvedCascadeResult"/> from <see cref="VarResolver"/>) and produces
/// the box tree per CSS Display L3 §3 — generating one principal <see cref="Box"/>
/// per element, materializing <c>::before</c> and <c>::after</c> pseudo-element
/// boxes, wrapping anonymous text in <see cref="BoxKind.TextRun"/> boxes, and
/// inserting anonymous-block wrappers when a block-container has mixed
/// inline + block children per §3.1.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pipeline position.</b> Runs after <see cref="VarResolver"/> (Task 8) +
/// <see cref="CalcResolver"/> (Task 9) + <see cref="PropertyResolverDispatch"/>
/// (Task 10). The walk computes per-element <see cref="ComputedStyle"/>s on the
/// fly: for each element it iterates the resolved declarations, dispatches each
/// through <see cref="PropertyResolverDispatch.Resolve"/>, and materializes the
/// result via <see cref="ResolverResult.MaterializeInto"/>. Inheritance happens
/// here too — properties not explicitly set on an element fall back to the
/// parent's value when the property's <see cref="PropertyMeta.Inherits"/> is
/// true (otherwise the registry default applies).
/// </para>
/// <para>
/// <b>Scope through Task 14.</b> DOM walk + display dispatch + pseudo
/// materialization (<c>::before</c> / <c>::after</c> with single-string,
/// multi-string concatenation, and <c>attr(name)</c> content per
/// <see cref="CssContentList"/>) + anonymous-block insertion
/// (Display L3 §3.1) + replaced-element detection + <c>display: none</c>
/// skip + <c>display: contents</c> child-promotion (§3.1.1) +
/// <c>&lt;br&gt;</c> as a forced <see cref="BoxKind.LineBreak"/> + table
/// fixup (Tables L3 §3 — wrapper / grid split per §2.1, anon row-group /
/// row / cell synthesis for bare table internals, whitespace-only text
/// stripping between table internals per §3.1, tree-wide orphan fixup
/// for loose <c>display: table-cell</c> / row / row-group outside any
/// table ancestor per Tables L3 §3.1 "Generate Missing Parents") +
/// <c>::marker</c> for list-items (Lists L3 §3.1 + Pseudo L4 §3.4 — disc /
/// circle / square / decimal-family / roman / alpha / lower-greek per
/// Counter Styles 3 §6) + <c>::first-line</c> / <c>::first-letter</c>
/// cascade staging (block-container-only per Pseudo L4 §3.2 / §3.3;
/// Phase 3 line-layout consumes the staged styles).
/// </para>
/// <para>
/// <b>Out of scope (later cycles).</b> Generated content via
/// <c>counter()</c> / <c>counters()</c> (needs counter-reset / counter-
/// increment property machinery), <c>image()</c> / <c>url()</c> /
/// <c>linear-gradient()</c> (needs the resource pipeline),
/// <c>open-quote</c> / <c>close-quote</c> (needs quotation-stack with
/// depth tracking + <c>quotes</c> property), modern multi-arg <c>attr()</c>
/// type / fallback (needs the typed-value pipeline), block-in-inline
/// split (Display L3 §3.2), <c>list-style-image</c>, <c>&lt;ol start&gt;</c>
/// / <c>&lt;li value&gt;</c> attribute overrides.
/// </para>
/// </remarks>
internal static class BoxBuilder
{
    /// <summary>Walk <paramref name="document"/>'s element tree + emit the box
    /// tree. Returns the synthetic <see cref="BoxKind.Root"/> box; the document
    /// element's principal box is its first child.</summary>
    /// <param name="document">The HTML document — typically from
    /// <c>HtmlParsingHost.ParseAsync</c>.</param>
    /// <param name="cascade">Resolved (post-<c>var()</c>) cascade output for
    /// every styled element + pseudo-element.</param>
    /// <param name="diagnostics">Sink for property-resolution failures.
    /// <see langword="null"/> is allowed.</param>
    public static Box Build(
        IDocument document,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cascade);

        var rootStyle = ComputedStyle.Rent();
        ApplyDefaults(rootStyle);
        var root = Box.CreateRoot(rootStyle);

        if (document.DocumentElement is null) return root;

        // Per Task 16 review Rec 4 — dedupe CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001
        // emissions by (rule-source-location, pseudo-name, element-tag) so a
        // broad selector like `img::before { content: 'X' }` doesn't flood the
        // sink with one diagnostic per matching <img> in the document. The
        // cache is build-scoped — fresh per Build() call.
        var emittedPseudoSuppressionKeys = new System.Collections.Generic.HashSet<string>(
            StringComparer.Ordinal);

        var docBoxes = BuildElementBoxes(
            document.DocumentElement, parentStyle: rootStyle,
            cascade, diagnostics, emittedPseudoSuppressionKeys);
        foreach (var box in docBoxes)
        {
            root.AppendChild(box);
        }
        FixupAnonymousBlocks(root);

        // Per Rec 1 (Tables L3 §3.1 — Generate Missing Parents): a final
        // tree-wide pass wraps loose table internals (a CSS-only
        // `display: table-cell` directly inside <body>, a stray <tr> outside
        // any table, etc.) in synthesized table+grid+row-group+row scaffolding.
        // FixupTable above is wrapper-rooted; this pass picks up the orphans
        // it can't see.
        FixupOrphanedTableInternals(root);

        return root;
    }

    /// <summary>Recursive worker — emits the box(es) for an element. Usually
    /// returns a single-element list (the principal box), but returns an empty
    /// list for <c>display: none</c> and a flattened child list for
    /// <c>display: contents</c> (per Display L3 §3.1.1 the box itself
    /// disappears; the children become children of the grandparent).</summary>
    private static IReadOnlyList<Box> BuildElementBoxes(
        IElement element,
        ComputedStyle parentStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics,
        System.Collections.Generic.HashSet<string> emittedPseudoSuppressionKeys)
    {
        // <br> is special — it's a forced line break, not a generic inline.
        // Per HTML "Rendering" §15.3.6 the UA stylesheet defines:
        //   br { content: "\A"; white-space: pre-line }
        // i.e., a hard newline. We model it explicitly so Phase 3 line layout
        // can treat it as a break opportunity instead of an empty inline that
        // silently disappears during line construction.
        if (element.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            var brStyle = ComputedStyle.Rent();
            ApplyDefaults(brStyle);
            ApplyInheritance(brStyle, parentStyle);
            ApplyResolvedDeclarations(brStyle, cascade.TryGetStylesFor(element), diagnostics);
            // BoxKind.LineBreak with the source element so diagnostics still
            // point back to the right DOM node. The kind is inline-level.
            return new[] { Box.ForElement(BoxKind.LineBreak, brStyle, element) };
        }

        var style = ComputedStyle.Rent();
        ApplyDefaults(style);
        ApplyInheritance(style, parentStyle);
        ApplyResolvedDeclarations(style, cascade.TryGetStylesFor(element), diagnostics);

        var displayText = ReadDisplay(style, element);
        var mapResult = DisplayMapper.Map(displayText, element.LocalName, out var kind);

        switch (mapResult)
        {
            case DisplayMapper.DisplayMappingResult.None:
                style.Dispose();
                return Array.Empty<Box>();

            case DisplayMapper.DisplayMappingResult.Contents:
                // Per Display L3 §3.1.1: the principal box disappears + each
                // in-flow child gets promoted to the grandparent. We still use
                // `style` for inheritance into the children even though the
                // box itself isn't created.
                var promoted = new List<Box>();
                foreach (var node in element.ChildNodes)
                {
                    BuildChildNode(node, style, element, cascade, diagnostics,
                        emittedPseudoSuppressionKeys, promoted);
                }
                style.Dispose();
                return promoted;

            case DisplayMapper.DisplayMappingResult.Unsupported:
                // Cycle-1 fallback: ruby family + unknown values become block.
                kind = BoxKind.BlockContainer;
                break;
        }

        var box = Box.ForElement(kind, style, element);

        // Per Task 14: collect ::first-line / ::first-letter cascade styles
        // for Phase 3 line-layout to apply during fragment rendering. Box
        // generation cannot materialize them because the affected glyph extent
        // depends on line-breaking + container width, so we just stage them.
        StageFragmentPseudoStyles(box, element, style, cascade, diagnostics);

        // ::marker — for list-items, generated BEFORE ::before per Lists L3
        // §3.1: the marker is the first inline-level child of the list item
        // when `list-style-position: inside` and an out-of-flow sibling when
        // `outside`. Cycle 1 emits it as the first child regardless; layout
        // honors `list-style-position` later.
        if (kind == BoxKind.ListItem)
        {
            var marker = BuildListItemMarker(element, style, cascade, diagnostics);
            if (marker is not null) box.AppendChild(marker);
        }

        // ::before pseudo-element comes before the children. Keys in the cascade
        // are lowercase pseudo-element identifiers without the `::` prefix
        // (per SelectorCompiler's `PseudoElement(lower)` convention).
        var beforePseudo = BuildPseudo(element, "before", style, cascade, diagnostics,
            emittedPseudoSuppressionKeys);
        if (beforePseudo is not null) box.AppendChild(beforePseudo);

        // Children — DOM nodes in document order.
        var collector = new List<Box>(8);
        foreach (var node in element.ChildNodes)
        {
            BuildChildNode(node, style, element, cascade, diagnostics,
                emittedPseudoSuppressionKeys, collector);
        }
        foreach (var c in collector) box.AppendChild(c);

        // ::after pseudo-element comes after the children.
        var afterPseudo = BuildPseudo(element, "after", style, cascade, diagnostics,
            emittedPseudoSuppressionKeys);
        if (afterPseudo is not null) box.AppendChild(afterPseudo);

        FixupAnonymousBlocks(box);

        // Per Rec 4 (Tables L3 §3.1.4 — Drop unsupported boxes): TableColumn
        // accepts no children; TableColumnGroup accepts only TableColumn
        // children. Strip everything else so the column metadata path doesn't
        // accidentally render padding / text / nested elements that authors
        // erroneously put inside <col> / <colgroup>.
        if (box.Kind is BoxKind.TableColumn or BoxKind.TableColumnGroup)
        {
            DropIrrelevantColumnChildren(box);
        }

        // Table fixup per Tables L3 §3 — split the wrapper into
        // [captions..., TableGrid → fixed internals]; wrap bare rows / cells /
        // non-table content in the anon row-group / row / cell scaffolding the
        // table layout algorithm requires. Runs only on table wrappers since
        // the algorithm is wrapper-rooted.
        if (box.Kind is BoxKind.Table or BoxKind.InlineTable)
        {
            FixupTable(box);
        }

        return new[] { box };
    }

    /// <summary>Append boxes for one DOM child node (text → TextRun;
    /// element → recurse with display:contents flattening). Used by both the
    /// principal-box path + the display:contents promotion path.</summary>
    private static void BuildChildNode(
        INode node,
        ComputedStyle parentStyle,
        IElement parentElement,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics,
        System.Collections.Generic.HashSet<string> emittedPseudoSuppressionKeys,
        List<Box> collector)
    {
        switch (node)
        {
            case IText text:
                if (text.Data.Length > 0)
                {
                    collector.Add(Box.TextRun(text.Data, parentStyle, parentElement));
                }
                break;
            case IElement childElement:
                var produced = BuildElementBoxes(childElement, parentStyle, cascade,
                    diagnostics, emittedPseudoSuppressionKeys);
                foreach (var child in produced) collector.Add(child);
                break;
            // Comment / CDATA / etc. are ignored for box generation.
        }
    }

    // ============================================================
    // Task 14 — Pseudo-element materialization (::marker, ::first-line/letter)
    // ============================================================

    /// <summary>Build the marker box for a list-item (Lists L3 §3 + Pseudo
    /// L4 §3.4). Returns <see langword="null"/> for
    /// <c>list-style-type: none</c>; otherwise produces a
    /// <see cref="BoxKind.Marker"/> box carrying a <see cref="BoxPseudo.Marker"/>
    /// designation, a <see cref="BoxKind.TextRun"/> child holding the marker
    /// glyph (disc / number / alpha / roman per <c>list-style-type</c>), and
    /// a freshly-rented <see cref="ComputedStyle"/> that inherits from the
    /// list-item + applies the cascade's <c>::marker</c> rule if any.</summary>
    /// <remarks>
    /// Cycle-1 deferrals: <c>list-style-image</c> (needs the resource pipeline);
    /// <c>@counter-style</c> custom counters (post-v1); <c>&lt;ol start&gt;</c> /
    /// <c>&lt;li value&gt;</c> attribute overrides (cycle 2). The marker is
    /// always inserted as the first child of the list item — cycle 1 does NOT
    /// honor <c>list-style-position: outside</c> by attaching to the
    /// list-item's principal box's outside flow; that's a layout-time concern.
    /// </remarks>
    private static Box? BuildListItemMarker(
        IElement host,
        ComputedStyle hostStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics)
    {
        // Apply ::marker pseudo cascade if the author styled it. Per Task 14
        // review Rec 6 + CSS Pseudo L4 §3.4: only the marker-applicable
        // property subset (font-*, color, line-height, letter-spacing,
        // content, list-style-type, etc.) is honored; arbitrary
        // display/margin/padding/background slots would otherwise corrupt
        // the marker layout.
        var markerStyle = ComputedStyle.Rent();
        ApplyDefaults(markerStyle);
        ApplyInheritance(markerStyle, hostStyle);
        var markerRules = cascade.TryGetStylesForPseudo(host, "marker");
        if (markerRules is not null)
        {
            ApplyMarkerApplicableDeclarations(markerStyle, markerRules, diagnostics);
        }

        // Per PR #10 review Rec 2: read the EFFECTIVE list-style-type AFTER
        // marker rules apply so a `li::marker { list-style-type: square }`
        // declaration overrides the host list-item's value. Reading from
        // hostStyle before applying marker rules ignored the override.
        // (`list-style-type` is in MarkerApplicableProperties so the cascade
        // value lands on markerStyle when delivered.)
        var styleType = ReadListStyleType(markerStyle, host);
        if (styleType == "none")
        {
            markerStyle.Dispose();
            return null;
        }

        // Per Task 14 review Rec 2 + Task 16 review Rec 3 + CSS Pseudo L4
        // §3.4: ::marker accepts a `content` property that overrides the
        // default list-style-type marker. Three outcomes:
        //   - AbsentOrNormal: fall back to the list-style-type-derived glyph
        //   - Supported:      use the parsed override text
        //   - Unsupported:    SUPPRESS the marker entirely — falling back
        //                     to a default disc/decimal glyph would render
        //                     a marker the author explicitly did not request
        var (resolution, overrideText) =
            ResolveMarkerContent(host, markerRules, diagnostics);
        if (resolution == MarkerContentResolution.Unsupported)
        {
            markerStyle.Dispose();
            return null;
        }
        var markerText = resolution == MarkerContentResolution.Supported
            ? overrideText ?? string.Empty
            : MarkerTextFor(host, styleType, cascade);

        var markerBox = Box.ForPseudo(BoxKind.Marker, markerStyle, host, BoxPseudo.Marker);
        if (markerText.Length > 0)
        {
            markerBox.AppendChild(Box.TextRun(markerText, markerStyle, host));
        }
        return markerBox;
    }

    /// <summary>Per Task 14 review Rec 6 + CSS Pseudo L4 §3.4 — apply the
    /// subset of author <c>::marker</c> declarations that affect marker
    /// layout / rendering. The spec restricts ::marker to text-related
    /// properties (font-*, color, line-height, letter-spacing, word-spacing,
    /// text-transform, white-space, content, etc.); arbitrary <c>display</c>
    /// / <c>margin-*</c> / <c>padding-*</c> / <c>background-*</c> /
    /// <c>position</c> declarations would corrupt layout if honored.</summary>
    private static void ApplyMarkerApplicableDeclarations(
        ComputedStyle style,
        ResolvedRuleSet ruleSet,
        ICssDiagnosticsSink? diagnostics)
    {
        foreach (var winner in ruleSet.Winners)
        {
            if (!IsMarkerApplicableProperty(winner.Property)) continue;
            if (!PropertyMetadata.NameToId.TryGetValue(winner.Property, out var id)) continue;
            var location = winner.OriginalDeclaration.Location;
            var result = PropertyResolverDispatch.Resolve(id, winner.ResolvedValue, diagnostics, location);
            result.MaterializeInto(style, id);
        }
    }

    /// <summary>The CSS property name allowlist for <c>::marker</c> per
    /// Pseudo L4 §3.4. Cycle-1 covers the inheritable text + font subset that
    /// NetPdf currently registers in <c>properties.json</c>; future property
    /// additions (white-space, text-transform, text-decoration-*, etc.) will
    /// extend this list as they ship.</summary>
    private static readonly System.Collections.Generic.HashSet<string> MarkerApplicableProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "color",
            "content",
            "cursor",
            "font-style",
            "font-weight",
            "letter-spacing",
            "line-height",
            "list-style-position",   // affects whether marker is inline-flow
            "list-style-type",       // re-mapping default style on the marker itself
        };

    private static bool IsMarkerApplicableProperty(string propertyName) =>
        MarkerApplicableProperties.Contains(propertyName);

    /// <summary>Try to extract a marker text override from the
    /// <c>::marker { content: ... }</c> declaration. Returns the parsed text
    /// when the content is a supported form (string / multi-string /
    /// <c>attr()</c> per <see cref="CssContentList"/>); returns
    /// <see langword="null"/> when content is absent / <c>normal</c> /
    /// <c>none</c> / unsupported (let the default list-style-type marker
    /// take over).</summary>
    /// <remarks>
    /// <b>AngleSharp.Css 1.0.0-beta.144 limitation.</b> The current parser
    /// silently drops <c>::marker</c> selectors during CSS parse (similar to
    /// its <c>display: contents</c> behavior — see <see cref="DisplayMapper"/>
    /// remarks). As a result, in practice the cascade delivers
    /// <see langword="null"/> for <see cref="ResolvedCascadeResult.TryGetStylesForPseudo"/>
    /// with pseudo "marker" today, and this method always returns
    /// <see langword="null"/>. The implementation is still correct per CSS
    /// Pseudo L4 §3.4 — once cycle 2's CssPreprocessor recovery preserves
    /// <c>::marker</c> rules through the cascade, this path will fire without
    /// further changes.
    /// </remarks>
    /// <summary>Per Task 16 review Rec 3 — three-way result so callers can
    /// distinguish "no override (fall back to list-style-type)" from
    /// "unsupported content (suppress marker entirely after emitting
    /// diagnostic)". Rendering a default disc/decimal glyph when the author
    /// asked for <c>counter(items)</c> would surface a marker the author
    /// didn't request — worse than no marker at all.</summary>
    private enum MarkerContentResolution
    {
        /// <summary>No <c>content:</c> rule, or <c>content: normal</c> /
        /// <c>content: none</c> — fall back to the list-style-type glyph.</summary>
        AbsentOrNormal = 0,
        /// <summary>The author wrote <c>content:</c> with a supported value;
        /// use the resolved text returned alongside the resolution as the
        /// marker glyph.</summary>
        Supported = 1,
        /// <summary>The author wrote <c>content:</c> with an unsupported
        /// value (counter / url / quote / multi-arg attr); a diagnostic was
        /// emitted and the marker should be suppressed entirely (no glyph)
        /// per CSS Pseudo L4 §3.4 — falling back to a default disc/decimal
        /// would render a marker the author explicitly did not request.</summary>
        Unsupported = 2,
    }

    private static (MarkerContentResolution Resolution, string? ResolvedText) ResolveMarkerContent(
        IElement host, ResolvedRuleSet? markerRules, ICssDiagnosticsSink? diagnostics)
    {
        if (markerRules is null) return (MarkerContentResolution.AbsentOrNormal, null);
        var contentDecl = markerRules.GetWinner("content");
        if (contentDecl is null) return (MarkerContentResolution.AbsentOrNormal, null);
        var raw = contentDecl.ResolvedValue.Trim();
        if (raw.Length == 0
            || raw.Equals("normal", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return (MarkerContentResolution.AbsentOrNormal, null);
        }
        var location = contentDecl.OriginalDeclaration.Location;
        if (CssContentList.TryParse(raw, host, diagnostics, location, out var text))
        {
            return (MarkerContentResolution.Supported, text);
        }
        // CssContentList already emitted CSS-CONTENT-FUNCTION-UNSUPPORTED-001
        // (or CSS-ATTR-MULTI-ARG-UNSUPPORTED-001). Suppress the marker.
        return (MarkerContentResolution.Unsupported, null);
    }

    /// <summary>Read the computed <c>list-style-type</c> keyword from the
    /// list-item's style. When the cascade hasn't set it explicitly, walks
    /// up the DOM to find an <c>&lt;ol&gt;</c> / <c>&lt;ul&gt;</c> /
    /// <c>&lt;menu&gt;</c> ancestor and returns the corresponding HTML UA
    /// default per "Rendering" §15.3.4 — <c>decimal</c> for <c>ol</c>,
    /// <c>disc</c> for <c>ul</c>/<c>menu</c>. Defaults to <c>disc</c> when
    /// no list-context ancestor exists.</summary>
    private static string ReadListStyleType(ComputedStyle style, IElement element)
    {
        if (style.IsSet(PropertyId.ListStyleType))
        {
            var slot = style.Get(PropertyId.ListStyleType);
            if (slot.Tag == ComputedSlotTag.Keyword)
            {
                var name = ListStyleTypeName(slot.AsKeyword());
                if (name is not null) return name;
            }
        }
        // No cascade-resolved value — consult the nearest list-context
        // ancestor for the UA default.
        var ancestor = element.ParentElement;
        while (ancestor is not null)
        {
            var name = ancestor.LocalName;
            if (string.Equals(name, "ol", StringComparison.OrdinalIgnoreCase))
                return "decimal";
            if (string.Equals(name, "ul", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "menu", StringComparison.OrdinalIgnoreCase))
                return "disc";
            ancestor = ancestor.ParentElement;
        }
        return "disc";
    }

    /// <summary>Reverse-map <see cref="KeywordResolver"/>'s
    /// <see cref="PropertyId.ListStyleType"/> table indices to their string
    /// keywords. Cycle-1 maintains this manually; cycle 2 will source-generate
    /// the reverse map alongside the keyword tables themselves.</summary>
    private static string? ListStyleTypeName(int id) => id switch
    {
        0 => "none", 1 => "disc", 2 => "circle", 3 => "square",
        4 => "decimal", 5 => "decimal-leading-zero",
        6 => "lower-roman", 7 => "upper-roman",
        8 => "lower-alpha", 9 => "upper-alpha",
        10 => "lower-latin", 11 => "upper-latin",
        12 => "lower-greek",
        _ => null,
    };

    /// <summary>Compose the marker glyph + trailing space for the given
    /// list-style-type. Numeric / alphabetic markers consult
    /// <see cref="ComputeListItemPosition"/> (1-indexed sibling position
    /// among list-item children, where "list-item" means computed
    /// <c>display: list-item</c> per Task 14 review Rec 3).</summary>
    private static string MarkerTextFor(IElement host, string listStyleType, ResolvedCascadeResult cascade)
    {
        // Trailing NBSP keeps the marker visually attached to the content
        // even when text shaping aggressively collapses spaces around it.
        const string trailer = " ";
        return listStyleType switch
        {
            "disc" => "•" + trailer,             // •
            "circle" => "◦" + trailer,           // ◦
            "square" => "▪" + trailer,           // ▪
            "decimal" => ComputeListItemPosition(host, cascade).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + trailer,
            "decimal-leading-zero" => FormatDecimalLeadingZero(ComputeListItemPosition(host, cascade)) + "." + trailer,
            "lower-roman" => ToRoman(ComputeListItemPosition(host, cascade), upper: false) + "." + trailer,
            "upper-roman" => ToRoman(ComputeListItemPosition(host, cascade), upper: true) + "." + trailer,
            "lower-alpha" or "lower-latin" => ToAlpha(ComputeListItemPosition(host, cascade), upper: false) + "." + trailer,
            "upper-alpha" or "upper-latin" => ToAlpha(ComputeListItemPosition(host, cascade), upper: true) + "." + trailer,
            "lower-greek" => ToGreek(ComputeListItemPosition(host, cascade)) + "." + trailer,
            _ => "•" + trailer,
        };
    }

    /// <summary>Bijective base-24 Greek alphabetic conversion per CSS Counter
    /// Styles 3 §6 — symbols α β γ δ ε ζ η θ ι κ λ μ ν ξ ο π ρ σ τ υ φ χ ψ ω
    /// (omits final-sigma ς since lists use the medial form per Counter
    /// Styles 3 Appendix). Values 1..24 → α..ω; 25..600 → αα..ωω; etc.</summary>
    private static string ToGreek(int n)
    {
        if (n < 1) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, GreekLowerSymbols[n % GreekLowerSymbols.Length]);
            n /= GreekLowerSymbols.Length;
        }
        return sb.ToString();
    }

    /// <summary>The 24 lowercase Greek symbols used by <c>lower-greek</c>
    /// per CSS Counter Styles 3 §6.</summary>
    private static readonly char[] GreekLowerSymbols =
    {
        'α', 'β', 'γ', 'δ', 'ε', 'ζ', 'η', 'θ',
        'ι', 'κ', 'λ', 'μ', 'ν', 'ξ', 'ο', 'π',
        'ρ', 'σ', 'τ', 'υ', 'φ', 'χ', 'ψ', 'ω',
    };

    /// <summary>Count this <paramref name="host"/>'s 1-indexed position among
    /// its sibling list-items per Task 14 review Rec 3 — "list-item" is any
    /// element whose computed <c>display</c> is <c>list-item</c>, NOT just
    /// <c>&lt;li&gt;</c>. CSS-only authors who put <c>display: list-item</c>
    /// on a div get correct numbering. Cycle-1 still ignores
    /// <c>&lt;ol start&gt;</c> / <c>&lt;li value&gt;</c> attribute overrides
    /// (cycle 2); CSS <c>counter-reset</c> / <c>counter-increment</c> are
    /// out-of-scope until the property machinery lands.</summary>
    private static int ComputeListItemPosition(IElement host, ResolvedCascadeResult cascade)
    {
        var parent = host.ParentElement;
        if (parent is null) return 1;
        var index = 1;
        foreach (var sibling in parent.Children)
        {
            if (ReferenceEquals(sibling, host)) return index;
            if (IsListItemElement(sibling, cascade)) index++;
        }
        return index;
    }

    /// <summary>Per Task 14 review Rec 3 — <see langword="true"/> when
    /// <paramref name="element"/>'s computed <c>display</c> is
    /// <c>list-item</c>. The HTML UA default for <c>&lt;li&gt;</c> is
    /// <c>list-item</c> (per HtmlDefaultDisplay), but any element can have
    /// <c>display: list-item</c> via CSS — we must count those too.</summary>
    private static bool IsListItemElement(IElement element, ResolvedCascadeResult cascade)
    {
        // Check explicit cascade rule first; if absent or doesn't change
        // display, fall back to the UA default for the element's local name.
        var ruleSet = cascade.TryGetStylesFor(element);
        if (ruleSet is not null)
        {
            var displayDecl = ruleSet.GetWinner("display");
            if (displayDecl is not null)
            {
                var v = displayDecl.ResolvedValue.Trim();
                return v.Equals("list-item", StringComparison.OrdinalIgnoreCase);
            }
        }
        // Fall back to UA default — only `<li>` defaults to list-item.
        return string.Equals(element.LocalName, "li", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Format a list position with a leading zero when the count is
    /// less than 10 (per Lists L3 §7.1's <c>decimal-leading-zero</c>).</summary>
    private static string FormatDecimalLeadingZero(int n) =>
        n < 10 && n >= 0
            ? "0" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Roman-numeral conversion 1..3999. Out-of-range values fall
    /// back to decimal per Lists L3 §7.1.4.</summary>
    private static string ToRoman(int n, bool upper)
    {
        if (n < 1 || n > 3999)
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        var pairs = new (string, int)[]
        {
            ("M", 1000), ("CM", 900), ("D", 500), ("CD", 400),
            ("C", 100), ("XC", 90), ("L", 50), ("XL", 40),
            ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1),
        };
        foreach (var (sym, val) in pairs)
        {
            while (n >= val) { sb.Append(sym); n -= val; }
        }
        var s = sb.ToString();
        return upper ? s : s.ToLowerInvariant();
    }

    /// <summary>Bijective base-26 alphabetic conversion (1→a, 26→z, 27→aa, …)
    /// per Lists L3 §7.1.4.</summary>
    private static string ToAlpha(int n, bool upper)
    {
        if (n < 1) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new System.Text.StringBuilder();
        var baseChar = upper ? 'A' : 'a';
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(baseChar + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }

    /// <summary>Stage <c>::first-line</c> + <c>::first-letter</c> cascade
    /// styles on <paramref name="box"/> for Phase 3 line-layout to apply
    /// during fragment rendering. Box generation cannot materialize them as
    /// boxes because the affected glyph extent depends on line-breaking +
    /// container width — Phase 3 owns that. Returns silently when no rules
    /// apply (the common case).</summary>
    private static void StageFragmentPseudoStyles(
        Box box,
        IElement element,
        ComputedStyle elementStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics)
    {
        // Per Task 14 review Rec 5 + CSS Pseudo L4 §3.2 / §3.3:
        // ::first-line and ::first-letter only apply to block-container
        // boxes (their semantics describe styling of the first line / first
        // typographic letter of the inline formatting context the box
        // establishes). Skip the rent + apply work for ineligible kinds.
        if (!IsBlockContainer(box.Kind)) return;

        var firstLineRules = cascade.TryGetStylesForPseudo(element, "first-line");
        if (firstLineRules is not null)
        {
            var s = ComputedStyle.Rent();
            ApplyDefaults(s);
            ApplyInheritance(s, elementStyle);
            ApplyResolvedDeclarations(s, firstLineRules, diagnostics);
            box.FirstLineStyle = s;
        }

        var firstLetterRules = cascade.TryGetStylesForPseudo(element, "first-letter");
        if (firstLetterRules is not null)
        {
            var s = ComputedStyle.Rent();
            ApplyDefaults(s);
            ApplyInheritance(s, elementStyle);
            ApplyResolvedDeclarations(s, firstLetterRules, diagnostics);
            box.FirstLetterStyle = s;
        }
    }

    /// <summary>Task 16 cycle 1 — emit
    /// <see cref="NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001"/>
    /// when an author's <c>::before</c> / <c>::after</c> rule targets a
    /// replaced element. Replaced elements (img/video/canvas/iframe/object/
    /// embed) are atomic per CSS Pseudo L4 §3 — generated content has no
    /// place to attach. The author should know their rule has no effect.
    /// Pulls the source location from the rule's <c>content</c> declaration
    /// when available, so the diagnostic points back at the offending
    /// rule.</summary>
    private static void EmitPseudoSuppressedOnReplaced(
        IElement host, string pseudoName, ResolvedRuleSet ruleSet,
        ICssDiagnosticsSink? diagnostics,
        System.Collections.Generic.HashSet<string> emittedKeys)
    {
        if (diagnostics is null) return;
        var contentDecl = ruleSet.GetWinner("content");
        var location = contentDecl is not null
            ? contentDecl.OriginalDeclaration.Location
            : NetPdf.Css.Parser.CssSourceLocation.Unknown;

        // Per Task 16 review Rec 4 — dedupe by (rule-source-location, pseudo,
        // element-tag). One broad selector like `img::before` would otherwise
        // emit per matching <img> in the document, flooding the sink. When the
        // location is Unknown (no source-tracking; tests / synthesized CSS),
        // fall back to (pseudo, tag) so the diagnostic still emits at most once
        // per pseudo+tag pair.
        var locKey = location.Source is not null
            ? $"{location.Source}:{location.Line}:{location.Column}"
            : "?";
        var dedupKey = $"{locKey}|{pseudoName}|{host.LocalName.ToLowerInvariant()}";
        if (!emittedKeys.Add(dedupKey)) return;

        diagnostics.Emit(new NetPdf.Css.Diagnostics.CssDiagnostic(
            NetPdf.Css.Diagnostics.CssDiagnosticCodes.CssPseudoSuppressedOnReplaced001,
            $"::{pseudoName} on replaced <{host.LocalName}> is suppressed (CSS Pseudo L4 §3 — replaced elements cannot host generated content).",
            NetPdf.Css.Diagnostics.CssDiagnosticSeverity.Info,
            location));
    }

    /// <summary>Generate the box for a pseudo-element rule set when one is
    /// registered + has a non-<c>none</c>/non-<c>normal</c> <c>content</c>
    /// property. Task 14 cycle 1 supports single + multi-string concatenation
    /// + <c>attr(<i>name</i>)</c>; <c>counter()</c>/<c>counters()</c>,
    /// <c>image()</c>/<c>url()</c>, and <c>open-quote</c>/<c>close-quote</c>
    /// are still skipped (no pseudo box) — counters need the
    /// counter-reset/increment property machinery (cycle 2), images need the
    /// resource pipeline, quotes need a stack-aware quotation depth resolver.</summary>
    private static Box? BuildPseudo(
        IElement host,
        string pseudoName,
        ComputedStyle hostStyle,
        ResolvedCascadeResult cascade,
        ICssDiagnosticsSink? diagnostics,
        System.Collections.Generic.HashSet<string> emittedPseudoSuppressionKeys)
    {
        // Per Task 14 review Rec 1 + CSS Pseudo L4 §3 — replaced elements
        // (img/video/canvas/iframe/object/embed) are atomic and have no
        // place to host generated content. ::before / ::after declarations
        // on a replaced originating element generate no pseudo box.
        var ruleSet = cascade.TryGetStylesForPseudo(host, pseudoName);
        if (HtmlReplacedElements.IsReplaced(host.LocalName))
        {
            // Task 16 cycle 1: emit a diagnostic so authors learn their
            // ::before/::after rule on a replaced element will have no
            // effect. Only emit when a rule actually targeted the pseudo —
            // a replaced element with no rules doesn't warrant noise.
            if (ruleSet is not null)
            {
                EmitPseudoSuppressedOnReplaced(host, pseudoName, ruleSet,
                    diagnostics, emittedPseudoSuppressionKeys);
            }
            return null;
        }

        if (ruleSet is null) return null;

        var contentDecl = ruleSet.GetWinner("content");
        if (contentDecl is null) return null;

        var rawContent = contentDecl.ResolvedValue.Trim();
        if (rawContent.Length == 0
            || rawContent.Equals("none", StringComparison.OrdinalIgnoreCase)
            || rawContent.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Task 14: try the extended content-list parser (multi-string + attr())
        // before falling back to single-string. Counter / image / quote tokens
        // still produce null (skip the pseudo). Task 16 cycle 1 — pass the
        // diagnostics sink + declaration source location through so the parser
        // can emit CSS-CONTENT-FUNCTION-UNSUPPORTED-001 / CSS-ATTR-MULTI-ARG-
        // UNSUPPORTED-001 when it rejects.
        var contentLocation = contentDecl.OriginalDeclaration.Location;
        if (!CssContentList.TryParse(rawContent, host, diagnostics, contentLocation, out var generatedText))
        {
            return null;
        }

        // Build the pseudo's style — inherits from host, then applies the
        // pseudo's own resolved declarations.
        var pseudoStyle = ComputedStyle.Rent();
        ApplyDefaults(pseudoStyle);
        ApplyInheritance(pseudoStyle, hostStyle);
        ApplyResolvedDeclarations(pseudoStyle, ruleSet, diagnostics);

        // Per CSS Pseudo L4 §3.1: ::before / ::after default to display:inline
        // unless the cascade explicitly sets otherwise. We must NOT fall back
        // to the host's UA-default display (e.g., `<div>::before` would
        // wrongly default to block). And replaced-element detection must NOT
        // consult the host's local name (a pseudo-element on `<img>` is not
        // itself replaced).
        var displayText = ReadPseudoDisplay(pseudoStyle);
        var map = DisplayMapper.Map(displayText, elementLocalName: null, out var kind);
        if (map == DisplayMapper.DisplayMappingResult.None)
        {
            pseudoStyle.Dispose();
            return null;
        }
        if (map != DisplayMapper.DisplayMappingResult.Resolved)
        {
            kind = BoxKind.InlineBox;
        }

        var pseudo = pseudoName.Equals("before", StringComparison.OrdinalIgnoreCase)
            ? BoxPseudo.Before
            : BoxPseudo.After;

        var pseudoBox = Box.ForPseudo(kind, pseudoStyle, host, pseudo);
        if (generatedText.Length > 0)
        {
            pseudoBox.AppendChild(Box.TextRun(generatedText, pseudoStyle, host));
        }
        return pseudoBox;
    }

    /// <summary>Inserts <see cref="BoxKind.AnonymousBlock"/> wrappers around
    /// contiguous inline-level child runs whenever <paramref name="parent"/> is
    /// a "block container" per CSS Display L3 §2.1 + §3.1 — that is, a box
    /// whose inner formatting context lays out children as either a BFC or an
    /// IFC. The set covers block-level outers (<see cref="BoxKind.Root"/>,
    /// <see cref="BoxKind.BlockContainer"/>, <see cref="BoxKind.ListItem"/>,
    /// <see cref="BoxKind.AnonymousBlock"/>) AND inline-level boxes whose
    /// inner FC is flow-root (<see cref="BoxKind.InlineBlockContainer"/>) AND
    /// table cells (<see cref="BoxKind.TableCell"/>) + table captions
    /// (<see cref="BoxKind.TableCaption"/>). Operates in-place on
    /// <see cref="Box.Children"/>.</summary>
    private static void FixupAnonymousBlocks(Box parent)
    {
        if (!IsBlockContainer(parent.Kind)) return;
        if (parent.Children.Count == 0) return;

        var hasBlock = false;
        var hasInline = false;
        for (var i = 0; i < parent.Children.Count; i++)
        {
            var c = parent.Children[i];
            if (c.IsBlockLevel) hasBlock = true;
            else if (c.IsInlineLevel) hasInline = true;
            if (hasBlock && hasInline) break;
        }
        if (!(hasBlock && hasInline)) return;

        var snapshot = new List<Box>(parent.Children.Count);
        foreach (var c in parent.Children) snapshot.Add(c);
        foreach (var c in snapshot) parent.RemoveChild(c);

        List<Box>? currentRun = null;
        foreach (var c in snapshot)
        {
            if (c.IsBlockLevel)
            {
                FlushRun(parent, ref currentRun);
                parent.AppendChild(c);
            }
            else
            {
                currentRun ??= new List<Box>();
                currentRun.Add(c);
            }
        }
        FlushRun(parent, ref currentRun);

        static void FlushRun(Box parent, ref List<Box>? run)
        {
            if (run is null || run.Count == 0) return;
            var wrapper = Box.Anonymous(BoxKind.AnonymousBlock, parent.Style);
            foreach (var child in run) wrapper.AppendChild(child);
            parent.AppendChild(wrapper);
            run = null;
        }
    }

    /// <summary><see langword="true"/> when <paramref name="kind"/> establishes
    /// a "block container" per Display L3 §2.1 — i.e., a box whose inner
    /// formatting context is block (flow / flow-root) and therefore subject
    /// to the §3.1 anonymous-block insertion rule when its children mix
    /// block-level + inline-level. Excludes flex / grid containers (their
    /// inner FCs are flex / grid, not flow) and table internals other than
    /// cell + caption (which have their own table-internal FC).</summary>
    private static bool IsBlockContainer(BoxKind kind) => kind switch
    {
        BoxKind.Root or BoxKind.BlockContainer or BoxKind.ListItem
            or BoxKind.AnonymousBlock or BoxKind.InlineBlockContainer
            or BoxKind.TableCell or BoxKind.TableCaption => true,
        _ => false,
    };

    // ============================================================
    // Task 13 — Table fixup per CSS Tables L3 §3
    // ============================================================

    /// <summary>Per Rec 5 — captions partitioned by their position relative
    /// to the first non-caption table internal in source order. Before-set
    /// goes ahead of the synthesized grid; After-set goes after it.</summary>
    private readonly struct TableCaptionsSplit
    {
        public TableCaptionsSplit() { Before = new(); After = new(); }
        public List<Box> Before { get; }
        public List<Box> After { get; }
    }

    /// <summary>Restructure a table wrapper (<see cref="BoxKind.Table"/> or
    /// <see cref="BoxKind.InlineTable"/>) per CSS Tables L3 §2.1 + §3.
    /// After fixup the wrapper has the shape:
    /// <code>
    /// Wrapper → [TableCaption*, TableGrid → [TableRowGroup | TableColumnGroup | TableColumn]*]
    /// </code>
    /// — captions stay as direct children of the wrapper (so caption-side
    /// margins compose against the wrapper, not the grid); everything else
    /// moves under an anonymous <see cref="BoxKind.TableGrid"/>; bare rows /
    /// cells / non-table content inside the grid get wrapped in synthesized
    /// row-group / row / cell anonymous boxes per §3.1.1 + §3.1.2;
    /// whitespace-only text between table internals is stripped per §3.1.</summary>
    private static void FixupTable(Box wrapper)
    {
        var snapshot = SnapshotChildren(wrapper);
        var filtered = StripWhitespaceTextRuns(snapshot);

        // Per Rec 5: split captions into "before any internal" / "after first
        // internal" buckets so the grid sits in the correct source-order
        // position relative to each caption. Caption-side rendering position
        // is layout-time, but the box tree should faithfully reflect source
        // order around the synthesized grid.
        var captions = new TableCaptionsSplit();
        var internals = new List<Box>();
        var sawInternal = false;
        foreach (var c in filtered)
        {
            if (c.Kind == BoxKind.TableCaption)
            {
                if (sawInternal) captions.After.Add(c);
                else captions.Before.Add(c);
            }
            else
            {
                sawInternal = true;
                internals.Add(c);
            }
        }

        // Recurse into explicit row-groups + rows so their bare cells / non-cell
        // content gets wrapped before grid-level fixup runs.
        foreach (var box in internals)
        {
            switch (box.Kind)
            {
                case BoxKind.TableRowGroup:
                case BoxKind.TableHeaderGroup:
                case BoxKind.TableFooterGroup:
                    FixRowGroupContents(box);
                    break;
                case BoxKind.TableRow:
                    FixRowContents(box);
                    break;
            }
        }

        var gridChildren = GridLevelFixup(internals, wrapper.Style);

        // Reassemble per Rec 5: preserve caption position relative to the grid.
        // Captions appearing before any non-caption internal stay before the
        // grid; captions appearing after the first internal go after the grid.
        // (Visual position is determined by `caption-side`, but the box-tree
        // position should faithfully reflect source order around the grid.)
        foreach (var caption in captions.Before) wrapper.AppendChild(caption);
        var grid = Box.Anonymous(BoxKind.TableGrid, CreateAnonBoxStyle(wrapper.Style));
        foreach (var c in gridChildren) grid.AppendChild(c);
        wrapper.AppendChild(grid);
        foreach (var caption in captions.After) wrapper.AppendChild(caption);
    }

    /// <summary>Wrap bare <see cref="BoxKind.TableRow"/> children in an
    /// anonymous <see cref="BoxKind.TableRowGroup"/>; wrap bare
    /// <see cref="BoxKind.TableCell"/> + non-table content in an anonymous
    /// row-group → row → (cell | anon-cell). Pass-through:
    /// <see cref="BoxKind.TableRowGroup"/>, <see cref="BoxKind.TableHeaderGroup"/>,
    /// <see cref="BoxKind.TableFooterGroup"/>, <see cref="BoxKind.TableColumn"/>,
    /// <see cref="BoxKind.TableColumnGroup"/>.</summary>
    private static List<Box> GridLevelFixup(List<Box> items, ComputedStyle anonStyle)
    {
        var result = new List<Box>();
        List<Box>? bareRowRun = null;
        List<Box>? bareNonRowRun = null;

        foreach (var item in items)
        {
            switch (item.Kind)
            {
                case BoxKind.TableRowGroup:
                case BoxKind.TableHeaderGroup:
                case BoxKind.TableFooterGroup:
                case BoxKind.TableColumn:
                case BoxKind.TableColumnGroup:
                    FlushBareRowRun(result, ref bareRowRun, anonStyle);
                    FlushBareNonRowRun(result, ref bareNonRowRun, anonStyle);
                    result.Add(item);
                    break;
                case BoxKind.TableRow:
                    FlushBareNonRowRun(result, ref bareNonRowRun, anonStyle);
                    bareRowRun ??= new List<Box>();
                    bareRowRun.Add(item);
                    break;
                default:
                    FlushBareRowRun(result, ref bareRowRun, anonStyle);
                    bareNonRowRun ??= new List<Box>();
                    bareNonRowRun.Add(item);
                    break;
            }
        }
        FlushBareRowRun(result, ref bareRowRun, anonStyle);
        FlushBareNonRowRun(result, ref bareNonRowRun, anonStyle);
        return result;

        static void FlushBareRowRun(List<Box> dest, ref List<Box>? run, ComputedStyle style)
        {
            if (run is null || run.Count == 0) return;
            var anonGroup = NewAnonTablePart(BoxKind.TableRowGroup, style);
            foreach (var row in run) anonGroup.AppendChild(row);
            dest.Add(anonGroup);
            run = null;
        }

        static void FlushBareNonRowRun(List<Box> dest, ref List<Box>? run, ComputedStyle style)
        {
            if (run is null || run.Count == 0) return;
            // Bare non-rows at the grid level: wrap in anon row-group → anon row,
            // then bare cells pass through and non-cells wrap in anon cells.
            var anonGroup = NewAnonTablePart(BoxKind.TableRowGroup, style);
            var anonRow = NewAnonTablePart(BoxKind.TableRow, style);
            List<Box>? nonCellRun = null;
            foreach (var item in run)
            {
                if (item.Kind == BoxKind.TableCell)
                {
                    FlushNonCellRun(anonRow, ref nonCellRun, style);
                    anonRow.AppendChild(item);
                }
                else
                {
                    nonCellRun ??= new List<Box>();
                    nonCellRun.Add(item);
                }
            }
            FlushNonCellRun(anonRow, ref nonCellRun, style);
            anonGroup.AppendChild(anonRow);
            dest.Add(anonGroup);
            run = null;
        }
    }

    /// <summary>Wrap bare <see cref="BoxKind.TableCell"/> + non-table content
    /// children of an explicit row-group in an anonymous
    /// <see cref="BoxKind.TableRow"/>; pass-through real
    /// <see cref="BoxKind.TableRow"/> children. Mutates
    /// <paramref name="rowGroup"/> in place.</summary>
    private static void FixRowGroupContents(Box rowGroup)
    {
        var snapshot = SnapshotChildren(rowGroup);
        var filtered = StripWhitespaceTextRuns(snapshot);

        // Recurse into explicit rows first so their non-cell content gets
        // wrapped before we re-attach.
        foreach (var c in filtered)
        {
            if (c.Kind == BoxKind.TableRow) FixRowContents(c);
        }

        var fixedChildren = new List<Box>();
        List<Box>? bareNonRowRun = null;

        foreach (var c in filtered)
        {
            if (c.Kind == BoxKind.TableRow)
            {
                FlushBareNonRowRun(fixedChildren, ref bareNonRowRun, rowGroup.Style);
                fixedChildren.Add(c);
            }
            else
            {
                bareNonRowRun ??= new List<Box>();
                bareNonRowRun.Add(c);
            }
        }
        FlushBareNonRowRun(fixedChildren, ref bareNonRowRun, rowGroup.Style);

        foreach (var c in fixedChildren) rowGroup.AppendChild(c);

        static void FlushBareNonRowRun(List<Box> dest, ref List<Box>? run, ComputedStyle style)
        {
            if (run is null || run.Count == 0) return;
            var anonRow = NewAnonTablePart(BoxKind.TableRow, style);
            List<Box>? nonCellRun = null;
            foreach (var item in run)
            {
                if (item.Kind == BoxKind.TableCell)
                {
                    FlushNonCellRun(anonRow, ref nonCellRun, style);
                    anonRow.AppendChild(item);
                }
                else
                {
                    nonCellRun ??= new List<Box>();
                    nonCellRun.Add(item);
                }
            }
            FlushNonCellRun(anonRow, ref nonCellRun, style);
            dest.Add(anonRow);
            run = null;
        }
    }

    /// <summary>Wrap non-<see cref="BoxKind.TableCell"/> children of an explicit
    /// row in anonymous cells; pass-through real cells. Mutates
    /// <paramref name="row"/> in place.</summary>
    private static void FixRowContents(Box row)
    {
        var snapshot = SnapshotChildren(row);
        var filtered = StripWhitespaceTextRuns(snapshot);

        var fixedChildren = new List<Box>();
        List<Box>? nonCellRun = null;

        foreach (var c in filtered)
        {
            if (c.Kind == BoxKind.TableCell)
            {
                FlushNonCellRun(fixedChildren, ref nonCellRun, row.Style);
                fixedChildren.Add(c);
            }
            else
            {
                nonCellRun ??= new List<Box>();
                nonCellRun.Add(c);
            }
        }
        FlushNonCellRun(fixedChildren, ref nonCellRun, row.Style);

        foreach (var c in fixedChildren) row.AppendChild(c);

        static void FlushNonCellRun(List<Box> dest, ref List<Box>? run, ComputedStyle style)
        {
            if (run is null || run.Count == 0) return;
            var anonCell = NewAnonTablePart(BoxKind.TableCell, style);
            foreach (var item in run) anonCell.AppendChild(item);
            dest.Add(anonCell);
            run = null;
        }
    }

    /// <summary>Helper used by <see cref="GridLevelFixup"/> +
    /// <see cref="FixRowGroupContents"/> to wrap consecutive non-cell items in
    /// an anonymous <see cref="BoxKind.TableCell"/> per Tables L3 §3.1.</summary>
    private static void FlushNonCellRun(Box anonRow, ref List<Box>? run, ComputedStyle style)
    {
        if (run is null || run.Count == 0) return;
        var anonCell = NewAnonTablePart(BoxKind.TableCell, style);
        foreach (var item in run) anonCell.AppendChild(item);
        anonRow.AppendChild(anonCell);
        run = null;
    }

    /// <summary>Detach <paramref name="parent"/>'s children + return them as a
    /// fresh list. Used by table fixup to restructure children without
    /// mutating the live <see cref="Box.Children"/> view mid-walk.</summary>
    private static List<Box> SnapshotChildren(Box parent)
    {
        var snapshot = new List<Box>(parent.Children.Count);
        foreach (var c in parent.Children) snapshot.Add(c);
        foreach (var c in snapshot) parent.RemoveChild(c);
        return snapshot;
    }

    /// <summary>Drop whitespace-only <see cref="BoxKind.TextRun"/> entries —
    /// per Tables L3 §3.1, sequences of inline boxes whose only content is
    /// "white space" between table internals do NOT generate boxes. Without
    /// this, AngleSharp's preserved indentation between <c>&lt;tr&gt;</c> /
    /// <c>&lt;td&gt;</c> elements would otherwise become anonymous
    /// table-cells full of whitespace.</summary>
    private static List<Box> StripWhitespaceTextRuns(List<Box> children)
    {
        var result = new List<Box>(children.Count);
        foreach (var c in children)
        {
            if (c.Kind == BoxKind.TextRun && IsWhitespaceOnly(c.Text)) continue;
            result.Add(c);
        }
        return result;
    }

    /// <summary><see langword="true"/> when every char in <paramref name="text"/>
    /// is CSS whitespace (space, tab, LF, CR, FF). An empty string is treated
    /// as whitespace-only.</summary>
    private static bool IsWhitespaceOnly(string text)
    {
        foreach (var c in text)
        {
            if (c is not (' ' or '\t' or '\n' or '\r' or '\f')) return false;
        }
        return true;
    }

    /// <summary>Per Rec 1 (Tables L3 §3.1 — Generate Missing Parents): walk
    /// the box tree post-order and wrap orphan table internals
    /// (<see cref="BoxKind.TableCell"/> not in a row, <see cref="BoxKind.TableRow"/>
    /// not in a row-group, etc.) in synthesized anonymous Table wrappers.
    /// <see cref="FixupTable"/> then runs on each synthesized wrapper to insert
    /// the full row-group / row / cell scaffolding the table layout
    /// algorithm requires.</summary>
    /// <remarks>
    /// Recurses into every box's children before checking the parent itself —
    /// this lets a TableCell inside a non-table div correctly get wrapped
    /// even if the cell's own subtree contains further orphans. The recursion
    /// also descends into table internals (e.g., into a TableCell's content)
    /// since those cells are block containers and may themselves host further
    /// orphan internals; the orphan-check at the end is suppressed only for
    /// table internals whose direct children are already governed by
    /// <see cref="FixupTable"/> (the wrapper / grid / row-group / row /
    /// column / column-group / caption families).
    /// </remarks>
    private static void FixupOrphanedTableInternals(Box parent)
    {
        // Recurse first — children get fixed up before the parent decides
        // whether to wrap them. Snapshot to avoid mutation-during-iteration.
        var children = new List<Box>(parent.Children.Count);
        foreach (var c in parent.Children) children.Add(c);
        foreach (var c in children) FixupOrphanedTableInternals(c);

        // Suppress orphan check for boxes whose direct children are already
        // governed by FixupTable. TableCell + TableCaption are block
        // containers hosting arbitrary content, so their direct children CAN
        // contain orphans; the others (Table, InlineTable, TableGrid,
        // TableRowGroup family, TableRow, TableColumn, TableColumnGroup) have
        // their structures pinned by FixupTable + DropIrrelevantColumnChildren.
        if (parent.IsTablePart
            && parent.Kind is not (BoxKind.TableCell or BoxKind.TableCaption))
        {
            return;
        }

        // Sweep parent's (possibly newly-restructured) children for orphans.
        if (!HasOrphanedTableInternals(parent)) return;

        var snapshot = SnapshotChildren(parent);
        var result = new List<Box>();
        List<Box>? orphanRun = null;

        foreach (var child in snapshot)
        {
            if (IsOrphanedTableInternal(child, parent.Kind))
            {
                orphanRun ??= new List<Box>();
                orphanRun.Add(child);
            }
            else
            {
                FlushOrphanRun(result, ref orphanRun, parent.Style);
                result.Add(child);
            }
        }
        FlushOrphanRun(result, ref orphanRun, parent.Style);

        foreach (var c in result) parent.AppendChild(c);

        // Wrapping orphans introduces block-level Table boxes that may need
        // to be separated from inline siblings via anonymous-block insertion
        // (e.g., `<body><span>x</span><div display:table-cell>y</div></body>`).
        FixupAnonymousBlocks(parent);

        static void FlushOrphanRun(List<Box> result, ref List<Box>? run, ComputedStyle parentStyle)
        {
            if (run is null || run.Count == 0) return;
            // Synthesize a block-level Table wrapper around the orphans, then
            // run FixupTable to apply the standard row-group / row / cell
            // wrapping rules on its now-internal children.
            var anonTable = NewAnonTablePart(BoxKind.Table, parentStyle);
            foreach (var orphan in run) anonTable.AppendChild(orphan);
            FixupTable(anonTable);
            result.Add(anonTable);
            run = null;
        }
    }

    /// <summary>Quick test for "any of <paramref name="parent"/>'s children
    /// is an orphan that needs wrapping". Skips the snapshot/restructure path
    /// when there's nothing to do.</summary>
    private static bool HasOrphanedTableInternals(Box parent)
    {
        foreach (var c in parent.Children)
        {
            if (IsOrphanedTableInternal(c, parent.Kind)) return true;
        }
        return false;
    }

    /// <summary>Per Tables L3 §3.1, a child box's parent kind is constrained
    /// by its own kind. Returns <see langword="true"/> when the
    /// <paramref name="child"/> + <paramref name="parentKind"/> pair violates
    /// those constraints — i.e., the child is misparented and needs an
    /// anonymous wrapper.</summary>
    private static bool IsOrphanedTableInternal(Box child, BoxKind parentKind) => child.Kind switch
    {
        BoxKind.TableCaption => parentKind is not (BoxKind.Table or BoxKind.InlineTable),
        BoxKind.TableRowGroup or BoxKind.TableHeaderGroup or BoxKind.TableFooterGroup
            => parentKind != BoxKind.TableGrid,
        BoxKind.TableRow => parentKind is not
            (BoxKind.TableRowGroup or BoxKind.TableHeaderGroup or BoxKind.TableFooterGroup),
        BoxKind.TableCell => parentKind != BoxKind.TableRow,
        BoxKind.TableColumn => parentKind is not (BoxKind.TableColumnGroup or BoxKind.TableGrid),
        BoxKind.TableColumnGroup => parentKind != BoxKind.TableGrid,
        _ => false,
    };

    /// <summary>Per Rec 4 (Tables L3 §3.1.4) — strip children that the
    /// box-generation rules forbid for column / column-group elements.
    /// <see cref="BoxKind.TableColumn"/> accepts no children at all (a column
    /// is metadata-only — it controls the i'th column's width / styling but
    /// has no rendered content). <see cref="BoxKind.TableColumnGroup"/>
    /// accepts only <see cref="BoxKind.TableColumn"/> children.</summary>
    private static void DropIrrelevantColumnChildren(Box columnish)
    {
        if (columnish.Children.Count == 0) return;
        var snapshot = SnapshotChildren(columnish);
        if (columnish.Kind == BoxKind.TableColumn) return; // Drop everything.
        // TableColumnGroup: keep only TableColumn children.
        foreach (var c in snapshot)
        {
            if (c.Kind == BoxKind.TableColumn) columnish.AppendChild(c);
        }
    }

    /// <summary>Construct an anonymous table-internal box (row-group, row,
    /// cell). The public <see cref="Box.Anonymous"/> factory is restricted to
    /// always-anonymous kinds (<see cref="BoxKind.Root"/>,
    /// <see cref="BoxKind.LineBox"/>, <see cref="BoxKind.AnonymousBlock"/>,
    /// <see cref="BoxKind.AnonymousInline"/>, <see cref="BoxKind.TableGrid"/>);
    /// table internals can be either source-backed (from
    /// <c>&lt;tr&gt;</c>/<c>&lt;td&gt;</c>/<c>&lt;tbody&gt;</c>) or anonymous
    /// (synthesized here by table fixup), so we use the constructor directly.</summary>
    /// <remarks>
    /// <b>Style isolation</b> per CSS 2.1 §17.5.1 / Tables L3 §3.2: the new
    /// anonymous box gets its own freshly-rented <see cref="ComputedStyle"/>
    /// — inheritable properties are copied from <paramref name="parentStyle"/>
    /// (so <c>color</c> / <c>font-family</c> / <c>line-height</c> propagate
    /// through the synthesized scaffolding), but non-inheritable properties
    /// (<c>background</c>, <c>border</c>, <c>padding</c>, <c>margin</c>,
    /// <c>display</c>) reset to their CSS initial values. Sharing the wrapper's
    /// style instance directly would leak the wrapper's background / borders
    /// onto every anon row-group, row, and cell.
    /// </remarks>
    private static Box NewAnonTablePart(BoxKind kind, ComputedStyle parentStyle)
    {
        var anonStyle = CreateAnonBoxStyle(parentStyle);
        return new Box(kind, anonStyle, sourceElement: null, BoxPseudo.None, text: string.Empty);
    }

    /// <summary>Rent + initialize a fresh <see cref="ComputedStyle"/> for an
    /// anonymous box. Non-inheritable properties take their CSS initial
    /// values (via <see cref="ApplyDefaults"/>); inheritable properties are
    /// copied from <paramref name="parentStyle"/> via
    /// <see cref="ApplyInheritance"/>. Per CSS 2.1 §17.5.1 anonymous boxes
    /// inherit only inheritable properties from their nearest non-anonymous
    /// ancestor.</summary>
    private static ComputedStyle CreateAnonBoxStyle(ComputedStyle parentStyle)
    {
        var style = ComputedStyle.Rent();
        ApplyDefaults(style);
        ApplyInheritance(style, parentStyle);
        return style;
    }

    // ============================================================
    // ComputedStyle construction helpers
    // ============================================================

    /// <summary>Read the computed <c>display</c> keyword. Falls back to the
    /// HTML UA-default per <see cref="HtmlDefaultDisplay"/> when the cascade
    /// didn't set it (no UA stylesheet shipped yet — cycle-2 work).</summary>
    private static string ReadDisplay(ComputedStyle style, IElement element)
    {
        if (style.IsDeferred(PropertyId.Display)
            && style.TryGetDeferred(PropertyId.Display, out var rawText)
            && rawText is not null)
        {
            return rawText;
        }
        if (style.IsSet(PropertyId.Display))
        {
            var slot = style.Get(PropertyId.Display);
            if (slot.Tag == ComputedSlotTag.Keyword)
            {
                var id = slot.AsKeyword();
                return DisplayKeywordName(id) ?? HtmlDefaultDisplay.GetDefault(element.LocalName);
            }
        }
        return HtmlDefaultDisplay.GetDefault(element.LocalName);
    }

    /// <summary>Read the computed <c>display</c> keyword for a pseudo-element
    /// — similar to <see cref="ReadDisplay"/> but falls back to <c>"inline"</c>
    /// (CSS Pseudo L4 §3.1 default for <c>::before</c> / <c>::after</c>) when
    /// the cascade didn't set it. Crucially does NOT consult the host's
    /// <see cref="HtmlDefaultDisplay"/> — a <c>div::before</c> defaults to
    /// inline, not block.</summary>
    private static string ReadPseudoDisplay(ComputedStyle style)
    {
        if (style.IsDeferred(PropertyId.Display)
            && style.TryGetDeferred(PropertyId.Display, out var rawText)
            && rawText is not null)
        {
            return rawText;
        }
        if (style.IsSet(PropertyId.Display))
        {
            var slot = style.Get(PropertyId.Display);
            if (slot.Tag == ComputedSlotTag.Keyword)
            {
                var id = slot.AsKeyword();
                var name = DisplayKeywordName(id);
                if (name is not null) return name;
            }
        }
        return "inline";
    }

    /// <summary>Reverse of <see cref="KeywordResolver"/>'s display table.
    /// Cycle-1 maintains this manually; cycle 2 will source-generate the
    /// reverse map alongside the keyword tables themselves.</summary>
    private static string? DisplayKeywordName(int id) => id switch
    {
        0 => "block", 1 => "inline", 2 => "inline-block", 3 => "list-item",
        4 => "flex", 5 => "inline-flex", 6 => "grid", 7 => "inline-grid",
        8 => "flow-root", 9 => "table", 10 => "inline-table",
        11 => "table-row-group", 12 => "table-header-group",
        13 => "table-footer-group", 14 => "table-row", 15 => "table-cell",
        16 => "table-column-group", 17 => "table-column", 18 => "table-caption",
        19 => "ruby", 20 => "ruby-base", 21 => "ruby-text",
        22 => "ruby-base-container", 23 => "ruby-text-container",
        24 => "contents", 25 => "none",
        _ => null,
    };

    /// <summary>Initialise <paramref name="style"/> with each property's
    /// registry default per <see cref="PropertyMetadata.Table"/>. Runs first
    /// so inheritance + explicit declarations overwrite later.</summary>
    /// <remarks>
    /// <b>Display is intentionally skipped.</b> The CSS spec default per
    /// properties.json is <c>inline</c>, but the proper default for an HTML
    /// element comes from the UA stylesheet (<c>div</c> → <c>block</c>,
    /// <c>tr</c> → <c>table-row</c>, etc. per HTML Living Standard "Rendering"
    /// §15.3). The UA stylesheet doesn't ship in cycle 1 — <see cref="ReadDisplay"/>
    /// consults <see cref="HtmlDefaultDisplay"/> as a stand-in when the cascade
    /// hasn't explicitly set <c>display</c>. Writing the registry's <c>inline</c>
    /// default here would mask that fallback and yield <see cref="BoxKind.InlineBox"/>
    /// for every unstyled HTML element.
    /// </remarks>
    private static void ApplyDefaults(ComputedStyle style)
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (meta.Id == PropertyId.Display) continue;
            // Task 14: list-style-type's UA default depends on the element
            // (`ol` → decimal, `ul`/`menu` → disc, others → disc) per HTML
            // "Rendering" §15.3.4. Skip the registry default (disc) and let
            // ReadListStyleType resolve via DOM-ancestor lookup so an <ol>
            // without an explicit cascade rule still numbers its items.
            if (meta.Id == PropertyId.ListStyleType) continue;
            var result = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue);
            result.MaterializeInto(style, meta.Id);
        }
    }

    /// <summary>Copy inherited properties from <paramref name="parentStyle"/>
    /// onto <paramref name="style"/>. Per CSS Cascade 4 §4.2 the cascade walks
    /// down through inheritance for properties whose
    /// <see cref="PropertyMeta.Inherits"/> is <see langword="true"/> and the
    /// property has no explicit declaration on the child.</summary>
    private static void ApplyInheritance(ComputedStyle style, ComputedStyle parentStyle)
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (!meta.Inherits) continue;
            if (parentStyle.IsSet(meta.Id))
            {
                var parentSlot = parentStyle.Get(meta.Id);
                if (!parentSlot.IsUnset)
                {
                    style.Set(meta.Id, parentSlot);
                }
                else if (parentStyle.TryGetDeferred(meta.Id, out var raw) && raw is not null)
                {
                    style.SetDeferred(meta.Id, raw);
                }
            }
        }
    }

    /// <summary>Iterate the resolved declarations + materialize each into
    /// <paramref name="style"/>. Each declaration goes through
    /// <see cref="PropertyResolverDispatch.Resolve"/> (with the
    /// declaration's source location threaded through
    /// per Task 12 hardening Rec 8 so any emitted diagnostic keeps its line/
    /// column origin) +
    /// <see cref="ResolverResult.MaterializeInto"/>.</summary>
    private static void ApplyResolvedDeclarations(
        ComputedStyle style,
        ResolvedRuleSet? ruleSet,
        ICssDiagnosticsSink? diagnostics)
    {
        if (ruleSet is null) return;
        foreach (var winner in ruleSet.Winners)
        {
            if (!PropertyMetadata.NameToId.TryGetValue(winner.Property, out var id))
                continue;
            var location = winner.OriginalDeclaration.Location;
            var result = PropertyResolverDispatch.Resolve(id, winner.ResolvedValue, diagnostics, location);
            result.MaterializeInto(style, id);
        }
    }
}
