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

        if (isBase64)
        {
            // Base64 may arrive percent-encoded ('+' as %2B etc.) and with literal whitespace —
            // unescape, then strip whitespace per RFC 2397 §3's forgiving readers.
            var unescaped = Uri.UnescapeDataString(payload.ToString());
            var compact = unescaped.Replace(" ", "").Replace("\t", "").Replace("\n", "").Replace("\r", "");
            var buffer = new byte[Base64.GetMaxDecodedFromUtf8Length(compact.Length)];
            if (!Convert.TryFromBase64String(compact, buffer, out var written))
            {
                reason = "data: URI base64 payload failed to decode";
                return false;
            }
            bytes = buffer.AsSpan(0, written).ToArray();
        }
        else
        {
            // Percent-encoded textual payload → raw bytes (Latin-1 round-trip of the unescaped
            // string; binary data is conventionally base64, this branch serves textual payloads).
            var unescaped = Uri.UnescapeDataString(payload.ToString());
            bytes = System.Text.Encoding.UTF8.GetBytes(unescaped);
        }

        if (bytes.Length == 0)
        {
            reason = "data: URI decoded to an empty payload";
            return false;
        }
        return true;
    }
}
