# Troubleshooting: a recorded video did not upload on Wi-Fi

2026-11-25 · Meeting Assistant Android app (`MeetingAssistant_App`, Phase 8 step D) · Samsung phone `RFCY21A421K`
Fix on branch `fix/video-upload-background-network` (not committed yet).

## Summary

A 22-minute 720p video (708 MB) recorded at 09:24 stayed on the phone as **"Waiting for Wi-Fi"** for more than five
hours, although the phone was on Wi-Fi and charging.

The cause was **not** Wi-Fi, the server, or the upload code. Two things combined:

1. **Samsung cuts the app's network about 30 seconds after the app leaves the screen** while the app is
   battery-optimised (Android reports `REASON_OEM_DENY`). The upload job asks for "an unmetered network", and Android
   checks that per app, so for this app Wi-Fi never "arrived" while it was closed.
2. **Opening the app made things worse instead of better.** The moment the app came to the screen, the job started,
   a few milliseconds before the network actually let the app's requests through. The first request failed in 5 ms
   ("No connection"), WorkManager doubled its wait, and after 8 such failures the next try was 64 minutes away.

**Fix:** the app now asks once to leave battery optimisation (Android's own dialog), a run that finds no connection
tries again after 3 s and 10 s before giving up, and the log names the exception. **Verified on the phone:** with the
exemption no block appeared in the background, and the video uploaded in **4 minutes, with the app closed**, then
appeared in the cloud.

## Symptom

- Video `20261125-092402`: recorded 09:24, 1 323 s, 720p, 708 386 825 bytes.
- The Video screen showed it "Waiting for Wi-Fi". Wi-Fi was on, and later the phone was plugged in.
- Its local record (`files/videos/20261125-092402.json`): `status: Waiting`, `idVideo: null`, `sentBytes: 0`.
  **No upload link had ever been obtained from DevLog**, so not one byte had been sent.

## How the investigation went

The approach was to follow the request from the bottom up: is there a network → is there a scheduled job → why isn't
it running → what happens when it does run. Each step used what Android itself reports (`dumpsys`) rather than
guessing, and every hypothesis below was checked with a command, not assumed.

### Step 1. Is the phone really on usable Wi-Fi? Yes, so not the cause

`dumpsys connectivity`: the default network is Wi-Fi "Infinitum-2.4g", `VALIDATED`, `NOT_METERED`, RSSI −56. The
video worker requires `NetworkType.UNMETERED`, which this network satisfies.

### Step 2. Is the server reachable? Yes, so not the cause

The DevLog API (`ca-devlog-api…azurecontainerapps.io`) answered right away (HTTP 404 on `/`, which means it's up).
Later the same server gave the upload link on the first request.

### Step 3. Does an upload job exist? Yes, and it had already run 7 times

`dumpsys jobscheduler` showed the job `#VideoUploadWorker` for the app, plus two clues:

- `Minimum latency: +31m59s`: a backoff of 30 s doubled six times, so the worker had **already run and asked to retry**.
- `Unsatisfied constraints: CONNECTIVITY`, **although Wi-Fi was validated and unmetered.**

WorkManager's own database (copied from `no_backup/androidx.work.workdb`, read with the SDK's `sqlite3`) confirmed it:
`state = ENQUEUED`, `run_attempt_count = 7`, `stop_reason = -256`. So the runs were not cut short by the system: each
one finished and **returned `Retry`**.

So there were two questions: why does Android say "no connectivity" for this app, and why did the runs that did start
fail?

### Step 4. Why "no connectivity" with Wi-Fi up? Samsung blocks this app's network (root cause 1)

`dumpsys connectivity` reports a block reason per app. For the app's UID (10059):

```
UID: 10059; Network: 178 (blocked=REASON_APP_BACKGROUND|REASON_OEM_DENY)
UID=10059 blockedReasons=c0
```

- `REASON_APP_BACKGROUND` (`0x40`) is Android's normal block for idle background apps. JobScheduler lifts it for a
  job it runs (the app is listed under "Requested standby exceptions"), so this one is harmless.
- `REASON_OEM_DENY` (`0x80`) is **Samsung's own firewall**. JobScheduler can't lift it, so the job's connectivity
  constraint stays unmet.

The job's constraint history matched: connectivity was satisfied only for a minute or two at a time (43 and 17
minutes earlier), which fits the moments the app was open.

**Measured on the phone** (14:05): the app was sent to the background with its current settings.

| Time in background | Block reasons |
| --- | --- |
| 5 s | `0x40` (APP_BACKGROUND only) |
| 30 s | `0xc0` (APP_BACKGROUND + **OEM_DENY**) |
| 60 s, 90 s | `0xc0` |

Then the app was taken out of battery optimisation (`cmd deviceidle whitelist +<package>`, the same list Android's
"Stop optimizing battery usage?" dialog writes to) and the test repeated:

| Time in background (`procState=LAST`) | Block reasons |
| --- | --- |
| 10 s, 45 s, 90 s | **none** (`effective=NONE`) |

