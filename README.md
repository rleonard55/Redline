# Redline

Spelling and grammar checking for all of Windows. Redline follows the text box you're typing in —
Notepad, Word, Teams, Outlook on the web, chat apps — underlines mistakes in place, and fixes them
with a click or a hotkey.

Everything runs locally: spelling uses the Windows spell checker, grammar uses
[Harper](https://github.com/Automattic/harper). No text leaves your machine, and Redline's logs never
contain what you type.

> **Status:** pre-release (0.x). It works day to day on the apps listed in
> [docs/compatibility.md](docs/compatibility.md); expect rough edges elsewhere.

## Features

- Squiggles drawn right over the app you're typing in: red for spelling, blue for grammar, purple for
  punctuation (style suggestions, in amber, are optional)
- Point at a squiggle for a one-click fix, or press **Ctrl+Alt+.** for all suggestions
- Add to dictionary, ignore once, or ignore a grammar rule everywhere
- Skips password fields, password managers and terminals automatically; exclude any other app in Settings
- Every fix is verified before and after typing, and undone if the result isn't what was expected

## Install

Download `Redline-<version>-x64.msi` from [Releases](../../releases) and run it. It installs for the
current user only (no admin rights needed), starts Redline, and sets it to start when you sign in.
Redline lives in the system tray; right-click the icon for Settings, Pause and Exit.

The installer isn't code-signed yet, so Windows SmartScreen may warn about it
(**More info → Run anyway**).

Requires Windows 10 (version 2004) or Windows 11, x64.

## Build from source

Requires the .NET 9 SDK, and the Rust toolchain (MSVC) for the Harper grammar library.

```powershell
cd native/harper-ffi; cargo build --release; cd ../..
dotnet build Redline.sln
dotnet test Redline.sln
dotnet run --project src/Redline.App
```

Build the installer with `powershell -ExecutionPolicy Bypass -File installer/build.ps1` (output in `artifacts/`).

[IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) describes the architecture; [CLAUDE.md](CLAUDE.md) holds
the current status and working notes.

## License

MIT — see [LICENSE](LICENSE). Redline bundles [Harper](https://github.com/Automattic/harper)
(Apache-2.0) and its Rust dependencies under their own licenses.
