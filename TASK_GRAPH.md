# Task Graph — Fase 3: Integración al flujo de trabajo diario

**Source:** `roadmap-meeting-ai-assistant.md`, "Fase 3 — Integración al flujo de trabajo diario".
**Author:** Lead Software Architect analysis, 2026-08-07.
**Scope:** implementation steps to close Fase 3. No source code included below — this
is the plan a developer executes against.

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
T1 (centralize recording state)
  └─> T2 (tray icon + hide-to-tray window behavior)
        ├─> T3 (global hotkey)
        └─> T4 (toast notification on report ready / on error)
  └─> T5 (optional autostart via StartupTask)
T2, T5 ─────────────────────────────────────────> T6 (MSIX signing + local install)
```

T1 is the prerequisite for everything else. T3 and T4 both build on the tray
icon's "app stays alive without a visible window" behavior established in T2.
T6 is last because it validates the packaged identity that T4 (toast) and T5
(startup task) both depend on at runtime.

---

## T1 — Centralize recording state and completion/error events

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

## T2 — Tray icon with context menu + hide-to-tray window behavior

**Depends on:** T1.
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

### Implementation
1. Add a tray-icon package to `MeetingAssistant.App.csproj`. Recommended:
   `H.NotifyIcon.WinUI` — it is the maintained community package for WinUI 3
   tray icons (WinUI 3 itself ships no `NotifyIcon` equivalent). Resolve to
   the current latest version compatible with `net10.0-windows` /
   WindowsAppSDK 2.3.1 at implementation time; record the exact version
   actually pinned in the commit, don't guess it here.
2. Add an `Assets` icon suitable for the tray (16x16/menu-bar scale) if the
   existing `AppIcon.ico` isn't appropriately sized.
3. Create the tray icon at `App.xaml.cs` startup (after `apiServer.Start()`),
   wired to a context menu with at minimum:
   - "Grabar reunión" / "Detener grabación" (label reflects
     `RecordingCoordinator.IsRecording` from T1, toggles the same coordinator
     call the RecordPage button uses).
   - "Abrir ventana principal" (shows/activates `MainWindow`).
   - "Salir" (the only path that calls `apiServer.Stop()` + disposes the tray
     icon + actually exits the process).
4. In `MainWindow.xaml.cs`, intercept the window close request (`Closed` event
   is too late to cancel — use `AppWindow.Closing` or the WinUI 3
   `Window.Closed`/`AppWindow` closing-with-cancel pattern) to hide instead of
   close, unless the close was initiated by the tray "Salir" command (use a
   flag the tray Exit handler sets before calling `window.Close()`/app exit).
5. Update `App.xaml.cs`: remove the `window.Closed += (_, _) =>
   apiServer.Stop();` line entirely; `apiServer.Stop()` now only happens from
   the tray Exit path (and normal process-exit cleanup).

### Acceptance criteria
- Clicking the window's X button hides the window; the process is still
  running (visible in Task Manager), the tray icon is still present, and
  `POST /recording/start` against the local API still succeeds while the
  window is hidden.
- Starting a recording from the tray menu, then opening the main window,
  shows `RecordPage` already reflecting `IsRecording = true`.
- Choosing "Salir" from the tray while a recording is *not* in progress stops
  the API server, removes the tray icon, and ends the process (nothing left
  in Task Manager).
- Choosing "Salir" while a recording *is* in progress does not silently drop
  the in-progress audio capture — decide and document the behavior (block
  exit with a confirmation, or stop-and-process-then-exit) and implement it;
  do not leave it unhandled.

---

## T3 — Global hotkey to start/stop recording

**Depends on:** T2 (needs the app alive without a visible window for the
hotkey to be useful at all).
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

## Explicitly out of scope for this task graph

- Building out `HistoryPage` / `SettingsPage` beyond the one autostart toggle
  (T5) — that's Fase 2, already noted as an existing gap, not Fase 3's job.
- Deepgram Keyterm Prompting fix for mixed ES/EN jargon (Fase 0's deferred
  known limitation) — unrelated to Fase 3.
- Store submission / public distribution of the MSIX package — the roadmap
  scope is "Personal, independiente."

## Open questions for the user (non-blocking, but affect T3/T5 defaults)

- What hotkey combination should be the default for T3? (Needs to avoid
  colliding with common IDE/OS shortcuts on this machine.)
- Should "Salir" from the tray while a recording is in progress block with a
  confirmation dialog, or auto-stop-and-process before exiting? (T2
  acceptance criteria require *a* decision, not this specific one.)
