// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Net;
using System.Net.Sockets;

namespace NetPdf;

/// <summary>
/// Per Phase B B-7 — the SSRF / SSRP gate every resource-loader implementation
/// is expected to call before issuing an outbound fetch. Combines two checks:
///
/// <list type="number">
///   <item><description><b>Scheme allowlist</b> — only schemes enabled on the
///   active <see cref="SecurityPolicy"/> are accepted. Default policy
///   (<see cref="SecurityPolicy.SafeDefault"/>) accepts <c>file:</c> only when
///   the path is under <see cref="HtmlPdfOptions.BaseUri"/> + <c>data:</c>
///   inline content; both <c>http:</c> and <c>https:</c> are off.</description></item>
///   <item><description><b>Private / loopback / link-local IP blocklist</b> for
///   HTTP(S) hosts. Catches the standard SSRF amplifier set: cloud-metadata
///   endpoints (<c>169.254.169.254</c> AWS / GCE / Azure / Alibaba; <c>fd00::/8</c>
///   IPv6 ULA), localhost (<c>127.0.0.0/8</c>, <c>::1</c>), private LAN
///   (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>, <c>fc00::/7</c>), link-local
///   (<c>169.254/16</c>, <c>fe80::/10</c>), multicast (<c>224.0.0.0/4</c>,
///   <c>ff00::/8</c>), and unspecified (<c>0.0.0.0</c>, <c>::</c>). Hosts that
///   resolve via DNS are NOT validated here — the loader must resolve to
///   IP, then re-validate to defend against DNS rebinding. <see cref="IsBlockedIp"/>
///   exposes the IP check standalone for that use.</description></item>
/// </list>
///
/// <para><b>Phase B contract.</b> NetPdf v1 ships with no default loader, so this
/// validator's only client today is unit tests. Phase 5 wires a real
/// <see cref="IResourceLoader"/> implementation that calls
/// <see cref="ValidateScheme"/> at intent time + <see cref="IsBlockedIp"/>
/// after DNS resolution, refusing the fetch on any "unsafe" verdict and
/// emitting <c>RES-LOAD-FAILED-001</c> with the rejection reason.</para>
///
/// <para><b>What this does NOT do.</b> Network policy (firewalls, egress
/// proxies, captive portals) belongs at the OS / fabric layer; this validator
/// addresses application-level SSRF defense only. It also does not handle
/// HTTP redirects — the loader must re-validate the redirect target host
/// after each hop.</para>
/// </summary>
public static class UriSafetyValidator
{
    /// <summary>Per PR #16 review user-recommendation #5 + Copilot review #8 —
    /// the result of <see cref="Validate"/> is no longer a binary
    /// safe/unsafe split. <c>file:</c> URIs under the <c>AllowFileSchemeUnderBaseUri</c>
    /// default need a base-path check that this validator does not perform
    /// (it has no <c>BaseUri</c> in scope), and treating the
    /// scheme-allowlist verdict as a complete answer led to easy misuse.
    /// The three-state <see cref="SafetyOutcome"/> makes the contract
    /// explicit at the call site.</summary>
    public enum SafetyOutcome
    {
        /// <summary>Scheme + host both pass policy. The loader may proceed
        /// with the fetch.</summary>
        Safe = 0,
        /// <summary>Scheme or host violates policy. The loader must reject
        /// the fetch + emit <c>RES-SECURITY-DENIED-001</c>.</summary>
        Unsafe = 1,
        /// <summary>Scheme is allowed but a follow-up check is required
        /// before the fetch is safe. For <c>file:</c> URIs this means the
        /// loader must verify the resolved path lies under
        /// <c>HtmlPdfOptions.BaseUri</c>'s directory subtree (the
        /// <c>AllowFileSchemeUnderBaseUri</c> contract). The validator
        /// cannot perform this check itself because it does not know the
        /// active <c>BaseUri</c>.</summary>
        RequiresBasePathCheck = 2,
    }

