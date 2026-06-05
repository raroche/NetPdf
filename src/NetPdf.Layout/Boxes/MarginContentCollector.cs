// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using NetPdf.Css.Cascade;

namespace NetPdf.Layout.Boxes;

/// <summary>
/// Phase 3 Task 22 (<c>string-set</c> / <c>string()</c>) + Task 23 (<c>position: running()</c> /
/// <c>content: element()</c>) — collects, from the document, the named strings and running-element text
/// a page-margin box's <c>content</c> can pull via <c>string(name)</c> / <c>element(name)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Walks the element tree in DOCUMENT ORDER reading the cascade's raw declared values: an element with
/// <c>string-set: name &lt;content-list&gt;</c> sets the named string (later assignments win — the value
/// "as of the end of the page", the common running-header case); an element with
/// <c>position: running(name)</c> registers its text for <c>element(name)</c>. The result is a
/// <see cref="CssContentList.MarginContentContext"/> threaded to the margin-box painter.
/// </para>
/// <para>
/// <b>First-cut scope (single page).</b> The CSS GCPM L3 cross-page "running" semantics (a named string
/// / running element persists onto later pages until re-set) need the multi-page driver and are
/// deferred. <c>string-set</c> resolves the common content-lists — a bare <c>content()</c> (the
/// element's text), <c>attr()</c>, and literal strings (via <see cref="CssContentList"/>); a mixed list
/// containing <c>content()</c> alongside other tokens is a documented follow-up. <c>element(name)</c>
/// pulls the running element's TEXT (its own block box / styling is deferred — deferrals.md).
/// </para>
/// </remarks>
internal static class MarginContentCollector
{
    /// <summary>Collect the named strings (<c>string-set</c>) + running-element text
    /// (<c>position: running()</c>) reachable from <paramref name="root"/>, reading raw declared values
    /// from <paramref name="cascade"/>. Returns an empty context when nothing is found.</summary>
    public static CssContentList.MarginContentContext Collect(IElement root, ResolvedCascadeResult cascade)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(cascade);
        Dictionary<string, string>? named = null;
        Dictionary<string, string>? running = null;
        Walk(root, cascade, ref named, ref running);
        return new CssContentList.MarginContentContext(named, running);
    }

    private static void Walk(
        IElement element, ResolvedCascadeResult cascade,
        ref Dictionary<string, string>? named, ref Dictionary<string, string>? running)
    {
        var rules = cascade.TryGetStylesFor(element);
        if (rules is not null)
        {
            // string-set: "<custom-ident> <content-list>" — last assignment in document order wins.
            var stringSet = rules.GetWinner("string-set")?.ResolvedValue;
            if (!string.IsNullOrWhiteSpace(stringSet)
                && TryResolveStringSet(stringSet, element, out var name, out var value))
            {
                (named ??= new(StringComparer.Ordinal))[name] = value;
            }

            // position: running(<name>) — register the element's text for content: element(name).
            var position = rules.GetWinner("position")?.ResolvedValue;
            if (position is not null && TryParseRunningName(position, out var runName))
            {
                (running ??= new(StringComparer.Ordinal))[runName] = element.TextContent ?? string.Empty;
            }
        }

        foreach (var child in element.Children) // IElement children, in document order.
            Walk(child, cascade, ref named, ref running);
    }

    /// <summary>Parse <c>string-set: &lt;custom-ident&gt; &lt;content-list&gt;</c>: split the leading
    /// ident (the name) from the content-list, then resolve the list. A bare <c>content()</c> →
    /// <paramref name="element"/>'s text; otherwise the list is resolved via <see cref="CssContentList"/>
    /// (literal strings + <c>attr()</c>). Returns <see langword="false"/> for <c>none</c>, a missing
    /// content-list, or an unresolvable one.</summary>
    private static bool TryResolveStringSet(string raw, IElement element, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            return false;

        // Leading custom-ident (the string name).
        var i = 0;
        while (i < trimmed.Length && IsIdentChar(trimmed[i])) i++;
        if (i == 0) return false;                 // no name
        name = trimmed[..i];

        var rest = trimmed[i..].Trim();
        if (rest.Length == 0) return false;       // no content-list

        // The common `string-set: name content()` form → the element's own text.
        if (rest.Equals("content()", StringComparison.OrdinalIgnoreCase)
            || rest.Equals("content(text)", StringComparison.OrdinalIgnoreCase))
        {
            value = element.TextContent ?? string.Empty;
            return true;
        }

        // Otherwise resolve a literal-string / attr() content-list against the element.
        return CssContentList.TryParse(rest, element, out value);
    }

    /// <summary><see langword="true"/> when <paramref name="rawPosition"/> is a
    /// <c>running(&lt;custom-ident&gt;)</c> value (CSS GCPM L3) — the element is removed from normal flow
    /// and its content pulled into a page-margin box by <c>content: element(name)</c>.
    /// <see cref="BoxBuilder"/> uses this to SKIP the element from the body box tree (single source of
    /// truth with the running-text collection here).</summary>
    internal static bool IsRunning(string? rawPosition) =>
        rawPosition is not null && TryParseRunningName(rawPosition, out _);

    /// <summary>Parse a <c>position</c> value of the form <c>running(&lt;custom-ident&gt;)</c>, returning
    /// the running-string name. Any other position value (static/relative/absolute/…) → false.</summary>
    private static bool TryParseRunningName(string raw, out string name)
    {
        name = string.Empty;
        var trimmed = raw.Trim();
        const string prefix = "running(";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(")", StringComparison.Ordinal))
            return false;
        var inner = trimmed[prefix.Length..^1].Trim();
        if (inner.Length == 0) return false;
        foreach (var c in inner)
            if (!IsIdentChar(c)) return false;
        name = inner;
        return true;
    }

    private static bool IsIdentChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
}
