// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NetPdf;
using NetPdf.Languages.All;
using NetPdf.Languages.Arabic;
using NetPdf.Languages.Cjk;
using NetPdf.Languages.European;
using NetPdf.Languages.Indic;
using Xunit;

// This test IS a reflection tool — it walks each shipping assembly's public surface on purpose. The xUnit
// test host runs JIT (never trimmed / AOT-published), so the trim/AOT analyzers' "reflection may break under
// trimming" diagnostics (IL2026/IL2070/IL2072/IL2075) do not apply here. Suppress them for this file only.
#pragma warning disable IL2026, IL2070, IL2072, IL2075

namespace NetPdf.UnitTests.Api;

/// <summary>
/// Public-API surface LOCK for the six shipping packages (the facade + the five language packs — the only
/// assemblies a consumer references). The public API is frozen for v1 (CLAUDE.md / build/version.json
/// <c>publicApiFrozen</c>), so any accidental addition, removal, or signature change to the public surface
/// must be a deliberate, reviewed act. This test reflects each shipping assembly into a stable, sorted text
/// snapshot and compares it to a committed golden under <c>Api/PublicApi/&lt;Assembly&gt;.txt</c>.
///
/// <para>Complements <c>EnablePackageValidation</c> (task 20), whose breaking-change baseline can't exist
/// until 1.0.0 is on nuget.org — this guards the surface in-repo NOW. On a mismatch the test writes a
/// <c>&lt;Assembly&gt;.received.txt</c> next to the golden and fails with a diff. To re-baseline a
/// deliberate change, run with the <c>UPDATE_API_GOLDEN=1</c> environment variable set (or copy the
/// received file over the golden) and commit the updated golden in the same PR.</para>
/// </summary>
public sealed class PublicApiSurfaceTests
{
    // One representative public type per shipping assembly, to resolve the assembly (and force-load the pack).
    public static TheoryData<string> ShippingAssemblies() => new()
    {
        typeof(HtmlPdf).Assembly.GetName().Name!,
        typeof(EuropeanHyphenation).Assembly.GetName().Name!,
        typeof(CjkHyphenation).Assembly.GetName().Name!,
        typeof(ArabicHyphenation).Assembly.GetName().Name!,
        typeof(IndicHyphenation).Assembly.GetName().Name!,
        typeof(AllLanguages).Assembly.GetName().Name!,
    };

    [Theory]
    [MemberData(nameof(ShippingAssemblies))]
    public void Public_api_surface_matches_the_committed_golden(string assemblyName)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .First(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.Ordinal));
        var actual = RenderPublicApi(assembly);

        var goldenDir = Path.Combine(RepoRoot(), "tests", "NetPdf.UnitTests", "Api", "PublicApi");
        Directory.CreateDirectory(goldenDir);
        var goldenPath = Path.Combine(goldenDir, assemblyName + ".txt");

        // Re-baseline mode: write the golden and pass. Used to bootstrap + to accept a reviewed change.
        if (!File.Exists(goldenPath) ||
            string.Equals(Environment.GetEnvironmentVariable("UPDATE_API_GOLDEN"), "1", StringComparison.Ordinal))
        {
            File.WriteAllText(goldenPath, actual);
            return;
        }

        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(goldenDir, assemblyName + ".received.txt"), actual);
            Assert.Fail(
                $"Public API of '{assemblyName}' changed vs the committed golden ({goldenPath}). The v1 public "
                + "surface is FROZEN — if this change is intended, re-run with UPDATE_API_GOLDEN=1 (or copy the "
                + $".received.txt over the golden) and commit it in the same PR.\n\n{DiffSummary(expected, actual)}");
        }
    }

    /// <summary>A deterministic, sorted textual rendering of an assembly's PUBLIC surface: every public /
    /// nested-public type, then its public DECLARED members (skipping property/event accessor + backing
    /// noise). Stable across runs so a diff pinpoints the exact API delta.</summary>
    private static string RenderPublicApi(Assembly assembly)
    {
        var sb = new StringBuilder();
        var types = assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsSpecialName)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            sb.Append(TypeKind(type)).Append(' ').Append(type.FullName).Append('\n');
            foreach (var line in MemberLines(type).OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> MemberLines(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var c in type.GetConstructors(flags))
            yield return $".ctor({Params(c.GetParameters())})";

        foreach (var m in type.GetMethods(flags))
        {
            // Skip property/event accessors (surfaced as the property/event itself); keep operators (op_*).
            if (m.IsSpecialName &&
                (m.Name.StartsWith("get_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("set_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("add_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("remove_", StringComparison.Ordinal)))
            {
                continue;
            }

            yield return $"method {TypeName(m.ReturnType)} {m.Name}({Params(m.GetParameters())})";
        }

        foreach (var p in type.GetProperties(flags))
        {
            var acc = (p.GetMethod?.IsPublic == true ? "get;" : "") + (p.SetMethod?.IsPublic == true ? "set;" : "");
            yield return $"property {TypeName(p.PropertyType)} {p.Name} {{ {acc} }}";
        }

        foreach (var f in type.GetFields(flags))
            yield return $"field {(f.IsLiteral ? "const " : "")}{TypeName(f.FieldType)} {f.Name}";

        foreach (var e in type.GetEvents(flags))
            yield return $"event {TypeName(e.EventHandlerType!)} {e.Name}";
    }

    private static string Params(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => $"{TypeName(p.ParameterType)} {p.Name}"));

    private static string TypeName(Type t)
    {
        if (t.IsGenericType)
        {
            var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(TypeName))}>";
        }

        return t.FullName ?? t.Name;
    }

    private static string TypeKind(Type t) =>
        t.IsEnum ? "enum"
        : t.IsInterface ? "interface"
        : t.IsValueType ? "struct"
        : typeof(Delegate).IsAssignableFrom(t) ? "delegate"
        : t.IsAbstract && t.IsSealed ? "static class"
        : "class";

    private static string DiffSummary(string expected, string actual)
    {
        var e = expected.Split('\n').ToHashSet(StringComparer.Ordinal);
        var a = actual.Split('\n').ToHashSet(StringComparer.Ordinal);
        var removed = e.Where(l => !a.Contains(l) && l.Length > 0).Take(20).Select(l => "  - " + l);
        var added = a.Where(l => !e.Contains(l) && l.Length > 0).Take(20).Select(l => "  + " + l);
        return "Removed from public API:\n" + string.Join("\n", removed)
            + "\n\nAdded to public API:\n" + string.Join("\n", added);
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
