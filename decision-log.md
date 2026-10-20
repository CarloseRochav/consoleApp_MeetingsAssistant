# Meeting Assistant: Decision Log

As of 2026-10-19 · Owner: Carlos

Every decision below was taken by the project owner and is in force as of this date. The detailed tracker is `roadmap-meeting-ai-assistant.md` in the repo; this log keeps the reasoning in one place.

**This file is the decision log.** It lives at `C:\Projects\PersonnalTool_App\consoleApp_MeetingsAssistant\decision-log.md`, versioned with the code. It replaced the Claude Doc version on 2026-10-19 (owner's decision); new decisions are added here, newest first in the register.

## Register

| Date | Area | Decision | Chosen over |
| --- | --- | --- | --- |
| 2026-10-19 | DevLog logging | Every log line written during a request carries the caller's payload (method, path, query, JSON body), secrets redacted, for reproducing errors; production logs errors only; callers never see error details | Logging only the error type and message, without the payload |
| 2026-10-19 | Records | Decision log kept as `decision-log.md` in the repo root | A Claude Doc outside the repo |
| 2026-10-19 | DevLog passwords | Password reset by email, sent over SMTP from the owner's personal mailbox; the owner fills in the credentials by hand | Azure Communication Services Email; a dedicated email service |
| 2026-10-19 | Phase 8 | Uploads go to a new private container in the existing Azure Storage account | A new storage account; uploading through the API |
| 2026-10-19 | Phase 8 | Daily spending cap of $2 per user (Deepgram + LLM) | No cap; a higher cap |
| 2026-10-19 | DevLog passwords | bcrypt hashes; the login returns no password, only what a client needs | Reversible encryption (the 2026-10-18 decision, now reversed) |
| 2026-10-19 | Phase 8 | Server worker in its own project and container; each job's status kept in the database for follow-up | A background service inside the API process |
| 2026-10-18 | DevLog passwords | User passwords encrypted reversibly (pgcrypto OpenPGP) | Hashing with bcrypt |
| 2026-10-18 | Phase 8 | Meetings API built inside DevLog\_Backend, in its own style | Separate MeetingAssistant.Api; a DevLog module reusing this repo's Core |
| 2026-10-18 | Transcription | Deepgram keyterms built and tested; term list left empty for now | Writing a list now |
| 2026-10-15 | Distribution | Keep the one-command install with a self-signed certificate | Paid signing, Microsoft Store, classic `.exe` installer |
| 2026-10-15 | Distribution | Release link signature must not contain `+` or `/` | Keeping the original link |
| 2026-10-14 | Distribution | Private `releases` container with a read-only SAS link | A public container |
| 2026-10-14 | Secrets | Package ships with no secrets; keys typed once per machine | Bundling `appsettings.json` |
| 2026-10-14 | Secrets | Development `appsettings.json` keeps keys in plain text | Moving them out of the file |
| 2026-10-14 | Reliability | Retry opening Postgres on transient errors only | A local write journal |
| 2026-10-09 | Data | History in Postgres (Neon), written directly | Local SQLite first, then sync |
| 2026-10-09 | Data | Raw WAV audio and reports to Azure Blob | Compressed audio; database only |
| 2026-10-09 | Secrets | Secrets never stored in the cloud database | Shared secrets across machines |
| 2026-10-09 | Secrets | Azure Key Vault deferred | Key Vault now |
| 2026-10-09 | Privacy | Transcript confidentiality in the cloud out of scope | Encrypting transcripts at rest |
| 2026-08-27 | Data | Local SQLite, hand-written ADO.NET | LiteDB, EF Core |
| 2026-08-27 | Secrets | API keys in the database encrypted with DPAPI (current user) | Plain text in the database |
| 2026-08-27 | Configuration | Database settings as a configuration layer, environment variables on top | Replacing configuration with the database |
| Phase 0 | Providers | Deepgram Nova-3 for transcription, Gemini Flash-Lite for extraction | Whisper, Claude, DeepSeek V4 Flash |

## Secrets and encryption

Secrets saved from the app are encrypted with Windows DPAPI; the developer's `appsettings.json` stays in plain text by choice, and no secret ever reaches the cloud or the installer.

### Where each secret lives

| Place | Protection | What is there |
| --- | --- | --- |
| Local SQLite `setting` table | DPAPI, scope current user | Keys and connection strings typed in Configuración or the first-run window |
| Development `appsettings.json` (app and harness) | None, plain text; git-ignored | The developer's own keys |
| Cloud database (Postgres) | Not allowed | Only non-secret settings: provider, model, prices, keyterms |
| Installer package | Not allowed | Only `appsettings.defaults.json`, with no keys |

Which key is a secret is decided in one place, `SettingKeyPolicy`, and it is enforced both on write and on read.

### Why DPAPI in the database (2026-08-27)

- SQLite does not encrypt. Moving keys from a JSON file into the database without encryption would only move plain text from one file to another.
- DPAPI ties each value to the Windows user and machine, so a copied database file is unreadable elsewhere.
- It needs no key management and no extra service.

**Trade-off:** a copied or restored database loses its secrets, and they must be typed again on each machine. That is also why secrets cannot go to the shared cloud database: another machine could not decrypt them.

### Why the development file stays plain text (2026-10-14)

- It lives only on the developer's machine, is git-ignored, and is the standard .NET development setup.
- Moving it was weighed in audit item 1 and declined; the cost is extra friction on every key rotation.

**Mitigations in place:** the file never enters a Release package (the build and `Build-Release.ps1` both refuse it), and the log warns when a value stored in the database hides a different one in the file, naming only the key.

### Why not Azure Key Vault (2026-10-09)

Key Vault would take secrets off the disk and share them between machines. It was deferred: it adds a paid service, an identity to manage and a startup dependency for a personal tool. It stays the upgrade path if secrets ever need to be shared.

## DevLog\_Backend: user passwords

Since 2026-10-19, DevLog\_Backend stores user passwords as **bcrypt hashes**: nobody, the API included, can read one back. This reverses the reversible encryption chosen on 2026-10-18.

### How it works

| Part | What it does |
| --- | --- |
| `per_users.password` | A bcrypt hash (`$2a$12$...`), or empty for no password (cannot log in) |
| `per_hash_password` | Hashes inside PostgreSQL with pgcrypto `crypt()`, cost 12; about 170 ms per login |
| Login (`per_select_users` option 0) | Hashes the typed password with the stored salt and compares |
| `Auth/Login` reply | Token, expiry, and `idUser, username, name, lastname, email`; never the password or the hash |
| Migration `006` | Hashes plain-text passwords in place, or decrypts the old encrypted ones once and hashes them |

### Justification

- **Chosen by the owner:** hashing is the standard practice; nothing in DevLog needs to read a password back.
- **Gained:** a stolen database yields no passwords, and the API no longer holds a private key that could be lost or leaked.
- **Given up:** a forgotten password cannot be recovered, only reset.
- **Login reply:** returning the password let it land in client logs and storage; the trimmed reply matches exactly what the DevLog frontend already reads.

### State

In DevLog `master` (`fc1c1b3`), tested locally: 21 HTTP checks, a simulated production database, and `schema.sql` on an empty database. **Applied to production on 2026-10-19** (migration `001`, then `006`): both users now hold bcrypt hashes, nothing is left in plain text or encrypted, a wrong password matches nobody, and the 24 imported meetings are untouched. Only the owner can confirm a correct login there. Migration `002` is obsolete.

### Password reset by email

With hashing, a forgotten password can only be replaced, so users need a way to reset it themselves; each user's email address is already stored. Flow:

1. The user enters their username; the reply is the same whether it exists or not, so it reveals nothing.
2. The server emails a one-use link that expires in 30 minutes; the database keeps only the link's hash.
3. The link opens a page in the DevLog frontend to set a new password; the link then stops working.
4. Requests are rate-limited per username and per address.

**Decided 2026-10-19: SMTP from the owner's personal mailbox.** The owner fills in the SMTP settings by hand in DevLog's untracked `appsettings.json` (section `Smtp`); the tracked `appsettings.example.json` holds only placeholders. The options weighed (prices were not verified, since the free option was chosen):

| Option | Experience | Cost | Effort | Trade-off |
| --- | --- | --- | --- | --- |
| Azure Communication Services Email (was recommended) | Mail from an Azure-provided sender address; your own domain can be added later | Pay per email (not verified) | Medium: one Azure resource, one secret in the server settings | Same Azure account and bill as storage; sender address looks generic until a domain is added |
| A dedicated email service (e.g. Resend, SendGrid) | Same | Free tier for low volume (not verified) | Medium: new account, usually your own domain | One more vendor and account to manage |
| **SMTP from a personal mailbox (chosen)** | Mail comes from your personal address | Free | Low | A personal mailbox credential on the server; providers restrict automated sending and it can land in spam |

**Which mailbox works (checked 2026-10-19):**

- **Outlook.com / Hotmail personal accounts do not work with a password.** Microsoft turned off password and app-password sign-in for SMTP on 2024-09-16; only OAuth sign-in works, which this feature does not implement ([Microsoft support](https://support.microsoft.com/en-us/office/f4202ebf-89c6-4a8a-bec3-3d60cf7deaef)).
- **Gmail works with an app password:** turn on 2-Step Verification, create an app password, and use `smtp.gmail.com`, port 587 (STARTTLS), with the full address as the username. Personal Gmail caps sending at about 500 messages a day, far above this use (secondary sources: [Nylas](https://developer.nylas.com/docs/cookbook/email/gmail-smtp-settings.md), [n8n](https://docs.n8n.io/integrations/builtin/credentials/send-email/gmail)).
- **Trade-offs accepted:** the mailbox credential sits on the server (an app password, revocable on its own, never the account's main password); reset emails come from a personal address and may land in spam.

**State (2026-10-19):** built in DevLog_Backend `master` (`53c6ef8`, migration `007`) and DevLog_Frontend `master` (`61993d4`, pages `/forgot-password` and `/reset-password`). Tested locally with a test mail server (Mailpit): 24 checks pass, including the same answer for unknown users, refused reused or expired links, rate limits, and no token in the logs. On 2026-10-19 also tested end to end with the owner's real Gmail (app password), against the local database: email received, link opened in the browser, password changed, sign-in with the new one. Migration `007` applied in production the same day (DevLog `60de009`); the owner then reset a password against production and it worked. A failure while opening a reset is caught so the answer stays the same, and goes to the normal log (`ILogger`) with the error type and message, plus the request's payload (see "DevLog\_Backend: logging").

## DevLog\_Backend: logging

**Decision (owner, 2026-10-19):** keep what the caller sent with every error, so it can be reproduced later. The caller never learns error details. Production logs errors only.

- **How:** a middleware (`Services/RequestPayloadLogging.cs`) reads each request once and attaches `Payload: <method> <path> query ... body ...` to every log line written while the request runs. In the console it shows on the line after the category; in a structured log (Azure later) it is a separate `Payload` field you can search.
- **What is never logged:** any field or query parameter named like a password, token, secret or key, at any depth, becomes `"[redacted]"`; headers are not logged (they carry the JWT and device tokens). This keeps the earlier rule: never log a password, a reset token, its hash or the link.
- **Limits:** only JSON bodies up to 64 KB are read; the logged text is cut at 4096 characters. Audio never goes through the API (uploads go straight to storage by SAS), so this does not touch meeting content.
- **Levels:** production `Error` and above; Development and QA `Information`.
- **Tested (2026-10-19)** with the database unreachable: login, reset request and reset confirm each logged their payload with the password, token, new password, a query-string token and a nested `apiKey` redacted; none of the test secrets appeared anywhere in the output; with Production settings no `info` or `warn` line was written. With the normal database, login and reset still read their bodies (401 and 400 as expected). DevLog `1808b54`.
- **Trade-off accepted:** usernames, emails and other non-secret fields now sit in the logs. A new secret field whose name doesn't match the redaction list would be logged; the rule is in DevLog's `CLAUDE.md`.

## Data and configuration

The Obsidian vault stays the product; the database is the record and the index, and the cloud gives one shared history across machines.

| Decision | Why | Trade-off accepted |
| --- | --- | --- |
| **SQLite, hand-written ADO.NET** (2026-08-27) | No ORM in the codebase; EF Core migrations were not worth the trimming risk. The native SQLite binary was verified inside the installed package before committing to it. | LiteDB was the runner-up (no native binary); interfaces in Core allow switching. |
| **Transcripts kept forever** (2026-08-27) | Enables search, re-extraction with another prompt, and measuring transcription errors. | Storage grows without limit. |
| **Postgres on Neon, written directly** (2026-10-09) | The app already needs the network for Deepgram and the LLM; a sync layer would add complexity for little gain. SQLite stays selectable, so self-tests run offline. | A long Postgres outage drops that meeting's rows (see risks). |
| **Raw WAV and reports to Azure Blob** (2026-10-09) | Backup of the original audio and a second copy of each report. | About 320 MB per hour of meeting, uncompressed. |
| **Configuration as stacked layers** (2026-08-27) | Packaged defaults, then local database, then cloud settings, then environment variables. The code reading settings never learns a database exists. | Rotating a key means checking the database too: a value saved from Configuración overrides the file. |
| **Environment variables on top** | They are the escape hatch when a bad stored value stops the app from starting. | None worth noting. |
| **Cloud only for shared, non-secret settings** | The keys that say where the cloud is, machine paths, and secrets can never come from it. | Each machine still configures its own vault path and keys. |

## Distribution

The app installs on other machines with one PowerShell command and one administrator prompt, then updates itself; the full comparison of options is in [Decision: How Meeting Assistant Is Installed on Other Machines](https://claude.ai/code/artifact/ffddabd0-ef1c-4cac-90b8-206736eef6fb).

- **Self-signed certificate (2026-10-14, confirmed 2026-10-15).** Free. Each new machine trusts it once, which is the admin prompt. Paid signing (about US$10/month) would make it a plain double-click; declined for a personal tool. Revisit if someone else needs to install the app.
- **Private `releases` container with a read-only SAS link.** The same Storage account holds meeting audio and has public access off, so it was not opened. Replacing the container's access policy revokes every link without rotating the account key.
- **Signature without `+` or `/` (2026-10-15).** The first real install failed with 403: Windows App Installer decodes `%2B` back to `+`, and Azure then rejects the signature. Publishing now picks the first policy name whose signature is clean, and the same name every time, so republishing does not change the link. The new policy also revoked the old link, which had travelled by email.

## Providers and cost

Providers were chosen on cost and quality for meetings that mix Spanish and English; prices are read from settings, never hard-coded.

- **Deepgram Nova-3 multilingual for transcription** (Phase 0). Chosen over Whisper and Claude on cost and quality. Official price US$0.0052/min ([pricing](https://deepgram.com/pricing)).
- **Gemini Flash-Lite for extraction** (Phase 0). Chosen over DeepSeek V4 Flash for stronger multilingual performance. DeepSeek through Azure AI Foundry stays selectable by configuration.
- **Deepgram keyterms: built, tested, list left empty** (2026-10-15 / 2026-10-18). They target a known weakness: mixed-language jargon such as "stored procedure" heard as "auth restore procedure". Deepgram accepts them with the multilingual model ([keyterm docs](https://developers.deepgram.com/docs/keyterm)); a real test confirmed the terms reach Deepgram. They cost US$0.0013/min extra, already set in the cloud prices, and only charged when the list has terms. The list is left empty until the owner writes it; the quality gain is not measured yet.
- **Unknown price means unknown cost.** When a price is missing, the cost is recorded as unknown, never as zero, so a total never looks complete when it is not.

## Accepted risks

These risks were accepted knowingly; each has a mitigation in place and a condition that would reopen the decision.

| Risk | Impact | Mitigation in place | Revisit if |
| --- | --- | --- | --- |
| Plain-text keys in the development `appsettings.json` | Anyone with access to the developer's disk reads the keys | Git-ignored; never packaged; log warns on overridden values | The machine is shared, or Key Vault becomes worth its cost |
| DPAPI ties secrets to one user and machine | A restored or copied database loses its keys | Keys are typed again from Configuración | Secrets need to move between machines |
| Transcripts stored in the cloud without extra encryption | A breach of Neon or Azure exposes meeting content | Private storage, account access only; out of scope by decision | Meetings start covering confidential material |
| Long Postgres outage | That meeting's rows miss the history; the report still reaches the vault and the WAV stays on disk | Retry on transient errors when opening the connection | It actually happens: then fall back to SQLite and migrate on startup |
| Neon cold start | First query after idle is slow | Logged as "Espera del arranque" | The wait nears 5 s |
| Self-signed installer | Each new machine needs one admin prompt | One-command install script | Someone else installs the app |
| SAS link leaks | Anyone can download the installer, which holds no secrets, until 2028 | Read-only; revocable by replacing the access policy | A link is shared beyond the owner |
| Keyterm list over 500 tokens | Deepgram rejects the request; the meeting is transcribed without the terms | Since 2026-10-18 the app retries without keyterms and warns; tested against Deepgram. The cost estimate then includes the keyterm extra that was not charged | — |
| 9-minute segments with the SDK's 30-second request limit | A slow response fails a long meeting's segment | Known, closed by the owner after review | Failures appear in real meetings |
| DevLog login timing shows which usernames exist | An unknown username answers faster than a wrong password, since no hash is computed | None yet; login rate limits come with Phase 8 step A | Before the API is reachable from the internet |
| Personal mailbox credential on the DevLog server | Whoever reads the server's settings can send mail as the owner | An app password (revocable alone, not the main password), only in untracked settings or the host's secret settings | The mailbox is used for anything sensitive, or other people get accounts |
| Reset emails from a personal address | They can land in spam; the sender looks personal | None | Users miss reset emails |
| Caller payloads in the DevLog logs | Whoever reads the logs sees usernames, emails and other non-secret request data | Secret-looking fields and all headers redacted; production keeps errors only; body size and log text capped | Other people get accounts, or a request carries a secret under an unexpected name |
