# Moving the DevLog / Meeting Assistant infrastructure to a new Azure account

As of 2026-11-16 · Written after the subscription was found suspended during the Android video-player test.

This guide explains how to move everything that runs in Azure today (the DevLog API, the
Meeting Assistant worker, the DevLog frontend, the DevLog AI agent, and the data and services they
use) to a new Azure account, and what has to change in each repo and client afterwards. It is a
plan, not a record of something done: nothing here has been executed yet.

---

## 1. What happened, and why it matters now

On 2026-11-16 the phone could not load the video library. Investigation (read-only `az` commands):

| Check | Result |
| --- | --- |
| Any HTTPS request to the API host, from the PC and from the phone | No answer: TCP connect to port 443 times out (the name still resolves to `74.157.44.53`) |
| `az containerapp list` | `ca-devlog-api`, `ca-devlog-frontend`, `ca-devlog-agent`: provisioning state **Failed** |
| `az containerapp revision list -n ca-devlog-api` | `ManagedClusterSuspended`: *"The compute resource for managed environment cae-devlog-agent has been suspended due to subscription has been disabled."* |
| `az storage container-rm list --storage-account stdevlogagent` | `AccountIsDisabled`: *"Operation is not allowed for account stdevlogagent"* |
| `az account show` | still says `Enabled` (likely a cached value; the services say otherwise) |

So **the subscription "Azure subscription 1" is disabled**. Everything in it is down: the API, the
worker, the frontend, the agent, the AI models it hosts, and access to the storage account.

**Why this is urgent.** Microsoft's documentation says a disabled subscription stays disabled for
**1 to 90 days, depending on the subscription type, and is then permanently deleted**. While it is
disabled, storage is meant to be read-only, so you can retrieve your data. Community answers from
Microsoft moderators say a **free trial gets 30 days** and other offers 90. The exact deadline is in
the email or portal notice Microsoft sent for this subscription. **Find that date first:** it is the
deadline for getting the data out (section 4).

---

## 2. What is deployed: full inventory

Subscription **Azure subscription 1** (`433d9c45-05fd-4a8e-9182-2ab85797e16e`), resource group
**`rg-ernes-102010-6283`** (westus3). A second group, `ccapai-test-rg`, is empty.

| Resource | Type | Region | Used by | Holds data? | How it comes back |
| --- | --- | --- | --- | --- | --- |
| `ca-devlog-api` | Container App (0–1 replicas, 0.5 vCPU / 1 GiB, port 8080) | eastus2 | Phone, desktop, frontend | No (stateless) | Redeploy from git (`tools/Deploy-Azure.ps1 -Step Api`) |
| `caj-mtg-worker` | Container Apps **Job** (event trigger on queue `mtg-jobs`, scales to zero, 2 h timeout) | eastus2 | Meeting pipeline | No | Redeploy (`Deploy-Azure.ps1 -Step Worker`) |
| `ca-devlog-frontend` | Container App (nginx, 0.25 vCPU / 0.5 GiB, port 8080) | eastus2 | Browser | No | `az acr build` + `az containerapp create/update` (`DevLog_Frontend/docs/DEPLOYMENT.md`) |
| `ca-devlog-agent` | Container App (Node, port 3000, 0–1 replicas) | eastus2 | `scripts/chat.ps1` | No (chat history in memory only) | `pnpm azure:deploy` (`DevLog_AI_Agent/scripts/deploy.ps1`) |
| `cae-devlog-agent` | Container Apps environment (Consumption) | eastus2 | All four above | No | Created by the agent's deploy script, or by hand |
| `crdevlogagent` | Container Registry (Basic, admin user) | eastus2 | All images | Images only | Rebuild every image from git (tags are commit SHAs) |
| `log-devlog-agent` | Log Analytics workspace | eastus2 | Environment logs | Logs (lost; not worth migrating) | New workspace |
| **`stdevlogagent`** | **Storage account** (StorageV2, Standard_LRS) | eastus2 | API, worker, agent | **Yes: see section 3** | New account + **copy the data** |
| `ernes-102010-3838-resource` | Azure AI Foundry account (+ project `ernes-102010-3838`) | westus3 | Worker (extraction, `grok-4.6`), agent (chat `grok-4.6`, embeddings `text-embedding-3-small`) | No (model deployments only) | New Foundry resource + deploy the same two models |
| `ai-search-service-charlie` | Azure AI Search, **Free** tier | centralus | Agent (index `devlog-chunks`) | Index (regenerable) | New free service + run the indexer locally again |
| `7a07cecf-…` | global resource (likely an alert or action group) | global | — | No | Check it in the portal; recreate only if it matters |

