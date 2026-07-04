// Marshalling utilities for C# ↔ Rust FFI
use std::ffi::CStr;

/// Convert C string pointer to Rust String
pub fn c_string_to_rust(ptr: *const u8) -> Result<String, String> {
    if ptr.is_null() {
        return Err("Null pointer".to_string());
    }

    unsafe {
        CStr::from_ptr(ptr as *const i8)
            .to_str()
            .map(|s| s.to_string())
            .map_err(|e| format!("UTF-8 error: {}", e))
    }
}

/// Convert Rust String to C string pointer (caller must free with memory_free(ptr, 0))
pub fn rust_string_to_c(s: &str) -> *mut u8 {
    let c_str = std::ffi::CString::new(s).unwrap_or_else(|_| std::ffi::CString::new("").unwrap());
    c_str.into_raw() as *mut u8
}
