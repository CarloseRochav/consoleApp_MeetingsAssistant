# Handoff: a delay between job retries (2026-11-09)

Found while testing step D on the phone (roadmap, step D, "Los 404 de Azure AI Foundry"): on
2026-11-10 01:03-01:04 UTC Azure AI Foundry answered `HTTP 404 DeploymentNotFound` for `grok-4.6`
for about 90 seconds. Nothing was wrong with the deployment (checked read-only: it exists, unchanged
since 2026-10-06, Azure's own metrics show just those 3 404s), but **job 32 used all three of its
attempts in 2 seconds** and succeeded on the last one. A slightly longer blip would have left a real
meeting `failed` on the phone.

Why it happens (read in both repos): after a retryable failure the worker calls
`mtg_worker_update_jobs` option 2, which puts the job back to `queued`; the drain loop
(`JobWorker` Drain mode) then calls `mtg_worker_claim_job` again, and the claim only looks at
`status`, `attempts` and staleness (`ORDER BY uploaded_at, idJob`), so it takes the **same job
straight away**. `Worker:MaxAttempts` is 3. There is no "not before" time anywhere.

Two prompts, to run in separate Claude Code sessions **in this order**: DevLog_Backend (only if the
design needs a schema change; decide there) → MeetingAssistant_Worker → (owner deploys) → nothing in
the app (the phone already treats a failed job's error as a message; check the wording only).

---

## Prompt 1: DevLog_Backend (open the session in `C:\Projects\DevLog_Backend\devLog_Backend`)

```
Read CLAUDE.md first, then C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\handoff-retry-delay.md and the roadmap's step D section "Los 404 de Azure AI Foundry". Record findings and decisions in that roadmap and in decision-log.md.

Goal: a failed-but-retryable Meeting Assistant job must not be claimed again immediately. Today mtg_worker_update_jobs option 2 sets it back to queued and mtg_worker_claim_job takes it at once, so a 1-minute outage of the LLM (seen 2026-11-10: three 404 DeploymentNotFound in 2 s) uses up all attempts.

1. Design the smallest server change (one migration, 017) that lets the worker say "retry this job no earlier than X": e.g. a nullable retry_after timestamptz on mtg_jobs, set by option 2 from a delay the worker passes (or a backoff the database computes from attempts: decide and say why), and respected by mtg_worker_claim_job (WHERE retry_after IS NULL OR retry_after <= now()). Keep the deployed worker working before the new one ships (named arguments with defaults, like p_kinds in 016).
2. Check every other place that reads queued jobs: the in-flight limit and daily cap (011), the abandoned-upload cleanup (013), Select_Jobs (should it show retry_after to the phone?), the stale-processing rule.
3. Verify locally (Docker Postgres, schema.sql + tools\Db.ps1 up, as CLAUDE.md describes): a retry with a delay is not claimed before it, is claimed after, the old two-/three-argument calls still work, the cap and in-flight counts unchanged. Report what was tested and what was not.

Follow every CLAUDE.md rule (stored functions only, named arguments, schema.sql in step, dbmate first/last lines, no secrets or meeting content in logs). Do not deploy or apply anything to production. Finish with a PR, CLAUDE.md updated, and a short contract summary for the worker session.
```

---

## Prompt 2: MeetingAssistant_Worker (open the session in `C:\Projects\MeetingAssistant_Worker`)

```
Read AGENTS.md, README.md and COPIED.md first, then C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\handoff-retry-delay.md and the roadmap's step D section "Los 404 de Azure AI Foundry". Record findings and decisions there.

Prerequisite: the DevLog session for this handoff is done (migration 017 or its decision that no schema change is needed); read its CLAUDE.md contract. If it is not there yet, stop and say so.

Goal: wait between attempts of a failed job instead of retrying it at once. Seen 2026-11-10 01:03-01:04 UTC: Azure AI Foundry answered 404 DeploymentNotFound for ~90 s and job 32 spent its three attempts in 2 s.

1. On a retryable failure, ask DevLog to retry the job no earlier than a delay that grows with the attempt (e.g. 15 s after the first, 60 s after the second; put the numbers in WorkerSettings) and make sure a wake-up message reaches the queue after that delay (the drain run clears wake-ups at start and ends when nothing is claimable: a job waiting for its retry time must still be picked up later — e.g. wakeQueue.WakeLaterAsync with the delay, as the database-down path already does).
2. Decide which failures are retryable with a delay: at least LLM 404/408/429/5xx and timeouts, Deepgram 5xx, network errors. Keep failing at once what a retry cannot fix (unknown prompt, no transcript, empty transcription).
3. Keep transcribe and reextract jobs behaving the same otherwise; nothing about the catalog changes.
4. Tests: JobProcessorTests/JobWorker tests with fakes (the delay is passed, the wake-up is scheduled, a non-retryable failure is not delayed), and tools/Run-IntegrationTests.ps1 against a local DevLog with the new migration (a fake LLM that fails twice, then succeeds: three attempts spread over the delays, done). Never log transcripts, reports, prompt text, keys or links.

Keep copied code untouched unless strictly needed (mark WORKER COPY CHANGE, list it in COPIED.md). Do not deploy: the owner runs DevLog's tools\Deploy-Azure.ps1 -Step Images/Worker after the DevLog release. Finish with a PR and a summary.
```

---

## Deploy order

DevLog 017 to production (old worker keeps working) → DevLog API if its contract changed →
worker. Then a meeting from the phone as the end-to-end check; the roadmap records it.
