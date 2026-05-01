// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace NetPdf;

/// <summary>
/// Controls which URI schemes and hosts the resource loader is allowed to fetch from,
/// plus per-resource size / time limits. The default is the BaseUri-sandbox model:
/// file:// reads under <see cref="HtmlPdfOptions.BaseUri"/>'s directory subtree are
/// permitted, but the rest of the filesystem and remote schemes (http/https) are not.
/// This lets typical templates with relative <c>&lt;img src="logo.png"&gt;</c> work
/// out of the box without exposing the host filesystem.
/// </summary>
public sealed class SecurityPolicy
{
    /// <summary>
    /// Allow reading any file via file:// URI. Requires explicit opt-in because it gives
    /// the loader full filesystem access. For sandboxed access to assets under
    /// <see cref="HtmlPdfOptions.BaseUri"/>, prefer <see cref="AllowFileSchemeUnderBaseUri"/>.
    /// </summary>
    public bool AllowFileScheme { get; init; }

    /// <summary>
    /// Allow reading file:// URIs whose path is under the directory of the configured
    /// <see cref="HtmlPdfOptions.BaseUri"/> (and only that subtree — path traversal
    /// outside it is blocked). Default <c>true</c>: lets relative
    /// <c>&lt;img src="logo.png"&gt;</c> work out of the box without exposing the
    /// rest of the filesystem.
    /// </summary>
    public bool AllowFileSchemeUnderBaseUri { get; init; } = true;

    public bool AllowHttpScheme { get; init; }
    public bool AllowHttpsScheme { get; init; }
    public bool AllowDataUri { get; init; } = true;

    /// <summary>
    /// When non-empty and HTTP(S) is allowed, only requests whose host matches one of
    /// these entries are permitted. Wildcards permitted: <c>*.example.com</c>.
    /// </summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    public TimeSpan ResourceTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public long MaxResourceBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>
    /// The default — BaseUri-sandboxed file:// reads, data URIs, no HTTP(S), 10 s timeout,
    /// 25 MB cap. Suitable for most document-rendering scenarios out of the box.
    /// </summary>
    public static SecurityPolicy SafeDefault { get; } = new();
}
