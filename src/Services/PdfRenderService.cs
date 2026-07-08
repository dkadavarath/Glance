using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Glance.Interop;

namespace Glance.Services;

/// <summary>
/// Service for rendering PDF pages to PNG using Rust backend.
/// Handles PDF loading, page rendering, and async/await bridge.
/// </summary>
public class PdfRenderService : IDisposable
{
    private IntPtr _pdfEngineHandle = IntPtr.Zero;
    private bool _disposed = false;
    private readonly SemaphoreSlim _ffiLock = new(1, 1);

    /// <summary>
    /// Initialize PDF renderer for a given PDF file path.
    /// </summary>
    /// <param name="pdfFilePath">Full path to the PDF file</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <exception cref="InvalidOperationException">If PDF initialization fails</exception>
    public async Task InitializeAsync(
        string pdfFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(pdfFilePath))
        {
            throw new ArgumentException("PDF file path cannot be null or empty", nameof(pdfFilePath));
        }

        if (_pdfEngineHandle != IntPtr.Zero)
        {
            throw new InvalidOperationException("PDF engine already initialized. Dispose first to initialize a new file.");
        }

        await _ffiLock.WaitAsync(cancellationToken);
        try
        {
            await AsyncBridge.RunNativeAsync(() =>
            {
                _pdfEngineHandle = GlanceNative.pdf_engine_create(pdfFilePath);
                if (_pdfEngineHandle == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"Failed to initialize PDF engine for '{pdfFilePath}'");
                }

                System.Diagnostics.Debug.WriteLine($"✓ PDF engine initialized for '{pdfFilePath}'");
            }, cancellationToken);
        }
        finally
        {
            _ffiLock.Release();
        }
    }

    /// <summary>
    /// Render a page from the loaded PDF to PNG bytes.
    /// </summary>
    /// <param name="pageIndex">Zero-based page index</param>
    /// <param name="width">Output width in pixels</param>
    /// <param name="height">Output height in pixels</param>
    /// <param name="dpi">Dots per inch for scaling (typically 96 or 150)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>PNG image bytes (valid PNG format)</returns>
    /// <exception cref="InvalidOperationException">If PDF engine not initialized or rendering fails</exception>
    public async Task<byte[]> RenderPageAsync(
        uint pageIndex,
        uint width,
        uint height,
        float dpi = 96.0f,
        CancellationToken cancellationToken = default)
    {
        if (_pdfEngineHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDF engine not initialized. Call InitializeAsync first.");
        }

        if (width == 0 || height == 0)
        {
            throw new ArgumentException("Width and height must be greater than zero");
        }

        await _ffiLock.WaitAsync(cancellationToken);
        try
        {
            if (_pdfEngineHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDF engine was disposed.");
            }
            return await AsyncBridge.RunNativeAsync(() =>
            {
                var opts = new GlanceNative.RenderOptions
                {
                    PageIndex = pageIndex,
                    Width = width,
                    Height = height,
                    Dpi = dpi
                };

                IntPtr pngDataPtr = IntPtr.Zero;
                ulong pngDataLen = 0;

                try
                {
                    var response = GlanceNative.pdf_render_page(
                        _pdfEngineHandle,
                        ref opts,
                        out pngDataPtr,
                        out pngDataLen);

                    if (!response.Success)
                    {
                        string errorMsg = response.ErrorMsg != IntPtr.Zero
                            ? Marshal.PtrToStringAnsi(response.ErrorMsg) ?? "Unknown error"
                            : "Unknown error (null error message)";

                        // Free error message
                        if (response.ErrorMsg != IntPtr.Zero)
                        {
                            GlanceNative.memory_free(response.ErrorMsg);
                        }

                        throw new InvalidOperationException(
                            $"Failed to render page {pageIndex}: {errorMsg}");
                    }

                    // Copy PNG data from unmanaged memory
                    byte[] pngBytes = new byte[pngDataLen];
                    if (pngDataPtr != IntPtr.Zero && pngDataLen > 0)
                    {
                        Marshal.Copy(pngDataPtr, pngBytes, 0, (int)pngDataLen);
                    }

                    System.Diagnostics.Debug.WriteLine(
                        $"✓ Page {pageIndex} rendered to PNG ({pngDataLen} bytes, {width}x{height}px)");

                    return pngBytes;
                }
                finally
                {
                    // Free PNG data (CRITICAL)
                    if (pngDataPtr != IntPtr.Zero)
                    {
                        GlanceNative.memory_free(pngDataPtr, pngDataLen);
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            _ffiLock.Release();
        }
    }

    /// <summary>
    /// Search for text occurrences across the document pages.
    /// </summary>
    /// <param name="query">Text query to search for</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>JSON string containing search matches with coordinates</returns>
    /// <exception cref="InvalidOperationException">If PDF engine is not initialized or search fails</exception>
    public async Task<string> SearchTextAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        if (_pdfEngineHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("PDF engine not initialized. Call InitializeAsync first.");
        }

        if (string.IsNullOrEmpty(query))
        {
            return "[]";
        }

        await _ffiLock.WaitAsync(cancellationToken);
        try
        {
            if (_pdfEngineHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDF engine was disposed.");
            }
            return await AsyncBridge.RunNativeAsync(() =>
            {
                IntPtr jsonDataPtr = IntPtr.Zero;
                ulong jsonDataLen = 0;

                try
                {
                    var response = GlanceNative.pdf_search_text(
                        _pdfEngineHandle,
                        query,
                        out jsonDataPtr,
                        out jsonDataLen);

                    if (!response.Success)
                    {
                        string errorMsg = response.ErrorMsg != IntPtr.Zero
                            ? Marshal.PtrToStringAnsi(response.ErrorMsg) ?? "Unknown error"
                            : "Unknown error";

                        if (response.ErrorMsg != IntPtr.Zero)
                        {
                            GlanceNative.memory_free(response.ErrorMsg);
                        }

                        throw new InvalidOperationException($"Search failed: {errorMsg}");
                    }

                    // Convert JSON data from unmanaged memory
                    string jsonStr = "[]";
                    if (jsonDataPtr != IntPtr.Zero && jsonDataLen > 0)
                    {
                        jsonStr = Marshal.PtrToStringAnsi(jsonDataPtr) ?? "[]";
                    }

                    return jsonStr;
                }
                finally
                {
                    // Free JSON data (CRITICAL)
                    if (jsonDataPtr != IntPtr.Zero)
                    {
                        GlanceNative.memory_free(jsonDataPtr);
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            _ffiLock.Release();
        }
    }

    /// <summary>
    /// Dispose of PDF engine resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var handleToDestroy = _pdfEngineHandle;
        _pdfEngineHandle = IntPtr.Zero;

        // Run disposal asynchronously on the thread pool to avoid blocking the UI thread and prevent deadlocks.
        // This allows any active page rendering tasks to complete and release the lock.
        Task.Run(async () =>
        {
            try
            {
                await _ffiLock.WaitAsync();
                if (handleToDestroy != IntPtr.Zero)
                {
                    GlanceNative.pdf_engine_destroy(handleToDestroy);
                    System.Diagnostics.Debug.WriteLine("✓ PDF engine destroyed asynchronously");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗ Error in native PDF engine disposal: {ex.Message}");
            }
            finally
            {
                try { _ffiLock.Release(); } catch { }
                _ffiLock.Dispose();
            }
        });
    }
}
