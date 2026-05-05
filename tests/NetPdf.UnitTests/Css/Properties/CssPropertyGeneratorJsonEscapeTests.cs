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
    private static (Compilation output, ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> diagnostics)
        RunGenerator(string propertiesJsonContent)
    {
#pragma warning disable IL3000
        var coreLibRef = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
#pragma warning restore IL3000
        var compilation = CSharpCompilation.Create(
            "NetPdf.Css.Test",
            new[] { CSharpSyntaxTree.ParseText("namespace NetPdf.Css.Properties { }") },
            new[] { coreLibRef });

        var generator = new CssPropertyGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
                new InMemoryAdditionalText("properties.json", propertiesJsonContent)));

        var result = driver.RunGenerators(compilation);
        var runResult = result.GetRunResult();
        return (compilation, runResult.Diagnostics);
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
        var (_, diagnostics) = RunGenerator(json);
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
        var (_, diagnostics) = RunGenerator(json);
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
        var (_, diagnostics) = RunGenerator(json);
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
        var (_, diagnostics) = RunGenerator(json);
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
        var (_, diagnostics) = RunGenerator(json);
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
