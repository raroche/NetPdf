// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace NetPdf.Css.Diagnostics;

/// <summary>
/// Per Phase A security hardening A-6 — single-place utility for sanitizing
/// untrusted text fragments that flow into <see cref="CssDiagnostic.Message"/>
/// values. Originally factored out of <c>CascadeResolver</c>'s selector-parse
/// emission (Phase 2 deep review C-2); the central form prevents drift across
/// the dozen+ emission sites scattered through the CSS pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threat model.</b> Diagnostic messages reach an <see cref="ICssDiagnosticsSink"/>
/// supplied by the host. Common sink implementations log to a terminal, write
/// to JSON-encoded structured logs, or post to an aggregator like Datadog. If
/// untrusted CSS / HTML / URL text reaches the sink VERBATIM, an attacker can:
/// (a) inject ANSI / VT100 escape sequences (C0 ESC = 0x1B + bracket-prefix)
/// that cause the terminal to reposition the cursor / clear the screen / dump
/// arbitrary bytes; (b) inject NUL / control chars that confuse log parsers;
/// (c) bloat log volume with multi-megabyte attacker-supplied text. The
/// sanitizer addresses all three.
/// </para>
/// <para>
/// <b>Sanitization rules.</b>
/// <list type="bullet">
///   <item>C0 control characters (U+0000..U+001F) and DEL (U+007F): replaced
///     with U+FFFD REPLACEMENT CHARACTER. The redaction is observable to a
///     reader — silently dropping would lose the signal that the input was
///     malformed.</item>
///   <item>C1 control characters (U+0080..U+009F): same treatment. C1 is the
///     extended-ASCII range some terminals interpret as additional escape
///     codes; redacting protects sinks that interpret either C0 or C1.</item>
///   <item>Length cap: input is truncated at <c>maxLength</c> chars + an
///     ellipsis marker (U+2026) is appended to signal truncation. The cap
///     prevents a 10 MiB attacker selector from bloating the message.</item>
/// </list>
/// </para>
/// <para>
/// <b>Path normalization (A-7).</b> <see cref="SanitizeFilePath"/> additionally
/// reduces a possibly absolute filesystem path to its basename so
/// host-supplied <c>BaseUri</c> values like <c>file:///C:/Users/Foo/secret/...</c>
/// don't leak filesystem topology when the path flows into a diagnostic
/// message via <c>CssSourceLocation.Source</c>. Caller-supplied
/// well-known sentinels (<c>about:blank</c>, <c>&lt;inline&gt;</c>) pass
/// through unchanged.
/// </para>
/// </remarks>
internal static class DiagnosticTextSanitizer
{
    /// <summary>Default cap for inline text fragments in diagnostic messages
    /// (selector text, raw value text, attribute names, exception reasons).</summary>
    public const int DefaultMaxLength = 120;

    public static string Sanitize(string? raw, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var capped = raw.Length > maxLength ? raw.AsSpan(0, maxLength) : raw.AsSpan();
        var sb = new StringBuilder(capped.Length);
        foreach (var ch in capped)
        {
            if (ch < 0x20 || ch == 0x7F || (ch >= 0x80 && ch <= 0x9F))
                sb.Append('�');
            else
                sb.Append(ch);
        }
        if (raw.Length > maxLength) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>Per Phase A A-7 — normalize <c>CssSourceLocation.Source</c>
    /// before it flows into a diagnostic message. Absolute filesystem paths
    /// (Unix-style starting with <c>/</c>; Windows-style with a drive letter
    /// like <c>C:\</c> or <c>file:///C:/</c>) are reduced to their final
    /// segment so host machine topology doesn't leak. Well-known sentinels
    /// (<c>about:blank</c>, <c>&lt;inline&gt;</c>, <c>&lt;unknown&gt;</c>) +
    /// HTTP(S) URLs pass through unchanged. Empty / null returns
    /// <c>&lt;unknown&gt;</c>.</summary>
    public static string SanitizeFilePath(string? source)
    {
        if (string.IsNullOrEmpty(source)) return "<unknown>";
        // Sentinels we deliberately preserve.
        if (source == "<inline>" || source == "<unknown>" || source == "about:blank") return source;
        // Network URLs are caller-controlled but not host-filesystem topology;
        // pass through with sanitization for control chars only.
        if (source.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase))
        {
            return Sanitize(source);
        }
        // file:/// or absolute filesystem path — extract basename only.
        var trimmed = source;
        if (trimmed.StartsWith("file:///", System.StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("file:///".Length);
        }
        else if (trimmed.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed.Substring("file://".Length);
        }
        var lastSep = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var basename = lastSep >= 0 && lastSep < trimmed.Length - 1
            ? trimmed.Substring(lastSep + 1)
            : trimmed;
        return Sanitize(string.IsNullOrEmpty(basename) ? "<unknown>" : basename);
    }
}