The job's constraints changed at the same moment: `CONNECTIVITY` became satisfied, and only its timer was left.

### Step 5. Why did the runs that did start fail? A race at start-up (root cause 2)

The app's own log tag (`VideoUpload`) for the earlier runs had already rotated out of logcat, so the failure was
reproduced live: open the app, then force the job (`cmd jobscheduler run -f -n androidx.work.systemjobscheduler …`).

```
13:40:10.386 VideoUpload: video 20261125-092402: new upload (Waiting)
13:40:10.391 VideoUpload: video 20261125-092402: Retry (No connection. It will try again on Wi-Fi.)
```

**5 ms between start and failure.** That's too fast for a real network attempt: the request failed before leaving the
phone. Seconds later, commands run as the app's own UID (`run-as`, so the same firewall rules apply) reached both
`8.8.8.8` and the API host, DNS included. So the network was fine for the app a moment later. JobScheduler started the
job as soon as the app came to the screen, slightly before the block was actually lifted for the app's sockets.

Since `idVideo` was still `null`, all 7 earlier retries happened at step 1 of the pipeline (asking for the link), where
only these errors lead to `Retry`: no connection, HTTP 5xx, or "server busy". This run showed it was "no connection".

### Step 6. Why did the wait grow to an hour? Exponential backoff, which opening the app doesn't reset

The worker uses `BackoffPolicy.EXPONENTIAL, 30 s`: 30 s, 1 min, 2 min, … 32 min, 64 min. And the app's start-up call
`resumeAll()` schedules with `ExistingWorkPolicy.KEEP`, so opening the app leaves the waiting job and its timer as
they are. Each visit to the app could add one more fast failure and double the wait.

### Leads that were checked and ruled out

| Lead | How it was checked | Why it's not the cause |
| --- | --- | --- |
| Wi-Fi not connected or metered | `dumpsys connectivity` | Validated and `NOT_METERED` |
| Server down | HTTP request from the PC | Answered; later served the upload |
| Upload never scheduled, or lost | `dumpsys jobscheduler`, WorkManager DB | Job present, `ENQUEUED`, 7 attempts |
| Phone signed out | Pipeline code | No token makes the worker return `failure`, and a 401 marks the video Failed. Neither happened |
| Broken or empty file | Local record and file size | 708 MB, `Waiting`; an empty file is marked Failed |
| Data Saver / "restrict background data" | `dumpsys netpolicy` | `Restrict background: false`; the app is in "Background metered allowed" |
| Background running forbidden | `cmd appops get … RUN_ANY_IN_BACKGROUND` | Default `allow` |
| App in a restricted standby bucket | `am get-standby-bucket`, job dump | Bucket 10 (ACTIVE), `WITHIN_QUOTA` |
| The race alone (without Samsung) | Measurement in step 4 | Without `OEM_DENY` the job would have run in the background, and the race only happens at the moment the app opens |

## Decisions: why this fix and not the alternatives

Four changes were considered. Two of them, plus logging, were chosen:

| # | Change | Decision | Why |
| --- | --- | --- | --- |
| 4 | Ask to leave battery optimisation | **Done** | It removes the root cause. Without it, uploads only move while the app is open, and Samsung cuts the network as soon as the app is closed |
| 2 | Retry inside the run when there's no connection (3 s, 10 s) | **Done** | It covers the start-up race and short drops (for example, switching from mobile data to Wi-Fi) without growing WorkManager's backoff |
| 3 | Log the exception's class name | **Done** | Cheap. It turns the next diagnosis into reading one log line. Only the class name, never the message (it can contain a URL), a token or content |
| 1 | Opening the app restarts a waiting upload (`REPLACE`) | **Not done** | It only works while the app is open, so it doesn't address the background case, which is the real goal. On its own it would start the job exactly at the start-up race and fail again. `REPLACE` also cancels a running upload unless guarded. With 4 and 2, failures, and so long waits, should be rare. Kept as a possible follow-up |

Other options rejected:

- **Require `CONNECTED` instead of `UNMETERED`:** that wouldn't help (`OEM_DENY` blocks every network), and it would
  break the owner's rule that videos upload on Wi-Fi only (2026-11-03).
- **Telling the user to find the Samsung setting themselves:** the setting is several screens deep and its name differs
  between Samsung versions. Android's own dialog is a single tap and is the supported API.
- **Shorter or linear backoff:** that treats the symptom. With the network blocked, every run would still fail; only
  the wait between failures would change.

## The fix (branch `fix/video-upload-background-network`)

