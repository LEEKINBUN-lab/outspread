# Third-party notice

This file lists the third-party components Outspread depends on and the state of
their licence verification, for anyone distributing the app or its sources.
Regenerate it whenever a `.csproj`, `global.json`, or a vendored asset changes:
the enumeration below was read from the four project files under `src/` and
`tests/`, from `global.json`, and from the resolved graphs in
`src/Outspread.Board/obj/project.assets.json`, not from general knowledge.

A licence marked "verified" was confirmed by reading a licence file or nuspec
declaration in the restored package in the local NuGet cache at the pinned
version. A licence marked "verify before redistribution" was **not** confirmed
from any file in this environment — do not assume a term for those rows.

## Toolchain

- .NET SDK `10.0.401`, `rollForward: latestFeature` (`global.json`).
- Target frameworks: `net10.0` (Core, Cli, Tests);
  `net10.0-windows10.0.26100.0`, x64, unpackaged (Board).

## Direct NuGet packages

Board (`src/Outspread.Board/Outspread.Board.csproj`):

- `Microsoft.WindowsAppSDK` 2.5.1 — verified: `license.txt` in the restored
  package is the Microsoft Software License Terms (proprietary, not open source).
- `Microsoft.Windows.SDK.BuildTools` 10.0.28000.2705 — licence: verify before
  redistribution. The nuspec carries no licence expression, only a `licenseUrl`
  pointing to `https://aka.ms/WinSDKLicenseURL`, and no licence file ships in
  the package as restored here.
- `QRCoder` 1.8.0 — verified: `LICENSE.txt` in the restored package is The MIT
  License (copyright 2013-2025 Raffael Herrmann, 2024-2025 Shane Krueger). Same
  pin as the CLI's: it turns the pairing payload into a module grid for the
  in-window sheet (`PairingPanel.cs`); the grid-to-shapes drawing is first-party.

Tests (`tests/Outspread.Core.Tests/Outspread.Core.Tests.csproj`, test-only,
not shipped in the app):

- `Microsoft.NET.Test.Sdk` 17.14.1 — licence: verify before redistribution.
  The nuspec carries no licence expression, only a deprecated `licenseUrl`.
- `xunit` 2.9.3 — verified: nuspec declares `Apache-2.0`.
- `xunit.runner.visualstudio` 2.8.2 — verified: nuspec declares `Apache-2.0`.

Cli (`src/Outspread.Cli/outspread.csproj`):

- `QRCoder` 1.8.0 — verified: `LICENSE.txt` in the restored package is The MIT
  License, copyright 2013-2025 Raffael Herrmann and 2024-2025 Shane Krueger; the
  nuspec's `licenseUrl` points at `https://licenses.nuget.org/MIT`. Used only to
  turn a pairing payload into a module grid; the grid-to-terminal-glyphs mapping
  is first-party and dependency-free (`src/Outspread.Core/QrTerminal.cs`).

Core references no packages; it is a plain `net10.0` project.

## Notable transitive components (resolved graph)

From `src/Outspread.Board/obj/project.assets.json`. This is not a full closure;
the full graph is what a restore produces.

- `Microsoft.WindowsAppSDK.WinUI` 2.3.9, `Microsoft.WindowsAppSDK.Runtime`
  2.5.1, `Microsoft.WindowsAppSDK.Foundation` 2.3.12,
  `Microsoft.WindowsAppSDK.Search` 2.5.5, `Microsoft.WindowsAppSDK.Base` 2.0.4,
  `Microsoft.WindowsAppSDK.DWrite` 2.1.0, `Microsoft.WindowsAppSDK.AI` 2.5.5,
  `Microsoft.WindowsAppSDK.ML` 2.1.94,
  `Microsoft.WindowsAppSDK.InteractiveExperiences` 2.1.9,
  `Microsoft.WindowsAppSDK.Widgets` 2.0.5, `Microsoft.Windows.AI.MachineLearning`
  2.1.74, `Microsoft.Windows.SDK.BuildTools.MSIX` 1.7.251221100 — verified: the
  WinUI package read here ships the same Microsoft Software License Terms
  (`license.txt`) as the metapackage; treat the whole family as that licence.
- `Microsoft.Web.WebView2` 1.0.3719.77 — verified: `LICENSE.txt` in the package
  is a BSD-3-clause-style Microsoft licence (redistribution permitted, keep the
  notice).
- `System.Numerics.Tensors` 9.0.0 — verified: nuspec declares MIT and the
  package's `LICENSE.TXT` is MIT (.NET Foundation).

## Windows App SDK / WinUI runtime redistribution

The app links the Windows App SDK and WinUI and is built **framework-dependent
and unpackaged** (`WindowsAppSDKSelfContained=false`,
`WindowsPackageType=None`, see the Board csproj): it requires the Windows App
Runtime to be present on the machine (2.5.1 at the time this was written) and
renders through WinUI, which pulls in Microsoft runtime components including
the WebView2 SDK. That means distributing this app distributes Microsoft
runtime components under Microsoft's own licence terms. The Microsoft Software
License Terms and the WebView2 `NOTICE.txt` / `LICENSE.txt` that ship inside
the restored packages are the governing texts; any redistribution must keep
Microsoft's own notices intact and must be checked against those terms rather
than against this file's summary. This is stated plainly and without clause
numbers on purpose: read the shipped licence files.

## Bundled files in this repository

- `mip.html` — **not the author's own prototype.** It is a saved copy of the
  Microsoft Learn page "Motion in practice - animation in Windows apps", with a
  canonical URL of `https://learn.microsoft.com/en-us/windows/apps/develop/motion/motion-in-practice`
  recorded in the file itself. Licence: verify before redistribution (Microsoft
  Learn content terms; documentation text, not code).
- `design/apple.json` — despite the extension, a saved HTML page from an Apple
  developer tutorial site (it declares `<base href="/tutorials/">` and a
  JavaScript-required shell). No licence statement inside the file. Licence:
  verify before redistribution.
- `design/RichTextLabel.tsx` — imports from `@tldraw/editor` and matches the
  tldraw source layout referenced in `docs/maturity-plan.md`; treat it as
  derivative of tldraw source. Licence: verify before redistribution (the tldraw
  licence at the relevant revision is not recorded in this repository).
- `design/feel.html`, `design/logo-variants.html`, `design/logo/make-ico.mjs` —
  the author's own prototypes and tooling (Chinese-language working notes in
  the files themselves; `feel.html` cites tldraw's published dot colour only as
  a numeric value, it carries no tldraw code).
- `src/Outspread.Board/Assets/outspread.ico` and `outspread-256.png` — the
  author's own logo, generated by `make-ico.mjs` from `logo-variants.html`
  (documented in the Board csproj comments).
- `boards/scratch.canvas` and `boards/assets/` — the author's sample board data.

One line for readers of the Chinese README: 用法与说明请看 `README.md`。
