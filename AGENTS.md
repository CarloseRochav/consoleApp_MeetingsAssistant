# AGENTS.md

## Project overview

Personal AI assistant for work-assignment meetings. Records audio
(loopback + microphone) and saves a structured report as Markdown into an
Obsidian vault. Since Phase 8 step B (2026-11-10) the desktop is a **thin
client of the DevLog API**: it records, converts to 16 kHz, uploads, follows
the job and writes the `.md`; transcription (Deepgram Nova-3) and extraction
(LLM) run in the server's worker. No provider key and no database connection
string on the machine. Windows desktop app (WinUI 3) — not cross-platform,
don't try to make it portable.

## Stack

.NET 10, C#, WinUI 3 (Windows App SDK 2.3.1), CommunityToolkit.Mvvm,
NAudio (WASAPI capture, 16 kHz conversion), `HttpClient` + System.Text.Json
for the DevLog API and the Azure block upload (no Azure SDK in the app),
SQLite for this machine's settings, `System.Net.HttpListener` for the local
control endpoint. The harness alone keeps Npgsql (final import) and
Azure.Storage.Blobs (publishing the installer).

## Architecture — non-negotiable rule

`src/MeetingAssistant.Core` must NOT reference ANY NuGet package (no NAudio,
Deepgram, Google.GenAI, Azure.*, Npgsql, OpenAI). Plain C# and interfaces
only (System.Text.Json attributes are part of the runtime, not a package).
Check `Core.csproj` before considering any task done — CI checks it too.

```
Core/Models          → DevLog API models (ApiSession, ApiReport, JobRow...),
                       ApiResult/ApiError, QueuedMeeting, SessionSource
Core/Abstractions    → IDevLogApi, IDeviceSessionStore, IAudioUploader,
                       IAudioConverter, IQueuedMeetingStore, IAudioCaptureService,
                       IReportStorage, IRecordingTrigger, ISettingsStore,
                       SettingKeyPolicy
Core/Services        → pure orchestration over those interfaces:
                       DeviceAuthService (login once → device token; 401 =
                       revoked), MeetingUploadQueue (the phone's pipeline:
                       job, blocks with resume, confirm, follow, report → vault),
                       MeetingRecorder (capture → 16 kHz → queue), and the
                       history / costs / prompts / server-settings services
Infrastructure       → Api/ (DevLogApiClient, AzureBlockUploader,
                       LocalRecordingApiServer), Audio/ (capture,
                       UploadAudioConverter), Storage/ (vault Markdown, JSON
                       queue, DPAPI device file, SQLite settings)
App                  → WinUI 3 shell (MVVM, CommunityToolkit.Mvvm).
                       App/Startup: ConfigurationBootstrapper (the config
                       layers), ServiceRegistration (composition root, one
                       method per area), BackgroundMaintenance (queue, network,
                       Select_Me), AppLog, AppPaths. App.xaml.cs is only the
                       lifecycle (setup → sign-in → main window).
Harness              → test console. Every command is an entry in
                       Commands/CommandCatalog.cs (Command pattern); `--help`
                       lists them.
```

`Infrastructure` references `Core`. `App`/`Harness` reference both. Never
the other way around.

**Fase 8 server is NOT in this repo.** Since 2026-10-18 the meetings API is
built inside `C:\Projects\DevLog_Backend\devLog_Backend`, in DevLog's own
conventions (its `CLAUDE.md` rules). The local branch `fase-8-api` with
`src/MeetingAssistant.Api` is abandoned reference code: port ideas from it,
never merge it or keep building it.

## Commands

- Build: `dotnet build MeetingAssistant.sln`
- CI: `.github/workflows/ci.yml` builds with warnings as errors, checks that
  Core has no packages, and runs every offline self-test on each push. No
  secrets: it copies the `*.example.json` files. A new offline self-test goes
  into its list. Smart App Control on the dev machine can block freshly built
  harness DLLs (`0x800711C7`); when that happens, CI is where the tests run.
- Test console (records N seconds and sends it to the DevLog API like the
  app; charges): `dotnet run --project src/MeetingAssistant.Harness -- 15`
- Real app: `dotnet run --project src/MeetingAssistant.App` (requires
  Developer Mode enabled on Windows). First run asks for the DevLog login.
- Offline self-tests (CI runs them; no network, fake transport): `--verify-api-client`,
  `--verify-block-upload`, `--verify-wav-convert`, `--verify-device-auth`,
  `--verify-upload-queue` (also runs the real background loop),
  `--verify-api-services`, `--verify-settings-config`, `--verify-release`.
  `--verify-long-audio [min]` is offline but writes hundreds of MB (not in CI);
  `--verify-capture [s]` records with the real devices and uploads nothing.
- Against the REAL DevLog API: `--api-signin <user>` gives the harness its own
  device (`harness-MACHINE`); without one it uses the app's device on this
  machine. `--verify-api` is read-only. `--api-process-file <wav>`,
  `--api-resume` and `--api-reextract <idSession> <promptId>` run real jobs
  (they charge) and write to `local-vault/` unless `--vault` is given.
