// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Response returned by <see cref="IResourceLoader"/>. Use <c>default</c>
/// (an empty <see cref="Content"/>) to indicate the resource was not found.
/// </summary>
public readonly record struct ResourceResponse
{
    public required ReadOnlyMemory<byte> Content { get; init; }
    public string? MimeType { get; init; }
    public string? CharSet { get; init; }
}
