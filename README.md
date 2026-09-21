# Glance

![Glance Logo](docs/img/logo_paramarca.png)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Framework: WinUI 3](https://img.shields.io/badge/Framework-WinUI_3-purple.svg)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
[![Platform: Windows 11](https://img.shields.io/badge/Platform-Windows_11-blue.svg)](https://www.microsoft.com/windows/)
[![Language: C#](https://img.shields.io/badge/Language-C%23-green.svg)](https://learn.microsoft.com/en-us/dotnet/csharp/)

A fast, lightweight PDF viewer for Windows 11, built on WinUI 3 with a Rust rendering
backend. It aims for the speed of a classic document viewer with first-class support for
modern input — precision touchpads, pen and multi-touch — and annotation tools that write
back into the file.

> **This is a fork of [jonas1ara/Glance](https://github.com/jonas1ara/Glance).** All work
> before September 2026 is the original author's. This fork diverges on distribution
> (unpackaged MSI rather than a Store package), input handling and viewer features; it is
> not affiliated with the upstream project and is not the build published on the
> Microsoft Store.

---

## Install

Download the MSI from the [latest release](https://github.com/dkadavarath/Glance/releases/latest)
and run it. It installs to `Program Files` and adds a Start Menu shortcut.

* **x64 only.** ARM64 builds from the same source (see below) but is not published.
* **The installer is unsigned**, so SmartScreen will warn on first run — *More info* →
  *Run anyway*.
* Installing a newer version replaces the old one; no need to uninstall first.

Glance registers itself as a PDF handler, so it appears under *Open with* and in
**Settings → Default apps**. It does not take over `.pdf` unless you tell Windows to.

---

## Features

**Reading**

* Continuous, single-page and two-page layouts, remembered between sessions.
* Full screen on <kbd>F11</kbd>, <kbd>Esc</kbd> to leave.
* Zoom anchored on the cursor or the page centre, whichever you prefer — the setting is
  in the Settings flyout. Pages stay centred whenever they fit the window.
* Side-by-side comparison of two documents.
* Sidebar index of page thumbnails, and a recent-documents welcome screen with covers
  rendered from each file's first page.
* Text search, 90° rotation, and light/dark themes that follow the system.

**Input**

* Pinch to zoom, two-finger pan, and a sideways swipe to turn pages in single-page mode.
* <kbd>Alt</kbd> + wheel scrolls horizontally; hold <kbd>Space</kbd> for a hand tool that
  drags the page and glides to a stop.
* Pen draws and rejects palm contacts; a second finger pans rather than corrupting a
  stroke in progress. Pointer state is tracked per contact, so multi-touch does not
  confuse the drawing tools.

**Annotating**

* Freehand ink, highlights with a 7-colour palette and adjustable opacity, sticky notes
  and an eraser.
* Annotations persist immediately to a local JSON store as you draw, and are written into
  the PDF itself when you save.
* <kbd>Ctrl</kbd>+<kbd>Z</kbd> undo, and a save prompt on exit when there are unsaved
  changes and auto-save is off.

**Elsewhere**

* Opens files from *Open with*, the command line, and drag-and-drop onto the window.
* Mica backdrop and an integrated title bar.
* Interface follows the system language; English and Spanish are supported.

### Known limitations

* Ink and highlights are painted into the page content when saved, so other readers see
  them but cannot select or edit them. Only sticky notes become real PDF annotations.
* No stamps or signature images yet, no printing, and links are not clickable. Text and
  images cannot be selected or copied. These are in progress.

---

## Architecture

A hybrid C# and Rust application:

* **Frontend:** WinUI 3 on .NET 10, targeting `net10.0-windows10.0.26100.0`.
* **Backend:** a Rust `cdylib` wrapping PDFium for rendering, annotation validation and
  persistence.
* **Bridge:** P/Invoke, in `src/Interop/GlanceNative.cs`.
* **Saving:** PDFsharp, in `src/Services/PdfExportService.cs`, writes annotations back
  into the document.

### Libraries

**Frontend (.NET / C#)**

* [Windows App SDK (WinUI 3)](https://github.com/microsoft/microsoft-ui-xaml) — the UI framework.
* [PDFsharp](https://github.com/empira/PDFsharp) — burns annotations into the original PDF.

**Backend and FFI (Rust)**

* [windows-rs](https://github.com/microsoft/windows-rs) — Windows API projections for Rust.
* [pdfium-render](https://github.com/ajrcarey/pdfium-render) — safe bindings around PDFium.
* [pdfium-binaries](https://github.com/bblanchon/pdfium-binaries) — prebuilt PDFium, pinned
  to 151.0.7920 for both architectures.

**Packaging**

* [WiX Toolset](https://wixtoolset.org/) v5 — builds the MSI in `installer/Glance.wxs`.

---

## System requirements

* **OS:** Windows 11. Built against the 26100 SDK; older builds are untested.
* **Architecture:** x64 for released binaries.
* Nothing else to install — the MSI is self-contained and carries its own .NET runtime.

---

<details>
<summary><strong>📚 Development and building</strong></summary>

### Prerequisites

* **.NET 10 SDK** — [dotnet.microsoft.com](https://dotnet.microsoft.com)
* **Rust 1.85+** — [rustup.rs](https://rustup.rs). The crate is edition 2024.
* **Visual Studio 2022 Build Tools** with the Desktop C++ workload and the Windows 11
  SDK — the MSVC linker is required even though the app is managed.
* **Developer Mode** enabled.

### Build

Rust first. The C# build fails without the native library.

```bash
git clone https://github.com/dkadavarath/Glance.git
cd Glance

rustup target add x86_64-pc-windows-msvc

cd native/glance-native
cargo build --release --target x86_64-pc-windows-msvc
cd ../..

dotnet run --project src/Glance.csproj
```

**Always pass `--target` explicitly**, including for your host architecture. Without it
cargo writes to `target/release/` while the C# build looks in `target/<triple>/release/`.
Release mode is required even for Debug C# builds, for the same reason.

MSBuild copies the architecture-matched `glance_native.dll` and `pdfium.dll` into the
output directory, so nothing needs copying by hand.

### Packaging

```powershell
dotnet publish src\Glance.csproj -c Release -r win-x64 --self-contained true `
  -p:Platform=x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false

$pub = (Resolve-Path "src\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish").Path
wix build installer\Glance.wxs -arch x64 -d Version=1.0.0.0 -d "PublishDir=$pub" `
  -ext WixToolset.UI.wixext -o "dist\Glance-1.0.0-x64.msi"
```

Trimming and ReadyToRun are disabled deliberately: the csproj turns both on for non-Debug
publishes, and trimming breaks WinUI 3's XAML reflection. WiX is pinned to v5 because v7
will not run without accepting the Open Source Maintenance Fee EULA.

Pushing a `v*` tag builds the MSI in CI and attaches it to a GitHub release.

### ARM64

Both architectures build from this one codebase — managed code is architecture-neutral
and only the two native DLLs vary, resolved per-RID by `src/Glance.csproj`.

```bash
cargo build --release --target aarch64-pc-windows-msvc
dotnet publish src/Glance.csproj -c Release -r win-arm64
```

Cross-compiling from x64 needs the `Microsoft.VisualStudio.Component.VC.Tools.ARM64`
component in the Visual Studio installer. CI builds x64 only, so ARM64 is untested.

</details>

---

## License

MIT, inherited from the upstream project. See [LICENSE](LICENSE).