**Not in Azure, so unaffected by the suspension:**

| Service | Used for | Effect of the move |
| --- | --- | --- |
| **Neon Postgres** (DevLog production DB) | Users, devices, jobs, sessions, transcripts, reports, prompts, costs, video rows | **Nothing moves.** All users, device keys, history and video *rows* survive. Only the blobs those rows point at need to be copied (section 3). Its reachability on 2026-11-16 was **not checked** |
| Deepgram | Transcription | Same key, no change |
| Gemini | Extraction (alternative provider) | Same key, no change |
| SMTP (personal mailbox) | Password-reset emails | Same credentials; the reset **link** changes (new frontend URL) |
| GitHub | Code | No change |

---

## 3. The data: what is in `stdevlogagent` and what is worth saving

The storage account is the **only place in Azure that holds data you can't regenerate**. Known
containers and queues, from the code and deploy scripts:

| Container / queue | Content | Value | If lost |
| --- | --- | --- | --- |
| **`videos`** | Your **cloud video library** (MP4s up to 16 GB each) | **Highest: the only copy.** The phone deletes its own copy once an upload is confirmed (decision 2026-11-05) | Gone for good. DB rows remain and the app shows "This video is no longer in your library" |
| `uploads` | Meeting WAVs: the audio archive (30-day retention, migration 018) and pending jobs | Medium: transcripts and reports are already in Neon | Lose "listen" and re-transcription for recent meetings |
| `devlog-notes` | Full Obsidian notes for the agent | Low: regenerated by `pnpm cli index` from the local vault | Re-index |
| `releases` *(probably here; confirm)* | Desktop installer (`.msix` + `.appinstaller`), reached through a SAS link | Low: republished from `artifacts\release\…` | Republish. **But installed desktops stop auto-updating**, see section 7 |
| Queue `mtg-jobs` | Worker wake-up messages | None (transient) | Recreated empty |
| Desktop's own audio blobs (Phase 6) *(location to confirm)* | Audio of sessions imported from the desktop | Low/medium | No "listen" for those sessions |

**Good news for the database:** blob references are stored **relative** (`blob_name`, for example
`uploads/<name>`), never as full URLs with the account name. So copying blobs into a new account with
the **same container names and blob names** means **no database change**: only the connection
string in the settings changes.

**Not verified yet:** whether the blobs can still be *read* while the subscription is disabled. The
management API refused (`AccountIsDisabled`). Reading the data directly with the account key was not
tried in this session, because it needs your permission. Check it yourself with Azure Storage
Explorer, or with:

```powershell
# Uses the connection string from DevLog's local appsettings.json; prints names and sizes only.
$env:AZURE_STORAGE_CONNECTION_STRING = (Get-Content 'C:\Projects\DevLog_Backend\devLog_Backend\appsettings.json' -Raw | ConvertFrom-Json).Uploads.ConnectionString
az storage container list --query "[].name" -o tsv
az storage blob list -c videos --query "[].{name:name, MB:properties.contentLength}" -o table
Remove-Item Env:AZURE_STORAGE_CONNECTION_STRING
```

---

## 4. First decision: reactivate, or move?

| Option | What it takes | Pros | Cons |
| --- | --- | --- | --- |
| **A. Reactivate the old subscription** (pay the balance, upgrade the free trial to pay-as-you-go, or open a billing support request) | Portal → Subscriptions → (turn off "Show only active") → the subscription → **Reactivate**. If the button is missing: a *Subscription management* support request | Everything comes back as it was; no URL changes, no client changes | Same account and same billing as before |
| **B. Move to a new Azure account** (the plan in this guide) | Sections 5–9 | New billing or credits; a chance to clean up names and add a custom domain | Every public URL changes; every client and settings file must be updated; the data must be copied |
| **A, then B (recommended if B is the goal)** | Reactivate just long enough to copy the data, then move, then cancel the old subscription | No risk to the video library; the move is done calmly, with the old system as a fallback | A few days of cost on the old subscription |

