# Task Graph — Fase 3: Integración al flujo de trabajo diario

**Source:** `roadmap-meeting-ai-assistant.md`, "Fase 3 — Integración al flujo de trabajo diario".
**Author:** Lead Software Architect analysis, 2026-08-07. Updated 2026-08-11,
2026-08-13 (T7 — startup diagnostics; corrects the T2 tray-icon diagnosis),
2026-08-14 (T8 — prompt catalog, attach transcript, vault save, rendered
Markdown preview), 2026-08-21 (T2.2 — tray-icon .ico fix + T2.1 implementado).
**Scope:** implementation steps to close Fase 3, plus the T8 prompt-catalog
branch (Fase 2/4 quality work brought forward). No source code included
below — this is the plan a developer executes against.

## Status

| Task | Status |
|---|---|
| T1 — Centralize recording state | ✅ DONE (validated 2026-08-11) |
| T1.5 — Revert unauthorized network exposure | ✅ DONE (validated 2026-08-11) |
| T2 — Tray icon + hide-to-tray | ✅ DONE (GUI-validated 2026-08-21; see T2.2) |
| T2.1 — Fix stale RecordPage state after external triggers | ✅ DONE (GUI-validated from tray and hotkey 2026-08-21). No longer blocks T3 |
| T3 — Global hotkey | ✅ DONE (implemented and GUI-validated 2026-08-21; default `Ctrl+Alt+F9`) |
| T4–T6 | ⬜ Not started |
| T7 — Startup diagnostics + config validation | ✅ DONE (built, run and verified 2026-08-13) |
| T8 — Prompt catalog after transcript | ✅ DONE (2026-08-14). Follow-ups same day: attach `.txt`, vault-path UX, rendered MD preview |

## Current-state findings (verified against code, not assumed)

- **Fase 3 step 3 (local HTTP endpoint) is already implemented**:
  `src/MeetingAssistant.Infrastructure/Api/LocalRecordingApiServer.cs` exposes
  `POST /recording/start` / `POST /recording/stop`, binds `http://localhost:{port}/`
  only, requires `X-Api-Token` header, double-checks `RemoteEndPoint` is loopback,
  and is started/stopped from `App.xaml.cs`. **No new work needed here** beyond
  what's listed in T4 (shared coordinator) — do not re-implement it.
- **MSIX packaging is already scaffolded**, not yet production-ready:
  `MeetingAssistant.App.csproj` has `EnableMsixTooling=true` and a
  `Package.appxmanifest` with `runFullTrust`. But `Identity/Publisher` is the
  placeholder `CN=AppPublisher` and there's no signing certificate — this is a
  packaging/signing task (T6), not a scaffolding task.
- **Blocking architectural gap found**: `App.xaml.cs` currently wires
  `window.Closed += (_, _) => apiServer.Stop();` — closing the main window today
  stops the API server. WinUI 3 unpackaged/packaged desktop apps also terminate
  the process by default when the last window closes. This directly conflicts
  with the Fase 3 goal ("iniciar/detener grabación sin abrir la ventana
  principal") — if closing the window kills the process, there is no process
  left for the tray icon to live in. **This must be fixed as part of T2**, not
  treated as a side effect.
- **State-sync gap found**: `RecordViewModel` is registered `AddTransient` and
  its `IsRecording`/`StatusMessage` are local fields initialized to
  `false`/`"Listo para grabar."`, never read from `IMeetingPipeline.IsRecording`.
  `IMeetingPipeline` itself is a shared `AddSingleton`. Once tray/hotkey/HTTP can
  all trigger start/stop independently of `RecordPage`, a fresh
  `RecordViewModel` (e.g. after re-navigating to the Grabar tab) will show stale
  state. **Must be fixed as part of T1**, before T2/T3 add more triggers.
- `HistoryPage` and `SettingsPage` are empty stubs (`InitializeComponent()`
  only). They are Fase 2 scope, not Fase 3. T5 (autostart) requires exactly one
  toggle control in Settings — call this out explicitly so the developer does
  not scope-creep into building the full Settings page.
- No tray/notification/startup-task package is referenced yet in
  `MeetingAssistant.App.csproj`. All three are net-new dependencies (T2–T5).

## Non-negotiable constraints carried over from AGENTS.md

- `MeetingAssistant.Core` must not gain any provider- or platform-specific
  reference. Everything in this task graph (tray, hotkey, toast, startup task,
  packaging) is Windows-shell-specific and belongs entirely in
  `MeetingAssistant.App` (or a new `App`-local folder) — never in `Core`, never
  in `Infrastructure` unless it's genuinely a swappable service abstraction
  (none of these are).
- No new `appsettings*.json` values may contain real paths/tokens; only
  `appsettings.example.json` gets placeholders, and it must be checked against
  `.gitignore` if any new file is introduced.
- Any new package version must be the actual latest-compatible version
  resolved at implementation time — do not guess or hardcode a version number
  in this document that hasn't been verified against NuGet.
- Before marking any task below done: build + run the real packaged app
  (`dotnet run --project src/MeetingAssistant.App`, Developer Mode required)
  and manually exercise the acceptance criteria. Do not mark done from reading
  the code alone.

## Dependency graph

```
T1 (centralize recording state) ── DONE
  └─> T1.5 (revert Cloudflare tunnel / all-interfaces bind) ── DONE
        └─> T2 (tray icon + hide-to-tray window behavior) ── DONE
              └─> T2.1 (fix stale RecordPage state) ── DONE
                    ├─> T3 (global hotkey)
                    └─> T4 (toast notification on report ready / on error)
  └─> T5 (optional autostart via StartupTask)
T2, T5 ─────────────────────────────────────────> T6 (MSIX signing + local install)

T7 (startup diagnostics) ── independent, depends on nothing above,
                                blocks nothing above (parallel branch)

T8 (prompt catalog after transcript) ── independent of T2.1–T6.
        Depends only on the Fase 1 extractor existing.
        Does not block T2.1/T3.
        Same-day follow-ups (all landed 2026-08-14):
          T8.1 attach existing .txt transcript
          T8.2 make vault save path visible + open in Explorer
          T8.3 tabbed report viewer (rendered MD / raw MD)
```

T1 is the prerequisite for everything else. T1.5 and T2.1 were not part of
the original plan — both are remediation tasks inserted after reviewing what
actually landed in the commits for the task they're attached to (see their
own sections for why). T3 and T4 both build on the tray icon's "app stays
alive without a visible window" behavior established in T2, and now also on
T2.1's fix so a third/fourth trigger source doesn't compound the same
stale-state problem. T6 is last because it validates the packaged identity
that T4 (toast) and T5 (startup task) both depend on at runtime.

---

## T1 — Centralize recording state and completion/error events

**Status: ✅ DONE** — validated 2026-08-11 against commit `c02804c`.

### Validation notes
- Implemented as `src/MeetingAssistant.App/Services/RecordingCoordinator.cs`:
  singleton, wraps `IMeetingPipeline`, exposes `IsRecording`/`IsProcessing`
  synchronously, and `StateChanged` / `RecordingCompleted` (carries
  `MeetingPipelineResult`) / `RecordingFailed` (carries the `Exception`)
  events — matches the spec below, plus adds its own `SemaphoreSlim`-based
  guard on top of `IMeetingPipeline`'s existing double-start guard (a
  reasonable addition, not required by the spec).
- `RecordViewModel` now takes `RecordingCoordinator` instead of
  `IMeetingPipeline` and seeds `IsRecording`/`IsProcessing` from it in its
  constructor — the stale-state acceptance criterion is met: a freshly
  constructed `RecordViewModel` reflects whatever state the coordinator (and
  therefore the shared `IMeetingPipeline` singleton) is actually in,
  regardless of which trigger caused it.
- `LocalRecordingApiServer` still depends on `IMeetingPipeline` directly, as
  the spec allowed — meaning HTTP-triggered start/stop does **not** raise
  `StateChanged`/`RecordingCompleted`/`RecordingFailed`. This is fine for T1
  itself (the acceptance criteria only require the *state* to read correctly
  on next construction, which it does, since `IsRecording` is a live
  passthrough, not cached). **T2 must account for this** — see its
  implementation notes below.
- `dotnet build MeetingAssistant.sln` succeeds; `MeetingAssistant.Core.csproj`
  still has zero package references (checked directly, not assumed).
