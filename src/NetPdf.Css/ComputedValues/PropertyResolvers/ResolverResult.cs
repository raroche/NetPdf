// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Discriminator for <see cref="ResolverResult"/>. Three semantically-distinct outcomes
/// the cycle-1 resolvers conflated under <see cref="ComputedSlot.Unset"/>:
/// <list type="bullet">
///   <item><see cref="Resolved"/> — typed value available; consumer reads
///     <see cref="ResolverResult.Slot"/>.</item>
///   <item><see cref="Deferred"/> — syntactically valid but needs context the
///     cascade stage doesn't have (font / viewport / container metrics, an
///     upstream typed-value parser still on the cycle-2 backlog). Consumer
///     keeps <see cref="ResolverResult.RawText"/> and re-resolves at a later
///     stage where the missing context is available.</item>
///   <item><see cref="Invalid"/> — value text could not be parsed. The cascade's
///     "invalid at computed value time" rule applies — the property's initial
///     value (or inherited value for inherited properties) is used. A
///     <c>CSS-PROPERTY-VALUE-INVALID-001</c> diagnostic was emitted.</item>
/// </list>
/// </summary>
internal enum ResolutionState : byte
{
    /// <summary>The resolver produced a typed value. Slot is meaningful; RawText is null.</summary>
    Resolved = 0,
    /// <summary>The value is well-formed but needs downstream context. Slot is
    /// <see cref="ComputedSlot.Unset"/>; RawText carries the original text for
    /// re-resolution.</summary>
    Deferred = 1,
    /// <summary>The value text could not be parsed. Slot is
    /// <see cref="ComputedSlot.Unset"/>; RawText is null; a diagnostic was emitted.</summary>
    Invalid = 2,
}

/// <summary>
/// Structured return from <see cref="PropertyResolverDispatch"/> and the per-property
/// leaf resolvers. The cycle-1 implementation collapsed these three outcomes into a
/// shared <see cref="ComputedSlot.Unset"/> sentinel, which made it impossible for the
/// cascade to distinguish "valid but needs context" from "invalid input" — both look
/// identical to the consumer. Without the distinction the cascade can't choose between
/// (a) carrying the raw text forward for layout-time finalization vs (b) falling back
/// to the property's initial / inherited value.
/// </summary>
/// <param name="Slot">The typed value, valid only when <see cref="State"/> is
/// <see cref="ResolutionState.Resolved"/>.</param>
/// <param name="State">The outcome category — see <see cref="ResolutionState"/>.</param>
/// <param name="RawText">The original (pre-resolution) text, present only when
/// <see cref="State"/> is <see cref="ResolutionState.Deferred"/> so a downstream
/// re-resolver can pick it up.</param>
internal readonly record struct ResolverResult(
    ComputedSlot Slot,
    ResolutionState State,
    string? RawText)
{
    /// <summary>Build a <see cref="ResolutionState.Resolved"/> result wrapping
    /// <paramref name="slot"/>.</summary>
    public static ResolverResult Resolved(ComputedSlot slot) =>
        new(slot, ResolutionState.Resolved, null);

    /// <summary>Build a <see cref="ResolutionState.Deferred"/> result carrying
    /// <paramref name="rawText"/> for downstream re-resolution.</summary>
    public static ResolverResult Deferred(string rawText) =>
        new(ComputedSlot.Unset, ResolutionState.Deferred, rawText);

    /// <summary>Build a <see cref="ResolutionState.Invalid"/> result. Caller is
    /// responsible for emitting the <c>CSS-PROPERTY-VALUE-INVALID-001</c> diagnostic
    /// before constructing this — the result itself does not carry a diagnostic.</summary>
    public static ResolverResult Invalid() =>
        new(ComputedSlot.Unset, ResolutionState.Invalid, null);

    public bool IsResolved => State == ResolutionState.Resolved;
    public bool IsDeferred => State == ResolutionState.Deferred;
    public bool IsInvalid => State == ResolutionState.Invalid;
}
