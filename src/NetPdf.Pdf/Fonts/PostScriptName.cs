// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace NetPdf.Pdf.Fonts;

/// <summary>
/// Sanitizes a font name into a PostScript-safe string suitable for use inside a PDF
/// <see cref="NetPdf.Pdf.Objects.PdfName"/> (BaseFont / FontName entries). PostScript
/// names are 7-bit ASCII without whitespace or PostScript-special characters; raw font
/// family names — especially for international fonts where <c>PostScriptName</c> is
/// missing — can contain arbitrary Unicode that would either need hex-escaping in the
/// PDF name or, worse, render as garbled bytes in viewers that don't honor the escape.
/// </summary>
internal static class PostScriptName
{
    /// <summary>Cap PostScript names at 63 characters per the spec (PLRM 5.2 — "Names").</summary>
    public const int MaxLength = 63;

    /// <summary>
    /// Produce a deterministic, PostScript-safe rendition of <paramref name="raw"/>:
    /// keep ASCII letters / digits / <c>-</c> / <c>+</c> / <c>_</c>, drop everything
    /// else. When sanitization leaves an empty or letter-less result (e.g. a CJK-only
    /// family name), fall back to <c>"Font" + 8 hex of SHA-256(UTF-8(raw))</c> so distinct
    /// inputs still produce distinct outputs.
    /// </summary>
    public static string Sanitize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var sb = new StringBuilder(Math.Min(raw.Length, MaxLength));
        var hasAlpha = false;
        foreach (var c in raw)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                sb.Append(c);
                hasAlpha = true;
            }
            else if (c is (>= '0' and <= '9') or '-' or '+' or '_')
            {
                sb.Append(c);
            }
            // Drop everything else — whitespace, PostScript reserved chars, non-ASCII.

            if (sb.Length >= MaxLength)
            {
                break;
            }
        }

        if (!hasAlpha)
        {
            return "Font" + ShortHash(raw);
        }
        return sb.ToString();
    }

    private static string ShortHash(string raw)
    {
        Span<byte> hash = stackalloc byte[32];
        var utf8 = Encoding.UTF8.GetBytes(raw);
        SHA256.HashData(utf8, hash);
        // Take the first 4 bytes as 8 uppercase hex chars.
        var prefix = BinaryPrimitives.ReadUInt32BigEndian(hash);
        return prefix.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
