// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// The page layout a PDF reader uses when it first opens the document (ISO 32000-2 §7.7.2, catalog
/// <c>/PageLayout</c>). Controls how many pages are shown side by side and whether the first page is
/// treated as a cover.
/// </summary>
public enum PdfPageLayout
{
    /// <summary>One page at a time (most readers' default).</summary>
    SinglePage,

    /// <summary>Pages in one continuous vertical column.</summary>
    OneColumn,

    /// <summary>Two continuous columns, odd-numbered pages on the left.</summary>
    TwoColumnLeft,

    /// <summary>Two continuous columns, odd-numbered pages on the right (cover on its own).</summary>
    TwoColumnRight,

    /// <summary>Two pages side by side, odd-numbered pages on the left.</summary>
    TwoPageLeft,

    /// <summary>Two pages side by side, odd-numbered pages on the right (cover on its own).</summary>
    TwoPageRight,
}

/// <summary>
/// How a PDF reader's navigation UI is presented when the document first opens (ISO 32000-2 §7.7.2,
/// catalog <c>/PageMode</c>) — e.g. whether the bookmarks (outline) panel is open.
/// </summary>
public enum PdfPageMode
{
    /// <summary>Neither the outline nor thumbnails panel is shown (readers' default).</summary>
    UseNone,

    /// <summary>Open with the document outline (bookmarks) panel visible.</summary>
    UseOutlines,

    /// <summary>Open with the page-thumbnails panel visible.</summary>
    UseThumbs,

    /// <summary>Open in full-screen presentation mode (no menu bar, window controls, or panels).</summary>
    FullScreen,
}
