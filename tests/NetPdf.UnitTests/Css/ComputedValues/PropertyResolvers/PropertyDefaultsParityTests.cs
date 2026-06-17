// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Linq;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;
using Xunit.Abstractions;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Rec 10 parity gate: every property's <see cref="PropertyMeta.DefaultValue"/> in
/// <c>properties.json</c> must run through <see cref="PropertyResolverDispatch"/>
/// and produce a non-Invalid result. If a default emits
/// <see cref="CssDiagnosticCodes.CssPropertyValueInvalid001"/>, the cascade will
/// flood the diagnostic sink for every styled element with no explicit declaration —
/// that's a serious correctness bug in either the resolver coverage or the default
/// itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acceptance criteria for a default value:</b>
/// </para>
/// <list type="number">
///   <item>For PropertyTypes wired in cycle 1 (Color / Length / Number / Integer /
///     Keyword + the dimension family), the default must be <b>Resolved</b> — the
///     resolver succeeded and the slot carries the typed initial value.</item>
///   <item>For PropertyTypes still deferred to cycle 2 (e.g. <c>LineHeight</c>,
///     <c>Content</c> — FontFamilyList / FontWeight / FontSize / LineWidth / FlexBasis /
///     VerticalAlign have since been wired), the default must be <b>Deferred</b> /
///     UnsupportedUnvalidated with the original text present — the cascade carries it forward
///     for cycle-2 re-resolution.</item>
/// </list>
/// <para>
/// <b>Invalid is always a failure</b> — even for cycle-2 PropertyTypes, the default
/// shouldn't trip CSS-PROPERTY-VALUE-INVALID-001. If that fires, either the default
/// in properties.json is wrong, or a resolver mis-classifies it as invalid when it
/// should defer.
/// </para>
/// </remarks>
public sealed class PropertyDefaultsParityTests
{
    private readonly ITestOutputHelper _output;
    public PropertyDefaultsParityTests(ITestOutputHelper output) => _output = output;

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Fact]
    public void Every_property_default_resolves_or_defers_but_is_never_invalid()
    {
        var failures = new List<string>();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            var sink = new CapturingSink();
            var result = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue, sink);

            if (result.IsInvalid)
            {
                var diagMessages = string.Join("; ", sink.Diagnostics.Select(d => d.Message));
                failures.Add($"  {meta.Name} (id={meta.Id}, type={meta.Type}, default=\"{meta.DefaultValue}\") → Invalid. Diagnostics: {diagMessages}");
            }
        }
        if (failures.Count > 0)
        {
            _output.WriteLine("Property defaults that resolve to Invalid (cascade would flood diagnostics):");
            foreach (var f in failures) _output.WriteLine(f);
            Assert.Fail($"{failures.Count} property defaults resolved as Invalid — see test output above.");
        }
    }

    [Fact]
    public void Every_property_default_emits_no_diagnostic()
    {
        // Even a Deferred result should be silent — diagnostics are for parse FAILURE,
        // not "later resolution needed". This test catches a resolver that classifies
        // a default as invalid + emits a diagnostic but happens to also return Invalid
        // (which the prior test catches) — belt-and-suspenders against accidental
        // diagnostic noise on the cascade-default path.
        var failures = new List<string>();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            var sink = new CapturingSink();
            _ = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue, sink);
            if (sink.Diagnostics.Count > 0)
            {
                var msgs = string.Join("; ", sink.Diagnostics.Select(d => d.Code + ": " + d.Message));
                failures.Add($"  {meta.Name} (default=\"{meta.DefaultValue}\") emitted: {msgs}");
            }
        }
        if (failures.Count > 0)
        {
            _output.WriteLine("Property defaults that emit a diagnostic on resolve:");
            foreach (var f in failures) _output.WriteLine(f);
            Assert.Fail($"{failures.Count} property defaults emit diagnostics — see test output above.");
        }
    }

    [Fact]
    public void Cycle_1_property_types_resolve_their_defaults_to_typed_slots()
    {
        // Properties whose type is wired in cycle 1 (Color / dimension family /
        // Number / Integer / Keyword) MUST land Resolved — anything else is a
        // resolver coverage gap.
        var cycle1Types = new HashSet<PropertyType>
        {
            PropertyType.Color,
            PropertyType.Length, PropertyType.LengthPercentage,
            PropertyType.LengthPercentageAuto, PropertyType.Percentage,
            PropertyType.TextSpacing,
            PropertyType.Number, PropertyType.Integer,
            PropertyType.Keyword,
            // Per Phase 3 Task 15 L8 — FlexBasis joined the resolved
            // family. Routes through LengthResolver (sharing the
            // numeric grammar with LengthPercentageAuto) + admits the
            // `auto` + `content` keywords specifically for §7.2. The
            // default `auto` now resolves to Keyword(0); pre-L8 the
            // dispatch returned UnsupportedUnvalidated.
            PropertyType.FlexBasis,
            // Per Phase 3 Task 15 L12 — MaxSize joined the resolved
            // family for §9.7 step-4 min/max-width clamping. Routes
            // through LengthResolver (sharing the numeric grammar
            // with LengthPercentageAuto) + admits the `none` keyword
            // per CSS Sizing L3 §5.2 (= no upper bound). The default
            // `none` now resolves to Keyword(0); pre-L12 the dispatch
            // returned UnsupportedUnvalidated.
            PropertyType.MaxSize,
            // Per Phase 3 Task 17 cycle 0b — GridTemplateList +
            // GridLine joined the resolved family. Route through
            // GridTemplateListResolver / GridLineResolver respectively;
            // the defaults `none` / `auto` resolve to Keyword(0) (= no
            // side-table entry), and any non-default value lands a
            // typed AST in ComputedStyle's side-table dictionary per
            // PR-#89 P1 #3 (uniform-storage decision). Pre-cycle-0b
            // both types returned UnsupportedUnvalidated; the dispatch
            // wiring in cycle 0b promotes them to Resolved.
            PropertyType.GridTemplateList,
            PropertyType.GridLine,
            // Per Phase 3 Task 18 cycle 7a — GridTemplateAreas joined
            // the resolved family. Routes through
            // GridTemplateAreasResolver; the default `none` resolves
            // to Keyword(0) (= no side-table entry), and any non-
            // default value lands the parsed 2-D map in the
            // side-table.
            PropertyType.GridTemplateAreas,
            // Per Phase 5 layout→PDF cycle 3 — LineWidth (border-*-width,
            // column-rule-width) joined the resolved family via LineWidthResolver:
            // thin/medium/thick → 1/3/5px + <length>. The default `medium` now
            // resolves to LengthPx(3); pre-cycle-3 it returned UnsupportedUnvalidated.
            PropertyType.LineWidth,
            // Per Phase 5 layout→PDF cycle 4 — the font-property family joined the
            // resolved set: FontSize (keywords + absolute lengths; em/%/larger/
            // smaller resolve in the box-builder walk), FontWeight (→ integer), and
            // FontFamilyList (→ a side-table list). The defaults medium / normal /
            // serif now resolve; pre-cycle-4 all three returned UnsupportedUnvalidated.
            PropertyType.FontSize,
            PropertyType.FontWeight,
            PropertyType.FontFamilyList,
            // vertical-align cycle — VerticalAlign joined the resolved family via
            // VerticalAlignResolver (keywords → a Keyword slot; <length>/<percentage> → a
            // LengthPercentage slot). The default `baseline` resolves to Keyword(0); pre-cycle it
            // returned UnsupportedUnvalidated.
            PropertyType.VerticalAlign,
        };

        var failures = new List<string>();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (!cycle1Types.Contains(meta.Type)) continue;
            var result = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue);
            if (!result.IsResolved)
            {
                failures.Add($"  {meta.Name} (type={meta.Type}, default=\"{meta.DefaultValue}\") → State={result.State}");
            }
        }
        if (failures.Count > 0)
        {
            _output.WriteLine("Cycle-1 property defaults that did not Resolve:");
            foreach (var f in failures) _output.WriteLine(f);
            Assert.Fail($"{failures.Count} cycle-1 property defaults failed to resolve.");
        }
    }

    [Fact]
    public void Cycle_2_property_types_return_UnsupportedUnvalidated_for_their_defaults()
    {
        // Per the hardening review: cycle-2 PropertyTypes surface as
        // UnsupportedUnvalidated — distinct from Deferred which means "validated".
        // Catches drift if a resolver mis-classifies a cycle-2 property.
        var cycle1Types = new HashSet<PropertyType>
        {
            PropertyType.Color,
            PropertyType.Length, PropertyType.LengthPercentage,
            PropertyType.LengthPercentageAuto, PropertyType.Percentage,
            PropertyType.TextSpacing,
            PropertyType.Number, PropertyType.Integer,
            PropertyType.Keyword,
            // Per Phase 3 Task 15 L8 — FlexBasis joined the resolved
            // family. Routes through LengthResolver (sharing the
            // numeric grammar with LengthPercentageAuto) + admits the
            // `auto` + `content` keywords specifically for §7.2. The
            // default `auto` now resolves to Keyword(0); pre-L8 the
            // dispatch returned UnsupportedUnvalidated.
            PropertyType.FlexBasis,
            // Per Phase 3 Task 15 L12 — MaxSize joined the resolved
            // family for §9.7 step-4 min/max-width clamping. Routes
            // through LengthResolver (sharing the numeric grammar
            // with LengthPercentageAuto) + admits the `none` keyword
            // per CSS Sizing L3 §5.2 (= no upper bound). The default
            // `none` now resolves to Keyword(0); pre-L12 the dispatch
            // returned UnsupportedUnvalidated.
            PropertyType.MaxSize,
            // Per Phase 3 Task 17 cycle 0b — GridTemplateList +
            // GridLine joined the resolved family. Route through
            // GridTemplateListResolver / GridLineResolver respectively;
            // the defaults `none` / `auto` resolve to Keyword(0) (= no
            // side-table entry), and any non-default value lands a
            // typed AST in ComputedStyle's side-table dictionary per
            // PR-#89 P1 #3 (uniform-storage decision). Pre-cycle-0b
            // both types returned UnsupportedUnvalidated; the dispatch
            // wiring in cycle 0b promotes them to Resolved.
            PropertyType.GridTemplateList,
            PropertyType.GridLine,
            // Per Phase 3 Task 18 cycle 7a — GridTemplateAreas joined
            // the resolved family. Routes through
            // GridTemplateAreasResolver; the default `none` resolves
            // to Keyword(0) (= no side-table entry), and any non-
            // default value lands the parsed 2-D map in the
            // side-table.
            PropertyType.GridTemplateAreas,
            // Per Phase 5 layout→PDF cycle 3 — LineWidth (border-*-width,
            // column-rule-width) joined the resolved family via LineWidthResolver:
            // thin/medium/thick → 1/3/5px + <length>. The default `medium` now
            // resolves to LengthPx(3); pre-cycle-3 it returned UnsupportedUnvalidated.
            PropertyType.LineWidth,
            // Per Phase 5 layout→PDF cycle 4 — the font-property family joined the
            // resolved set: FontSize (keywords + absolute lengths; em/%/larger/
            // smaller resolve in the box-builder walk), FontWeight (→ integer), and
            // FontFamilyList (→ a side-table list). The defaults medium / normal /
            // serif now resolve; pre-cycle-4 all three returned UnsupportedUnvalidated.
            PropertyType.FontSize,
            PropertyType.FontWeight,
            PropertyType.FontFamilyList,
            // Backlog #6 — Position (object-position) + PageName (page) are validation-only
            // registrations (PositionResolver / PageNameResolver). Their defaults `50% 50%` / `auto`
            // validate to Deferred (the value is consumed raw downstream), so they are NOT
            // UnsupportedUnvalidated — exclude them from the cycle-2 check.
            PropertyType.Position,
            PropertyType.PageName,
            // vertical-align cycle — VerticalAlign is now wired (VerticalAlignResolver); its default
            // `baseline` resolves to Keyword(0), so it is NOT UnsupportedUnvalidated — exclude it.
            PropertyType.VerticalAlign,
        };

        var failures = new List<string>();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            if (cycle1Types.Contains(meta.Type)) continue;
            var result = PropertyResolverDispatch.Resolve(meta.Id, meta.DefaultValue);
            if (!result.IsUnsupportedUnvalidated || result.RawText is null)
            {
                failures.Add($"  {meta.Name} (type={meta.Type}, default=\"{meta.DefaultValue}\") → State={result.State}, RawText={result.RawText ?? "(null)"}");
            }
        }
        if (failures.Count > 0)
        {
            _output.WriteLine("Cycle-2 property defaults that are not UnsupportedUnvalidated:");
            foreach (var f in failures) _output.WriteLine(f);
            Assert.Fail($"{failures.Count} cycle-2 property defaults misclassified.");
        }
    }

    // ============================================================
    // Representative valid + invalid corpus declarations
    // ============================================================

    [Fact]
    public void Representative_valid_declarations_resolve()
    {
        // A small corpus of declarations that real stylesheets contain, exercising
        // the cycle-1 surface end-to-end.
        var corpus = new (PropertyId Id, string Value)[]
        {
            (PropertyId.Color,           "red"),
            (PropertyId.Color,           "#abc"),
            (PropertyId.Color,           "rgb(255, 128, 0)"),
            (PropertyId.Color,           "rgb(255 128 0 / 0.8)"),
            (PropertyId.Color,           "hsl(120, 100%, 50%)"),
            (PropertyId.Color,           "transparent"),
            (PropertyId.BackgroundColor, "white"),
            (PropertyId.Width,           "100px"),
            (PropertyId.Width,           "75%"),
            (PropertyId.Width,           "auto"),
            (PropertyId.PaddingTop,      "8px"),
            (PropertyId.MarginLeft,      "-12px"),    // negative ok on margin
            (PropertyId.Top,             "-4px"),     // negative ok on top
            (PropertyId.LetterSpacing,   "normal"),
            (PropertyId.LetterSpacing,   "-0.5px"),   // negative letter-spacing ok
            (PropertyId.WordSpacing,     "normal"),
            (PropertyId.FlexGrow,        "0"),
            (PropertyId.FlexGrow,        "1.5"),
            (PropertyId.Display,         "flex"),
            (PropertyId.Position,        "relative"),
            (PropertyId.BoxSizing,       "border-box"),
            (PropertyId.TextAlign,       "center"),
            (PropertyId.BorderTopStyle,  "dotted"),
        };
        foreach (var (pid, value) in corpus)
        {
            var sink = new CapturingSink();
            var result = PropertyResolverDispatch.Resolve(pid, value, sink);
            Assert.True(result.IsResolved,
                $"Expected '{pid}: {value}' to Resolve, got State={result.State}.");
            Assert.Empty(sink.Diagnostics);
        }
    }

    [Fact]
    public void Representative_invalid_declarations_are_invalid_with_diagnostic()
    {
        var corpus = new (PropertyId Id, string Value)[]
        {
            (PropertyId.Color,       "not-a-color"),
            (PropertyId.Color,       "#zzz"),
            (PropertyId.Color,       "rgb(255, 128 / 0.5)"),  // mixed comma + slash
            (PropertyId.Width,       "16"),                    // bare non-zero
            (PropertyId.PaddingTop,  "-10px"),                 // negative on padding
            (PropertyId.PaddingTop,  "auto"),                  // auto not on LengthPercentage
            (PropertyId.PaddingTop,  "-5%"),                   // negative percentage
            (PropertyId.LetterSpacing, "5%"),                  // % not on letter-spacing
            (PropertyId.FlexGrow,    "-1"),                    // negative flex-grow
            (PropertyId.Display,     "fleks"),                 // typo
            (PropertyId.Position,    "stickyy"),                // typo
        };
        foreach (var (pid, value) in corpus)
        {
            var sink = new CapturingSink();
            var result = PropertyResolverDispatch.Resolve(pid, value, sink);
            Assert.True(result.IsInvalid,
                $"Expected '{pid}: {value}' to be Invalid, got State={result.State}.");
            Assert.NotEmpty(sink.Diagnostics);
            Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
        }
    }
}
