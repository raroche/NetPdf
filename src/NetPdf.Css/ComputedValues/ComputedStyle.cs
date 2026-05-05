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

    /// <summary>
    /// Marks the instance disposed and queues it back to the pool for re-rental
    /// (subject to <see cref="MaxPoolSize"/>). Idempotent. The slot/bitmap clearing
    /// happens lazily on the next <see cref="Rent"/> via <see cref="Reset"/>, not here,
    /// so disposing is cheap.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pool.Count < MaxPoolSize)
        {
            _pool.Add(this);
        }
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
            return;
        }
        _slots[index] = value;
        SetBit(index);
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
        for (var i = 2; i < name.Length; i++)
        {
            var c = name[i];
            var ok = (c >= 'a' && c <= 'z') ||
                     (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') ||
                     c == '-' || c == '_';
            if (!ok)
            {
                throw new ArgumentException(
                    $"Custom property name '{name}' contains invalid character '{c}' at position {i}. " +
                    "Body must be an ASCII identifier (letters / digits / '-' / '_'). Full CSS " +
                    "escape support is out of scope for v1.",
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
