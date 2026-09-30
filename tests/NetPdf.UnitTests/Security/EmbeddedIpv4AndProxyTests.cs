// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Net;
using NetPdf;
using Xunit;

namespace NetPdf.UnitTests.Security;

/// <summary>
/// SSRF blocklist gaps found by probing <see cref="UriSafetyValidator"/> with IPv6 forms that carry an
/// IPv4 address. Before the fix every address in <see cref="Ipv6FormsOfBlockedIpv4"/> validated as SAFE,
/// so an attacker who could get http(s) fetching enabled could reach an internal IPv4 target by writing it
/// as an IPv6 literal — the IPv4 blocklist never saw it.
///
/// <para>The practical one is NAT64: on an IPv6-only cloud subnet with a NAT64 gateway,
/// <c>64:ff9b::a00:1</c> really reaches the private address 10.0.0.1.</para>
/// </summary>
public sealed class EmbeddedIpv4AndProxyTests
{
    public static TheoryData<string, string> Ipv6FormsOfBlockedIpv4 => new()
    {
        // NAT64 well-known prefix (RFC 6052): embedded v4 in the low 32 bits.
        { "64:ff9b::a9fe:a9fe", "nat64-link-local-or-metadata" }, // 169.254.169.254
        { "64:ff9b::a00:1", "nat64-private" },                    // 10.0.0.1
        { "64:ff9b::7f00:1", "nat64-loopback" },                  // 127.0.0.1
        // 6to4 (RFC 3056): embedded v4 in bytes 2..5.
        { "2002:a9fe:a9fe::", "6to4-link-local-or-metadata" },
        { "2002:c0a8:101::1", "6to4-private" },                   // 192.168.1.1
        // IPv4-compatible (deprecated) and IPv4-translated forms.
        { "::127.0.0.1", "v4-compatible-loopback" },
        { "::a9fe:a9fe", "v4-compatible-link-local-or-metadata" },
        { "::ffff:0:7f00:1", "v4-translated-loopback" },
        // Ranges blocked outright: no trustworthy real destination.
        { "64:ff9b:1::a00:1", "nat64-local-use" },
        { "2001:0:4136:e378:8000:63bf:80ff:fffe", "teredo" },
        { "fec0::1", "site-local" },
        { "100::1", "discard-only" },
    };

    [Theory]
    [MemberData(nameof(Ipv6FormsOfBlockedIpv4))]
    public void Ipv6_forms_of_internal_addresses_are_blocked(string address, string expectedReason)
    {
        Assert.True(UriSafetyValidator.IsBlockedIp(IPAddress.Parse(address), out var reason),
            $"{address} must be blocked");
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("http://[64:ff9b::a9fe:a9fe]/latest/meta-data/")]
    [InlineData("http://[2002:a9fe:a9fe::]/")]
    [InlineData("http://[::127.0.0.1]/")]
    public void Ipv6_literal_urls_to_internal_targets_are_rejected_before_any_fetch(string url)
    {
        var policy = new SecurityPolicy { AllowHttpScheme = true, AllowHttpsScheme = true };

        var verdict = UriSafetyValidator.Validate(new Uri(url), policy);

        Assert.False(verdict.IsSafe, $"{url} must be rejected at intent time; reason: {verdict.Reason}");
    }

    [Theory]
    // The embedding forms are only blocked when the IPv4 INSIDE them is internal: a public address reached
    // through NAT64 or 6to4 is a legitimate path on an IPv6-only network and must keep working.
    [InlineData("64:ff9b::808:808")]          // NAT64 → 8.8.8.8
    [InlineData("2002:808:808::1")]           // 6to4 → 8.8.8.8
    // Ordinary global IPv6 that merely STARTS like one of the special prefixes must not be caught.
    [InlineData("2001:4860:4860::8888")]      // 2001::/16 but not Teredo's 2001:0::/32
    [InlineData("2606:4700:4700::1111")]
    public void Public_destinations_are_still_allowed(string address)
    {
        Assert.False(UriSafetyValidator.IsBlockedIp(IPAddress.Parse(address), out var reason),
            $"{address} is public and must be allowed; wrongly blocked as '{reason}'");
    }

    [Fact]
    public void Http_loader_never_routes_through_an_ambient_proxy()
    {
        // SocketsHttpHandler honors HTTP_PROXY / HTTPS_PROXY by default. The loader's DNS-rebinding defense
        // pins every connect to the IP it just validated; through a proxy that pin targets the proxy's
        // endpoint instead, and an https CONNECT lets the proxy resolve the hostname itself, outside the
        // blocklist. So the proxy must be off, whatever the environment says.
        using var handler = SafeHttpResourceLoader.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect); // redirects are walked + re-validated manually
        Assert.NotNull(handler.ConnectCallback); // the connect pin itself
    }
}
