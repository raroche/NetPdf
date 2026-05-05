// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System;
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
/// current 63-property registry). Pool via <see cref="Rent"/> / <see cref="IDisposable.Dispose"/>
/// so per-document churn stays GC-quiet.
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

    /// <summary>
    /// Allocates a fresh <see cref="ComputedStyle"/>. Currently a plain <c>new</c> —
    /// the API is shaped as <c>Rent</c> so a future ObjectPool integration can drop in
    /// without changing call sites. Cleared to all-Unset before return.
    /// </summary>
    public static ComputedStyle Rent() => new();

    /// <summary>
    /// Returns the instance to the pool. Currently no-op (the GC reclaims). Releases
    /// the lazy custom-property dictionary so the next consumer doesn't see stale entries.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _customProperties = null;
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

    /// <summary>Writes the slot for <paramref name="id"/> and marks it as explicitly set.</summary>
    public void Set(PropertyId id, ComputedSlot value)
    {
        ThrowIfDisposed();
        var index = (int)id;
        if ((uint)index >= (uint)PropertyMetadata.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"PropertyId {id} (index {index}) is outside the registry (Count = {PropertyMetadata.Count}).");
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
    public ComputedSlot GetCustomProperty(string name)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_customProperties is null) return ComputedSlot.Unset;
        return _customProperties.TryGetValue(name, out var slot) ? slot : ComputedSlot.Unset;
    }

    /// <summary>Writes the value of a custom property. Allocates the side dictionary on
    /// first call.</summary>
    public void SetCustomProperty(string name, ComputedSlot value)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(name);
        _customProperties ??= new Dictionary<string, ComputedSlot>(StringComparer.Ordinal);
        _customProperties[name] = value;
    }

    /// <summary><see langword="true"/> when <see cref="SetCustomProperty"/> has been called
    /// for <paramref name="name"/>.</summary>
    public bool HasCustomProperty(string name)
    {
        ThrowIfDisposed();
        if (_customProperties is null || string.IsNullOrEmpty(name)) return false;
        return _customProperties.ContainsKey(name);
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
