// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NetPdf.SourceGen;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Copilot finding #4 regression: <c>CssPropertyGenerator.ReadString</c> must implement
/// the standard JSON escape set per RFC 8259 §7 — the earlier implementation only
/// handled <c>\\</c> / <c>\"</c> / <c>\n</c> / <c>\r</c> / <c>\t</c> / <c>\/</c> and
/// silently appended the literal letter for everything else, so a <c>\uXXXX</c> /
/// <c>\b</c> / <c>\f</c> in <c>properties.json</c> would corrupt the generated source.
/// </summary>
public sealed class CssPropertyGeneratorJsonEscapeTests
{
    /// <summary>Runs the source generator AND compiles its output, returning the union of
    /// generator-driver diagnostics + post-generator consumer-compilation diagnostics.
    /// Required for the JSON-escape regression suite: a generator can be silent about an
    /// invalid escape but emit syntactically-broken C# (e.g., a stray backspace in a
    /// string literal). Compiling the generator's output against the supporting-type
    /// stubs surfaces those failures.</summary>
    private static ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> RunGenerator(string propertiesJsonContent)
    {
        // Mirror the supporting-type stubs from CssPropertyGeneratorDiagnosticsTests so the
        // generator's emitted source resolves PropertyMeta / PropertyType / AppliesTo /
        // ComputedValueKind correctly. The real types live in NetPdf.Css and are internal;
        // the synthetic compilation isn't InternalsVisibleTo'd, so we provide stubs.
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

#pragma warning disable IL3000
        // Reference every loaded assembly so framework types the generated source touches
        // (System.Collections.Frozen, System.Collections.Immutable, etc.) resolve.
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
#pragma warning restore IL3000

        var compilation = CSharpCompilation.Create(
            "NetPdf.Css.Test",
            new[] { CSharpSyntaxTree.ParseText(supportingTypes) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new CssPropertyGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("properties.json", propertiesJsonContent)));

        // Run + compile the post-generator output. RunGeneratorsAndUpdateCompilation
        // returns the new compilation with the generated trees included; emit-diagnostics
        // surface any syntactic / semantic errors the generator may have introduced.
        driver = (Microsoft.CodeAnalysis.CSharp.CSharpGeneratorDriver)driver
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);
        var consumerDiagnostics = outputCompilation.GetDiagnostics();
        return generatorDiagnostics.Concat(consumerDiagnostics).ToImmutableArray();
    }

    [Fact]
    public void Hex_escape_uXXXX_is_decoded_correctly()
    {
        // é is é. Use it in a string field; if the parser misreads the escape, the
        // generator's output reflects the wrong character or fails entirely.
        var json = """
            {
              "properties": [
                {
                  "name": "color",
                  "id": "Color",
                  "type": "Color",
                  "default": "café",
                  "inherit": true,
                  "applies_to": "All",
                  "computed": "AbsoluteColor"
                }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        // Generator should produce no errors — é decodes to é cleanly.
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Backslash_b_decodes_to_backspace()
    {
        // \b is U+0008 backspace. Allowed in JSON, must decode without error.
        var json = """
            {
              "properties": [
                {
                  "name": "color",
                  "id": "Color",
                  "type": "Color",
                  "default": "a\bb",
                  "inherit": true,
                  "applies_to": "All",
                  "computed": "AbsoluteColor"
                }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Backslash_f_decodes_to_form_feed()
    {
        var json = """
            {
              "properties": [
                {
                  "name": "color",
                  "id": "Color",
                  "type": "Color",
                  "default": "a\fb",
                  "inherit": true,
                  "applies_to": "All",
                  "computed": "AbsoluteColor"
                }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Invalid_escape_emits_generator_diagnostic()
    {
        // \q is not a valid JSON escape. Earlier code silently emitted 'q'; new code rejects.
        var json = """
            {
              "properties": [
                {
                  "name": "color",
                  "id": "Color",
                  "type": "Color",
                  "default": "a\qb",
                  "inherit": true,
                  "applies_to": "All",
                  "computed": "AbsoluteColor"
                }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        // Should surface as a generator diagnostic (NPDFGEN0003 malformed JSON).
        Assert.Contains(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void Truncated_uXXXX_escape_emits_generator_diagnostic()
    {
        // \u must be followed by exactly 4 hex digits. Truncation should fail.
        var json = """
            {
              "properties": [
                {
                  "name": "color",
                  "id": "Color",
                  "type": "Color",
                  "default": "a\u12",
                  "inherit": true,
                  "applies_to": "All",
                  "computed": "AbsoluteColor"
                }
              ]
            }
            """;
        var diagnostics = RunGenerator(json);
        Assert.Contains(diagnostics, d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    /// <summary>Minimal in-memory <see cref="AdditionalText"/> for the source-generator
    /// driver's <c>AddAdditionalTexts</c> input.</summary>
    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly string _content;
        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _content = content;
        }
        public override string Path { get; }
        public override SourceText? GetText(System.Threading.CancellationToken cancellationToken = default) =>
            SourceText.From(_content, Encoding.UTF8);
    }
}
