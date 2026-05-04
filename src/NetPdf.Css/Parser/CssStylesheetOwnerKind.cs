// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf.Css.Parser;

/// <summary>
/// Where a stylesheet was attached in the document. Used by diagnostics + cascade for
/// reporting and for tracing imported chains.
/// </summary>
internal enum CssStylesheetOwnerKind
{
    /// <summary>Unknown / synthetic / programmatically constructed.</summary>
    Unknown = 0,
    /// <summary>An inline <c>&lt;style&gt;</c> element.</summary>
    StyleElement = 1,
    /// <summary>An external <c>&lt;link rel="stylesheet"&gt;</c>.</summary>
    LinkElement = 2,
    /// <summary>A sheet pulled in by <c>@import</c> from another sheet.</summary>
    Imported = 3,
    /// <summary>A user-agent (browser-default) stylesheet.</summary>
    UserAgent = 4,
}
