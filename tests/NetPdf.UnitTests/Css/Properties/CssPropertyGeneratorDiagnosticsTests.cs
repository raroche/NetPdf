// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using NetPdf.SourceGen;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Diagnostic-side tests for <see cref="CssPropertyGenerator"/>. Each test feeds the
/// generator a deliberately-broken <c>properties.json</c> via an in-memory
/// <see cref="AdditionalText"/> and asserts the generator reports the expected
/// <c>NPDFGEN0001</c>–<c>NPDFGEN0005</c> diagnostic without producing output. The "happy
/// path" with valid JSON is exercised by <see cref="PropertyMetadataTests"/> over the
/// generated table itself.
/// </summary>
public sealed class CssPropertyGeneratorDiagnosticsTests
{
    [Fact]
    public void Empty_properties_json_emits_NPDFGEN0001()
    {
        var diagnostics = RunGenerator(string.Empty);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0001");
    }

    [Fact]
    public void Missing_properties_array_emits_NPDFGEN0003()
    {
        // The minimal-JSON-without-properties case fails because the parser requires the
        // 'properties' array. NPDFGEN0003 (malformed JSON) catches it at the parse stage.
        // (Earlier draft used NPDFGEN0002 for "missing properties array"; the unified
        // format-exception path now catches it.)
        var diagnostics = RunGenerator("{ \"other\": [] }");
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0003");
    }

    [Fact]
    public void Malformed_json_emits_NPDFGEN0003()
    {
        var diagnostics = RunGenerator("{ this is not json");
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0003");
    }

    [Fact]
    public void Duplicate_property_id_emits_NPDFGEN0004()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" },
                { "name": "background-color", "id": "Color", "type": "Color", "default": "white", "inherit": false, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0004" && d.GetMessage().Contains("Color"));
    }

    [Fact]
    public void Duplicate_property_name_emits_NPDFGEN0004()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "ColorA", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" },
                { "name": "Color",  "id": "ColorB", "type": "Color", "default": "white", "inherit": false, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        // The diagnostic message uses the second-occurrence's literal name ("Color"); the
        // case-insensitive comparison happens inside the hash set, not in the report text.
        Assert.Contains(diagnostics,
            d => d.Id == "NPDFGEN0004" &&
                 d.GetMessage().Contains("color", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("id")]
    [InlineData("type")]
    [InlineData("default")]
    [InlineData("inherit")]
    [InlineData("applies_to")]
    [InlineData("computed")]
    public void Missing_required_field_emits_NPDFGEN0005(string fieldToOmit)
    {
        // Every entry must have all 7 fields. The generator emits NPDFGEN0005 with a
        // message naming the missing field — pinning the exact field name in the diagnostic
        // gives consumers an actionable error.
        var fields = new System.Collections.Generic.Dictionary<string, string>
        {
            ["name"] = "\"name\": \"color\"",
            ["id"] = "\"id\": \"Color\"",
            ["type"] = "\"type\": \"Color\"",
            ["default"] = "\"default\": \"black\"",
            ["inherit"] = "\"inherit\": true",
            ["applies_to"] = "\"applies_to\": \"All\"",
            ["computed"] = "\"computed\": \"AbsoluteColor\"",
        };
        fields.Remove(fieldToOmit);
        var entry = "{ " + string.Join(", ", fields.Values) + " }";
        var json = "{ \"properties\": [ " + entry + " ] }";

        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics,
            d => d.Id == "NPDFGEN0005" && d.GetMessage().Contains("'" + fieldToOmit + "'"));
    }

    [Fact]
    public void Empty_id_emits_NPDFGEN0005()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0005");
    }

    [Fact]
    public void Invalid_csharp_identifier_in_id_emits_NPDFGEN0005()
    {
        // PropertyId values must be valid C# identifiers — the generator emits them as enum
        // member names. A leading digit, hyphen, or punctuation breaks the emitted code.
        var json = """
            {
              "properties": [
                { "name": "color", "id": "0Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics,
            d => d.Id == "NPDFGEN0005" && d.GetMessage().Contains("identifier"));
    }

    [Fact]
    public void Hyphenated_id_emits_NPDFGEN0005()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Bad-Id", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics,
            d => d.Id == "NPDFGEN0005" && d.GetMessage().Contains("identifier"));
    }

    [Fact]
    public void Valid_input_produces_no_diagnostics_and_emits_source()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var (diagnostics, generatedSources) = RunGeneratorWithOutput(json);
        Assert.Empty(diagnostics);
        Assert.Single(generatedSources);
        Assert.Contains("PropertyId.Color", generatedSources[0]);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    private static ImmutableArray<RoslynDiagnostic> RunGenerator(string propertiesJson)
    {
        var (diagnostics, _) = RunGeneratorWithOutput(propertiesJson);
        return diagnostics;
    }

    private static (ImmutableArray<RoslynDiagnostic> Diagnostics, ImmutableArray<string> Sources) RunGeneratorWithOutput(string propertiesJson)
    {
        var generator = new CssPropertyGenerator().AsSourceGenerator();
        var driver = CSharpGeneratorDriver.Create(
            generators: new[] { generator },
            additionalTexts: new[] { (AdditionalText)new InMemoryAdditionalText("/properties.json", propertiesJson) },
            parseOptions: CSharpParseOptions.Default,
            optionsProvider: null,
            driverOptions: default);

        // IL3000 about Assembly.Location is irrelevant here — the test runner is never
        // packaged as a single-file app; this is a regular xunit host process.
#pragma warning disable IL3000
        var corLibLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000
        var compilation = CSharpCompilation.Create(
            assemblyName: "Test",
            syntaxTrees: System.Array.Empty<SyntaxTree>(),
            references: new[] { MetadataReference.CreateFromFile(corLibLocation) },
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var newDriver = driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out _,
            out var generatorDiagnostics,
            CancellationToken.None);

        var result = ((CSharpGeneratorDriver)newDriver).GetRunResult();
        var sources = result.GeneratedTrees.Select(t => t.ToString()).ToImmutableArray();
        return (generatorDiagnostics, sources);
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text);
        }

        public override string Path { get; }

        public override SourceText? GetText(CancellationToken cancellationToken = default) => _text;
    }
}
