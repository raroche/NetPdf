// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Text;
using NetPdf.Layout.Boxes;

namespace NetPdf.LayoutSnapshots.Serialization;

/// <summary>
/// Task 18 — deterministic text serialization of a <see cref="Box"/> tree
/// for snapshot-test golden comparison. Output is indent-based; one node
/// per line; same input → byte-equal output.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format.</b> Each line: <c>{indent}{Kind}{source-tag?}{pseudo-tag?}{text?}</c>.
/// <list type="bullet">
///   <item><c>indent</c> — two spaces per depth level.</item>
///   <item><c>Kind</c> — the <see cref="BoxKind"/> name.</item>
///   <item><c>source-tag</c> — when the box has a source element:
///     <c> @element=&lt;localname&gt;</c>; absent for anonymous boxes.</item>
///   <item><c>pseudo-tag</c> — when the box has a pseudo:
///     <c> ::before</c> / <c> ::after</c> / <c> ::marker</c>.</item>
///   <item><c>text</c> — for <see cref="BoxKind.TextRun"/>:
///     <c> "text"</c> with literal escaping per
///     <see cref="EscapeForSnapshot"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Phase 3 forward-compat.</b> When layout-time fields land
/// (positions, dimensions), append them as <c>@x=...</c> / <c>@y=...</c>
/// / <c>@width=...</c> / <c>@height=...</c> after the source/pseudo tags.
/// Existing snapshots stay valid until consumers regenerate them.
/// </para>
/// </remarks>
internal static class BoxTreeSerializer
{
    public static string Serialize(Box root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var sb = new StringBuilder();
        Append(sb, root, depth: 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Box box, int depth)
    {
        for (var i = 0; i < depth; i++) sb.Append("  ");
        sb.Append(box.Kind);

        if (box.SourceElement is not null)
        {
            sb.Append(" @element=");
            sb.Append(box.SourceElement.LocalName.ToLowerInvariant());
        }

        if (box.Pseudo != BoxPseudo.None)
        {
            sb.Append(' ');
            sb.Append(PseudoTag(box.Pseudo));
        }

        if (box.Kind == BoxKind.TextRun && box.Text.Length > 0)
        {
            sb.Append(" \"");
            sb.Append(EscapeForSnapshot(box.Text));
            sb.Append('"');
        }

        sb.Append('\n');

        foreach (var child in box.Children)
        {
            Append(sb, child, depth + 1);
        }
    }

    private static string PseudoTag(BoxPseudo pseudo) => pseudo switch
    {
        BoxPseudo.Before => "::before",
        BoxPseudo.After => "::after",
        BoxPseudo.Marker => "::marker",
        _ => "::" + pseudo.ToString().ToLowerInvariant(),
    };

    /// <summary>Escape control characters + the double-quote so the
    /// snapshot remains plain-text-greppable. Common HTML/CSS escapes use
    /// the backslash form; everything else is printed literally.</summary>
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
