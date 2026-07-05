// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

// This test IS a reflection tool — it walks each shipping assembly's public surface on purpose. The xUnit
// test host runs JIT (never trimmed / AOT-published), so the trim/AOT analyzers' "reflection may break under
// trimming" diagnostics (IL2026/IL2070/IL2072/IL2075) do not apply here. Suppress them for this file only.
#pragma warning disable IL2026, IL2070, IL2072, IL2075

namespace NetPdf.UnitTests.Api;

/// <summary>
/// Public-API surface LOCK for every COMPILE-VISIBLE assembly a consumer sees. The public API is frozen for
/// v1 (CLAUDE.md / build/version.json <c>publicApiFrozen</c>), so any addition, removal, or signature change
/// must be a deliberate, reviewed act. This test reflects each shipping assembly into a stable, sorted text
/// snapshot and compares it to a committed golden under <c>Api/PublicApi/&lt;Assembly&gt;.txt</c>.
///
/// <para><b>Scope (review [P1]).</b> The <c>NetPdf</c> NuGet BUNDLES every internal <c>NetPdf.*</c> DLL into
/// <c>lib/net10.0/</c>, and NuGet exposes all of them as COMPILE assets — so a public type in
/// <c>NetPdf.Layout</c> (etc.) is consumer-visible and can drift. The lock therefore covers the facade AND
/// its seven bundled internal assemblies AND the five language packs — every DLL NuGet presents at compile
/// time — not just the facade.</para>
///
/// <para><b>Missing golden fails (review [P2]).</b> A golden is (re)written ONLY under
/// <c>UPDATE_API_GOLDEN=1</c>. Absent that flag a missing golden FAILS (naming the path) — so a deleted
/// golden, a renamed assembly, or a wrong repo-root can't silently self-baseline into a false green. To
/// accept a reviewed change, run with <c>UPDATE_API_GOLDEN=1</c> and commit the updated golden.</para>
///
/// <para><b>Fidelity (review [P3]).</b> The renderer records nullable reference annotations, <c>in/ref/out</c>
/// + <c>params</c> parameter modifiers, optional/default values, generic type-parameter constraints, and each
/// type's base type + implemented interfaces — so source- or binary-relevant changes don't slip through.</para>
/// </summary>
public sealed class PublicApiSurfaceTests
{
    /// <summary>Every assembly NuGet exposes at compile time: the facade + its seven bundled internal DLLs
    /// (from <c>NetPdf.csproj</c>'s <c>lib/net10.0/</c> bundle) + the five language-pack packages. Loaded by
    /// name so discovery is deterministic (not dependent on what another test happened to load first).</summary>
    private static readonly string[] ShippingAssemblyNames =
    [
        "NetPdf",
        "NetPdf.Css", "NetPdf.Layout", "NetPdf.Paginate", "NetPdf.Paint", "NetPdf.Pdf", "NetPdf.Text", "NetPdf.Svg",
        "NetPdf.Languages.European", "NetPdf.Languages.Cjk", "NetPdf.Languages.Arabic",
        "NetPdf.Languages.Indic", "NetPdf.Languages.All",
    ];

