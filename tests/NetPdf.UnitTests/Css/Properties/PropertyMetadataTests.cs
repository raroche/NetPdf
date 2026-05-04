// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.Properties;

/// <summary>
/// Tests for the source-generator-emitted <see cref="PropertyMetadata"/> table and
/// <see cref="PropertyId"/> enum. Asserts the generator produces a usable lookup with
/// every property declared in <c>properties.json</c>, that the enum values double as
/// <c>Table</c> indices, and that the case-insensitive name lookup works.
/// </summary>
public sealed class PropertyMetadataTests
{
    [Fact]
    public void Count_matches_the_table_length()
    {
        Assert.Equal(PropertyMetadata.Count, PropertyMetadata.Table.Length);
    }

    [Fact]
    public void Count_matches_the_properties_json_entry_count()
    {
        // Drift catcher: if someone adds a property to properties.json without rebuilding
        // (or regenerates without the new property), the test catches the mismatch.
        var json = LoadPropertiesJson();
        // Count entries by counting the "name": occurrences inside the properties array.
        // Cheap-and-cheerful since our schema is uniform.
        var nameCount = Regex.Matches(json, @"""name""\s*:\s*""[^""]+""").Count;
        Assert.Equal(PropertyMetadata.Count, nameCount);
    }

    [Fact]
    public void Enum_values_are_zero_indexed_and_match_table_positions()
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            Assert.Equal((PropertyId)i, PropertyMetadata.Table[i].Id);
        }
    }

    [Fact]
    public void NameToId_resolves_color_to_color_property()
    {
        Assert.Equal(PropertyId.Color, PropertyMetadata.NameToId["color"]);
    }

    [Fact]
    public void NameToId_is_case_insensitive()
    {
        Assert.Equal(PropertyId.Color, PropertyMetadata.NameToId["COLOR"]);
        Assert.Equal(PropertyId.MarginTop, PropertyMetadata.NameToId["Margin-Top"]);
    }

    [Fact]
    public void NameToId_returns_false_for_unknown_property()
    {
        Assert.False(PropertyMetadata.NameToId.ContainsKey("not-a-real-property"));
    }

    [Fact]
    public void NameToId_count_matches_table_count()
    {
        Assert.Equal(PropertyMetadata.Count, PropertyMetadata.NameToId.Count);
    }

    // ------------------------------------------------------------
    // Per-property metadata pins (representative subset)
    // ------------------------------------------------------------

    [Fact]
    public void Color_property_inherits_per_spec()
    {
        var color = PropertyMetadata.Table[(int)PropertyId.Color];
        Assert.Equal("color", color.Name);
        Assert.Equal(PropertyType.Color, color.Type);
        Assert.True(color.Inherits);
        Assert.Equal(AppliesTo.All, color.AppliesTo);
        Assert.Equal(ComputedValueKind.AbsoluteColor, color.Computed);
    }

    [Fact]
    public void MarginTop_does_not_inherit()
    {
        var marginTop = PropertyMetadata.Table[(int)PropertyId.MarginTop];
        Assert.Equal("margin-top", marginTop.Name);
        Assert.Equal(PropertyType.LengthPercentageAuto, marginTop.Type);
        Assert.False(marginTop.Inherits);
        Assert.Equal("0", marginTop.DefaultValue);
    }

    [Fact]
    public void FontFamily_inherits_with_serif_default()
    {
        var fontFamily = PropertyMetadata.Table[(int)PropertyId.FontFamily];
        Assert.True(fontFamily.Inherits);
        Assert.Equal(PropertyType.FontFamilyList, fontFamily.Type);
        Assert.Equal("serif", fontFamily.DefaultValue);
    }

    [Fact]
    public void Position_property_uses_static_default()
    {
        var position = PropertyMetadata.Table[(int)PropertyId.Position];
        Assert.Equal("static", position.DefaultValue);
        Assert.False(position.Inherits);
    }

    [Fact]
    public void All_table_entries_have_non_empty_names()
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            Assert.False(string.IsNullOrEmpty(PropertyMetadata.Table[i].Name),
                $"PropertyId {(PropertyId)i} has empty name");
        }
    }

    [Fact]
    public void All_table_entries_have_non_unknown_type()
    {
        // The Unknown type is the placeholder for missing-from-json entries. Catch any
        // properties.json entry that lacks (or misspells) its type.
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            Assert.True(meta.Type != PropertyType.Unknown,
                $"Property '{meta.Name}' has Unknown type — check properties.json 'type' field.");
        }
    }

    [Fact]
    public void All_table_entries_have_non_unknown_applies_to()
    {
        for (var i = 0; i < PropertyMetadata.Count; i++)
        {
            var meta = PropertyMetadata.Table[i];
            Assert.True(meta.AppliesTo != AppliesTo.Unknown,
                $"Property '{meta.Name}' has Unknown AppliesTo — check properties.json 'applies_to' field.");
        }
    }

    private static string LoadPropertiesJson()
    {
        // Walk up from the test assembly to find src/NetPdf.Css/properties.json.
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NetPdf.Css", "properties.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate src/NetPdf.Css/properties.json walking up from " + System.AppContext.BaseDirectory);
    }
}
