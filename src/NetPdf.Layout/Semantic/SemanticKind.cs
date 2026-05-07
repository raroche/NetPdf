// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Layout.Semantic;

/// <summary>
/// The semantic role a <see cref="SemanticNode"/> represents — derived from the
/// originating HTML element's tag (and a few attributes). Mirrors the structural
/// roles PDF/UA's tagged-structure tree uses (per ISO 32000-2:2020 §14.8.4
/// "Standard Structure Types") so v1.1's <c>NetPdf.Pdf.StructTree*</c> can
/// emit /P, /H1, /Lbl etc. directly off this tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cycle-1 scope.</b> The set covers the common content-bearing roles called
/// out in the Phase 2 doc — H1–H6, P, L/LI, Table/TR/TH/TD, Link, Figure,
/// BlockQuote, Code, and the HTML5 sectioning families (header / footer / nav /
/// main / aside / article / section). Out of scope: form-control roles
/// (Form/Field), MathML, SVG-figure metadata, RUBY (Ruby/RB/RT/RP), TOC
/// hierarchy, and the printer-mark roles (post-v1).
/// </para>
/// <para>
/// <b>Why explicit Heading1..Heading6.</b> PDF/UA's standard structure types
/// distinguish each heading level (/H1 through /H6) — encoding the level in the
/// kind avoids a separate Level property + makes the byte enum self-describing
/// for downstream paint code.
/// </para>
/// <para>
/// <b>Transparent elements.</b> <c>&lt;div&gt;</c>, <c>&lt;span&gt;</c>,
/// <c>&lt;thead&gt;</c>, <c>&lt;tbody&gt;</c>, <c>&lt;tfoot&gt;</c>, and other
/// purely-structural HTML elements have no semantic kind — the walker
/// flattens their children into the parent's child list. Authors who want a
/// semantic role on a generic container should use ARIA's <c>role="..."</c>
/// (cycle 2) or the appropriate HTML5 sectioning element.
/// </para>
/// </remarks>
internal enum SemanticKind : byte
{
    /// <summary>Root of the semantic tree — corresponds to the document
    /// itself. Always anonymous (no source element); created once by the
    /// builder.</summary>
    Document = 0,

    // ============================================================
    // Headings — PDF/UA /H1../H6
    // ============================================================

    /// <summary><c>&lt;h1&gt;</c> — top-level heading.</summary>
    Heading1,
    /// <summary><c>&lt;h2&gt;</c>.</summary>
    Heading2,
    /// <summary><c>&lt;h3&gt;</c>.</summary>
    Heading3,
    /// <summary><c>&lt;h4&gt;</c>.</summary>
    Heading4,
    /// <summary><c>&lt;h5&gt;</c>.</summary>
    Heading5,
    /// <summary><c>&lt;h6&gt;</c>.</summary>
    Heading6,

    // ============================================================
    // Block-level prose
    // ============================================================

    /// <summary><c>&lt;p&gt;</c> — paragraph (PDF/UA /P).</summary>
    Paragraph,
    /// <summary><c>&lt;blockquote&gt;</c> — block quotation (PDF/UA /BlockQuote).</summary>
    BlockQuote,
    /// <summary><c>&lt;code&gt;</c> / <c>&lt;pre&gt;</c> — preformatted /
    /// inline code (PDF/UA /Code).</summary>
    Code,

    // ============================================================
    // Lists — PDF/UA /L + /LI
    // ============================================================

    /// <summary><c>&lt;ul&gt;</c> / <c>&lt;ol&gt;</c> / <c>&lt;menu&gt;</c> —
    /// list container (PDF/UA /L).</summary>
    List,
    /// <summary><c>&lt;li&gt;</c> — list item (PDF/UA /LI).</summary>
    ListItem,

    // ============================================================
    // Tables — PDF/UA /Table + /TR + /TH + /TD + /Caption
    // ============================================================

    /// <summary><c>&lt;table&gt;</c> — table container (PDF/UA /Table).</summary>
    Table,
    /// <summary><c>&lt;tr&gt;</c> — table row (PDF/UA /TR).</summary>
    TableRow,
    /// <summary><c>&lt;th&gt;</c> — header cell (PDF/UA /TH).</summary>
    TableHeaderCell,
    /// <summary><c>&lt;td&gt;</c> — data cell (PDF/UA /TD).</summary>
    TableCell,
    /// <summary><c>&lt;caption&gt;</c> — table caption (PDF/UA /Caption).</summary>
    TableCaption,

    // ============================================================
    // Inline + embedded media
    // ============================================================

    /// <summary><c>&lt;a href&gt;</c> — hyperlink (PDF/UA /Link). Carries
    /// an <see cref="SemanticNode.Href"/> value.</summary>
    Link,
    /// <summary><c>&lt;img&gt;</c> — replaced image (PDF/UA /Figure leaf).
    /// Carries an <see cref="SemanticNode.AltText"/> from the
    /// <c>alt</c> / <c>aria-label</c> attribute.</summary>
    Image,
    /// <summary><c>&lt;figure&gt;</c> — figure container with optional
    /// caption (PDF/UA /Figure). May carry an <see cref="SemanticNode.AltText"/>
    /// from <c>aria-label</c> or its child <c>&lt;figcaption&gt;</c>.</summary>
    Figure,
    /// <summary><c>&lt;figcaption&gt;</c> — figure caption (PDF/UA /Caption
    /// inside /Figure).</summary>
    FigureCaption,

    // ============================================================
    // HTML5 sectioning — PDF/UA collapses these to /Sect, but we keep
    // the HTML5 distinctions so downstream consumers (e.g., navigation
    // outline generation) can attach role-specific behaviour.
    // ============================================================

    /// <summary><c>&lt;header&gt;</c>.</summary>
    Header,
    /// <summary><c>&lt;footer&gt;</c>.</summary>
    Footer,
    /// <summary><c>&lt;nav&gt;</c>.</summary>
    Nav,
    /// <summary><c>&lt;main&gt;</c>.</summary>
    Main,
    /// <summary><c>&lt;aside&gt;</c>.</summary>
    Aside,
    /// <summary><c>&lt;article&gt;</c>.</summary>
    Article,
    /// <summary><c>&lt;section&gt;</c>.</summary>
    Section,
}
