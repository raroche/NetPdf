// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;

namespace NetPdf.Layout.Semantic;

/// <summary>
/// Walks an HTML document and emits a <see cref="SemanticNode"/> tree per the
/// Phase 2 doc — captures HTML semantic / accessibility roles (headings, list
/// items, table cells, links, figures, sectioning) for v1.1's PDF/UA tagged
/// structure-tree emission. Runs in parallel with <see cref="Boxes.BoxBuilder"/>
/// — the two trees describe the same source document at different
/// granularities (visual layout vs. accessibility).
/// </summary>
/// <remarks>
/// <para>
/// <b>Walk semantics.</b> Each child node is processed in document order:
/// element children map to <see cref="SemanticKind"/> via
/// <see cref="MapElement"/> + recurse; text-node children become
/// <see cref="SemanticKind.InlineText"/> spans (per Task 15 review Rec 3 +
/// Rec 7) so loose text under transparent containers + interleaved text
/// inside leaf-recognized parents survive without double-tagging via the
/// parent's aggregate text. Recognized elements produce a node + recurse;
/// unrecognized elements are <i>transparent</i> — their children flatten
/// into the parent's child list with no node of their own.
/// </para>
/// <para>
/// <b>Hidden-element exclusion</b> per Task 15 review Rec 1. Elements with
/// any of: ARIA <c>aria-hidden="true"</c>, the HTML5 <c>hidden</c> attribute,
/// the always-hidden HTML metadata-content tags (<c>head</c>, <c>title</c>,
/// <c>style</c>, <c>script</c>, <c>link</c>, <c>meta</c>, <c>template</c>,
/// <c>base</c>, <c>noscript</c>), or a cascade-resolved <c>display: none</c>
/// / <c>visibility: hidden</c> (when a <see cref="ResolvedCascadeResult"/>
/// is supplied) are skipped entirely — neither the element nor its
/// descendants contribute semantic nodes.
/// </para>
/// <para>
/// <b>Cycle-1 deferrals.</b> ARIA role overrides (<c>role="..."</c>),
/// generated content bridging from <see cref="Boxes.BoxBuilder"/>
/// (<c>::before</c>/<c>::after</c>/<c>::marker</c> text per Task 15 review
/// Rec 4 — needs box-tree input rather than DOM-only walk; deferred to
/// cycle 2 alongside the rendered-tree-driven rebuild), HTML5 computed
/// header associations (<c>headers</c> resolution against the layout grid).
/// </para>
/// <para>
/// <b>v1 policy on generated content (per Task 18 hardening 2 review Rec 3).</b>
/// Generated text from <c>::before</c> / <c>::after</c> / <c>::marker</c>
/// pseudos is intentionally <i>absent</i> from the semantic tree in v1.
/// The semantic tree is sourced from the static DOM; the generated content
/// is materialized by <see cref="Boxes.BoxBuilder"/> against the rendered
/// tree, not visible to a DOM-only walk. Two consequences flow from this:
/// <list type="bullet">
///   <item>Box snapshots show <c>TextRun</c> text for, e.g., a
///     <c>.label::before { content: "[" attr(data-tag) "] " }</c> rule
///     while the corresponding semantic snapshot does <i>not</i> include
///     that text — the gap is intentional, not a bug.</item>
///   <item>For PDF/UA tagged-PDF emission in v1.1+, the rendered-tree-
///     driven rebuild will route generated text into the parent semantic
///     node as either <c>InlineText</c> (when the generated content
///     conveys information per WCAG 1.1.1 — list markers, attr-substituted
///     labels) or as <c>/Artifact</c>-marked content (when purely
///     decorative — leaders, ornamental quotes). The decision boundary
///     keys off the <c>::marker</c> vs <c>::before</c>/<c>::after</c>
///     distinction (markers are always informational; before/after default
///     to artifact unless they substitute element data via <c>attr()</c>
///     / <c>counter()</c>).</item>
/// </list>
/// The <c>SnapshotTests.PseudoWithAttr_generated_text_is_absent_from_semantic_tree_until_phase_5_pdfua</c>
/// Fact pins the current state explicitly so a future change is caught
/// as an intentional crossing of this boundary, not snapshot drift.
/// </para>
/// </remarks>
internal static class SemanticTreeBuilder
{
    /// <summary>Build a semantic tree for <paramref name="document"/>.
    /// When <paramref name="cascade"/> is supplied, cascade-resolved
    /// <c>display: none</c> + <c>visibility: hidden</c> elements are
    /// skipped (Task 15 review Rec 1). When omitted, only DOM-level
    /// hiding (the <c>hidden</c> attribute, <c>aria-hidden="true"</c>, and
    /// the metadata-content allowlist) excludes elements.</summary>
    public static SemanticNode Build(IDocument document, ResolvedCascadeResult? cascade = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new SemanticNode(SemanticKind.Document);
        if (document.DocumentElement is null) return root;
        // The synthetic Document root is a container — strip whitespace at
        // its top level (between html / body / etc.).
        var ctx = new WalkContext(
            Cascade: cascade,
            PreserveWhitespace: false,
            DropWhitespaceText: true,
            CancellationToken: cancellationToken);
        foreach (var node in WalkNode(document.DocumentElement, ctx))
        {
            root.AppendChild(node);
        }
        return root;
    }