**If the blobs can't be read and the videos matter, option A (at least briefly) is the only way to
save them.** Do it before the retention deadline.

### Why the resources can't simply be "moved"

Azure's resource *move* only works between subscriptions **in the same Microsoft Entra tenant**. A
new Azure account normally comes with **a new tenant**, and then:

- **ARM resource move is not available** across tenants.
- The official workaround is to **transfer the whole subscription** to the other tenant (a support
  request), which needs the subscription active and loses every role assignment.
- In practice, for this project, **redeploying is easier and cleaner than moving**: every compute
  resource is defined by a script in a repo, the database is in Neon, and only the storage data has
  to be copied.

---

## 5. Prepare the new account

1. **Create the account and subscription.** Note whether it is a free trial (30-day retention if it
   is ever disabled) or pay-as-you-go.
2. **Set a budget with alerts on day one** (Cost Management → Budgets, alerts at 50 / 80 / 100 %).
   The suspension came without warning in this project; a budget email is that warning.
3. **Log in from the PC:** `az login`, then `az account set --subscription "<new>"`, and check with `az account show`.
4. **Register the providers** used here (a new subscription has them off):
   `Microsoft.App`, `Microsoft.ContainerRegistry`, `Microsoft.OperationalInsights`,
   `Microsoft.Storage`, `Microsoft.CognitiveServices`, `Microsoft.Search`
   (`az provider register -n <name>`).
5. **Check model access in Azure AI Foundry:** `grok-4.6` and `text-embedding-3-small` must be
   available in the region you choose, with quota. This is the step most likely to block. If grok
   isn't available, the worker can run on Gemini (already supported by configuration) until it is.
6. **Choose new names.** Storage and registry names are **globally unique**, and the old ones
   (`stdevlogagent`, `crdevlogagent`) stay taken while the old subscription exists. Suggested
   pattern: one suffix used everywhere, for example `stdevlog2`, `crdevlog2`, `cae-devlog`,
   `ca-devlog-api`, `caj-mtg-worker`, `ca-devlog-frontend`, `ca-devlog-agent`.
7. **Choose one region** for everything (eastus2 as today, or one close to you). The old setup was
   spread over westus3, eastus2 and centralus, which works but complicates the picture.
8. **Strongly recommended: a custom domain** (for example `api.<yourdomain>` and
   `app.<yourdomain>`) bound to the Container Apps. Today every client has
   `*.victoriouspebble-bfc7fd71.eastus2.azurecontainerapps.io` built in, and that suffix changes
   with every new environment. With a custom domain, the **next** move changes only a DNS record,
   not the Android app, the desktop settings and the frontend.

---

## 6. Rebuild, step by step (order matters)

### Step 1: Shared foundation

```powershell
$rg = 'rg-devlog'; $loc = 'eastus2'
az group create -n $rg -l $loc
az storage account create -n stdevlog2 -g $rg -l $loc --sku Standard_LRS --kind StorageV2 --allow-blob-public-access false
az acr create -n crdevlog2 -g $rg -l $loc --sku Basic --admin-enabled true
az monitor log-analytics workspace create -n log-devlog -g $rg -l $loc
# Environment: the agent's deploy script can create it, or by hand with the workspace id and key:
az containerapp env create -n cae-devlog -g $rg -l $loc --logs-workspace-id <id> --logs-workspace-key <key>
```

Create the containers with **the same names** as before (`uploads`, `videos`, `devlog-notes`,
`releases`) and the queue `mtg-jobs`. The API also creates `videos` on the first upload, but create
it now so the copy has somewhere to go.

### Step 2: Copy the data (while the old account is readable)

Copy blob for blob, keeping names, with **AzCopy** (server-side copy between accounts, no
download to the PC). It needs a read SAS on the old account and a write SAS on the new one:

