# Redline Compatibility Harness Report

> Updated: 2026-10-03 21:42:00 UTC
> OS: Microsoft Windows NT 10.0.26200.0 | CLR: 9.0.19 | DPI Scale: 100%

## Summary Matrix

| Application | Process | Surface Class | Read | Caret | Geometry | Change Event | Replace | Overall Verdict |
|---|---|---|:---:|:---:|:---:|:---:|:---:|:---:|
| **Google Chrome** | `chrome.exe` | Web TinyMCE (`mce-content-body`) | ✅ (5.0ms) | ✅ (offset 13) | ✅ (5.0ms) | ✅ (2x) | ✅ (137ms) | **100% Pass** (ValuePattern) |
| **Windows 11 Notepad** | `Notepad.exe` | Win32 Direct2D (`RichEditD2DPT`) | ✅ (0.5ms) | ✅ (offset 25) | ✅ (0.6ms) | ✅ (1x) | ✅ (117ms) | **100% Pass** (ValuePattern) |
| **Microsoft Word** | `WINWORD.exe` | Win32 Word Surface (`_WwG`) | ✅ (1.2ms) | ⚠️ (selection) | ✅ (4.0ms) | ✅ (3x) | ✅ (506ms) | **Pass** (Clipboard Fallback) |
| **Microsoft Teams PWA** | `msedge.exe` | Web CKEditor 5 (`ck-content`) | ✅ (3.3ms) | ✅ (offset 6) | ✅ (3.2ms) | ✅ (4x) | 🔄 (needs cascade) | **Pass** (Read/Geom/Events) |

---

## Detailed Findings

### 1. Microsoft Teams PWA (`msedge.exe` / WebView2) — CKEditor 5
- **Control Type:** `Edit`
- **Class Name:** `fui-Primitive ... ck ck-content ck-editor__editable ...` (Fluent UI + CKEditor 5)
- **Framework ID:** `Chrome`
- **Supported Patterns:** `Value`, `Text`, `ScrollItem`
- **Text Reading:** ✅ Fast (3.3 ms) via `TextPattern`.
- **Caret Tracking:** ✅ Exact character offset (`6`), selection tracking supported.
- **Geometry Mapping:** ✅ Fast (3.2 ms) with valid bounding box coordinates.
- **Change Detection:** ✅ Fired `TextChangedEvent` (4x), `ValueChangedEvent`, and `SelectionChangedEvent` with 1.38s latency.
- **Text Replacement:** `ValuePattern.SetValue` failed verification because CKEditor 5's virtual DOM model does not respond to raw container property changes. 
  - **Resolution**: Updated `ReplaceProbe` to automatically fall through to `TextRange.Select + Paste` and `Clipboard Paste Fallback` when `ValuePattern` verification fails.

### 2. Microsoft Word (`WINWORD.exe`) — Classic Desktop Office
- **Control Type:** `Document`
- **Class Name:** `_WwG` (Word's native rendering canvas window)
- **Framework ID:** `Win32`
- **Supported Patterns:** `Scroll`, `Text` (Word does not support `ValuePattern`)
- **Text Reading:** ✅ Fast (1.2 ms) via `TextPattern.DocumentRange.GetText(-1)`.
- **Caret / Selection:** ⚠️ Caret range alone is not exposed via TextPattern2, but `GetSelection()` returns the active selection range reliably.
- **Geometry Mapping:** ✅ Fast & Accurate (4.0 ms) — bounding rectangles returned cleanly.
- **Change Detection:** ✅ Fired `TextChangedEvent` (3x) and `SelectionChangedEvent` with 2.0s latency.
- **Text Replacement:** ✅ Replaced and verified via **Clipboard Paste Fallback** (`Ctrl+V` with clipboard save/restore) in 506 ms.

### 3. Google Chrome (`chrome.exe`) — Web Rich Text Editor
- **Control Type:** `Edit`
- **Class Name:** `mce-content-body -polaris` (TinyMCE Rich Text Editor)
- **Framework ID:** `Chrome`
- **Supported Patterns:** `Value`, `Text`, `ScrollItem`
- **Text Reading:** ✅ Success via `TextPattern` in 5.0 ms.
- **Caret / Selection:** ✅ Success! Accurate caret offset (`13`), selection tracking functional.
- **Geometry Mapping:** ✅ Success! Bounding rectangles accurately computed in 5.0 ms.
- **Change Detection:** ✅ Success! Both `TextChangedEvent` (2x) and `ValueChangedEvent` captured with 2.4s typing latency.
- **Text Replacement:** ✅ Success via `ValuePattern.SetValue` in 137.2 ms, verified correct.

### 4. Windows 11 Modern Notepad (`Notepad.exe`)
- **Control Type:** `Document`
- **Class Name:** `RichEditD2DPT`
- **Framework ID:** `Win32`
- **Supported Patterns:** `Value`, `Text`
- **Text Reading:** ✅ Instantaneous (0.5 ms) via `TextPattern`.
- **Caret / Selection:** ✅ Success! Exact caret tracking (offset `25`).
- **Geometry Mapping:** ✅ Success! Bounding rectangle mapped in 0.6 ms.
- **Change Detection:** ✅ Success! Captured `TextChangedEvent` on typing.
- **Text Replacement:** ✅ Success via `ValuePattern.SetValue` in 117.1 ms, verified correct.
