//! C ABI over harper-core for Redline.
//!
//! Contract (mirrored in `HarperInterop.cs`):
//! - `harper_lint(ptr, len)` takes UTF-8 bytes (not NUL-terminated) and returns a
//!   NUL-terminated UTF-8 JSON string owned by this library, or null on internal failure.
//!   The caller must release it with `harper_free` exactly once.
//! - All offsets in the JSON are UTF-16 code-unit offsets into the input, so .NET can use
//!   them directly as string indices.
//! - No panic ever unwinds across the boundary.

use std::ffi::{c_char, CString};
use std::panic::{self, AssertUnwindSafe};
use std::sync::{Arc, Mutex};

use harper_core::linting::{Lint, LintGroup, Linter, Suggestion};
use harper_core::spell::FstDictionary;
use harper_core::{Dialect, Document};
use serde::Serialize;

/// `Linter::lint` takes `&mut self` (it caches between runs), so the shared instance is
/// behind a mutex. Built lazily because constructing the curated group is not free.
static LINTER: Mutex<Option<LintGroup>> = Mutex::new(None);

#[derive(Serialize)]
struct LintResponse {
    ok: bool,
    error: Option<String>,
    lints: Vec<LintDto>,
}

#[derive(Serialize)]
struct LintDto {
    /// UTF-16 offset of the flagged text.
    start: usize,
    /// UTF-16 length of the flagged text.
    length: usize,
    kind: String,
    message: String,
    priority: u8,
    /// Each entry is the full replacement for the flagged span ("" = delete).
    suggestions: Vec<String>,
}

/// Lints `text_len` bytes of UTF-8 at `text_ptr`. Returns a JSON string the caller must free
/// with `harper_free`, or null if even the error response could not be produced.
///
/// # Safety
/// `text_ptr` must be valid for reads of `text_len` bytes (it may be null only if `text_len` is 0).
#[no_mangle]
pub unsafe extern "C" fn harper_lint(text_ptr: *const u8, text_len: usize) -> *mut c_char {
    let bytes: &[u8] = if text_len == 0 || text_ptr.is_null() {
        &[]
    } else {
        // SAFETY: guaranteed by the caller per the contract above.
        unsafe { std::slice::from_raw_parts(text_ptr, text_len) }
    };

    let response = match panic::catch_unwind(AssertUnwindSafe(|| lint_bytes(bytes))) {
        Ok(lints) => LintResponse { ok: true, error: None, lints },
        Err(payload) => LintResponse {
            ok: false,
            error: Some(panic_message(&payload)),
            lints: Vec::new(),
        },
    };

    to_c_string(&response)
}

/// Frees a string returned by `harper_lint`. Null is a no-op.
///
/// # Safety
/// `ptr` must be null or a pointer previously returned by `harper_lint` and not yet freed.
#[no_mangle]
pub unsafe extern "C" fn harper_free(ptr: *mut c_char) {
    if !ptr.is_null() {
        // SAFETY: ptr came from CString::into_raw in to_c_string, per the contract above.
        drop(unsafe { CString::from_raw(ptr) });
    }
}

/// Static, NUL-terminated version string. Do NOT pass to `harper_free`.
#[no_mangle]
pub extern "C" fn harper_ffi_version() -> *const c_char {
    concat!("harper-ffi ", env!("CARGO_PKG_VERSION"), " (harper-core 2.11.0)\0").as_ptr() as *const c_char
}