    /// <summary>Per-walk context — carries the optional cascade pointer, the
    /// whitespace-preservation flag (Task 15 review Rec 8) so descendants
    /// of <c>&lt;pre&gt;</c> / <c>&lt;code&gt;</c> render their text runs
    /// raw, the drop-whitespace flag so containers (sectioning, list,
    /// table) suppress pure-indentation text between their structural
    /// children while text-leaf parents (paragraph, listitem, cell)
    /// preserve word-boundary whitespace, and the cancellation token
    /// (Phase 2 deep review Rec 6) so a hostile document stops the walk
    /// promptly rather than running the full DOM pass before noticing
    /// the stage boundary in <c>Phase2Pipeline</c>.</summary>
    private readonly record struct WalkContext(
        ResolvedCascadeResult? Cascade,
        bool PreserveWhitespace,
        bool DropWhitespaceText,
        System.Threading.CancellationToken CancellationToken);

    /// <summary>Walk an arbitrary DOM node (element or text) + return the
    /// semantic node(s) it contributes.</summary>
    private static IReadOnlyList<SemanticNode> WalkNode(INode node, WalkContext ctx)
    {
        ctx.CancellationToken.ThrowIfCancellationRequested();
        switch (node)
        {
            case IElement element:
                return WalkElement(element, ctx);
            case IText text:
                return WalkText(text, ctx);
            default:
                return Array.Empty<SemanticNode>();
        }
    }

    /// <summary>Walk an element + return the semantic node(s) it contributes.
    /// Returns a single-element list for recognized elements, a flattened
    /// child list for transparent / unrecognized elements, and an empty list
    /// for hidden elements per <see cref="IsHidden"/>.</summary>
    private static IReadOnlyList<SemanticNode> WalkElement(IElement element, WalkContext ctx)
    {
        if (IsHidden(element, ctx.Cascade)) return Array.Empty<SemanticNode>();

        var childCtx = ctx;
        // Per Rec 8 — descendants of <pre> / <code> preserve whitespace.
        if (IsWhitespacePreservingElement(element))
        {
            childCtx = childCtx with { PreserveWhitespace = true };
        }

        var kind = MapElement(element);
        if (kind is null)
        {
            // Transparent — recurse + flatten children. Inherit the current
            // ctx so the surrounding context's drop-whitespace + preserve-
            // whitespace policy continues to apply.
            return CollectChildSemantics(element, childCtx);
        }

        // Container kinds drop whitespace-only text between their structural
        // children; text-leaf kinds keep boundary whitespace for word
        // separation (Rec 3 + Rec 7).
        childCtx = childCtx with { DropWhitespaceText = !IsTextLeaf(kind.Value) };

        var node = new SemanticNode(
            kind: kind.Value,
            sourceElement: element,
            href: kind == SemanticKind.Link ? element.GetAttribute("href") : null,
            altText: ResolveAltText(element, kind.Value, out var hasExplicitDecorativeAlt),
            hasExplicitDecorativeAlt: hasExplicitDecorativeAlt,
            cell: ExtractCellMetadata(element, kind.Value),
            text: null);

        foreach (var child in CollectChildSemantics(element, childCtx))
        {
            node.AppendChild(child);
        }

        return new[] { node };
    }

