// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Discriminator for <see cref="ResolverResult"/>. Four semantically-distinct outcomes
/// the cycle-1 resolvers conflated under <see cref="ComputedSlot.Unset"/>:
/// <list type="bullet">
///   <item><see cref="Resolved"/> — typed value available; consumer reads
///     <see cref="ResolverResult.Slot"/>.</item>
///   <item><see cref="Deferred"/> — the resolver successfully validated the value
///     against the property's grammar but cannot reduce it without context the
///     cascade stage doesn't have (font / viewport / container metrics).
///     Consumer keeps <see cref="ResolverResult.RawText"/> and re-resolves at
///     layout time where the missing context is available. The text IS known
///     well-formed.</item>
///   <item><see cref="UnsupportedUnvalidated"/> — the property's
///     <see cref="NetPdf.Css.Properties.PropertyType"/> has no leaf resolver
///     wired yet (a <c>PropertyType</c> still on the backlog; the font properties
///     — <c>FontSize</c> / <c>FontFamilyList</c> / <c>FontWeight</c> — now resolve,
///     see <c>PropertyResolverDispatch</c>). The dispatch carries the raw text
///     forward but <b>has not validated it</b> — a typo could pass through.
///     Distinguished from <see cref="Deferred"/> so downstream stages and audits
///     know which values still need a real resolver. Consumer keeps
///     <see cref="ResolverResult.RawText"/>.</item>
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
    /// <summary>The value is well-formed (per the property's grammar) but needs
    /// downstream context to reduce. Slot is <see cref="ComputedSlot.Unset"/>;
    /// RawText carries the original text for re-resolution.</summary>
    Deferred = 1,
    /// <summary>The value text could not be parsed. Slot is
    /// <see cref="ComputedSlot.Unset"/>; RawText is null; a diagnostic was emitted.</summary>
    Invalid = 2,
    /// <summary>The property's <see cref="NetPdf.Css.Properties.PropertyType"/>
    /// has no leaf resolver wired yet (still on the backlog — the font properties
    /// now resolve). The dispatch carries the raw text forward but has <b>not
    /// validated it</b>. Slot is <see cref="ComputedSlot.Unset"/>; RawText carries
    /// the original. Distinguished from <see cref="Deferred"/> so audits + future
    /// resolvers can find the "still needs validation" surface.</summary>
    UnsupportedUnvalidated = 3,
}

/// <summary>
/// Structured return from <see cref="PropertyResolverDispatch"/> and the per-property
/// leaf resolvers. The cycle-1 implementation collapsed multiple outcomes into a
/// shared <see cref="ComputedSlot.Unset"/> sentinel, which made it impossible for the
/// cascade to distinguish "valid but needs context" from "invalid input" — both look
/// identical to the consumer. Without the distinction the cascade can't choose between
/// (a) carrying the raw text forward for layout-time finalization vs (b) falling back
/// to the property's initial / inherited value. The hardening review then split the
/// "deferred" bucket again so cycle-2 PropertyTypes don't silently inherit "validated"
/// semantics — see <see cref="ResolutionState"/> for the full 4-state contract.
/// </summary>
/// <param name="Slot">The typed value, valid only when <see cref="State"/> is
/// <see cref="ResolutionState.Resolved"/>. <see cref="ComputedSlot.Unset"/> for every
/// other state.</param>
/// <param name="State">The outcome category — see <see cref="ResolutionState"/>.</param>
/// <param name="RawText">The original (pre-resolution) text. Present (non-null) when
/// <see cref="State"/> is either <see cref="ResolutionState.Deferred"/> (validated,
/// needs downstream context) OR <see cref="ResolutionState.UnsupportedUnvalidated"/>
/// (no resolver wired yet, raw text passed through unchecked) — both of which carry
/// raw text downstream must preserve. Use <see cref="HasRawText"/> for the consolidated
/// check. Null for <see cref="ResolutionState.Resolved"/> + <see cref="ResolutionState.Invalid"/>.</param>
/// <param name="SideTablePayload">Per Phase 3 Task 17 cycle 0b — optional out-of-band
/// payload for <see cref="ResolutionState.Resolved"/> results whose typed value is
/// larger than an 8-byte <see cref="ComputedSlot"/> can hold. The grid resolvers
/// (<c>GridTemplateListResolver</c> / <c>GridLineResolver</c>) return their parsed
/// AST (<c>TrackList</c> / <c>GridLineValue</c>) here; <see cref="MaterializeInto"/>
/// stashes it in <see cref="ComputedStyle"/>'s side-table dictionary alongside writing
/// the slot tag (which marks the property as "see side-table"). Null for the simple-
/// value resolvers (length / color / number / keyword) which encode everything in
/// the slot.</param>
internal readonly record struct ResolverResult(
    ComputedSlot Slot,
    ResolutionState State,
    string? RawText,
    object? SideTablePayload = null)
{
    /// <summary>Build a <see cref="ResolutionState.Resolved"/> result wrapping
    /// <paramref name="slot"/>.</summary>
    public static ResolverResult Resolved(ComputedSlot slot) =>
        new(slot, ResolutionState.Resolved, null);

