// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Threading;

namespace NetPdf;

/// <summary>
/// Per Phase D D-1 — per-render state threaded through every
/// <see cref="SafeResourceLoader"/> fetch. Tracks the cumulative byte +
/// fetch counts that <see cref="SecurityPolicy.MaxResourcesPerRender"/> +
/// <see cref="SecurityPolicy.MaxTotalResourceBytes"/> bound.
///
/// <para>One instance lives per <c>HtmlPdf.ConvertAsync</c> invocation;
/// sharing across renders would let attacker-A's traffic exhaust attacker-B's
/// budget. The pipeline allocates this in
/// <c>HtmlParsingHost.ParseAsync</c> (or whoever owns the conversion's
/// top-level entry point) + passes it down through every CSS / image / font
/// resource lookup.</para>
/// </summary>
public sealed class ResourceFetchContext
{
    /// <summary>The active <see cref="SecurityPolicy"/>. Captured once at
    /// construction so a hostile loader can't swap policies mid-render.</summary>
    public SecurityPolicy Policy { get; }

    /// <summary>The conversion's <see cref="HtmlPdfOptions.BaseUri"/>.
    /// Loaders use this to resolve relative URIs + to anchor the
    /// <see cref="SecurityPolicy.AllowFileSchemeUnderBaseUri"/> path-prefix
    /// check that <see cref="UriSafetyValidator"/> defers.</summary>
    public Uri? BaseUri { get; }

    /// <summary>The conversion-level cancellation token.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>Running count of resources successfully fetched (or
    /// counted-against-budget — failed fetches still consume the slot so
    /// an attacker can't pile up rejected requests). Compared against
    /// <see cref="SecurityPolicy.MaxResourcesPerRender"/>.</summary>
    public int FetchedCount { get; private set; }

    /// <summary>Running cumulative bytes returned across every successful
    /// fetch. Compared against <see cref="SecurityPolicy.MaxTotalResourceBytes"/>.</summary>
    public long FetchedBytes { get; private set; }

    public ResourceFetchContext(SecurityPolicy policy, Uri? baseUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        BaseUri = baseUri;
        CancellationToken = cancellationToken;
    }

    /// <summary>Reserve one fetch slot against the per-render count cap.
    /// Returns a non-null reason when the slot budget is exhausted; null
    /// when the reservation succeeded. Counters are incremented on success;
    /// on failure they remain unchanged.
    ///
    /// <para>Byte budget enforcement is split out into <see cref="TryAddBytes"/>
    /// so the caller can reserve a slot at fetch START (when the byte count
    /// is unknown) + charge the actual byte count at fetch END (without
    /// re-incrementing the slot counter). This matches the typical loader
    /// flow: HTTP fetch begins → reserve slot → loader runs → response.Content
    /// has known length → charge bytes.</para></summary>
    public string? TryReserveSlot()
    {
        if (FetchedCount >= Policy.MaxResourcesPerRender)
        {
            return $"per-render fetch count cap ({Policy.MaxResourcesPerRender}) exhausted";
        }
        FetchedCount++;
        return null;
    }

    /// <summary>Charge <paramref name="bytes"/> against the per-render
    /// byte budget. Used after a successful fetch when the actual byte
    /// count is known; does not increment the slot counter (the caller
    /// already reserved one via <see cref="TryReserveSlot"/>). Returns a
    /// non-null reason when the byte budget would be exceeded; null on
    /// success. The counter is updated only on success.</summary>
    public string? TryAddBytes(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes), "byte count must be non-negative");
        if (FetchedBytes + bytes > Policy.MaxTotalResourceBytes)
        {
            return $"per-render byte budget ({Policy.MaxTotalResourceBytes / (1024 * 1024)} MiB) would be exceeded by this fetch";
        }
        FetchedBytes += bytes;
        return null;
    }
}
