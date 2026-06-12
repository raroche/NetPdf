// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Text;

namespace NetPdf;

/// <summary>
/// Minimal RFC 2397 <c>data:</c> URI decoder (img-pipeline cycle) —
/// <c>data:[&lt;mediatype&gt;][;base64],&lt;data&gt;</c>. A <c>data:</c> URI carries its bytes
/// inline, so it needs no <see cref="IResourceLoader"/>; <see cref="SafeResourceLoader"/> decodes
/// it directly (the scheme is allowed by <see cref="SecurityPolicy.SafeDefault"/> and the decoded
/// bytes still flow through the per-resource size cap, the MIME allowlist and the per-render byte
/// budget — an inline payload is attacker-reachable exactly like a fetched one).
/// </summary>
internal static class DataUriParser
{
    /// <summary>Decode <paramref name="uri"/>'s inline payload. Returns <see langword="false"/>
    /// (with a human-readable <paramref name="reason"/>) for a malformed URI — no comma, invalid
    /// base64, or a payload whose ENCODED form already exceeds <paramref name="maxBytes"/> (a
    /// cheap pre-decode cap so a hostile huge data: URI is rejected before allocation).</summary>
    public static bool TryDecode(
        Uri uri, long maxBytes, out byte[] bytes, out string? mimeType, out string reason)
    {
        bytes = Array.Empty<byte>();
        mimeType = null;
        reason = string.Empty;

        // Uri unescapes nothing for the data scheme via OriginalString — parse the raw text.
        // (AbsoluteUri/LocalPath re-encode; the payload must be read as authored.)
        var raw = uri.OriginalString;
        const string prefix = "data:";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            reason = "not a data: URI";
            return false;
        }
        var comma = raw.IndexOf(',');
        if (comma < 0)
        {
            reason = "malformed data: URI (no comma separating metadata from payload)";
            return false;
        }

        // Metadata: [<mediatype>][;charset=...][;base64]
        var meta = raw.AsSpan(prefix.Length, comma - prefix.Length);
        var isBase64 = false;
        while (true)
        {
            var semi = meta.LastIndexOf(';');
            if (semi < 0) break;
            var part = meta[(semi + 1)..].Trim();
            if (part.Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
                meta = meta[..semi];
                continue;
            }
            break; // charset=… or other parameters — keep as part of the mediatype tail.
        }
        // The mediatype is everything before the first ';' (parameters are irrelevant to the
        // MIME allowlist, which strips them anyway).
        var mediatype = meta;
        var firstSemi = mediatype.IndexOf(';');
        if (firstSemi >= 0) mediatype = mediatype[..firstSemi];
        mimeType = mediatype.IsWhiteSpace() || mediatype.IsEmpty
            ? null
            : mediatype.Trim().ToString();

        var payload = raw.AsSpan(comma + 1);
        // Pre-decode size gate: base64 expands ~4/3, percent-encoding ≥ 1 byte per char — the
        // ENCODED length is an upper bound proxy. Reject before any allocation.
        if (payload.Length > maxBytes * 4 / 3 + 4)
        {
            reason = $"data: URI payload ({payload.Length} encoded chars) exceeds the per-resource cap ({maxBytes} bytes)";
            return false;
        }

        // RAW percent-byte decode (PR #166 review P1 + P3) — `%XX` → the byte, everything else
        // its own byte(s). This is the RFC 2397/3986 octet semantics, so a percent-encoded
        // BINARY non-base64 payload round-trips exactly (the pre-fix UnescapeDataString +
        // UTF-8 re-encode corrupted bytes ≥ 0x80), and the no-throw contract holds by
        // construction: a malformed escape returns false with a reason instead of letting a
        // BCL exception escape SafeResourceLoader.FetchAsync.
        if (!TryPercentDecodeToBytes(payload, out var decoded, out reason))
        {
            return false;
        }

        if (isBase64)
        {
            // Strip whitespace per RFC 2397 §3's forgiving readers, then decode the (ASCII)
            // base64 alphabet from the percent-decoded octets.
            var compact = new byte[decoded.Length];
            var n = 0;
            foreach (var b in decoded)
            {
                if (b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r') continue;
                compact[n++] = b;
            }
            var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(n)];
            var status = Base64.DecodeFromUtf8(
                compact.AsSpan(0, n), buffer, out _, out var written);
            if (status != System.Buffers.OperationStatus.Done)
            {
                reason = "data: URI base64 payload failed to decode";
                return false;
            }
            bytes = buffer.AsSpan(0, written).ToArray();
        }
        else
        {
            bytes = decoded;
        }

        if (bytes.Length == 0)
        {
            reason = "data: URI decoded to an empty payload";
            return false;
        }
        return true;
    }

    /// <summary>Percent-decode <paramref name="payload"/> to raw octets: <c>%XX</c> → the byte;
    /// an ASCII char → its byte; a non-ASCII char (lenient — a data: URI authored directly in
    /// HTML can carry one) → its UTF-8 bytes. A malformed escape (<c>%</c> without two hex
    /// digits) FAILS with a reason — never throws (PR #166 review P1).</summary>
    private static bool TryPercentDecodeToBytes(
        ReadOnlySpan<char> payload, out byte[] bytes, out string reason)
    {
        bytes = Array.Empty<byte>();
        reason = string.Empty;
        var buffer = new byte[System.Text.Encoding.UTF8.GetMaxByteCount(payload.Length)];
        var n = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '%')
            {
                if (i + 2 >= payload.Length
                    || !TryHexNibble(payload[i + 1], out var hi)
                    || !TryHexNibble(payload[i + 2], out var lo))
                {
                    reason = $"data: URI payload has a malformed percent escape at offset {i}";
                    return false;
                }
                buffer[n++] = (byte)((hi << 4) | lo);
                i += 2;
            }
            else if (c <= 0x7F)
            {
                buffer[n++] = (byte)c;
            }
            else
            {
                // Lenient non-ASCII: encode the (possibly surrogate-paired) char as UTF-8.
                var charSpan = i + 1 < payload.Length && char.IsHighSurrogate(c)
                    ? payload.Slice(i, 2)
                    : payload.Slice(i, 1);
                n += System.Text.Encoding.UTF8.GetBytes(charSpan, buffer.AsSpan(n));
                if (charSpan.Length == 2) i++;
            }
        }
        bytes = buffer.AsSpan(0, n).ToArray();
        return true;
    }

    private static bool TryHexNibble(char c, out int value)
    {
        switch (c)
        {
            case >= '0' and <= '9': value = c - '0'; return true;
            case >= 'a' and <= 'f': value = c - 'a' + 10; return true;
            case >= 'A' and <= 'F': value = c - 'A' + 10; return true;
            default: value = 0; return false;
        }
    }
}
