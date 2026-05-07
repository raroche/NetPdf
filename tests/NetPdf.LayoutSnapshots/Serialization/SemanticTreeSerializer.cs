// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Layout.Semantic;

namespace NetPdf.LayoutSnapshots.Serialization;

/// <summary>
/// Task 18 — deterministic text serialization of a <see cref="SemanticNode"/>
/// tree for snapshot-test golden comparison. Mirror of
/// <see cref="BoxTreeSerializer"/> but for the accessibility / PDF-UA tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> Each line:
/// <c>{indent}{Kind}{source-tag?}{href-tag?}{alt-tag?}{cell-tag?}{text?}</c>.
/// <list type="bullet">
///   <item><c>indent</c> — two spaces per depth level.</item>
///   <item><c>Kind</c> — the <see cref="SemanticKind"/> name.</item>
///   <item><c>source-tag</c> — when the node has a source element:
///     <c> @element=&lt;localname&gt;</c>.</item>
///   <item><c>href-tag</c> — for <see cref="SemanticKind.Link"/>:
///     <c> @href="..."</c>.</item>
///   <item><c>alt-tag</c> — for <see cref="SemanticKind.Image"/> /
///     <see cref="SemanticKind.Figure"/>: <c> @alt="..."</c> when AltText
///     is non-null; <c> @alt=&lt;decorative&gt;</c> when
///     <see cref="SemanticNode.HasExplicitDecorativeAlt"/> is true;
///     <c> @alt=&lt;missing&gt;</c> when AltText is null without explicit
///     decorative flag.</item>
///   <item><c>cell-tag</c> — for table cells: <c> @rowspan=N</c> /
///     <c> @colspan=N</c> / <c> @scope=...</c> / <c> @headers="..."</c> /
///     <c> @abbr="..."</c> when set (defaults omitted).</item>
///   <item><c>text</c> — for <see cref="SemanticKind.InlineText"/>:
///     <c> "text"</c> with literal escaping.</item>
/// </list>
/// </para>
/// </remarks>
internal static class SemanticTreeSerializer
{
    public static string Serialize(SemanticNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sb = new StringBuilder();
        Append(sb, root, depth: 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, SemanticNode node, int depth)
    {
        for (var i = 0; i < depth; i++) sb.Append("  ");
        sb.Append(node.Kind);

        if (node.SourceElement is not null)
        {
            sb.Append(" @element=");
            sb.Append(node.SourceElement.LocalName.ToLowerInvariant());
        }

        if (node.Href is not null)
        {
            sb.Append(" @href=\"");
            sb.Append(EscapeForSnapshot(node.Href));
            sb.Append('"');
        }

        AppendAltTag(sb, node);
        AppendCellTag(sb, node);

        if (node.Kind == SemanticKind.InlineText && node.Text.Length > 0)
        {
            sb.Append(" \"");
            sb.Append(EscapeForSnapshot(node.Text));
            sb.Append('"');
        }

        sb.Append('\n');

        foreach (var child in node.Children)
        {
            Append(sb, child, depth + 1);
        }
    }

    private static void AppendAltTag(StringBuilder sb, SemanticNode node)
    {
        // Only Image + Figure carry alt-text; skip others.
        if (node.Kind != SemanticKind.Image && node.Kind != SemanticKind.Figure)
            return;

        if (node.AltText is null)
        {
            sb.Append(" @alt=<missing>");
            return;
        }
        if (node.HasExplicitDecorativeAlt)
        {
            sb.Append(" @alt=<decorative>");
            return;
        }
        sb.Append(" @alt=\"");
        sb.Append(EscapeForSnapshot(node.AltText));
        sb.Append('"');
    }

    private static void AppendCellTag(StringBuilder sb, SemanticNode node)
    {
        if (node.Cell is null) return;
        var cell = node.Cell.Value;
        if (cell.RowSpan != 1) sb.Append(" @rowspan=").Append(cell.RowSpan);
        if (cell.ColSpan != 1) sb.Append(" @colspan=").Append(cell.ColSpan);
        if (cell.Scope is not null) sb.Append(" @scope=").Append(cell.Scope);
        if (cell.Headers is not null)
        {
            sb.Append(" @headers=\"");
            sb.Append(EscapeForSnapshot(cell.Headers));
            sb.Append('"');
        }
        if (cell.Abbr is not null)
        {
            sb.Append(" @abbr=\"");
            sb.Append(EscapeForSnapshot(cell.Abbr));
            sb.Append('"');
        }
    }

    private static string EscapeForSnapshot(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
