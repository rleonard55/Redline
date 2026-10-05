# Redline — working notes for Claude

Redline is a Windows system-wide spelling/grammar checker: it follows keyboard focus across apps via
UI Automation (UIA), analyzes text locally (Windows Spell Checking API + Harper via Rust FFI), draws
squiggles in a click-through overlay, and applies fixes by selecting the exact range and typing.
`IMPLEMENTATION_PLAN.md` is the phase plan; `docs/compatibility.md` is the per-app evidence and the
list of app quirks and how each is handled. Read both before changing behavior.

## Status (2026-10-04)

| Phase | State | Commit |
|---|---|---|
| 0 Compatibility harness | done | 3f1f9de |
| 1 Core prototype | done | 3f1f9de, 9af3f8d |
| 2 Corrections (engine, popup, ignore/dictionary) | done | 830aaa4 |
| 3 Inline squiggle overlay | done | 4a5bb77 |
| 4 Compatibility hardening | done | 2fa0c5f, 02bbf49 |
| 5 Product hardening | **done** — part 1 (settings model/store/runtime hooks) done; part 2 (settings wired in, hotkey from settings, Run key, Settings window) done; part 3 (crash reports, log retention, diagnostics mode, perf counters) done; WiX MSI installer done; auto-update + third-party notices done; Settings/tray live checks pass; plan-gap review closed (compatibility record, perf rates, overlay tests, CI) | 9a62723, 2ad6818, 52f4216, 5db7d84, d53a042, 3b5fa45, db1e863; releases v0.6.0, v0.7.0 |
| 6 Optional AI | on-device AI grammar (GRMR-V3-G1B, opt-in) done; AI rewriting (6.1) **on hold** by the owner's choice | 3e6a44e; release v0.7.0 |

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
  Dictionary tab: *Import…* adds words from Word custom dictionaries (`%APPDATA%\Microsoft\UProof\*.DIC`, UTF-16)
  or any word list (`Core/Corrections/WordListFile`; `IPersonalDictionary.AddRange` saves once). The Windows user
  dictionary (`%APPDATA%\Microsoft\Spelling`) needs no import: the Windows spell checker already honors it.
  `ShowTab("Apps")` selects a tab (`App.ShowSettings(tab)`).
- **Welcome** (`App/WelcomeWindow`, code-only, Fluent): once, on the first start of a *fresh install*
  (`SettingsStore.IsNew` = no settings.json before this run); upgrades just set `General.WelcomeShown`. Tips (hover,
  hotkey from `App.HotkeyName` in bold, tray), an AI grammar offer while it's off, Open Settings / Get started.
  `Redline.exe --welcome` always shows it (startup-only argument, like --settings/--diagnostics).
- **Tray "Don't check in <app>"**: opening the menu moves focus to the taskbar, so it names the app Redline *last
  attached to* (`App._lastApp`, from `SurfaceChanged`; display name = exe FileDescription, else process name —
  `Core/Settings/AppExclusion`); hidden when there's none or it's already excluded. Adds "<name>.exe" to
  `Applications.Excluded`, balloon "click to undo" opens Settings > Apps. Any change to the exclusion list calls
  `SurfaceTracker.Reevaluate()` (detach + re-check focus): an attached surface skips the security check otherwise.
  Live-checked 2026-10-04 (11/11, scratch script): welcome + AI offer, exclusion saved, form no longer attached.
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
- Was flaky (now retried via `UiaTestHelpers.FromHandle`, also hit on CI): `UiaIntegrationTests.GenericAdapter_ReadsText_Caret_AndGeometry` sometimes failed in
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

### Phase 5 — public repo, auto-update, notices (done)
- Public repo: https://github.com/rleonard55/Redline (MIT). History was rewritten (2026-10-04) to the noreply
  author `8695387+rleonard55@users.noreply.github.com`, which is set as this repo's `user.email`; never commit
  with the Gmail address. Hashes above are post-rewrite.
