# Redline — working notes for Claude

Redline is a Windows system-wide spelling/grammar checker: it follows keyboard focus across apps via
UI Automation (UIA), analyzes text locally (Windows Spell Checking API + Harper via Rust FFI), draws
squiggles in a click-through overlay, and applies fixes by selecting the exact range and typing.
`IMPLEMENTATION_PLAN.md` is the phase plan; `docs/compatibility.md` is the per-app evidence and the
list of app quirks and how each is handled. Read both before changing behavior.

## Status (2026-10-03)

| Phase | State | Commit |
|---|---|---|
| 0 Compatibility harness | done | 3f1f9de |
| 1 Core prototype | done | 3f1f9de, 9af3f8d |
| 2 Corrections (engine, popup, ignore/dictionary) | done | 830aaa4 |
| 3 Inline squiggle overlay | done | 4a5bb77 |
| 4 Compatibility hardening | done | 2fa0c5f, 02bbf49 |
| 5 Product hardening | **in progress** — part 1 (settings model/store/runtime hooks) done; part 2 (settings wired in, hotkey from settings, Run key, Settings window) done; part 3 (crash reports, log retention, diagnostics mode, perf counters) done; WiX MSI installer done | 9a62723, 2ad6818, 52f4216, 5db7d84 |
| 6 Optional AI | not started | |

### Phase 5 — part 2 (done), how it fits together
- `App.ApplySettings(old, new)` applies everything at startup (`old` null) and on `SettingsStore.Changed`
  (marshalled to the UI thread). Tray Pause flips `General.Enabled`; the tray only *reflects* state (`SetPaused`).
  At startup it doesn't call `tracker.SetPaused(false)` (resume would schedule focus evaluation before Start).
- `HotkeyManager`: configured hotkey, falls back to Win+Alt+Space / Ctrl+Alt+; only if taken (tray notice).
  `TryChangeHotkey` registers the new one before releasing the old. The settings window calls it *before* saving.
  `Suspended` mutes presses while the capture box has focus.
- `StartupRegistration` (Redline.Windows): Run key value `Redline` = quoted exe path; tests use a throwaway
  `HKCU\Software\Redline.Tests\Run-<guid>`. At startup only an existing opt-in is refreshed; changes apply both ways.
- `SettingsWindow` (tray "Settings…"): saves on every change. Language shows "applies after restart".
- **Hover quick fix** (`Annotations/HoverController` + `HoverPill`, geometry in `Core/Geometry/HoverLayout`):
  resting the pointer on a squiggle for 300 ms shows a pill under the word: [● top suggestion | ⋯].
  Suggestion → `CorrectionController.ApplyFirstSuggestionAsync` (engine-verified); ⋯ → the full popup.
  The cursor is polled (50 ms, only while squiggles are shown) against the rectangles the overlay already
  measured, so there are no extra UIA queries and the overlay stays click-through. It arms only after the
  pointer moves (typing disarms it), not while mouse buttons are down, and only if the pointer is over the
  target (WindowFromPoint) and the target is in front. Setting: `General.HoverSuggestions` (default on).
- Hover pill verified live with `tools/manual-tests/scripts/redline_hover_e2e.ps1` (apply keeps the form in front,
  no re-pop without moving, ⋯ popup takes focus, Ignore hands focus back). Not yet verified live: tray Pause
  round-trip, hotkey capture with real key presses, Start-with-Windows toggle (the Settings window itself was
  smoke-tested via a scratch WPF harness).

### Phase 5 — part 3 (done)
- `Core/Diagnostics/LogFiles`: daily `redline-yyyyMMdd.log`, rolls to `-1`, `-2`… at 10 MB; prunes logs and
  `crash-*.txt` older than 7 days, then oldest-first to 50 MB total (never the current file). Prunes on open/rollover.
- `DiagnosticsLog.FileLevel`: Information normally, Debug when `General.DiagnosticsMode` is on (ring buffer for the
  Diagnostics window keeps every level). Error+ with an exception also writes the stack trace (file only).
- `Core/Diagnostics/CrashReport`: AppDomain unhandled exceptions write `crash-*.txt` (types, messages, stack,
  version, OS, uptime); terminating ones also leave a `crash-pending` marker, so the next start shows a tray notice.
- `Core/Diagnostics/PerfCounters` (DI singleton, optional ctor param where used): `read` (TextChangeWatcher),
  `analysis` + `analysis.<analyzer>` (App), `overlay` (OverlayManager layout), `correction` (ReplacementEngine).
  Shown live in the Diagnostics window ("Timings"); summary logged every 5 min in diagnostics mode and at exit.
