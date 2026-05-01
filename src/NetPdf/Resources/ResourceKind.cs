// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// What the resource will be used as. Loaders may apply different policies per kind
/// (e.g., size limits, MIME validation).
/// </summary>
public enum ResourceKind
{
    Image,
    Font,
    Stylesheet,
    Other,
}