- `tools\Revoke-OldDevices.ps1 [-Apply]` (the owner, with his DevLog login) revokes
  this machine's old devices, keeping the one the app uses; lists only without
  `-Apply`.
- `--migrate-to-devlog <idUser> [--apply]` is the owner's final import (old
  Postgres → DevLog `mtg`, target `DevLog__ConnectionString`). It compares row
  by row and refuses `--apply` if any DevLog row would change (the import
  upserts by source id and the API creates its own rows since 2026-10-19).
  `--verify-db` inspects the REAL local settings database read-only;
  `--set-setting <key> [value]` writes one setting into it (omit the value to
  delete the key).


- Installer for other machines: `installer/Build-Release.ps1 [-Publish]` bumps
  the version in `Package.appxmanifest` (commit it), builds a signed,
  self-contained Release `.msix` into `artifacts/release/<version>/` (ignored),
  and refuses to continue if the package contains `appsettings.json`, a
  provider or database assembly, or a key name in its defaults.
  `--publish-release <folder> [--apply]` uploads it to the private `releases`
  container and writes the `.appinstaller` with a read-only SAS link;
  `--verify-release` is offline. The install command it prints carries that
  link: never paste it into a tracked file.
- The packaged config layer is two files: `appsettings.defaults.json` (tracked,
  no secrets, ships in every package) and `appsettings.json` (real keys, Debug
  only, never in a Release package; since step B it holds no provider key). A
  machine missing required settings (vault, `DevLog:BaseUrl`) opens
  `SetupWindow` instead of the startup-error window.

**Use `dotnet run` — not `dotnet build` — to test a change in the real app.**
The dev-registered package executes the `AppX\` layout under
`bin\x64\Debug\...\win-x64\AppX`, and `dotnet build` refreshes only the
directory above it. Launching by AUMID after a plain build silently runs the
OLD binary. This already cost a debugging round on 2026-08-28.

Related: a packaged app does **not** inherit environment variables from the
console that launches it (the shell broker activates it). The
`Seccion__Clave` escape hatch works only as a user-level variable:
`[Environment]::SetEnvironmentVariable('Api__Port','5759','User')`.

## Non-negotiable conventions

- Configuration is stacked `IConfiguration` layers, in this order:
  packaged `appsettings.defaults.json` + `appsettings.json` → legacy per-user
  JSON (T9; migrated into the database on first run and replaced by a redacted
  copy) → SQLite `setting` table (this machine only: vault, hotkey, local
  endpoint token) → environment variables. Since step B there is no cloud
  layer: shared settings (provider, model, keyterms, audio retention) live in
  the DevLog API, in the user's own layer, and are edited through it (never a
  secret, never `Pricing:*`). **Environment variables stay on top**: they are
  the escape hatch when a bad stored value prevents startup (e.g. a user-level
  `DevLog__BaseUrl`). Don't reorder it, and don't replace `IConfiguration` with
  the database. Whether a key is a secret (and therefore DPAPI-encrypted) is
  decided in ONE place, `Core/Abstractions/SettingKeyPolicy.cs` — never inline
  at a call site.
- The device token (`device.json`) and the upload link in each queue file are
  credentials: DPAPI on disk, `[redacted]` in every `ToString`, never logged.
  The password is never stored: it travels in one request from a `char[]` that
  is cleared.
- The real `appsettings.json` is NEVER committed — only
  `appsettings.example.json` with placeholders. Any new
  `appsettings.json` (new project, new path) must be explicitly checked
  against `.gitignore`, never assumed to already be covered.
- Never fabricate values: pricing, credentials, test results that weren't
  actually run. If something couldn't be verified, say so explicitly in
  the report — don't fill in a plausible-looking value to make it look
  complete.
- Before marking a task done: test it for real (build + actual run, not
  just reading the code). If something blocks the real test (e.g.
  Developer Mode disabled, a missing value), say so and stop — don't
  silently substitute a mock.
- No emojis in headers/callouts of the `.md` files generated for
  Obsidian.
- Any new network surface requires explicit authentication, even
  "localhost only" — `HttpListener`/HTTP.sys on Windows does not give a
  true loopback socket bind. See
  `Infrastructure/Api/LocalRecordingApiServer.cs` for the pattern already
  in use (token + `RemoteEndPoint` verification).

## Current state and pending work

See `roadmap-meeting-ai-assistant.md` at the repo root — it's the
detailed phase-by-phase tracker, with what's done and what's pending.
Update it when closing any milestone; don't let it go stale.

Every decision the owner makes (what was chosen, over what, why, the
trade-offs and accepted risks) goes into the decision log, `decision-log.md`
at the repo root (a Claude Doc until 2026-10-19; that copy is retired).
Record it there and in the roadmap in the same turn the decision is made,
and give the owner both locations when asked; never leave a decision only in
chat. Present a pending decision as one table (experience, cost, effort,
trade-off) plus a recommendation.

## Security incident that already happened — don't repeat it

A real local path (Windows username + employer name) once leaked into a
tracked `appsettings.example.json`. Never put a real value, "just to
test," into any `*.example.json` file.