```powershell
azcopy copy "https://stdevlogagent.blob.core.windows.net/videos?<read-SAS>" `
            "https://stdevlog2.blob.core.windows.net/?<write-SAS>" --recursive
# Repeat for uploads (and releases, devlog-notes if you prefer copying to regenerating).
```

Then **compare counts and total sizes** container by container (the `az storage blob list` command in
section 3, run against each account). Write the numbers down: this is the evidence the copy is complete.

If the old account can't be read and won't be reactivated: the video library is lost. The DB rows
stay, and you can delete them from the app.

### Step 3: AI services

- **Foundry:** create the resource and deploy `grok-4.6` (worker extraction and agent chat) and
  `text-embedding-3-small` (agent, 1536 dimensions). Note the new **endpoint** and **key**.
- **AI Search:** create a **Free** service (one per subscription), then from the agent repo with the
  vault on this PC: `pnpm cli index:setup` and `pnpm cli index`. This rebuilds `devlog-chunks` and
  `devlog-notes` from the vault, so nothing needs copying.

### Step 4: Update the local settings files (the source of every secret)

Every deploy script reads secrets from **untracked local files** and pushes them only into Container
Apps secrets. Update these files, and only these:

| File (untracked) | Keys to change |
| --- | --- |
| `DevLog_Backend/devLog_Backend/appsettings.json` | `Uploads:ConnectionString` → new storage account (videos use it too) |
| `DevLog_Backend/devLog_Backend/appsettings.Production.json` | **Nothing**: Neon connection string and JWT key stay the same. Keeping the **same JWT key** means every signed-in device stays signed in |
| `MeetingAssistant_Worker/src/MeetingAssistant.Worker/appsettings.json` | `AzureFoundry:Endpoint`, `AzureFoundry:ApiKey`, `AzureFoundry:Deployment` (if renamed) |
| `DevLog_AI_Agent/.env` | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_SEARCH_ENDPOINT`, `AZURE_SEARCH_API_KEY`, `AZURE_STORAGE_CONNECTION_STRING`, `CORS_ORIGIN` |

And the **names in the scripts** (tracked; commit these changes):

| File | What to change |
| --- | --- |
| `DevLog_Backend/devLog_Backend/tools/Deploy-Azure.ps1` | `$ResourceGroup`, `$Environment`, `$Registry`, `$StorageName`, `$FrontendUrl` (the password-reset link base) |
| `DevLog_AI_Agent/scripts/deploy.ps1` | Resource group, registry, environment, app names |
| `DevLog_Frontend/docs/DEPLOYMENT.md` | Names and URLs in the commands |

### Step 5: Images and deployments

1. **API and worker:** start Docker Desktop, then
   `tools/Deploy-Azure.ps1 -Step Images` (builds both images, checks that no `appsettings` file is
   inside, pushes), then `-Step Api`, then `-Step Worker`. Both repos must be committed: the
   image tag is the commit.
2. **Frontend:** `az acr build -r crdevlog2 -t devlog-frontend:<sha> --no-logs .`, then
   `az containerapp create` with port 8080 and `BACKEND_URL=https://<new API host>`.
3. **Agent:** `pnpm azure:deploy` (creates or updates `ca-devlog-agent`).
4. **API settings that depend on the frontend URL:** `PasswordReset__ResetUrl` comes from
   `$FrontendUrl` in the script. Deploy the frontend first, or rerun `-Step Api` afterwards.

### Step 6: Smoke test the server side

- `POST https://<api>/Auth/Login` with a wrong password returns **401** (DevLog has no `/health` route).
- The video endpoints answer 401 without a token (as on 2026-11-04).
- Frontend: a login with a wrong password returns 401, which proves the `/api` proxy reaches the API.
- `scripts/chat.ps1` gets an answer from the agent.

---

## 7. Repoint the clients