    /// <summary>Walk a text node — emits an <see cref="SemanticKind.InlineText"/>
    /// span. In whitespace-preserving contexts (per Rec 8) the text is
    /// taken raw; otherwise <see cref="NormalizeInlineText"/> collapses
    /// internal whitespace runs but preserves boundary whitespace so
    /// <c>"Hello "</c> + <c>&lt;span&gt;world&lt;/span&gt;</c> reads as
    /// <c>"Hello world"</c> when concatenated. Pure-whitespace text in a
    /// container (drop-whitespace) context emits no node.</summary>
    private static IReadOnlyList<SemanticNode> WalkText(IText text, WalkContext ctx)
    {
        var raw = text.Data ?? string.Empty;
        if (raw.Length == 0) return Array.Empty<SemanticNode>();

        if (ctx.PreserveWhitespace)
        {
            // Raw text — preserve every char. Rec 8.
            return new[]
            {
                new SemanticNode(SemanticKind.InlineText, sourceElement: null, text: raw),
            };
        }

        var normalized = NormalizeInlineText(raw);
        if (normalized.Length == 0) return Array.Empty<SemanticNode>();

        // Drop pure-whitespace runs in container contexts (Section, List,
        // Table, Document) — that's just source indentation. Inside text
        // leaves we keep boundary whitespace so word separation survives.
        if (ctx.DropWhitespaceText && IsAllWhitespace(normalized))
            return Array.Empty<SemanticNode>();

        return new[]
        {
            new SemanticNode(SemanticKind.InlineText, sourceElement: null, text: normalized),
        };
    }

    /// <summary>Collect the semantic-tree children of <paramref name="element"/>
    /// — recurses into each child node (text + element) per Rec 3.</summary>
    private static List<SemanticNode> CollectChildSemantics(IElement element, WalkContext ctx)
    {
        var result = new List<SemanticNode>();
        foreach (var child in element.ChildNodes)
        {
            foreach (var node in WalkNode(child, ctx))
            {
                result.Add(node);
            }
        }
        return result;
    }

    /// <summary>Map an HTML element to its <see cref="SemanticKind"/>, or
    /// <see langword="null"/> when the element is transparent (children
    /// flatten into the parent). Lookup is ASCII case-insensitive per
    /// HTML5.</summary>
    private static SemanticKind? MapElement(IElement element)
    {
        var lower = element.LocalName.ToLowerInvariant();
        switch (lower)
        {
            case "h1": return SemanticKind.Heading1;
            case "h2": return SemanticKind.Heading2;
            case "h3": return SemanticKind.Heading3;
            case "h4": return SemanticKind.Heading4;
            case "h5": return SemanticKind.Heading5;
            case "h6": return SemanticKind.Heading6;
            case "p": return SemanticKind.Paragraph;
            case "blockquote": return SemanticKind.BlockQuote;
            case "code":
            case "pre":
                return SemanticKind.Code;
            case "ul":
            case "ol":
            case "menu":
                return SemanticKind.List;
            case "li": return SemanticKind.ListItem;
            case "table": return SemanticKind.Table;
            case "tr": return SemanticKind.TableRow;
            case "th": return SemanticKind.TableHeaderCell;
            case "td": return SemanticKind.TableCell;
            case "caption": return SemanticKind.TableCaption;
            case "a":
                // Per Task 15 review Rec 2 + HTML5 §4.6.1: <a> with no `href`
                // does NOT represent a hyperlink — it's a placeholder. Treat
                // as transparent so its content flattens into the parent.
                return string.IsNullOrEmpty(element.GetAttribute("href"))
                    ? null
                    : SemanticKind.Link;
            case "img": return SemanticKind.Image;
            case "figure": return SemanticKind.Figure;
            case "figcaption": return SemanticKind.FigureCaption;
            case "header": return SemanticKind.Header;
            case "footer": return SemanticKind.Footer;
            case "nav": return SemanticKind.Nav;
            case "main": return SemanticKind.Main;
            case "aside": return SemanticKind.Aside;
            case "article": return SemanticKind.Article;
            case "section": return SemanticKind.Section;
            default: return null;
        }
    }

