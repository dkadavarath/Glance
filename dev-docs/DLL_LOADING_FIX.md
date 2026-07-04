# DLL Resolution and Loading Mechanics

In a hybrid desktop application, loading unmanaged dynamic link libraries (DLLs) across C# and Rust boundaries requires careful path resolution. Glance relies on two dynamic libraries:
1.  **`glance_native.dll`**: The native Rust backend compiling unmanaged business logic.
2.  **`pdfium.dll`**: The Google PDFium rendering binary, dynamically loaded by Rust.

This document describes how these libraries are resolved, loaded, and packaged.

---

## 1. Loading `glance_native.dll` from C#

WinUI 3 applications packaged with MSIX deploy binaries to a read-only system directory (`C:\Program Files\WindowsApps\...`). Unpackaged deployments execute directly from build outputs. To ensure the application finds `glance_native.dll` in both packaging environments, Glance implements a custom DLL import resolver.

### DLL Import Resolver Registration
In [GlanceNative.cs](file:///C:/Users/adria/GitHub/Glance/src/Interop/GlanceNative.cs), we register the custom resolver inside the static constructor:

```csharp
static GlanceNative()
{
    NativeLibrary.SetDllImportResolver(typeof(GlanceNative).Assembly, DllImportResolver);
}
```

### Path Resolution Logic
The `DllImportResolver` searches for `glance_native.dll` in multiple locations sequentially:

1.  **`AppContext.BaseDirectory`**: The execution directory of the application. Suitable for unpacked testing and standard bin outputs.
2.  **Parent Directory (`..`)**: The directory one level up. Packaged WinUI applications deploy content binaries slightly offset from the app entry point.
3.  **System PATH**: Falls back to the operating system's default search algorithm (e.g., standard system directories or PATH environment variable) if not found in application subfolders.

If the DLL exists at a resolved path, `NativeLibrary.Load(dllPath)` loads the binary into the process address space.

---

## 2. Loading `pdfium.dll` from Rust

The Rust backend (`glance_native.dll`) does not statically compile PDFium. Instead, it dynamically binds to `pdfium.dll` at runtime.

### Bindings Setup
In [pdf_engine.rs](file:///C:/Users/adria/GitHub/Glance/native/glance-native/src/rendering/pdf_engine.rs), the initialization searches for the binary using:

```rust
let pdfium = Pdfium::new(
    Pdfium::bind_to_library("pdfium.dll")
        .or_else(|_| Pdfium::bind_to_library("bin/pdfium.dll"))
        .or_else(|_| Pdfium::bind_to_system_library())
        .map_err(|e| format!("Failed to load pdfium.dll: {:?}", e))?
);
```

*   **`pdfium.dll`**: Looks in the current working directory of the executing process.
*   **`bin/pdfium.dll`**: Fallback subdirectory, commonly used in Visual Studio project structures.
*   **System Library**: Standard Windows system paths (`C:\Windows\System32`, etc.).

---

## 3. MSBuild Integration & Automatic Deployment

To avoid manually copying DLL files after compilation, the C# project file ([Glance.csproj](file:///C:/Users/adria/GitHub/Glance/src/Glance.csproj)) automates deployment using MSBuild targets.

### Including DLLs as Content
The project specifies that the native binaries must be treated as project content and copied when newer:

```xml
<ItemGroup>
  <!-- Copy from Rust build directory to C# output directory -->
  <Content Include="..\native\glance-native\target\release\glance_native.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>glance_native.dll</Link>
  </Content>
  <Content Include="pdfium.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>pdfium.dll</Link>
  </Content>
</ItemGroup>
```

### Post-Build Copy Target
During the build phase, MSBuild executes the `CopyNativeDll` target after compiling the C# project to copy both unmanaged binaries directly to the target output directory (`$(OutputPath)`):

```xml
<Target Name="CopyNativeDll" AfterTargets="Build">
  <Copy SourceFiles="..\native\glance-native\target\release\glance_native.dll" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true" />
  <Copy SourceFiles="pdfium.dll" DestinationFolder="$(OutputPath)" SkipUnchangedFiles="true" />
  <Message Text="Copied native DLLs to $(OutputPath)" Importance="High" />
</Target>
```

*   This target guarantees that `glance_native.dll` and `pdfium.dll` reside side-by-side with `Glance.exe` in the output folder.
*   It ensures the resolver always loads the latest compiled DLL binaries, preventing missing library crashes (`DllNotFoundException`).