| File | Change |
| --- | --- |
| `core/.../videos/VideoPipeline.kt` | `VideoStep.Retry` gets `offline` (no answer at all) and `cause` (the exception's class name). New `advancePatiently()`: after an `offline` retry, waits 3 s then 10 s and tries again, then hands back to WorkManager. The thumbnail sheet's retries are left alone, because its attempts are counted per run (`MAX_THUMBS_ATTEMPTS`) |
| `feature-video/.../upload/VideoUploads.kt` | The worker calls `advancePatiently()` and logs `no connection (UnknownHostException); trying again in 3 s` and the final result with its cause |
| `feature-video/.../ui/VideoViewModels.kt` | `uploadsUnrestricted` (from `PowerManager.isIgnoringBatteryOptimizations`), checked again each time the screen resumes |
| `feature-video/.../ui/VideoScreen.kt` | Card "Let videos upload in the background" with **Allow**, which opens `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`. Hidden once allowed. Previews updated (light and dark) |
| `feature-video/src/main/AndroidManifest.xml` | `REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` |
| `core/src/test/.../VideoPipelineTest.kt` | 4 new tests: no connection then success in the same run, with the cause named; still offline after the quick tries hands back to WorkManager (3 requests); a server error isn't retried in the run; a cut sheet upload isn't retried in the run, so its attempt count stays correct |

## Verification

| Check | Result |
| --- | --- |
| `gradlew :core:test` | Pass. `VideoPipelineTest` 19/19, including the 4 new tests |
| `gradlew assembleDebug` | Pass |
| `adb install -r` keeps the exemption | Yes (the allowlist entry survived the update) |
| Card appears when optimised | Yes, on the Video screen |
| **Allow** opens the system dialog | Yes: "Stop optimizing battery usage?". The owner tapped OK (Android ignores taps injected over adb on this dialog, by design) |
| Exemption granted, card hidden | Yes (`deviceidle` allowlist: `user,com.carloserochav.meetingassistant,10059`) |
| Upload starts on its own | 14:44:10, when the backoff from the 13:40:10 failure ended. Link obtained on the first request (`idVideo 16`) |
| Upload continues with the app closed | Yes. `procState=FGS` (foreground service, "Uploading a video" notification), no block reasons |
| Throughput | 104 MB at +15 s, 224 MB at +60 s, 384 MB at +120 s (about 2.7 MB/s) |
| Finished | 14:48:11, `Done`. 708 MB in **4 min 1 s**, confirmed by the server, thumbnail sheet sent, phone copy deleted |
| In the cloud | Video screen: "Wed 25 Nov, 09:24 · 22 min · 720p · 676 MB". Cloud storage 994 MB → 1.6 GB |

### Not measured

- **The quick retry (change 2) didn't trigger on the phone**: the first request after the fix succeeded. It's covered by
  unit tests only. It will show in logcat as `no connection (…); trying again in 3 s` the next time it happens.
- **Whether Samsung keeps the exemption over days** (for example, its automatic optimisation or "sleeping apps"
  list) wasn't observed. An uninstall resets it, and the card would then appear again.
- **Meeting uploads** (`MeetingUploadWorker`, `NetworkType.CONNECTED`) are exposed to the same `OEM_DENY` block when
  the app is optimised, but weren't tested. The exemption is per app, so allowing it from the Video screen covers
  meetings too. The quick retry wasn't added to meetings.

## Mistakes along the way (so the next session avoids them)

- **Wrong estimate of when the upload would resume.** I said 15:09, reading `Minimum latency` from a job
  that had been re-created. The real time was 14:44:10, exactly 64 minutes after the 13:40:10 failure. Compute it from
  the last failure, not from the job dump.
- **`cmd jobscheduler run` needs WorkManager's namespace.** Without `-n androidx.work.systemjobscheduler`, it answers
  "Could not find job". And even then, WorkManager refuses to run a job early while it's in backoff ("Delaying execution
  … because it is being executed before schedule"), so there's no adb shortcut around a backoff.
- **PowerShell eats a leading `-` in arguments** (`whitelist -com.example` became `.example`). Quote it:
  `"-com.example"`.
- **`adb` isn't on PATH** on this PC. Use `%LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe`, and the SDK's
  `sqlite3.exe` from the same folder for WorkManager's database.

## Commands used (for next time)

```powershell
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"; $p = "com.carloserochav.meetingassistant"

# Network: default network and the app's own block reasons (0x40 = background, 0x80 = Samsung OEM deny)
& $adb shell dumpsys connectivity | Select-String "Active default network|UID=<uid> blockedReasons"
& $adb shell dumpsys netpolicy | Select-String "UID=<uid> state=" -Context 0,1

# The job: constraints, backoff, history
& $adb shell dumpsys jobscheduler | Select-String "#VideoUploadWorker" -Context 0,45

# WorkManager's own record: attempts and state
& $adb exec-out run-as $p cat no_backup/androidx.work.workdb > workdb   # also -wal and -shm
sqlite3 workdb "select state, run_attempt_count, stop_reason from workspec where worker_class_name like '%Video%';"

# The video's local record (never print uploadUrl: it's a signed link)
& $adb shell run-as $p cat files/videos/<id>.json

# Network as the app itself (same firewall rules)
& $adb shell "run-as $p ping -c 1 <api host>"

# Battery exemption (what the app's Allow card grants)
& $adb shell dumpsys deviceidle whitelist | Select-String $p

# The app's log
& $adb logcat -d -s VideoUpload:*
```
