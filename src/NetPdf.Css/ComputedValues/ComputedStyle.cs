// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NetPdf.Css.Properties;

namespace NetPdf.Css.ComputedValues;

/// <summary>
/// Per-element CSS computed style — one <see cref="ComputedSlot"/> per registered
/// property, plus a sparse side dictionary for custom (<c>--*</c>) properties. The
/// cascade resolver (Task 7) builds an instance per element; later passes (used-value,
/// layout, paint) read it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage layout.</b> A <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/>
/// of length <see cref="PropertyMetadata.Count"/> lays the slots out contiguously in the
/// instance, giving cache-friendly index-by-<see cref="PropertyId"/> access. A separate
/// <see cref="System.Runtime.CompilerServices.InlineArrayAttribute"/> of <see cref="ulong"/>
/// holds the "is set" bitmap so the cascade can distinguish "explicit declaration"
/// from "inherit / initial fall-back" without sentinel-value tricks.
/// </para>
/// <para>
/// <b>Class vs struct.</b> Phase 2 doc speculated a <c>readonly struct</c> but landing
/// it as a sealed class trades ~24 bytes of object-header overhead for reference
/// semantics — the cascade can store the same instance in multiple lookup tables and
/// pass it to layout passes without struct-copy worries. Per-instance cost is
/// 24 + 8 × <see cref="PropertyMetadata.Count"/> + bitmap bytes (≈530 bytes for the
/// current 63-property registry).
/// </para>
/// <para>
/// <b>Pooling.</b> <see cref="Rent"/> draws from a bounded process-wide
/// <see cref="ConcurrentBag{T}"/> of returned instances; <see cref="IDisposable.Dispose"/>
/// hands the instance back. The pool is capped at <see cref="MaxPoolSize"/> so it can't
/// grow unboundedly under high churn. <b>Use-after-Dispose contract:</b> after a caller
/// disposes their reference, the same physical instance may be re-rented by another
/// caller and reset; the original holder must not touch it. The instance's
/// <c>_disposed</c> flag is a soft guard that catches most violations but won't catch
/// the race where another caller has re-rented and cleared the flag. (Same constraint as
/// <see cref="System.Buffers.ArrayPool{T}"/>.)
/// </para>
/// <para>
/// <b>Custom properties</b> (<c>--*</c>) are out-of-band: a sparse <see cref="Dictionary{TKey,TValue}"/>
/// keyed by name. CSS Custom Properties L1 §2 says they have arbitrary token-stream
/// values; not worth a fixed-size slot. The dictionary is created lazily on first set.
/// </para>
/// <para>
/// <b>Per-property typed accessors</b> (e.g., <c>style.Color</c> returning a typed
/// <c>RgbaColor</c>) belong in Tasks 9–10, where typed value records and per-property
/// keyword tables live. Task 5 ships only the raw <see cref="Get"/> /
/// <see cref="Set"/> / <see cref="IsSet"/> trio over <see cref="ComputedSlot"/>.
/// </para>
/// </remarks>
internal sealed class ComputedStyle : IDisposable
{
    /// <summary>Bitmap word size matches <see cref="ulong"/>.</summary>
    private const int BitsPerWord = 64;

    /// <summary>Number of bitmap words needed to cover all properties.</summary>
    public const int BitmapWordCount = (PropertyMetadata.Count + BitsPerWord - 1) / BitsPerWord;

    /// <summary>Per-property slot storage. Inline-laid-out so access is index-not-pointer.</summary>
    private SlotStorage _slots;

    /// <summary>Set-bit per property indicating "explicit value written".</summary>
    private BitmapStorage _bitmap;

    /// <summary>Lazily-allocated for elements whose stylesheets set custom properties.</summary>
    private Dictionary<string, ComputedSlot>? _customProperties;

    private bool _disposed;

