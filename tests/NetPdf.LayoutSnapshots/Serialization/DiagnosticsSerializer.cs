// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetPdf;

namespace NetPdf.LayoutSnapshots.Serialization;

/// <summary>
/// Task 18 hardening Rec 3 — deterministic serializer for the
/// <see cref="NetPdf.Diagnostic"/>s emitted while running a fixture
/// through the Phase 2 pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why per-fixture diagnostic snapshots.</b> Without a sink, an
/// unsupported-feature regression is invisible — the box / semantic
/// trees stay shaped the same when, e.g., <c>::before</c> on a replaced
/// element silently stops firing <c>CSS-PSEUDO-SUPPRESSED-ON-REPLACED-001</c>.
/// Capturing the diagnostic shape per fixture turns that into a snapshot
/// mismatch the moment the emission set drifts.
/// </para>
/// <para>
/// <b>Public sink path.</b> Per Task 18 hardening 2 review Rec 1, the
/// serializer takes the <i>public</i> <see cref="NetPdf.Diagnostic"/>
/// shape so a single sink set on <see cref="HtmlPdfOptions.Diagnostics"/>
/// captures both <c>HTML-*</c> diagnostics from <c>HtmlParsingHost</c>
/// (<c>HTML-SCRIPT-IGNORED-001</c>, <c>HTML-JAVASCRIPT-URL-IGNORED-001</c>)
/// and CSS-stage diagnostics that flow through
/// <see cref="NetPdf.Diagnostics.PublicDiagnosticsSinkAdapter"/>. Cycle 1
/// only captured the internal <c>ICssDiagnosticsSink</c>, leaving HTML
/// regressions silent.
/// </para>
/// <para>
/// <b>Format.</b> One diagnostic per line, sorted by
/// <c>(Code, Severity, Location, Message)</c> for stable ordering across
/// runs. Each line is
/// <c>{Code}\t{Severity}\t{Location}\t{NormalizedMessage}</c>. Messages
/// are length-truncated at 120 chars + collapsed whitespace runs to a
/// single space so payloads (like the offending function name in a
/// <c>CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001</c> emission) stay
/// readable in diff output.
/// </para>
/// <para>
/// <b>Location format</b> (Task 18 hardening 2 review Rec 2). When
/// <see cref="SourceLocation.Unknown"/>, emit <c>&lt;unknown&gt;</c> —
/// today's typical state for CSS-stage diagnostics until Task 3 wires
/// real positions; explicit placeholder is more honest than a silent
/// <c>0:0</c> that flips meaning when Task 3 lands. When File is null
/// but Line/Column are set, emit <c>&lt;inline&gt;:Line:Column</c> —
/// the case for HTML diagnostics on documents parsed without a BaseUri
/// (every snapshot fixture). When File is set, emit
/// <c>{File}:Line:Column</c> verbatim. Authors who feed snapshots
/// fixtures with absolute file paths must remember snapshots aren't
/// path-portable across machines (best practice: BaseUri stays unset
/// for snapshot fixtures so the <c>&lt;inline&gt;</c> form keeps tests
/// CI-stable).
/// </para>
/// <para>
/// <b>Empty diagnostics file.</b> A fixture that legitimately emits
/// no diagnostics gets an empty file. The snapshot helper treats empty
/// content as a valid expected state — proves "this fixture runs clean"
/// rather than "we forgot to capture diagnostics".
/// </para>
/// </remarks>
internal static class DiagnosticsSerializer
{
    public static string Serialize(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return string.Empty;

        var sorted = diagnostics
            .OrderBy(d => d.Code, System.StringComparer.Ordinal)
            .ThenBy(d => d.Severity)
            .ThenBy(d => FormatLocation(d.Location), System.StringComparer.Ordinal)
            .ThenBy(d => d.Message ?? string.Empty, System.StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var d in sorted)
        {
            sb.Append(d.Code);
            sb.Append('\t');
            sb.Append(d.Severity);
            sb.Append('\t');
            sb.Append(FormatLocation(d.Location));
            sb.Append('\t');
            sb.Append(NormalizeMessage(d.Message));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private const int MessageMaxLength = 120;

    private static string FormatLocation(SourceLocation location)
    {
        if (location == SourceLocation.Unknown) return "<unknown>";
        var file = string.IsNullOrEmpty(location.File) ? "<inline>" : location.File;
        return $"{file}:{location.Line}:{location.Column}";
    }

    private static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        var sb = new StringBuilder(message.Length);
        var lastWasWhitespace = false;
        foreach (var ch in message)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t' || ch == ' ')
            {
                if (!lastWasWhitespace) sb.Append(' ');
                lastWasWhitespace = true;
            }
            else
            {
                sb.Append(ch);
                lastWasWhitespace = false;
            }
        }
        var collapsed = sb.ToString().Trim();
        return collapsed.Length <= MessageMaxLength
            ? collapsed
            : collapsed.Substring(0, MessageMaxLength) + "...";
    }
}