- **Process note, not a code defect, flagged for awareness going forward:**
  this work landed bundled inside commit `c02804c`
  ("Wav chunking process and manually upload") together with unrelated
  Deepgram-chunking/manual-upload changes (`ITranscriptionClient`,
  `DeepgramTranscriptionClient`, `MeetingPipeline.ProcessAudioFileAsync`,
  `Harness/Program.cs`, a new "process existing file" flow in `RecordPage`).
  None of that is in scope for Fase 3 and none of it was reviewed against
  this task graph — it's out of scope for this review, called out here only
  so it isn't mistaken for T1/T2 work later.
  Separately, the commit literally titled **"Changes TASK 1"** (`91f0ac0`)
  does **not** contain this work at all — see T1.5.

### Original spec (for reference — already satisfied)
**Depends on:** none.
**Touches:** `src/MeetingAssistant.App` only (new file(s) + edits to
`App.xaml.cs`, `RecordViewModel.cs`).

### Problem
Three more trigger sources are about to be added (tray, hotkey, and the
existing HTTP endpoint already bypasses `RecordViewModel`). `RecordViewModel`
is `Transient` and owns UI-facing state (`IsRecording`, `StatusMessage`,
`LastSavedReportPath`, `LastTranscript`) that nothing else can read or react
to. Tray/hotkey need the same "is it recording right now" answer the UI shows,
and toast notifications (T4) need to know when a `StopRecordingAndProcessAsync`
call — triggered from *any* source — finishes or fails.

### Implementation
1. Introduce a new singleton, App-layer component (e.g.
   `Services/RecordingCoordinator`) that wraps the existing
   `IMeetingPipeline` singleton. It is the single place that calls
   `StartRecordingAsync` / `StopRecordingAndProcessAsync`.
2. It exposes:
   - Current state (`IsRecording`, `IsProcessing`) readable synchronously.
   - An event/callback raised on successful stop (carrying
     `MeetingPipelineResult`) and one raised on failure (carrying the
     exception), so subscribers (toast handler in T4, tray menu label) don't
     need to poll.
3. Register it in `App.xaml.cs`'s `ConfigureServices` as `AddSingleton`,
   ahead of `RecordViewModel`.
4. `RecordViewModel` stops calling `IMeetingPipeline` directly — it calls the
   coordinator instead, and initializes its own `IsRecording` from the
   coordinator's current state in its constructor (fixes the stale-state gap
   found above).
5. `LocalRecordingApiServer` keeps depending on `IMeetingPipeline` directly —
   no change required there since it doesn't hold UI state — **but** confirm
   in T2/T3 that both the API server and the coordinator ultimately serialize
   through the same singleton `IMeetingPipeline`, so `IMeetingPipeline`'s
   existing guard clauses (double-start / stop-without-start →
   `InvalidOperationException`) remain the single source of truth preventing
   two triggers from racing each other.

