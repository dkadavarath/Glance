# PDFium DLL Maintenance & Architecture Guide

This document explains the architecture of the native PDFium dependency in Glance, the DLL search paths, and the maintenance routines for updating the pre-compiled `pdfium.dll` binary.

---

## 1. Directory Layout & Architecture

Glance utilizes a dual-engine architecture where `pdfium.dll` serves as the underlying C++ rendering engine. You will find `pdfium.dll` in two locations within the repository, each serving a distinct purpose:

### A. Frontend Runtime Directory (`src/pdfium.dll`)
* **Purpose:** This copy is package-bound and ships to production.
* **How it works:** When building the project, MSBuild references the file in `src/` as a **Content** item in `Glance.csproj`:
  ```xml
  <Content Include="pdfium.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
  ```
* **DLL Search Path:** When `Glance.exe` starts, Windows loads our Rust library `glance_native.dll`. In turn, the Rust library dynamically loads `pdfium.dll`. Windows searches for native dependencies in the application's active executing directory. Copying `pdfium.dll` to the output folder ensures it is found at runtime.

### B. Rust Developer Directory (`native/glance-native/pdfium.dll`)
* **Purpose:** This copy is used exclusively for localized development testing.
* **How it works:** When you execute Rust unit tests via `cargo test` from the `native/glance-native/` directory, the Rust compiler does not run within the C# environment. It needs `pdfium.dll` local to the Rust execution workspace to run PDF rendering validations.

---

## 2. Update Frequency & Guidelines

Unlike package-managed C# libraries, native C++ DLLs do not require constant updates. Follow these criteria to determine when to upgrade `pdfium.dll`:

| Trigger | Frequency | Rationale |
| :--- | :--- | :--- |
| **Security Patches (CVEs)** | Every 3 to 6 months | PDF parsers are common targets for buffer overflow exploits. Google regularly patches security vulnerabilities in Chromium/PDFium. |
| **Rendering Errors** | Ad-hoc (upon user report) | If a user reports rendering anomalies with complex fonts, 3D diagrams, or vector graphics, a newer PDFium build may resolve it. |
| **Backend Rust Updates** | Upon upgrading `pdfium-render` | If the Rust backend bindings (`pdfium-render` crate) are upgraded, they may depend on API endpoints exposed only in newer PDFium builds. |

---

## 3. Step-by-Step Update Guide

When you need to update `pdfium.dll`, follow these steps to prevent compatibility regressions:

### Step 1: Download the Binaries
1. Go to the [bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries/releases) release section.
2. Download the latest stable package for your development platform (typically `pdfium-windows-x64.tgz`).
3. Extract the archive to access `bin/pdfium.dll`.

### Step 2: Replace the DLLs in the Repository
Overwrite the existing files in both target locations with the new version:
* Copy the new `pdfium.dll` into `src/`
* Copy the new `pdfium.dll` into `native/glance-native/`

### Step 3: Run Rust Backend Tests
Open a terminal in the Rust workspace and run the test suite to verify the new DLL has no API regressions:
```bash
cd native/glance-native
cargo test
```

### Step 4: Run C# Integration Tests
1. Compile and launch Glance in debug mode:
   ```bash
   cd src
   dotnet run --project Glance.csproj
   ```
2. Manually test basic PDF rendering operations:
   * Open multiple PDF documents.
   * Verify zoom, page navigation, and text search coordinates.
   * Draw highlights, ink notes, and edit sticky comments to ensure flat annotations compile cleanly.

### Step 5: Commit and Deploy
Once validations pass, commit the updated binaries and submit the new MSIX package to the Microsoft Store:
```bash
git add src/pdfium.dll native/glance-native/pdfium.dll
git commit -m "chore: update pdfium.dll to version [NEW_VERSION] for security and performance"
git push origin master
```

---

## 4. Useful Resources
* **Official Source Repository:** [pdfium.googlesource.com/pdfium](https://pdfium.googlesource.com/pdfium/)
* **Pre-compiled Releases:** [github.com/bblanchon/pdfium-binaries](https://github.com/bblanchon/pdfium-binaries)
* **Google Group Support:** [groups.google.com/g/pdfium](https://groups.google.com/g/pdfium)
