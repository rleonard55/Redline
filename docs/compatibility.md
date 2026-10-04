# Redline Compatibility

> Updated: 2026-10-03 (Phase 4). Windows 11 (10.0.26200), one monitor at 100% scaling.
> Every surface below uses the generic UI Automation adapter — no app-specific adapter has been
> needed yet (see "Phase 4 decision" below).

## Verified with the running app

Tested end to end against real documents: focus tracking (12 app switches per app), analysis,
squiggle overlay (checked by screenshot), and applying corrections through the replacement engine.

| Application | Engine | Surface (class) | Attach on switch | Squiggles | Apply fix | Notes |
|---|---|---|:---:|:---:|:---:|---|
| Windows 11 Notepad | Win32 | `RichEditD2DPT` | ✅ ~30–125 ms | ✅ | ✅ select + type | Typed text must be paced (see quirks) |
| Microsoft Word | Win32 | `_WwG` | ✅ ~40–460 ms | ✅ | ✅ select + type | Ribbon boxes (`NetUI*`) are skipped; caret offset works via selection |
| Teams PWA — appointment title | Edge | `fui-Input__input` | ✅ ~35–55 ms | ✅ | ✅ | |
| Teams PWA — appointment notes | Edge | `DziEn Z6_Ux` (rich editor) | ✅ | ✅ | ✅ select + type | |
| Teams PWA — chat box | Edge | `ck-editor__editable` (CKEditor 5) | ✅ ~40 ms | ✅ | ✅ via hotkey + popup (never sends) | Phase 0: ValuePattern.SetValue fails here; select + type is used instead |
| Outlook PWA — subject | Chrome | `fui-Input__input` | ✅ ~30–60 ms | ✅ | ✅ (Phase 2) | Empty field's placeholder is ignored |
| Outlook PWA — body | Chrome | `customScrollBar … DziEn` | ✅ | ✅ incl. text after signature images | ✅ select + type / guarded paste | Signature with embedded images: see drift quirk |
| ChatGPT, Claude desktop, Antigravity IDE chat | Electron/Chromium | various | ✅ | ✅ (ChatGPT, live) | not tested | Picked up incidentally during testing |
| Web page `<textarea>` | Edge, Chrome | (none) | ✅ | ✅ | ✅ select + type | Local test page |
| Web page `<input type=text>` | Edge, Chrome | (none) | ✅ | ✅ | ✅ select + type | Empty input's placeholder is ignored |
| Web page `contenteditable` (no ARIA role) | Edge, Chrome | `Group` | ✅ | ✅ incl. text after an image | ✅ select + type | Accepted as a Chromium Group that has TextPattern and is focusable |
| Web page focusable non-editable element (`tabindex=0`) | Edge, Chrome | `Group` | ignored ✅ | — | — | Has no TextPattern |
| Web page `<input type=password>` | Edge, Chrome | (none) | blocked ✅ | — | — | Sensitive |
| VS Code editor (Monaco) | Electron | `native-edit-context` | reads as empty | — | — | **Not supported.** Without screen-reader mode Monaco exposes only "The editor is not accessible at this time…"; Redline treats it as an empty field and does nothing. See below |

Display scaling (checked 2026-10-04 at 125% on one monitor with `tools/manual-tests/scripts/scale_check.ps1`):
DPI-aware apps (the ones above) are correct. DPI-virtualized windows (a DPI-unaware app, or a system-aware app
after the scale changed) were wrong and are now handled — see the quirk table. Not checked live: moving between
monitors with different scaling (one monitor).

Always-on-top targets: underlines now show (overlay becomes topmost with the target), checked live 2026-10-04.

## App-specific quirks and how Redline handles them

