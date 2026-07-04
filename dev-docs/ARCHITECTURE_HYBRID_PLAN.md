# Hybrid C# ↔ Rust Integration Model

Glance implements a **hybrid architecture** that balances C# development speed with Rust native speed. This document describes the integration plan, architectural boundaries, and unmanaged data flow (marshalling) between the two layers.

---

## 1. Boundary of Responsibilities

To maintain clean separation, each language focuses on what it does best:

```mermaid
graph TD
    subgraph "C# Frontend (WinUI 3)"
        A[User Input & Gesture Canvas] --> B[Visual Layout & Mica Backdrop]
        B --> C[Page Navigation UI]
        C --> D[Annotation Render Layer]
        D --> E[PdfExportService - PDFsharp compiler]
    end
    subgraph "P/Invoke FFI Bridge"
        F[GlanceNative Wrapper]
    end
    subgraph "Rust Backend (Native DLL)"
        G[PdfEngineImpl - PDFium Wrapper] --> H[High-Res Page Rendering to PNG]
        G --> I[Coordinate-Mapped Text Search]
        G --> J[JSON Annotation Validator]
    end
    C# --> F
    F --> Rust
```

### C# Layer (Frontend & Aesthetics)
*   **User Interface:** Window shell, panels, toolbars, dialogs, sliders, and navigation lists.
*   **Input Processing:** Canvas gestures, drawing strokes tracking, ink rendering, and drag-and-drop.
*   **Application Settings:** System theme styling, and local persistent configurations.
*   **Orchestration:** Deciding when to render pages, save files, or perform searches.

### Rust Layer (Performance & Engine)
*   **PDF Parsing:** Offloaded to Google's PDFium library.
*   **Rasterization:** Generating high-resolution PNG image buffers from PDF pages.
*   **Text Processing:** Scanning, extracting, and matching text queries with coordinates.
*   **Validation:** Parsing, indexing, and validating annotation schemas.

---

## 2. P/Invoke Bridge Implementation

Inter-process communication uses standard C-style FFI bindings via .NET Platform Invoke (P/Invoke).

### Shared Memory Structs
Both languages must share identical memory structures. We specify `#[repr(C)]` in Rust and `[StructLayout(LayoutKind.Sequential)]` in C#.

#### Render Options Layout
*   **Rust Struct:**
    ```rust
    #[repr(C)]
    pub struct RenderOptions {
        pub page_index: u32,
        pub width: u32,
        pub height: u32,
        pub dpi: f32,
    }
    ```
*   **C# Struct:**
    ```csharp
    [StructLayout(LayoutKind.Sequential)]
    public struct RenderOptions
    {
        public uint PageIndex;
        public uint Width;
        public uint Height;
        public float Dpi;
    }
    ```

#### Native Response Wrapper
All FFI operations return a unified response to handle success status and errors safely:
*   **Rust Struct:**
    ```rust
    #[repr(C)]
    pub struct Response {
        pub success: bool,
        pub error_msg: *const u8,
        pub data_ptr: *mut u8,
        pub data_len: u64,
    }
    ```
*   **C# Struct:**
    ```csharp
    [StructLayout(LayoutKind.Sequential)]
    public struct Response
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool Success;
        public IntPtr ErrorMsg;
        public IntPtr DataPtr;
        public ulong DataLen;
    }
    ```

---

## 3. Data Flow & Marshalling Life Cycle

Here is the exact lifecycle of an FFI call (e.g., rendering a page):

```mermaid
sequenceDiagram
    participant C# as C# (PdfRenderService)
    participant FFI as FFI Bridge (GlanceNative)
    participant Rust as Rust (bindings.rs)
    participant PDFium as PDFium Library

    C#->>FFI: pdf_render_page(engineHandle, ref opts)
    FFI->>Rust: pdf_render_page(engine, opts)
    Rust->>PDFium: Render page to Bitmap
    PDFium-->>Rust: Bitmap Data
    Rust->>Rust: Encode Bitmap to PNG bytes
    Rust->>Rust: Convert Vec to Boxed Slice & forget it (prevents deallocation)
    Rust-->>FFI: Returns Response (Success, ptr to PNG, data_len)
    FFI-->>C#: Return Response struct
    C#->>C#: Marshal.Copy(ptr, managedArray, 0, len)
    C#->>FFI: memory_free(ptr, len)
    FFI->>Rust: memory_free(ptr, len)
    Rust->>Rust: Reconstruct Boxed Slice and drop it (frees unmanaged memory)
```

---

## 4. Memory Ownership Policies

To prevent memory leaks and crashes, Glance follows three rules:

1.  **Rust Allocates, Rust Frees:** C# must never attempt to free pointers allocated by Rust using C#’s `Marshal.FreeHGlobal` or `coTaskMemFree`. All unmanaged pointers must be returned to Rust via `memory_free`.
2.  **Explicit Lifetime Forgetting:** When Rust returns an allocated buffer (PNG bytes or a JSON string) to C#, it must invoke `std::mem::forget` (or `Box::into_raw`/`CString::into_raw`) to tell the compiler not to drop it when the FFI function exits.
3.  **Correct Deallocation Strategy:**
    *   C-style strings (`len == 0`) must be freed via `CString::from_raw`.
    *   Arbitrary byte buffers (`len > 0`) must be freed via `Box::from_raw` using the exact `len` as capacity to prevent allocator corruption.