- Auto-update (`Core/Updates/UpdateCatalog` = parsing/version logic, tested; `App/Updates/UpdateService`):
  GET api.github.com/repos/rleonard55/Redline/releases/latest 1 min after start, then every 24 h
  (`General.CheckForUpdates`, default on; Settings > About has Check now / Install). 404 = no releases = up to date.
  Installed copies download `Redline-X.Y.Z-x64.msi` to `%LOCALAPPDATA%\Redline\updates`, verify size + SHA-256
  against GitHub's asset `digest` (no digest = refuse), then tray item + one notification per version; install
  = `msiexec /i … /passive` (MSI exits us via --exit and relaunches). Build-folder copies only link to the release.
- Releases: bump `<Version>` in Directory.Build.props, commit, push, then `installer/release.ps1` (clean+pushed
  main, tag vX.Y.Z, build, `gh release create` with MSI + notices, verifies the uploaded digest). Ask the user
  before publishing a release — it is public and every installed copy will offer it.
- Third-party notices: `installer/make-notices.ps1` (run by build.ps1) writes THIRD-PARTY-NOTICES.txt into the
  publish folder: Redline LICENSE, cargo-about output for all crates in harper_ffi.dll (`native/harper-ffi/about.toml`
  + `about.hbs`; 4 MPL-2.0 crates get crates.io source links), .NET runtime + Windows Desktop license/notices
  from the runtime packs named in Redline.deps.json. Needs `cargo install cargo-about --locked --features cli`.
- Version 0.6.0 is the first public release: the user installed the 0.5.2 test MSI, so anything lower can't upgrade it.
- Live e2e for Settings/tray: `tools/manual-tests/scripts/redline_settings_e2e.ps1` (pause via checkbox and tray
  menu, hotkey capture with real keys + Reset, Run key on/off, diagnostics mode, About check). Needs no Redline
  running; starts the build-folder exe with `--settings`. **11/11 passed 2026-10-04** (incl. tray Pause via the
  real tray menu in the hidden-icons overflow). Keep scripts ASCII-only: PowerShell 5.1 reads BOM-less .ps1 as
  ANSI, so build non-ASCII strings with `[char]0x2014` etc. Win11 tray buttons are named "<app> <tooltip>".

### Light/dark theme (done)
- Settings + Diagnostics windows: WPF Fluent theme, `ThemeMode = ThemeMode.System` set **per window** in the ctor
  before `InitializeComponent` (experimental in .NET 9: `#pragma warning disable WPF0001`). Not app-wide, so the
  overlay/pill/popup windows are untouched. Follows Windows live (WM_SETTINGCHANGE "ImmersiveColorSet").