fn lint_bytes(bytes: &[u8]) -> Vec<LintDto> {
    // .NET's UTF-8 encoder turns lone surrogates into U+FFFD, which is also one UTF-16 unit,
    // so lossy decoding never shifts offsets for text coming from C#.
    let text = String::from_utf8_lossy(bytes);
    if text.trim().is_empty() {
        return Vec::new();
    }

    let document = Document::new_plain_english_curated(&text);
    let source = document.get_source();

    let lints = {
        // A previous panic while linting poisons the mutex; the cached state may be
        // inconsistent, so rebuild the linter rather than reuse it.
        let mut guard = match LINTER.lock() {
            Ok(g) => g,
            Err(poisoned) => {
                let mut g = poisoned.into_inner();
                *g = None;
                g
            }
        };
        let linter = guard.get_or_insert_with(|| {
            let dictionary: Arc<FstDictionary> = FstDictionary::curated();
            LintGroup::new_curated(dictionary, Dialect::American)
        });
        linter.lint(&document)
    };

    let utf16 = utf16_prefix(source);
    lints.iter().map(|lint| to_dto(lint, source, &utf16)).collect()
}

/// `prefix[i]` = UTF-16 length of `source[..i]`; has `source.len() + 1` entries.
fn utf16_prefix(source: &[char]) -> Vec<usize> {
    let mut prefix = Vec::with_capacity(source.len() + 1);
    let mut acc = 0usize;
    prefix.push(0);
    for c in source {
        acc += c.len_utf16();
        prefix.push(acc);
    }
    prefix
}

fn to_dto(lint: &Lint, source: &[char], utf16: &[usize]) -> LintDto {
    let start = lint.span.start.min(source.len());
    let end = lint.span.end.clamp(start, source.len());
    let flagged: String = source[start..end].iter().collect();

    let suggestions = lint
        .suggestions
        .iter()
        .map(|s| match s {
            Suggestion::ReplaceWith(chars) => chars.iter().collect(),
            Suggestion::InsertAfter(chars) => {
                let mut replacement = flagged.clone();
                replacement.extend(chars.iter());
                replacement
            }
            Suggestion::Remove => String::new(),
        })
        .collect();

    LintDto {
        start: utf16[start],
        length: utf16[end] - utf16[start],
        kind: format!("{:?}", lint.lint_kind),
        message: lint.message.clone(),
        priority: lint.priority,
        suggestions,
    }
}

fn to_c_string(response: &LintResponse) -> *mut c_char {
    let json = serde_json::to_string(response).unwrap_or_else(|e| {
        format!(r#"{{"ok":false,"error":"serialization failed: {}","lints":[]}}"#, e.to_string().replace('"', "'"))
    });
    // serde_json escapes control characters, so interior NULs are impossible; fall back to null defensively.
    match CString::new(json) {
        Ok(c) => c.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

fn panic_message(payload: &Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        format!("harper panicked: {s}")
    } else if let Some(s) = payload.downcast_ref::<String>() {
        format!("harper panicked: {s}")
    } else {
        "harper panicked".to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CStr;

    fn lint_json(text: &str) -> serde_json::Value {
        unsafe {
            let ptr = harper_lint(text.as_ptr(), text.len());
            assert!(!ptr.is_null());
            let json = CStr::from_ptr(ptr).to_str().unwrap().to_owned();
            harper_free(ptr);
            serde_json::from_str(&json).unwrap()
        }
    }

    #[test]
    fn empty_input_is_ok() {
        let v = lint_json("");
        assert_eq!(v["ok"], true);
        assert_eq!(v["lints"].as_array().unwrap().len(), 0);
    }

    #[test]
    fn finds_article_error() {
        let v = lint_json("This is an test.");
        assert_eq!(v["ok"], true);
        assert!(!v["lints"].as_array().unwrap().is_empty());
    }

    #[test]
    fn offsets_are_utf16() {
        // U+1F600 is 1 char in Rust but 2 UTF-16 units; the lint after it must be shifted by 2.
        let plain = lint_json("This is an test.");
        let emoji = lint_json("\u{1F600} This is an test.");
        let a = plain["lints"][0]["start"].as_u64().unwrap();
        let b = emoji["lints"][0]["start"].as_u64().unwrap();
        assert_eq!(b, a + 3); // emoji (2 units) + space (1)
    }

    #[test]
    fn free_null_is_noop() {
        unsafe { harper_free(std::ptr::null_mut()) };
    }
}
