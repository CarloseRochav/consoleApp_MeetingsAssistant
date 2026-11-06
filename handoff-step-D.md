# Handoff: finishing Phase 8 step D (2026-11-05)

Three prompts, one per repo, to run in separate Claude Code sessions **in this order**:
DevLog_Backend → MeetingAssistant_Worker → (owner deploys both) → MeetingAssistant_App.
The app prompt's part 1 does not depend on the server and can run at any time.

Plan and decisions: `roadmap-meeting-ai-assistant.md` ("Lo que falta del paso D") and
`decision-log.md` (2026-11-05 entries).

---

## Prompt 1: DevLog_Backend (open the session in `C:\Projects\DevLog_Backend\devLog_Backend`)

```
Read CLAUDE.md first, then the Meeting Assistant plan: C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\roadmap-meeting-ai-assistant.md (section "Lo que falta del paso D") and decision-log.md (2026-11-05 entries). Record findings and decisions there.

Goal: two server pieces the Android app needs to finish Phase 8 step D, built before step B (owner's decision 2026-11-05). Work on a new branch, one migration (016), following every rule in CLAUDE.md (stored functions only, named arguments, BL/DL/Interfaces flow, models naming, logging redaction, schema.sql kept in step, dbmate first/last lines).

1. Built-in prompt catalog.
   - Today the built-in prompts (id, version, text) exist only in the Meeting Assistant desktop code and in the worker's copy (C:\Projects\MeetingAssistant_Worker\src\MeetingAssistant.Worker\Copied\MeetingAssistant.Core\Services\PromptCatalog.cs and Prompts/*); mtg_prompt_versions holds only edits. A phone cannot show or edit a prompt it cannot read.
   - Add a table for the built-ins (prompt_id PK, version, name, system_prompt, updated_at; check what PromptDefinition carries and mirror it) and a worker function to upsert the whole catalog (the worker will call it at the start of each run; the worker repo is done in a separate session, so just provide the function and document its contract).
   - Expose them to clients: Meetings/Select_Prompt_Versions option 4 = built-ins (or a new action if that fits the controller's shape better; decide and say why). Accepts JWT and device token like the rest of MeetingsController. Never log prompt text (system_prompt is already redacted in the payload log).
   - Insert_Prompt_Versions should validate prompt_id against the catalog once it is populated (decide what happens while the table is still empty, e.g. right after deploy before the worker's first run, and document it).

2. Re-extraction as a server job.
   - Today re-extraction runs only on the desktop with its own provider keys; the phone holds no provider key, and step B will remove them from the desktop too. So it becomes a job the worker runs.
   - mtg_jobs gets a kind ('transcribe' default for every existing row, 'reextract') and a prompt_id (NULL for transcribe). A reextract job targets an existing session of the caller that has a transcript; it is created already 'queued' (no upload, no blob), and wakes the worker through the same queue message as a confirm.
   - Jobs/InsertUpdate_Jobs option 2 = re-extract { idSession, prompt_id } → the queued job row. 404 for a session that is not the caller's, 409 when it has no transcript, 400 for an unknown prompt_id. The daily spending cap and the in-flight limit (migration 011) apply exactly as for uploads (429 with the same body).
   - mtg_worker_claim_job returns the kind, idSession and prompt_id so the worker knows what to do. mtg_worker_update_jobs option 1 (report ready) must work for a reextract job: a new report row in that session, job done. Option 0 (transcript) must refuse a reextract job. Failed/retry behaves as today. Check the abandoned-link cleanup (013) and anything else that assumes every job has a blob.
   - Select_Jobs rows show kind and prompt_id.

3. Verify for real, locally (Docker Postgres + Azurite, as CLAUDE.md describes): schema.sql on an empty database, the migration through tools\Db.ps1 up, every new path including 401 without a token, another user's session 404, the cap 429, a device token, and simulated worker calls (claim → update) for both kinds. Throwaway users, deactivated afterwards. Report what was tested and what was not. Extend tools\Verify-Api if it covers these endpoints.

Do not deploy, merge or apply anything to production: the owner runs tools\Release-Api.ps1 and tools\Db.ps1 for production. Finish with a PR, the CLAUDE.md sections updated, and a short summary of the new contracts (for the worker and app sessions).
```

---

## Prompt 2: MeetingAssistant_Worker (open the session in `C:\Projects\MeetingAssistant_Worker`)

