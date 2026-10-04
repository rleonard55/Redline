# Redline — working notes for Claude

Redline is a Windows system-wide spelling/grammar checker: it follows keyboard focus across apps via
UI Automation (UIA), analyzes text locally (Windows Spell Checking API + Harper via Rust FFI), draws
squiggles in a click-through overlay, and applies fixes by selecting the exact range and typing.
`IMPLEMENTATION_PLAN.md` is the phase plan; `docs/compatibility.md` is the per-app evidence and the
list of app quirks and how each is handled. Read both before changing behavior.

## Status (2026-10-03)

| Phase | State | Commit |
|---|---|---|
| 0 Compatibility harness | done | 78e4d2c |
| 1 Core prototype | done | 78e4d2c, c2ee9d1 |
| 2 Corrections (engine, popup, ignore/dictionary) | done | 205f0de |
| 3 Inline squiggle overlay | done | f683e89 |
| 4 Compatibility hardening | done | 93bb57d, d8e6afb |
| 5 Product hardening | **in progress** — part 1 (settings model/store/runtime hooks) done | 4d1ceb4 |
| 6 Optional AI | not started | |

### Phase 5 — next steps, in order
1. **Wire settings into the app** (`src/Redline.App/App.xaml.cs`): register `SettingsStore`
   (`SettingsStore.DefaultPath`) in DI; on start and on `Changed` apply:
   `tracker.SetPaused(!General.Enabled)` (and make the tray Pause item update `Enabled`),
   `cache.SetWriting(Writing)`, `pipeline.Debounce = AnalysisDelayMs`,
   `security.SetUserExclusions(Applications.Excluded)`, hotkey (below), Run key (below).
   `General.Language` is passed to `SpellAnalyzer` at startup only (show "applies after restart").
2. **Hotkey from settings**: replace the hard-coded list in `App.xaml.cs` with `Hotkey.TryParse(General.Hotkey)`;
   keep fallbacks (Win+Alt+Space, Ctrl+Alt+;) only if the configured one is taken, and tell the user.
   Re-register on change via a `TryChangeHotkey(Hotkey)` method the settings UI calls *before* saving.
