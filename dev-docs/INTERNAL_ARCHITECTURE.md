# Internal Architecture

Glance is built using a hybrid architecture that combines a high-performance native backend in **Rust** with a modern Fluent design frontend in **C# (WinUI 3)**. This document details the role of each file, key functions, memory management strategies, and platform integration.

---

## 1. Project Directory Structure

```
Glance/
├── dev-docs/                         # Technical developer documentation
│   ├── INTERNAL_ARCHITECTURE.md      # Detailed internal architecture description (This file)
│   ├── DLL_LOADING_FIX.md            # DLL resolution and deployment mechanics
│   ├── ARCHITECTURE_HYBRID_PLAN.md   # Hybrid C# ↔ Rust integration plan
│   └── PACKAGING_AND_SIGNING_GUIDE.md # Packaging and store publication guide
├── docs/                             # Public landing page website (GitHub Pages)
├── native/                           # Native Rust backend
│   └── glance-native/
│       ├── src/
│       │   ├── lib.rs                # Crate entry point
│       │   ├── ffi/
│       │   │   ├── bindings.rs       # C-compatible FFI bindings & memory management
│       │   │   └── marshalling.rs    # String convert helpers (Rust ↔ C-string)
│       │   ├── rendering/
│       │   │   └── pdf_engine.rs     # Rust PDFium bindings and search engine
│       │   └── utils/
│       │       └── error.rs          # Memory-safe FFI error creation
│       └── Cargo.toml
└── src/                              # WinUI 3 C# Frontend
    ├── Interop/
    │   ├── GlanceNative.cs           # P/Invoke signature declarations
    │   └── AsyncBridge.cs            # C# Task threadpool async bridge
    ├── Services/
    │   ├── LocalizationService.cs    # Multi-language dictionary & detector (ES/EN)
    │   ├── PdfRenderService.cs       # Client wrapper for native Rust engine
    │   └── PdfExportService.cs       # PDFsharp compiler for flat annotation output
    ├── MainPage.xaml.cs              # Core UI coordinator
    └── MainWindow.xaml.cs            # Top-level window lifecycle & closing flow
```

---

## 2. Key Components & Functions

### C# Frontend (WinUI 3 / .NET 10)

#### A. Window Lifecycle & Closing Flow ([MainWindow.xaml.cs](file:///C:/Users/adria/GitHub/Glance/src/MainWindow.xaml.cs))
*   **`AppWindow_Closing`**: Triggered when the user attempts to close the window.
    *   It checks if `MainPage.HasUnsavedChanges` is true.
    *   If auto-save is enabled in preferences (`AutoSaveOnExit` is true), it calls `mainPage.SaveOriginalPdfSilentAsync()` inside a background ThreadPool thread using `Task.Run` (to avoid UI thread suspension deadlocks) and terminates the process with `System.Environment.Exit(0)`.
    *   If auto-save is disabled, it shows a 3-button dialog (`ContentDialog`) offering **"Save and Exit"** (which runs the save on a background thread and exits), **"Exit without saving"** (exits immediately), or **"Cancel"** (stays open). This dialog is fully localized.
    *   If there are no unsaved changes, it shuts down the process immediately with `System.Environment.Exit(0)`.

#### B. UI Coordinator & Background Control ([MainPage.xaml.cs](file:///C:/Users/adria/GitHub/Glance/src/MainPage.xaml.cs))
*   **`LoadPdfAsync(file)`**: Sets up the visual structure.
    1.  Cancels any active background rendering tasks using `_backgroundRenderCts.Cancel()`.
    2.  Creates a new instance of `PdfRenderService` and initializes the Rust engine.
    3.  Uses UWP's native `Windows.Data.Pdf.PdfDocument` via a memory stream to extract page dimensions quickly (lazy loading placeholder UI).
    4.  Starts background rendering for remaining pages sequentially.
*   **`RenderRemainingPagesAsync`**: Runs on a background thread. Iterates through the document pages to render them using Rust. Uses a `CancellationToken` to stop execution immediately if the document is closed or saved.
*   **`SaveOriginalPdfSilentAsync(bool onExit = false)`**: Saves current annotations.
    1.  Cancels background rendering to prevent concurrent FFI calls.
    2.  Disposes of `_pdfRenderService` to unlock the PDF file handle.
    3.  Invokes `PdfExportService` to burn drawings/notes into the PDF.
    4.  Overwrites the original file.
    5.  Resets rotation angles, and saves the updated unrotated annotations to the JSON database via `SaveAnnotationsAsync` (preserving annotations in memory and sidecar files so they remain clickable/editable).
    6.  If `onExit` is true, skips reloading the PDF structure in memory to prevent shutdown deadlocks.
    7.  If `onExit` is false, reloads the document structures and Rust rendering engine, re-renders the current page, and restarts background rendering.

