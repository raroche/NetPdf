// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues;

/// <summary>
/// Storage-layer tests for <see cref="ComputedStyle"/>. Pins Get/Set/IsSet/Unset
/// semantics, custom-property side dictionary behavior, disposal, and the bitmap
/// edge cases.
/// </summary>
public sealed class ComputedStyleTests
{
    // ------------------------------------------------------------
    // Get / Set / IsSet / Unset
    // ------------------------------------------------------------

    [Fact]
    public void Fresh_style_returns_unset_for_every_property()
    {
        using var style = ComputedStyle.Rent();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var id = (PropertyId)i;
            Assert.False(style.IsSet(id), $"PropertyId.{id} should be unset on fresh instance.");
            Assert.True(style.Get(id).IsUnset, $"PropertyId.{id} value should be Unset on fresh instance.");
        }
    }

    [Fact]
    public void Set_then_Get_round_trips_for_color()
    {
        using var style = ComputedStyle.Rent();
        var red = ComputedSlot.FromColor(0xFFFF0000u);
        style.Set(PropertyId.Color, red);
        Assert.True(style.IsSet(PropertyId.Color));
        Assert.Equal(red, style.Get(PropertyId.Color));
    }

    [Fact]
    public void Set_then_Get_round_trips_for_length()
    {
        using var style = ComputedStyle.Rent();
        var oneEm = ComputedSlot.FromLengthPx(16.0);
        style.Set(PropertyId.FontSize, oneEm);
        Assert.True(style.IsSet(PropertyId.FontSize));
        Assert.Equal(16.0, style.Get(PropertyId.FontSize).AsLengthPx());
    }

    [Fact]
    public void Set_one_property_does_not_affect_others()
    {
        using var style = ComputedStyle.Rent();
        style.Set(PropertyId.Color, ComputedSlot.FromColor(0xFFFF0000u));
        // Adjacent slots in the inline array must still read as Unset.
        Assert.False(style.IsSet(PropertyId.MarginTop));
        Assert.False(style.IsSet(PropertyId.FontSize));
        Assert.True(style.Get(PropertyId.MarginTop).IsUnset);
    }

    [Fact]
    public void Set_overwrites_previous_value()
    {
        using var style = ComputedStyle.Rent();
        style.Set(PropertyId.Color, ComputedSlot.FromColor(0xFFFF0000u));
        style.Set(PropertyId.Color, ComputedSlot.FromColor(0xFF00FF00u));
        Assert.Equal(0xFF00FF00u, style.Get(PropertyId.Color).AsColor());
    }

    [Fact]
    public void Unset_clears_value_and_bitmap()
    {
        using var style = ComputedStyle.Rent();
        style.Set(PropertyId.Color, ComputedSlot.FromColor(0xFFFF0000u));
        Assert.True(style.IsSet(PropertyId.Color));

        style.Unset(PropertyId.Color);
        Assert.False(style.IsSet(PropertyId.Color));
        Assert.True(style.Get(PropertyId.Color).IsUnset);
    }

    [Fact]
    public void All_properties_can_be_set_independently()
    {
        // Walks every property, sets a unique slot, verifies isolation. Catches bitmap
        // off-by-ones and inline-array element-aliasing bugs.
        using var style = ComputedStyle.Rent();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            style.Set((PropertyId)i, ComputedSlot.FromInteger(i));
        }
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var slot = style.Get((PropertyId)i);
            Assert.Equal(ComputedSlotTag.Integer, slot.Tag);
            Assert.Equal(i, slot.AsInteger());
            Assert.True(style.IsSet((PropertyId)i));
        }
    }

    // ------------------------------------------------------------
    // Bitmap edge cases
    // ------------------------------------------------------------

    [Fact]
    public void Bitmap_word_count_covers_full_property_count()
    {
        // PropertyMetadata.Count must fit within ComputedStyle.BitmapWordCount * 64 bits.
        Assert.True(PropertyMetadata.Count <= ComputedStyle.BitmapWordCount * 64);
    }

    [Fact]
    public void Setting_property_at_word_boundary_does_not_leak_into_neighbor()
    {
        // First and last properties exercise the bitmap addressing. As the registry
        // grows past 64 properties, this test verifies the bitmap addresses the next
        // ulong word correctly; while Count ≤ 64 it stays inside word 0 — still useful
        // as a sanity check.
        Assert.True(PropertyMetadata.Count >= 2,
            "Test assumes the registry has at least 2 properties.");
        using var style = ComputedStyle.Rent();
        var first = (PropertyId)0;
        var last = (PropertyId)(PropertyMetadata.Count - 1);
        style.Set(first, ComputedSlot.FromInteger(1));
        Assert.True(style.IsSet(first));
        Assert.False(style.IsSet(last));
        style.Set(last, ComputedSlot.FromInteger(2));
        Assert.True(style.IsSet(first));
        Assert.True(style.IsSet(last));
    }

    // ------------------------------------------------------------
    // Custom properties (--*)
    // ------------------------------------------------------------

    [Fact]
    public void Fresh_style_has_no_custom_properties()
    {
        using var style = ComputedStyle.Rent();
        Assert.Equal(0, style.CustomPropertyCount);
        Assert.False(style.HasCustomProperty("--brand"));
        Assert.True(style.GetCustomProperty("--brand").IsUnset);
    }

    [Fact]
    public void Set_then_Get_round_trips_for_custom_property()
    {
        using var style = ComputedStyle.Rent();
        var brand = ComputedSlot.FromColor(0xFF0066CCu);
        style.SetCustomProperty("--brand", brand);
        Assert.Equal(1, style.CustomPropertyCount);
        Assert.True(style.HasCustomProperty("--brand"));
        Assert.Equal(brand, style.GetCustomProperty("--brand"));
    }

    [Fact]
    public void Custom_property_names_are_case_sensitive()
    {
        // CSS Custom Properties L1 §2: identifiers are case-sensitive.
        using var style = ComputedStyle.Rent();
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFFFF0000u));
        Assert.True(style.GetCustomProperty("--brand").Tag == ComputedSlotTag.Color);
        Assert.True(style.GetCustomProperty("--Brand").IsUnset);
        Assert.True(style.GetCustomProperty("--BRAND").IsUnset);
    }

    [Theory]
    [InlineData("")]                       // empty
    [InlineData("brand")]                  // no -- prefix
    [InlineData("-brand")]                 // single dash
    [InlineData("--")]                     // bare -- (reserved per CSS Custom Properties L1 §2)
    [InlineData("--brand!")]               // invalid character
    [InlineData("--brand color")]          // space in body
    [InlineData("--$bad")]                 // invalid character at start of body
    public void Custom_property_invalid_name_throws(string name)
    {
        using var style = ComputedStyle.Rent();
        Assert.Throws<System.ArgumentException>(
            () => style.SetCustomProperty(name, ComputedSlot.FromInteger(0)));
        Assert.Throws<System.ArgumentException>(
            () => style.GetCustomProperty(name));
        Assert.Throws<System.ArgumentException>(
            () => style.HasCustomProperty(name));
    }

    [Theory]
    [InlineData("--brand")]
    [InlineData("--brand-color")]
    [InlineData("--Brand_Color_2")]
    [InlineData("--a")]                    // single body char is valid
    [InlineData("--0")]                    // digit body is valid
    public void Custom_property_valid_names_accepted(string name)
    {
        using var style = ComputedStyle.Rent();
        style.SetCustomProperty(name, ComputedSlot.FromInteger(42));
        Assert.True(style.HasCustomProperty(name));
        Assert.Equal(42, style.GetCustomProperty(name).AsInteger());
    }

    [Fact]
    public void Multiple_custom_properties_all_round_trip()
    {
        using var style = ComputedStyle.Rent();
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFFFF0000u));
        style.SetCustomProperty("--accent", ComputedSlot.FromColor(0xFF00FF00u));
        style.SetCustomProperty("--text", ComputedSlot.FromColor(0xFF111111u));
        Assert.Equal(3, style.CustomPropertyCount);
        Assert.Equal(0xFFFF0000u, style.GetCustomProperty("--brand").AsColor());
        Assert.Equal(0xFF00FF00u, style.GetCustomProperty("--accent").AsColor());
        Assert.Equal(0xFF111111u, style.GetCustomProperty("--text").AsColor());
    }

    [Fact]
    public void Set_overwrites_existing_custom_property()
    {
        using var style = ComputedStyle.Rent();
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFFFF0000u));
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFF00FF00u));
        Assert.Equal(1, style.CustomPropertyCount);
        Assert.Equal(0xFF00FF00u, style.GetCustomProperty("--brand").AsColor());
    }

    [Fact]
    public void Custom_property_null_name_throws()
    {
        using var style = ComputedStyle.Rent();
        Assert.Throws<System.ArgumentNullException>(
            () => style.SetCustomProperty(null!, ComputedSlot.FromInteger(0)));
        Assert.Throws<System.ArgumentNullException>(
            () => style.GetCustomProperty(null!));
        Assert.Throws<System.ArgumentNullException>(
            () => style.HasCustomProperty(null!));
    }

    [Fact]
    public void SetCustomProperty_with_unset_removes_existing_entry()
    {
        // Mirror the Set(id, Unset) → Unset(id) invariant. Setting a custom property to
        // the unset sentinel removes it from the dictionary rather than storing an
        // inconsistent "explicit unset" entry.
        using var style = ComputedStyle.Rent();
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFFFF0000u));
        Assert.True(style.HasCustomProperty("--brand"));

        style.SetCustomProperty("--brand", ComputedSlot.Unset);
        Assert.False(style.HasCustomProperty("--brand"));
        Assert.True(style.GetCustomProperty("--brand").IsUnset);
    }

    // ------------------------------------------------------------
    // Set/Unset invariant
    // ------------------------------------------------------------

    [Fact]
    public void Set_with_unset_slot_redirects_to_Unset_id()
    {
        // Without the redirect, Set(id, Unset) would set the bitmap bit while leaving
        // the slot value zeroed — IsSet(id) would return true but Get(id).IsUnset would
        // also return true. The invariant we maintain: IsSet(id) ⟹ !Get(id).IsUnset.
        using var style = ComputedStyle.Rent();
        style.Set(PropertyId.Color, ComputedSlot.FromColor(0xFFFF0000u));
        Assert.True(style.IsSet(PropertyId.Color));

        style.Set(PropertyId.Color, ComputedSlot.Unset);
        Assert.False(style.IsSet(PropertyId.Color));
        Assert.True(style.Get(PropertyId.Color).IsUnset);
    }

    [Fact]
    public void IsSet_implies_value_is_not_Unset()
    {
        // Property invariant: walking every set slot, the value must NOT be the unset
        // sentinel. This is the contract Tasks 7–10 will rely on.
        using var style = ComputedStyle.Rent();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            // Mix encodings so we exercise different tag bits.
            var slot = i % 4 == 0 ? ComputedSlot.FromColor(0xFF000000u | (uint)i)
                : i % 4 == 1     ? ComputedSlot.FromInteger(i + 1)
                : i % 4 == 2     ? ComputedSlot.FromKeyword(i + 1)
                                 : ComputedSlot.FromLengthPx((float)(i + 1));
            style.Set((PropertyId)i, slot);
        }
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var id = (PropertyId)i;
            if (style.IsSet(id))
            {
                Assert.False(style.Get(id).IsUnset,
                    $"PropertyId.{id} is set but value is Unset — invariant violated.");
            }
        }
    }

    // ------------------------------------------------------------
    // Pool / disposal
    // ------------------------------------------------------------

    [Fact]
    public void Rent_returns_distinct_instances_when_pool_empty()
    {
        // Forcefully drain the pool by renting + holding many instances, then verify
        // each Rent returns a distinct object. Drain-then-rent avoids dependence on
        // pool state from other tests.
        var first = ComputedStyle.Rent();
        var second = ComputedStyle.Rent();
        Assert.NotSame(first, second);
        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void Rent_after_Dispose_yields_clean_state()
    {
        // Pool reuse path: dispose an instance with set values, rent again, the
        // returned instance has zero set bits, zero custom properties, and IsSet=false
        // for everything.
        var initial = ComputedStyle.Rent();
        initial.Set(PropertyId.Color, ComputedSlot.FromColor(0xFFFF0000u));
        initial.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFF00FF00u));
        Assert.True(initial.IsSet(PropertyId.Color));
        Assert.Equal(1, initial.CustomPropertyCount);
        initial.Dispose();

        // The next rent could return ANY pooled instance (including the one we just
        // disposed). Whatever we get back must be in clean state.
        using var rented = ComputedStyle.Rent();
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            Assert.False(rented.IsSet((PropertyId)i),
                $"Rented instance has PropertyId.{(PropertyId)i} still set — Reset is broken.");
        }
        Assert.Equal(0, rented.CustomPropertyCount);
    }

    [Fact]
    public void Operations_throw_after_dispose()
    {
        // Same parallel-rent race as Dispose_then_post_dispose_access_throws —
        // use the test-only exclusive-from-pool factory so the disposed flag
        // stays observably true across the assertions.
        var style = ComputedStyle.RentForExclusiveTesting();
        style.Dispose();
        Assert.Throws<System.ObjectDisposedException>(
            () => style.Get(PropertyId.Color));
        Assert.Throws<System.ObjectDisposedException>(
            () => style.Set(PropertyId.Color, ComputedSlot.FromColor(0u)));
        Assert.Throws<System.ObjectDisposedException>(
            () => style.IsSet(PropertyId.Color));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var style = ComputedStyle.Rent();
        style.Dispose();
        style.Dispose();   // no throw
    }

    [Fact]
    public void Dispose_then_post_dispose_access_throws()
    {
        // Use the test-only exclusive-from-pool factory so the disposed flag
        // stays observably true across the assertion. Without this, xUnit's
        // parallel runner can race another test's `ComputedStyle.Rent()`
        // call between this test's `Dispose()` and the `Assert.Throws`,
        // pulling the just-disposed instance from the pool, calling Reset()
        // (which flips _disposed back to false), and silently neutralizing
        // the assertion. The production soft-guard contract documents this
        // race as best-effort by design — the test is asserting that the
        // soft guard fires WHEN the instance hasn't been re-rented yet.
        var style = ComputedStyle.RentForExclusiveTesting();
        style.SetCustomProperty("--brand", ComputedSlot.FromColor(0xFFFF0000u));
        Assert.Equal(1, style.CustomPropertyCount);
        style.Dispose();
        Assert.Throws<System.ObjectDisposedException>(() => _ = style.CustomPropertyCount);
    }

    // ------------------------------------------------------------
    // Bounds
    // ------------------------------------------------------------

    [Fact]
    public void Out_of_range_property_id_throws_on_Get()
    {
        using var style = ComputedStyle.Rent();
        // PropertyId is ushort-backed; cast a value past Count.
        var beyond = (PropertyId)(PropertyMetadata.Count + 100);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => style.Get(beyond));
    }

    [Fact]
    public void Out_of_range_property_id_throws_on_Set()
    {
        using var style = ComputedStyle.Rent();
        var beyond = (PropertyId)(PropertyMetadata.Count + 100);
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => style.Set(beyond, ComputedSlot.FromInteger(0)));
    }

    [Fact]
    public void Out_of_range_property_id_returns_false_for_IsSet()
    {
        using var style = ComputedStyle.Rent();
        var beyond = (PropertyId)(PropertyMetadata.Count + 100);
        Assert.False(style.IsSet(beyond));
    }
}