- Gotchas: an implicit style in Window.Resources with `BasedOn="{StaticResource {x:Type CheckBox}}"` resolves to
  the *Aero* style (Fluent isn't merged yet at parse time) — don't add implicit control styles; set margins locally.
  Fluent tab headers are wide and wrapped into rows, so Settings uses `TabStripPlacement="Left"`. Hint text uses
  `{DynamicResource TextFillColorSecondaryBrush}`.
- Flyouts: `Annotations/SystemTheme` reads HKCU `...\Themes\Personalize\AppsUseLightTheme`; `FlyoutPalette.Light/Dark`
  (frozen brushes). `SuggestionPopup` sets `Flyout.*` DynamicResources per show; `HoverPill.ApplyPalette` per show.
- Tray menu (WinForms `ContextMenuStrip`): `TrayIcon/TrayMenuRenderer` (light/dark professional renderer, own
  tick/separator/hover drawing, DWM rounded corners) applied in the menu's `Opening` from `SystemTheme.IsDark`.
  Message boxes use `App/MessageDialog` (Fluent, follows the theme) instead of the always-light Win32 `MessageBox`;
  don't add new `MessageBox.Show` calls. Tray balloons are drawn by Windows.
- Verified live 2026-10-04 (dark and light, incl. switching with Settings open; hover e2e passes in both; tray menu
  checked with a scratch harness that builds the real `TrayIconHost`).
- Diagnostics window: same per-window Fluent theme; the log ListBox gets a dense item style built in code on Loaded
  (`FindResource(typeof(ListBoxItem))` as BasedOn — Fluent items are ~40 px tall). Checked light + dark with a
  harness that invokes `App.ConfigureServices` by reflection and captures via `PrintWindow(PW_RENDERFULLCONTENT)`
  (no activation, works while covered — a SetForegroundWindow capture grabbed VS Code instead).
  `tools/manual-tests/scripts/shot_window.ps1` captures one window (DWM frame bounds, CAPTUREBLT).

### Phase 6 — AI grammar with GRMR-V3-G1B (done, opt-in)
- Model: qingy2024/GRMR-V3-G1B-GGUF **Q4_K_M** (806,056,704 bytes), URL pinned to commit 7d2aa920…, SHA-256
  e01b82bf… (`Analysis.Grmr/GrmrModelStore.cs` `GrmrModel`). Prompt (from tokenizer_config.json):
  `<start_of_turn>text\n{text}<end_of_turn>\n<start_of_turn>corrected\n`, BOS added by the tokenizer, stop at
  `<end_of_turn>`, greedy (temp 0). Prototype: Q8_0 gave identical answers and was ~30% slower; ~0.4–1 s/sentence
  with 4 threads; no changes to correct sentences; misses "Their going", "Me and him".
- Runtime: LLamaSharp **0.25.0** + Backend.Cpu (0.26+ needs Microsoft.Extensions 10.x). The backend's native assets
  are excluded (`ExcludeAssets="native"`) and the win-x64 tree is copied by hand in the csproj: a self-contained
  RID publish otherwise flattens avx/avx2/avx512/noavx into one folder (NETSDK1152).
- **GPU (Vulkan)**: also ships `LLamaSharp.Backend.Vulkan.Windows` 0.25.0 (`native/vulkan/`, ~26 MB; same hand copy;
  its llama.dll still loads `ggml-cpu.dll` from the avx folders). One build for NVIDIA/AMD/Intel via the driver's
  vulkan-1.dll; LLamaSharp detects Vulkan by running `vulkaninfo --summary` (installed with GPU drivers), else CPU.
  Setting `Writing.AiGrammarUseGpu` (default on). `LlamaSentenceCorrector.Create`: the native library is chosen
  **once per process** (Vulkan only if the GPU was wanted at first load -> `GpuNeedsRestart`); GPU = all layers,
  `SplitMode.None`/`MainGpu 0` (only on the GPU: the CPU runtime has no devices and rejects main_gpu 0); a failed
  GPU load falls back to CPU until restart. Toggling the setting unloads the model; it reloads on the next sentence.
  `GpuGuard` (models folder): `gpu-trial` marker around the first GPU load+sentence of a session (a driver crash
  kills the process with nothing to catch); found at startup -> `gpu-blocked` (CPU only) + tray notice, until the
  user turns the setting off and on. Device name parsed from llama.cpp's "using device ... (name) - N MiB free" log.
  Measured on Intel Iris Xe (2026-10-04): same latency as CPU (~1.2-1.5 s/sentence, identical output) but ~9x less
  CPU time (4.5 s vs 41.5 s over 8 sentences); discrete GPUs should also be much faster (not measured here).
  Real-model test: `REDLINE_GRMR_GPU=0` forces the CPU.
- `GrmrAnalyzer` (supplementary `ITextAnalyzer`): never waits for the model. `AnalyzeAsync` splits sentences
  (`Core/Text/SentenceSplitter`, <= 500 chars), returns cached results, and replaces the work queue with uncached
  sentences (new/edited first, max 60). One background worker corrects them; when a sentence gets edits it raises
  `ResultsReady` (throttled 1.5 s) -> `AnalysisPipeline.Refresh()` (re-runs the latest snapshot in full, bypassing
  the unchanged-text shortcut). Model loads lazily, unloads after 10 idle minutes or when disabled; a load failure
  sets `Failed` until restart. LRU cache of 4000 sentences.
- `Core/Text/RewriteDiff`: token LCS -> word-level edits; drops whitespace-only edits and an added final period;
  insertions attach to the neighbouring word; rejects rewrites (> 6 edits, > 50% of word chars changed, length
  ratio outside 0.6–1.6). Issues: Analyzer "GRMR", RuleId "GRMR:Correction", Grammar/Punctuation category.
- Pipeline: `ITextAnalyzer.IsSupplementary` — the pipeline tags those issues (`TextIssue.Supplementary`) and keeps
  them all; `IssueCacheManager.Filter` drops a supplementary issue only if it overlaps a primary issue that is still
  *visible* after user filters (so "tset" keeps the spelling squiggle, but an ignored/hidden primary doesn't
  suppress the model's).
- Setting `Writing.AiGrammar` (default off). `IssueCacheManager` hides GRMR issues while it's off. Turning it on
  starts the download (`App.ApplyAiGrammar`); at startup a missing model is never fetched silently. Settings >
  Writing shows Download/Cancel/Remove + progress. Downloads resume from `.partial` (Range), 60 s stall timeout.
- Tests: Core `GrammarModelTests.cs`, Analysis `GrmrTests.cs` (fake corrector + fake HTTP server).
  Real model: `REDLINE_GRMR_MODEL=<path to gguf> dotnet test tests/Redline.Analysis.Tests --filter Integration`.
  Live: `tools/manual-tests/scripts/redline_grmr_e2e.ps1` (needs the model + AiGrammar on; waits for the model's
  underline on "go", applies "goes" and "was"->"were" via the pill). **PASS 2026-10-04**: Harper took "go" (the
  model's overlapping edit was suppressed as designed), the model's "was"->"were" applied and verified; the model
  also flagged "repot" -> "report", which spelling can't (real word). Model download via Settings: 23 s, hash OK.
- Found while testing: underlines were hidden on a TopMost target window (overlay stayed in the normal band).
  **Fixed** in `OverlayWindow.InsertAfterFor` (topmost target -> topmost overlay directly above it; drops out of the
  topmost band again for normal targets); covered by `Redline.Annotations.Tests`.
- Notices: `installer/licenses/*.txt` (LLamaSharp, llama.cpp, CommunityToolkit, dotnet/extensions; fetched from
  upstream — the NuGet packages only carry a license expression) + a section on the model's licenses.
  MSI grew to ~65 MB (local 0.7.0 build 2026-10-04).

### Paragraph / sentence / selection fixes (2026-10-05, live-tested)
- Owner's choices: **always preview**, the model's whole-sentence versions are included (correction only, not 6.1
  rewriting), the selection uses the **same hotkey**, and the **paragraph** is the default scope (Sentence/Paragraph toggle).
- `Core/Corrections/CompositeFix` (pure, `CompositeFixTests`): visible issues entirely inside the scope -> first
  suggestion each, non-overlapping (primary over supplementary, then earlier/shorter), deletions pre-expanded.
  A capitalized spelling word mid-sentence is offered unticked (likely a name). `Combine` = ticked edits, except a
  ticked model version replaces every edit in its sentence. `Preview` = Same/Removed/Inserted runs, split only at
  word boundaries ("go" -> "goes" shows the whole word).
- `ReplacementEngine.ApplyBatchAsync(adapter, version, edits)`: snapshot version checked **once**, then back to front;
  before each edit the live text must read as the last verified edit left it, and each edit goes through the normal
  focus/select/type/verify/undo path (`ApplyEditAsync`, shared with `ApplyAsync`). A failed edit is undone and the
  rest skipped; applied ones stay (each verified) -> "Applied 2 of 4 changes. ...". One Ctrl+Z per edit to undo.
- GRMR: the cache keeps the strict edits (underlines) **and** `RewriteDiff.ComputeLoose` (no edit-count/fraction
  limits, length ratio 0.5-2.0, but it rejects answers that drop > 1 word or > 15% of
  the words: live, the model turned "This is an tset of the new feature." into "This is a new feature."). `GetSentenceAlternativesAsync(text, scope, wait)` puts uncached sentences in a
  priority queue ahead of the analysis queue and waits (popup uses 10 s) - never shown as underlines.
- UI: `SuggestionPopup` has "Fix this paragraph (N changes)..." (F) when the paragraph has >= 2 edits or the model
  is available -> `FixPopup` (preview, checkbox per change, 1-9 toggle, S/P scope, Enter applies; model versions
  arrive later via `SetAlternatives`, only those that differ from the individual fixes). Hotkey with a non-empty
  selection (`ITextSurfaceAdapter.GetSelectionAsync`) -> `FixPopup` for the selection. Placement/palette shared
  in `Corrections/FlyoutWindow`.
- Live e2e: `tools/manual-tests/scripts/redline_fix_e2e.ps1` (throwaway WinForms form; stops the installed Redline
  with --exit and restarts it; hotkey -> F -> Enter for the paragraph, then a selection -> hotkey -> Enter).
  **PASS 2026-10-05** (3 of 3 and 2 of 2 edits, SelectAndType, other paragraph untouched, form stays in front).
  A script started from the background can't take the foreground: `ForceFront` (AttachThreadInput) fixes that.
  Interactive engine tests incl. 3 batch tests pass; `SelectAndPaste_AppliesAndRestoresTheClipboard` failed in its
  own `Clipboard.SetText` (CLIPBRD_E_CANT_OPEN, another process held the clipboard) - unrelated.

### Paragraph gutter pill (2026-10-05, live-tested)
- A thin vertical bar left of every visible paragraph with >= 2 fixes (`OverlayManager.GutterMinChanges`; one fix
  is the hover pill's job); click = `FixPopup` for that paragraph (`CorrectionController.ShowParagraphFixAsync`,
  anchored at the paragraph's extent, Paragraph scope first). Setting `General.ParagraphGutter` (default on,
  Settings > General).
- `Core/Geometry/GutterLayout` (pure, tested): 4 px bar, 6 px gap, 12 px clickable strip (alpha-1 background:
  layered windows pass clicks through fully transparent pixels), as tall as the visible lines (min 14 px), and the
  bar stays **inside** the surface (text at the control's edge -> flush with it, not outside the window).
- `OverlayManager` measures the paragraphs in the **same** `GetBoundsAsync` call as the squiggles (no extra round
  trip, only while the target is in front) and hides the pills with the squiggles (scroll, move, other app).
  `GutterPill` (Annotations): WS_EX_NOACTIVATE + MA_NOACTIVATE like the hover pill; z-order = directly below the
  overlay (`OverlayWindow.Handle`), i.e. just above the target, so the target's menus still cover it. Max 30 pills.
- Live e2e: `tools/manual-tests/scripts/redline_gutter_e2e.ps1 [-Indent 40]`: 2 pills (not on the 1-issue
  paragraph), click -> popup -> Enter -> 3 of 3 applied to that paragraph only, its pill goes away. **PASS
  2026-10-05** at the window edge and indented. Once (indented) the popup was dismissed before Enter ("0 edits
  chosen" = it lost activation); the rerun passed - watch for it. Don't run these while the user is typing: a run
  during the user's ChatGPT session never attached to the form (Redline stayed on ChatGPT).
- **Sticky pills** (OverlayManager `_sticky`, `ShiftAnchor`): paragraphs that had a pill at the last analysis keep
  it, at their start offset shifted across edits, until fresh analysis says otherwise (typing drops the edited
  word's issue and used to blink the pill out until re-analysis). Clicking one just before analysis lands waits
  for it (`ShowParagraphFixAsync` uses `WaitForCurrentIssuesAsync`).
- **Latency (2026-10-05):** default analysis delay 300 -> **150 ms** (`GeneralSettings.DefaultDelayMs`); settings
  files got `revision` (`RedlineSettings.Migrated`, CurrentRevision 1): an older file still at 300 moves to 150
  once, a chosen value is kept. `TextChangeWatcher.FastPoll` 1 s -> 500 ms (apps without UIA change events).
  Typical keystroke-pause -> pill ~0.2-0.35 s (debounce + ~6-80 ms analysis + ~5 ms layout).
- **Fixed (pre-existing):** squiggles could stay hidden for good after a fix: the anchor check had flagged
  "scrolling", then the edit/new issues cleared `_anchor`, so `CheckAnchor` returned early and layout skipped
  itself while scrolling. Now a scrolling state without an anchor re-lays out. Found by
  `redline_gutter_e2e.ps1`, which now samples the pill count for 3 s after the fix (6/6 steady after the fix).

### Phase gaps closed (2026-10-04, after the plan review)
- **Overlay on DPI change:** `OverlayWindow.ShowAt` re-applies its exact rectangle if `WM_DPICHANGED` (sent during
  `SetWindowPos` onto a monitor with another scale) made WPF resize it. Not verifiable here (one monitor).
- **125% checked live** (`tools/manual-tests/scripts/scale_check.ps1`, one scaling step up, always restores):
  DPI-aware targets correct. DPI-virtualized targets were wrong (UIA's Win32 proxy: physical client origin +
  unscaled offsets) — **fixed** in `GenericUiaAdapter.VirtualizationFix` (Win32/WinForm framework + native hwnd,
  `GetDpiForWindow` != monitor DPI -> `Core/Geometry/DpiVirtualization.FromClientOrigin`); verified at 125%
  (overlay shot + hover e2e in a virtualized window pass). The scaling API (`SPI_GET/SETLOGICALDPIOVERRIDE`) takes ONE int = steps relative to the *recommended*
  scale (the user's display recommends 150%; 100% = -2). Reading it as a struct once set the display to 150% for
  ~2 min — the script now checks the resulting DPI and restores in `finally`. Ask before running it.
- **Report a problem** (Settings > Compatibility): `Core/Diagnostics/CompatibilityIssue` builds a pre-filled public
  GitHub new-issue URL (title, user prompt, that entry's JSON, label `compatibility`); opened in the browser, the
  user reviews and submits. Automatic upload (telemetry option B) is **not wanted for now** (2026-10-04).
- **Perf stats (5.1):** `PerfCounters.Counts/Rates` (per-minute rates per counter) + `ResourceUsage` (private/working
  set/managed MB, handles, threads): in the 5-min diagnostics-mode summary and the Diagnostics "Timings" line.
- **Compatibility status (5.1, Settings > Apps):** `Core/Diagnostics/CompatibilityLog` — local per-app record keyed by
  process + control type/class + framework: target app version, patterns, reads/empty reads, issues placed/not placed
  per overlay pass, fixes applied/methods/problems (Redline's fixed messages), blocked reasons. No text, titles or
  URLs. `%LOCALAPPDATA%\Redline\compatibility.json`, 30 days, 200 entries, saved every minute when dirty + at exit.
  Fed by `SurfaceTracker.SurfaceChanged/SnapshotChanged/SurfaceBlocked` (new event), `OverlayManager` (layout) and
  `ReplacementEngine` (outcome). Settings > **Compatibility** tab (status per app, details in the row tooltip,
  *View report* = exact JSON in `ReportWindow`, *Clear*). This is step 1 of `docs/telemetry.md` (proposal; nothing
  is sent — upload options and open questions are there for the user to decide).
- **Testing strategy:** `tests/Redline.Annotations.Tests` (squiggle render via RenderTargetBitmap, overlay z-order
  incl. topmost targets with off-screen non-activating windows, palettes); `.github/workflows/ci.yml` (windows-latest:
  cargo build/test harper-ffi, dotnet build/test Release, uploads TRX). CI not yet run on GitHub (needs a push).
- UI harnesses for checking windows without stopping the user's Redline live in the session scratchpad only: they
  invoke `App.ConfigureServices` by reflection, construct the real window, and capture with `PrintWindow(.., 2)`.

### Phase 5 — next steps, in order
1. Phase 5 wrap-up (it is functionally complete), then Phase 6 (optional AI) if the user wants it.
   Updater verified live 2026-10-04: installed 0.5.9 test build -> Settings > About > Check now found v0.6.0,
   downloaded + digest matched, Install closed 0.5.9 and 0.6.0 relaunched in ~10 s. The user now runs the
   installed 0.6.0 (start with Windows on). Future releases: bump Version, commit, push, release.ps1 (ask first).

Deferred (documented in docs/compatibility.md): VS Code editor support (needs a VS Code extension);
multi-monitor / non-100% DPI is implemented but untested (user has one 100% monitor). The user does **not** want
Phase 6 AI rewriting for now (2026-10-04).

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
- `src/Redline.Analysis.Grmr` — `GrmrAnalyzer`, `LlamaSentenceCorrector` (LLamaSharp), `GrmrModelStore`, `GrmrPrompt`.
- `src/Redline.Annotations` — `OverlayWindow`, `SquiggleLayer`, `OverlayManager`, `HoverPill`, `SystemTheme`.
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