### Acceptance criteria
- Starting a recording from the RecordPage button, then navigating away and
  back to RecordPage, shows `IsRecording = true` and the correct button label
  (no stale "Grabar reunión" on a page that's actually mid-recording).
- Manually calling `POST /recording/start` via curl/Postman while RecordPage
  is open and then opening RecordPage fresh reflects `IsRecording = true`
  (proves the coordinator, not per-page local state, is authoritative).
- `dotnet build MeetingAssistant.sln` succeeds; no new `Core`/`Infrastructure`
  reference introduced.

---

## T1.5 — Revert unauthorized network exposure (Cloudflare Tunnel + all-interfaces bind)

**Status: ✅ DONE** — validated 2026-08-11 against commit `9183e0e`.

### Validation notes
- Confirmed via diff and `grep`: `LocalRecordingApiServer`'s prefix is back
  to `http://localhost:{port}/`, the class doc comment is restored (reworded
  but same meaning: "Solo escucha en localhost, nunca en 0.0.0.0 ni en todas
  las interfaces."), `CloudflareTunnelService.cs` is deleted,
  `CloudflareTunnel` is gone from `appsettings.example.json`, and
  `grep -ri "cloudflare\|cloudflared\|trycloudflare" src/` returns nothing.
  `dotnet build MeetingAssistant.sln` succeeds.
- **Not independently re-verified:** the LAN-connectivity acceptance
  criterion (connecting from a second device) — no second device available
  in this review pass. Confidence is high anyway because the code is a
  byte-for-byte revert to the configuration that was already running in
  production before `91f0ac0`, not new code.
- Note for awareness, not a code defect: a real (gitignored, untracked)
  `appsettings.json` on this machine may still have a `CloudflareTunnel`
  section from when the feature was briefly live — that's local state
  outside the repo, no repo action needed, but stop running `cloudflared`
  manually if that was ever started by hand.

### Original spec (for reference — already satisfied)
**Depends on:** T1 (done).
**Touches:** `src/MeetingAssistant.Infrastructure/Api/LocalRecordingApiServer.cs`,
`src/MeetingAssistant.App/App.xaml.cs`,
`src/MeetingAssistant.App/CloudflareTunnelService.cs` (delete),
`src/MeetingAssistant.App/appsettings.example.json`.

### Why this exists
Commit `91f0ac0` — the one literally titled **"Changes TASK 1"** — does not
implement T1 at all. It adds an unplanned `CloudflareTunnelService` that
launches `cloudflared tunnel --url http://localhost:{port}`, publishing this
app's recording API on a public `*.trycloudflare.com` URL, and it changes
`LocalRecordingApiServer`'s `HttpListener` prefix from
`http://localhost:{port}/` to `http://+:{port}/` (all interfaces) —
**unconditionally**, not gated behind `CloudflareTunnel:Enabled`. Neither
change is in this task graph, and the bind change directly contradicts
`AGENTS.md`'s non-negotiable rule: "Solo bind a localhost, nunca a 0.0.0.0."

The existing `RemoteEndPoint`-loopback check in `HandleRequestAsync` does
**not** defend against this the way the commit's own code comment claims:
`cloudflared` terminates the public connection locally and forwards it to
`http://localhost:{port}`, so every tunneled request already arrives at the
listener looking like a loopback connection. Once `CloudflareTunnel:Enabled`
is `true`, the only thing standing between an internet caller and an
endpoint that turns on this machine's microphone is a single static
`X-Api-Token` header — no rotation, no rate limiting, no access logging. The
`+` bind itself widens the attack surface for every user of this app whether
or not they ever touch the tunnel.

Confirmed with the user (2026-08-11): revert this now. If remote access to
the recording API is wanted later, it needs its own explicitly-planned task
with real security acceptance criteria — it doesn't get to ride in silently
inside another task's commit.

### Implementation
1. In `LocalRecordingApiServer.cs`: revert the listener prefix to
   `_listener.Prefixes.Add($"http://localhost:{port}/");` and restore the
   original doc comment ("Solo escucha en localhost, nunca a 0.0.0.0..."),
   removing the "+"-prefix justification block that replaced it.
2. Delete `src/MeetingAssistant.App/CloudflareTunnelService.cs` entirely.
3. In `App.xaml.cs`'s `OnLaunched`/`ConfigureServices`: remove the
   `CloudflareTunnelService` DI registration, the `tunnel.Start()` call and
   its surrounding `try/catch` (which exists only to unwind
   `apiServer.Start()` if `tunnel.Start()` throws — no longer needed), and
   the `tunnel.Stop()` call in the window-closed handler. Restore
   `window.Closed += (_, _) => apiServer.Stop();` to its original,
   single-line form. (T2 will edit this same line again for hide-to-tray —
   do T1.5 first so T2 isn't reverting half of this change.)
4. Remove the `"CloudflareTunnel"` section from `appsettings.example.json`.
5. Leave the unrelated `.sln` Visual Studio-version bump from commit
   `91f0ac0` alone — that part isn't part of this revert.
6. If a real (untracked, gitignored) local `appsettings.json` on this
   machine has `CloudflareTunnel:Enabled: true` or `cloudflared` is running
   as a background process, that's local state outside the repo — flag it to
   the user rather than trying to change it from here.

### Acceptance criteria
- `grep -ri "cloudflare\|cloudflared\|trycloudflare" src/` returns no matches.
- `LocalRecordingApiServer`'s listener prefix is exactly
  `http://localhost:{port}/`, matching pre-`91f0ac0` behavior.
- `dotnet build MeetingAssistant.sln` succeeds.
- From a second device on the same LAN, connecting to
  `http://<this-machine's-LAN-IP>:5757/recording/start` fails to connect
  (confirms the listener is no longer bound to non-loopback interfaces) —
  this is the regression test that matters most; don't skip it.
- `POST http://localhost:5757/recording/start` with a valid `X-Api-Token`
  from the same machine still succeeds (the legitimate local use case is
  unaffected).

---

## T2 — Tray icon with context menu + hide-to-tray window behavior

**Status: ✅ DONE, with a follow-up (T2.1)** — validated 2026-08-11 against
commit `9183e0e`.

### Validation notes
- `TrayIconService` (new, `H.NotifyIcon.WinUI` 2.4.1) correctly stays
  decoupled from app-lifecycle sequencing: it only raises
  `OpenMainWindowRequested`/`ExitRequested`; `App.xaml.cs` owns
  `apiServer.Stop()` and process exit, exactly as specced.
- Tray label refresh is correct: `menu.Opening` re-reads
  `coordinator.IsRecording` live (not cached) on every right-click, which is
  what makes it correct even after an HTTP- or hotkey-triggered start.
  Verified specifically: start via `POST /recording/start`, then open the
  tray context menu — reads "Detener grabación" correctly, since
  `RecordingCoordinator.IsRecording` passes straight through to
  `IMeetingPipeline.IsRecording`.
- Hide-to-tray uses `AppWindow.Closing` with `args.Cancel = true` +
  `AppWindow.Hide()`, gated by a `_exitRequestedFromTray` flag set in
  `MainWindow.BeginExitFromTray()` — the correct WinUI 3 pattern
  (`Window.Closed` can't be cancelled; `AppWindow.Closing` can).
  `apiServer.Stop()` no longer runs from any window-close path — only from
  `App.ExitApplicationAsync()`.
- Exit-while-recording: `ExitApplicationAsync()` checks
  `coordinator.IsRecording || coordinator.IsProcessing` and shows a
  `ContentDialog` ("Salir sin guardar" / "Cancelar", default button =
  Cancel) before discarding and exiting — matches the resolved decision in
  this task graph, plus a safety touch (safe default button) beyond what was
  specified.
- `dotnet build MeetingAssistant.sln` succeeds; no new `Core` reference.
- **Update 2026-08-12 — the recommended smoke test caught a real crash:**
  the user ran the packaged app and it crashed on every single launch.
  Windows Event Viewer (`Get-WinEvent`, `Application` log, source
  "Application Error") showed the exact same fault on all 3 attempts:
  `Exception code: 0xc000027b` (`STATUS_STOWED_EXCEPTION`) in
  `Microsoft.UI.Xaml.dll`, identical fault offset every time. This code
  means an unhandled managed exception escaped a WinRT-invoked callback on
  the UI thread — confirmed as a known WinUI 3 pattern via web search
  (microsoft/microsoft-ui-xaml#9793 and related issues describe the same
  `0xC000027B` signature for exactly this "exception escapes an event
  handler" case). Deterministic, every-launch reproduction pointed at
  unconditional startup code, and the only new unconditional startup code
  from T2 is `TrayIconService.AttachTo()`.
  - **Root cause (best-evidence diagnosis, not a debugger-confirmed stack
    trace — see caveat below):** `TrayIconService.AttachTo()` set
    `TaskbarIcon.IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico"))`.
    H.NotifyIcon converts `IconSource` to a native `HICON` synchronously
    inside `ForceCreate()`; a multi-frame `.ico` loaded through
    `BitmapImage` is not a reliably decodable source for that conversion.
  - **Fix applied directly** (this was a live crash blocking the user, not
    task-graph planning work): swapped the icon source to
    `Assets/Square44x44Logo.targetsize-24_altform-unplated.png` — the 24px
    unplated PNG Windows already generates for exactly this taskbar/tray
    use case, already present in the project's `Assets/` and already
    referenced as `Content` in the `.csproj`. Also wrapped
    `_trayIconService.AttachTo(_window)` in `App.xaml.cs` in a try/catch
    that logs the full exception to `startup-errors.log` (next to the exe)
    and lets the app continue without a tray icon rather than hard-crashing
    — the tray icon is a convenience, not a critical dependency, and this
    also means if the PNG fix is wrong, the *next* crash produces a real
    logged stack trace instead of an opaque native fault code.
    `dotnet build MeetingAssistant.sln` still succeeds (0 errors).
  - **Caveat — genuinely unverified:** this diagnosis was built from Event
    Viewer's native fault record, not a managed stack trace (no debugger
    was attached with "Common Language Runtime Exceptions" enabled at
    crash time — recommend enabling that in VS's Debug > Windows >
    Exception Settings going forward so any *next* crash gives the exact
    exception/line immediately instead of requiring this kind of
    reconstruction). The fix could not be re-run and confirmed from this
    session (no interactive GUI access). **User must confirm the app now
    launches without crashing before T2 is trusted as done**, and check
    `startup-errors.log` if it still doesn't.
- **Real gap found, spun into T2.1 below:** `MainWindow` is a singleton and
  `RecordPage` is navigated to exactly once, in `MainWindow`'s constructor.
  `ShowFromTray()` only un-hides that same window — it never re-navigates.
  `RecordViewModel` seeds its state from the coordinator in its constructor
  but never subscribes to `RecordingCoordinator.StateChanged` afterward. So:
  start a recording from the tray, then "Abrir ventana principal" —
  `RecordPage` shows the stale "Grabar reunión" / "Listo para grabar." label,
  because it's the same long-lived `RecordViewModel` instance from launch.
  Not a functional bug — `ToggleRecordingAsync` branches on the *live*
  `_recordingCoordinator.IsRecording`, not the stale field, so clicking the
  mislabeled button still does the correct thing — but it fails the
  acceptance criterion below as literally written. Confirmed with the user
  (2026-08-11): fix now, before T3.

### Original spec (for reference)
**Depends on:** T1 (done), T1.5 (done).
**Touches:** `MeetingAssistant.App.csproj` (new package), `App.xaml.cs`,
`MainWindow.xaml.cs`, new file(s) under `src/MeetingAssistant.App/Services/`.

### Fixes the blocking gap
`App.xaml.cs` currently stops the API server and (by default WinUI 3
behavior) exits the process when `MainWindow.Closed` fires. This task changes
that: closing the window via the title-bar X must **hide** the window and
keep the process, the API server, and the pipeline singleton alive. Only an
explicit "Salir" (Exit) command from the tray context menu should call
`apiServer.Stop()`, dispose resources, and actually terminate the app
(`Application.Current.Exit()` or equivalent).

### Prerequisite check
Before starting, confirm T1.5 has actually landed: `App.xaml.cs` must contain
no reference to `CloudflareTunnelService`, and the window-closed handler must
be back to its single-line `apiServer.Stop()` form. If it isn't, stop and
finish T1.5 first — this task edits the same block.

### RecordingCoordinator surface you're building against (already exists, from T1)
`src/MeetingAssistant.App/Services/RecordingCoordinator.cs`, registered
`AddSingleton`:
- `bool IsRecording { get; }` — **live passthrough** to
  `IMeetingPipeline.IsRecording`, not cached. Safe to read fresh at any time.
- `bool IsProcessing { get; }` — cached flag, only true mid
  transcribe/extract/save.
- `event EventHandler? StateChanged` — fires on start/stop/processing
  transitions **only when the action went through the coordinator**.
- `event EventHandler<RecordingCompletedEventArgs>? RecordingCompleted` /
  `event EventHandler<RecordingFailedEventArgs>? RecordingFailed`.
- `Task StartRecordingAsync(CancellationToken = default)` /
  `Task<MeetingPipelineResult> StopRecordingAndProcessAsync(CancellationToken = default)`
  — both throw `InvalidOperationException` on misuse (already-recording,
  nothing-to-stop); catch these at the call site, don't let them propagate
  out of a tray menu click handler.

**Important gap to design around:** `LocalRecordingApiServer` calls
`IMeetingPipeline` directly, bypassing the coordinator (this was intentional
in T1). That means a recording started via `POST /recording/start` does
**not** raise `StateChanged`. Don't rely on `StateChanged` alone to keep the
tray menu label correct — always re-read `coordinator.IsRecording` at the
moment the context menu is about to be shown (its live passthrough makes
this cheap and always correct), and additionally subscribe to
`StateChanged` only to get a snappier label update for actions that *did* go
through the coordinator (tray itself, hotkey in T3, RecordPage button).

### Implementation
1. Add a tray-icon package to `MeetingAssistant.App.csproj`. Recommended:
   `H.NotifyIcon.WinUI` — the maintained community package for WinUI 3 tray
   icons (WinUI 3 ships no `NotifyIcon` equivalent). Resolve to the current
   latest version compatible with `net10.0-windows` / WindowsAppSDK 2.3.1 at
   implementation time; record the exact version actually pinned in the
   commit — don't guess it here.
2. Add a tray-sized icon asset if `Assets/AppIcon.ico` isn't appropriate at
   small sizes (H.NotifyIcon typically wants a multi-resolution `.ico`).
3. Create `src/MeetingAssistant.App/Services/TrayIconService.cs`:
   - Constructor takes `RecordingCoordinator coordinator`. Keep it decoupled
     from `apiServer`/`window` — it should not own app-lifecycle sequencing.
   - `void AttachTo(Window mainWindow)` (or equivalent) builds the
     `H.NotifyIcon` tray icon + context menu once the window exists.
   - Context menu items, minimum:
     - **Toggle item** — label computed each time the menu opens, from
       `coordinator.IsRecording` ("Grabar reunión" when `false`, "Detener
       grabación" when `true`). Click handler is `async void`; wrap the
       `StartRecordingAsync`/`StopRecordingAndProcessAsync` call in
       try/catch — on failure, surface it via the tray icon's balloon tip or
       tooltip for now (T4 replaces this with a proper toast). Never let an
       exception escape an `async void` event handler — an unhandled one
       crashes the whole app, not just the menu action.
     - **"Abrir ventana principal"** — raises an `OpenMainWindowRequested`
       event (`EventHandler`) that `App.xaml.cs` handles by calling
       `window.Activate()` / restoring visibility (mirrors whatever hide
       mechanism step 5 below implements).
     - **"Salir"** — raises an `ExitRequested` event (`EventHandler`).
       `TrayIconService` does not call `apiServer.Stop()` or exit the
       process itself; `App.xaml.cs` — which already owns those references —
       is the single place shutdown is sequenced.
   - Subscribe internally to `coordinator.StateChanged` to refresh the
     toggle item's label proactively (not required for correctness per the
     gap above, but avoids a label that only updates on next menu-open).
4. In `App.xaml.cs`:
   - Register `TrayIconService` as `AddSingleton`, after
     `RecordingCoordinator`.
   - In `OnLaunched`, after `apiServer.Start()` and after `window` is
     created/activated: resolve `TrayIconService`, call `AttachTo(window)`,
     subscribe `OpenMainWindowRequested` → show/activate `window`.
   - Subscribe `ExitRequested` to a new `ExitApplicationAsync()` local
     method that implements the **resolved exit-while-recording behavior**
     (see below), then calls `apiServer.Stop()`, disposes the tray icon, and
     calls `Application.Current.Exit()` (or `Environment.Exit(0)` if the
     WinUI shutdown path doesn't fully terminate a tray-resident app —
     verify which is needed at implementation time).
   - **Exit-while-recording behavior (architect's decision, not left open):**
     if `coordinator.IsRecording || coordinator.IsProcessing` when "Salir" is
     clicked, show a confirmation dialog ("Hay una grabación en curso. ¿Salir
     de todas formas? Se perderá la grabación." / Cancel / Salir sin
     guardar) rather than silently discarding audio *or* silently blocking
     the app in a multi-second stop-and-process call during shutdown.
     Choosing "Salir sin guardar" discards the in-progress capture (don't
     call `StopRecordingAndProcessAsync` on the way out — it can take many
     seconds for transcription+LLM extraction, which is a bad user
     experience mid-exit) and proceeds with shutdown. This is a reasonable
     default; flag it to the user as changeable, but implement *something*
     deterministic — don't leave it unhandled.
5. In `MainWindow.xaml.cs`: intercept the window close request. WinUI 3's
   `Window.Closed` cannot be cancelled — use `AppWindow.Closing` (available
   via `window.AppWindow`, part of `Microsoft.UI.Windowing`), which exposes
   an `AppWindowClosingEventArgs.Cancel` you can set to `true`. Set `Cancel =
   true` and hide the window (`AppWindow.Hide()` or set `Visible = false`,
   whichever the installed WindowsAppSDK version's `AppWindow` API supports —
   confirm at implementation time) instead of letting it close, **unless**
   the close was initiated by `TrayIconService`'s Exit path (use a bool flag
   `_exitRequestedFromTray` set immediately before that path calls
   `window.Close()`, checked inside the `Closing` handler to let a real exit
   proceed).
6. `App.xaml.cs`'s window-closed/closing wiring from T1.5 changes shape here:
   the simple `window.Closed += (_, _) => apiServer.Stop();` restored by
   T1.5 is no longer correct once closing hides instead of terminating —
   `apiServer.Stop()` must only run from the tray Exit path (step 4), not
   from any window-close event, since the window can now close (hide)
   without the app exiting.

### Acceptance criteria
- Clicking the window's X button hides the window; the process is still
  running (visible in Task Manager), the tray icon is still present, and
  `POST /recording/start` against the local API still succeeds while the
  window is hidden.
- Starting a recording from the tray menu, then opening the main window,
  shows `RecordPage` already reflecting `IsRecording = true` (via
  `RecordViewModel`'s T1 constructor seeding — no extra work needed here).
  **Correction from validation:** this does not hold once the window is
  hidden-and-reshown rather than freshly constructed — `RecordViewModel` is
  only seeded once, at launch. Constructor-seeding alone was sufficient for
  T1 (fresh construction per navigation) but not sufficient once T2 makes
  the window/page long-lived. See T2.1 immediately below.
- Starting a recording via `POST /recording/start` (HTTP, not tray), then
  right-clicking the tray icon, shows "Detener grabación" — not a stale
  "Grabar reunión" — proving the label is read live and doesn't depend on
  `StateChanged` alone (this is the regression test for the T1 gap called
  out above).
- Choosing "Salir" from the tray while nothing is recording stops the API
  server, removes the tray icon, and ends the process (nothing left in Task
  Manager).
- Choosing "Salir" while a recording or processing is in progress shows the
  confirmation dialog from step 4; confirming discards the capture and exits
  cleanly; cancelling leaves the app running and the recording untouched.
- `dotnet build MeetingAssistant.sln` succeeds; no new `Core` reference
  introduced.

---

## T2.1 — Fix stale RecordPage state after external triggers

**Status: 🔴 TODO — blocks T3.**
**Depends on:** T2 (done).
**Touches:** `src/MeetingAssistant.App/ViewModels/RecordViewModel.cs` only.

### Why
`MainWindow` and its `RecordPage` are constructed exactly once, at app
launch (`MainWindow`'s constructor calls `ContentFrame.Navigate(typeof(RecordPage))`
a single time; `TrayIconService.ShowFromTray()`-equivalent
(`MainWindow.ShowFromTray()`) only un-hides the existing window — it never
re-navigates). `RecordViewModel` reads `RecordingCoordinator.IsRecording`/
`IsProcessing` once, in its constructor, and never again. Once a recording
can be started by something other than this specific `RecordViewModel`
instance — the tray toggle, the HTTP endpoint, and (after T3) the global
hotkey all call `RecordingCoordinator`/`IMeetingPipeline` directly — the
already-alive `RecordPage` has no way to find out. The button label and
`StatusMessage` go stale until the user interacts with the page again.

This is UI-display-only: `ToggleRecordingAsync` correctly branches on the
live `_recordingCoordinator.IsRecording`, not the stale `IsRecording`
property, so no wrong action can be triggered — but a user watching a stale
"Grabar reunión" while a meeting is actually being recorded is a real,
user-visible correctness problem for a tool whose entire job is not missing
what happens in a meeting.

### Implementation
1. In `RecordViewModel`'s constructor, after seeding `IsRecording`/
   `IsProcessing`/`StatusMessage` from the coordinator (existing code, keep
   it — it's still correct for the first paint), subscribe to
   `_recordingCoordinator.StateChanged`, `RecordingCompleted`, and
   `RecordingFailed`.
2. On `StateChanged`: re-read `_recordingCoordinator.IsRecording` and
   `IsProcessing` into the observable properties, and recompute
   `StatusMessage` the same way the constructor does ("Grabando..." /
   "Procesando..." / "Listo para grabar.") — factor that three-way branch
   into a small private helper so the constructor and the event handler
   don't duplicate the string logic.
3. On `RecordingCompleted`: update `LastTranscript`/`LastSavedReportPath`/
   `StatusMessage` from the event's `Result`, mirroring what `StopAsync()`
   already does on success — this is what makes a tray/HTTP/hotkey-triggered
   completion show up correctly if the user opens the window afterward,
   not just a start.
4. On `RecordingFailed`: update `StatusMessage`/`ErrorDetails` from the
   event's `Exception`, mirroring the existing catch blocks.
5. These handlers fire on whatever thread raises the event — `RecordingCoordinator`
   invokes them synchronously from inside `StartRecordingAsync`/
   `StopRecordingAndProcessAsync`, which for the tray path runs on the UI
   thread already (WinUI event handlers), but the HTTP path
   (`LocalRecordingApiServer.HandleRequestAsync`) explicitly calls
   `IMeetingPipeline` directly, **not** through `RecordingCoordinator` — so
   HTTP-triggered actions still won't raise these events at all (this is the
   same T1-documented gap, not new). Marshal to the UI thread defensively
   anyway (e.g. `DispatcherQueue.TryEnqueue`) for whichever paths *do* raise
   the event through a non-UI thread, since `ObservableObject` property
   setters raising `PropertyChanged` off the UI thread will throw or corrupt
   binding in WinUI.
6. Since `RecordViewModel` is never explicitly disposed (it's `Transient`
   but in practice only one instance is ever alive for the lifetime of this
   single-window app), an unsubscribe isn't strictly required for a leak —
   but if `RecordViewModel` gains an `IDisposable`/cleanup path for any
   other reason later, unsubscribe there. Don't add one solely for this.
7. Do **not** attempt to fix the separate HTTP-bypasses-the-coordinator gap
   here — that's pre-existing, documented in T1's validation notes, and out
   of scope for this fix. If you want HTTP-triggered actions to also update
   a hidden `RecordPage` live, that requires routing `LocalRecordingApiServer`
   through `RecordingCoordinator` instead of `IMeetingPipeline` directly,
   which is a bigger architectural change than this task — raise it
   separately if it matters to you.

### Acceptance criteria
- Start a recording from the tray menu (window hidden). Click "Abrir ventana
  principal." `RecordPage` immediately shows "Detener grabación" and
  "Grabando..." — not stale text from launch.
- Stop that recording from the tray menu. Reopen the window (if closed) —
  `RecordPage` shows the saved report path and transcript, same as if the
  button on `RecordPage` itself had been clicked.
- Force a failure while recording started from the tray (e.g. temporarily
  break the LLM API key), stop it from the tray — reopening the window shows
  the error in `StatusMessage`/`ErrorDetails`, not a stale "Listo para
  grabar."
- Starting/stopping directly from the `RecordPage` button (not tray) still
  behaves exactly as before — no regression to the existing, already-correct
  path.
- `dotnet build MeetingAssistant.sln` succeeds.

---

## T2.2 — Tray icon .ico fix and T2.1 implementation (2026-08-21)

**Status: ✅ DONE — GUI-validated 2026-08-21.**
**Touches:** `Assets/TrayIcon.ico` (new), `MeetingAssistant.App.csproj`,
`Services/TrayIconService.cs`, `ViewModels/RecordViewModel.cs`.

### Tray icon

The failing path was reproduced headlessly instead of inferred: each candidate
asset was loaded with `new System.Drawing.Icon(stream)` — the exact constructor
at the bottom of the 2026-08-13 stack trace (`StreamExtensions.ToSmallIcon`).

| Asset | Result |
|---|---|
| `Square44x44Logo.targetsize-24_altform-unplated.png` (what shipped) | `ArgumentException: Argument 'picture' must be a picture that can be used as a Icon` — the reported failure, reproduced exactly |
| `AppIcon.ico` (6 frames, 370 KB) | loads fine |
| `TrayIcon.ico` (new, 1 frame, 4 KB) | loads fine |

Note the middle row: the multi-frame `AppIcon.ico` is *not* rejected by this
API, so the 2026-08-12 diagnosis ("the multi-frame `.ico` is what crashed the
app at launch") was never confirmed and remains unproven. What is now proven is
that the PNG cannot work. `TrayIcon.ico` was chosen over reverting to
`AppIcon.ico` because it is the asset the 2026-08-13 correction already
specified, it is 4 KB instead of 370 KB, and it removes the multi-frame
variable from a path that has already burned two debugging sessions.

`Assets/TrayIcon.ico` is generated from `Assets/Square44x44Logo.scale-200.png`
(88x88) downscaled to a single 32x32 frame written as a raw DIB/BMP payload
(BITMAPINFOHEADER with doubled height, 32-bit BGRA bottom-up XOR data, zeroed
AND mask) — deliberately not PNG-compressed, since PNG-inside-ICO is the other
thing `System.Drawing.Icon` handles badly.

The icon was subsequently confirmed visible in the notification area during an
interactive launch on 2026-08-21. No new icon exception was written to
`startup-errors.log` during that launch.

### T2.1 state sync

`RecordViewModel` now subscribes to `RecordingCoordinator.StateChanged`,
`RecordingCompleted` and `RecordingFailed`, marshalling every update through
`DispatcherQueue.TryEnqueue` (queue captured in the constructor with
`DispatcherQueue.GetForCurrentThread()`).

Two details worth knowing before touching this code again:

1. **`_localOperationDepth`.** The coordinator raises its events synchronously
   from inside the call, so a RecordPage-initiated operation would receive its
   own events and overwrite the specific message it had just set
   ("Transcribiendo...", "Reporte guardado en...") with a generic
   "Procesando..." / "Listo para grabar.". Every RecordPage-initiated
   coordinator call increments this counter in a `try`/`finally`; the handlers
   read it at *raise* time, before enqueuing, and skip. External triggers (tray,
   and later the hotkey) are unaffected — the counter is zero for them.
2. **The idle transition does not blank a result.** `StopRecordingAndProcessAsync`
   raises `RecordingCompleted` first and only then, from its `finally`, the idle
   `StateChanged`. The idle branch therefore resets `StatusMessage` only when it
   is still one of the transient strings, so the report path written by the
   completion handler survives.

Unchanged and still true: the HTTP endpoint calls `IMeetingPipeline` directly,
not the coordinator, so HTTP-triggered runs raise none of these events and will
still leave a hidden `RecordPage` stale. Same T1 gap, deliberately out of scope
(see T2.1 step 7).

### Verification actually performed

- `dotnet build MeetingAssistant.sln` — 0 errors, 1 pre-existing warning
  (`LocalRecordingApiServer._cts`, CS0649, unrelated to this change).
- The icon load matrix above, run for real against `System.Drawing`.
- `Assets\TrayIcon.ico` confirmed present in the generated
  `MeetingAssistant.App.build.appxrecipe`, i.e. it is part of the deployed
  package layout. (The `bin/**/AppX/Assets` folder still lacks it, but that
  folder is a stale 2026-08-12 layout and is not what deployment reads.)
- Interactive launch: icon visible; tray start changed the tray label and the
  open RecordPage to "Detener grabación" / "Grabando..."; tray stop processed
  the recording and generated the report; the user confirmed the tray
  capabilities and normal page flow worked.
- GUI testing exposed a separate bug in the original tray wiring: H.NotifyIcon's
  default WinUI `PopupMenu` invokes `ICommand`, but all three items used XAML
  `Click` handlers, so toggle/open/exit were inert. They now use `RelayCommand`
  / `AsyncRelayCommand`; caught recording failures are also persisted to
  `startup-errors.log` instead of being notification-only.
- **Regression introduced and fixed (2026-08-22):** that same rewrite dropped
  `menu.Opening += (_, _) => RefreshToggleLabel();`. Without it the tray label
  refreshes only from `RecordingCoordinator.StateChanged`, and the HTTP endpoint
  calls `IMeetingPipeline` directly without raising it — so a recording started
  with `POST /recording/start` left the tray reading "Grabar reunión", the exact
  case T2's validation notes had verified. Restoring `menu.Opening` alone is not
  enough, and understanding why matters: `ContextMenuMode` defaults to
  `PopupMenu`, so H.NotifyIcon builds a native menu and never shows the
  `MenuFlyout` as a XAML flyout — the same reason the `Click` handlers were
  inert. `TrayContextMenuOpen` exists only as a protected method in the WinUI
  port (it does not compile as an event), so the label refresh is now also wired
  to `TaskbarIcon.RightClickCommand`, which is public. `menu.Opening` is kept for
  the `SecondWindow`/`ActiveWindow` modes; `RefreshToggleLabel` is idempotent.
  **Unverified:** whether `RightClickCommand` runs before the native menu is
  built. If a right-click after an HTTP-triggered start still shows a stale
  label, that ordering is the thing to check first.

---

## T3 — Global hotkey to start/stop recording

**Status: ✅ DONE — implemented and GUI-validated 2026-08-21.**

**Depends on:** T2 (done), T2.1 (must land first — same reasoning as T1.5
blocking T2: get the state-sync story consistent before adding a third
trigger source).
**Touches:** new file(s) under `src/MeetingAssistant.App/Services/`,
`App.xaml.cs`/`MainWindow.xaml.cs` wiring.

### Implementation
1. No new NuGet package — WinUI 3 has no hotkey API, and this is a narrow
   enough surface (2–3 P/Invoke signatures: `RegisterHotKey`,
   `UnregisterHotKey`, handling `WM_HOTKEY`) that hand-written interop is more
   consistent with this repo's existing preference for minimal footprint
   (e.g. `HttpListener` over Kestrel for the same reason — see
   `LocalRecordingApiServer`'s doc comment).
2. Obtain the `MainWindow`'s `HWND` via
   `WinRT.Interop.WindowNative.GetWindowHandle(window)` (already available
   transitively through `Microsoft.WindowsAppSDK`).
3. Register the hotkey once at startup (after the window is created) against
   that `HWND`, and subclass the window procedure (or hook
   `Microsoft.UI.Xaml.Window`'s message pump — confirm the correct WinUI 3
   pattern for intercepting `WM_HOTKEY` at implementation time, since WinUI 3
   does not expose a raw `WndProc` override the way Win32/WinForms does) to
   react to `WM_HOTKEY` and call the same `RecordingCoordinator` toggle used
   by the tray menu and the RecordPage button.
4. The default hotkey combination is a product decision, not an architecture
   one — flag it as an open question for the user (see "Open questions"
   below) rather than assuming a value. Make it configurable via
   `appsettings.json` (`Hotkey:Modifiers`, `Hotkey:Key`) with a sensible
   fallback, following the same `IConfiguration` pattern already used for
   `Api:Port`/`Api:AuthToken`.
5. Unregister the hotkey on tray "Salir" / real process exit — leaked global
   hotkey registrations survive process crashes on some Windows versions, so
   this must be cleaned up deterministically, not left to finalization.

### Acceptance criteria
- Pressing the configured hotkey while the main window is hidden starts a
  recording; pressing it again stops and processes it, exactly as the tray
  menu item / RecordPage button would.
- Registering the same hotkey combination in another already-running
  application does not crash this app on startup — `RegisterHotKey` failure
  (hotkey already claimed) is caught and surfaced (tray tooltip, log, or
  toast from T4), not swallowed silently.
- No leaked hotkey registration after a clean "Salir" (verify: after exit,
  the same hotkey combination can be registered by another test process).

### Validation notes (2026-08-21)

- Added App-local `GlobalHotkeyService`; no new NuGet package. It uses
  `RegisterHotKey` / `UnregisterHotKey` and `SetWindowSubclass` /
  `RemoveWindowSubclass`, keeping the subclass delegate alive for the service
  lifetime. The HWND comes from `WindowNative.GetWindowHandle`.
- Configuration keys are `Hotkey:Modifiers` and `Hotkey:Key`. The fallback and
  example default are `Control+Alt` + `F9` (`Ctrl+Alt+F9`), selected by the user
  on 2026-08-21 after rejecting the earlier `Ctrl+Shift+R` choice because it
  conflicts with browser hard refresh.
- With the main window hidden, the user started and stopped a real recording
  with `Ctrl+Alt+F9`; RecordPage updated live and showed the resulting transcript
  and saved report path.
- Collision exercised for real: a separate Win32 probe registered
  `Ctrl+Alt+F9` first. The app still launched and remained responsive; the
  failure was surfaced through the tray and logged as Win32 error 1409 under
  `GlobalHotkeyService.Register`.
- Clean-exit cleanup exercised for real: after tray "Salir", the app process
  was gone and the separate probe immediately registered the same combination.
- `dotnet build MeetingAssistant.sln` completed with 0 errors. **Correction
  (2026-08-22):** the original note here claimed 0 warnings; a full build still
  emits the pre-existing `CS0649` for `LocalRecordingApiServer._cts`
  (`LocalRecordingApiServer.cs:28`), reconfirmed with a forced rebuild of
  `MeetingAssistant.Infrastructure`. It predates T3 and is unrelated to it — the
  0-warning reading came from an incremental build that skipped Infrastructure,
  reproduced here: a plain `dotnet build` after touching only App prints
  0 warnings, while `-t:Rebuild` prints the CS0649. Report solution build
  results from a rebuild, or name the project that was actually compiled.
- `MeetingAssistant.Core.csproj` was rechecked and still has zero package
  references.

---

## T4 — Toast notification when the report is ready (and on failure)

**Depends on:** T2 (must work while the window is hidden/tray-only — this is
why the roadmap's "Toast/InfoBar" choice must resolve to **Toast**, not
`InfoBar`: `InfoBar` only renders inside a visible page, which defeats the
purpose once T2 lets the app run window-hidden).
**Touches:** `src/MeetingAssistant.App/Services/` (or the T1 coordinator
file), `App.xaml.cs`, `Package.appxmanifest` (verify, likely no change
needed).

### Implementation
1. No new NuGet package: `Microsoft.Windows.AppNotifications` (the
   `AppNotificationManager` API) ships as part of `Microsoft.WindowsAppSDK`,
   already referenced at 2.3.1. Confirm at implementation time that this
   version actually includes a stable `AppNotificationManager` — if the build
   fails to resolve the namespace, that's a real signal to bump the
   WindowsAppSDK package version, not to fall back to a legacy toast library.
2. Call `AppNotificationManager.Default.Register()` once at app startup
   (`App.xaml.cs`) and `Unregister()` on real exit (tray "Salir" path from
   T2), mirroring how `apiServer.Start()`/`Stop()` are already bracketed.
3. Subscribe to the T1 `RecordingCoordinator`'s success/failure events:
   - Success: toast with the saved report path (and maybe a
     "abrir en Explorador" action button — nice-to-have, not required).
   - Failure: toast with the error message, since a failure that happens
     while the window is hidden currently has **no visible surface at all**
     (today, errors only reach `RecordViewModel.StatusMessage`, which no one
     sees if `RecordPage` isn't open). This closes a real gap, not just an
     enhancement.
4. Toast content must not leak sensitive data into notification history
   beyond what's already written to the Markdown report itself (saved report
   path is fine; don't embed the full transcript in the toast body).

### Acceptance criteria
- Trigger a recording via tray or hotkey, hide the window, stop the
  recording — a Windows toast appears with the saved report path, even
  though no app window is visible.
- Force a failure (e.g. temporarily invalid API key) and confirm a failure
  toast appears — this path currently has zero user-visible feedback when
  the window is closed, so this is the regression test that matters most.
- Toast notifications appear correctly attributed to the app (correct name/
  icon), which requires the app to be running with package identity —
  confirm this is true both under `dotnet run` (WinApp BuildTools debug
  registration) and after T6's real MSIX install.

---

## T5 — Optional autostart on Windows boot

**Depends on:** T1 (coordinator not strictly required, but keeps the toggle
consistent with everything else touching app lifecycle).
**Touches:** `Package.appxmanifest` (new `Extensions` entry), one new toggle
control in `SettingsPage.xaml`/`.xaml.cs` (minimal — do not build out the rest
of the Settings page beyond this control), new file under
`src/MeetingAssistant.App/Services/`.

### Implementation
1. Because the app is packaged (MSIX, per T6), the correct API is
   `Windows.ApplicationModel.StartupTask` — **not** a
   `HKCU\...\Run` registry key or Startup-folder shortcut, both of which are
   the wrong mechanism for a packaged app and don't get the proper Windows
   Settings > Startup Apps toggle/consent UX.
2. Declare the startup task in `Package.appxmanifest`: add the `uap5`
   namespace and a `<uap5:Extension Category="windows.startupTask">` entry
   with a `StartupTaskId` and `Enabled="false"` by default (per roadmap:
   "Autostart opcional" — must be opt-in, never silently enabled).
3. Add a single toggle switch to `SettingsPage` that calls
   `StartupTask.GetAsync(taskId)` then `RequestEnableAsync()` /
   `Disable()`, and reflects the returned `StartupTaskState` (handle
   `DisabledByUser` / `DisabledByPolicy` distinctly in the UI — these are
   states where the toggle must show as off *and explain why*, not just
   silently fail).
4. This only works meaningfully once the app has real package identity
   (T6) — during plain `dotnet run` debug registration it may behave
   differently. Note this explicitly to whoever tests it so a "doesn't work
   under `dotnet run`" result isn't mistaken for a bug.

### Acceptance criteria
- Toggling "Iniciar con Windows" on in Settings, then rebooting (or using
  `Task Manager > Startup Apps` to confirm registration without a full
  reboot), shows the app registered as a startup task.
- Toggling it off removes the registration.
- If Windows Settings' own "Startup apps" panel has this app disabled at the
  OS level, the in-app toggle reflects `DisabledByUser`/`DisabledByPolicy`
  rather than showing a misleading "on".

---

## T6 — MSIX signing and local persistent install

**Depends on:** T2, T5 (validates the packaged-identity assumptions both rely
on).
**Touches:** `Package.appxmanifest`, packaging/signing config (new files —
certificate, publish profile — must be checked against `.gitignore`; the
repo's `.gitignore` already excludes `AppPackages/`, `*.msix*`, `*.appx*`,
`*.pubxml`, so the *output* is already safe, but a `.pfx` certificate file if
generated in-repo is not currently excluded — verify and add if needed).

### Implementation
1. Replace the placeholder `Identity/Publisher` (`CN=AppPublisher`) in
   `Package.appxmanifest` with a real subject name for a self-signed
   certificate — this is a personal/independent tool (per roadmap header),
   so self-signed + sideload is the correct target, not Store submission.
2. Generate a self-signed code-signing certificate (Visual Studio's "Create
   App Packages" wizard can do this, or `New-SelfSignedCertificate` +
   `Set-AuthenticodeSignature` manually). Store the `.pfx` outside the repo
   or ensure it's gitignored — never commit a signing certificate.
3. Produce a local install via Visual Studio's "Package and Publish >
   Create App Packages" (sideload target) or `msbuild`
   `/p:AppxPackageSigningEnabled=true` equivalent, then `Add-AppxPackage`
   the resulting `.msix`.
4. Verify Developer Mode requirement (already documented in `AGENTS.md`) is
   sufficient for sideload install without additional certificate trust
   steps, or document the one-time "trust this certificate" step if needed.

### Acceptance criteria
- A freshly-installed (not `dotnet run`) copy of the app launches from the
  Start Menu, shows the tray icon, responds to the hotkey, serves the local
  HTTP endpoint, fires toast notifications, and honors the autostart toggle
  — i.e., T2–T5 all re-verified against the real packaged install, not just
  `dotnet run`.
- Uninstalling via Windows Settings > Apps cleanly removes the app (no
  orphaned startup task registration, no orphaned tray icon process).
- The signing certificate file is not present in `git status` / not tracked.

---

## T7 — Startup diagnostics, configuration validation, and the 2026-08-13 launch crash

**Status: ✅ DONE** — built, run and verified 2026-08-13. Unplanned work: this
started as a live crash blocking the user, not as task-graph planning.

### How it presented

The user reported that running the app showed "a message about an invalid
debugger installed" and then **opened a second Visual Studio instance**. That
framing is misleading and cost time on the previous attempt too — worth
recording so nobody re-diagnoses it as a broken IDE:

```
app throws unhandled  →  escapes to XAML framework  →  0xc000027b
                      →  Windows Error Reporting
                      →  AeDebug = vsjitdebugger.exe, Auto unset
                      →  JIT debugger dialog  →  "new instance of Visual Studio"
```

The second VS window is Windows' Just-In-Time debugger doing exactly what it is
configured to do. **The Visual Studio installation was never the problem.** Do
not "repair" VS, and do not disable JIT debugging — that only hides the next
crash.

### Root cause (confirmed, not inferred)

`appsettings.json` had drifted from `appsettings.example.json`: the example
documents an `Api` section (`Port`, `AuthToken`), the real local file had no
`Api` section at all. `LocalRecordingApiServer`'s **constructor** throws when
`Api:AuthToken` is missing — correctly, since that endpoint opens the
microphone. DI resolved it inside `App.OnLaunched`, nothing caught it, and it
became the `0xc000027b` above.

Note this is a *different* cause from the 2026-08-12 crash recorded under T2,
which was diagnosed as the tray icon. Both produced an identical native fault
signature, which is exactly why that signature is not sufficient evidence on
its own.

### Diagnostic evidence gathered (for reuse next time)

| Check | Command | Result at the time |
|---|---|---|
| Fault record | `Get-WinEvent` Application log, `Application Error` | `0xc000027b` in `Microsoft.UI.Xaml.dll` |
| Package registration | `Get-AppxPackage` | registered dev-mode, `Status: Ok` |
| Developer Mode | `HKLM:\...\AppModelUnlock` | enabled |
| Debug registration | `HKCU:\...\ActivatableClasses\Package\<PFN>\DebugInformation` | absent — ruled out a stale debugger hook |
| JIT config | `HKLM:\...\AeDebug` | `vsjitdebugger.exe`, `Auto` unset → prompts |

### What was implemented

1. **`Views/StartupErrorWindow.cs` (new)** — last-resort error window, built
   entirely in code with no XAML and no `InitializeComponent`. Deliberate: it
   must be able to report failures that happened *while* loading XAML or
   building the DI container, so it cannot depend on either.
2. **`App.xaml.cs`** — `RegisterGlobalExceptionHandlers()` runs first in the
   constructor (XAML `UnhandledException`, `AppDomain.CurrentDomain.UnhandledException`,
   `TaskScheduler.UnobservedTaskException`); `ConfigureServices()` can no longer
   throw out of the constructor (failure is stashed and reported from
   `OnLaunched`); `OnLaunched`'s body moved into `LaunchCore()` inside a
   try/catch. The error window is only shown while `_window is null` — once the
   main window is alive, a stray async failure is logged but does not interrupt
   the user.
3. **`Services/StartupConfigurationValidator.cs` (new)** — runs before anything
   is registered and reports **every** missing key at once instead of failing on
   the first. Provider-aware (`Llm:Provider` decides whether Gemini or
   AzureFoundry credentials are required); `AzureFoundry:ApiKey` is deliberately
   optional because Azure.Identity is a valid alternative. Uses the same
   placeholder rule as `App.ReadSetting` (a value starting with `<` counts as
   missing) so validating and reading cannot disagree.

### Log location — corrected, this is easy to get wrong

`startup-errors.log` was moved off `AppContext.BaseDirectory` (unwritable under
`WindowsApps` when packaged — which is why no log ever appeared despite repeated
crashes) to `LocalApplicationData`. **But for a packaged run Windows redirects
that into the package container:**

```
%LOCALAPPDATA%\Packages\962A0BC5-...__1z32rh13vfry6\LocalCache\Local\MeetingAssistant\startup-errors.log
```

Not `%LOCALAPPDATA%\MeetingAssistant\`. Looking in the unredirected path makes a
working log look like a broken one.

### Verification actually performed (not read-only)

- `dotnet build MeetingAssistant.sln` — 0 errors, 0 warnings.
- App launches, stays up, port 5757 listening, `POST /recording/stop` without a
  token returns **401**.
- Validator exercised for real by removing `Api:AuthToken` **and**
  `AzureFoundry:Deployment`, then running the packaged app — both were reported
  together in one message. Config restored and re-verified afterward.

### Correction to the T2 tray-icon diagnosis above

The 2026-08-12 fix (swap `Assets/AppIcon.ico` → `Assets/Square44x44Logo.targetsize-24_altform-unplated.png`,
`TrayIconService.cs:58`) **did not work, and made the failure harder to see.**
First real managed stack trace, captured 2026-08-13:

```
System.ArgumentException: Argument 'picture' must be a picture that can be used as a Icon.
   at System.Drawing.Icon.Initialize(...)
   at H.NotifyIcon.StreamExtensions.ToSmallIcon(Stream stream)
   at H.NotifyIcon.ImageExtensions.ToIconAsync(ImageSource, CancellationToken)
   at H.NotifyIcon.TaskbarIcon.<OnIconSourceChanged>d__163.MoveNext()
```

`ToSmallIcon` feeds the stream to `System.Drawing.Icon(Stream)`, which requires
an **ICO** stream. A PNG is *less* usable there than the original `.ico`, not
more. It now fails **asynchronously** (a `Task` continuation posted to the
dispatcher), so the `try/catch` around `AttachTo()` never sees it — which is why
it stayed invisible until the global handlers from T7 caught it.

**Consequence — a real usability trap, not cosmetic:** `MainWindow` handles
close with `args.Cancel = true; AppWindow.Hide()`. With no working tray icon,
closing the window leaves the process running and unreachable, still holding
port 5757 — so the *next* launch fails to bind with
`HttpListenerException (183): ... conflicts with an existing registration`.
This was hit twice during testing.

**Correct fix (not yet applied):** supply a small single-frame **BMP-encoded
`.ico`** (16×16/32×32) as a dedicated tray asset, added as `Content` in the
`.csproj`, and point `IconSource` at it. Do not use a PNG, and do not use the
multi-frame `AppIcon.ico`. Re-confirm by launching and checking the log is
clean.

### Related config changes made in the same session

- `Storage:VaultPath` in both `appsettings.json` files pointed at a directory
  that did not exist (4 missing segments). `MarkdownReportStorage.cs:34` calls
  `Directory.CreateDirectory`, so this would **not** have thrown — it would have
  silently created an empty tree and written reports somewhere that is not the
  Obsidian vault. At the user's request it now points at `local-vault/` in the
  repo root (added to `.gitignore`, since it holds meeting content). The
  original value is preserved in each file as `Storage:VaultPathOriginal` so it
  can be restored.
- `MeetingAssistant.Harness/appsettings.json` was missing entirely, breaking
  `dotnet build MeetingAssistant.sln` with `MSB3030`. Present now; confirmed
  covered by `.gitignore` line 15.

### Follow-ups left open

- Tray icon `.ico` fix described above (folds back into T2 / T2.1).
- Config validation covers **presence only**, by decision. It would not catch a
  `Storage:VaultPath` that is well-formed but points nowhere real — the exact
  problem found above. Extending it to resolve paths is a candidate if that
  recurs.
- `MeetingAssistant.Harness` still duplicates `ReadRequiredSetting`/`ReadSetting`
  from `App.xaml.cs` and does not call the validator, so it retains the original
  fail-on-first-missing-key behaviour.

---

## T8 — Prompt catalog after transcript + report viewer

**Status: ✅ DONE** — 2026-08-14, including same-day follow-ups T8.1–T8.3.
**Depends on:** Fase 1 extractor (done). Independent of T2.1 / T3.
**Touches:** `MeetingAssistant.Core` (catalog, prompt, extractor, pipeline
split — still no provider packages), `Infrastructure/Storage`,
`App` RecordPage/ViewModel/coordinator + Markdig preview,
`Harness` extract-from-transcript path.

### Why
The extractor had a single hardcoded system prompt
(`ReportExtractionPrompt` v1 — assignment-meeting JSON). As soon as a
transcript existed, the only thing the app could do was run that prompt
and save a `MeetingReport`. Real use needs more than one report shape
and a pause after the transcript: pick a prompt, **view it**, generate,
**view the report** (rendered, not only raw), and find the file in the
Obsidian vault.

### What landed

**T8 — catalog + pipeline split**
1. Core `IPromptCatalog` / `PromptDefinition` (id, display name,
   description, version, system prompt, output kind). Two built-in
   entries:
   - `assignment-meeting` @ v1 — original structured `MeetingReport`.
   - `functional-spec` @ **v2** — user-supplied technical-analyst
     prompt. Output is Markdown in the **same language as the
     transcript**. Six sections: executive summary, entities/states,
     flow diagram (text or Mermaid), business rules, pending points,
     agreed actions. Not bound to any product domain. An earlier v1
     JSON schema / sample-domain wording was replaced the same day.
2. `ILlmReportExtractor.ExtractAsync` takes an optional `promptId`
   and returns `ExtractionResult` (Markdown body + metadata + prompt,
   plus `MeetingReport` only for the assignment prompt).
3. RecordPage stops at the transcript:
   `StopRecordingAndTranscribeAsync` / `TranscribeAudioFileAsync` /
   `ExtractAndSaveAsync`. Tray / HTTP / harness-record still run the
   full pipeline with the default prompt.
4. Vault save: `Storage:VaultPath` + `Storage:SubFolder`
   (`MeetingReports`). Frontmatter includes `prompt-id`. File prefix
   follows the prompt (`meeting-report-…` / `functional-spec-…`).
5. RecordPage: catalog ComboBox, prompt preview, "Generar reporte".
   Same transcript can be re-run with a different prompt.
6. Harness: `--extract-transcript <file> [--prompt <id>]` and
   `--verify-render`.

**T8.1 — attach existing transcript**
- RecordPage button **Adjuntar transcripción (.txt)**. Loads the file
  into `LastTranscript` (no Deepgram) and reuses the catalog/generate
  path. Rejects non-`.txt` and empty files. Disabled while recording
  or processing. Audio import was renamed **Procesar audio existente**.

**T8.2 — vault path visible**
- Reports were already written to the configured Obsidian vault, under
  `MeetingReports` (not the vault root — easy to miss). RecordPage now
  labels **Guardado en el vault de Obsidian**, shows the full path, and
  has **Mostrar en el Explorador** (`explorer.exe /select`).

**T8.3 — rendered Markdown tab**
- Report area is a `TabView`: **Vista previa** (default) and
  **Markdown** (raw). Preview = Markdig **1.3.2** → HTML in `WebView2`.
  `UseAdvancedExtensions` + `DisableHtml`. Theme follows the app
  (light/dark). YAML frontmatter is stripped before render. Markdig is
  referenced only from `MeetingAssistant.App` — Core still has zero
  package references. If WebView2 fails to start, the error is shown
  and the raw tab still works.

### Acceptance criteria (all of T8–T8.3)
- After record / process-audio / attach-`.txt`, the transcript is on
  screen and the LLM is **not** called until the user picks a prompt
  and clicks generate.
- Catalog lists both built-in prompts; selecting one shows the full
  system prompt. Generate writes a new vault file (does not overwrite)
  and refreshes the on-screen report.
- Generated file lands in `{VaultPath}/MeetingReports/` with
  `prompt-id` in the frontmatter. The path is visible in the UI;
  Explorer can select that file.
- Report viewer has two tabs: rendered Markdown and raw Markdown.
- Tray / HTTP stop still auto-extract with `assignment-meeting`.
- `MeetingAssistant.Core.csproj` still has zero package references.
- `dotnet build MeetingAssistant.sln` succeeds.

### Validation notes (2026-08-14)
- `dotnet build MeetingAssistant.sln` — 0 errors, 0 warnings after
  each increment (catalog, v2 prompt, attach `.txt`, Explorer button,
  TabView + Markdig). Final App build with Markdig 1.3.2: 0/0.
- `MeetingAssistant.Core.csproj` still has zero package references.
  Markdig lives only in `MeetingAssistant.App.csproj`.
- `--verify-render` lists `assignment-meeting @v1` and
  `functional-spec @v2`, and asserts the v2 prompt contains the six
  user-specified sections and does not name an external product.
- Real LLM extracts via harness `--extract-transcript` +
  `--prompt functional-spec` produced vault files
  `functional-spec-20260814-165821.md` (v1 prompt, later superseded)
  and `functional-spec-20260814-175515.md` (v2, Spanish output
  matching a Spanish transcript, six requested sections).
- A later UI generate wrote
  `functional-spec-20260814-180734.md` into the configured vault's
  `MeetingReports` folder (confirmed on disk). The preview in the app
  is a copy of that file, not a substitute for it.
- `dotnet run --project src/MeetingAssistant.App` reached
  "Launching packaged application..." and stayed up. Tab click-through
  of Vista previa / Markdown was confirmed by the user ("Pretty well");
  no GUI automation from this session.

### Follow-ups left open (T8)
- Mermaid diagrams in a functional-spec report render as fenced code
  in the preview (Markdig does not execute Mermaid). Not requested.
- Settings still has no UI to edit `Storage:VaultPath` / `SubFolder`.
- T2.1 (stale RecordPage after tray/HTTP start) is unchanged and
  still blocks T3.

---

## Explicitly out of scope for this task graph

- Building out `HistoryPage` / `SettingsPage` beyond the one autostart toggle
  (T5) — that's Fase 2, already noted as an existing gap, not Fase 3's job.
- Deepgram Keyterm Prompting fix for mixed ES/EN jargon (Fase 0's deferred
  known limitation) — unrelated to Fase 3.
- Store submission / public distribution of the MSIX package — the roadmap
  scope is "Personal, independiente."

## Open questions for the user

- ~~What hotkey combination should be the default for T3?~~ Resolved
  2026-08-21: `Ctrl+Alt+F9`, selected by the user after real testing.
- ~~Should "Salir" from the tray while a recording is in progress block with
  a confirmation dialog, or auto-stop-and-process before exiting?~~ Resolved
  in T2: confirmation dialog, discard-on-confirm (see T2 step 4).
