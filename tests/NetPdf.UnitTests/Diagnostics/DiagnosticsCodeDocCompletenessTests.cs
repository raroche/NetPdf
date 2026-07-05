// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

// Intentional reflection over the internal *DiagnosticCodes tables to enumerate every stable code. The
// xUnit host runs JIT (never trimmed/AOT-published), so the trim/AOT "reflection may break" diagnostics
// don't apply here — suppress them for this file only.
#pragma warning disable IL2026, IL2070, IL2072, IL2075

namespace NetPdf.UnitTests.Diagnostics;

/// <summary>
/// DOC-COMPLETENESS gate for diagnostic codes. Cross-cutting rule 7 (CLAUDE.md): "unsupported features emit
/// a STABLE code from docs/diagnostics-codes.md — never drop content silently." This test enforces the
/// other half of that contract: every code the engine can emit must be DOCUMENTED. It reflects every
/// <c>const string</c> code out of the internal <c>*DiagnosticCodes</c> tables across the loaded
/// <c>NetPdf.*</c> assemblies and asserts each appears in <c>docs/diagnostics-codes.md</c> — so adding a new
/// code without a doc entry (skipping the <c>/add-diagnostic-code</c> workflow) fails CI.
/// </summary>
public sealed class DiagnosticsCodeDocCompletenessTests
{
    // A stable diagnostic code: SCREAMING-KEBAB with a 3-digit suffix, e.g. RES-LOAD-FAILED-001.
    private static readonly Regex CodePattern = new("^[A-Z][A-Z0-9]*(-[A-Z0-9]+)+-[0-9]{3}$", RegexOptions.Compiled);

    [Fact]
    public void Every_emitted_diagnostic_code_is_documented()
    {
        var defined = DefinedCodes();
        Assert.True(defined.Count >= 60, $"discovery found only {defined.Count} codes (expected ~63).");

        var docText = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "diagnostics-codes.md"));
        var documented = Regex.Matches(docText, "[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)+-[0-9]{3}")
            .Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        var missing = defined.Where(c => !documented.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            "These diagnostic codes are emitted by the engine but NOT documented in docs/diagnostics-codes.md "
            + "(run /add-diagnostic-code, or add a row):\n  " + string.Join("\n  ", missing));
    }

    /// <summary>Every <c>const string</c> whose value is a diagnostic code, across every
    /// <c>*DiagnosticCodes</c> table in the loaded <c>NetPdf.*</c> assemblies. Seeded with the three known
    /// tables so their assemblies are loaded; also self-discovers any future table.</summary>
    private static SortedSet<string> DefinedCodes()
    {
        // Touch one type per known table so its assembly is loaded before we scan the AppDomain.
        _ = new[]
        {
            typeof(NetPdf.DiagnosticCodes),
            typeof(NetPdf.Paginate.Diagnostics.PaginateDiagnosticCodes),
            typeof(NetPdf.Css.Diagnostics.CssDiagnosticCodes),
        };

        var codes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => a.GetName().Name?.StartsWith("NetPdf", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }

            foreach (var type in types.Where(t => t.Name.EndsWith("DiagnosticCodes", StringComparison.Ordinal)))
            {
                foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (f is { IsLiteral: true, FieldType: var ft } && ft == typeof(string) &&
                        f.GetRawConstantValue() is string value && CodePattern.IsMatch(value))
                    {
                        codes.Add(value);
                    }
                }
            }
        }

        return codes;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "NetPdf.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException("Could not locate the repo root (NetPdf.slnx).");
    }
}
