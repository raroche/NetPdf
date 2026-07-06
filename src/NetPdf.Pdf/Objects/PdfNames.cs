// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Pdf.Objects;

/// <summary>
/// Pre-allocated <see cref="PdfName"/> instances for the standard PDF names that NetPdf emits.
/// Held as static readonly so reference equality is reliable and allocation pressure is zero
/// for these (which appear in nearly every PDF object NetPdf produces).
/// </summary>
internal static class PdfNames
{
    // Document structure
    public static readonly PdfName Type = new("Type");
    public static readonly PdfName Subtype = new("Subtype");
    public static readonly PdfName Catalog = new("Catalog");
    public static readonly PdfName Pages = new("Pages");
    public static readonly PdfName Page = new("Page");
    public static readonly PdfName Kids = new("Kids");
    public static readonly PdfName Count = new("Count");
    public static readonly PdfName Parent = new("Parent");

    // Page geometry
    public static readonly PdfName MediaBox = new("MediaBox");
    public static readonly PdfName CropBox = new("CropBox");
    public static readonly PdfName BleedBox = new("BleedBox");
    public static readonly PdfName TrimBox = new("TrimBox");
    public static readonly PdfName ArtBox = new("ArtBox");
    public static readonly PdfName Rotate = new("Rotate");
    public static readonly PdfName Resources = new("Resources");
    public static readonly PdfName Contents = new("Contents");

    // Tiling patterns (tiling-patterns cycle, ISO 32000-2 §8.7.3)
    public static readonly PdfName Pattern = new("Pattern");
    public static readonly PdfName PatternType = new("PatternType");
    public static readonly PdfName PaintType = new("PaintType");
    public static readonly PdfName TilingType = new("TilingType");
    public static readonly PdfName BBox = new("BBox");
    public static readonly PdfName XStep = new("XStep");
    public static readonly PdfName YStep = new("YStep");
    public static readonly PdfName Matrix = new("Matrix");

    // Shadings + functions (Phase 4 gradients, ISO 32000-2 §8.7.4.5 + §7.10)
    public static readonly PdfName Shading = new("Shading");
    public static readonly PdfName ShadingType = new("ShadingType");
    public static readonly PdfName Coords = new("Coords");
    public static readonly PdfName Extend = new("Extend");
    public static readonly PdfName Function = new("Function");
    public static readonly PdfName FunctionType = new("FunctionType");
    public static readonly PdfName Domain = new("Domain");
    public static readonly PdfName Range = new("Range");
    public static readonly PdfName C0 = new("C0");
    public static readonly PdfName C1 = new("C1");
    public static readonly PdfName N = new("N");
    public static readonly PdfName Functions = new("Functions");
    public static readonly PdfName Bounds = new("Bounds");
    public static readonly PdfName Encode = new("Encode");

    // Streams
    public static readonly PdfName Length = new("Length");
    public static readonly PdfName Filter = new("Filter");
    public static readonly PdfName FlateDecode = new("FlateDecode");
    public static readonly PdfName DCTDecode = new("DCTDecode");
    public static readonly PdfName CCITTFaxDecode = new("CCITTFaxDecode");
    public static readonly PdfName ASCII85Decode = new("ASCII85Decode");
    public static readonly PdfName DecodeParms = new("DecodeParms");

    // Fonts
    public static readonly PdfName Font = new("Font");
    public static readonly PdfName BaseFont = new("BaseFont");
    public static readonly PdfName Encoding = new("Encoding");
    public static readonly PdfName ToUnicode = new("ToUnicode");
    public static readonly PdfName FontDescriptor = new("FontDescriptor");
    public static readonly PdfName FontFile = new("FontFile");
    public static readonly PdfName FontFile2 = new("FontFile2");
    public static readonly PdfName FontFile3 = new("FontFile3");
    public static readonly PdfName Type0 = new("Type0");
    public static readonly PdfName Type1 = new("Type1");
    public static readonly PdfName TrueType = new("TrueType");
    public static readonly PdfName CIDFontType0 = new("CIDFontType0");
    public static readonly PdfName CIDFontType2 = new("CIDFontType2");
    public static readonly PdfName CIDSystemInfo = new("CIDSystemInfo");
    public static readonly PdfName CIDToGIDMap = new("CIDToGIDMap");
    public static readonly PdfName DescendantFonts = new("DescendantFonts");
    public static readonly PdfName Identity = new("Identity");
    public static readonly PdfName IdentityH = new("Identity-H");
    public static readonly PdfName IdentityV = new("Identity-V");
    public static readonly PdfName WinAnsiEncoding = new("WinAnsiEncoding");
    public static readonly PdfName Registry = new("Registry");
    public static readonly PdfName Ordering = new("Ordering");
    public static readonly PdfName Supplement = new("Supplement");

