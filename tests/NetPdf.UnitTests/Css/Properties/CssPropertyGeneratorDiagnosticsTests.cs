// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;
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
    // Enum-token validation (NPDFGEN0006)
    // ------------------------------------------------------------

    [Fact]
    public void Unknown_property_type_emits_NPDFGEN0006()
    {
        // Without the enum-token check, the generator would emit `PropertyType.Bogus`
        // and the consumer's compile would fail with a confusing CS0117. NPDFGEN0006
        // catches it at the generator stage with an actionable message.
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Bogus", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0006" && d.GetMessage().Contains("'Bogus'"));
    }

    [Fact]
    public void Unknown_applies_to_emits_NPDFGEN0006()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "Everyone", "computed": "AbsoluteColor" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0006" && d.GetMessage().Contains("'Everyone'"));
    }

    [Fact]
    public void Unknown_computed_emits_NPDFGEN0006()
    {
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "Magic" }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Id == "NPDFGEN0006" && d.GetMessage().Contains("'Magic'"));
    }

    // ------------------------------------------------------------
    // Edge cases
    // ------------------------------------------------------------

    [Fact]
    public void Empty_properties_array_still_emits_valid_compilable_source()
    {
        // An empty properties array is degenerate but valid — generator should produce a
        // PropertyId enum with no values + a Count=0 metadata table. The output must still
        // compile so projects can stage incremental property additions.
        var json = "{ \"properties\": [] }";
        var (diagnostics, sources) = RunGeneratorWithOutput(json);
        Assert.Empty(diagnostics);
        Assert.Single(sources);
        Assert.Contains("PropertyId", sources[0]);
        Assert.Contains("Count = 0", sources[0]);

        AssertGeneratedSourceCompiles(sources[0]);
    }

    [Fact]
    public void Generated_source_for_valid_input_compiles_against_supporting_types()
    {
        // The strongest test: the generator output must compile when paired with the
        // hand-written supporting types (PropertyType, AppliesTo, ComputedValueKind,
        // PropertyMeta). Catches mismatches between the generator's emitted symbols and
        // the API surface of the hand-written types.
        var json = """
            {
              "properties": [
                { "name": "color", "id": "Color", "type": "Color", "default": "black", "inherit": true, "applies_to": "All", "computed": "AbsoluteColor" },
                { "name": "font-size", "id": "FontSize", "type": "FontSize", "default": "medium", "inherit": true, "applies_to": "All", "computed": "AbsoluteLength" },
                { "name": "letter-spacing", "id": "LetterSpacing", "type": "TextSpacing", "default": "normal", "inherit": true, "applies_to": "All", "computed": "AbsoluteLength" },
                { "name": "max-width", "id": "MaxWidth", "type": "MaxSize", "default": "none", "inherit": false, "applies_to": "BlockOrInlineOrReplaced", "computed": "Specified" },
                { "name": "align-items", "id": "AlignItems", "type": "Keyword", "default": "normal", "inherit": false, "applies_to": "FlexContainers", "computed": "Specified" }
              ]
            }
            """;
        var (diagnostics, sources) = RunGeneratorWithOutput(json);
        Assert.Empty(diagnostics);
        Assert.Single(sources);
        AssertGeneratedSourceCompiles(sources[0]);
    }

    // ------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------

    /// <summary>
    /// Builds a compilation containing the generated source plus minimal stubs of the
    /// supporting types (<c>PropertyType</c>, <c>AppliesTo</c>, <c>ComputedValueKind</c>,
    /// <c>PropertyMeta</c>) and asserts the compilation has no errors. Catches generator
    /// output that names symbols not present on the hand-written types.
    /// </summary>
    private static void AssertGeneratedSourceCompiles(string generatedSource)
    {
        const string supportingTypes = """
            namespace NetPdf.Css.Properties;

            internal enum PropertyType : byte
            {
                Unknown, Color, Length, LengthPercentage, LengthPercentageAuto,
                Number, Integer, Percentage, Keyword, String, Url,
                Time, Angle, Resolution, FontFamilyList, FontWeight,
                LineWidth, FontSize, LineHeight, Content, VerticalAlign, FlexBasis,
                TextSpacing, MaxSize, Custom,
            }

            internal enum AppliesTo : byte
            {
                Unknown, All, BlockOrInlineOrReplaced, Positioned, BlockOnly, InlineOnly,
                ListItem, TableElements, ReplacedOnly, FlexItems, GridItems,
                FlexContainers, GridContainers,
            }

            internal enum ComputedValueKind : byte
            {
                Specified, AbsoluteColor, AbsoluteLength, ResolvedNumber, ResolvedKeyword, Custom,
            }

            internal readonly record struct PropertyMeta(
                PropertyId Id,
                string Name,
                PropertyType Type,
                string DefaultValue,
                bool Inherits,
                AppliesTo AppliesTo,
                ComputedValueKind Computed);
            """;

        // Reference every assembly currently loaded into the test process. That set
        // includes the full netcoreapp framework (System.Runtime, System.Collections,
        // System.Collections.Frozen, System.Collections.Immutable, etc.) so the
        // compilation can resolve any framework type the generated source touches.
        // The IL3000 single-file warning is irrelevant for the xunit host process.
#pragma warning disable IL3000
        var refs = System.AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
#pragma warning restore IL3000

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratedCompileCheck",
            syntaxTrees: new[]
            {
                CSharpSyntaxTree.ParseText(generatedSource),
                CSharpSyntaxTree.ParseText(supportingTypes),
            },
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == RoslynSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated source failed to compile:\n" +
            string.Join("\n", errors.Select(d => d.ToString())));
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
