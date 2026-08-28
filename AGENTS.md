# AGENTS.md

## Project overview

Personal AI assistant for work-assignment meetings. Records audio
(loopback + microphone), transcribes it (Deepgram Nova-3), extracts a
structured report via an LLM (Gemini Flash-Lite or DeepSeek via Azure
Foundry, swappable by config), and saves it as Markdown into an Obsidian
vault. Windows desktop app (WinUI 3) — not cross-platform, don't try to
make it portable.

## Stack

.NET 10, C#, WinUI 3 (Windows App SDK 2.3.1), CommunityToolkit.Mvvm,
NAudio (WASAPI), Deepgram SDK, Google GenAI SDK, Azure.Identity + OpenAI
SDK (for Azure AI Foundry), `System.Net.HttpListener` for the local
control endpoint.

## Architecture — non-negotiable rule

`src/MeetingAssistant.Core` must NOT reference ANY provider-specific
NuGet package (no NAudio, Deepgram, Google.GenAI, Azure.Identity, OpenAI).
Plain C# and interfaces only. Check `Core.csproj` before considering any
task done — if it has a provider reference, something went wrong.

```
Core/Models         → MeetingReport, TaskItem, Priority (schema + robust parsing)
Core/Abstractions    → interfaces + pure orchestration. ILlmClient,
                       ITranscriptionClient, IAudioCaptureService,
                       IReportStorage, ILlmReportExtractor, IMeetingPipeline
                       (the last one DOES have an implementation here —
                       it only composes abstractions, no SDKs, doesn't
                       break the rule)
Infrastructure       → concrete adapters: Llm/, Transcription/, Audio/,
                       Storage/, Api/, Cost/
App                  → WinUI 3 shell (MVVM, CommunityToolkit.Mvvm)
Harness              → end-to-end test console
```

`Infrastructure` references `Core`. `App`/`Harness` reference both. Never
the other way around.

## Commands

- Build: `dotnet build MeetingAssistant.sln`
- Test console (records N seconds, runs the full pipeline):
  `dotnet run --project src/MeetingAssistant.Harness -- 15`
- Real app: `dotnet run --project src/MeetingAssistant.App` (requires
  Developer Mode enabled on Windows)
- Self-tests, all in the harness, all safe to run (temp DBs, never the real
  one): `--verify-render`, `--verify-db-selftest`, `--verify-settings-config`,
  `--verify-pipeline-history <wav>`. `--verify-db` inspects the REAL database
  read-only; `--set-setting <key> [value]` writes one setting into it (omit the
  value to delete the key).

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

- Configuration is four stacked `IConfiguration` layers, in this order:
  packaged `appsettings.json` → legacy per-user JSON (T9; migrated into the
  database on first run and replaced by a redacted copy) → SQLite `setting`
  table → environment variables. **Environment variables stay on top**: they
  are the escape hatch when a bad stored value prevents startup. Don't reorder
  it, and don't replace `IConfiguration` with the database — the whole point is
  that `ReadRequiredSetting`, `StartupConfigurationValidator` and
  `ConfigPricingCostEstimator` never learn the database exists. Whether a key is
  a secret (and therefore DPAPI-encrypted) is decided in ONE place,
  `Core/Abstractions/SettingKeyPolicy.cs` — never inline at a call site.
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

## Security incident that already happened — don't repeat it

A real local path (Windows username + employer name) once leaked into a
tracked `appsettings.example.json`. Never put a real value, "just to
test," into any `*.example.json` file.
