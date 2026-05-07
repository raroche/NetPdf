// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;

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
/// <b>Walk semantics.</b> Each child element is mapped to a
/// <see cref="SemanticKind"/> via <see cref="MapElement"/>. Recognized elements
/// produce a <see cref="SemanticNode"/> + recurse into their children;
/// unrecognized elements (<c>div</c>, <c>span</c>, <c>thead</c>,
/// <c>tbody</c>, <c>tfoot</c>, etc.) are <i>transparent</i> — their children
/// are flattened into the parent's child list with no node of their own.
/// This matches PDF/UA's approach of skipping purely-presentational
/// containers in the structure tree.
/// </para>
/// <para>
/// <b>Cycle-1 scope.</b> Builds the tree from the static post-parse DOM —
/// no consultation of the cascade or computed display values. ARIA roles
/// (<c>role="heading"</c>, <c>role="list"</c>, etc.) are out-of-scope for
/// cycle 1 and land in cycle 2 alongside the cycle-2 ARIA-aware role mapper.
/// </para>
/// </remarks>
internal static class SemanticTreeBuilder
{
    /// <summary>Build a semantic tree for <paramref name="document"/>.
    /// Returns a synthetic <see cref="SemanticKind.Document"/> root with the
    /// document element's recognized descendants attached as children.</summary>
    public static SemanticNode Build(IDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = new SemanticNode(SemanticKind.Document);
        if (document.DocumentElement is null) return root;
        foreach (var node in WalkElement(document.DocumentElement))
        {
            root.AppendChild(node);
        }
        return root;
    }

    /// <summary>Walk an element + return the semantic node(s) it contributes.
    /// Returns a single-element list for recognized elements, a flattened
    /// child list for transparent / unrecognized elements (so the caller can
    /// AddRange them into its own collector).</summary>
    private static IReadOnlyList<SemanticNode> WalkElement(IElement element)
    {
        var kind = MapElement(element);
        if (kind is null)
        {
            // Transparent element — recurse + flatten children into the
            // caller's list.
            return CollectChildSemantics(element);
        }

        var node = new SemanticNode(
            kind: kind.Value,
            sourceElement: element,
            href: kind == SemanticKind.Link ? element.GetAttribute("href") : null,
            altText: ResolveAltText(element, kind.Value),
            text: IsTextLeaf(kind.Value)
                ? NormalizeText(element.TextContent)
                : null);

        // Recurse into children regardless of leaf-ness so nested links /
        // images / nested tables surface even within paragraph-leaf parents.
        foreach (var child in CollectChildSemantics(element))
        {
            node.AppendChild(child);
        }

        return new[] { node };
    }

    /// <summary>Collect the semantic-tree children of <paramref name="element"/>
    /// — recurse into each child element, flattening transparent
    /// elements' contributions into the result list.</summary>
    private static List<SemanticNode> CollectChildSemantics(IElement element)
    {
        var result = new List<SemanticNode>();
        foreach (var child in element.Children)
        {
            foreach (var node in WalkElement(child))
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
    private static SemanticKind? MapElement(IElement element) =>
        element.LocalName.ToLowerInvariant() switch
        {
            "h1" => SemanticKind.Heading1,
            "h2" => SemanticKind.Heading2,
            "h3" => SemanticKind.Heading3,
            "h4" => SemanticKind.Heading4,
            "h5" => SemanticKind.Heading5,
            "h6" => SemanticKind.Heading6,
            "p" => SemanticKind.Paragraph,
            "blockquote" => SemanticKind.BlockQuote,
            "code" or "pre" => SemanticKind.Code,
            "ul" or "ol" or "menu" => SemanticKind.List,
            "li" => SemanticKind.ListItem,
            "table" => SemanticKind.Table,
            "tr" => SemanticKind.TableRow,
            "th" => SemanticKind.TableHeaderCell,
            "td" => SemanticKind.TableCell,
            "caption" => SemanticKind.TableCaption,
            "a" => SemanticKind.Link,
            "img" => SemanticKind.Image,
            "figure" => SemanticKind.Figure,
            "figcaption" => SemanticKind.FigureCaption,
            "header" => SemanticKind.Header,
            "footer" => SemanticKind.Footer,
            "nav" => SemanticKind.Nav,
            "main" => SemanticKind.Main,
            "aside" => SemanticKind.Aside,
            "article" => SemanticKind.Article,
            "section" => SemanticKind.Section,
            // Transparent — children flatten into the parent.
            _ => null,
        };

    /// <summary>Per the Phase 2 doc accessibility-name resolution rule for
    /// figures + images: <c>alt</c> attribute → <c>aria-label</c> →
    /// child <c>&lt;figcaption&gt;</c>'s text content. Returns
    /// <see langword="null"/> when no source supplies a name (and the
    /// kind isn't one that carries an accessible name).</summary>
    private static string? ResolveAltText(IElement element, SemanticKind kind)
    {
        switch (kind)
        {
            case SemanticKind.Image:
                // <img alt> is the accessibility name; <img aria-label>
                // is the secondary fallback.
                var imgAlt = element.GetAttribute("alt");
                if (!string.IsNullOrEmpty(imgAlt)) return imgAlt;
                var imgAria = element.GetAttribute("aria-label");
                return string.IsNullOrEmpty(imgAria) ? null : imgAria;

            case SemanticKind.Figure:
                // <figure aria-label> wins; fall back to the child <figcaption>
                // text content. (No `alt` attribute on <figure> per HTML5.)
                var figAria = element.GetAttribute("aria-label");
                if (!string.IsNullOrEmpty(figAria)) return figAria;
                foreach (var child in element.Children)
                {
                    if (child.LocalName.Equals("figcaption", StringComparison.OrdinalIgnoreCase))
                    {
                        var caption = NormalizeText(child.TextContent);
                        return string.IsNullOrEmpty(caption) ? null : caption;
                    }
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary><see langword="true"/> when <paramref name="kind"/> typically
    /// carries inline / leaf text content (per ISO 32000-2 §14.8.4 these are
    /// the "block-level structure" types whose children are usually inline-
    /// level content). Container kinds (Document, List, Table, sectioning)
    /// are NOT text leaves — their text is the union of their descendants'
    /// text and would be redundant with child nodes' <see cref="SemanticNode.Text"/>.</summary>
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

    /// <summary>Trim + collapse whitespace runs for a clean accessibility
    /// name. AngleSharp's <c>TextContent</c> preserves the source
    /// indentation; for accessibility we want a single-line, single-spaced
    /// string per WAI-ARIA's accessible-name computation.</summary>
    private static string NormalizeText(string? raw)
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
}
