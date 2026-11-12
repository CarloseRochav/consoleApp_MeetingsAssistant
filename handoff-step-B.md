# Handoff: Phase 8 step B, the desktop as an API client (2026-11-09)

Plan and decisions: `roadmap-meeting-ai-assistant.md` (Phase 8, "Paso B — escritorio como cliente")
and `decision-log.md` (2026-11-09 entries for step B, and the Phase 8 decisions of 2026-10-15).

**Goal (Phase 8, step B):** the desktop (WinUI 3, this repo) becomes a thin client of the DevLog API,
like the Android app: it records, triggers (hotkey, tray, local endpoint), uploads, and writes the
`.md` into the vault. **No provider key and no database connection string on the machine.**
Transcription and extraction run in the worker; history, search, costs, prompts, settings and
re-extraction go through the API.

**Owner's decisions (2026-11-09):** one cutover, no fallback mode; offline the desktop records,
queues and sends later; the server keeps a cloud archive of the audio for a set number of days;
it is not known whether the desktop runs on other machines (so the final import must be checked
for new rows and id collisions before it runs).

**Order:** retry-delay handoff first (`handoff-retry-delay.md`, it touches the same two server repos)
→ DevLog (archive) → worker (archive) → owner deploys → desktop (this repo) → cutover (owner).
Migration numbers: the retry delay takes 017 if it needs one; the archive takes the next.

What already exists in the API (used by the phone, tested in production): device sign-in
(`Auth/Login` once → `Devices/InsertUpdate_Devices` → device token; `Devices/Select_Me`), upload jobs
(`Jobs/InsertUpdate_Jobs` options 0/1/2, `Jobs/Select_Jobs`, block upload to a SAS link, 1 GiB
limit, daily cap and in-flight limit with a 429 body), reports, sessions, transcript, search, costs,
settings and prompt layers (`Meetings/*`), the prompt catalog and re-extraction (016). See DevLog's
`CLAUDE.md` and the Android app (`C:\projects\MeetingAssistant_App`, `:core`) for a working client.

---

## Prompt 1: DevLog_Backend (open the session in `C:\Projects\DevLog_Backend\devLog_Backend`)

```
Read CLAUDE.md first, then the Meeting Assistant plan: C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\handoff-step-B.md, roadmap-meeting-ai-assistant.md (Phase 8, "Paso B") and decision-log.md (2026-11-09 entries). Record findings and decisions there.

Prerequisite: the retry-delay work (handoff-retry-delay.md) is merged; take the next migration number after it. If it is not, stop and say so.

Goal (owner's decision 2026-11-09): keep a cloud archive of meeting audio for a set number of days instead of the worker deleting it as soon as the report is done (today's behaviour since 2026-10-30). The desktop today keeps its WAVs in Azure Blob N days (its own retention); after step B the server owns this for every client.

1. Design it in DevLog's style (stored functions, named arguments, BL/DL, one migration): where archived audio lives (a separate container, or the uploads blob kept with an expiry), per user or shared retention (a setting in the user's layer with a base default, like the other Meetings settings?), what the worker calls when a job is done (archive instead of delete), the cleanup that deletes expired audio (a stored function the worker or a timer calls), and whether clients can get a short read link to listen again (an endpoint like Videos/Select_Video_Link). Decide each, say why, record it.
2. Keep the deployed worker working until the new one ships (it deletes audio today; that must stay valid).
3. Verify locally (Docker Postgres + Azurite, as CLAUDE.md describes): archive on done, a failed job keeps its audio as today, the cleanup deletes only expired audio, another user's audio is 404, 401 without a token, the read link if added. Report what was tested and what was not.

Do not deploy or apply anything to production. Finish with a PR, CLAUDE.md updated, and a contract summary for the worker and desktop sessions.
```

---

## Prompt 2: MeetingAssistant_Worker (open the session in `C:\Projects\MeetingAssistant_Worker`)

```
Read AGENTS.md, README.md and COPIED.md first, then C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\handoff-step-B.md, the roadmap's Phase 8 "Paso B" and decision-log.md (2026-11-09). Record findings and decisions there.

Prerequisite: the DevLog archive migration from handoff-step-B.md (Prompt 1) exists; read its CLAUDE.md contract. If not, stop and say so. Start from an up-to-date main on a new branch.

Goal: when a transcribe job is done, archive its audio as the DevLog contract says instead of deleting it; run the expired-audio cleanup if the contract gives it to the worker. A job that fails for good keeps its audio, as today. Re-extraction jobs have no audio: unchanged.

Tests: JobProcessorTests with fakes and tools/Run-IntegrationTests.ps1 against a local DevLog with the new migration. Never log transcripts, reports, prompt text, keys or links. Keep copied code untouched unless strictly needed (WORKER COPY CHANGE + COPIED.md). Do not deploy. Finish with a PR and a summary.
```

