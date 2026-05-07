// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetPdf.Css.Diagnostics;

namespace NetPdf.LayoutSnapshots.Serialization;

/// <summary>
/// Task 18 hardening Rec 3 — deterministic serializer for the
/// <see cref="CssDiagnostic"/>s emitted while running a fixture through
/// the Phase 2 pipeline.
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
/// <b>Format.</b> One diagnostic per line, sorted by <c>(Code, Severity, Message)</c>
/// for stable ordering across runs. Each line is
/// <c>{Code}\t{Severity}\t{NormalizedMessage}</c>. Messages are
/// length-truncated at 120 chars + collapsed whitespace runs to a single
/// space — long enough to differentiate between, e.g., the modern color
/// function name in a <c>CSS-MODERN-COLOR-FUNCTION-UNSUPPORTED-001</c>
/// payload, but short enough to keep the snapshot diffs readable. Source
/// location is intentionally omitted — <c>CssSourceLocation</c> is
/// frequently <c>Unknown</c> until Task 3 wires real positions, and
/// emitting <c>(line=0,col=0)</c> on every entry would be noise that
/// also flips to real values once Task 3 lands, churning every existing
/// snapshot.
/// </para>
/// <para>
/// <b>Empty diagnostics file.</b> A fixture that legitimately emits
/// no diagnostics gets a single trailing newline (the file exists but
/// is "empty"). The snapshot helper treats empty content as a valid
/// expected state — proves "this fixture runs clean" rather than "we
/// forgot to capture diagnostics".
/// </para>
/// </remarks>
internal static class DiagnosticsSerializer
{
    public static string Serialize(IReadOnlyList<CssDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return string.Empty;

        var sorted = diagnostics
            .OrderBy(d => d.Code, System.StringComparer.Ordinal)
            .ThenBy(d => d.Severity)
            .ThenBy(d => d.Message ?? string.Empty, System.StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var d in sorted)
        {
            sb.Append(d.Code);
            sb.Append('\t');
            sb.Append(d.Severity);
            sb.Append('\t');
            sb.Append(NormalizeMessage(d.Message));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private const int MessageMaxLength = 120;

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