| Quirk | Where | Handling |
|---|---|---|
| Stray focus event for a hidden, non-focusable helper `Edit` 200–500 ms after the real one | Edge, Chrome, Electron | Focus events for non-focusable elements are checked against UIA's global focus |
| Focus briefly passes through the page/container when a window is re-activated | Web apps, IDEs | Non-sensitive detaches wait 300 ms and cancel if focus returns; sensitive blocks are immediate |
| Empty input exposes its placeholder ("Subject") as text via TextPattern | Chromium | When ValuePattern reports "", the field is treated as empty |
| Character units drift from UTF-16 after embedded objects (images) and paragraph breaks — e.g. +2 after an email signature | Chromium rich editors | Fixed conventions first, then a bounded nearby search; a match must also reproduce surrounding context |
| Line break counts as one UIA character; `Move` expands an empty range to one unit | WPF | CRLF-as-one candidate; ranges re-collapsed after `Move` |
| `Select()` is applied asynchronously | Chromium | Selection read-back is polled for up to 500 ms |
| UIA reports DOM focus while the page's child window (`Chrome_RenderWidgetHostHWND`) is foreground, so keystrokes don't reach the page | Chromium | Typing requires the target's exact top-level window to be foreground |
| First keystrokes after activation are dropped | Chromium PWAs | 150 ms settle after Redline activates a window, and after the suggestion popup hands focus back |
| UIA `SetFocus` can leave the page's child window foreground | Chromium | `SetFocus` first, then bring the top-level window forward; the overlay compares the foreground window's root |
| Batched Unicode keystrokes after a space all become the batch's last character ("ab cd" → "ab dd") | Windows 11 Notepad | Characters are sent one per SendInput call, 10 ms apart |
| Emptied last paragraph is represented as a trailing space instead of a line break | Chromium | Verification forgives differences in trailing whitespace only |
| Lone Alt press activates the menu bar / ribbon key tips, moving focus off the editor | Notepad, Word | (Test-harness artifact; users switching normally aren't affected) |
| Address bar and ribbon fields are editable but never prose | Chrome/Edge, Office | `OmniboxViewViews` and `NetUI*` are skipped |
| Terminal input receives passwords typed at sudo/ssh prompts | xterm.js (VS Code, Antigravity), Windows Terminal, conhost | Blocked as sensitive |
| `contenteditable` without an ARIA role is exposed as `Group`, not `Edit` | Chromium | Accepted when it has TextPattern and is focusable (non-editable focusable elements have no TextPattern) |
| Spelling and grammar both flag the same word | All | One squiggle per range (spelling, grammar, punctuation, style) |
| Querying a classic Win32 edit control's text geometry through UIA pulls focus back to it | Win32 Edit (e.g. WinForms) | Geometry is only queried while the target window is in front (it closed the suggestion popup otherwise) |
| Text rectangles sit a few pixels high, so underlines cross the lower part of the letters (any scale) | Win32 Edit (WinForms) with large fonts | Not handled yet; cosmetic |
| In a window Windows bitmap-scales, UIA's Win32 proxy returns text rectangles as the control's physical origin plus *unscaled* character offsets, so underlines shrink toward the control's top-left | Win32 / WinForms controls in DPI-unaware (or stale system-aware) apps at scaling other than 100% | When the control's window DPI differs from its monitor's, offsets from the client origin are scaled by monitor ÷ window DPI (`DpiVirtualization`). Verified at 125%: underlines and the hover pill line up |

## Phase 4 decision

The plan reserved specialized adapters (Chrome DevTools Protocol, Office COM, Win32 `EM_*`
messages, Electron) for applications where the generic UI Automation adapter proves insufficient.
None has so far: every quirk above was solvable inside the generic adapter, engine, or tracker.
Revisit if a future target can't read text, can't map geometry, or can't be edited by select + type.

The one such target found so far is **VS Code's code editor**. Monaco exposes no document text over
UI Automation by default; in screen-reader mode it exposes only a page of lines around the cursor
through a hidden textarea, with no usable on-screen geometry. Supporting it would need the plan's
"Electron adapter" in the form of a VS Code extension (estimated 5–8 days). Deferred: code editors
have their own spell-check extensions, and Redline's other Electron surfaces (chat inputs) work.

---

# Phase 0 Compatibility Harness Report (historical)


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
