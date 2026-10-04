# Compatibility telemetry — proposal

**Status:** steps 1 and 2 below are built (local record; "Report a problem" opens a pre-filled public GitHub
issue the user reviews and submits). Automatic upload (step 3) is not planned for now.

## Goal

Find out which apps and text fields Redline doesn't work in, with enough detail to reproduce and
retest the problem — without ever learning what anyone typed.

## Principles

1. **Opt-in.** Off by default; turning it on is an explicit choice in Settings, never a pre-ticked box
   or a nag.
2. **What you see is what is sent.** The payload is the exact JSON shown by Settings › Compatibility ›
   *View report*, byte for byte. No extra fields are added in transit.
3. **No content, ever.** No document text, no window titles, no web addresses, no file paths, no user
   or machine names. Only app identity, field identity and counts.
4. **Local first.** The record is useful on its own (Settings shows per-app status); sharing it is a
   separate, later step.

## What is recorded (built: `Core/Diagnostics/CompatibilityLog`)

Stored in `%LOCALAPPDATA%\Redline\compatibility.json`, kept 30 days, at most 200 entries. One entry per
app + kind of text field:

| Field | Example | Why it's needed to retest |
|---|---|---|
| `process`, `appVersion` | `ms-teams.exe`, `25255.703.3889.2480` | Same app, same build |
| `controlType`, `className`, `framework` | `Edit`, `ck-editor__editable`, `Chrome` | Which field inside the app (chat box vs. title vs. search) |
| `patterns` | `Text`, `Value` | What UI Automation offers there |
| `attaches`, `textReads`, `emptyReads` | 12, 340, 0 | Did Redline read the field at all ("reads as empty" = like VS Code) |
| `layoutPasses`, `issuesPlaced`, `issuesNotPlaced` | 50, 120, 3 | Could underlines be positioned |
| `fixesApplied`, `fixMethods`, `fixProblems` | 4, `SelectAndType: 4`, `Reverted: The result didn't match…: 1` | Do corrections work, and which step fails (fixed Redline messages, never text) |
| `blocked` | `Password field: 2` | Fields Redline deliberately skipped |
| report header | Redline `0.7.0`, Windows `10.0.26200.0`, date | Reproduce on the same versions |

`className` comes from the app's own UI code (e.g. a web editor's CSS class). It identifies the
editor component, not the user; it is shown in the report so anyone can check.

Possibly worth adding before any upload: display count and scale factors (most geometry bugs depend on
DPI), and the browser site **domain** for web apps (very useful — "Outlook on the web" vs. "Gmail" —
but more sensitive; it would need its own separate opt-in).

## Ways to share it

| Option | How | Effort | Privacy | Tells us |
|---|---|---|---|---|
| **A. "Report a problem" button** | Per app row: opens a pre-filled GitHub issue (title + that entry's JSON) in the browser; the user reads it and decides whether to submit | ~½ day, no server | Best: manual, reviewed, nothing automatic. Public issue (the user sees that) | Problems people care enough to report |
| **B. Opt-in periodic upload** | Weekly HTTPS POST of the report to a minimal endpoint (e.g. an Azure Function or Cloudflare Worker writing JSON blobs); a "Sent" log shows each upload | 2–4 days + a small hosted service and a privacy notice | Good if the endpoint keeps no IPs; still needs a random, resettable install ID to count users rather than uploads | Coverage across all opted-in users, including silent failures |
| C. Analytics SDK (App Insights, Sentry…) | Vendor SDK | 1 day | Worst fit: collects more by default, harder to show "exactly this is sent" | — |

## Recommended way to decide

1. **Done:** local record + Settings › Compatibility + *View report* (ships in the next release).
2. **Next, cheap test:** add option A. Over a month or two after release, see whether reports
   arrive and whether they contain enough to reproduce. Same JSON, zero infrastructure, no privacy
   question beyond what the user sees and submits.
3. **Only if (2) shows demand or clear blind spots:** build option B, reusing the same payload, with a
   Settings toggle ("Share this report weekly"), a visible send log, and a short privacy notice in the
   README.

## Open questions for the owner

- Is a public GitHub issue acceptable for option A, or should it go by e-mail / a private form?
- For option B: who hosts the endpoint, and is a pseudonymous install ID acceptable (it makes the data
  pseudonymous personal data under GDPR, which means a privacy notice and a way to delete it)?
- Should the browser site domain ever be collected, even as a separate opt-in?
