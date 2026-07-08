// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// WP-7 — two CSS-pipeline defects. RC-13: AngleSharp expands a shorthand with a <c>var()</c> value
/// (e.g. <c>background: var(--tint)</c>) into ~10 EMPTY-valued longhands; forwarding those let a later
/// empty <c>background-color</c> win the cascade over the preprocessor-recovered value, painting the
/// element transparent (missing zebra stripes). RC-12: <c>text-transform</c> was registered but consumed
/// nowhere, so <c>uppercase</c>/<c>lowercase</c>/<c>capitalize</c> rendered the original case.
/// </summary>
public sealed class CssPipelineWp7Tests
{
    private static int BgFills(string html, string colorRegex)
    {
        var res = HtmlPdf.ConvertDetailed(html, new HtmlPdfOptions { PrintBackgrounds = true });
        return Regex.Matches(Encoding.Latin1.GetString(res.Pdf), colorRegex).Count;
    }

    // ---- RC-13: background via var() paints (was clobbered by empty longhands) ----

    [Fact]
    public void Background_via_var_is_not_clobbered_by_empty_longhands()
    {
        // #ffeeee ≈ 1 0.933 0.933. Two even rows → two zebra fills, same as a literal color.
        const string tail =
            "table{border-collapse:collapse}td{padding:4px}tbody tr:nth-child(even){background:%BG%}"
            + "</style></head><body><table><tbody>"
            + "<tr><td>1</td></tr><tr><td>2</td></tr><tr><td>3</td></tr><tr><td>4</td></tr>"
            + "</tbody></table></body></html>";
        var viaVar = "<!doctype html><html><head><style>:root{--tint:#ffeeee}" + tail.Replace("%BG%", "var(--tint)");
        var viaLiteral = "<!doctype html><html><head><style>" + tail.Replace("%BG%", "#ffeeee");

        var literalFills = BgFills(viaLiteral, @"1 0\.93\d* 0\.93\d* rg");
        Assert.Equal(2, literalFills);                       // control
        Assert.Equal(literalFills, BgFills(viaVar, @"1 0\.93\d* 0\.93\d* rg"));
    }

    // ---- RC-12: text-transform ----

    private static byte[] Para(string css, string text) =>
        HtmlPdf.Convert($"<!doctype html><html><head><style>p{{{css}}}</style></head><body><p>{text}</p></body></html>");

    [Fact]
    public void Text_transform_uppercase_renders_like_literal_upper_case()
    {
        // uppercase("hello world") must produce the same bytes as the literal "HELLO WORLD".
        Assert.True(Para("text-transform:uppercase", "hello world").SequenceEqual(Para("", "HELLO WORLD")));
        // ...and must NOT equal the untransformed lower-case rendering.
        Assert.False(Para("text-transform:uppercase", "hello world").SequenceEqual(Para("", "hello world")));
    }

    [Fact]
    public void Text_transform_lowercase_renders_like_literal_lower_case()
    {
        Assert.True(Para("text-transform:lowercase", "HELLO WORLD").SequenceEqual(Para("", "hello world")));
    }

    [Fact]
    public void Text_transform_capitalize_upper_cases_each_word_start()
    {
        Assert.True(Para("text-transform:capitalize", "hello world").SequenceEqual(Para("", "Hello World")));
    }
}
