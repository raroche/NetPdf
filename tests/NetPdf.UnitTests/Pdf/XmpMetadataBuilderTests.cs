// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using NetPdf.Pdf;
using Xunit;

namespace NetPdf.UnitTests.Pdf;

/// <summary>
/// The XMP packet builder must always emit well-formed RDF/XML — even for hostile metadata that
/// contains characters illegal in XML 1.0 (lone surrogates, the U+FFFE/U+FFFF noncharacters). Valid
/// astral characters (emoji, supplementary-plane text) must survive intact.
/// </summary>
public sealed class XmpMetadataBuilderTests
{
    private static XElement ParseXmpRoot(byte[] packet)
    {
        var xml = Encoding.UTF8.GetString(packet);
        var start = xml.IndexOf("<x:xmpmeta", StringComparison.Ordinal);
        var endTag = "</x:xmpmeta>";
        var end = xml.IndexOf(endTag, StringComparison.Ordinal) + endTag.Length;
        // Parse only the XML element (drop the xpacket processing-instruction wrapper); throws if the
        // builder left any XML-1.0-illegal character in the content.
        return XElement.Parse(xml.Substring(start, end - start));
    }

    [Fact]
    public void Packet_is_valid_xml_and_drops_illegal_characters_but_keeps_astral_text()
    {
        // "Ok" + lone high surrogate + "Lone" + 😀 (valid astral pair) + "Astral" + U+FFFF + "Nonchar".
        var hostileTitle = "Ok\uD800Lone\uD83D\uDE00Astral\uFFFFNonchar";
        var packet = XmpMetadataBuilder.Build(
            hostileTitle, author: null, subject: null, keywords: null,
            creator: null, lang: null, producer: "NetPdf");

        Assert.NotNull(packet);
        var root = ParseXmpRoot(packet!);              // would throw on invalid XML

        var text = root.ToString();
        Assert.Contains("\uD83D\uDE00", text);            // the valid astral character survives
        Assert.DoesNotContain('\uD800', text);         // the lone surrogate is gone
        Assert.DoesNotContain('\uFFFF', text);        // the U+FFFF noncharacter is gone
        Assert.Contains("Ok", text);
        Assert.Contains("Nonchar", text);
    }

    [Fact]
    public void Xml_entities_are_escaped()
    {
        var packet = XmpMetadataBuilder.Build(
            "A & B <x> \"q\" 'a'", author: null, subject: null, keywords: null,
            creator: null, lang: null, producer: "NetPdf");

        // Parses (proves escaping worked) and round-trips the literal text.
        var root = ParseXmpRoot(packet!);
        var title = root.Descendants().First(e => e.Name.LocalName == "li").Value;
        Assert.Equal("A & B <x> \"q\" 'a'", title);
    }
}
