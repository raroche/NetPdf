// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NetPdf.SourceGen;

/// <summary>
/// Reads <c>properties.json</c> (the CSS property registry) at compile time and emits the
/// typed-API surface the cascade resolver consumes: a <c>PropertyId</c> enum, a
/// <c>PropertyMetadata.Table</c> indexed by id, and a <c>PropertyMetadata.NameToId</c>
/// <see cref="System.Collections.Frozen.FrozenDictionary{TKey, TValue}"/>. To add a new CSS
/// property, append an entry to <c>properties.json</c> and rebuild — no hand-edited
/// boilerplate.
/// </summary>
/// <remarks>
/// <para>
/// The generator is registered as an <c>IIncrementalGenerator</c>. It picks up
/// <c>properties.json</c> from the consuming project's <c>&lt;AdditionalFiles&gt;</c> list
/// and produces a single source file (<c>PropertyTables.g.cs</c>) into the consumer's
/// compilation. The supporting enum + record types (<c>PropertyType</c>, <c>AppliesTo</c>,
/// <c>ComputedValueKind</c>, <c>PropertyMeta</c>) are hand-written in
/// <c>src/NetPdf.Css/Properties/</c> — we don't generate them so they stay reviewable.
/// </para>
/// <para>
/// JSON parsing uses a minimal hand-written tokenizer rather than <c>System.Text.Json</c>
/// because Roslyn source generators run on netstandard2.0 where <c>System.Text.Json</c>
/// requires bundling into the analyzer output, which adds load-path complexity. Our schema
/// is small enough that the dependency-free path is simpler.
/// </para>
/// <para>
/// Malformed JSON, missing fields, or duplicate ids raise diagnostics
/// (<c>NPDFGEN0001</c>–<c>NPDFGEN0004</c>) so build breaks rather than silently emitting
/// wrong code.
/// </para>
/// </remarks>
[Generator]
public sealed class CssPropertyGenerator : IIncrementalGenerator
{
    private const string ExpectedFileName = "properties.json";
    private const string EmittedFileName = "PropertyTables.g.cs";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var jsonFiles = context.AdditionalTextsProvider
            .Where(static file => Path.GetFileName(file.Path)
                .Equals(ExpectedFileName, StringComparison.OrdinalIgnoreCase))
            .Select(static (file, ct) =>
                new JsonInput(file.Path, file.GetText(ct)?.ToString() ?? string.Empty))
            .Collect();