- Settings window has a Diagnostics tab (toggle + Open logs folder).
- Known flaky: `UiaIntegrationTests.GenericAdapter_ReadsText_Caret_AndGeometry` sometimes fails in
  `AutomationElement.FromHandle` (COMException) — seen 2026-10-03 while the user's Redline was running; passes on re-run.

### Phase 5 — installer (done)
- `installer/build.ps1 [-SkipHarper] [-Version x.y.z]` → `artifacts/Redline-<ver>-x64.msi` (cargo, self-contained
  win-x64 publish to `artifacts/publish`, then `installer/Redline.Installer.wixproj`, WiX **6.0.2** — v7 needs the
  OSMF EULA). Version comes from `Directory.Build.props`. The wixproj is not in Redline.sln.
- Per-user (no elevation) into `%LOCALAPPDATA%\Programs\Redline`; Start-menu shortcut; launches Redline after install
  (`LAUNCHAPP=0` to skip). User data in `%LOCALAPPDATA%\Redline` is kept on uninstall.
- Start with Windows: the MSI owns HKCU Run `Redline` (`STARTWITHWINDOWS=0` to skip); on upgrade it's re-added only
  if present before (RegistrySearch + SetProperty). **The Run key is the truth**: at startup the app syncs
  `General.StartWithWindows` from it and never rewrites it; only toggling the setting writes or removes it.
- Closing a running instance: `Redline.exe --exit` sets the `Local\Redline.App.Exit` event, waits 10 s, then kills.
  The MSI runs it before InstallValidate on uninstall/upgrade. Don't use util:CloseApplication: WM_CLOSE only closes
  the hidden WPF windows and it's sequenced after RemoveFiles — the uninstall hung for minutes.
- Icon: `src/Redline.App/Redline.ico` from `installer/make-icon.ps1` (tray design; DIB frames + 256 px PNG).
- Verified 2026-10-03 on the user's machine: install, upgrade 0.5.0→0.5.1 while running (graceful exit, relaunch),
  upgrade with startup turned off (stays off), uninstall while running (8 s, everything removed). Not tested: the
  interactive (non-/qn) install, which has no wizard UI — just the progress dialog.

### Phase 5 — next steps, in order
1. Live check of the remaining part-2 items above (needs the user's running instance closed; ask first —
   ending the process from a session may be blocked by auto mode, so ask the user to Exit from the tray).
   Also part 3: Diagnostics tab toggle changes the file level, Timings row fills in, Open logs folder works.
2. Auto-update — **needs the user's decision** on hosting (e.g. GitHub Releases).

Deferred (documented in docs/compatibility.md): VS Code editor support (needs a VS Code extension);
multi-monitor / non-100% DPI is implemented but untested (user has one 100% monitor).

## Layout
- `src/Redline.Core` — models, `TextDiff`, `DocumentState`, `AnalysisPipeline`, `IssueSet`,
  `IssueCacheManager`, `IgnoreList`, `CorrectionMath` (unit mapping, drift search helpers,
  `SelectionMatches`, `EquivalentForVerification`), `Geometry/OverlayLayout`, `Settings/*`.
- `src/Redline.Windows` — `UiaDispatcher` (STA), `FocusMonitor`, `ForegroundWindowMonitor`,
  `WindowEventMonitor`, `SurfaceTracker`, `SecurityFilter`, `AdapterSelector`,
  `Adapters/GenericUiaAdapter`, `Corrections/ReplacementEngine`, `Input/KeyboardInput`,
  `Input/ClipboardScope`, `StartupRegistration`.
- `src/Redline.Analysis` — `SpellAnalyzer` (COM `ISpellCheckerFactory`; KD-2 revised), `PersonalDictionary`.
- `src/Redline.Analysis.Harper` + `native/harper-ffi` — Harper grammar via Rust cdylib (harper-core =2.11.0).
- `src/Redline.Annotations` — `OverlayWindow`, `SquiggleLayer`, `OverlayManager`.
- `src/Redline.App` — WPF tray host, DI, diagnostics window, `Settings/SettingsWindow`, `SuggestionPopup`,
  `CorrectionController`, `GlobalHotkey`, `HotkeyManager`.
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
- The hover pill must never activate (WS_EX_NOACTIVATE + MA_NOACTIVATE): clicking it has to leave the target
  in front with its caret, or the engine's foreground check fails (and the user's focus jumps).
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