| Client | Where the address lives | What to do | Users affected |
| --- | --- | --- | --- |
| **Android app** | `app/build.gradle.kts`, `API_BASE_URL` (built into the APK) | Change it, rebuild, `adb install -r`. **No new sign-in:** the device key lives in Neon and is still valid | Every phone needs the new build |
| **Desktop app** | Settings → Server (API address), per machine | Type the new address on each machine. The device token stays valid (same DB) | Each machine, by hand |
| **Desktop installer and updates** | The `.appinstaller` URL (a SAS link to the `releases` container) is **built into the installed package** | Republish the release to the new `releases` container and make a new SAS link. **Machines already installed look for updates at the old URL**, so they need a one-time reinstall with the new command (`installer/INSTALL.md`). Note: no machine had 1.0.4.0 installed yet on 2026-11-12, so the impact may be small | Each installed machine, once |
| **Frontend** | `BACKEND_URL` on the container | Set on create (step 5) | None |
| **Password-reset emails** | `PasswordReset__ResetUrl` | Set by `-Step Api` from `$FrontendUrl` | Old reset emails stop working (they expire anyway) |
| **Agent** | `CORS_ORIGIN` | Set in `.env` | — |

---

## 8. Final checks, end to end

These match the exit tests already used in the project:

1. **Phone meeting:** record → upload → report in the vault (`tools/Test-MeetingReport.ps1`).
2. **Phone video:** the cloud library lists the copied videos, one plays, and the read link renews after 15 minutes.
3. **A new video:** one recorded after the move uploads on Wi-Fi and plays.
4. **Desktop:** History, search, costs and re-extraction through the new API.
5. **Revoked device:** `tools/Revoke-Device.ps1` revokes one and it goes back to sign-in.
6. **Costs:** after a day, Cost Management shows charges only where expected (the Container Apps
   scale to zero; storage and registry are the steady costs).
7. **Logs:** no token, password, SAS link or meeting content in the new Log Analytics workspace.

Anything that can't be measured is reported as not measured.

---

## 9. Close the old subscription

Only after section 8 passes:

1. Confirm that the blob counts and sizes in the new account match the numbers written down in step 2.
2. Rotate anything that might still be valid in the old account: once it's deleted, its keys die with it.
3. Let the old subscription lapse, or cancel it. Note the date in `decision-log.md`.
4. Optional cleanup in the repos: remove old names from docs and examples.

---

## 10. Avoid a repeat: what to change permanently

| Change | Why |
| --- | --- |
| **Budget and alert emails** on the new subscription | The only early warning before a suspension |
| **A custom domain** for the API and frontend | The next move changes DNS, not the apps |
| **Make the Android API address a build setting**, not a constant in `build.gradle.kts` | Point the phone at a local stack, staging or a new host without code edits |
| **All Azure names in one file** (or Bicep/IaC) instead of constants in three scripts | One place to change; the whole environment can be recreated by one command |
| **A second copy of the video library** (blob soft delete, or a periodic AzCopy to another account or a disk) | The phone deletes its copy after upload, so the cloud copy is the only one |
| **A local stack** (Docker Compose: Postgres, Azurite, API, worker, frontend) | Development and testing don't stop when Azure does |

---

## Sources

- Microsoft Learn, *Azure subscription states*: disabled 1–90 days by subscription type, then
  deleted; storage read-only while disabled.
  <https://learn.microsoft.com/en-us/azure/cost-management-billing/manage/subscription-states>
- Microsoft Q&A, reactivating a disabled or deleted subscription (moderator answers: 30 days for a
  free trial, 90 for other offers; *Reactivate* button or a support request):
  <https://learn.microsoft.com/en-us/answers/questions/2031166/can-i-reactivate-a-subscription-that-has-been-dele>
- Microsoft Learn, moving resources to a subscription in another tenant (ARM move needs the same
  tenant; transfer the subscription instead):
  <https://learn.microsoft.com/en-us/troubleshoot/azure/virtual-machines/windows/move-vm-cross-tenant-migration-guide>
- Microsoft Q&A, copying resources and blobs between subscriptions (server-side copy, AzCopy):
  <https://learn.microsoft.com/en-us/answers/questions/2128557/copying-resources-from-one-subscription-to-another>
- Project sources: `DevLog_Backend/devLog_Backend/tools/Deploy-Azure.ps1`,
  `DevLog_Frontend/docs/DEPLOYMENT.md`, `DevLog_AI_Agent/docs/deploy.md`, `db/schema.sql`
  (relative `blob_name`), `roadmap-meeting-ai-assistant.md`, `decision-log.md`.