#### C. Render Wrapper ([PdfRenderService.cs](file:///C:/Users/adria/GitHub/Glance/src/Services/PdfRenderService.cs))
*   **`InitializeAsync`**: Calls `GlanceNative.pdf_engine_create`.
*   **`RenderPageAsync`**: Packages rendering parameters into a `RenderOptions` struct and makes the P/Invoke call to `pdf_render_page`. Copies unmanaged PNG bytes into a managed C# `byte[]` array and immediately frees the unmanaged pointer via `GlanceNative.memory_free(ptr, len)`.
*   **`SearchTextAsync`**: Performs text searches by calling `pdf_search_text` and marshals the JSON string containing search match coordinates.

#### D. Localized Dictionary ([LocalizationService.cs](file:///C:/Users/adria/GitHub/Glance/src/Services/LocalizationService.cs))
*   Detects the system locale.
*   Serves localized string keys for UI controls, tooltips, search placeholders, and exit dialogues dynamically.

---

### Rust Backend (`glance_native.dll`)

#### A. PDF Engine Core ([pdf_engine.rs](file:///C:/Users/adria/GitHub/Glance/native/glance-native/src/rendering/pdf_engine.rs))
*   Wraps the `pdfium-render` crate.
*   **`PdfEngineImpl::new`**: Initializes the PDFium library and opens the file. Transmutes the document's lifetime to `'static` to store it alongside the `Pdfium` context.
*   **`render_page_to_png`**: Renders a page to a raw bitmap at specific dimensions and encodes it to PNG format.
*   **`search_text`**: Traverses the page text content, locates coordinate boundaries, and formats search matches as JSON.

#### B. FFI Bindings & Memory Management ([bindings.rs](file:///C:/Users/adria/GitHub/Glance/native/glance-native/src/ffi/bindings.rs))
*   Exports `extern "C"` endpoints using `#[no_mangle]`.
*   Defines the `Response` layout structure:
    ```rust
    #[repr(C)]
    pub struct Response {
        pub success: bool,
        pub error_msg: *const u8,
        pub data_ptr: *mut u8,
        pub data_len: u64,
    }
    ```

---

## 3. Unmanaged Memory Management (Rust ↔ C#)

FFI boundaries require strict ownership policies. Since C# is garbage-collected and Rust uses static lifetimes, allocating and deallocating memory must be handled explicitly.

### The Memory Safety Trap
Originally, `memory_free` attempted to deallocate any pointer by reconstructing a `Vec` with an arbitrary capacity:
```rust
// BUGGY ORIGINAL CODE (Caused Heap Corruption & Crashes)
drop(Vec::from_raw_parts(ptr, 0, 1024));
```
Because the allocated buffer size (e.g., a 300KB PNG image or a 50-byte error string) did not match the forced capacity of `1024`, the system allocator corrupted the heap, causing access violations (`0xc0000005`).

### The Safe Solution
We divided unmanaged memory allocations into two categories:
1.  **C-Style Strings (Null-Terminated):** Error messages and JSON strings.
2.  **Slices (Arbitrary Byte Buffers):** PNG image data.

```rust
// CORRECTED RUST IMPLEMENTATION
#[no_mangle]
pub extern "C" fn memory_free(ptr: *mut u8, len: u64) {
    if !ptr.is_null() {
        unsafe {
            if len == 0 {
                // 1. Reconstruct CString from raw pointer and let it drop safely
                let _ = std::ffi::CString::from_raw(ptr as *mut std::os::raw::c_char);
            } else {
                // 2. Reconstruct Boxed Slice (capacity matches len) and drop it
                let slice = std::slice::from_raw_parts_mut(ptr, len as usize);
                let _ = Box::from_raw(slice);
            }
        }
    }
}
```

On the C# side, `GlanceNative.cs` imports this with an optional length:
```csharp
[DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
public static extern void memory_free(IntPtr ptr, ulong len = 0);
```

*   When C# frees a string (error message or JSON): `GlanceNative.memory_free(ptr);` (defaults to length `0`, freeing as CString).
*   When C# frees a PNG buffer: `GlanceNative.memory_free(ptr, dataLen);` (frees as a boxed slice).

---

## 4. Platform API Integration

Glance implements a **dual-engine** PDF strategy:

1.  **Metadata and Layout (Windows UWP Native):**
    *   Glance loads the document structure using UWP's `Windows.Data.Pdf.PdfDocument` via `InMemoryRandomAccessStream`.
    *   This provides extremely fast access to page counts, page sizes, and metadata.
    *   C# handles the visual rendering pipeline in the main UI thread.