    /// <summary>Result of a validation check.</summary>
    /// <param name="Outcome">The three-state safety verdict.</param>
    /// <param name="Reason">When <see cref="Outcome"/> is
    /// <see cref="SafetyOutcome.Unsafe"/>, a human-readable reason
    /// suitable for diagnostic emission. For
    /// <see cref="SafetyOutcome.RequiresBasePathCheck"/>, names the
    /// follow-up check the loader must perform. <see langword="null"/>
    /// when <see cref="Outcome"/> is <see cref="SafetyOutcome.Safe"/>.</param>
    public readonly record struct Verdict(SafetyOutcome Outcome, string? Reason)
    {
        /// <summary>Convenience: <see langword="true"/> only when
        /// <see cref="Outcome"/> is <see cref="SafetyOutcome.Safe"/>.
        /// Callers that need the three-state distinction should switch
        /// on <see cref="Outcome"/> directly.</summary>
        public bool IsSafe => Outcome == SafetyOutcome.Safe;
    }

    /// <summary>Validate <paramref name="uri"/>'s scheme + host against
    /// <paramref name="policy"/>. For host validation, the URI host is parsed
    /// as an IP literal where applicable; symbolic hostnames pass the host
    /// check (the loader is expected to call <see cref="IsBlockedIp"/> after
    /// DNS resolution to defend against DNS rebinding).</summary>
    public static Verdict Validate(Uri uri, SecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(policy);

        var schemeVerdict = ValidateScheme(uri, policy);
        if (schemeVerdict.Outcome != SafetyOutcome.Safe) return schemeVerdict;

        // Per PR #16 Copilot review #8 — IP blocklist + AllowedHosts apply
        // ONLY to fetch-routing schemes (http, https). For file: + data:
        // schemes the host portion either doesn't exist (data:) or is
        // not network-routed (file://localhost/etc/x is a path semantic,
        // not a network host). Applying the blocklist there yielded
        // surprising rejections for legitimate inputs.
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is "http" or "https")
        {
            // For HTTP(S), if the host parses as an IP literal, run the
            // blocklist check now. Symbolic hosts defer to post-DNS
            // validation.
            if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                if (IPAddress.TryParse(uri.Host, out var ip) && IsBlockedIp(ip, out var why))
                {
                    return new Verdict(SafetyOutcome.Unsafe,
                        $"host '{uri.Host}' is in the {why} blocklist");
                }
            }