3. **Start with Windows**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Redline` =
   quoted exe path. Default is OFF (`StartWithWindows=false`) until an installer exists — don't
   register a bin\Debug path at login. Make it testable against a throwaway HKCU key.
4. **Settings window** (WPF, tray "Settings…"): tabs General (enabled, language, delay, hotkey
   capture box, start with Windows), Writing (spelling/grammar/style), Apps (user exclusions
   add/remove; built-in list `SecurityFilter.BuiltInExclusions` shown read-only), Dictionary
   (personal dictionary words with Remove; `IgnoreList.IgnoredRules` with Restore).
5. Crash handling + log retention (7 days / 50 MB, never user text), diagnostics-mode toggle,
   perf counters.
6. Installer — **needs the user's decision**: MSIX (needs code-signing cert) vs WiX MSI. Must ship
   `harper_ffi.dll`. Installer should turn on start-with-Windows.
7. Auto-update — **needs the user's decision** on hosting (e.g. GitHub Releases).

Deferred (documented in docs/compatibility.md): VS Code editor support (needs a VS Code extension);
multi-monitor / non-100% DPI is implemented but untested (user has one 100% monitor).

## Layout
- `src/Redline.Core` — models, `TextDiff`, `DocumentState`, `AnalysisPipeline`, `IssueSet`,
  `IssueCacheManager`, `IgnoreList`, `CorrectionMath` (unit mapping, drift search helpers,
  `SelectionMatches`, `EquivalentForVerification`), `Geometry/OverlayLayout`, `Settings/*`.
- `src/Redline.Windows` — `UiaDispatcher` (STA), `FocusMonitor`, `ForegroundWindowMonitor`,
  `WindowEventMonitor`, `SurfaceTracker`, `SecurityFilter`, `AdapterSelector`,
  `Adapters/GenericUiaAdapter`, `Corrections/ReplacementEngine`, `Input/KeyboardInput`,
  `Input/ClipboardScope`.
- `src/Redline.Analysis` — `SpellAnalyzer` (COM `ISpellCheckerFactory`; KD-2 revised), `PersonalDictionary`.
- `src/Redline.Analysis.Harper` + `native/harper-ffi` — Harper grammar via Rust cdylib (harper-core =2.11.0).
- `src/Redline.Annotations` — `OverlayWindow`, `SquiggleLayer`, `OverlayManager`.
- `src/Redline.App` — WPF tray host, DI, diagnostics window, `SuggestionPopup`, `CorrectionController`, `GlobalHotkey`.
- `tools/Redline.CompatibilityHarness` — Phase 0 harness. `tools/manual-tests` — real-app probes and scripts (see its README).

## Build & test
- `dotnet build Redline.sln`; `dotnet test Redline.sln`.
- Interactive tests (take focus, type, use the clipboard): `REDLINE_INTERACTIVE_TESTS=1 dotnet test`.
- VSTest's summary "Total" under-counts theory rows; use `--logger "console;verbosity=normal"` to list what ran.
- Rust: `cargo test --release` in `native/harper-ffi` (MSVC toolchain installed). `dotnet build -p:BuildHarper=true`
  also runs cargo. The DLL is copied to outputs when `native/harper-ffi/target/release/harper_ffi.dll` exists.
- If the user is running `src/Redline.App/bin/.../Redline.exe`, building `Redline.App` fails (files locked).
  Don't kill their instance without asking; test projects still build and run.
- Git repo root is this folder (`Redline/`); the parent `Documents` folder is a separate repo.

## Invariants — don't break these
- **Never type into anything without verifying**: snapshot version current, live text unchanged,
  target's *exact top-level window* is foreground, and the selection reads as the flagged text
  (`CorrectionMath.SelectionMatches`: only CRLF/LF and zero-width chars tolerated, contiguous ranges).
  After editing, verify; undo if the result differs (only trailing blanks at document end are forgiven).
- Selection before typing; typed characters are sent **one SendInput call per char, 10 ms apart**
  (Notepad corrupts batched Unicode input after a space).
- Clipboard fallback only when the clipboard is empty/plain text; restore it unless someone else changed it.
- **Geometry/UIA queries against the target only while the target is foreground** — querying a Win32
  edit through UIA pulls focus to it (this closed the suggestion popup once).
- `new DispatcherTimer(interval, priority, callback, dispatcher)` **starts** the timer. Use the
  `(priority, dispatcher)` overload and call `Start()` explicitly.
- Security: password fields, credential-looking fields, password managers and **terminal input**
  (xterm-helper-textarea, TermControl, ConsoleWindowClass) are blocked and detach immediately.
- Logs never contain user text (process, control type/class, counts, timings only).

## Session tooling gotchas (Claude Code on this machine)
- Bash heredocs: an apostrophe in the content (e.g. "can't") can break the tool's wrapper, and `\\`/`\n`
  inside heredocs may be mangled. For non-trivial edits, write a Python patch script with the Write
  tool and run it; avoid backslash escapes by using `(char)0x0A`-style code where possible.
- After writing C#/Rust files, grep for stray control characters:
  `grep -nP '[\x00-\x08\x0b\x0c\x0e-\x1f]' <files>`.
- PowerShell 5.1 `Add-Type` needs `-ReferencedAssemblies System.Drawing` for drawing types.

## Testing in the user's real apps
- The user keeps test drafts containing "This is an tset" in Notepad, Word, Teams (chat, appointment)
  and Outlook (PWA in Chrome). Window handles change between sessions — look them up by title.
- Ask before touching the user's apps; never press Enter in Teams/Outlook; guard every scripted
  keystroke with a foreground check (see `tools/manual-tests/scripts/redline_popup_e2e.ps1`).
- Verify squiggles by screenshot (BitBlt with CAPTUREBLT — layered windows are otherwise missing).
- The suggestion hotkey is Ctrl+Alt+. (Ctrl+Alt+Space belongs to the Claude desktop app).