2.  **High-Speed Page Rendering & Text Search (Rust PDFium Native):**
    *   When a page needs to be rendered at high resolution, or when doing text searches across the document, the task is offloaded to the Rust FFI library.
    *   `PdfRenderService` calls the unmanaged Rust DLL on a background thread pool via `AsyncBridge`.
    *   This prevents blocking the UI thread and ensures a smooth 60fps experience.

---

To prevent background threads from accessing the native Rust engine while it is being disposed of during file save operations, and to eliminate cross-thread deadlocks:

*   **Pre-emptive Cancellation:** Before acquiring the `_ffiSemaphore` lock in major UI operations (`SaveOriginalPdfSilentAsync`, `LoadPdfAsync`, `HomeButton_Click`, `CleanupForExitAsync`), the background rendering `CancellationTokenSource` is cancelled immediately:
    ```csharp
    _backgroundRenderCts?.Cancel();
    ```
    This triggers background tasks to throw `OperationCanceledException` and release the semaphore immediately, avoiding lock contention.
*   **Non-Blocking UI Thread Cancellation:** In `CancelBackgroundRenderAsync()`, we cancel the CTS but do **not** await the background task (`_backgroundRenderTask`) on the UI thread. This prevents deadlocks where the background task is waiting to marshal to the UI thread (via `DispatcherQueue`) while the UI thread is blocked awaiting the task's completion.
*   **Thread-Safe Engine Disposal:** Disposing of `PdfRenderService` is synchronized with active rendering operations. The `Dispose()` method calls `_ffiLock.Wait()` before executing `pdf_engine_destroy`. This prevents race conditions where the unmanaged Rust PDF engine handle is destroyed while a ThreadPool thread is executing `pdf_render_page`.
*   **Background Thread Execution on Close:** Since WinUI 3 suspends UI dispatcher continuations during the window closing sequence, executing asynchronous operations (like auto-saving annotations) directly on the UI thread causes the method to hang. To solve this, `SaveOriginalPdfSilentAsync(true)` is executed on a ThreadPool background thread using `Task.Run()`, followed by a call to `System.Environment.Exit(0)` which cleanly and forcefully terminates the process and releases all remaining DLL locks.

---

## 6. Coordinate Systems & DPI Scale Translation

There is a fundamental difference in pixel density between the Windows UI layout space and the physical PDF coordinate system:
*   **WinUI 3 Layout Space (DIPs):** Runs at **96 DPI**. All UI gestures, drawing canvases, and rectangles are measured in these DPI-independent pixels.
*   **PDF Point Space:** Runs at **72 DPI** (PDF standard points). PDFium text search and PDFsharp page generation operate in this space.
*   **CropBox Boundary:** The origin `(0, 0)` in PDF pages starts at the bottom-left of the physical paper (`MediaBox`). Any `/CropBox` offset shifts the visible area.

To achieve exact alignment:
1.  **Search Highlights (Rust PDF -> C# UI):**
    *   Rust uses PDFium to search text. It gets coordinates relative to the unrotated page `MediaBox`.
    *   Rust subtracts the `CropBox` bottom-left offset to make it relative to the visible area.
    *   C# receives these coordinates and multiplies them by `1.3333` (`96.0 / 72.0`) to scale them from 72 DPI to the 96 DPI UI grid.
2.  **Exporting Annotations (C# UI -> Rust PDF):**
    *   C# captures drawing pointer strokes and highlight boundaries in 96 DPI DIPs.
    *   C# multiplies these coordinates and pen thicknesses by `0.75` (`72.0 / 96.0`) to convert them to 72 DPI PDF points.
    *   `PdfExportService` maps these points according to the page's native rotation and applies the `CropBox` offset before drawing via PDFsharp.

---

## 7. Interactive Notes Overlay and Background Flow

To allow readers to click and edit sticky notes while scrolling:
*   The annotation overlay `ItemsControl` binds its `Background` to `AnnotationsBackground` dynamically.
*   In **Navigate (Read) mode**, the background is set to `null` (`x:Null`). This stops the empty overlay from intercepting pointer events, allowing scrolling/panning to pass through to the `ScrollViewer` while keeping the `Button` `💬` hit-testable and clickable.
*   In **Editing modes**, the background is set to `Transparent` to capture gestures anywhere for drawing highlights or pen strokes.
*   Annotations are kept in the C# view model and synchronized to the JSON database on file save, ensuring they remain interactive, editable, and clickable when the application is reopened.

