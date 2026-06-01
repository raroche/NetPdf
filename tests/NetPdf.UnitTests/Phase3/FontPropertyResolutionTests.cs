// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// Phase 5 layout→PDF cycle 4 — integration tests that drive HTML through the real
/// cascade + box-builder walk and assert the parent-relative font properties
/// resolve against the parent's resolved values (the box-builder
/// <c>ResolveDeferredFontProperties</c> step).
/// </summary>
public sealed class FontPropertyResolutionTests
{
    private static Box? FindByTag(Box box, string tag)
    {
        if (string.Equals(box.SourceElement?.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            return box;
        foreach (var child in box.Children)
        {
            var found = FindByTag(child, tag);
            if (found is not null) return found;
        }
        return null;
    }

    private static Box? FindPseudo(Box box, BoxPseudo pseudo)
    {
        if (box.Pseudo == pseudo) return box;
        foreach (var child in box.Children)
        {
            var found = FindPseudo(child, pseudo);
            if (found is not null) return found;
        }
        return null;
    }

    [Fact]
    public async Task Pseudo_element_font_size_em_resolves_against_the_host()
    {
        // ::before's font-size:2em must resolve against the host <p>'s 10px → 20px.
        // (Covers ResolveDeferredFontProperties being wired into the pseudo path,
        // not just the element path.)
        var html = "<!DOCTYPE html><html><head><style>p::before{content:'x';font-size:2em}</style>" +
            "</head><body><p style=\"font-size:10px\">y</p></body></html>";
        var phase2 = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var before = FindPseudo(phase2.BoxRoot, BoxPseudo.Before);
        Assert.NotNull(before);
        Assert.Equal(20.0, before!.Style.Get(PropertyId.FontSize).AsLengthPx(), 3);
    }

    private static async Task<Box> ParentChildAsync(string parentDecl, string childDecl)
    {
        var html = "<!DOCTYPE html><html><body>" +
            $"<div style=\"{parentDecl}\"><p style=\"{childDecl}\">x</p></div></body></html>";
        var phase2 = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindByTag(phase2.BoxRoot, "p");
        Assert.NotNull(p);
        return p!;
    }

    [Fact]
    public async Task Font_size_em_resolves_against_the_parent()
    {
        var p = await ParentChildAsync("font-size:20px", "font-size:2em");
        var slot = p.Style.Get(PropertyId.FontSize);
        Assert.Equal(ComputedSlotTag.LengthPx, slot.Tag);
        Assert.Equal(40.0, slot.AsLengthPx(), 3); // 2em × 20px
    }

    [Fact]
    public async Task Font_size_percent_resolves_against_the_parent()
    {
        var p = await ParentChildAsync("font-size:20px", "font-size:150%");
        Assert.Equal(30.0, p.Style.Get(PropertyId.FontSize).AsLengthPx(), 3); // 150% × 20
    }

    [Fact]
    public async Task Font_weight_bolder_resolves_against_the_parent()
    {
        var p = await ParentChildAsync("font-weight:bold", "font-weight:bolder");
        Assert.Equal(900, p.Style.ReadFontWeight()); // bolder vs parent 700 → 900
    }

    [Fact]
    public async Task Font_family_resolves_to_the_authored_list()
    {
        var p = await ParentChildAsync("", "font-family:Arial, sans-serif");
        Assert.Equal(new[] { "Arial", "sans-serif" }, p.Style.ReadFontFamily().Families.ToArray());
    }

    [Fact]
    public async Task Inherited_font_family_propagates_to_descendants()
    {
        // body sets the family; the <p> doesn't redeclare it → it must INHERIT the
        // list (the canonical `body { font-family: ... }` pattern).
        var html = "<!DOCTYPE html><html><body style=\"font-family:Arial, sans-serif\">" +
            "<p>x</p></body></html>";
        var phase2 = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindByTag(phase2.BoxRoot, "p");
        Assert.NotNull(p);
        Assert.Equal(new[] { "Arial", "sans-serif" }, p!.Style.ReadFontFamily().Families.ToArray());
    }

    [Fact]
    public async Task Default_font_size_stays_16px()
    {
        // The medium→16 mapping keeps default-font-size elements unchanged — this is
        // what bounds the cycle-4 ripple (text measurement is identical to before).
        var html = "<!DOCTYPE html><html><body><p>x</p></body></html>";
        var phase2 = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        var p = FindByTag(phase2.BoxRoot, "p");
        Assert.NotNull(p);
        Assert.Equal(16.0, p!.Style.Get(PropertyId.FontSize).AsLengthPx(), 3);
    }
}