    /// <summary>Per Task 15 review Rec 1 — skip elements that won't render
    /// in the final document so they don't enter the tagged structure tree.</summary>
    private static bool IsHidden(IElement element, ResolvedCascadeResult? cascade)
    {
        if (IsAlwaysHiddenMetadataElement(element.LocalName)) return true;
        if (element.HasAttribute("hidden")) return true;
        var ariaHidden = element.GetAttribute("aria-hidden");
        if (string.Equals(ariaHidden, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (cascade is not null && IsCascadeHidden(element, cascade)) return true;
        return false;
    }

    private static bool IsAlwaysHiddenMetadataElement(string localName) => localName.ToLowerInvariant() switch
    {
        "head" or "title" or "style" or "script" or "link" or "meta"
            or "template" or "base" or "noscript" or "area" or "param"
            or "source" or "track" => true,
        _ => false,
    };

    private static bool IsCascadeHidden(IElement element, ResolvedCascadeResult cascade)
    {
        var rs = cascade.TryGetStylesFor(element);
        if (rs is null) return false;
        var displayDecl = rs.GetWinner("display");
        if (displayDecl is not null
            && displayDecl.ResolvedValue.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var visibilityDecl = rs.GetWinner("visibility");
        if (visibilityDecl is not null
            && visibilityDecl.ResolvedValue.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>Per Task 15 review Rec 8 — <c>&lt;pre&gt;</c> + <c>&lt;code&gt;</c>
    /// preserve their text content's whitespace verbatim.</summary>
    private static bool IsWhitespacePreservingElement(IElement element) =>
        element.LocalName.Equals("pre", StringComparison.OrdinalIgnoreCase)
        || element.LocalName.Equals("code", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per the Phase 2 doc accessibility-name resolution rule for
    /// figures + images: <c>alt</c> attribute → <c>aria-label</c> →
    /// child <c>&lt;figcaption&gt;</c>'s text content. Per Rec 6, distinguish
    /// missing alt (returns null) from explicit decorative alt="" (returns
    /// "" with <paramref name="hasExplicitDecorativeAlt"/> set).</summary>
    private static string? ResolveAltText(IElement element, SemanticKind kind, out bool hasExplicitDecorativeAlt)
    {
        hasExplicitDecorativeAlt = false;
        switch (kind)
        {
            case SemanticKind.Image:
            {
                if (element.HasAttribute("alt"))
                {
                    var altRaw = element.GetAttribute("alt") ?? string.Empty;
                    var altNormalized = NormalizeAccessibleName(altRaw);
                    if (altNormalized.Length == 0)
                    {
                        // alt="" → explicitly decorative per HTML5 §4.8.3.
                        hasExplicitDecorativeAlt = true;
                        return string.Empty;
                    }
                    return altNormalized;
                }
                if (element.HasAttribute("aria-label"))
                {
                    var ariaRaw = element.GetAttribute("aria-label") ?? string.Empty;
                    var ariaNormalized = NormalizeAccessibleName(ariaRaw);
                    return ariaNormalized.Length == 0 ? null : ariaNormalized;
                }
                return null;
            }

            case SemanticKind.Figure:
            {
                if (element.HasAttribute("aria-label"))
                {
                    var ariaRaw = element.GetAttribute("aria-label") ?? string.Empty;
                    var ariaNormalized = NormalizeAccessibleName(ariaRaw);
                    if (ariaNormalized.Length > 0) return ariaNormalized;
                }
                foreach (var child in element.Children)
                {
                    if (child.LocalName.Equals("figcaption", StringComparison.OrdinalIgnoreCase))
                    {
                        var caption = NormalizeAccessibleName(child.TextContent);
                        return caption.Length == 0 ? null : caption;
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>Per Task 15 review Rec 5 — extract <c>rowspan</c> /
    /// <c>colspan</c> / <c>scope</c> / <c>headers</c> / <c>abbr</c> from
    /// table-cell elements. Returns <see langword="null"/> for non-cell
    /// kinds.</summary>
    private static TableCellMetadata? ExtractCellMetadata(IElement element, SemanticKind kind)
    {
        if (kind != SemanticKind.TableCell && kind != SemanticKind.TableHeaderCell)
            return null;

        var rowSpan = ParseSpanAttribute(element.GetAttribute("rowspan"), 1);
        var colSpan = ParseSpanAttribute(element.GetAttribute("colspan"), 1);
        var scope = element.GetAttribute("scope");
        var headers = element.GetAttribute("headers");
        var abbr = element.GetAttribute("abbr");

        return new TableCellMetadata
        {
            RowSpan = rowSpan,
            ColSpan = colSpan,
            Scope = string.IsNullOrEmpty(scope) ? null : scope,
            Headers = string.IsNullOrEmpty(headers) ? null : headers,
            Abbr = string.IsNullOrEmpty(abbr) ? null : abbr,
        };
    }

    /// <summary>Parse an HTML5 span attribute (rowspan / colspan) per §4.9.10
    /// — non-negative integer; values &lt; 1 clamp to 1; non-integer values
    /// fall back to <paramref name="fallback"/>.</summary>
    private static int ParseSpanAttribute(string? attr, int fallback)
    {
        if (string.IsNullOrEmpty(attr)) return fallback;
        if (!int.TryParse(attr, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return fallback;
        }
        return value < 1 ? 1 : value;
    }

    /// <summary><see langword="true"/> when <paramref name="kind"/> typically
    /// carries inline / leaf text content (per ISO 32000-2 §14.8.4 these are
    /// the "block-level structure" types whose children are usually inline-
    /// level content). Container kinds (Document, List, Table, sectioning)
    /// drop whitespace-only text between structural children.</summary>
    private static bool IsTextLeaf(SemanticKind kind) => kind switch
    {
        SemanticKind.Heading1 or SemanticKind.Heading2 or SemanticKind.Heading3
            or SemanticKind.Heading4 or SemanticKind.Heading5 or SemanticKind.Heading6
            or SemanticKind.Paragraph or SemanticKind.BlockQuote or SemanticKind.Code
            or SemanticKind.ListItem
            or SemanticKind.TableHeaderCell or SemanticKind.TableCell
            or SemanticKind.TableCaption
            or SemanticKind.FigureCaption
            or SemanticKind.Link => true,
        _ => false,
    };

    /// <summary>Inline-text whitespace normalizer — collapses internal
    /// whitespace runs to single spaces but PRESERVES leading + trailing
    /// whitespace as a single space when it was present in the source.
    /// Used by <see cref="WalkText"/> so word boundaries between adjacent
    /// inline text spans aren't lost.</summary>
    private static string NormalizeInlineText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        var inWs = false;
        foreach (var c in raw)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (!inWs)
                {
                    sb.Append(' ');
                    inWs = true;
                }
            }
            else
            {
                sb.Append(c);
                inWs = false;
            }
        }
        return sb.ToString();
    }

    /// <summary>Accessibility-name normalizer — trims leading + trailing
    /// whitespace and collapses internal whitespace runs to single spaces.
    /// Per WAI-ARIA's accessible-name computation, the resulting string is
    /// a clean single-line label. Used for <c>alt</c>, <c>aria-label</c>,
    /// + <c>&lt;figcaption&gt;</c> resolution — NOT for inline text spans
    /// (those use <see cref="NormalizeInlineText"/> instead so word
    /// boundaries survive).</summary>
    private static string NormalizeAccessibleName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var sb = new System.Text.StringBuilder(raw.Length);
        var inWs = false;
        var hasContent = false;
        foreach (var c in raw)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (hasContent) inWs = true;
                continue;
            }
            if (inWs)
            {
                sb.Append(' ');
                inWs = false;
            }
            sb.Append(c);
            hasContent = true;
        }
        return sb.ToString();
    }

    private static bool IsAllWhitespace(string s)
    {
        foreach (var c in s)
        {
            if (c is not (' ' or '\t' or '\r' or '\n' or '\f')) return false;
        }
        return true;
    }
}