---

## Prompt 3: the desktop (open the session in `C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant`)

```
Read AGENTS.md first, then handoff-step-B.md, roadmap-meeting-ai-assistant.md (Phase 8: design conditions, step A, step D's Android app as the reference client, and "Paso B") and decision-log.md (2026-11-09 step B entries). Record findings and decisions there.

Prerequisite: the archive server work from handoff-step-B.md is deployed (roadmap says so). If not, do step 0 and the client work that does not depend on it, and say so.

Goal (Phase 8 step B): the desktop becomes a thin client of the DevLog API. No provider key and no connection string on the machine. One cutover, no fallback mode (owner's decision 2026-11-09). Keep AGENTS.md's Core rule: Core has no provider package; the API client lives in Infrastructure behind Core's interfaces.

0. Before anything else, read-only: run `--migrate-to-devlog <idUser>` WITHOUT --apply against the old database and DevLog production, and report the counts. The import keeps source ids, and the API has created sessions, reports, settings and prompt versions since the 2026-10-19 import (phone sessions use ids 26 and up; the owner saved and reverted a test prompt version on 2026-11-09). If the old database has any row newer than that import, find out whether its id collides with an API row and propose how to bring it over without overwriting anything (never run --apply with a collision). The owner is not sure whether the desktop runs on other machines.
1. Sign-in: the user's DevLog login once (password never stored, cleared from memory) → this machine's device token (name it after the machine), stored with DPAPI, redacted in every toString and log; Select_Me at start; a 401 means revoked → back to sign-in with a message. A Settings page shows the device and signs out.
2. Recording → report through the API: keep capture as it is; before upload convert to 16 kHz 16-bit PCM mono (today 44.1 kHz 32-bit float ≈ 10.6 MB/min would pass the API's 1 GiB limit at ~100 minutes); create the job, upload block by block with resume (the phone's BlockUploader is the reference), confirm, follow the job, fetch the newest report, write the .md into the vault with the existing MarkdownReportStorage. The source sent is "desktop".
3. Offline and restarts (owner's decision): recording always works; the upload queue is persisted and resumes when the API is back and when the app starts again; show where each meeting is (waiting, sending x%, writing report, ready, failed with the server's reason, 429 with the server's numbers).
4. Everything else through the API: history, transcript, search, costs (NULL never shown as 0), settings (the user's own layer; never a secret, never Pricing), prompts (catalog + own edits, back to the built-in), re-extraction as a job (Jobs option 2). The cloud audio archive per the DevLog contract (listen again, if it gives a read link).
5. Remove the local provider mode in the same release: the Deepgram/Gemini/Foundry clients, provider keys, the direct Postgres and SQLite stores, the Blob archive client, from App and Infrastructure and from appsettings*.json and the Phase 7 installer. Keep only what the harness still needs for the final import, or move that command out.
6. Harness: replace the self-tests that called providers or databases with self-tests against the API (step A left "the harness autotests against the API" to step B), plus offline tests with a fake transport. CI as AGENTS.md says.
7. Test on this PC against production before the cutover: a Teams call recorded → .md in the vault with no provider key on the machine; a long recording (over 100 minutes before conversion, to prove the 16 kHz upload fits); offline record then send; a restart in the middle of an upload; history/search/costs/prompts/re-extraction from the desktop; a revoked device. Anything not measured is reported as not measured.

Do not run the final import with --apply, delete the old database or rotate keys: those are the owner's cutover steps (below). Commit per feature, and finish with a PR.
```

---

## Cutover (the owner, after Prompt 3 is tested)

1. Final import, only as the desktop session's step 0 says (none if nothing new; a collision plan if any).
2. Install the new desktop release on every machine that has the app (the Phase 7 installer).
3. ~~Rotate the provider keys that lived on desktop machines and the old database's password~~: **dropped by the owner (2026-11-11, `decision-log.md`)**. Delete the old database (or keep it read-only for a while: decide then). Optionally clear the old keys from each machine's local settings (Settings → "Borrar las claves viejas de esta máquina") and revoke old devices with `tools\Revoke-OldDevices.ps1`.
4. The roadmap records step B as done, with what was tested.
