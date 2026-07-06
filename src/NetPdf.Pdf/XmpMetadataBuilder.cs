// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Globalization;
using System.Text;

namespace NetPdf.Pdf;

/// <summary>
/// Builds the XMP (Extensible Metadata Platform, ISO 16684-1) packet emitted as the catalog
/// <c>/Metadata</c> stream. XMP mirrors the <c>/Info</c> dictionary in RDF/XML so modern readers,
/// search indexers, and archival tooling can read the document's title/author/description/keywords
/// without parsing the classic Info dictionary — and it is the prerequisite carrier for a later
/// PDF/A conformance pass.
/// </summary>
/// <remarks>
/// The packet is deterministic: only the caller-supplied fields are emitted, in a fixed order,
/// with a fixed xpacket id. No timestamps or random document IDs are injected here (dates ride the
/// optional <c>CreationDate</c>/<c>ModDate</c>), so the same metadata always yields the same bytes.
/// </remarks>
internal static class XmpMetadataBuilder
{
    // The canonical xpacket wrapper id (a fixed magic constant from the XMP specification).
    private const string PacketId = "W5M0MpCehiHzreSzNTczkc9d";

    /// <summary>
    /// Build the XMP packet bytes (UTF-8) for the given metadata, or <see langword="null"/> when no
    /// field is present (so a bare document emits no <c>/Metadata</c> stream). The
    /// <paramref name="producer"/> is always available but on its own is not enough to warrant an XMP
    /// packet — at least one of title/author/subject/keywords/creator/lang or a date must be set.
    /// Callers are expected to pass values already sanitized + length-capped (the packet only
    /// XML-escapes); the <paramref name="createDate"/>/<paramref name="modDate"/> are emitted as
    /// ISO 8601 <c>xmp:CreateDate</c>/<c>xmp:ModifyDate</c>, mirroring <c>/Info</c>'s dates.
    /// </summary>
    public static byte[]? Build(
        string? title, string? author, string? subject, string? keywords,
        string? creator, string? lang, string producer,
        DateTimeOffset? createDate = null, DateTimeOffset? modDate = null)
    {
        // Blank (whitespace-only) values count as unset — a caller that forgets to trim must not
        // trip an otherwise-empty packet into existence (and break the bare-document byte stability).
        var hasContent =
            !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(author) ||
            !string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(keywords) ||
            !string.IsNullOrWhiteSpace(creator) || !string.IsNullOrWhiteSpace(lang) ||
            createDate is not null || modDate is not null;
        if (!hasContent) return null;

        var sb = new StringBuilder(512);
        // The xpacket header's begin marker is the UTF-8 BOM (U+FEFF) — written as an explicit escape
        // so it can't be silently stripped/normalized by an editor (which would change the bytes).
        sb.Append("<?xpacket begin=\"\uFEFF\" id=\"").Append(PacketId).Append("\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\"");
        sb.Append(" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        sb.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\"");
        sb.Append(" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">\n");

        if (!string.IsNullOrWhiteSpace(title))
        {
            sb.Append("   <dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">")
              .Append(Escape(title)).Append("</rdf:li></rdf:Alt></dc:title>\n");
        }

        if (!string.IsNullOrWhiteSpace(author))
        {
            // dc:creator is an ordered array (rdf:Seq) of authors.
            sb.Append("   <dc:creator><rdf:Seq><rdf:li>")
              .Append(Escape(author)).Append("</rdf:li></rdf:Seq></dc:creator>\n");
        }

        if (!string.IsNullOrWhiteSpace(subject))
        {
            sb.Append("   <dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">")
              .Append(Escape(subject)).Append("</rdf:li></rdf:Alt></dc:description>\n");
        }

        if (!string.IsNullOrWhiteSpace(keywords))
        {
            // Keywords live in pdf:Keywords (the classic-Info mirror), a plain text property.
            sb.Append("   <pdf:Keywords>").Append(Escape(keywords)).Append("</pdf:Keywords>\n");
        }

        // Producer is always emitted alongside descriptive metadata (mirrors /Info /Producer).
        sb.Append("   <pdf:Producer>").Append(Escape(producer)).Append("</pdf:Producer>\n");

        if (!string.IsNullOrWhiteSpace(creator))
        {
            sb.Append("   <xmp:CreatorTool>").Append(Escape(creator)).Append("</xmp:CreatorTool>\n");
        }

        if (!string.IsNullOrWhiteSpace(lang))
        {
            sb.Append("   <dc:language><rdf:Bag><rdf:li>")
              .Append(Escape(lang)).Append("</rdf:li></rdf:Bag></dc:language>\n");
        }

        if (createDate is { } cd)
            sb.Append("   <xmp:CreateDate>").Append(FormatDate(cd)).Append("</xmp:CreateDate>\n");
        if (modDate is { } md)
            sb.Append("   <xmp:ModifyDate>").Append(FormatDate(md)).Append("</xmp:ModifyDate>\n");

        sb.Append("  </rdf:Description>\n");
        sb.Append(" </rdf:RDF>\n");
        sb.Append("</x:xmpmeta>\n");
        sb.Append("<?xpacket end=\"w\"?>");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>Format a timestamp as an XMP/ISO 8601 date (e.g. <c>2026-07-05T09:30:00+00:00</c>).
    /// The offset is preserved as <c>±HH:mm</c> (invariant), so the same input is always the same bytes.</summary>
    private static string FormatDate(DateTimeOffset value)
        => value.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture);

    /// <summary>XML-escape the five predefined entities so metadata text can't break the RDF/XML.</summary>
    private static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                default:
                    // A valid astral character arrives as a high+low surrogate PAIR — emit both.
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[i + 1]);
                        i++;
                        break;
                    }

                    // Otherwise keep only characters legal in an XML 1.0 document: drop C0 controls
                    // (except TAB/CR/LF), LONE surrogates, and the U+FFFE/U+FFFF noncharacters — any of
                    // which would make the RDF/XML packet unparseable.
                    if (IsXmlChar(c)) sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>True when <paramref name="c"/> (a single UTF-16 unit, surrogate pairs handled by the
    /// caller) is a legal XML 1.0 character: not a C0 control other than TAB/CR/LF, not a lone
    /// surrogate, and not U+FFFE/U+FFFF.</summary>
    private static bool IsXmlChar(char c)
    {
        if (c < 0x20) return c is '\t' or '\r' or '\n';
        if (c is >= '\uD800' and <= '\uDFFF') return false;   // lone surrogate
        if (c is '\uFFFE' or '\uFFFF') return false;          // U+FFFE / U+FFFF noncharacters
        return true;
    }
}
