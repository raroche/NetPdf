// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Frozen;
using System.Linq;
using AngleSharp.Css.Dom;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.RealDocuments.Css;

/// <summary>
/// Corpus-level coverage check for <see cref="PropertyMetadata.NameToId"/>. Walks every
/// declaration in every <c>&lt;style&gt;</c> block of every invoice corpus file and asserts
/// each authored property name is either (a) registered in the property metadata table, or
/// (b) explicitly listed in <see cref="UnsupportedAllowlist"/> as known-deferred /
/// known-unsupported with a documented reason.
/// </summary>
/// <remarks>
/// <para>
/// This is the inverse of the generator's drift catcher: that test fires when properties.json
/// gains an entry without a rebuild. This one fires when the corpus uses a property the
/// registry doesn't know about — either properties.json needs the new entry, or the property
/// belongs in the allowlist with an explanation.
/// </para>
/// <para>
/// Legacy aliases (<c>page-break-inside</c> ↔ modern <c>break-inside</c>) are normalized via
/// <see cref="LegacyAliases"/> before lookup so the test mirrors how the cascade resolver
/// will handle them in Task 7+.
/// </para>
/// </remarks>
public sealed class PropertyCoverageCorpusTests
{
    private readonly ITestOutputHelper _output;

    public PropertyCoverageCorpusTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Properties present in the corpus but intentionally not in <c>properties.json</c> yet.
    /// Each entry should have a one-line rationale. New entries here are a signal that
    /// either the property should land in <c>properties.json</c> or the cascade should emit
    /// a stable diagnostic for it.
    /// </summary>
    private static readonly FrozenSet<string> UnsupportedAllowlist = new[]
    {
        // Shorthand properties — AngleSharp.Css emits them but the cascade works on
        // longhands. The shorthand is decomposed into its longhand components which are
        // in properties.json.
        "background", "border", "border-bottom", "border-left", "border-right", "border-top",
        "border-style", "border-color", "border-width", "border-radius",
        "font", "list-style", "margin", "padding", "outline",
        "transition", "animation", "flex", "grid", "place-items", "place-content", "place-self",
        "text-decoration",
        // CSS sub-features that the renderer emits diagnostics for in Phase 4 (post-v1).
        "background-image", "background-position", "background-position-x", "background-position-y",
        "background-repeat", "background-repeat-x", "background-repeat-y",
        "background-size", "background-attachment", "background-origin", "background-clip",
        "border-bottom-left-radius", "border-bottom-right-radius",
        "border-top-left-radius", "border-top-right-radius",
        "outline-color", "outline-style", "outline-width", "outline-offset",
        "box-shadow", "text-shadow", "filter", "mix-blend-mode", "isolation",
        "transform", "transform-origin", "transform-style", "perspective", "perspective-origin",
        "will-change", "backface-visibility",
        "object-fit", "object-position",
        "list-style-type", "list-style-position", "list-style-image",
        "table-layout", "caption-side", "empty-cells", "border-spacing",
        "transition-property", "transition-duration", "transition-timing-function", "transition-delay",
        "animation-name", "animation-duration", "animation-timing-function", "animation-delay",
        "animation-iteration-count", "animation-direction", "animation-fill-mode", "animation-play-state",
        "page-break-before", "page-break-after", // legacy synonyms; modern break-* not in corpus today
        "break-before", "break-after",
        "orphans", "widows",
        "z-index", "visibility",
        "opacity",
        "direction", "unicode-bidi",
        "text-indent", "text-overflow", "text-wrap", "word-break", "overflow-wrap", "hyphens",
        "tab-size",
        "color-scheme", "accent-color", "caret-color",
        "user-select", "pointer-events", "touch-action", "scroll-behavior",
        "src", "font-display", "font-feature-settings", "font-variation-settings",
        "font-variant", "font-variant-caps", "font-variant-ligatures", "font-variant-numeric",
        "all", "initial-letter",
        // CSS Grid (Level 1) — large surface, deferred to Phase 3.
        "grid-area", "grid-auto-columns", "grid-auto-flow", "grid-auto-rows",
        "grid-column", "grid-column-end", "grid-column-gap", "grid-column-start",
        "grid-row", "grid-row-end", "grid-row-gap", "grid-row-start",
        "grid-template", "grid-template-areas", "grid-template-columns", "grid-template-rows",
        "gap", "row-gap", "column-gap",
        // Text decoration sub-properties.
        "text-decoration-color", "text-decoration-style", "text-decoration-thickness",
        // Vendor prefixes — AngleSharp.Css surfaces them; the cascade strips at Task 7.
        "-webkit-print-color-adjust", "print-color-adjust",
    }.ToFrozenSet(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Map legacy property names to the modern equivalent the registry knows about. The
    /// cascade resolver will perform the same alias step in Task 7 so authored documents
    /// with legacy syntax cascade correctly.
    /// </summary>
    private static readonly FrozenDictionary<string, string> LegacyAliases =
        new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["page-break-inside"] = "break-inside",
        }.ToFrozenDictionary(System.StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("Corpus/Invoices/01-classic-pure-css.html")]
    [InlineData("Corpus/Invoices/02-tailwind-cdn.html")]
    [InlineData("Corpus/Invoices/03-tailwind-cdn-responsive.html")]
    [InlineData("Corpus/Invoices/04-anvil-running-elements.html")]
    public async Task Corpus_invoice_property_names_are_known_or_documented_unsupported(string relativePath)
    {
        var html = LoadCorpusFile(relativePath);
        var host = new HtmlParsingHost();
        var document = await host.ParseAsync(html, new HtmlPdfOptions());

        var sheets = document.StyleSheets.OfType<ICssStyleSheet>().ToList();
        var unknown = new System.Collections.Generic.SortedSet<string>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in sheets)
        {
            CollectUnknownProperties(sheet, unknown);
        }