```
Read AGENTS.md, README.md and COPIED.md first, then the Meeting Assistant plan: C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\roadmap-meeting-ai-assistant.md (section "Lo que falta del paso D") and decision-log.md (2026-11-05 entries). Record findings and decisions there.

Prerequisite: DevLog_Backend migration 016 (built-in prompt catalog + re-extraction jobs) exists on a branch or is merged; read its CLAUDE.md sections "Uploads and jobs", "Worker functions" and the new ones for 016 to get the exact function contracts. If 016 is not there yet, stop and say so.

Goal (owner's decision 2026-11-05: server pieces for the phone, before step B):

1. Publish the built-in prompt catalog: at the start of every run, before taking jobs, upsert BuiltInPromptCatalog.GetAll() through the new DevLog function. Failure to publish is logged (without prompt text) and never stops job processing.

2. Process re-extraction jobs: a claimed job of kind 'reextract' has an idSession and a prompt_id and no audio. Read that session's existing transcript through a stored function (add nothing to DevLog here: if no suitable function exists, stop and say what 016 is missing), build the job owner's configuration as for any job, extract with ExtractAsync(transcript, promptId: <the job's prompt_id>), save the report through mtg_worker_update_jobs option 1, with cost as today. No Deepgram, no audio download, no audio delete. Failures and retries behave as for transcribe jobs. Transcribe jobs must behave exactly as before.

3. Tests: extend JobProcessorTests for both kinds (fakes, as the existing tests), plus tools/Run-IntegrationTests.ps1 against a local DevLog database with 016. Never log transcripts, reports, prompt text, keys or links.

Keep copied code untouched unless strictly needed (mark WORKER COPY CHANGE and list it in COPIED.md). Do not deploy: the owner runs DevLog's tools\Deploy-Azure.ps1 -Step Images/Worker. Finish with a PR and a summary.
```

---

## Prompt 3: MeetingAssistant_App (open the session in `C:\projects\MeetingAssistant_App`)

```
Read AGENTS.md and README.md first, then the plan: C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\roadmap-meeting-ai-assistant.md (step D sections, especially "Lo que falta del paso D") and decision-log.md (2026-11-05 entries). Record findings and decisions there. The phone is a Samsung SM-S931B on adb (Android Studio's SDK); every feature is tested on it before it is called done, and anything not measured is reported as not measured.

Goal: finish Phase 8 step D in the app. Follow every rule in AGENTS.md: :core plain Kotlin with unit tests (FakeTransport), API → repository (:core) → ViewModel → screen, loading/error/empty states, light and dark @Preview for every new screen, MaterialTheme roles only, never log tokens, links or meeting content, versions only in libs.versions.toml. These are meetings features: they live in :feature-meetings (and :core), never in :feature-video.

Part 1 (no server change needed; do it now):
1. Report-ready notification: when MeetingPipeline downloads a report, show a local notification ("Your meeting's report is ready"); tapping it opens that report. No push (owner's decision 2026-11-05). Handle POST_NOTIFICATIONS being denied.
2. History of all sessions (desktop ones too): Meetings/Select_Sessions (paged), each session's reports (Select_Reports option 0) and transcript (Select_Transcripts), read in the app. Today the Meetings screen lists only meetings recorded on this phone; decide with the owner how the two lists relate (one merged list or a separate "History" screen) before building it.
3. Search: Meetings/Select_Search (hits come marked with [ and ]), opening the session.
4. Costs: Meetings/Select_Costs option 0 (totals) and 1 (per month), micro-dollars, NULL = unknown (never shown as 0).

Part 2 (only once DevLog migration 016 and the worker change are deployed; check the roadmap, and if they are not, stop after part 1 and say so):
5. Prompt editor: list the built-ins (Select_Prompt_Versions option 4, or whatever 016 settled on) with the effective version for this user (option 2) and this user's own edits (option 3); edit a prompt into the user's own layer (Insert_Prompt_Versions, shared = false; 409 = that version exists); "back to the built-in" (version and system_prompt null). No shared/admin editing from the phone.
6. Re-extraction: from a session, pick a prompt and start Jobs/InsertUpdate_Jobs option 2; follow the job like an upload's (Select_Jobs) and show the new report when done; handle 409 (no transcript), 429 (daily cap / in flight, show the server's numbers), 404.

Commit per feature with clear messages, push when the owner agrees, and update the roadmap with what was tested on the phone.
```