    /// <summary>Per Phase 3 Task 17 cycle 0b — build a
    /// <see cref="ResolutionState.Resolved"/> result whose payload lives in
    /// <see cref="ComputedStyle"/>'s side-table dictionary rather than packed into
    /// the 8-byte <see cref="ComputedSlot"/>. The slot is set to
    /// <see cref="ComputedSlot.FromSideTableIndex(int)"/> with index <c>0</c> as a
    /// "see side-table" marker (the actual lookup is by <see cref="NetPdf.Css.Properties.PropertyId"/>,
    /// not by the index — each property has at most one side-table entry).
    /// <see cref="MaterializeInto"/> writes both the slot and the payload.</summary>
    /// <param name="payload">The typed AST (e.g., <c>TrackList</c>, <c>GridLineValue</c>).
    /// Must be non-null — callers wanting "no side-table value" should use the
    /// <see cref="Resolved(ComputedSlot)"/> factory with a default-keyword slot
    /// instead.</param>
    public static ResolverResult ResolvedSideTable(object payload)
    {
        if (payload is null)
        {
            throw new System.ArgumentNullException(nameof(payload),
                "ResolvedSideTable payload must be non-null — use Resolved(slot) for default values.");
        }
        return new(ComputedSlot.FromSideTableIndex(0), ResolutionState.Resolved, null, payload);
    }

    /// <summary>Build a <see cref="ResolutionState.Deferred"/> result carrying
    /// <paramref name="rawText"/> for downstream re-resolution.</summary>
    public static ResolverResult Deferred(string rawText) =>
        new(ComputedSlot.Unset, ResolutionState.Deferred, rawText);

    /// <summary>Build a <see cref="ResolutionState.Invalid"/> result. Caller is
    /// responsible for emitting the <c>CSS-PROPERTY-VALUE-INVALID-001</c> diagnostic
    /// before constructing this — the result itself does not carry a diagnostic.</summary>
    public static ResolverResult Invalid() =>
        new(ComputedSlot.Unset, ResolutionState.Invalid, null);

    /// <summary>Build a <see cref="ResolutionState.UnsupportedUnvalidated"/> result
    /// for a property whose PropertyType has no resolver wired yet. The raw text
    /// rides along but is <b>not validated</b> — a typo could pass through.
    /// Cycle-2 work upgrades these to Resolved / Deferred / Invalid.</summary>
    public static ResolverResult UnsupportedUnvalidated(string rawText) =>
        new(ComputedSlot.Unset, ResolutionState.UnsupportedUnvalidated, rawText);

    public bool IsResolved => State == ResolutionState.Resolved;
    public bool IsDeferred => State == ResolutionState.Deferred;
    public bool IsInvalid => State == ResolutionState.Invalid;
    public bool IsUnsupportedUnvalidated => State == ResolutionState.UnsupportedUnvalidated;

    /// <summary><see langword="true"/> when the result carries raw text downstream
    /// must preserve (i.e., <see cref="ResolutionState.Deferred"/> or
    /// <see cref="ResolutionState.UnsupportedUnvalidated"/>). Helps consumers
    /// avoid silently dropping <see cref="RawText"/>.</summary>
    public bool HasRawText =>
        State is ResolutionState.Deferred or ResolutionState.UnsupportedUnvalidated;

    /// <summary>
    /// Materializes this result into <paramref name="style"/> for the given
    /// <paramref name="propertyId"/>. <see cref="ResolutionState.Resolved"/>
    /// writes the typed slot via <see cref="ComputedStyle.Set"/>;
    /// <see cref="ResolutionState.Deferred"/> and
    /// <see cref="ResolutionState.UnsupportedUnvalidated"/> route the
    /// <see cref="RawText"/> through <see cref="ComputedStyle.SetDeferred"/>
    /// so it can't be silently dropped; <see cref="ResolutionState.Invalid"/>
    /// is a no-op (cascade falls back per L4 §4.4).
    /// </summary>
    /// <returns><see langword="true"/> when a value was written;
    /// <see langword="false"/> for <see cref="ResolutionState.Invalid"/>.</returns>
    /// <remarks>
    /// This method is the recommended consumer entry point — using it eliminates
    /// the cycle-1 footgun where a caller pattern-matched on
    /// <see cref="IsResolved"/> alone and dropped <see cref="RawText"/> for
    /// every Deferred / UnsupportedUnvalidated result. Tests on the materialization
    /// path verify the round-trip.
    /// </remarks>
    public bool MaterializeInto(ComputedStyle style, PropertyId propertyId)
    {
        switch (State)
        {
            case ResolutionState.Resolved:
                // Per Phase 3 Task 17 cycle 0b — write the side-table payload
                // BEFORE the slot so a reader that races on the slot tag still
                // sees a consistent (tag, payload) pair. Per PR-#90 review F6,
                // ComputedStyle.Set now owns the inverse invariant (= a non-
                // SideTableIndex slot auto-clears any prior payload), so we
                // no longer need an explicit ClearSideTablePayload call on the
                // null-payload branch.
                if (SideTablePayload is not null)
                {
                    style.SetSideTablePayload(propertyId, SideTablePayload);
                }
                style.Set(propertyId, Slot);
                return true;
            case ResolutionState.Deferred:
            case ResolutionState.UnsupportedUnvalidated:
                style.SetDeferred(propertyId, RawText!);
                return true;
            case ResolutionState.Invalid:
            default:
                return false;
        }
    }
}