    public static TheoryData<string> ShippingAssemblies()
    {
        var data = new TheoryData<string>();
        foreach (var name in ShippingAssemblyNames) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShippingAssemblies))]
    public void Public_api_surface_matches_the_committed_golden(string assemblyName)
    {
        // Load by name (fails loudly if the assembly is missing from the test output), so the set of
        // snapshotted assemblies is exactly ShippingAssemblyNames regardless of load order.
        var assembly = Assembly.Load(assemblyName);
        var actual = RenderPublicApi(assembly);

        var goldenDir = Path.Combine(RepoRoot(), "tests", "NetPdf.UnitTests", "Api", "PublicApi");
        var goldenPath = Path.Combine(goldenDir, assemblyName + ".txt");
        var updating = string.Equals(Environment.GetEnvironmentVariable("UPDATE_API_GOLDEN"), "1", StringComparison.Ordinal);

        if (updating)
        {
            Directory.CreateDirectory(goldenDir);
            File.WriteAllText(goldenPath, actual);
            return;
        }

        // A missing golden is a HARD failure — never a silent self-baseline (review [P2]).
        if (!File.Exists(goldenPath))
        {
            File.WriteAllText(Path.Combine(goldenDir, assemblyName + ".received.txt"), actual);
            Assert.Fail(
                $"No committed public-API golden for '{assemblyName}' at {goldenPath}. If this assembly is "
                + "newly shipping, run the suite with UPDATE_API_GOLDEN=1 and commit the golden; otherwise a "
                + "golden was deleted / the repo root is wrong — do NOT let CI self-baseline.");
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

    /// <summary>A deterministic, sorted textual rendering of an assembly's PUBLIC surface.</summary>
    private static string RenderPublicApi(Assembly assembly)
    {
        var nullCtx = new NullabilityInfoContext();
        var sb = new StringBuilder();
        var types = assembly.GetTypes()
            .Where(t => (t.IsPublic || t.IsNestedPublic) && !t.IsSpecialName)
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            sb.Append(TypeKind(type)).Append(' ').Append(TypeDeclaration(type));
            var inherits = BaseAndInterfaces(type);
            if (inherits.Length > 0) sb.Append(" : ").Append(inherits);
            sb.Append(Constraints(type.GetGenericArguments())).Append('\n');

            foreach (var line in MemberLines(type, nullCtx).OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.Append("    ").Append(line).Append('\n');
            }
        }

        return sb.ToString();
    }

    private static IEnumerable<string> MemberLines(Type type, NullabilityInfoContext ctx)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var c in type.GetConstructors(flags))
            yield return $".ctor({Params(c.GetParameters(), ctx)})";

        foreach (var m in type.GetMethods(flags))
        {
            if (m.IsSpecialName &&
                (m.Name.StartsWith("get_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("set_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("add_", StringComparison.Ordinal) ||
                 m.Name.StartsWith("remove_", StringComparison.Ordinal)))
            {
                continue;
            }

            var generics = m.IsGenericMethodDefinition ? $"<{string.Join(", ", m.GetGenericArguments().Select(a => a.Name))}>" : "";
            var ret = TypeName(m.ReturnParameter.ParameterType, ctx.Create(m.ReturnParameter));
            yield return $"method {ret} {m.Name}{generics}({Params(m.GetParameters(), ctx)}){Constraints(m.GetGenericArguments())}";
        }

        foreach (var p in type.GetProperties(flags))
        {
            var acc = (p.GetMethod?.IsPublic == true ? "get;" : "") + (p.SetMethod?.IsPublic == true ? "set;" : "");
            var idx = p.GetIndexParameters();
            var idxStr = idx.Length > 0 ? $"[{Params(idx, ctx)}]" : "";
            yield return $"property {TypeName(p.PropertyType, ctx.Create(p))} {p.Name}{idxStr} {{ {acc} }}";
        }

        foreach (var f in type.GetFields(flags))
        {
            var kind = f.IsLiteral ? "const " : f.IsInitOnly ? "readonly " : "";
            yield return $"field {kind}{TypeName(f.FieldType, ctx.Create(f))} {f.Name}";
        }

        foreach (var e in type.GetEvents(flags))
            yield return $"event {TypeName(e.EventHandlerType!, null)} {e.Name}";
    }

    private static string Params(ParameterInfo[] ps, NullabilityInfoContext ctx) =>
        string.Join(", ", ps.Select(p =>
        {
            var byRef = p.ParameterType.IsByRef;
            var mod = p.IsOut ? "out " : byRef ? (p.IsIn ? "in " : "ref ") : "";
            var pars = p.IsDefined(typeof(ParamArrayAttribute), false) ? "params " : "";
            var pt = byRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            var def = p.HasDefaultValue ? $" = {FormatDefault(p.RawDefaultValue)}" : "";
            return $"{mod}{pars}{TypeName(pt, ctx.Create(p))} {p.Name}{def}";
        }));

    private static string FormatDefault(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "?",
    };

    // A base type worth recording (skip the implicit Object / ValueType / Enum / Delegate roots) plus the
    // type's public interfaces, sorted — so a change to the inheritance/implements set is caught.
    private static string BaseAndInterfaces(Type type)
    {
        var parts = new List<string>();
        var baseType = type.BaseType;
        if (baseType is not null && baseType != typeof(object) && baseType != typeof(ValueType)
            && baseType != typeof(Enum) && baseType != typeof(MulticastDelegate))
        {
            parts.Add(TypeName(baseType, null));
        }

        parts.AddRange(type.GetInterfaces().Where(i => i.IsPublic || i.IsNestedPublic)
            .Select(i => TypeName(i, null)).OrderBy(s => s, StringComparer.Ordinal));
        return string.Join(", ", parts);
    }

    // Generic-parameter constraints in a compact, stable form: `where T : class, IFoo, new()`.
    private static string Constraints(Type[] genericArgs)
    {
        var sb = new StringBuilder();
        foreach (var g in genericArgs.Where(g => g.IsGenericParameter))
        {
            var cs = new List<string>();
            var attrs = g.GenericParameterAttributes;
            if (attrs.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint)) cs.Add("class");
            if (attrs.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) cs.Add("struct");
            cs.AddRange(g.GetGenericParameterConstraints()
                .Where(c => c != typeof(ValueType))
                .Select(c => TypeName(c, null)).OrderBy(s => s, StringComparer.Ordinal));
            if (attrs.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint)
                && !attrs.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint)) cs.Add("new()");
            if (cs.Count > 0) sb.Append(" where ").Append(g.Name).Append(" : ").Append(string.Join(", ", cs));
        }

        return sb.ToString();
    }

    private static string TypeDeclaration(Type t)
    {
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(a => a.Name))}>";
    }

    private static string TypeName(Type t, NullabilityInfo? nullability)
    {
        var suffix = nullability?.ReadState == NullabilityState.Nullable && !t.IsValueType ? "?" : "";
        return CoreTypeName(t) + suffix;
    }

    private static string CoreTypeName(Type t)
    {
        if (t.IsByRef) return CoreTypeName(t.GetElementType()!);
        if (t.IsGenericParameter) return t.Name;
        if (t.IsArray) return CoreTypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericType)
        {
            var name = t.GetGenericTypeDefinition().FullName ?? t.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(CoreTypeName))}>";
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