    // Font descriptor metrics
    public static readonly PdfName FontName = new("FontName");
    public static readonly PdfName Flags = new("Flags");
    public static readonly PdfName FontBBox = new("FontBBox");
    public static readonly PdfName ItalicAngle = new("ItalicAngle");
    public static readonly PdfName Ascent = new("Ascent");
    public static readonly PdfName Descent = new("Descent");
    public static readonly PdfName Leading = new("Leading");
    public static readonly PdfName CapHeight = new("CapHeight");
    public static readonly PdfName XHeight = new("XHeight");
    public static readonly PdfName StemV = new("StemV");
    public static readonly PdfName StemH = new("StemH");
    public static readonly PdfName AvgWidth = new("AvgWidth");
    public static readonly PdfName MaxWidth = new("MaxWidth");
    public static readonly PdfName MissingWidth = new("MissingWidth");
    public static readonly PdfName W = new("W");
    public static readonly PdfName DW = new("DW");
    public static readonly PdfName Length1 = new("Length1");

    // Images / XObjects
    public static readonly PdfName XObject = new("XObject");
    public static readonly PdfName Image = new("Image");
    public static readonly PdfName Form = new("Form");
    public static readonly PdfName Width = new("Width");
    public static readonly PdfName Height = new("Height");
    public static readonly PdfName BitsPerComponent = new("BitsPerComponent");
    public static readonly PdfName ColorSpace = new("ColorSpace");
    public static readonly PdfName DeviceRGB = new("DeviceRGB");
    public static readonly PdfName DeviceGray = new("DeviceGray");
    public static readonly PdfName DeviceCMYK = new("DeviceCMYK");
    public static readonly PdfName ICCBased = new("ICCBased");
    public static readonly PdfName Indexed = new("Indexed");
    public static readonly PdfName SMask = new("SMask");
    public static readonly PdfName Mask = new("Mask");
    public static readonly PdfName Decode = new("Decode");
    public static readonly PdfName Intent = new("Intent");

    // FlateDecode parameters (PDF 32000-2:2020 §7.4.4 Table 8).
    public static readonly PdfName Predictor = new("Predictor");
    public static readonly PdfName Columns = new("Columns");
    public static readonly PdfName Colors = new("Colors");

    // Document metadata
    public static readonly PdfName Title = new("Title");
    public static readonly PdfName Author = new("Author");
    public static readonly PdfName Subject = new("Subject");
    public static readonly PdfName Keywords = new("Keywords");
    public static readonly PdfName Producer = new("Producer");
    public static readonly PdfName Creator = new("Creator");
    public static readonly PdfName CreationDate = new("CreationDate");
    public static readonly PdfName ModDate = new("ModDate");

    // Catalog document properties + XMP metadata stream
    public static readonly PdfName Lang = new("Lang");
    public static readonly PdfName ViewerPreferences = new("ViewerPreferences");
    public static readonly PdfName DisplayDocTitle = new("DisplayDocTitle");
    public static readonly PdfName Metadata = new("Metadata");
    public static readonly PdfName XML = new("XML");

    // Trailer
    public static readonly PdfName Root = new("Root");
    public static readonly PdfName Info = new("Info");
    public static readonly PdfName Size = new("Size");
    public static readonly PdfName ID = new("ID");
    public static readonly PdfName Prev = new("Prev");
    public static readonly PdfName Encrypt = new("Encrypt");

    // Annotations
    public static readonly PdfName Annot = new("Annot");
    public static readonly PdfName Annots = new("Annots");
    public static readonly PdfName Link = new("Link");
    public static readonly PdfName URI = new("URI");
    public static readonly PdfName A = new("A");
    public static readonly PdfName S = new("S");
    public static readonly PdfName Rect = new("Rect");
    public static readonly PdfName Border = new("Border");

    // Tagged PDF / structure
    public static readonly PdfName StructTreeRoot = new("StructTreeRoot");
    public static readonly PdfName MarkInfo = new("MarkInfo");
    public static readonly PdfName Marked = new("Marked");
    public static readonly PdfName StructElem = new("StructElem");
    public static readonly PdfName K = new("K");
    public static readonly PdfName P = new("P");
    public static readonly PdfName MCID = new("MCID");

    // Outlines
    public static readonly PdfName Outlines = new("Outlines");
    public static readonly PdfName First = new("First");
    public static readonly PdfName Last = new("Last");
    public static readonly PdfName Next = new("Next");
    public static readonly PdfName Dest = new("Dest");

    // ExtGState (transparency)
    public static readonly PdfName ExtGState = new("ExtGState");
    public static readonly PdfName CA = new("CA");
    public static readonly PdfName ca = new("ca");
    public static readonly PdfName BM = new("BM");
}