    /// <summary>Test-only flag — when set, <see cref="Dispose"/> marks the
    /// instance disposed but does NOT return it to <see cref="_pool"/>, so
    /// the disposed-flag guard stays observably true. Without this, the
    /// soft-guard contract races under parallel test execution: another
    /// thread can <see cref="Rent"/> the just-disposed instance + reset the
    /// flag before the original holder's use-after-dispose access fires
    /// <see cref="ObjectDisposedException"/>. The race is intentional in
    /// production (the soft guard is documented as best-effort), but
    /// non-deterministic for unit tests that assert the throw. Set via
    /// <see cref="RentForExclusiveTesting"/>; never set in production
    /// rent paths.</summary>
    private bool _excludedFromPool;

    /// <summary>Set by <see cref="MarkAsBoxOwned"/> when a box-tree node attaches
    /// this style. Once set, <see cref="Dispose"/> refuses to return the instance
    /// to the pool — the box still references it, and pool re-rental would let
    /// another caller clear/repopulate the slots and silently corrupt the box's
    /// view. Cycle-2 / Phase 3 will add an explicit box-tree disposal sweep that
    /// clears this flag once the tree is discarded.</summary>
    private bool _isBoxOwned;

    /// <summary>Maximum instances retained in the pool. Capped to bound memory under
    /// high rent/dispose churn — beyond this, <see cref="Dispose"/> drops to GC.</summary>
    private const int MaxPoolSize = 256;

    /// <summary>Process-wide bag of returned instances ready for re-rental.</summary>
    private static readonly ConcurrentBag<ComputedStyle> _pool = new();

    /// <summary>
    /// Returns a <see cref="ComputedStyle"/> ready for use — either a previously-disposed
    /// instance pulled from the pool (cleared to all-Unset state on the way out) or a
    /// fresh allocation when the pool is empty.
    /// </summary>
    public static ComputedStyle Rent()
    {
        if (_pool.TryTake(out var instance))
        {
            instance.Reset();
            return instance;
        }
        return new ComputedStyle();
    }

    /// <summary>Test-only factory — returns a fresh instance that bypasses
    /// the pool entirely. <see cref="Dispose"/> marks the instance disposed
    /// but skips the <see cref="_pool"/>.<see cref="ConcurrentBag{T}.Add"/>
    /// call, so the disposed flag stays observably true regardless of
    /// concurrent <see cref="Rent"/> activity in parallel tests. Use this
    /// when a unit test asserts the use-after-dispose <see cref="ObjectDisposedException"/>
    /// — the production <see cref="Rent"/> path's soft-guard contract is
    /// documented as best-effort + races by design under high churn, which
    /// is fine in production but non-deterministic in xUnit's parallel
    /// runner. The instance is never recycled (it's leaked at end of test);
    /// negligible overhead since called rarely.</summary>
    internal static ComputedStyle RentForExclusiveTesting()
    {
        var instance = new ComputedStyle();
        instance._excludedFromPool = true;
        return instance;
    }