            // Host allowlist (when configured). Wildcards: leading "*." matches
            // any single subdomain. The check is case-insensitive per host
            // grammar.
            if (policy.AllowedHosts is { Count: > 0 } allowedHosts
                && !MatchesAllowedHost(uri.Host, allowedHosts))
            {
                return new Verdict(SafetyOutcome.Unsafe,
                    $"host '{uri.Host}' is not in the allowed-host list");
            }
        }

        return schemeVerdict;
    }

    /// <summary>Scheme-only check (no host inspection). Useful when the caller
    /// has not yet resolved the URL or wants to short-circuit on scheme
    /// rejection before paying DNS cost.</summary>
    public static Verdict ValidateScheme(Uri uri, SecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(policy);

        switch (uri.Scheme.ToLowerInvariant())
        {
            case "http":
                return policy.AllowHttpScheme
                    ? new Verdict(SafetyOutcome.Safe, null)
                    : new Verdict(SafetyOutcome.Unsafe, "http: scheme is disabled by SecurityPolicy");
            case "https":
                return policy.AllowHttpsScheme
                    ? new Verdict(SafetyOutcome.Safe, null)
                    : new Verdict(SafetyOutcome.Unsafe, "https: scheme is disabled by SecurityPolicy");
            case "file":
                if (policy.AllowFileScheme)
                    return new Verdict(SafetyOutcome.Safe, null);
                if (policy.AllowFileSchemeUnderBaseUri)
                {
                    // Per PR #16 review user-recommendation #5 — the under-baseuri
                    // gate IS the actual safety check, but the validator can't
                    // perform it (no BaseUri in scope). Return the new
                    // RequiresBasePathCheck outcome so the caller knows it
                    // MUST perform the path-prefix check before the fetch is
                    // safe. The previous Verdict(true) result was easy to
                    // misuse as a complete verdict.
                    return new Verdict(SafetyOutcome.RequiresBasePathCheck,
                        "file: requires loader to verify the resolved path is under HtmlPdfOptions.BaseUri's subtree");
                }
                return new Verdict(SafetyOutcome.Unsafe, "file: scheme is disabled by SecurityPolicy");
            case "data":
                return policy.AllowDataUri
                    ? new Verdict(SafetyOutcome.Safe, null)
                    : new Verdict(SafetyOutcome.Unsafe, "data: URIs are disabled by SecurityPolicy");
            default:
                return new Verdict(SafetyOutcome.Unsafe, $"scheme '{uri.Scheme}:' is not in the allowed set");
        }
    }

    /// <summary>Per Phase C C-6 — validate an HTTP redirect target. The
    /// loader calls this for every <c>Location:</c> header before issuing
    /// the next-hop fetch. Three checks layered on top of
    /// <see cref="Validate"/>:
    /// <list type="number">
    ///   <item><description>The redirect target itself passes scheme + host
    ///   policy (delegates to <see cref="Validate"/>) — re-checks the IP
    ///   blocklist on the new host so an open-redirect → SSRF chain
    ///   (initial URL safe, redirect target on <c>169.254.169.254</c>) is
    ///   blocked at the moment of redirection.</description></item>
    ///   <item><description>The hop count is below
    ///   <see cref="SecurityPolicy.MaxRedirectHops"/>. Each call records
    ///   one hop; once exhausted, return <see cref="SafetyOutcome.Unsafe"/>
    ///   with a redirect-chain reason.</description></item>
    ///   <item><description>Cross-scheme downgrade rejection: <c>https</c>
    ///   → <c>http</c> redirects are blocked (typical browser behavior +
    ///   defense against MITM-induced downgrade). Same-scheme +
    ///   <c>http</c> → <c>https</c> upgrades pass.</description></item>
    /// </list>
    /// </summary>
    public static Verdict ValidateRedirect(Uri origin, Uri redirectTarget, SecurityPolicy policy, int hopsAlreadyFollowed)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(redirectTarget);
        ArgumentNullException.ThrowIfNull(policy);

        if (hopsAlreadyFollowed >= policy.MaxRedirectHops)
        {
            return new Verdict(SafetyOutcome.Unsafe,
                $"redirect chain exceeded the {policy.MaxRedirectHops}-hop cap");
        }

        // Cross-scheme downgrade. Allow same-scheme + http → https upgrades.
        var originScheme = origin.Scheme.ToLowerInvariant();
        var targetScheme = redirectTarget.Scheme.ToLowerInvariant();
        if (originScheme == "https" && targetScheme == "http")
        {
            return new Verdict(SafetyOutcome.Unsafe,
                "redirect downgrades https → http; rejected to defend against MITM-induced downgrade");
        }

        // Run the standard policy check on the target. This is where the
        // open-redirect → SSRF chain gets caught — the target's host is
        // re-validated against the IP blocklist + AllowedHosts.
        return Validate(redirectTarget, policy);
    }

    /// <summary>True when <paramref name="ip"/> falls in any of the
    /// well-known SSRF amplifier ranges (loopback, private, link-local,
    /// cloud-metadata, multicast, unspecified). The <paramref name="reason"/>
    /// out parameter names the matching range so the caller can produce a
    /// specific diagnostic.</summary>
    public static bool IsBlockedIp(IPAddress ip, out string reason)
    {
        ArgumentNullException.ThrowIfNull(ip);

        if (IPAddress.IsLoopback(ip)) { reason = "loopback"; return true; }
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedV4(ip, out reason);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsBlockedV6(ip, out reason);
        }
        reason = "unsupported-address-family";
        return true;
    }

    private static bool IsBlockedV4(IPAddress ip, out string reason)
    {
        var bytes = ip.GetAddressBytes();
        // 0.0.0.0/8 — "this network" / unspecified.
        if (bytes[0] == 0) { reason = "unspecified"; return true; }
        // 10.0.0.0/8 — RFC 1918 private.
        if (bytes[0] == 10) { reason = "private"; return true; }
        // 100.64.0.0/10 — RFC 6598 carrier-grade NAT.
        if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) { reason = "carrier-grade-nat"; return true; }
        // 127.0.0.0/8 — loopback (covered by IsLoopback but defensive).
        if (bytes[0] == 127) { reason = "loopback"; return true; }
        // 169.254.0.0/16 — RFC 3927 link-local + cloud-metadata
        // (169.254.169.254 is AWS / GCE / Azure / Alibaba IMDS).
        if (bytes[0] == 169 && bytes[1] == 254) { reason = "link-local-or-metadata"; return true; }
        // 172.16.0.0/12 — RFC 1918 private.
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) { reason = "private"; return true; }
        // 192.0.0.0/24 — IETF assignments / well-known.
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0) { reason = "ietf-reserved"; return true; }
        // 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 — TEST-NET.
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2) { reason = "test-net"; return true; }
        if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) { reason = "test-net"; return true; }
        if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) { reason = "test-net"; return true; }
        // 192.168.0.0/16 — RFC 1918 private.
        if (bytes[0] == 192 && bytes[1] == 168) { reason = "private"; return true; }
        // 198.18.0.0/15 — RFC 2544 benchmark.
        if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19)) { reason = "benchmark"; return true; }
        // 224.0.0.0/4 — multicast.
        if (bytes[0] >= 224 && bytes[0] <= 239) { reason = "multicast"; return true; }
        // 240.0.0.0/4 — reserved (includes 255.255.255.255 broadcast).
        if (bytes[0] >= 240) { reason = "reserved"; return true; }

        reason = "";
        return false;
    }

    private static bool IsBlockedV6(IPAddress ip, out string reason)
    {
        var bytes = ip.GetAddressBytes();
        // :: — unspecified.
        var allZero = true;
        for (var i = 0; i < bytes.Length; i++) { if (bytes[i] != 0) { allZero = false; break; } }
        if (allZero) { reason = "unspecified"; return true; }
        // ::1 — loopback (covered by IsLoopback above but defensive).
        if (IPAddress.IsLoopback(ip)) { reason = "loopback"; return true; }
        // ::ffff:0:0/96 — IPv4-mapped; recurse into V4 check on the trailing 4 bytes.
        if (IsIPv4MappedV6(bytes))
        {
            var v4 = new IPAddress(new[] { bytes[12], bytes[13], bytes[14], bytes[15] });
            if (IPAddress.IsLoopback(v4)) { reason = "v4-mapped-loopback"; return true; }
            if (IsBlockedV4(v4, out var v4Reason))
            {
                reason = "v4-mapped-" + v4Reason;
                return true;
            }
        }
        // fc00::/7 — Unique Local Addresses (RFC 4193).
        if ((bytes[0] & 0xFE) == 0xFC) { reason = "unique-local"; return true; }
        // fe80::/10 — link-local.
        if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80) { reason = "link-local"; return true; }
        // ff00::/8 — multicast.
        if (bytes[0] == 0xFF) { reason = "multicast"; return true; }

        reason = "";
        return false;
    }

    private static bool IsIPv4MappedV6(byte[] bytes)
    {
        // ::ffff:a.b.c.d — bytes[0..9] = 0, bytes[10..11] = 0xff, 0xff.
        for (var i = 0; i < 10; i++) { if (bytes[i] != 0) return false; }
        return bytes[10] == 0xFF && bytes[11] == 0xFF;
    }

    private static bool MatchesAllowedHost(string host, IReadOnlyList<string> allowed)
    {
        foreach (var pattern in allowed)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            // Wildcard form: "*.example.com" matches "foo.example.com" but
            // not "example.com" itself (single-label wildcard, not greedy).
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..]; // ".example.com"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && host.Length > suffix.Length
                    && !host[..^suffix.Length].Contains('.'))
                {
                    return true;
                }
                continue;
            }
            if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
