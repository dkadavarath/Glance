# Glance

![Glance Logo](docs/img/logo_paramarca.png)

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Framework: WinUI 3](https://img.shields.io/badge/Framework-WinUI_3-purple.svg)](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
[![Platform: Windows 11](https://img.shields.io/badge/Platform-Windows_11-blue.svg)](https://www.microsoft.com/windows/)
[![Language: C#](https://img.shields.io/badge/Language-C%23-green.svg)](https://learn.microsoft.com/en-us/dotnet/csharp/)

<a href="https://apps.microsoft.com/detail/GLANCE_STORE_ID" target="_blank">
  <img src="https://developer.microsoft.com/en-us/store/badges/images/English_get-it-from-MS.png" alt="Get it from Microsoft Store" height="40" />
</a>

Glance is a fast, lightweight, and elegant PDF document viewer designed specifically for Windows 11. Built from the ground up following Fluent Design principles, it offers a visually integrated experience featuring Mica transparency and smooth transitions. It combines the speed and minimalist design of classic document viewers like GNOME Evince with modern annotation tools inspired by Adobe Acrobat.

<!-- ![Glance Demo](docs/img/Glance.gif) -->

---

## Key Features

* **Premium Aesthetics and Fluent Design:**
  * Native use of Windows 11 Mica backdrop that adapts dynamically to the user's desktop wallpaper.
  * Integrated window title bar and translucent sidebar for an immersive reading experience.
  * Full support for automatic light and dark system themes.
* **Visual Welcome Screen (Evince-style):**
  * Grid layout displaying recent documents using cover page thumbnails rendered from the first page of each PDF.
  * Single-click quick access to recently opened files with automatic registry cleanup if files are moved or deleted.
* **Side-by-Side Comparison (Split View):**
  * Compare two PDF documents side-by-side dynamically with synchronous vertical layout grids.
  * Clear comparison toggle that safely restores localized viewer states on close.
* **Freehand Drawing Tools:**
  * Smooth digital freehand ink drawing with a rounded pen pointer (PenMode), ideal for digital signatures, sketches, or writing handwritten notes directly on the page.
* **Precision Highlighter Tool:**
  * Accurate text selection highlight mapping.
  * Dynamic 7-color palette (Yellow, Green, Cyan, Magenta, Red, Blue, Black) that appears exclusively in editing modes.
  * Automatic Alpha channel calculation (31% opacity) to ensure the translucent color highlights the text without obscuring the original content.
* **Sticky Notes:**
  * Drop floating comment bubbles anywhere on the PDF with a clean popover overlay to write, view, edit, and store reader remarks.
* **System Language Localization:**
  * Automatic interface localization detection (Spanish and English supported natively).
* **Interactive Close and Save Flow:**
  * Prompts users with a localized 3-button confirmation ("Save and Exit", "Exit without saving", "Cancel") if unsaved changes exist and auto-save is off, preventing data loss.
* **Auto-saving Annotations:**
  * All notes, highlights, and freehand drawing strokes are saved automatically to a local JSON database upon pointer release, ensuring immediate persistence.
* **Real-time Document Rotation:**
  * Native rotation controls to rotate the document in 90-degree increments, dynamically updating dimensions to prevent page clipping.
* **Keyboard Shortcuts (Undo):**
  * Full edit history with support for undoing annotations using the universal Ctrl + Z shortcut.
* **Fluent Sidebar Index:**
  * Navigation via high-definition page thumbnails rendered sequentially to prevent visual layout scrambling during UI virtualization recycling.

---

## Architecture

Glance uses a **hybrid C# + Rust architecture** for optimal performance:

* **Frontend:** WinUI 3 (C#/.NET 10.0) - Native Windows 11 aesthetics with Mica backdrop
* **Backend:** Rust - Native performance for PDF rendering and storage
* **Bridge:** P/Invoke FFI - Type-safe C# ↔ Rust interop

---

## System Requirements

* **OS:** Windows 10 or later
* **Platform:** Windows App SDK 1.5+ (WinUI 3)
* **Runtime:** .NET 10.0

---

<details>
<summary><strong>📚 Development & Building (For Contributors)</strong></summary>

### Prerequisites

* **.NET 10.0 SDK** - [dotnet.microsoft.com](https://dotnet.microsoft.com)
* **Rust 1.70+** - [rustup.rs](https://rustup.rs)
* **Visual Studio 2022** (optional)

### Build Instructions

1. Clone repository:
   ```bash
   git clone https://github.com/jonas1ara/Glance.git
   cd Glance
   ```

2. Build Rust backend:
   ```bash
   cd native/glance-native
   cargo build --release
   ```
   *(Note: Building in release mode is required as the C# project is configured to automatically look for the Rust DLL in `target/release/`).*

3. Build and Run C# frontend:
   ```bash
   cd ../../src
   dotnet run --project Glance.csproj
   ```
   *(Note: The MSBuild system automatically copies the compiled `glance_native.dll` and `pdfium.dll` to the output directory during build, so no manual file copying is needed).*

### Architecture Phases

- **Phase 1: FFI Foundation (P/Invoke bridge)** ✅
  * Established C-compatible unmanaged boundaries.
  * Registered dynamic DLL resolvers (`NativeLibrary.SetDllImportResolver`) to locate Glance backend binaries.
- **Phase 2: Persistence (JSON file I/O)** ✅
  * Engineered Rust-side serialization adaptors using Serde.
  * Formulated local drawing database formats to store vectors, comments, and marks.
- **Phase 3: Annotation Processing (validation, geometry)** ✅
  * Formulated validation rules for comments, highlights, and colors.
  * Created safety checks for drawing coordinates.
- **Phase 4: PDF Rendering (lazy loading, async)** ✅
  * Integrated Google PDFium via dynamic library binding.
  * Designed async rendering buffers on C# thread pools using `CancellationToken` loops to prevent thread collisions.
- **Phase 5: Localization & UX Polish** ✅
  * Integrated operating system locale detector.
  * Formulated localized interactive exit confirmation dialog flows (Save, Discard, Cancel).

### Build Variants

- **Debug:** `cargo build --debug && dotnet build`
- **Release:** `cargo build --release && dotnet build -c Release`
- **Clean:** `cargo clean && dotnet clean`

</details>

---

## License

This project is licensed under the MIT License. See the LICENSE file for details.