        context.RegisterSourceOutput(jsonFiles, static (sourceContext, inputs) =>
        {
            if (inputs.IsDefaultOrEmpty) return;

            var input = inputs[0];
            if (!TryParse(input, sourceContext, out var properties)) return;
            if (!ValidateProperties(properties, sourceContext)) return;

            var source = EmitSource(properties);
            sourceContext.AddSource(EmittedFileName, SourceText.From(source, Encoding.UTF8));
        });
    }

    private struct JsonInput
    {
        public string Path;
        public string Text;
        public JsonInput(string path, string text) { Path = path; Text = text; }
    }

    private sealed class PropertyEntry
    {
        // Per-field "supplied" flag tracks JSON key presence so the validator can emit a
        // precise diagnostic for each missing required field instead of silently falling
        // back to default values.
        public string Name = string.Empty;
        public bool NameSupplied;
        public string Id = string.Empty;
        public bool IdSupplied;
        public string Type = string.Empty;
        public bool TypeSupplied;
        public string Default = string.Empty;
        public bool DefaultSupplied;
        public bool Inherits;
        public bool InheritSupplied;
        public string AppliesTo = string.Empty;
        public bool AppliesToSupplied;
        public string Computed = string.Empty;
        public bool ComputedSupplied;
    }

    private static bool TryParse(
        JsonInput input,
        SourceProductionContext context,
        out ImmutableArray<PropertyEntry> properties)
    {
        properties = ImmutableArray<PropertyEntry>.Empty;
        if (string.IsNullOrEmpty(input.Text))
        {
            Report(context, "NPDFGEN0001", "Empty properties.json",
                "properties.json at '" + input.Path + "' is empty.");
            return false;
        }

        try
        {
            var list = JsonParser.ParseProperties(input.Text);
            properties = list.ToImmutableArray();
            return true;
        }
        catch (FormatException ex)
        {
            Report(context, "NPDFGEN0003", "Malformed properties.json",
                "properties.json at '" + input.Path + "' is not valid JSON: " + ex.Message);
            return false;
        }
    }

    private static bool ValidateProperties(
        ImmutableArray<PropertyEntry> properties,
        SourceProductionContext context)
    {
        var ok = true;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < properties.Length; index++)
        {
            var p = properties[index];
            // Build a stable diagnostic tag pointing at the offending entry even when name
            // itself is missing — falls back to id, then to the source-order index.
            var tag = !string.IsNullOrEmpty(p.Name)
                ? "name='" + p.Name + "'"
                : !string.IsNullOrEmpty(p.Id) ? "id='" + p.Id + "'" : "[index " + index + "]";

            // NPDFGEN0005 — every field is required. The "Supplied" flag distinguishes
            // a missing key from a key with an empty value; both fail.
            if (!p.NameSupplied)      { Report(context, "NPDFGEN0005", "Missing 'name'",       "Property " + tag + " is missing the required 'name' field."); ok = false; }
            if (!p.IdSupplied)        { Report(context, "NPDFGEN0005", "Missing 'id'",         "Property " + tag + " is missing the required 'id' field."); ok = false; }
            if (!p.TypeSupplied)      { Report(context, "NPDFGEN0005", "Missing 'type'",       "Property " + tag + " is missing the required 'type' field."); ok = false; }
            if (!p.DefaultSupplied)   { Report(context, "NPDFGEN0005", "Missing 'default'",    "Property " + tag + " is missing the required 'default' field."); ok = false; }
            if (!p.InheritSupplied)   { Report(context, "NPDFGEN0005", "Missing 'inherit'",    "Property " + tag + " is missing the required 'inherit' field."); ok = false; }
            if (!p.AppliesToSupplied) { Report(context, "NPDFGEN0005", "Missing 'applies_to'", "Property " + tag + " is missing the required 'applies_to' field."); ok = false; }
            if (!p.ComputedSupplied)  { Report(context, "NPDFGEN0005", "Missing 'computed'",   "Property " + tag + " is missing the required 'computed' field."); ok = false; }

            if (p.NameSupplied && string.IsNullOrEmpty(p.Name))
            {
                Report(context, "NPDFGEN0005", "Empty 'name'", "Property " + tag + " has an empty 'name'.");
                ok = false;
            }
            if (p.IdSupplied && string.IsNullOrEmpty(p.Id))
            {
                Report(context, "NPDFGEN0005", "Empty 'id'", "Property " + tag + " has an empty 'id'.");
                ok = false;
            }
            if (p.IdSupplied && !string.IsNullOrEmpty(p.Id) && !IsValidCSharpIdentifier(p.Id))
            {
                Report(context, "NPDFGEN0005", "Invalid C# identifier in 'id'",
                    "Property " + tag + " has 'id'='" + p.Id + "' which is not a valid C# identifier.");
                ok = false;
            }

            if (!string.IsNullOrEmpty(p.Id) && !seenIds.Add(p.Id))
            {
                Report(context, "NPDFGEN0004", "Duplicate property id",
                    "Property id '" + p.Id + "' appears more than once in properties.json.");
                ok = false;
            }
            if (!string.IsNullOrEmpty(p.Name) && !seenNames.Add(p.Name))
            {
                Report(context, "NPDFGEN0004", "Duplicate property name",
                    "Property name '" + p.Name + "' appears more than once in properties.json (case-insensitive).");
                ok = false;
            }
        }
        return ok;
    }

    private static bool IsValidCSharpIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (!(char.IsLetter(value[0]) || value[0] == '_')) return false;
        for (var i = 1; i < value.Length; i++)
            if (!(char.IsLetterOrDigit(value[i]) || value[i] == '_')) return false;
        return true;
    }

    private static string EmitSource(ImmutableArray<PropertyEntry> properties)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("//   This file was generated by NetPdf.SourceGen/CssPropertyGenerator from properties.json.");
        sb.AppendLine("//   To add or modify a property: edit properties.json and rebuild — do not edit this file.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("// Copyright 2026 Roland Aroche and NetPdf contributors.");
        sb.AppendLine("// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.");
        sb.AppendLine();
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Collections.Frozen;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Collections.Immutable;");
        sb.AppendLine();
        sb.AppendLine("namespace NetPdf.Css.Properties;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Stable identifier for every CSS property the cascade knows about. Backed by");
        sb.AppendLine("/// <see cref=\"ushort\"/> so the value doubles as an index into");
        sb.AppendLine("/// <see cref=\"PropertyMetadata.Table\"/>. Generated from properties.json.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal enum PropertyId : ushort");
        sb.AppendLine("{");
        for (var i = 0; i < properties.Length; i++)
        {
            sb.Append("    ");
            sb.Append(properties[i].Id);
            sb.Append(" = ");
            sb.Append(i);
            sb.AppendLine(",");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Compile-time-emitted CSS property registry. Index into <see cref=\"Table\"/> by");
        sb.AppendLine("/// <see cref=\"PropertyId\"/>; resolve a property name to its id via <see cref=\"NameToId\"/>");
        sb.AppendLine("/// (case-insensitive O(1) lookup). Generated from properties.json.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("internal static class PropertyMetadata");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Number of properties in the registry.</summary>");
        sb.Append("    public const int Count = ");
        sb.Append(properties.Length);
        sb.AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Per-property metadata, indexed by <see cref=\"PropertyId\"/>. Returned as an");
        sb.AppendLine("    /// <see cref=\"ImmutableArray{T}\"/> so consumers cannot mutate entries (a plain");
        sb.AppendLine("    /// <c>readonly PropertyMeta[]</c> would still allow <c>Table[i] = ...</c>).");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public static ImmutableArray<PropertyMeta> Table => _table;");
        sb.AppendLine();
        sb.AppendLine("    private static readonly ImmutableArray<PropertyMeta> _table = ImmutableArray.Create<PropertyMeta>(");
        for (var i = 0; i < properties.Length; i++)
        {
            var p = properties[i];
            sb.Append("        new PropertyMeta(PropertyId.");
            sb.Append(p.Id);
            sb.Append(", ");
            sb.Append(EscapeStringLiteral(p.Name));
            sb.Append(", PropertyType.");
            sb.Append(string.IsNullOrEmpty(p.Type) ? "Unknown" : p.Type);
            sb.Append(", ");
            sb.Append(EscapeStringLiteral(p.Default ?? string.Empty));
            sb.Append(", ");
            sb.Append(p.Inherits ? "true" : "false");
            sb.Append(", AppliesTo.");
            sb.Append(string.IsNullOrEmpty(p.AppliesTo) ? "Unknown" : p.AppliesTo);
            sb.Append(", ComputedValueKind.");
            sb.Append(string.IsNullOrEmpty(p.Computed) ? "Specified" : p.Computed);
            sb.Append(")");
            sb.AppendLine(i == properties.Length - 1 ? "" : ",");
        }
        sb.AppendLine("    );");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>Case-insensitive name → id lookup. Built once at type init.</summary>");
        sb.AppendLine("    public static readonly FrozenDictionary<string, PropertyId> NameToId = BuildNameToId();");
        sb.AppendLine();
        sb.AppendLine("    private static FrozenDictionary<string, PropertyId> BuildNameToId()");
        sb.AppendLine("    {");
        sb.AppendLine("        var dict = new Dictionary<string, PropertyId>(Count, System.StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine("        for (var i = 0; i < Count; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            var meta = _table[i];");
        sb.AppendLine("            dict[meta.Name] = meta.Id;");
        sb.AppendLine("        }");
        sb.AppendLine("        return dict.ToFrozenDictionary(System.StringComparer.OrdinalIgnoreCase);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void Report(SourceProductionContext context, string id, string title, string message)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            new DiagnosticDescriptor(
                id, title, message, "CssPropertyGenerator",
                DiagnosticSeverity.Error, isEnabledByDefault: true),
            location: null));
    }

    /// <summary>
    /// Minimal JSON parser scoped to the <c>properties.json</c> schema:
    /// <c>{ "properties": [ { "name": "...", "id": "...", ... }, ... ] }</c>.
    /// Skips line comments (<c>// ...</c>) and block comments (<c>/* ... */</c>) as a
    /// concession for human-edited JSON; supports trailing commas; rejects malformed
    /// structure with a <see cref="FormatException"/>.
    /// </summary>
    private static class JsonParser
    {
        public static List<PropertyEntry> ParseProperties(string text)
        {
            var pos = 0;
            SkipWhitespaceAndComments(text, ref pos);
            ExpectChar(text, ref pos, '{');
            var entries = new List<PropertyEntry>();
            var sawProperties = false;

            while (true)
            {
                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == '}') { pos++; break; }

                var key = ReadString(text, ref pos);
                SkipWhitespaceAndComments(text, ref pos);
                ExpectChar(text, ref pos, ':');
                SkipWhitespaceAndComments(text, ref pos);

                if (key == "properties")
                {
                    sawProperties = true;
                    ReadPropertyArray(text, ref pos, entries);
                }
                else
                {
                    SkipValue(text, ref pos);
                }

                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == ',') { pos++; continue; }
                if (Peek(text, pos) == '}') { pos++; break; }
                throw new FormatException("Expected ',' or '}' at position " + pos);
            }

            if (!sawProperties)
                throw new FormatException("Missing top-level 'properties' array.");

            return entries;
        }

        private static void ReadPropertyArray(string text, ref int pos, List<PropertyEntry> entries)
        {
            ExpectChar(text, ref pos, '[');
            while (true)
            {
                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == ']') { pos++; return; }

                entries.Add(ReadEntry(text, ref pos));

                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == ',') { pos++; continue; }
                if (Peek(text, pos) == ']') { pos++; return; }
                throw new FormatException("Expected ',' or ']' at position " + pos);
            }
        }

        private static PropertyEntry ReadEntry(string text, ref int pos)
        {
            ExpectChar(text, ref pos, '{');
            var entry = new PropertyEntry();
            while (true)
            {
                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == '}') { pos++; return entry; }

                var key = ReadString(text, ref pos);
                SkipWhitespaceAndComments(text, ref pos);
                ExpectChar(text, ref pos, ':');
                SkipWhitespaceAndComments(text, ref pos);

                switch (key)
                {
                    case "name":       entry.Name      = ReadString(text, ref pos); entry.NameSupplied      = true; break;
                    case "id":         entry.Id        = ReadString(text, ref pos); entry.IdSupplied        = true; break;
                    case "type":       entry.Type      = ReadString(text, ref pos); entry.TypeSupplied      = true; break;
                    case "default":    entry.Default   = ReadString(text, ref pos); entry.DefaultSupplied   = true; break;
                    case "inherit":    entry.Inherits  = ReadBool(text, ref pos);   entry.InheritSupplied   = true; break;
                    case "applies_to": entry.AppliesTo = ReadString(text, ref pos); entry.AppliesToSupplied = true; break;
                    case "computed":   entry.Computed  = ReadString(text, ref pos); entry.ComputedSupplied  = true; break;
                    default:           SkipValue(text, ref pos); break;
                }

                SkipWhitespaceAndComments(text, ref pos);
                if (Peek(text, pos) == ',') { pos++; continue; }
                if (Peek(text, pos) == '}') { pos++; return entry; }
                throw new FormatException("Expected ',' or '}' at position " + pos);
            }
        }

        private static string ReadString(string text, ref int pos)
        {
            ExpectChar(text, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < text.Length)
            {
                var c = text[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && pos < text.Length)
                {
                    var e = text[pos++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        case '/': sb.Append('/'); break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                sb.Append(c);
            }
            throw new FormatException("Unterminated string starting at position " + pos);
        }

        private static bool ReadBool(string text, ref int pos)
        {
            if (text.Length - pos >= 4 && text.Substring(pos, 4) == "true") { pos += 4; return true; }
            if (text.Length - pos >= 5 && text.Substring(pos, 5) == "false") { pos += 5; return false; }
            throw new FormatException("Expected boolean at position " + pos);
        }

        private static void SkipValue(string text, ref int pos)
        {
            SkipWhitespaceAndComments(text, ref pos);
            var c = Peek(text, pos);
            if (c == '"') { ReadString(text, ref pos); return; }
            if (c == '{')
            {
                var depth = 0;
                while (pos < text.Length)
                {
                    var ch = text[pos++];
                    if (ch == '{') depth++;
                    else if (ch == '}' && --depth == 0) return;
                    else if (ch == '"') { pos--; ReadString(text, ref pos); }
                }
                throw new FormatException("Unterminated object");
            }
            if (c == '[')
            {
                var depth = 0;
                while (pos < text.Length)
                {
                    var ch = text[pos++];
                    if (ch == '[') depth++;
                    else if (ch == ']' && --depth == 0) return;
                    else if (ch == '"') { pos--; ReadString(text, ref pos); }
                }
                throw new FormatException("Unterminated array");
            }
            // primitive (number, true, false, null)
            while (pos < text.Length)
            {
                var ch = text[pos];
                if (ch == ',' || ch == '}' || ch == ']' || char.IsWhiteSpace(ch)) return;
                pos++;
            }
        }

        private static void SkipWhitespaceAndComments(string text, ref int pos)
        {
            while (pos < text.Length)
            {
                var c = text[pos];
                if (char.IsWhiteSpace(c)) { pos++; continue; }
                if (c == '/' && pos + 1 < text.Length)
                {
                    var next = text[pos + 1];
                    if (next == '/')
                    {
                        pos += 2;
                        while (pos < text.Length && text[pos] != '\n') pos++;
                        continue;
                    }
                    if (next == '*')
                    {
                        pos += 2;
                        while (pos + 1 < text.Length && !(text[pos] == '*' && text[pos + 1] == '/')) pos++;
                        if (pos + 1 < text.Length) pos += 2;
                        continue;
                    }
                }
                return;
            }
        }

        private static char Peek(string text, int pos) =>
            pos < text.Length ? text[pos] : '\0';

        private static void ExpectChar(string text, ref int pos, char expected)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length || text[pos] != expected)
                throw new FormatException("Expected '" + expected + "' at position " + pos +
                    (pos < text.Length ? " (got '" + text[pos] + "')" : " (end of input)"));
            pos++;
        }
    }
}
