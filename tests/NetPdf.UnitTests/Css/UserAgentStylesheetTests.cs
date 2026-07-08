// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NetPdf;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using NetPdf.Phase2;
using Xunit;

namespace NetPdf.UnitTests.Css;

/// <summary>
/// RC-UA — the built-in user-agent stylesheet. Before it, un-styled HTML lost every default the
/// HTML rendering model's UA sheet provides (heading sizes/weights/margins, b/strong/th bold,
/// em/i italic, list indentation, th centering). These tests drive the real conversion pipeline
/// (which prepends <see cref="UserAgentStylesheet"/>) and assert the computed values on bare markup,
/// plus that any author rule overrides the UA default.
/// </summary>
public sealed class UserAgentStylesheetTests
{
    private static async Task<Box> BuildAsync(string html)
    {
        var result = await Phase2Pipeline.RunFromHtmlAsync(html, new HtmlPdfOptions());
        return result.BoxRoot;
    }

    private static Box? Find(Box root, string tag)
    {
        if (root.SourceElement?.LocalName.Equals(tag, System.StringComparison.OrdinalIgnoreCase) == true)
            return root;
        foreach (var c in root.Children)
        {
            var hit = Find(c, tag);
            if (hit is not null) return hit;
        }
        return null;
    }

    [Fact]
    public async Task Bare_h1_is_bold_and_two_em()
    {
        var h1 = Find(await BuildAsync("<h1>Title</h1>"), "h1")!;
        Assert.Equal(32.0, h1.Style.ReadLengthPxOrDefault(PropertyId.FontSize, 16), precision: 1); // 2em × 16px
        Assert.Equal(700, h1.Style.ReadFontWeight());
    }

    [Fact]
    public async Task Author_font_size_and_weight_override_the_ua_default()
    {
        var h1 = Find(await BuildAsync(
            "<html><head><style>h1{font-size:8px;font-weight:400}</style></head><body><h1>x</h1></body></html>"),
            "h1")!;
        Assert.Equal(8.0, h1.Style.ReadLengthPxOrDefault(PropertyId.FontSize, 16), precision: 1);
        Assert.Equal(400, h1.Style.ReadFontWeight());
    }

    [Fact]
    public async Task Strong_is_bold_and_em_is_italic_without_author_css()
    {
        var root = await BuildAsync("<p><strong>a</strong><em>b</em></p>");
        Assert.Equal(700, Find(root, "strong")!.Style.ReadFontWeight());
        // font-style keyword ids: normal=0, italic=1, oblique=2.
        Assert.Equal(1, Find(root, "em")!.Style.ReadKeywordOrDefault(PropertyId.FontStyle, 0));
    }

    [Fact]
    public async Task Th_is_bold_and_centered()
    {
        var th = Find(await BuildAsync("<table><tr><th>H</th></tr></table>"), "th")!;
        Assert.Equal(700, th.Style.ReadFontWeight());
        // text-align keyword ids: start=0,end=1,left=2,right=3,center=4,justify=5.
        Assert.Equal(4, th.Style.ReadKeywordOrDefault(PropertyId.TextAlign, 0));
    }

    [Fact]
    public async Task Lists_have_default_left_padding()
    {
        var ul = Find(await BuildAsync("<ul><li>x</li></ul>"), "ul")!;
        Assert.Equal(40.0, ul.Style.ReadLengthPxOrZero(PropertyId.PaddingLeft), precision: 1);
    }

    [Fact]
    public void Bare_h1_renders_at_two_em_end_to_end()
    {
        // End-to-end through the facade: the h1 glyph run uses a 24pt font (2em × 16px × 0.75 pt/px),
        // distinct from the 12pt (16px) body text — proving the UA sheet reaches the paint stage.
        var pdf = HtmlPdf.ConvertDetailed("<h1>Title</h1><p>body</p>").Pdf;
        var s = Encoding.Latin1.GetString(pdf);
        var sizes = Regex.Matches(s, @"/[A-Za-z0-9]+ (\d+(?:\.\d+)?) Tf")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct().ToList();
        Assert.Contains(sizes, sz => System.Math.Abs(sz - 24.0) < 0.5);
        Assert.Contains(sizes, sz => System.Math.Abs(sz - 12.0) < 0.5);
    }
}