    /// <summary>
    /// Marks the instance disposed and queues it back to the pool for re-rental
    /// (subject to <see cref="MaxPoolSize"/>). Idempotent. The slot/bitmap clearing
    /// happens lazily on the next <see cref="Rent"/> via <see cref="Reset"/>, not here,
    /// so disposing is cheap.
    /// </summary>
    /// <remarks>
    /// <b>Box-ownership safety (Task 11 hardening Rec 6).</b> If
    /// <see cref="MarkAsBoxOwned"/> has been called (a <c>NetPdf.Layout.Boxes.Box</c>
    /// holds a reference), <see cref="Dispose"/> is a no-op — the instance stays alive
    /// for the box-tree lifetime. Cycle-2 / Phase 3 will add a tree-disposal sweep that
    /// clears the box-owned flag and re-enables pool return.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        if (_isBoxOwned) return;
        _disposed = true;
        if (_excludedFromPool) return;
        if (_pool.Count < MaxPoolSize)
        {
            _pool.Add(this);
        }
    }

    /// <summary>
    /// Marks this style as held by one or more <c>NetPdf.Layout.Boxes.Box</c>
    /// instances. Idempotent: repeated calls are no-ops. Once marked, the pool
    /// cannot recycle the instance until the flag is cleared (Phase 3 work) —
    /// otherwise a re-rented instance would be cleared/reset while the box still
    /// reads from it, silently corrupting the box's view per
    /// Task 11 hardening Rec 6.
    /// </summary>
    public void MarkAsBoxOwned()
    {
        ThrowIfDisposed();
        _isBoxOwned = true;
    }

    /// <summary><see langword="true"/> when at least one <c>NetPdf.Layout.Boxes.Box</c>
    /// has marked this style as owned.</summary>
    public bool IsBoxOwned => _isBoxOwned;

    /// <summary>Per Phase 2 deep review Rec 7 — release the box-ownership claim
    /// + return the instance to the pool. Called by the box-tree disposal sweep
    /// (<c>Phase2Result.Dispose</c>) once the rendered tree is discarded.
    /// Without this, every conversion leaks one <see cref="ComputedStyle"/> per
    /// styled box / pseudo / fragment-pseudo to GC instead of recycling through
    /// the pool — repeated conversions miss the fast path. Idempotent: a
    /// double-release is safe.</summary>
    public void ReleaseFromBox()
    {
        if (_disposed) return;
        _isBoxOwned = false;
        Dispose();
    }

    /// <summary>
    /// Clears all slots, bitmap words, and the custom-property dictionary, and flips
    /// <see cref="_disposed"/> back to <see langword="false"/>. Called by <see cref="Rent"/>
    /// when reusing a pooled instance — the just-rented caller sees a fresh-looking
    /// <see cref="ComputedStyle"/>.
    /// </summary>
    private void Reset()
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            _slots[i] = ComputedSlot.Unset;
        }
        for (var i = 0; i < BitmapWordCount; i++)
        {
            _bitmap[i] = 0;
        }
        _customProperties?.Clear();
        _deferredText?.Clear();
        _sideTablePayloads?.Clear();
        _isBoxOwned = false;
        _disposed = false;
    }

    /// <summary>
    /// Reads the slot for <paramref name="id"/>. Returns <see cref="ComputedSlot.Unset"/>
    /// if the slot has not been set.
    /// </summary>
    public ComputedSlot Get(PropertyId id)
    {
        ThrowIfDisposed();
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"PropertyId {id} (index {index}) is outside the registry (Count = {PropertyMetadata.Count}).");
        return _slots[index];
    }

    /// <summary>
    /// Writes the slot for <paramref name="id"/> and marks it as explicitly set. As a
    /// special case, <see cref="Set"/> with <see cref="ComputedSlot.Unset"/> is treated
    /// as <see cref="Unset(PropertyId)"/> — this preserves the invariant
    /// <c>IsSet(id) ⟹ !Get(id).IsUnset</c>, which downstream stages (cascade, computed
    /// values) rely on to distinguish "explicitly set" from "no value yet". Without the
    /// redirect, a caller could create a slot that's both set in the bitmap and
    /// holding the unset sentinel value.
    ///
    /// <para><b>Side-table invariant per PR-#90 review F6:</b> when the new
    /// <paramref name="value"/>'s tag is anything OTHER than
    /// <see cref="ComputedSlotTag.SideTableIndex"/>, any prior side-table payload
    /// for <paramref name="id"/> is dropped automatically. This makes
    /// <see cref="ComputedStyle"/> the owner of the (slot, side-table) consistency
    /// rule — callers no longer need to clear side-table entries explicitly when
    /// transitioning from a parsed AST to a simple-slot value. Cascade resolvers
    /// + <see cref="PropertyResolvers.ResolverResult.MaterializeInto"/> rely on
    /// this invariant.</para>
    /// </summary>
    public void Set(PropertyId id, ComputedSlot value)
    {
        ThrowIfDisposed();
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"PropertyId {id} (index {index}) is outside the registry (Count = {PropertyMetadata.Count}).");
        if (value.IsUnset)
        {
            // Redirect to Unset so the bitmap and slot agree.
            _slots[index] = ComputedSlot.Unset;
            ClearBit(index);
            _deferredText?.Remove(id);
            // Per Phase 3 Task 17 cycle 0b — drop the side-table payload too
            // so a Set(id, Unset) call wipes ALL state for the property, not
            // just the slot + deferred text. Otherwise a stale AST would
            // outlive the slot it was paired with.
            _sideTablePayloads?.Remove(id);
            return;
        }
        _slots[index] = value;
        SetBit(index);
        // A typed value overrides any prior deferred text — keep them in sync.
        _deferredText?.Remove(id);
        // Per PR-#90 review F6 — make ComputedStyle own the invariant that the
        // slot tag + side-table presence stay in sync. A new SideTableIndex
        // slot is paired with its payload via SetSideTablePayload BEFORE this
        // call (the cascade dispatch's convention); any other tag means the
        // property's value is fully encoded in the 8-byte slot, so any prior
        // side-table payload is stale and must drop.
        if (value.Tag != ComputedSlotTag.SideTableIndex)
        {
            _sideTablePayloads?.Remove(id);
        }
    }

    /// <summary>
    /// <see langword="true"/> when <see cref="Set"/> has been called for
    /// <paramref name="id"/>. The cascade uses this to distinguish "explicit
    /// value" from "inherit from parent" / "initial value from registry".
    /// </summary>
    public bool IsSet(PropertyId id)
    {
        ThrowIfDisposed();
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count) return false;
        return GetBit(index);
    }

    /// <summary>Clears the slot back to <see cref="ComputedSlot.Unset"/> and unmarks the
    /// "is set" flag. Used by cascade revert / unset / revert-layer.</summary>
    public void Unset(PropertyId id)
    {
        ThrowIfDisposed();
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count) return;
        _slots[index] = ComputedSlot.Unset;
        ClearBit(index);
        _deferredText?.Remove(id);
        _sideTablePayloads?.Remove(id);
    }

    // ------------------------------------------------------------
    // Deferred text (Rec 5 of Task 10 hardening review)
    //
    // Stores raw declaration text for properties whose resolver returned a
    // Deferred or UnsupportedUnvalidated state. Layout time / cycle-2 resolvers
    // re-resolve from this side store. Without it, callers materializing a
    // Deferred ResolverResult into ComputedStyle would silently drop the raw
    // text and the cascade would have no record that a value was ever supplied.
    //
    // Storage: a sparse Dictionary<PropertyId, string> — most elements have no
    // deferred values, so we don't pay the per-property cost. Allocated lazily
    // on first SetDeferred call. Reset() and Unset() clear/remove entries to
    // keep the pool's contract intact.
    // ------------------------------------------------------------

    private Dictionary<PropertyId, string>? _deferredText;

    /// <summary>Records raw declaration text for a property whose resolver could not
    /// yet produce a typed value (Deferred or UnsupportedUnvalidated). Sets the
    /// "is set" bit so the cascade still knows an explicit declaration applied,
    /// even though <see cref="Get"/> returns <see cref="ComputedSlot.Unset"/>.
    /// Layout time (or a future cycle-2 resolver) reads back via
    /// <see cref="TryGetDeferred"/>.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="rawText"/> is null.</exception>
    public void SetDeferred(PropertyId id, string rawText)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rawText);
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"PropertyId {id} (index {index}) is outside the registry (Count = {PropertyMetadata.Count}).");
        _slots[index] = ComputedSlot.Unset;
        _deferredText ??= new Dictionary<PropertyId, string>();
        _deferredText[id] = rawText;
        SetBit(index);
        // Per PR-#90 review F6 — a deferred-text write supersedes any prior
        // typed AST in the side-table. Drop it so the next ReadGridXxx() sees
        // the deferred state cleanly (= falls back to property default until
        // layout-time re-resolution fills the AST back in).
        _sideTablePayloads?.Remove(id);
    }

    /// <summary><see langword="true"/> when <see cref="SetDeferred"/> has been called
    /// for <paramref name="id"/> (and not subsequently overwritten by <see cref="Set"/>
    /// or cleared by <see cref="Unset(PropertyId)"/>).</summary>
    public bool IsDeferred(PropertyId id)
    {
        ThrowIfDisposed();
        return _deferredText is not null && _deferredText.ContainsKey(id);
    }

    /// <summary>Reads the deferred raw text for <paramref name="id"/>. Returns
    /// <see langword="false"/> when the property has no deferred value (typed slot
    /// may still be present via <see cref="Get"/>).</summary>
    public bool TryGetDeferred(PropertyId id, out string? rawText)
    {
        ThrowIfDisposed();
        if (_deferredText is null)
        {
            rawText = null;
            return false;
        }
        return _deferredText.TryGetValue(id, out rawText);
    }

    // ------------------------------------------------------------
    // Side-table payloads (Phase 3 Task 17 cycle 0b — PR-#89 P1 #3)
    //
    // Properties whose computed value is larger than an 8-byte ComputedSlot
    // (e.g., the grid-template-{rows,columns} TrackList AST or the grid-line
    // GridLineValue carrying an optional named-line string ref) stash their
    // payload here. The slot itself holds the SideTableIndex tag as a
    // "look in the side-table" marker; the dictionary keys by PropertyId
    // since each property has at most one side-table entry.
    //
    // Storage: a sparse Dictionary<PropertyId, object> — most elements have
    // no side-table values, so we don't pay per-property cost. Allocated
    // lazily on first SetSideTablePayload call. Reset() drops it back to
    // null so pool re-rental doesn't leak prior payloads.
    //
    // Type-safety: callers read via TryGetSideTablePayload<T> which checks
    // the runtime type — a misuse (asking for TrackList on a GridLineValue
    // slot) returns false rather than throwing, matching the reader-fallback
    // pattern used by the dimension family (e.g., ReadFlexBasis returns Auto
    // for non-matching slot tags).
    // ------------------------------------------------------------

    private Dictionary<PropertyId, object>? _sideTablePayloads;

    /// <summary>Per Phase 3 Task 17 cycle 0b — stash <paramref name="payload"/>
    /// in the side-table for <paramref name="id"/>. Caller is responsible for
    /// setting the matching <see cref="ComputedSlot"/> via <see cref="Set"/>
    /// (typically <see cref="ComputedSlot.FromSideTableIndex"/>) so readers
    /// can find the entry through the slot tag.</summary>
    /// <exception cref="ArgumentNullException">When <paramref name="payload"/> is null.</exception>
    public void SetSideTablePayload(PropertyId id, object payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"PropertyId {id} (index {index}) is outside the registry (Count = {PropertyMetadata.Count}).");
        _sideTablePayloads ??= new Dictionary<PropertyId, object>();
        _sideTablePayloads[id] = payload;
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — clear any side-table payload
    /// for <paramref name="id"/>. Used when a subsequent cascade winner replaces
    /// a side-table value with a simple-slot value (= e.g., grid-template-rows:
    /// 100px 200px first, then grid-template-rows: none winning — the second
    /// declaration must wipe the prior AST so the reader doesn't return stale data).
    /// Idempotent: a clear on a property with no payload is a no-op.</summary>
    public void ClearSideTablePayload(PropertyId id)
    {
        ThrowIfDisposed();
        _sideTablePayloads?.Remove(id);
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the side-table payload for
    /// <paramref name="id"/> as type <typeparamref name="T"/>. Returns
    /// <see langword="false"/> when no entry exists or the entry's runtime type
    /// doesn't match <typeparamref name="T"/> (= type-mismatch falls through to
    /// the reader's default-value path, matching the dimension family's slot-tag-
    /// mismatch fallback pattern).</summary>
    public bool TryGetSideTablePayload<T>(PropertyId id, out T payload) where T : class
    {
        ThrowIfDisposed();
        if (_sideTablePayloads is not null
            && _sideTablePayloads.TryGetValue(id, out var raw)
            && raw is T typed)
        {
            payload = typed;
            return true;
        }
        payload = null!;
        return false;
    }

    /// <summary>Per Phase 5 layout→PDF cycle 4 — read the side-table payload for
    /// <paramref name="id"/> as the raw boxed object, regardless of type. Used by
    /// inheritance to carry an inherited side-table value (e.g. a font-family list)
    /// from the parent: the inherited slot is only a <see cref="ComputedSlotTag.SideTableIndex"/>
    /// marker, so its payload must be copied alongside it or the child reads the
    /// property's default.</summary>
    public bool TryGetSideTablePayloadRaw(PropertyId id, out object? payload)
    {
        ThrowIfDisposed();
        if (_sideTablePayloads is not null && _sideTablePayloads.TryGetValue(id, out payload))
            return true;
        payload = null;
        return false;
    }

    /// <summary>Per Phase 3 Task 17 cycle 0b — read the side-table payload as a
    /// value-type via the boxed-object stash. Mirrors the class-typed overload
    /// but unboxes the value type; <c>GridLineValue</c> is the cycle-0b consumer
    /// (= a record struct that can't satisfy the <c>class</c> constraint).</summary>
    public bool TryGetSideTablePayloadStruct<T>(PropertyId id, out T payload) where T : struct
    {
        ThrowIfDisposed();
        if (_sideTablePayloads is not null
            && _sideTablePayloads.TryGetValue(id, out var raw)
            && raw is T boxed)
        {
            payload = boxed;
            return true;
        }
        payload = default;
        return false;
    }

    // ------------------------------------------------------------
    // Custom properties (--*)
    // ------------------------------------------------------------

    /// <summary>
    /// Reads the value of a custom property by its full name including the leading <c>--</c>.
    /// Returns <see cref="ComputedSlot.Unset"/> when the property has not been set on this
    /// element. Custom-property names are case-sensitive per CSS Custom Properties L1 §2.
    /// </summary>
    /// <exception cref="ArgumentException">When <paramref name="name"/> is not a valid
    /// custom-property name (must start with <c>--</c>, length &gt; 2, identifier body).</exception>
    public ComputedSlot GetCustomProperty(string name)
    {
        ThrowIfDisposed();
        ValidateCustomPropertyName(name);
        if (_customProperties is null) return ComputedSlot.Unset;
        return _customProperties.TryGetValue(name, out var slot) ? slot : ComputedSlot.Unset;
    }

    /// <summary>Writes the value of a custom property. Allocates the side dictionary on
    /// first call. As with <see cref="Set"/>, passing <see cref="ComputedSlot.Unset"/>
    /// removes the property rather than leaving an inconsistent entry.</summary>
    /// <exception cref="ArgumentException">When <paramref name="name"/> is not a valid
    /// custom-property name.</exception>
    public void SetCustomProperty(string name, ComputedSlot value)
    {
        ThrowIfDisposed();
        ValidateCustomPropertyName(name);
        if (value.IsUnset)
        {
            _customProperties?.Remove(name);
            return;
        }
        _customProperties ??= new Dictionary<string, ComputedSlot>(StringComparer.Ordinal);
        _customProperties[name] = value;
    }

    /// <summary><see langword="true"/> when <see cref="SetCustomProperty"/> has been called
    /// for <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">When <paramref name="name"/> is not a valid
    /// custom-property name.</exception>
    public bool HasCustomProperty(string name)
    {
        ThrowIfDisposed();
        ValidateCustomPropertyName(name);
        return _customProperties is not null && _customProperties.ContainsKey(name);
    }

    /// <summary>
    /// Validates a custom property name per CSS Custom Properties L1 §2: must start with
    /// <c>--</c>, be longer than the bare <c>--</c> (which is reserved for future use by
    /// the spec), and have an identifier body (ASCII letters, digits, hyphens, underscores).
    /// Full CSS escape support is out of scope for v1; the cascade in Task 7 will canonicalize
    /// before reaching this layer.
    /// </summary>
    private static void ValidateCustomPropertyName(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (name.Length <= 2 || name[0] != '-' || name[1] != '-')
        {
            throw new ArgumentException(
                $"Custom property name '{name}' is invalid. Names must start with '--' and " +
                "have at least one body character (CSS Custom Properties L1 §2).",
                nameof(name));
        }
        // Per CSS Custom Properties L1 §2 + CSS Syntax L3 §4.3.11: custom-property names
        // are <dashed-ident> — `--` followed by valid ident-continue chars. ident-continue
        // includes ASCII letters / digits / '-' / '_' AND non-ASCII chars (>= U+0080).
        // CSS escape sequences (`\41`, `\:`) are also valid in raw source but normalize to
        // their decoded code points before reaching this validator (the parser decodes them);
        // by the time names land here they're already in normalized form, so the validator
        // only needs to accept the resolved character set.
        for (var i = 2; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c >= 'a' && c <= 'z') ||
                     (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') ||
                     c == '-' || c == '_' ||
                     c >= 0x80; // non-ASCII per CSS Syntax L3 §4.3.11
            if (!ok)
            {
                throw new ArgumentException(
                    $"Custom property name '{name}' contains invalid character '{c}' (U+{(int)c:X4}) at position {i}. " +
                    "Body must be a CSS <ident-continue> per Syntax L3 §4.3.11 — letters, digits, '-', '_', or non-ASCII.",
                    nameof(name));
            }
        }
    }

    /// <summary>Number of custom properties set on this element.</summary>
    public int CustomPropertyCount
    {
        get
        {
            ThrowIfDisposed();
            return _customProperties?.Count ?? 0;
        }
    }

    // ------------------------------------------------------------
    // Bitmap helpers
    // ------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GetBit(int index)
    {
        var word = index / BitsPerWord;
        var bit = index % BitsPerWord;
        return (_bitmap[word] & (1UL << bit)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetBit(int index)
    {
        var word = index / BitsPerWord;
        var bit = index % BitsPerWord;
        _bitmap[word] |= 1UL << bit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearBit(int index)
    {
        var word = index / BitsPerWord;
        var bit = index % BitsPerWord;
        _bitmap[word] &= ~(1UL << bit);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ComputedStyle));
    }

    /// <summary>
    /// Inline-array storage for the property slots. The length argument to
    /// <see cref="InlineArrayAttribute"/> must be a compile-time constant —
    /// <see cref="PropertyMetadata.Count"/> is a generated <c>const int</c>, which the
    /// C# compiler accepts here.
    /// </summary>
    [InlineArray(PropertyMetadata.Count)]
    private struct SlotStorage
    {
        // Single field is required for InlineArray; the type acts as N×ComputedSlot.
        private ComputedSlot _slot0;
    }

    /// <summary>
    /// Inline-array storage for the "is-set" bitmap. One bit per property — currently
    /// <see cref="BitmapWordCount"/> = 1 ulong covers up to 64 properties. As the
    /// registry grows past 64, the constant will pull additional ulongs in automatically.
    /// </summary>
    [InlineArray(BitmapWordCount)]
    private struct BitmapStorage
    {
        private ulong _word0;
    }
}
