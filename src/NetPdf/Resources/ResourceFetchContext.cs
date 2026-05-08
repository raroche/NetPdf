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
///
/// <para><b>Per PR #18 review #6 — concurrency-safe.</b> Reservations use
/// <see cref="Interlocked"/> so a future Phase 5 implementation can fan
/// out parallel image/font/style fetches without races. Pre-fix concurrent
/// callers could each read a stale <c>FetchedCount</c>, find it under the
/// cap, increment from that stale value, and collectively exceed the
/// cap. Post-fix the count + byte ledger maintain their invariants under
/// concurrent reservation.</para>
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

    private int _fetchedCount;
    private long _fetchedBytes;

    /// <summary>Running count of resources successfully fetched. Read
    /// is volatile per <see cref="Interlocked"/> semantics; writes go
    /// through <see cref="TryReserveSlot"/>'s atomic CAS.</summary>
    public int FetchedCount => Volatile.Read(ref _fetchedCount);

    /// <summary>Running cumulative bytes returned across every successful
    /// fetch. Written via <see cref="Interlocked.Add(ref long, long)"/>
    /// in <see cref="TryAddBytes"/>; reads are volatile.</summary>
    public long FetchedBytes => Interlocked.Read(ref _fetchedBytes);

    public ResourceFetchContext(SecurityPolicy policy, Uri? baseUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
        BaseUri = baseUri;
        CancellationToken = cancellationToken;
    }

    /// <summary>Reserve one fetch slot against the per-render count cap.
    /// Returns a non-null reason when the slot budget is exhausted; null
    /// when the reservation succeeded.
    ///
    /// <para>Per PR #18 review #6 — uses CAS (<see cref="Interlocked.CompareExchange(ref int, int, int)"/>)
    /// so concurrent callers can race the increment without exceeding
    /// the cap. The CAS loop reads the current count, computes the
    /// next value, attempts to swap; on contention re-reads + retries.</para>
    ///
    /// <para>Byte budget enforcement is split out into <see cref="TryAddBytes"/>
    /// so the caller can reserve a slot at fetch START (when the byte count
    /// is unknown) + charge the actual byte count at fetch END (without
    /// re-incrementing the slot counter).</para></summary>
    public string? TryReserveSlot()
    {
        var max = Policy.MaxResourcesPerRender;
        while (true)
        {
            var current = Volatile.Read(ref _fetchedCount);
            if (current >= max)
            {
                return $"per-render fetch count cap ({max}) exhausted";
            }
            if (Interlocked.CompareExchange(ref _fetchedCount, current + 1, current) == current)
            {
                return null;
            }
            // Lost the CAS race; another thread incremented between our
            // read + write. Retry the load + re-check the cap.
        }
    }

    /// <summary>Charge <paramref name="bytes"/> against the per-render
    /// byte budget. Used after a successful fetch when the actual byte
    /// count is known; does not increment the slot counter (the caller
    /// already reserved one via <see cref="TryReserveSlot"/>). Returns a
    /// non-null reason when the byte budget would be exceeded; null on
    /// success.
    ///
    /// <para>Per PR #18 review #6 — concurrency-safe via CAS.
    /// <see cref="Interlocked.Add(ref long, long)"/> alone would
    /// over-charge: two concurrent calls could each see a value under
    /// the cap, both add their bytes, and end above the cap. The CAS
    /// loop reserves the byte count atomically.</para></summary>
    public string? TryAddBytes(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes), "byte count must be non-negative");
        var cap = Policy.MaxTotalResourceBytes;
        while (true)
        {
            var current = Interlocked.Read(ref _fetchedBytes);
            var next = current + bytes;
            if (next > cap)
            {
                return $"per-render byte budget ({cap / (1024 * 1024)} MiB) would be exceeded by this fetch";
            }
            if (Interlocked.CompareExchange(ref _fetchedBytes, next, current) == current)
            {
                return null;
            }
            // Lost the CAS race; retry.
        }
    }
}
