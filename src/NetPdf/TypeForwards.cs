// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Runtime.CompilerServices;

// Binary-compatibility type forwards. HyphenationRegistry first shipped in the NetPdf facade assembly
// (git tag 0.9.0-rc1) and moved to NetPdf.Text in Phase 5 so NetPdf.Layout can consult it for lang-based
// hyphenation routing. Both DLLs ship inside the single NetPdf NuGet package, so a consumer compiled
// against the type's original home (`[NetPdf]NetPdf.Hyphenation.HyphenationRegistry`) keeps resolving it via
// this forward rather than hitting a TypeLoadException. The repo is private + unpublished before 1.0, so no
// external consumer depends on this yet — it is preventative hygiene for the move (and the standard pattern
// for relocating a public type between assemblies).
[assembly: TypeForwardedTo(typeof(NetPdf.Hyphenation.HyphenationRegistry))]