        if (unknown.Count > 0)
        {
            // Format helpfully so the failing test points the user at properties.json.
            _output.WriteLine($"Unknown properties found in {relativePath}:");
            foreach (var p in unknown) _output.WriteLine("  - " + p);
        }

        Assert.True(unknown.Count == 0,
            relativePath + " uses " + unknown.Count + " property name(s) not in PropertyMetadata.NameToId " +
            "and not in PropertyCoverageCorpusTests.UnsupportedAllowlist: " +
            string.Join(", ", unknown) +
            ". Add the property to properties.json (preferred) or to the allowlist with a one-line rationale.");
    }

    [Fact]
    public void Legacy_aliases_resolve_to_known_properties()
    {
        // Pin: every alias's right-hand side must exist in the registry. Catches drift if
        // properties.json renames the modern equivalent.
        foreach (var (legacy, modern) in LegacyAliases)
        {
            Assert.True(PropertyMetadata.NameToId.ContainsKey(modern),
                $"Legacy alias '{legacy}' → '{modern}' but '{modern}' is not in PropertyMetadata.NameToId.");
        }
    }

    private static void CollectUnknownProperties(
        ICssStyleSheet sheet,
        System.Collections.Generic.SortedSet<string> unknown)
    {
        foreach (var rule in sheet.Rules)
        {
            CollectFromRule(rule, unknown);
        }
    }

    private static void CollectFromRule(ICssRule rule, System.Collections.Generic.SortedSet<string> unknown)
    {
        if (rule is ICssStyleRule styleRule)
        {
            foreach (var declaration in styleRule.Style)
            {
                if (declaration is null) continue;
                CheckProperty(declaration.Name, unknown);
            }
        }
        else if (rule is ICssGroupingRule grouping)
        {
            foreach (var nested in grouping.Rules) CollectFromRule(nested, unknown);
        }
        else if (rule is ICssPageRule pageRule)
        {
            foreach (var declaration in pageRule.Style)
            {
                if (declaration is null) continue;
                CheckProperty(declaration.Name, unknown);
            }
        }
        // Other rule types (charset/import/namespace/font-face/keyframes) don't carry style
        // declarations in the property-name sense we care about here.
    }

    private static void CheckProperty(string name, System.Collections.Generic.SortedSet<string> unknown)
    {
        if (string.IsNullOrEmpty(name)) return;

        // Vendor-prefixed -ms-/-moz-/-o- variants beyond the few in the allowlist: skip
        // silently; they're known-unsupported by definition.
        if (name.StartsWith("-moz-", System.StringComparison.Ordinal) ||
            name.StartsWith("-ms-", System.StringComparison.Ordinal) ||
            name.StartsWith("-o-", System.StringComparison.Ordinal))
            return;

        // Custom properties (--*) bypass the registry entirely — they live in a sparse side
        // table per element (Phase 2 Task 8 wires them).
        if (name.StartsWith("--", System.StringComparison.Ordinal)) return;

        // Apply legacy alias normalization before lookup.
        var normalized = LegacyAliases.TryGetValue(name, out var modern) ? modern : name;

        if (PropertyMetadata.NameToId.ContainsKey(normalized)) return;
        if (UnsupportedAllowlist.Contains(name)) return;
        unknown.Add(name);
    }

    private static string LoadCorpusFile(string relativePath)
    {
        var corpusRoot = LocateCorpusRoot();
        var fullPath = System.IO.Path.Combine(corpusRoot, relativePath);
        Assert.True(System.IO.File.Exists(fullPath), $"corpus file missing: {fullPath}");
        return System.IO.File.ReadAllText(fullPath);
    }

    private static string LocateCorpusRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "Corpus");
            if (System.IO.Directory.Exists(candidate)) return dir.FullName;
            var csproj = System.IO.Path.Combine(dir.FullName, "NetPdf.RealDocuments.csproj");
            if (System.IO.File.Exists(csproj)) return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.InvalidOperationException("Could not locate the NetPdf.RealDocuments source folder.");
    }
}
