# Manual / real-app tests

Tools used to verify Redline against real applications (Phases 1–4). They drive the user's desktop:
take focus, type, take screenshots. **Ask before running them**, and check window handles first —
they change every session (find them with
`Get-Process | ? MainWindowTitle | select ProcessName, MainWindowTitle, MainWindowHandle`).

Scripts expect a build of `src/Redline.App` and write logs/screenshots under `%TEMP%` and
`%LOCALAPPDATA%\Redline\logs`. Some scripts contain hard-coded window handles from the session
they were written in; update them before use.

## Probes (`probes/`, console apps referencing the real Redline libraries)
| Probe | Usage | What it does |
|---|---|---|
| AppProbe | `dotnet run -- <hwnd> [hwnd…]` | Lists a window's text surfaces: security decision, adapter, text (only shown if it contains "tset"), caret, issues, verified geometry |
| CorrectionProbe | `dotnet run -- <hwnd> <classContains> [Strategy]` | Analyzes and applies fixes through the real ReplacementEngine until none are left |
| VisualCheck | `dotnet run -- <hwnd> <class> append <png> <text> line\|end [cleanup]` / `measure <png> <text> [cleanup]` / `remove-at <offset> <text>` | Appends test text, screenshots the running Redline's squiggles, checks geometry, cleans up |
| WebCheck | `dotnet run -- <hwnd> <outDir> <prefix> id\|class <key…>` | Per field: classify, focus, screenshot, analyze, apply first fix |
| FocusSpy | `dotnet run -- [none\|read\|subscribe\|both]` | Logs UIA focus events while switching into a window |
| FgSpy | `dotnet run -- <seconds>` | Logs system-wide foreground/focus WinEvents (found the popup-deactivation regression) |

## Scripts (`scripts/`, PowerShell)
- `redline_popup_e2e.ps1` — the full user flow in a throwaway WinForms window: hotkey → popup → fix,
  delete repeated word, ignore. Every keystroke is guarded by a foreground check. Run after any change
  to focus, overlay, popup or engine code. `_nontop` variant uses a non-topmost window.
- `redline_hover_e2e.ps1` — hover quick-fix pill: hover → pill → click suggestion (form must stay in front),
  hover → "⋯" → popup → Ignore. Moves the mouse pointer; clicks are guarded by WindowFromPoint on the pill.
  Note: the test form is TopMost, so squiggles aren't drawn over it (overlay stays out of the topmost band); hover still works.
- `redline_teams_chat_e2e.ps1` — same flow in the Teams chat box (never sends).
- `redline_overlay_*.ps1` — overlay screenshots: static, typing/move/scroll, scroll mid/settled.
- `redline_grace_wpf.ps1` — focus-bounce grace period vs immediate sensitive detach.
- `redline_notepad.ps1`, `redline_teams.ps1`, `redline_ide.ps1` — app-switching attach tests.
- `hotkey_check.ps1` — which global hotkeys are free; `placeholder_check.ps1` — ValuePattern vs TextPattern.

`webtest.html` — local page with textarea, input, placeholder input, contenteditable (plain and with an
image), a focusable non-editable card and a password field.
