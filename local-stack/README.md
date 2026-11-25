# Moved: the local stack now lives in `DevLog_Infra`

The Docker Compose stack that ran here (2026-11-16) moved on 2026-11-18 to the platform repo:

**`C:\Projects\DevLog_Infra`**. See its `README.md`.

```powershell
cd C:\Projects\DevLog_Infra
pwsh -File tools/New-LocalEnv.ps1
docker compose up -d --build
pwsh -File tools/Test-LocalStack.ps1
```

**Why it moved:** the stack runs the backend, frontend and worker together, so it belongs to the
platform, not to one client app (`DevLog_Infra/docs/adr/0001-separate-platform-repo.md`; decision
log, 2026-11-18).

**What changed:** the same ports (Postgres 5434, API 18080, frontend 18081, Azurite 10010/10011),
plus Mailpit (18025), migrations through dbmate, pinned images, and the worker in
`Providers:Mode = Fake` by default (no paid provider calls unless `-WithProviderKeys`).

**Old data:** the containers of the old compose project (`devlog-local`) were removed; its volumes
`devlog-local_pgdata` and `devlog-local_blobs` were kept (test data only). To delete them:

```powershell
docker volume rm devlog-local_pgdata devlog-local_blobs
```

This folder can be deleted once nothing points here.
