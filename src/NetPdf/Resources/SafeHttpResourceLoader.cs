// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetPdf;

/// <summary>
/// Per PR #18 review #1 — built-in HTTP / HTTPS resource loader that
/// closes the SSRF gap left by the bare <see cref="IResourceLoader"/>
/// contract. The pre-fix flow:
///
/// <code>
/// SafeResourceLoader → UriSafetyValidator.Validate (scheme + IP if literal)
///                    → user IResourceLoader (HttpClient default)
///                                ↓
///                       DNS resolution + auto-follow redirects
///                       (NEITHER seen by SafeResourceLoader)
/// </code>
///
/// <para>Symbolic hosts (e.g., <c>attacker.com</c>) deferred IP validation
/// until after DNS, but the user loader was a black box: it could resolve
/// to a private / loopback / cloud-metadata IP + connect. Auto-redirect
/// could step through hostile <c>Location:</c> headers without the
/// wrapper's <see cref="UriSafetyValidator.ValidateRedirect"/> running.</para>
///
/// <para><b>What this loader does instead.</b>
/// <list type="number">
///   <item>Resolve the host via <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>;
///   immediately reject every resolved address that hits the
///   <see cref="UriSafetyValidator.IsBlockedIp"/> blocklist (loopback,
///   private, link-local incl. AWS metadata <c>169.254.169.254</c>,
///   IPv6 ULA, IPv4-mapped, etc.).</item>
///   <item>Use <see cref="SocketsHttpHandler.ConnectCallback"/> to
///   intercept the connect step + use the pre-validated IP — defeats
///   the DNS-rebinding attack (resolve, validate, then re-resolve to a
///   different IP at connect time) by pinning the IP from the validation
///   pass.</item>
///   <item>Set <see cref="SocketsHttpHandler.AllowAutoRedirect"/> = false.
///   On a 30x response, manually walk the <c>Location:</c> header through
///   <see cref="UriSafetyValidator.ValidateRedirect"/> + recurse with the
///   updated hop count.</item>
///   <item>Cap response bytes at the configured per-resource limit so a
///   malicious server can't stream gigabytes.</item>
/// </list>
/// </para>
///
/// <para><b>NetPdf v1 ships no default loader.</b> This class is a
/// reference implementation that consumers can opt into via:
/// <code>options.ResourceLoader = new SafeHttpResourceLoader();</code>
/// Phase 5's wireup may wire it automatically when an HTTP-fetching
/// surface is requested.</para>
/// </summary>
public sealed class SafeHttpResourceLoader : IResourceLoader, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SecurityPolicy _policy;

    /// <summary>Construct with the active <see cref="SecurityPolicy"/>.
    /// The loader captures the policy at construction so the configured
    /// IP blocklist + redirect cap + per-resource size cap apply
    /// uniformly across every fetch issued by this loader instance.</summary>
    public SafeHttpResourceLoader(SecurityPolicy? policy = null)
    {
        _policy = policy ?? SecurityPolicy.SafeDefault;
        var handler = new SocketsHttpHandler
        {
            // Per PR #18 review #1 — disable auto-redirect; we walk
            // the chain manually + validate each hop.
            AllowAutoRedirect = false,
            // ConnectCallback intercepts the TCP connect so we can
            // validate the resolved IP before any bytes hit the wire.
            ConnectCallback = ValidatedConnect,
            // Tighter limits than HttpClient's defaults to keep adversarial
            // servers from holding connections open.
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
        };
        _httpClient = new HttpClient(handler);
    }

    /// <summary>Per Phase 5 contract — fetch a resource with full
    /// SSRF / redirect / size enforcement.</summary>
    public async ValueTask<ResourceResponse> LoadAsync(Uri uri, ResourceKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            // Caller should have already filtered via SafeResourceLoader's
            // scheme check; defensive rejection here keeps the contract
            // honest.
            throw new InvalidOperationException(
                $"SafeHttpResourceLoader does not handle the '{uri.Scheme}' scheme.");
        }

        var currentUri = uri;
        var hops = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Per PR #18 review #1 — resolve the host + validate every
            // resolved address against the IP blocklist BEFORE any
            // socket activity. ConnectCallback below uses the same
            // address (the first that passes) so the validation +
            // connect see consistent IPs (defeats DNS rebind that
            // would re-resolve at connect time).
            var validatedIp = await ResolveAndValidateAsync(currentUri.Host, ct).ConfigureAwait(false);
            if (validatedIp is null)
            {
                throw new HttpRequestException(
                    $"DNS resolution for '{currentUri.Host}' returned no addresses or every address was in the IP blocklist");
            }
            // Stash the validated IP on the request so ConnectCallback
            // can pin it.
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Options.Set(new HttpRequestOptionsKey<IPAddress>(ValidatedIpKey), validatedIp);
            // ResponseHeadersRead so the body stream can be size-bounded
            // before full materialization.
            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Manual redirect handling.
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                if (location is null)
                {
                    throw new HttpRequestException(
                        $"redirect status {(int)response.StatusCode} returned no Location header");
                }
                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                var redirectVerdict = UriSafetyValidator.ValidateRedirect(currentUri, nextUri, _policy, hops);
                if (!redirectVerdict.IsSafe)
                {
                    throw new HttpRequestException(
                        $"redirect from '{currentUri}' to '{nextUri}' rejected: {redirectVerdict.Reason}");
                }
                currentUri = nextUri;
                hops++;
                continue;
            }

            response.EnsureSuccessStatusCode();

            // Stream the body with a hard byte cap so an attacker
            // can't tar-pit / stream gigabytes.
            var bytes = await ReadBoundedAsync(response, ct).ConfigureAwait(false);
            return new ResourceResponse
            {
                Content = bytes,
                MimeType = response.Content.Headers.ContentType?.MediaType,
                CharSet = response.Content.Headers.ContentType?.CharSet,
            };
        }
    }

    /// <summary>Per PR #18 review #1 — resolve the host + return the
    /// first address that passes <see cref="UriSafetyValidator.IsBlockedIp"/>.
    /// Returns null when DNS returned no addresses OR every resolved
    /// address is on the blocklist.</summary>
    private static async ValueTask<IPAddress?> ResolveAndValidateAsync(string host, CancellationToken ct)
    {
        // GetHostAddressesAsync handles both IP literals (returns the
        // single address) and symbolic names (DNS query). Either way
        // each result must pass IsBlockedIp.
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }
        foreach (var ip in addresses)
        {
            if (!UriSafetyValidator.IsBlockedIp(ip, out _))
            {
                return ip;
            }
        }
        return null;
    }

    private const string ValidatedIpKey = "NetPdf.SafeHttpResourceLoader.ValidatedIp";

    /// <summary>Per PR #18 review #1 — connect to the validated IP, not
    /// to a freshly-resolved address. Defeats DNS-rebind attacks that
    /// would resolve to a public IP at validation time + a private IP
    /// at connect time. The ConnectCallback is invoked by
    /// SocketsHttpHandler after the request is built but before any
    /// bytes hit the wire.</summary>
    private static async ValueTask<Stream> ValidatedConnect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (!context.InitialRequestMessage.Options.TryGetValue(
            new HttpRequestOptionsKey<IPAddress>(ValidatedIpKey), out var validatedIp))
        {
            throw new InvalidOperationException(
                "SafeHttpResourceLoader connect-callback invoked without a validated IP.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Pin to the validated IP. context.DnsEndPoint.Port carries
            // the port the handler wanted to use (80 / 443 / custom).
            await socket.ConnectAsync(new IPEndPoint(validatedIp, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Per PR #18 review #1 — stream-bounded read. Caps at
    /// <see cref="SecurityPolicy.MaxResourceBytes"/>; if the response
    /// exceeds the cap, throws <see cref="HttpRequestException"/>
    /// (caught by SafeResourceLoader's exception filter + surfaced as
    /// a typed failure).</summary>
    private async ValueTask<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var cap = _policy.MaxResourceBytes;
        // If Content-Length is known + over the cap, reject immediately.
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > cap)
        {
            throw new HttpRequestException(
                $"response Content-Length {declaredLength.Value} exceeds per-resource cap {cap}");
        }
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[Math.Min(declaredLength ?? 64 * 1024L, cap + 1)];
        var written = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = (int)Math.Min(buffer.Length - written, cap + 1 - written);
            if (remaining <= 0)
            {
                throw new HttpRequestException(
                    $"response body exceeds per-resource cap ({cap} bytes); halted at {written}");
            }
            var read = await stream.ReadAsync(buffer.AsMemory(written, remaining), ct).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            if (written > cap)
            {
                throw new HttpRequestException(
                    $"response body exceeds per-resource cap ({cap} bytes); halted at {written}");
            }
        }
        return new ReadOnlyMemory<byte>(buffer, 0, written);
    }

    public void Dispose() => _httpClient.Dispose();
}
